using System.Buffers.Binary;
using System.Runtime.InteropServices;

using Egami.Pixels;

namespace Egami.Formats.Extended.SunRaster;

/// <summary>
/// Reads Sun raster, written from the rasterfile description in the SunOS manual pages (rasterfile(5)): types 0 (old), 1
/// (standard), 2 (byte-encoded) and 3 (RGB order); colormaps of type 1 (equal RGB), and type 2 (raw) maps skipped. Without a
/// colormap, 1-bit pixels are black where set and 8-bit pixels are gray levels. Rows stream from the input.
/// </summary>
internal sealed class SunRasterReader : ExtendedReader
{
    internal const int HeaderLength = 32;
    internal const uint Magic = 0x59A66A95;

    internal const int TypeOld = 0;
    internal const int TypeStandard = 1;
    internal const int TypeByteEncoded = 2;
    internal const int TypeRgb = 3;

    public override RasterInfo ReadInfo(EgamiInput input, ReaderOptions options)
    {
        Header header = ReadHeader(input);
        return Describe(SunRasterFormat.Info, header.Width, header.Height, header.Layout, RasterAccess.Streamed, header.RowBytes + ((long)header.Width * 3));
    }

    public override RowReader Open(EgamiInput input, ReaderOptions options)
    {
        Header header = ReadHeader(input);
        input.CheckFrameSize(header.Width, header.Height);
        return header.Layout.ChannelOrder == PixelChannelOrder.Gray
            ? new Rows<Gray8>(input, options, header)
            : new Rows<Rgb8>(input, options, header);
    }

    private static Header ReadHeader(EgamiInput input)
    {
        Span<byte> bytes = stackalloc byte[HeaderLength];
        input.ReadExactly(bytes);
        if (BinaryPrimitives.ReadUInt32BigEndian(bytes) != Magic)
        {
            throw input.Corrupt(0, "The Sun raster signature is wrong.");
        }

        uint width = BinaryPrimitives.ReadUInt32BigEndian(bytes[4..]);
        uint height = BinaryPrimitives.ReadUInt32BigEndian(bytes[8..]);
        uint depth = BinaryPrimitives.ReadUInt32BigEndian(bytes[12..]);
        uint type = BinaryPrimitives.ReadUInt32BigEndian(bytes[20..]);
        uint mapType = BinaryPrimitives.ReadUInt32BigEndian(bytes[24..]);
        uint mapLength = BinaryPrimitives.ReadUInt32BigEndian(bytes[28..]);
        if (width is 0 or > int.MaxValue || height is 0 or > int.MaxValue)
        {
            throw input.Corrupt(4, $"The image size {width}×{height} is invalid.");
        }

        if (depth is not (1 or 8 or 24 or 32))
        {
            throw input.Corrupt(12, $"A depth of {depth} bits is not defined.");
        }

        if (type > TypeRgb)
        {
            throw input.Corrupt(20, $"Raster type {type} is not supported; types 0 to 3 are.");
        }

        if (mapType > 2)
        {
            throw input.Corrupt(24, $"Colormap type {mapType} is not defined.");
        }

        input.CheckMetadataBytes(mapLength);
        Rgb8[] map = [];
        if (mapType == 1 && mapLength > 0)
        {
            int entries = (int)(mapLength / 3);
            byte[] planes = new byte[mapLength];
            input.ReadExactly(planes);
            map = new Rgb8[Math.Min(entries, 256)];
            for (int i = 0; i < map.Length; i++)
            {
                map[i] = new Rgb8(planes[i], planes[entries + i], planes[(2 * entries) + i]);
            }
        }
        else
        {
            input.Skip(mapLength);
        }

        bool indexed = depth <= 8;
        bool gray = indexed && (map.Length == 0 || IsGray(map));
        long rowBytes = ((((long)width * depth) + 15) / 16) * 2;
        return new Header((int)width, (int)height, (int)depth, (int)type, map, gray ? Gray8.Layout : Rgb8.Layout, rowBytes);
    }

    private static bool IsGray(ReadOnlySpan<Rgb8> map)
    {
        foreach (Rgb8 c in map)
        {
            if (c.R != c.G || c.G != c.B)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>What the header says.</summary>
    private sealed record Header(int Width, int Height, int Depth, int Type, Rgb8[] Map, PixelLayoutInfo Layout, long RowBytes);

    /// <summary>The rows; in a byte-encoded file a run may carry on into the next row.</summary>
    private sealed class Rows<TNatural> : ExtendedRows<TNatural, Rgb8>
        where TNatural : unmanaged, IPixelType<TNatural>
    {
        private readonly Header _header;
        private readonly ByteFeed _feed;
        private readonly byte[] _line;
        private readonly Rgb8[] _palette;
        private int _run;
        private byte _value;

        public Rows(EgamiInput input, ReaderOptions options, Header header)
            : base(input, options, SunRasterFormat.Info, "Sun", header.Width, header.Height)
        {
            _header = header;
            input.CheckMemory(header.RowBytes);
            _line = new byte[header.RowBytes];
            _feed = new ByteFeed(input);
            _palette = header.Map.Length > 0 ? Pad(header.Map) : DefaultPalette(header.Depth);
            Metadata.FormatDetails = new SunRasterDetails
            {
                Depth = header.Depth,
                Rle = header.Type == TypeByteEncoded,
                RgbOrder = header.Type == TypeRgb,
                Colormap = header.Map,
            };
        }

        protected override bool DecodeRow(int y, Span<Rgb8> row)
        {
            int filled = Fill(_line);
            _line.AsSpan(filled).Clear();
            ReadOnlySpan<byte> line = _line;
            int width = row.Length;
            switch (_header.Depth)
            {
                case 1:
                    for (int x = 0; x < width; x++)
                    {
                        row[x] = _palette[(line[x >> 3] >> (7 - (x & 7))) & 1];
                    }

                    break;
                case 8:
                    for (int x = 0; x < width; x++)
                    {
                        row[x] = _palette[line[x]];
                    }

                    break;
                default:
                    int step = _header.Depth / 8;
                    int first = step - 3;
                    bool rgb = _header.Type == TypeRgb;
                    for (int x = 0; x < width; x++)
                    {
                        int at = (x * step) + first;
                        row[x] = rgb ? new Rgb8(line[at], line[at + 1], line[at + 2]) : new Rgb8(line[at + 2], line[at + 1], line[at]);
                    }

                    break;
            }

            return filled == _line.Length;
        }

        private static Rgb8[] Pad(Rgb8[] map)
        {
            Rgb8[] palette = new Rgb8[256];
            map.CopyTo(palette, 0);
            return palette;
        }

        private static Rgb8[] DefaultPalette(int depth)
        {
            if (depth == 1)
            {
                return [new Rgb8(255, 255, 255), new Rgb8(0, 0, 0)];
            }

            Rgb8[] gray = new Rgb8[256];
            for (int i = 0; i < 256; i++)
            {
                gray[i] = new Rgb8((byte)i, (byte)i, (byte)i);
            }

            return gray;
        }

        /// <summary>Reads the next row's bytes, decoding runs in a byte-encoded file; returns how many there were.</summary>
        private int Fill(Span<byte> line)
        {
            if (_header.Type != TypeByteEncoded)
            {
                return _feed.Read(line);
            }

            int filled = 0;
            while (filled < line.Length)
            {
                if (_run > 0)
                {
                    int n = Math.Min(_run, line.Length - filled);
                    line.Slice(filled, n).Fill(_value);
                    filled += n;
                    _run -= n;
                    continue;
                }

                int b = _feed.Next();
                if (b < 0)
                {
                    break;
                }

                if (b != 0x80)
                {
                    line[filled++] = (byte)b;
                    continue;
                }

                int count = _feed.Next();
                if (count < 0)
                {
                    break;
                }

                if (count == 0)
                {
                    line[filled++] = 0x80;
                    continue;
                }

                int value = _feed.Next();
                if (value < 0)
                {
                    break;
                }

                _run = count + 1;
                _value = (byte)value;
            }

            return filled;
        }
    }
}

/// <summary>
/// Writes Sun raster: gray images as 8-bit gray levels without a colormap, everything else as 24-bit pixels, blue first (type
/// 1), or run-length coded (type 2) when <see cref="SunRasterWriterOptions.Rle"/> is set.
/// </summary>
internal sealed class SunRasterWriter : ExtendedWriter
{
    protected override FormatInfo Format => SunRasterFormat.Info;

    public override RowEncoder<TPixel> Start<TPixel>(Stream output, EncodeStart<TPixel> start)
    {
        SunRasterWriterOptions sun = start.Options as SunRasterWriterOptions ?? SunRasterWriterOptions.Default;
        start.ReportLosses("Sun raster", SaveKeeps.None);
        return IsGray<TPixel>() ? new Encoder<TPixel, Gray8>(output, start, sun.Rle) : new Encoder<TPixel, Bgr8>(output, start, sun.Rle);
    }

    private sealed class Encoder<TPixel, TFile> : RowEncoder<TPixel>
        where TPixel : unmanaged, IPixelType<TPixel>
        where TFile : unmanaged, IPixelType<TFile>
    {
        private readonly Stream _stream;
        private readonly OutputBuffer _output;
        private readonly MemoryStream? _encoded;
        private readonly TFile[] _row;
        private readonly byte[] _line;
        private readonly int _width;
        private readonly int _height;
        private readonly int _depth;

        public Encoder(Stream output, EncodeStart<TPixel> start, bool rle)
        {
            _stream = output;
            _width = start.Width;
            _height = start.Height;
            _depth = TFile.Layout.BitsPerPixel;
            _row = new TFile[_width];
            _line = new byte[((((long)_width * _depth) + 15) / 16) * 2];
            if (rle)
            {
                // The header holds the coded length, so the coded rows wait in memory until the last one.
                _encoded = new MemoryStream();
                _output = new OutputBuffer(_encoded);
            }
            else
            {
                _output = new OutputBuffer(output);
                WriteHeader(_output, (long)_line.Length * _height);
            }
        }

        public override void WriteRow(ReadOnlySpan<TPixel> row)
        {
            PixelConvert.Convert<TPixel, TFile>(row, _row);
            MemoryMarshal.AsBytes(_row.AsSpan()).CopyTo(_line);
            if (_encoded is null)
            {
                _output.Write(_line);
            }
            else
            {
                Encode(_line);
            }
        }

        public override void Complete()
        {
            _output.Flush();
            if (_encoded is not null)
            {
                using OutputBuffer header = new(_stream, SunRasterReader.HeaderLength);
                WriteHeader(header, _encoded.Length);
                header.Flush();
                _encoded.Position = 0;
                _encoded.CopyTo(_stream);
            }
        }

        protected override void Dispose(bool disposing)
        {
            _output.Dispose();
            _encoded?.Dispose();
            base.Dispose(disposing);
        }

        private void WriteHeader(OutputBuffer output, long length)
        {
            Span<byte> header = output.GetSpan(SunRasterReader.HeaderLength)[..SunRasterReader.HeaderLength];
            BinaryPrimitives.WriteUInt32BigEndian(header, SunRasterReader.Magic);
            BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)_width);
            BinaryPrimitives.WriteUInt32BigEndian(header[8..], (uint)_height);
            BinaryPrimitives.WriteUInt32BigEndian(header[12..], (uint)_depth);
            BinaryPrimitives.WriteUInt32BigEndian(header[16..], (uint)Math.Min(length, uint.MaxValue));
            BinaryPrimitives.WriteUInt32BigEndian(header[20..], (uint)(_encoded is null ? SunRasterReader.TypeStandard : SunRasterReader.TypeByteEncoded));
            BinaryPrimitives.WriteUInt32BigEndian(header[24..], 0);
            BinaryPrimitives.WriteUInt32BigEndian(header[28..], 0);
            output.Advance(SunRasterReader.HeaderLength);
        }

        /// <summary>Codes a row: runs of three to 256 equal bytes, and any run of the escape byte 0x80, as 0x80, count − 1, value.</summary>
        private void Encode(ReadOnlySpan<byte> line)
        {
            Span<byte> span = _output.GetSpan(line.Length * 3);
            int n = 0;
            int i = 0;
            while (i < line.Length)
            {
                byte value = line[i];
                int run = 1;
                while (run < 256 && i + run < line.Length && line[i + run] == value)
                {
                    run++;
                }

                if (run >= 3 || (value == 0x80 && run == 2))
                {
                    span[n++] = 0x80;
                    span[n++] = (byte)(run - 1);
                    span[n++] = value;
                }
                else if (value == 0x80)
                {
                    span[n++] = 0x80;
                    span[n++] = 0;
                }
                else
                {
                    span[n++] = value;
                    if (run == 2)
                    {
                        span[n++] = value;
                    }
                }

                i += run;
            }

            _output.Advance(n);
        }
    }
}
