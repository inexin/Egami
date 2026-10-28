using System.Buffers.Binary;
using System.Runtime.InteropServices;

using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Extended.Pcx;

/// <summary>
/// Reads PCX, written from ZSoft's "PCX Technical Reference Manual" (revision 5): one-plane images of 1, 2, 4 or 8 bits, 1-bit
/// images of 2 to 4 planes (EGA), and 8-bit images of 3 (RGB) or 4 (RGBA) planes. Rows stream from the input, except for 8-bit
/// indexed images, whose palette follows the pixels: those read the rest of the file first.
/// </summary>
internal sealed class PcxReader : ExtendedReader
{
    internal const int HeaderLength = 128;
    internal const int SignatureLength = 68;

    /// <summary>The marker before the 256-color palette at the end of the file.</summary>
    internal const byte PaletteMarker = 0x0C;

    /// <summary>Returns whether the header has the ZSoft byte and a version, encoding and layout the format defines.</summary>
    internal static bool Matches(ReadOnlySpan<byte> header)
    {
        return header.Length >= SignatureLength && header[0] == 0x0A && header[1] is 0 or 2 or 3 or 4 or 5 && header[2] <= 1
            && Supported(header[3], header[65]) && BinaryPrimitives.ReadUInt16LittleEndian(header[8..]) >= BinaryPrimitives.ReadUInt16LittleEndian(header[4..])
            && BinaryPrimitives.ReadUInt16LittleEndian(header[10..]) >= BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);
    }

    private static bool Supported(int bits, int planes)
    {
        return (planes == 1 && bits is 1 or 2 or 4 or 8) || (bits == 1 && planes is 2 or 3 or 4) || (bits == 8 && planes is 3 or 4);
    }

    public override RasterInfo ReadInfo(EgamiInput input, ReaderOptions options)
    {
        Header header = ReadHeader(input);
        return Describe(PcxFormat.Info, header.Width, header.Height, header.Layout, RasterAccess.Streamed, header.WorkingBytes);
    }

    public override RowReader Open(EgamiInput input, ReaderOptions options)
    {
        Header header = ReadHeader(input);
        input.CheckFrameSize(header.Width, header.Height);
        ByteFeed feed;
        Rgb8[] palette = header.Palette;
        bool paletteMissing = false;
        if (header.Planes == 1 && header.Bits == 8)
        {
            // The palette is the last 769 bytes: read the rest of the file to reach it. Run-length coding at most doubles the data.
            long raw = (long)header.LineBytes * header.Height;
            byte[] data = InputData.ReadToEnd(input, (raw * 2) + 769 + 4096, out int length);
            if (length >= 769 && data[length - 769] == PaletteMarker)
            {
                palette = new Rgb8[256];
                MemoryMarshal.Cast<byte, Rgb8>(data.AsSpan(length - 768, 768)).CopyTo(palette);
                length -= 769;
            }
            else
            {
                palette = Gray256();
                paletteMissing = true;
            }

            feed = new ByteFeed(data, length, HeaderLength);
        }
        else
        {
            feed = new ByteFeed(input);
        }

        RowReader rows = header.Layout.ChannelOrder switch
        {
            PixelChannelOrder.Gray => new Rows<Gray8, Rgb8>(input, options, header, feed, palette),
            PixelChannelOrder.Rgba => new Rows<Rgba8, Rgba8>(input, options, header, feed, palette),
            _ => new Rows<Rgb8, Rgb8>(input, options, header, feed, palette),
        };
        if (paletteMissing)
        {
            ((IPcxRows)rows).PaletteMissing();
        }

        return rows;
    }

    private static Rgb8[] Gray256()
    {
        Rgb8[] gray = new Rgb8[256];
        for (int i = 0; i < 256; i++)
        {
            gray[i] = new Rgb8((byte)i, (byte)i, (byte)i);
        }

        return gray;
    }

    private static Header ReadHeader(EgamiInput input)
    {
        Span<byte> bytes = stackalloc byte[HeaderLength];
        input.ReadExactly(bytes);
        if (bytes[0] != 0x0A)
        {
            throw input.Corrupt(0, "The PCX signature byte is wrong.");
        }

        int bits = bytes[3];
        int planes = bytes[65];
        if (!Supported(bits, planes))
        {
            throw input.Corrupt(3, $"A PCX image of {planes} planes of {bits} bits is not defined.");
        }

        int xMin = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);
        int yMin = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]);
        int xMax = BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]);
        int yMax = BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..]);
        if (xMax < xMin || yMax < yMin)
        {
            throw input.Corrupt(4, $"The window {xMin},{yMin} to {xMax},{yMax} is empty.");
        }

        int width = xMax - xMin + 1;
        int height = yMax - yMin + 1;
        int lineBytes = BinaryPrimitives.ReadUInt16LittleEndian(bytes[66..]);
        int needed = ((width * bits) + 7) / 8;
        if (lineBytes < needed)
        {
            throw input.Corrupt(66, $"A plane line is {lineBytes} bytes; {width} pixels of {bits} bits need {needed}.");
        }

        Rgb8[] palette;
        if (bits == 1 && planes == 1)
        {
            palette = [new Rgb8(0, 0, 0), new Rgb8(255, 255, 255)];
        }
        else
        {
            palette = new Rgb8[16];
            MemoryMarshal.Cast<byte, Rgb8>(bytes.Slice(16, 48)).CopyTo(palette);
        }

        bool grayHint = BinaryPrimitives.ReadUInt16LittleEndian(bytes[68..]) == 2;
        PixelLayoutInfo layout = planes == 4 && bits == 8 ? Rgba8.Layout
            : (bits == 1 && planes == 1) || (bits == 8 && planes == 1 && grayHint) ? Gray8.Layout
            : Rgb8.Layout;
        int hDpi = BinaryPrimitives.ReadUInt16LittleEndian(bytes[12..]);
        int vDpi = BinaryPrimitives.ReadUInt16LittleEndian(bytes[14..]);
        return new Header(width, height, bytes[1], bytes[2] == 1, bits, planes, lineBytes, palette, layout, hDpi, vDpi);
    }

    /// <summary>What the header says.</summary>
    internal sealed record Header(int Width, int Height, int Version, bool Rle, int Bits, int Planes, int LineBytes, Rgb8[] Palette,
        PixelLayoutInfo Layout, int HorizontalDpi, int VerticalDpi)
    {
        public long WorkingBytes => ((long)Planes * LineBytes) + ((long)Width * Layout.BytesPerPixel);
    }

    private interface IPcxRows
    {
        void PaletteMissing();
    }

    /// <summary>The scanlines, decoded one at a time; a run may carry on into the next scanline.</summary>
    private sealed class Rows<TNatural, TWork> : ExtendedRows<TNatural, TWork>, IPcxRows
        where TNatural : unmanaged, IPixelType<TNatural>
        where TWork : unmanaged, IPixelType<TWork>
    {
        private readonly Header _header;
        private readonly ByteFeed _feed;
        private readonly Rgb8[] _palette;
        private readonly byte[] _line;
        private int _run;
        private byte _value;

        public Rows(EgamiInput input, ReaderOptions options, Header header, ByteFeed feed, Rgb8[] palette)
            : base(input, options, PcxFormat.Info, "Pcx", header.Width, header.Height)
        {
            _header = header;
            _feed = feed;
            _palette = palette;
            _line = new byte[header.Planes * header.LineBytes];
            Metadata.FormatDetails = new PcxDetails
            {
                Version = header.Version,
                BitsPerPixel = header.Bits,
                Planes = header.Planes,
                Palette = header.Planes * header.Bits <= 8 ? palette : ReadOnlyMemory<Rgb8>.Empty,
            };
            if (header.HorizontalDpi > 0 && header.VerticalDpi > 0)
            {
                Metadata.Resolution = new Resolution(header.HorizontalDpi, header.VerticalDpi, ResolutionUnit.PerInch);
            }
        }

        public void PaletteMissing()
        {
            Damaged("PaletteMissing", "The 256-color palette is missing from the end of the file; the pixels are read as gray levels.");
        }

        protected override bool DecodeRow(int y, Span<TWork> row)
        {
            int filled = Fill(_line);
            _line.AsSpan(filled).Clear();
            Span<byte> pixels = MemoryMarshal.AsBytes(row);
            int stride = pixels.Length / row.Length;
            int width = row.Length;
            int lineBytes = _header.LineBytes;
            if (_header.Planes >= 3 && _header.Bits == 8)
            {
                for (int p = 0; p < _header.Planes; p++)
                {
                    ReadOnlySpan<byte> plane = _line.AsSpan(p * lineBytes, width);
                    for (int x = 0; x < width; x++)
                    {
                        pixels[(x * stride) + p] = plane[x];
                    }
                }
            }
            else
            {
                int bits = _header.Bits;
                int mask = (1 << bits) - 1;
                Rgb8[] palette = _palette;
                for (int x = 0; x < width; x++)
                {
                    int index = 0;
                    if (_header.Planes == 1)
                    {
                        int bit = x * bits;
                        index = (_line[bit >> 3] >> (8 - bits - (bit & 7))) & mask;
                    }
                    else
                    {
                        for (int p = 0; p < _header.Planes; p++)
                        {
                            index |= ((_line[(p * lineBytes) + (x >> 3)] >> (7 - (x & 7))) & 1) << p;
                        }
                    }

                    Rgb8 color = (uint)index < (uint)palette.Length ? palette[index] : default;
                    int at = x * stride;
                    pixels[at] = color.R;
                    pixels[at + 1] = color.G;
                    pixels[at + 2] = color.B;
                }
            }

            return filled == _line.Length;
        }

        /// <summary>Decodes the next scanline into <paramref name="line"/>; returns how many bytes there were.</summary>
        private int Fill(Span<byte> line)
        {
            if (!_header.Rle)
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

                if ((b & 0xC0) == 0xC0)
                {
                    int value = _feed.Next();
                    if (value < 0)
                    {
                        break;
                    }

                    _run = b & 0x3F;
                    _value = (byte)value;
                }
                else
                {
                    line[filled++] = (byte)b;
                }
            }

            return filled;
        }
    }
}
