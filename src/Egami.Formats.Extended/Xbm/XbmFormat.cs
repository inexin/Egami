using System.Globalization;
using System.Text;

using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Extended.Xbm;

/// <summary>
/// XBM, the X Window System's one-bit bitmap: C source with the width and height as #define lines and the bits as an array,
/// black where set. Registered with <see cref="ExtendedContextExtensions.WithXbm"/>.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="register-one"/>
/// </example>
public static class XbmFormat
{
    /// <summary>Gets the format's registration: name, MIME type, extension, signature, reader and writer.</summary>
    public static FormatInfo Info { get; } = new()
    {
        Name = "XBM",
        MimeTypes = ["image/x-xbitmap"],
        Extensions = ["xbm"],
        SignatureLength = 256,
        Matches = XbmReader.Matches,
        Reader = new XbmReader(),
        Writer = new XbmWriter(),
    };
}

/// <summary>Details only XBM has, kept in <see cref="RasterMetadata.FormatDetails"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="details"/>
/// </example>
public sealed class XbmDetails : FormatDetails
{
    /// <summary>Gets or sets the name the C identifiers start with, such as "cursor" for cursor_width. The writer writes it back.</summary>
    public string Name { get; set; } = "image";

    /// <summary>Gets or sets the hotspot, the point a cursor points with, or <see langword="null"/> if the file has none.</summary>
    public Point? Hotspot { get; set; }

    /// <inheritdoc/>
    public override FormatDetails Copy()
    {
        return new XbmDetails { Name = Name, Hotspot = Hotspot };
    }
}

/// <summary>
/// Reads XBM, written from the X11 bitmap file format as Xlib's XReadBitmapFile and the bitmap(1) manual page describe it: X11
/// files of bytes and X10 files of 16-bit words, both with the leftmost pixel in the lowest bit. The file is read whole.
/// </summary>
internal sealed class XbmReader : ExtendedReader
{
    /// <summary>Returns whether the data starts with a #define of a width.</summary>
    internal static bool Matches(ReadOnlySpan<byte> header)
    {
        CSource source = new(header);
        if (!source.Accept("#define"u8))
        {
            return false;
        }

        ReadOnlySpan<byte> name = source.Identifier();
        return name.EndsWith("_width"u8) && source.Number() > 0;
    }

    public override RasterInfo ReadInfo(EgamiInput input, ReaderOptions options)
    {
        // The #define lines are at the start: a probe reads no further than the first window.
        Parsed file = Parse(input, input.Peek(64 * 1024), headerOnly: true);
        return Describe(XbmFormat.Info, file.Width, file.Height, Gray8.Layout, RasterAccess.Buffered, (long)file.Width * file.Height);
    }

    public override RowReader Open(EgamiInput input, ReaderOptions options)
    {
        byte[] text = InputData.ReadToEnd(input, MaxText(input), out int length);
        Parsed file = Parse(input, text.AsSpan(0, length), headerOnly: false);
        input.CheckFrameSize(file.Width, file.Height);
        return new Rows(input, options, file, text, length);
    }

    /// <summary>Returns the most text a file within the limits can have: under a byte a pixel, plus room for the header.</summary>
    private static long MaxText(EgamiInput input) => Math.Min(input.Limits.MaxPixels + (1 << 20), Array.MaxLength);

    private static Parsed Parse(EgamiInput input, ReadOnlySpan<byte> text, bool headerOnly)
    {
        CSource source = new(text);
        long width = -1;
        long height = -1;
        long hotX = -1;
        long hotY = -1;
        string name = "image";
        while (source.Accept("#define"u8))
        {
            ReadOnlySpan<byte> identifier = source.Identifier();
            long value = source.Number();
            if (identifier.EndsWith("_width"u8))
            {
                width = value;
                name = Encoding.ASCII.GetString(identifier[..^6]);
            }
            else if (identifier.EndsWith("_height"u8))
            {
                height = value;
            }
            else if (identifier.EndsWith("_x_hot"u8))
            {
                hotX = value;
            }
            else if (identifier.EndsWith("_y_hot"u8))
            {
                hotY = value;
            }
        }

        if (width <= 0 || height <= 0 || width > int.MaxValue || height > int.MaxValue)
        {
            throw input.Corrupt(0, $"The XBM size {width}×{height} is missing or invalid.");
        }

        int declaration = source.Position;
        if (headerOnly)
        {
            return new Parsed((int)width, (int)height, false, declaration, name, null);
        }

        if (!source.SkipPast((byte)'{'))
        {
            throw input.Corrupt(declaration, "The XBM bits array is missing.");
        }

        bool words = source.Since(declaration).IndexOf("short"u8) >= 0;
        Point? hotspot = hotX >= 0 && hotY >= 0 && hotX <= int.MaxValue && hotY <= int.MaxValue ? new Point((int)hotX, (int)hotY) : null;
        return new Parsed((int)width, (int)height, words, source.Position, name, hotspot);
    }

    /// <summary>What the header says, and where the numbers start.</summary>
    private sealed record Parsed(int Width, int Height, bool Words, int DataStart, string Name, Point? Hotspot);

    /// <summary>The rows, read from the numbers in order as they are asked for.</summary>
    private sealed class Rows : ExtendedRows<Gray8, Gray8>
    {
        private readonly Parsed _file;
        private readonly byte[] _text;
        private readonly int _length;
        private readonly byte[] _bits;
        private int _position;
        private bool _ended;

        public Rows(EgamiInput input, ReaderOptions options, Parsed file, byte[] text, int length)
            : base(input, options, XbmFormat.Info, "Xbm", file.Width, file.Height)
        {
            _file = file;
            _text = text;
            _length = length;
            _position = file.DataStart;
            int unit = file.Words ? 16 : 8;
            _bits = new byte[(file.Width + unit - 1) / unit * (unit / 8)];
            Metadata.FormatDetails = new XbmDetails { Name = file.Name, Hotspot = file.Hotspot };
        }

        protected override bool DecodeRow(int y, Span<Gray8> row)
        {
            CSource source = new(_text.AsSpan(0, _length)) { Position = _position };
            int step = _file.Words ? 2 : 1;
            int filled = 0;
            while (!_ended && filled < _bits.Length)
            {
                long value = source.Number();
                if (value < 0)
                {
                    _ended = true;
                    break;
                }

                _bits[filled] = (byte)value;
                if (step == 2)
                {
                    _bits[filled + 1] = (byte)(value >> 8);
                }

                filled += step;
                source.SkipSpace();
                if (!source.Accept(","u8))
                {
                    _ended = true;
                }
            }

            _position = source.Position;
            _bits.AsSpan(filled).Clear();
            Bits.Expand(_bits, row, msbFirst: false, one: 0, zero: 255);
            if (filled < _bits.Length)
            {
                row[Math.Min(row.Length, filled * 8)..].Clear();
                return false;
            }

            return true;
        }
    }
}

/// <summary>Writes X11 XBM: a pixel is set (black) when its gray value is below half. The name and hotspot come from <see cref="XbmDetails"/>.</summary>
internal sealed class XbmWriter : ExtendedWriter
{
    protected override FormatInfo Format => XbmFormat.Info;

    public override RowEncoder<TPixel> Start<TPixel>(Stream output, EncodeStart<TPixel> start)
    {
        start.ReportLosses("XBM", SaveKeeps.None);
        XbmDetails? details = start.Metadata.FormatDetails as XbmDetails;
        return new Encoder<TPixel>(output, start.Width, start.Height, CName(details?.Name), details?.Hotspot);
    }

    /// <summary>Makes <paramref name="name"/> a C identifier: letters, digits and underscores, not starting with a digit.</summary>
    internal static string CName(string? name)
    {
        StringBuilder builder = new();
        foreach (char c in name ?? "")
        {
            builder.Append(char.IsAsciiLetterOrDigit(c) ? c : '_');
        }

        if (builder.Length == 0 || char.IsAsciiDigit(builder[0]))
        {
            builder.Insert(0, "image");
        }

        return builder.ToString();
    }

    private sealed class Encoder<TPixel> : RowEncoder<TPixel>
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        private readonly OutputBuffer _output;
        private readonly Gray8[] _row;
        private readonly byte[] _bits;
        private readonly long _total;
        private long _written;

        public Encoder(Stream output, int width, int height, string name, Point? hotspot)
        {
            _output = new OutputBuffer(output);
            _row = new Gray8[width];
            _bits = new byte[(width + 7) / 8];
            _total = (long)_bits.Length * height;
            StringBuilder header = new();
            header.Append(CultureInfo.InvariantCulture, $"#define {name}_width {width}\n#define {name}_height {height}\n");
            if (hotspot is Point hot)
            {
                header.Append(CultureInfo.InvariantCulture, $"#define {name}_x_hot {hot.X}\n#define {name}_y_hot {hot.Y}\n");
            }

            header.Append(CultureInfo.InvariantCulture, $"static unsigned char {name}_bits[] = {{\n   ");
            _output.Write(Encoding.ASCII.GetBytes(header.ToString()));
        }

        public override void WriteRow(ReadOnlySpan<TPixel> row)
        {
            PixelConvert.Convert<TPixel, Gray8>(row, _row);
            Bits.Pack(_row, _bits, msbFirst: false, oneIsLight: false);
            Span<byte> span = _output.GetSpan(_bits.Length * 8);
            int n = 0;
            foreach (byte b in _bits)
            {
                span[n++] = (byte)'0';
                span[n++] = (byte)'x';
                span[n++] = Hex(b >> 4);
                span[n++] = Hex(b & 15);
                _written++;
                if (_written < _total)
                {
                    span[n++] = (byte)',';
                    if (_written % 12 == 0)
                    {
                        span[n++] = (byte)'\n';
                        span[n++] = (byte)' ';
                        span[n++] = (byte)' ';
                    }

                    span[n++] = (byte)' ';
                }
            }

            _output.Advance(n);
        }

        public override void Complete()
        {
            _output.Write(" };\n"u8);
            _output.Flush();
        }

        protected override void Dispose(bool disposing)
        {
            _output.Dispose();
            base.Dispose(disposing);
        }

        private static byte Hex(int digit) => (byte)(digit < 10 ? '0' + digit : 'a' + digit - 10);
    }
}
