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

[Collection("Samples")]
public class Extending
{
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
}
