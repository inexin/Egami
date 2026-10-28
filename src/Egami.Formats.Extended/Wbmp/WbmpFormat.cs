using Egami.Pixels;

namespace Egami.Formats.Extended.Wbmp;

/// <summary>
/// WBMP, the Wireless Application Protocol's one-bit bitmap, type 0: a type and a header byte, the width and height as WAP
/// multi-byte integers, then rows of bits, white 1 and black 0. Registered with <see cref="ExtendedContextExtensions.WithWbmp"/>.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="register-one"/>
/// </example>
public static class WbmpFormat
{
    /// <summary>Gets the format's registration: name, MIME type, extension, signature, reader and writer.</summary>
    public static FormatInfo Info { get; } = new()
    {
        Name = "WBMP",
        MimeTypes = ["image/vnd.wap.wbmp"],
        Extensions = ["wbmp"],
        SignatureLength = 12,
        Matches = WbmpReader.Matches,
        Reader = new WbmpReader(),
        Writer = new WbmpWriter(),
    };
}

/// <summary>
/// Reads WBMP type 0, written from the WAP-237 Wireless Application Environment specification's WBMP section. Extension headers
/// are skipped. Rows stream straight from the input.
/// </summary>
internal sealed class WbmpReader : ExtendedReader
{
    /// <summary>The largest width or height read: a WAP multi-byte integer of up to four bytes.</summary>
    private const int MaxSize = (1 << 28) - 1;

    /// <summary>
    /// Returns whether the data starts like a type 0 WBMP without extension headers and with a size above zero. The signature is
    /// weak, so formats with a real signature are matched first.
    /// </summary>
    internal static bool Matches(ReadOnlySpan<byte> header)
    {
        if (header.Length < 4 || header[0] != 0 || header[1] != 0)
        {
            return false;
        }

        int at = 2;
        return Number(header, ref at) is > 0 and <= MaxSize && Number(header, ref at) is > 0 and <= MaxSize;
    }

    public override RasterInfo ReadInfo(EgamiInput input, ReaderOptions options)
    {
        (int width, int height) = ReadHeader(input);
        return Describe(WbmpFormat.Info, width, height, Gray8.Layout, RasterAccess.Streamed, width + ((width + 7) / 8));
    }

    public override RowReader Open(EgamiInput input, ReaderOptions options)
    {
        (int width, int height) = ReadHeader(input);
        input.CheckFrameSize(width, height);
        return new Rows(input, options, width, height);
    }

    /// <summary>Reads a multi-byte integer from a header, or returns −1 if it is cut short, too long or has a leading zero group.</summary>
    private static long Number(ReadOnlySpan<byte> header, ref int at)
    {
        long value = 0;
        for (int i = 0; i < 4 && at < header.Length; i++)
        {
            byte b = header[at++];
            if (i == 0 && b == 0x80)
            {
                return -1;
            }

            value = (value << 7) | (uint)(b & 0x7F);
            if ((b & 0x80) == 0)
            {
                return value;
            }
        }

        return -1;
    }

    private static (int Width, int Height) ReadHeader(EgamiInput input)
    {
        if (ReadNumber(input) != 0)
        {
            throw input.Corrupt(0, "Only WBMP type 0 is defined.");
        }

        byte fixHeader = input.ReadByte();
        if ((fixHeader & 0x80) != 0)
        {
            SkipExtensions(input, (fixHeader >> 5) & 3);
        }

        long width = ReadNumber(input);
        long height = ReadNumber(input);
        if (width is <= 0 or > MaxSize || height is <= 0 or > MaxSize)
        {
            throw input.Corrupt(input.Position, $"The image size {width}×{height} is invalid.");
        }

        return ((int)width, (int)height);
    }

    private static long ReadNumber(EgamiInput input)
    {
        long value = 0;
        for (int i = 0; i < 4; i++)
        {
            byte b = input.ReadByte();
            value = (value << 7) | (uint)(b & 0x7F);
            if ((b & 0x80) == 0)
            {
                return value;
            }
        }

        throw input.Corrupt(input.Position, "A WBMP number is longer than four bytes.");
    }

    /// <summary>Skips the extension headers: a bit field (type 0) or parameter and value pairs (type 3), each flagged to continue.</summary>
    private static void SkipExtensions(EgamiInput input, int type)
    {
        for (int i = 0; i < 1024; i++)
        {
            byte b = input.ReadByte();
            if (type == 3)
            {
                input.Skip(((b >> 4) & 7) + (b & 15));
            }

            if ((b & 0x80) == 0)
            {
                return;
            }
        }

        throw input.Corrupt(input.Position, "The WBMP extension headers do not end.");
    }

    private sealed class Rows : ExtendedRows<Gray8, Gray8>
    {
        private readonly byte[] _bits;

        public Rows(EgamiInput input, ReaderOptions options, int width, int height)
            : base(input, options, WbmpFormat.Info, "Wbmp", width, height)
        {
            _bits = new byte[(width + 7) / 8];
        }

        protected override bool DecodeRow(int y, Span<Gray8> row)
        {
            int read = Input.Read(_bits);
            Bits.Expand(_bits.AsSpan(0, read), row, msbFirst: true, one: 255, zero: 0);
            if (read < _bits.Length)
            {
                row[(read * 8)..].Clear();
                return false;
            }

            return true;
        }
    }
}

/// <summary>Writes WBMP type 0: a pixel is white when its gray value is at least half, else black.</summary>
internal sealed class WbmpWriter : ExtendedWriter
{
    protected override FormatInfo Format => WbmpFormat.Info;

    public override RowEncoder<TPixel> Start<TPixel>(Stream output, EncodeStart<TPixel> start)
    {
        start.ReportLosses("WBMP", SaveKeeps.None);
        return new Encoder<TPixel>(output, start.Width, start.Height);
    }

    private sealed class Encoder<TPixel> : RowEncoder<TPixel>
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        private readonly OutputBuffer _output;
        private readonly Gray8[] _row;
        private readonly int _rowBytes;

        public Encoder(Stream output, int width, int height)
        {
            _output = new OutputBuffer(output);
            _row = new Gray8[width];
            _rowBytes = (width + 7) / 8;
            _output.WriteByte(0);
            _output.WriteByte(0);
            WriteNumber(width);
            WriteNumber(height);
        }

        public override void WriteRow(ReadOnlySpan<TPixel> row)
        {
            PixelConvert.Convert<TPixel, Gray8>(row, _row);
            Bits.Pack(_row, _output.GetSpan(_rowBytes)[.._rowBytes], msbFirst: true, oneIsLight: true);
            _output.Advance(_rowBytes);
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

        private void WriteNumber(int value)
        {
            Span<byte> bytes = stackalloc byte[5];
            int n = 0;
            do
            {
                bytes[n++] = (byte)(value & 0x7F);
                value >>= 7;
            }
            while (value > 0);

            for (int i = n - 1; i >= 0; i--)
            {
                _output.WriteByte((byte)(bytes[i] | (i > 0 ? 0x80 : 0)));
            }
        }
    }
}
