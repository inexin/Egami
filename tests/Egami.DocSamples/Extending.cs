using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

using Egami.Formats;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.DocSamples;

#region custom-operation
/// <summary>Swaps red and blue, in parallel bands of rows.</summary>
public sealed class SwapRedBlue : IRasterOperation
{
    public Size OutputSize(Size input) => input;

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        input.ForEachBand((start, end) =>
        {
            for (int y = start; y < end; y++)
            {
                Span<TPixel> source = input.Source.Row(y);
                Span<TPixel> destination = input.Destination.Row(y);
                for (int x = 0; x < source.Length; x++)
                {
                    Vector4 v = source[x].ToScaled();
                    destination[x] = TPixel.FromScaled(new Vector4(v.Z, v.Y, v.X, v.W));
                }
            }
        });
    }
}
#endregion

#region custom-format
/// <summary>A minimal format: "RAW8", width and height as big-endian numbers, then RGBA rows.</summary>
public static class Raw8Format
{
    public static FormatInfo Info { get; } = new()
    {
        Name = "RAW8",
        Extensions = ["raw8"],
        SignatureLength = 4,
        Matches = header => header.StartsWith("RAW8"u8),
        Reader = new Reader(),
        Writer = new Writer(),
    };

    private sealed class Reader : IFormatReader
    {
        public RasterInfo ReadInfo(EgamiInput input, ReaderOptions options)
        {
            (int width, int height) = Header(input);
            return new RasterInfo { Format = Info, Width = width, Height = height, Layout = Rgba8.Layout, FrameCount = 1, EstimatedBytes = 4L * width * height };
        }

        public Raster<TPixel> Read<TPixel>(EgamiInput input, ReaderOptions options)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            (int width, int height) = Header(input);

            // CreateRaster checks the size against the safety limits before allocating.
            Raster<TPixel> raster = input.CreateRaster<TPixel>(width, height);
            using IMemoryOwner<byte> row = input.RentScratch(width * 4);
            for (int y = 0; y < height; y++)
            {
                input.ReadExactly(row.Memory.Span[..(width * 4)]);
                PixelConvert.Convert<Rgba8, TPixel>(MemoryMarshal.Cast<byte, Rgba8>(row.Memory.Span[..(width * 4)]), raster.Row(y));
            }

            return raster;
        }

        public Raster Read(EgamiInput input, ReaderOptions options) => Read<Rgba8>(input, options);

        private static (int Width, int Height) Header(EgamiInput input)
        {
            input.Skip(4);
            return ((int)input.ReadUInt32BigEndian(), (int)input.ReadUInt32BigEndian());
        }
    }

    private sealed class Writer : IFormatWriter
    {
        public void Write<TPixel>(Raster<TPixel> raster, Stream output, WriterOptions options)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            Span<byte> header = stackalloc byte[12];
            "RAW8"u8.CopyTo(header);
            BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)raster.Width);
            BinaryPrimitives.WriteUInt32BigEndian(header[8..], (uint)raster.Height);
            output.Write(header);
            Rgba8[] row = new Rgba8[raster.Width];
            for (int y = 0; y < raster.Height; y++)
            {
                PixelConvert.Convert<TPixel, Rgba8>(raster.Row(y), row);
                output.Write(MemoryMarshal.AsBytes(row.AsSpan()));
            }
        }
    }
}
#endregion

#region row-format
/// <summary>The same RAW8 format, streaming: rows go in and out one at a time, so RasterDecoder, RasterEncoder and Transcode stream it.</summary>
public static class StreamingRaw8Format
{
    public static FormatInfo Info { get; } = Raw8Format.Info with { Reader = new Reader(), Writer = new Writer() };

    private sealed class Reader : IFormatReader, IRowFormatReader
    {
        private static readonly IFormatReader Whole = Raw8Format.Info.Reader!;

        public RasterInfo ReadInfo(EgamiInput input, ReaderOptions options) => Whole.ReadInfo(input, options) with { Access = RasterAccess.Streamed };

        public Raster<TPixel> Read<TPixel>(EgamiInput input, ReaderOptions options)
            where TPixel : unmanaged, IPixelType<TPixel> => Whole.Read<TPixel>(input, options);

        public Raster Read(EgamiInput input, ReaderOptions options) => Whole.Read(input, options);

        // The open image reads each row when it is asked for; the decoder crops, reduces and converts after it.
        public RowReader Open(EgamiInput input, ReaderOptions options)
        {
            input.Skip(4);
            int width = (int)input.ReadUInt32BigEndian();
            int height = (int)input.ReadUInt32BigEndian();

            // No raster checks the size here, so check it before the row buffer is allocated.
            input.CheckFrameSize(width, height);
            return new Rows(input, width, height);
        }
    }

    private sealed class Rows(EgamiInput input, int width, int height) : RowReader<Rgba8>
    {
        private readonly Rgba8[] _row = new Rgba8[width];
        private int _next;
        private bool _started;

        public override int Width => width;

        public override int Height => height;

        public override Metadata.RasterMetadata Metadata { get; } = new();

        public override Metadata.FrameMetadata? NextFrame()
        {
            if (_started)
            {
                return null;
            }

            _started = true;
            return new Metadata.FrameMetadata();
        }

        public override void ReadRow<TPixel>(int y, int x, Span<TPixel> destination)
        {
            for (; _next <= y; _next++)
            {
                input.ReadExactly(MemoryMarshal.AsBytes(_row.AsSpan()));
            }

            PixelConvert.Convert<Rgba8, TPixel>(_row.AsSpan(x, destination.Length), destination);
        }
    }

    private sealed class Writer : IFormatWriter, IRowFormatWriter
    {
        public void Write<TPixel>(Raster<TPixel> raster, Stream output, WriterOptions options)
            where TPixel : unmanaged, IPixelType<TPixel> => Raw8Format.Info.Writer!.Write(raster, output, options);

        // Save, SaveAsync, RasterEncoder and Transcode all write through the encoder.
        public RowEncoder<TPixel> Start<TPixel>(Stream output, EncodeStart<TPixel> start)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            // RAW8 keeps 8-bit RGBA and nothing else: deeper samples or metadata are reported as lost.
            start.ReportLosses("RAW8", SaveKeeps.Alpha);
            return new Encoder<TPixel>(output, start.Width, start.Height);
        }
    }

    private sealed class Encoder<TPixel> : RowEncoder<TPixel>
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        private readonly Stream _output;
        private readonly Rgba8[] _row;

        public Encoder(Stream output, int width, int height)
        {
            _output = output;
            _row = new Rgba8[width];
            Span<byte> header = stackalloc byte[12];
            "RAW8"u8.CopyTo(header);
            BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)width);
            BinaryPrimitives.WriteUInt32BigEndian(header[8..], (uint)height);
            output.Write(header);
        }

        public override void WriteRow(ReadOnlySpan<TPixel> row)
        {
            PixelConvert.Convert<TPixel, Rgba8>(row, _row);
            _output.Write(MemoryMarshal.AsBytes(_row.AsSpan()));
        }

        public override void Complete()
        {
        }
    }
}
#endregion

[Collection("Samples")]
public class Extending
{
    [Fact]
    public void Reach()
    {
        #region reach
        // How far a chain reads around each pixel: run it on a part of a picture padded by this much, and the part comes out as it
        // would in the whole picture.
        OperationReach? reach = RasterOps.Reach(ops => ops.Blur(3).Glow(4, Rgba8.White));
        if (reach is OperationReach r)
        {
            Console.WriteLine($"pad by {r.X} × {r.Y}");
        }

        // A resize reads the whole image: its reach is unknown.
        Assert.Null(RasterOps.Reach(ops => ops.Resize(100, 100)));
        #endregion

        Assert.NotNull(reach);
    }

    [Fact]
    public void UseCustomOperation()
    {
        using SampleFiles files = new();
        using Raster<Rgba8> image = Raster.Load<Rgba8>("duck.png");
        Rgba8 before = image.Row(0)[0];

        #region use-custom-operation
        image.Apply(ops => ops.Fit(100, 100).Add(new SwapRedBlue()), TestContext.Current.CancellationToken);
        #endregion

        Assert.NotEqual(before, image.Row(0)[0]);
    }

    [Fact]
    public void UseCustomFormat()
    {
        using SampleFiles files = new();
        using Raster image = Raster.Load("duck.png");

        #region use-custom-format
        // Register it in a context...
        EgamiContext context = EgamiContext.Default.WithFormat(Raw8Format.Info);
        using (FileStream output = File.Create("duck.raw8"))
        {
            image.Save(output, Raw8Format.Info);
        }

        using Raster back = Raster.Load("duck.raw8", context);

        // ...or, once at startup before any other Egami call, make it part of the default context:
        // EgamiContext.SetDefault(new EgamiContext().WithFormat(Raw8Format.Info));
        #endregion

        Assert.Equal(image.Width, back.Width);
    }

    [Fact]
    public void UseRowFormat()
    {
        using SampleFiles files = new();
        EgamiContext context = EgamiContext.Default.WithFormat(StreamingRaw8Format.Info);
        using (FileStream raw = File.Create("duck.raw8"))
        {
            Raster.Transcode(File.OpenRead("duck.png"), raw, StreamingRaw8Format.Info, x => x.Fit(64, 64));
        }

        using (FileStream input = File.OpenRead("duck.raw8"))
        using (FileStream small = File.Create("small.raw8"))
        {
            Raster.Transcode(input, small, StreamingRaw8Format.Info, x => x.Fit(32, 32), context: context);
        }

        using RasterDecoder decoder = RasterDecoder.Open("small.raw8", context);
        Assert.Equal(RasterAccess.Streamed, decoder.Info.Access);
        Assert.Equal(32, Math.Max(decoder.Info.Width, decoder.Info.Height));
    }
}
