using System.Buffers.Binary;
using System.Runtime.InteropServices;

using Egami.Pixels;

namespace Egami.Formats.Extended.Farbfeld;

/// <summary>
/// Farbfeld, suckless.org's lossless format: "farbfeld", the width and height as big-endian 32-bit numbers, then 16-bit
/// big-endian RGBA pixels, row by row. Registered with <see cref="ExtendedContextExtensions.WithFarbfeld"/>.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="register-one"/>
/// </example>
public static class FarbfeldFormat
{
    /// <summary>Gets the format's registration: name, MIME type, extension, signature, reader and writer.</summary>
    public static FormatInfo Info { get; } = new()
    {
        Name = "Farbfeld",
        MimeTypes = ["image/x-farbfeld"],
        Extensions = ["ff"],
        SignatureLength = 8,
        Matches = header => header.StartsWith("farbfeld"u8),
        Reader = new FarbfeldReader(),
        Writer = new FarbfeldWriter(),
    };
}

/// <summary>Reads Farbfeld, written from the format's specification on suckless.org. Rows stream straight from the input.</summary>
internal sealed class FarbfeldReader : ExtendedReader
{
    private const int HeaderLength = 16;

    public override RasterInfo ReadInfo(EgamiInput input, ReaderOptions options)
    {
        (int width, int height) = ReadHeader(input);
        return Describe(FarbfeldFormat.Info, width, height, Rgba16.Layout, RasterAccess.Streamed, (long)width * 8);
    }

    public override RowReader Open(EgamiInput input, ReaderOptions options)
    {
        (int width, int height) = ReadHeader(input);
        input.CheckFrameSize(width, height);
        return new Rows(input, options, width, height);
    }

    private static (int Width, int Height) ReadHeader(EgamiInput input)
    {
        Span<byte> header = stackalloc byte[HeaderLength];
        input.ReadExactly(header);
        if (!header.StartsWith("farbfeld"u8))
        {
            throw input.Corrupt(0, "The Farbfeld signature is wrong.");
        }

        uint width = BinaryPrimitives.ReadUInt32BigEndian(header[8..]);
        uint height = BinaryPrimitives.ReadUInt32BigEndian(header[12..]);
        if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue)
        {
            throw input.Corrupt(8, $"The image size {width}×{height} is invalid.");
        }

        return ((int)width, (int)height);
    }

    private sealed class Rows(EgamiInput input, ReaderOptions options, int width, int height)
        : ExtendedRows<Rgba16, Rgba16>(input, options, FarbfeldFormat.Info, "Farbfeld", width, height)
    {
        protected override bool DecodeRow(int y, Span<Rgba16> row)
        {
            Span<byte> bytes = MemoryMarshal.AsBytes(row);
            int read = Input.Read(bytes);
            Span<ushort> samples = MemoryMarshal.Cast<byte, ushort>(bytes);
            if (BitConverter.IsLittleEndian)
            {
                BinaryPrimitives.ReverseEndianness(samples, samples);
            }

            if (read < bytes.Length)
            {
                // A pixel cut short is dropped whole.
                row[(read / 8)..].Clear();
                return false;
            }

            return true;
        }
    }
}

/// <summary>Writes Farbfeld: every pixel as 16-bit RGBA, so nothing but metadata is lost.</summary>
internal sealed class FarbfeldWriter : ExtendedWriter
{
    protected override FormatInfo Format => FarbfeldFormat.Info;

    public override RowEncoder<TPixel> Start<TPixel>(Stream output, EncodeStart<TPixel> start)
    {
        start.ReportLosses("Farbfeld", SaveKeeps.Alpha | SaveKeeps.Wide);
        return new Encoder<TPixel>(output, start.Width, start.Height);
    }

    private sealed class Encoder<TPixel> : RowEncoder<TPixel>
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        private readonly OutputBuffer _output;
        private readonly Rgba16[] _row;

        public Encoder(Stream output, int width, int height)
        {
            _output = new OutputBuffer(output);
            _row = new Rgba16[width];
            Span<byte> header = _output.GetSpan(16);
            "farbfeld"u8.CopyTo(header);
            BinaryPrimitives.WriteUInt32BigEndian(header[8..], (uint)width);
            BinaryPrimitives.WriteUInt32BigEndian(header[12..], (uint)height);
            _output.Advance(16);
        }

        public override void WriteRow(ReadOnlySpan<TPixel> row)
        {
            PixelConvert.Convert<TPixel, Rgba16>(row, _row);
            Span<byte> bytes = _output.GetSpan(_row.Length * 8);
            Span<ushort> samples = MemoryMarshal.Cast<byte, ushort>(bytes[..(_row.Length * 8)]);
            ReadOnlySpan<ushort> source = MemoryMarshal.Cast<Rgba16, ushort>(_row);
            if (BitConverter.IsLittleEndian)
            {
                BinaryPrimitives.ReverseEndianness(source, samples);
            }
            else
            {
                source.CopyTo(samples);
            }

            _output.Advance(_row.Length * 8);
        }

        public override void Complete()
        {
            _output.Flush();
        }

        protected override void Dispose(bool disposing)
        {
            _output.Dispose();
            base.Dispose(disposing);
        }
    }
}
