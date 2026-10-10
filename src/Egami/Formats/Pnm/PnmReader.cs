using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Text;

using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Pnm;

/// <summary>
/// Reads PBM, PGM, PPM (plain and binary) and PAM, written from the Netpbm format pages. Only the first image of a stream is read.
/// Samples are scaled to the pixel type exactly, as round(v × 255 / maxval), or × 65535 above maxval 255.
/// </summary>
internal sealed partial class PnmReader : IFormatReader
{
    internal static readonly PnmReader Instance = new();

    private const int MaxHeaderLine = 4096;

    private static readonly Dictionary<string, int> TupleDepths = new(StringComparer.Ordinal)
    {
        ["BLACKANDWHITE"] = 1,
        ["GRAYSCALE"] = 1,
        ["RGB"] = 3,
        ["BLACKANDWHITE_ALPHA"] = 2,
        ["GRAYSCALE_ALPHA"] = 2,
        ["RGB_ALPHA"] = 4,
    };

    private enum Natural
    {
        Gray8,
        Gray16,
        GrayAlpha8,
        Rgb8,
        Rgba8,
        Rgba16,
    }

    /// <summary>Returns whether the data starts with P1 to P7, PF or Pf, and a whitespace character.</summary>
    internal static bool Matches(ReadOnlySpan<byte> header)
    {
        return header.Length >= 3 && header[0] == (byte)'P' && header[1] is (>= (byte)'1' and <= (byte)'7') or (byte)'F' or (byte)'f' && IsSpace(header[2]);
    }

    internal static bool IsSpace(byte b)
    {
        return b is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or 0x0B or 0x0C;
    }

    public RasterInfo ReadInfo(EgamiInput input, ReaderOptions options)
    {
        if (Pfm.Is(input))
        {
            return Pfm.ReadInfo(input);
        }

        Header header = ReadHeader(input);
        PixelLayoutInfo layout = LayoutOf(header.Natural);
        return new RasterInfo
        {
            Format = PnmFormat.Info,
            Width = header.Width,
            Height = header.Height,
            Layout = layout,
            FrameCount = 1,
            HasMetadata = header.Comments.Count > 0,
            EstimatedBytes = (long)header.Width * header.Height * layout.BytesPerPixel,
            Access = RasterAccess.Streamed,
            WorkingBytes = WorkingBytes(header),
        };
    }

    public Raster<TPixel> Read<TPixel>(EgamiInput input, ReaderOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        return Pfm.Is(input) ? Pfm.Read<TPixel>(input, options) : Read<TPixel>(input, options, ReadHeader(input));
    }

    public Raster Read(EgamiInput input, ReaderOptions options)
    {
        if (Pfm.Is(input))
        {
            return Pfm.Read(input, options);
        }

        Header header = ReadHeader(input);
        return header.Natural switch
        {
            Natural.Gray8 => Read<Gray8>(input, options, header),
            Natural.Gray16 => Read<Gray16>(input, options, header),
            Natural.GrayAlpha8 => Read<GrayAlpha8>(input, options, header),
            Natural.Rgb8 => Read<Rgb8>(input, options, header),
            Natural.Rgba8 => Read<Rgba8>(input, options, header),
            _ => Read<Rgba16>(input, options, header),
        };
    }

    private static PixelLayoutInfo LayoutOf(Natural natural)
    {
        return natural switch
        {
            Natural.Gray8 => Gray8.Layout,
            Natural.Gray16 => Gray16.Layout,
            Natural.GrayAlpha8 => GrayAlpha8.Layout,
            Natural.Rgb8 => Rgb8.Layout,
            Natural.Rgba8 => Rgba8.Layout,
            _ => Rgba16.Layout,
        };
    }

    private static Raster<TPixel> Read<TPixel>(EgamiInput input, ReaderOptions options, Header header)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Raster<TPixel> raster = input.CreateRaster<TPixel>(header.Width, header.Height);
        try
        {
            RasterMetadata metadata = raster.Metadata;
            metadata.FormatDetails = new PnmDetails
            {
                Kind = header.Magic switch { 1 or 4 => PnmKind.Bitmap, 2 or 5 => PnmKind.Graymap, 3 or 6 => PnmKind.Pixmap, _ => PnmKind.Pam },
                PlainText = header.Magic <= 3,
                MaxValue = header.MaxValue,
                TupleType = header.TupleType,
            };
            if (options.ReadMetadata)
            {
                foreach (string comment in header.Comments)
                {
                    metadata.Text.Add(new TextEntry("Comment", comment));
                }
            }

            foreach (string warning in header.Warnings)
            {
                metadata.WarningList.Add(warning);
            }

            using Decoder<TPixel> decoder = new(input, header, options.Recovery == RecoveryMode.Strict);
            decoder.Run(raster);
            return raster;
        }
        catch
        {
            raster.Dispose();
            throw;
        }
    }

    private static Header ReadHeader(EgamiInput input)
    {
        if (input.ReadByte() != 'P')
        {
            throw input.Corrupt(0, "The Netpbm signature is wrong.");
        }

        int magic = input.ReadByte() - '0';
        if (magic is < 1 or > 7)
        {
            throw input.Corrupt(1, "The Netpbm signature is wrong.");
        }

        HeaderScanner scanner = new(input);
        Header header;
        if (magic == 7)
        {
            header = scanner.ReadPam();
        }
        else
        {
            int width = scanner.ReadNumber("width");
            int height = scanner.ReadNumber("height");
            int max = magic is 1 or 4 ? 1 : scanner.ReadNumber("maxval");
            long at = input.Position;
            if (!IsSpace(input.ReadByte()))
            {
                throw input.Corrupt(at, "The header does not end with a whitespace character.");
            }

            int depth = magic is 3 or 6 ? 3 : 1;
            header = new Header(magic, width, height, depth, max, null, scanner.Comments, scanner.Warnings);
        }

        if (header.Width < 1 || header.Height < 1)
        {
            throw input.Corrupt(-1, $"The image size {header.Width}×{header.Height} is invalid.");
        }

        if (header.MaxValue is < 1 or > 65535)
        {
            throw input.Corrupt(-1, $"The maxval {header.MaxValue} is outside 1 to 65535.");
        }

        return header;
    }

    /// <summary>What the header says. Bitmaps (P1, P4) have depth 1 and maxval 1.</summary>
    private sealed record Header(int Magic, int Width, int Height, int Depth, int MaxValue, string? TupleType, List<string> Comments, List<string> Warnings)
    {
        public bool Bitmap => Magic is 1 or 4;

        public bool Plain => Magic <= 3;

        public bool Wide => MaxValue > 255;

        public Natural Natural => (Depth, Wide) switch
        {
            (1, false) => Natural.Gray8,
            (1, true) => Natural.Gray16,
            (2, false) => Natural.GrayAlpha8,
            (3, false) => Natural.Rgb8,
            (4, false) => Natural.Rgba8,
            _ => Natural.Rgba16,
        };
    }

    /// <summary>Reads header tokens: whitespace, # comments to the end of the line, decimal numbers and PAM lines.</summary>
    private sealed class HeaderScanner(EgamiInput input)
    {
        private long _commentBytes;

        public List<string> Comments { get; } = [];

        public List<string> Warnings { get; } = [];

        public int ReadNumber(string what)
        {
            while (true)
            {
                byte b = Next();
                if (IsSpace(b))
                {
                    input.ReadByte();
                }
                else if (b == '#')
                {
                    ReadComment();
                }
                else
                {
                    break;
                }
            }

            long at = input.Position;
            if (Next() is < (byte)'0' or > (byte)'9')
            {
                throw input.Corrupt(at, $"The {what} is not a number.");
            }

            long value = 0;
            while (input.Peek(1) is [>= (byte)'0' and <= (byte)'9'] digit)
            {
                value = (value * 10) + digit[0] - '0';
                if (value > int.MaxValue)
                {
                    throw input.Corrupt(at, $"The {what} is too large.");
                }

                input.ReadByte();
            }

            return (int)value;
        }

        public Header ReadPam()
        {
            long at = input.Position;
            if (!IsSpace(input.ReadByte()))
            {
                throw input.Corrupt(at, "P7 is not followed by a newline.");
            }

            int width = -1, height = -1, depth = -1, max = -1;
            string? tuple = null;
            while (true)
            {
                at = input.Position;
                string line = ReadLine().Trim(' ', '\t', '\r', '\v', '\f');
                if (line.Length == 0)
                {
                    continue;
                }

                if (line[0] == '#')
                {
                    AddComment(line[1..]);
                    continue;
                }

                int space = line.IndexOfAny([' ', '\t']);
                string key = space < 0 ? line : line[..space];
                string value = space < 0 ? string.Empty : line[(space + 1)..].Trim(' ', '\t');
                switch (key)
                {
                    case "ENDHDR":
                        if (width < 0 || height < 0 || depth < 0 || max < 0)
                        {
                            throw input.Corrupt(at, "The PAM header lacks WIDTH, HEIGHT, DEPTH or MAXVAL.");
                        }

                        return new Header(7, width, height, CheckDepth(depth, tuple, at), max, tuple, Comments, Warnings);
                    case "WIDTH":
                        width = Number(value, key, at);
                        break;
                    case "HEIGHT":
                        height = Number(value, key, at);
                        break;
                    case "DEPTH":
                        depth = Number(value, key, at);
                        break;
                    case "MAXVAL":
                        max = Number(value, key, at);
                        break;
                    case "TUPLTYPE":
                        tuple = tuple is null ? value : $"{tuple} {value}";
                        break;
                    default:
                        Warnings.Add($"The PAM header line '{Shorten(line)}' is unknown and was ignored.");
                        break;
                }
            }
        }

        private static string Shorten(string text)
        {
            return text.Length <= 40 ? text : text[..40] + "…";
        }

        private int CheckDepth(int depth, string? tuple, long at)
        {
            if (tuple is not null && TupleDepths.TryGetValue(tuple, out int expected) && expected == depth)
            {
                return depth;
            }

            if (depth is < 1 or > 4)
            {
                throw new UnsupportedFormatException($"PAM images of depth {depth} (tuple type '{tuple}') are not supported.");
            }

            string read = depth switch { 1 => "gray", 2 => "gray with alpha", 3 => "RGB", _ => "RGBA" };
            Warnings.Add($"The PAM tuple type '{Shorten(tuple ?? string.Empty)}' at byte {at} does not fit depth {depth}; the image is read as {read}.");
            return depth;
        }

        private int Number(string text, string key, long at)
        {
            if (!int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int value))
            {
                throw input.Corrupt(at, $"The PAM {key} '{Shorten(text)}' is not a number.");
            }

            return value;
        }

        private string ReadLine()
        {
            StringBuilder line = new();
            while (true)
            {
                byte b = input.ReadByte();
                if (b == '\n')
                {
                    return line.ToString();
                }

                if (line.Length >= MaxHeaderLine)
                {
                    throw input.Corrupt(input.Position, "A PAM header line is too long.");
                }

                line.Append((char)b);
            }
        }

        private void ReadComment()
        {
            input.ReadByte();
            List<byte> bytes = [];
            while (input.Peek(1) is [var b] && b != '\n' && b != '\r')
            {
                bytes.Add(b);
                input.ReadByte();
                input.Limits.CheckMetadataBytes(_commentBytes + bytes.Count);
            }

            AddComment(Encoding.UTF8.GetString([.. bytes]));
        }

        private void AddComment(string text)
        {
            _commentBytes += text.Length;
            input.Limits.CheckMetadataBytes(_commentBytes);
            Comments.Add(text.StartsWith(' ') ? text[1..] : text);
        }

        private byte Next()
        {
            ReadOnlySpan<byte> next = input.Peek(1);
            return next.IsEmpty ? throw new CorruptDataException(PnmFormat.Info.Name, input.Position, "The data ends inside the header.") { IsTruncation = true } : next[0];
        }
    }

    /// <summary>Decodes the samples row by row into the raster.</summary>
    /// <summary>Decodes the rows of a PNM image in order, one at a time, into any pixel type.</summary>
    private sealed class Decoder<TPixel>(EgamiInput input, Header header, bool strict) : IDisposable
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        private readonly int _samples = checked(header.Width * header.Depth);
        private readonly ByteReader? _text = header.Plain ? new ByteReader(input) : null;
        private byte[] _bytes = ArrayPool<byte>.Shared.Rent(header.Wide ? checked(header.Width * header.Depth) * 2 : Math.Max(checked(header.Width * header.Depth), (header.Width + 7) / 8));
        private ushort[] _wide = header.Wide ? ArrayPool<ushort>.Shared.Rent(checked(header.Width * header.Depth)) : [];
        private Rgba16[] _expanded = header.Wide && header.Depth > 1 ? ArrayPool<Rgba16>.Shared.Rent(header.Width) : [];
        private bool _clamped;
        private bool _disposed;

        /// <summary>Gets the table that scales 8-bit samples below maxval 255, or an empty one.</summary>
        private byte[] Lut { get; } = !header.Wide && !header.Bitmap && header.MaxValue != 255 ? ScaleTable(header.MaxValue) : [];

        /// <summary>Decodes every row into <paramref name="raster"/>, keeping the rows before damage (Lenient).</summary>
        public void Run(Raster<TPixel> raster)
        {
            int y = 0;
            try
            {
                for (; y < header.Height; y++)
                {
                    DecodeRow(raster.Row(y));
                }

                Finish(raster.Metadata.WarningList);
            }
            catch (CorruptDataException e) when (!strict)
            {
                for (int rest = y; rest < header.Height; rest++)
                {
                    raster.Row(rest).Clear();
                }

                raster.Metadata.WarningList.Add(Damaged(e, y));
            }
        }

        /// <summary>Returns the warning for damage found at row <paramref name="y"/>.</summary>
        public string Damaged(CorruptDataException e, int y)
        {
            return $"{e.Message} The {header.Height - y} rows from row {y} are left black.";
        }

        /// <summary>Decodes the next row into <paramref name="target"/>, which is the image's width.</summary>
        public void DecodeRow(Span<TPixel> target)
        {
            input.CancellationToken.ThrowIfCancellationRequested();
            int width = header.Width;
            byte[] bytes = _bytes;
            byte[] lut = Lut;

            // Binary 8-bit samples at maxval 255 are already the natural type's bytes: read them straight into its rows.
            bool direct = !header.Wide && !header.Plain && !header.Bitmap && lut.Length == 0 && TPixel.Layout == LayoutOf(header.Natural);
            if (header.Wide)
            {
                ReadWide(bytes, _wide);
                Span<ushort> samples = _wide.AsSpan(0, _samples);
                if ((header.Depth == 3 && typeof(TPixel) == typeof(Rgb16)) || (header.Depth == 2 && typeof(TPixel) == typeof(GrayAlpha16)))
                {
                    // The scaled 16-bit samples are the row, in the same order.
                    samples.CopyTo(MemoryMarshal.Cast<TPixel, ushort>(target));
                }
                else if (header.Depth == 1)
                {
                    if (TPixel.Layout.ChannelType == PixelChannelType.Unorm8)
                    {
                        // Narrow to 8 bits first, then take the fast 8-bit conversions.
                        Span<byte> narrow = bytes.AsSpan(0, width);
                        Narrow(samples, narrow);
                        PixelConvert.Convert<Gray8, TPixel>(MemoryMarshal.Cast<byte, Gray8>(narrow), target);
                    }
                    else
                    {
                        PixelConvert.Convert<Gray16, TPixel>(MemoryMarshal.Cast<ushort, Gray16>(samples), target);
                    }
                }
                else
                {
                    Expand(samples, _expanded.AsSpan(0, width));
                    PixelConvert.Convert<Rgba16, TPixel>(_expanded.AsSpan(0, width), target);
                }

                return;
            }

            Span<byte> row = bytes.AsSpan(0, _samples);
            if (direct)
            {
                input.ReadExactly(MemoryMarshal.AsBytes(target));
                return;
            }

            ReadNarrow(row, lut);
            switch (header.Depth)
            {
                case 1:
                    PixelConvert.Convert<Gray8, TPixel>(MemoryMarshal.Cast<byte, Gray8>(row), target);
                    break;
                case 2:
                    PixelConvert.Convert<GrayAlpha8, TPixel>(MemoryMarshal.Cast<byte, GrayAlpha8>(row), target);
                    break;
                case 3:
                    PixelConvert.Convert<Rgb8, TPixel>(MemoryMarshal.Cast<byte, Rgb8>(row), target);
                    break;
                default:
                    PixelConvert.Convert<Rgba8, TPixel>(MemoryMarshal.Cast<byte, Rgba8>(row), target);
                    break;
            }
        }

        /// <summary>After the last row: reports clamped samples and data after the image.</summary>
        public void Finish(List<string> warnings)
        {
            _text?.Finish();
            if (_clamped)
            {
                warnings.Add($"Some samples are above the maxval {header.MaxValue}; they were clamped.");
            }

            ReadOnlySpan<byte> rest = input.Peek(4096);
            foreach (byte b in rest)
            {
                if (!IsSpace(b))
                {
                    warnings.Add("More data follows the image; only the first image is read.");
                    break;
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _text?.Finish();
            _text?.Release();
            ArrayPool<byte>.Shared.Return(_bytes);
            _bytes = [];
            if (_wide.Length > 0)
            {
                ArrayPool<ushort>.Shared.Return(_wide);
                _wide = [];
            }

            if (_expanded.Length > 0)
            {
                ArrayPool<Rgba16>.Shared.Return(_expanded);
                _expanded = [];
            }
        }

        /// <summary>Narrows 16-bit samples to the nearest 8-bit value, as Gray16 to Gray8 does: (v × 255 + 32895) >> 16.</summary>
        private static void Narrow(ReadOnlySpan<ushort> samples, Span<byte> output)
        {
            int x = 0;
            if (Vector256.IsHardwareAccelerated)
            {
                Vector256<uint> scale = Vector256.Create(255u);
                Vector256<uint> round = Vector256.Create(32895u);
                for (; x <= samples.Length - Vector256<ushort>.Count; x += Vector256<ushort>.Count)
                {
                    (Vector256<uint> lower, Vector256<uint> upper) = Vector256.Widen(Vector256.Create(samples.Slice(x, Vector256<ushort>.Count)));
                    Vector256<ushort> narrowed = Vector256.Narrow(((lower * scale) + round) >> 16, ((upper * scale) + round) >> 16);
                    Vector128<byte> bytes = Vector128.Narrow(narrowed.GetLower(), narrowed.GetUpper());
                    bytes.CopyTo(output.Slice(x, Vector128<byte>.Count));
                }
            }

            for (; x < samples.Length; x++)
            {
                output[x] = (byte)(((samples[x] * 255) + 32895) >> 16);
            }
        }

        private static byte[] ScaleTable(int max)
        {
            byte[] lut = new byte[256];
            for (int v = 0; v < 256; v++)
            {
                lut[v] = (byte)((((Math.Min(v, max) * 255 * 2) + max) / (2 * max)));
            }

            return lut;
        }

        private static ushort Scale16(int v, int max)
        {
            return (ushort)((((long)v * 65535 * 2) + max) / (2 * max));
        }

        /// <summary>Reads one row of 8-bit samples (or bits) and scales them to 0 to 255.</summary>
        private void ReadNarrow(Span<byte> row, byte[] lut)
        {
            int max = header.MaxValue;
            switch (header.Magic)
            {
                case 1:
                    for (int x = 0; x < row.Length; x++)
                    {
                        byte b = NextNonSpace();
                        if (b is not ((byte)'0' or (byte)'1'))
                        {
                            throw input.Corrupt(_text!.Offset - 1, "A PBM sample is not 0 or 1.");
                        }

                        row[x] = b == '1' ? (byte)0 : (byte)255;
                    }

                    return;
                case 4:
                    // The packed bits fill the start of the row, which is then expanded from its end backwards.
                    int bytes = (header.Width + 7) / 8;
                    input.ReadExactly(row[..bytes]);
                    for (int x = row.Length - 1; x >= 0; x--)
                    {
                        row[x] = (row[x >> 3] & (0x80 >> (x & 7))) != 0 ? (byte)0 : (byte)255;
                    }

                    return;
                case 2 or 3:
                    for (int x = 0; x < row.Length; x++)
                    {
                        row[x] = (byte)Clamp(ReadPlainNumber(), max);
                    }

                    break;
                default:
                    input.ReadExactly(row);
                    if (max < 255)
                    {
                        foreach (byte b in row)
                        {
                            if (b > max)
                            {
                                Clamped();
                                break;
                            }
                        }
                    }

                    break;
            }

            if (lut.Length > 0)
            {
                for (int x = 0; x < row.Length; x++)
                {
                    row[x] = lut[row[x]];
                }
            }
        }

        /// <summary>Reads one row of 16-bit samples and scales them to 0 to 65535.</summary>
        private void ReadWide(byte[] bytes, ushort[] wide)
        {
            int max = header.MaxValue;
            Span<ushort> samples = wide.AsSpan(0, _samples);
            if (header.Plain)
            {
                for (int x = 0; x < samples.Length; x++)
                {
                    samples[x] = (ushort)Clamp(ReadPlainNumber(), max);
                }
            }
            else
            {
                input.ReadExactly(MemoryMarshal.AsBytes(samples));
                if (BitConverter.IsLittleEndian)
                {
                    BinaryPrimitives.ReverseEndianness(samples, samples);
                }

                if (max != 65535 && samples.IndexOfAnyInRange((ushort)(max + 1), ushort.MaxValue) >= 0)
                {
                    for (int x = 0; x < samples.Length; x++)
                    {
                        samples[x] = (ushort)Clamp(samples[x], max);
                    }
                }
            }

            if (max != 65535)
            {
                for (int x = 0; x < samples.Length; x++)
                {
                    samples[x] = Scale16(samples[x], max);
                }
            }
        }

        private void Expand(ReadOnlySpan<ushort> samples, Span<Rgba16> pixels)
        {
            switch (header.Depth)
            {
                case 2:
                    for (int x = 0; x < pixels.Length; x++)
                    {
                        ushort g = samples[2 * x];
                        pixels[x] = new Rgba16(g, g, g, samples[(2 * x) + 1]);
                    }

                    break;
                case 3:
                    for (int x = 0; x < pixels.Length; x++)
                    {
                        pixels[x] = new Rgba16(samples[3 * x], samples[(3 * x) + 1], samples[(3 * x) + 2], ushort.MaxValue);
                    }

                    break;
                default:
                    MemoryMarshal.Cast<ushort, Rgba16>(samples).CopyTo(pixels);
                    break;
            }
        }

        private int Clamp(int value, int max)
        {
            if (value > max)
            {
                Clamped();
                return max;
            }

            return value;
        }

        private void Clamped()
        {
            if (strict)
            {
                throw input.Corrupt(input.Position, $"A sample is above the maxval {header.MaxValue}.");
            }

            _clamped = true;
        }

        private byte NextNonSpace()
        {
            byte b;
            do
            {
                b = _text!.Next();
            }
            while (IsSpace(b));
            return b;
        }

        private int ReadPlainNumber()
        {
            ByteReader text = _text!;
            ReadOnlySpan<byte> window = text.Window(16);
            int i = 0;
            while (true)
            {
                while (i < window.Length && IsSpace(window[i]))
                {
                    i++;
                }

                if (i < window.Length)
                {
                    break;
                }

                text.Advance(i);
                window = text.Window(16);
                i = 0;
                if (window.IsEmpty)
                {
                    throw new CorruptDataException(PnmFormat.Info.Name, text.Offset, "The data ends unexpectedly.") { IsTruncation = true };
                }
            }

            if (window.Length - i < 12)
            {
                // Keep a whole number in one window.
                text.Advance(i);
                window = text.Window(16);
                i = 0;
            }

            if ((uint)(window[i] - '0') > 9)
            {
                throw input.Corrupt(text.Offset + i, "A sample is not a number.");
            }

            int value = 0;
            for (; i < window.Length && (uint)(window[i] - '0') <= 9; i++)
            {
                value = Math.Min((value * 10) + window[i] - '0', 1_000_000);
            }

            text.Advance(i);
            if (i == window.Length)
            {
                // More digits may follow in the next window.
                for (int d = text.Peek(); (uint)(d - '0') <= 9; d = text.Peek())
                {
                    value = Math.Min((value * 10) + d - '0', 1_000_000);
                    text.Next();
                }
            }

            return value;
        }
    }
}
