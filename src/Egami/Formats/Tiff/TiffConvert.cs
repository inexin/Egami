using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Egami.Pixels;

namespace Egami.Formats.Tiff;

/// <summary>
/// Turns a page's raw samples (as <see cref="TiffDecoder"/> leaves them: chunky, little-endian, predictor undone) into its natural
/// pixel type. The common layouts copy straight across; everything else goes through one general path that reads each sample
/// as a number, applies the photometric interpretation and writes the natural channels.
/// </summary>
internal sealed class TiffConvert
{
    private readonly TiffPage _page;
    private readonly Path _path;
    private readonly int _naturalBytes;
    private readonly byte[]? _lut;
    private readonly double _max;
    private readonly Colors.Vector3d _labWhite;
    private readonly Colors.Matrix3d _labToRgb;

    private enum Path
    {
        Direct,
        Invert8,
        Bilevel,
        Palette8,
        General,
    }

    public TiffConvert(TiffPage page)
    {
        _page = page;
        _naturalBytes = page.Layout.BytesPerPixel;
        _max = page.Float ? 1 : (double)((1UL << page.Bits) - 1);
        if (page.Photometric is 8 or 9 or 10)
        {
            // Lab is relative to the page's white (D50 by default, as Adobe's note and libtiff have it), adapted to sRGB's D65 by Bradford, as ICC does.
            _labWhite = new Colors.Chromaticity(page.WhitePoint.X, page.WhitePoint.Y).ToXyz();
            _labToRgb = Colors.ColorValueMath.XyzToRgb * Colors.Matrix3d.Bradford(_labWhite, Colors.Chromaticity.D65.ToXyz());
        }
        bool plainAlpha = page.AlphaIndex < 0 || (!page.AssociatedAlpha && page.AlphaIndex == page.Samples - 1 && page.Samples == page.ColorSamples + 1);
        bool extrasOk = page.Samples == page.ColorSamples || (plainAlpha && page.AlphaIndex >= 0);
        _path = Path.General;
        if (page.Format == 1 && extrasOk && page.Bits is 8 or 16 && page.Photometric is 1 or 2
            || (page.Format == 1 && page.Bits is 8 or 16 && page.Photometric == 5 && page.Cmyk && page.Samples == 4)
            || (page.Format == 3 && extrasOk && page.Bits == 32 && page.Photometric is 1 or 2 && page.Natural != TiffNatural.RgbaF32)
            || (page.Format == 3 && page.Bits == 32 && page.Photometric == 2 && extrasOk && page.Natural == TiffNatural.RgbaF32)
            || (page.Format == 3 && page.Bits == 16 && page.Photometric == 2 && page.Samples == 4 && plainAlpha && page.AlphaIndex == 3)
            || page.Compression is TiffCodes.SgiLog or TiffCodes.SgiLog24)
        {
            _path = Path.Direct;
        }
        else if (page.Format == 1 && page.Bits == 8 && page.Photometric == 0 && page.Samples == 1)
        {
            _path = Path.Invert8;
        }
        else if (page.Bits == 1 && page.Samples == 1 && page.Photometric is 0 or 1 or 4)
        {
            _path = Path.Bilevel;
            _lut = new byte[256 * 8];
            for (int b = 0; b < 256; b++)
            {
                for (int k = 0; k < 8; k++)
                {
                    bool set = ((b >> (7 - k)) & 1) != 0;
                    _lut[(b * 8) + k] = (byte)(set == (page.Photometric != 0) ? 255 : 0);
                }
            }
        }
        else if (page.Bits == 8 && page.Photometric == 3 && page.Samples == 1 && page.Natural == TiffNatural.Rgb8)
        {
            _path = Path.Palette8;
            _lut = new byte[256 * 3];
            ushort[] map = page.ColorMap!;
            for (int i = 0; i < 256; i++)
            {
                _lut[i * 3] = (byte)(map[i] >> 8);
                _lut[(i * 3) + 1] = (byte)(map[256 + i] >> 8);
                _lut[(i * 3) + 2] = (byte)(map[512 + i] >> 8);
            }
        }
    }

    /// <summary>Gets whether rows copy straight into the natural type with no conversion.</summary>
    public bool IsDirect => _path == Path.Direct;

    /// <summary>Converts <paramref name="count"/> pixels from column <paramref name="x"/> of a raw chunky row into natural pixels.</summary>
    public void Convert(ReadOnlySpan<byte> row, int x, int count, Span<byte> destination)
    {
        TiffPage page = _page;
        switch (_path)
        {
            case Path.Direct:
                row.Slice(x * _naturalBytes, count * _naturalBytes).CopyTo(destination);
                return;
            case Path.Invert8:
                {
                    ReadOnlySpan<byte> from = row.Slice(x, count);
                    for (int i = 0; i < count; i++)
                    {
                        destination[i] = (byte)~from[i];
                    }

                    return;
                }

            case Path.Bilevel:
                {
                    ReadOnlySpan<byte> lut = _lut;
                    int i = 0;
                    int bit = x;
                    while (i < count && (bit & 7) != 0)
                    {
                        destination[i++] = lut[(row[bit >> 3] * 8) + (bit & 7)];
                        bit++;
                    }

                    while (count - i >= 8)
                    {
                        lut.Slice(row[bit >> 3] * 8, 8).CopyTo(destination[i..]);
                        i += 8;
                        bit += 8;
                    }

                    while (i < count)
                    {
                        destination[i++] = lut[(row[bit >> 3] * 8) + (bit & 7)];
                        bit++;
                    }

                    return;
                }

            case Path.Palette8:
                {
                    ReadOnlySpan<byte> lut = _lut;
                    ReadOnlySpan<byte> from = row.Slice(x, count);
                    for (int i = 0; i < count; i++)
                    {
                        int at = from[i] * 3;
                        destination[i * 3] = lut[at];
                        destination[(i * 3) + 1] = lut[at + 1];
                        destination[(i * 3) + 2] = lut[at + 2];
                    }

                    return;
                }
        }

        General(row, x, count, destination);
        _ = page;
    }

    private void General(ReadOnlySpan<byte> row, int x, int count, Span<byte> destination)
    {
        TiffPage page = _page;
        int samples = page.Samples;
        Span<double> s = stackalloc double[samples];
        for (int i = 0; i < count; i++)
        {
            int pixel = x + i;
            for (int k = 0; k < samples; k++)
            {
                s[k] = Sample(row, (pixel * samples) + k);
            }

            Write(s, destination.Slice(i * _naturalBytes, _naturalBytes));
        }
    }

    /// <summary>Reads sample <paramref name="index"/> of a row as a number: an integer's value (signed ones offset to unsigned) or a float's.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private double Sample(ReadOnlySpan<byte> row, int index)
    {
        TiffPage page = _page;
        int bits = page.Bits;
        if (page.Float)
        {
            return bits switch
            {
                16 => (double)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(row[(index * 2)..])),
                32 => BinaryPrimitives.ReadSingleLittleEndian(row[(index * 4)..]),
                _ => BinaryPrimitives.ReadDoubleLittleEndian(row[(index * 8)..]),
            };
        }

        ulong raw = bits switch
        {
            8 => row[index],
            16 => BinaryPrimitives.ReadUInt16LittleEndian(row[(index * 2)..]),
            24 => (ulong)(row[index * 3] | (row[(index * 3) + 1] << 8) | (row[(index * 3) + 2] << 16)),
            32 => BinaryPrimitives.ReadUInt32LittleEndian(row[(index * 4)..]),
            _ => Bits(row, (long)index * bits, bits),
        };
        if (page.Format == 2)
        {
            raw ^= 1UL << (bits - 1);
        }

        return raw;
    }

    /// <summary>Reads <paramref name="count"/> bits from <paramref name="bit"/>, most significant first.</summary>
    private static ulong Bits(ReadOnlySpan<byte> row, long bit, int count)
    {
        ulong value = 0;
        for (int k = 0; k < count; k++, bit++)
        {
            value = (value << 1) | (uint)((row[(int)(bit >> 3)] >> (7 - (int)(bit & 7))) & 1);
        }

        return value;
    }

    private void Write(Span<double> s, Span<byte> natural)
    {
        TiffPage page = _page;
        double max = _max;
        double a = page.AlphaIndex >= 0 ? s[page.AlphaIndex] / max : 1;
        double r;
        double g;
        double b;
        switch (page.Photometric)
        {
            case 0 or 1 or 4:
                double gray = s[0] / max;
                if (page.Photometric == 0)
                {
                    gray = 1 - gray;
                }

                if (page.AssociatedAlpha)
                {
                    gray = Unpremultiply(gray, a);
                }

                WriteGray(gray, a, natural);
                return;
            case 3:
                int index = (int)s[0];
                ushort[] map = page.ColorMap!;
                int entries = map.Length / 3;
                (r, g, b) = index < entries ? (map[index] / 65535.0, map[entries + index] / 65535.0, map[(2 * entries) + index] / 65535.0) : (0, 0, 0);
                break;
            case 5 when !page.Cmyk:
                WriteGray(1 - (s[0] / max), 1, natural);
                return;
            case 5:
                double c = s[0] / max;
                double m = s[1] / max;
                double y = s[2] / max;
                double k = s[3] / max;
                if (page.Natural is TiffNatural.Cmyk8 or TiffNatural.Cmyk16)
                {
                    WriteCmyk(c, m, y, k, natural);
                    return;
                }

                // CMYK without a profile, as libtiff's RGBA interface and Egami's Cmyk8 convert it.
                (r, g, b) = ((1 - c) * (1 - k), (1 - m) * (1 - k), (1 - y) * (1 - k));
                break;
            case 6:
                (r, g, b) = YCbCr(s[0], s[1], s[2]);
                break;
            case 8 or 9 or 10:
                (r, g, b) = Lab(s[0], s[1], s[2]);
                break;
            default:
                (r, g, b) = (s[0] / max, s[1] / max, s[2] / max);
                break;
        }

        if (page.AssociatedAlpha && page.Photometric is 2 or 5)
        {
            (r, g, b) = (Unpremultiply(r, a), Unpremultiply(g, a), Unpremultiply(b, a));
        }

        WriteColor(r, g, b, a, natural);
    }

    private double Unpremultiply(double c, double a)
    {
        if (a <= 0)
        {
            return 0;
        }

        return _page.Float ? c / a : Math.Min(1, c / a);
    }

    /// <summary>YCbCr to RGB (TIFF 6.0 section 21), each component scaled by ReferenceBlackWhite; returns 0 to 1.</summary>
    internal (double R, double G, double B) YCbCr(double y, double cb, double cr)
    {
        double[] reference = _page.ReferenceBlackWhite;
        double[] k = _page.Coefficients;
        double full = _max;
        double half = Math.Floor(full / 2);
        double luma = (y - reference[0]) * full / (reference[1] - reference[0]);
        double blue = (cb - reference[2]) * half / (reference[3] - reference[2]);
        double red = (cr - reference[4]) * half / (reference[5] - reference[4]);
        double r = luma + (red * (2 - (2 * k[0])));
        double b = luma + (blue * (2 - (2 * k[2])));
        double g = (luma - (k[2] * b) - (k[0] * r)) / k[1];
        return (r / full, g / full, b / full);
    }

    /// <summary>
    /// Redoes the YCbCr conversion of RGB a JPEG decoder made with JFIF's full-range BT.601, in place, with the page's
    /// ReferenceBlackWhite and coefficients instead.
    /// </summary>
    public void YCbCrInPlace(Span<byte> pixels)
    {
        for (int i = 0; i + 2 < pixels.Length; i += 3)
        {
            double red = pixels[i];
            double green = pixels[i + 1];
            double blue = pixels[i + 2];
            double y = (0.299 * red) + (0.587 * green) + (0.114 * blue);
            double cb = 128 + ((blue - y) / 1.772);
            double cr = 128 + ((red - y) / 1.402);
            (double r, double g, double b) = YCbCr(y, cb, cr);
            pixels[i] = Unorm8(r);
            pixels[i + 1] = Unorm8(g);
            pixels[i + 2] = Unorm8(b);
        }
    }

    /// <summary>CIE L*a*b* to sRGB (gamma encoded, 0 to 1), relative to the page's white point.</summary>
    internal (double R, double G, double B) Lab(double l, double a, double b)
    {
        TiffPage page = _page;
        int bits = page.Bits;
        double full = _max;
        double half = Math.Pow(2, bits - 1);
        double unit = bits > 8 ? Math.Pow(2, bits - 8) : 1;
        double lightness = l * 100 / full;
        double ca;
        double cb;
        switch (page.Photometric)
        {
            case 8:
                // Signed a* and b*, stored as two's complement whatever the sample format says.
                ca = (a >= half ? a - (2 * half) : a) / unit;
                cb = (b >= half ? b - (2 * half) : b) / unit;
                break;
            case 9:
                ca = (a - half) / unit;
                cb = (b - half) / unit;
                break;
            default:
                // ITU-T T.42's default ranges: a* −85 to 85, b* −75 to 125.
                ca = (a * 170 / full) - 85;
                cb = (b * 200 / full) - 75;
                break;
        }

        Colors.Vector3d xyz = Colors.Cie.LabToXyz(new Colors.Vector3d(lightness, ca, cb), _labWhite);
        Colors.Vector3d linear = _labToRgb * xyz;
        return (Encode(linear.X), Encode(linear.Y), Encode(linear.Z));

        static double Encode(double v) => Colors.ColorValueMath.Encode(Math.Clamp(v, 0, 1));
    }

    private void WriteGray(double gray, double a, Span<byte> natural)
    {
        switch (_page.Natural)
        {
            case TiffNatural.Gray8:
                natural[0] = Unorm8(gray);
                break;
            case TiffNatural.GrayAlpha8:
                natural[0] = Unorm8(gray);
                natural[1] = Unorm8(a);
                break;
            case TiffNatural.Gray16:
                MemoryMarshal.Write(natural, Unorm16(gray));
                break;
            case TiffNatural.GrayAlpha16:
                MemoryMarshal.Write(natural, Unorm16(gray));
                MemoryMarshal.Write(natural[2..], Unorm16(a));
                break;
            case TiffNatural.GrayF32:
                MemoryMarshal.Write(natural, (float)gray);
                break;
            default:
                WriteColor(gray, gray, gray, a, natural);
                break;
        }
    }

    private void WriteColor(double r, double g, double b, double a, Span<byte> natural)
    {
        switch (_page.Natural)
        {
            case TiffNatural.Rgb8:
                natural[0] = Unorm8(r);
                natural[1] = Unorm8(g);
                natural[2] = Unorm8(b);
                break;
            case TiffNatural.Rgba8:
                natural[0] = Unorm8(r);
                natural[1] = Unorm8(g);
                natural[2] = Unorm8(b);
                natural[3] = Unorm8(a);
                break;
            case TiffNatural.Rgb16:
                MemoryMarshal.Write(natural, new Rgb16(Unorm16(r), Unorm16(g), Unorm16(b)));
                break;
            case TiffNatural.Rgba16:
                MemoryMarshal.Write(natural, new Rgba16(Unorm16(r), Unorm16(g), Unorm16(b), Unorm16(a)));
                break;
            case TiffNatural.RgbF32:
                MemoryMarshal.Write(natural, new RgbF32((float)r, (float)g, (float)b));
                break;
            case TiffNatural.RgbaF32:
                MemoryMarshal.Write(natural, new RgbaF32((float)r, (float)g, (float)b, (float)a));
                break;
            case TiffNatural.RgbaF16:
                MemoryMarshal.Write(natural, new RgbaF16((Half)r, (Half)g, (Half)b, (Half)a));
                break;
            default:
                WriteGray((0.299 * r) + (0.587 * g) + (0.114 * b), a, natural);
                break;
        }
    }

    private void WriteCmyk(double c, double m, double y, double k, Span<byte> natural)
    {
        if (_page.Natural == TiffNatural.Cmyk8)
        {
            natural[0] = Unorm8(c);
            natural[1] = Unorm8(m);
            natural[2] = Unorm8(y);
            natural[3] = Unorm8(k);
            return;
        }

        MemoryMarshal.Write(natural, new Cmyk16(Unorm16(c), Unorm16(m), Unorm16(y), Unorm16(k)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Unorm8(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255, MidpointRounding.AwayFromZero);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort Unorm16(double v) => (ushort)Math.Round(Math.Clamp(v, 0, 1) * 65535, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Converts subsampled YCbCr data units (TIFF 6.0 section 21: each SubH × SubV luma samples row by row, then Cb and Cr) of one
    /// strip or tile into RGB rows of <paramref name="band"/> at column <paramref name="x0"/>, a row <paramref name="stride"/> bytes.
    /// </summary>
    public void Blocks(ReadOnlySpan<byte> units, int unitWidth, int unitHeight, int x0, int width, Span<byte> band, int stride)
    {
        TiffPage page = _page;
        int subH = page.SubH;
        int subV = page.SubV;
        int across = (unitWidth + subH - 1) / subH;
        int down = (unitHeight + subV - 1) / subV;
        int blockBytes = (subH * subV) + 2;
        bool sixteen = page.Natural == TiffNatural.Rgb16;
        for (int by = 0; by < down; by++)
        {
            for (int bx = 0; bx < across; bx++)
            {
                int at = ((by * across) + bx) * blockBytes;
                if (at + blockBytes > units.Length)
                {
                    return;
                }

                byte cb = units[at + (subH * subV)];
                byte cr = units[at + (subH * subV) + 1];
                for (int j = 0; j < subV; j++)
                {
                    int y = (by * subV) + j;
                    if (y >= unitHeight)
                    {
                        break;
                    }

                    for (int i = 0; i < subH; i++)
                    {
                        int x = (bx * subH) + i;
                        if (x >= unitWidth || x0 + x >= width)
                        {
                            break;
                        }

                        (double r, double g, double b) = YCbCr(units[at + (j * subH) + i], cb, cr);
                        Span<byte> pixel = band.Slice((y * stride) + ((x0 + x) * (sixteen ? 6 : 3)));
                        if (sixteen)
                        {
                            MemoryMarshal.Write(pixel, new Rgb16(Unorm16(r), Unorm16(g), Unorm16(b)));
                        }
                        else
                        {
                            pixel[0] = Unorm8(r);
                            pixel[1] = Unorm8(g);
                            pixel[2] = Unorm8(b);
                        }
                    }
                }
            }
        }
    }

    /// <summary>Turns SGILog XYZ floats into linear sRGB (D65) in place.</summary>
    public static void XyzToLinearSrgb(Span<float> xyz)
    {
        for (int i = 0; i + 2 < xyz.Length; i += 3)
        {
            float x = xyz[i];
            float y = xyz[i + 1];
            float z = xyz[i + 2];
            xyz[i] = (3.2404542f * x) - (1.5371385f * y) - (0.4985314f * z);
            xyz[i + 1] = (-0.9692660f * x) + (1.8760108f * y) + (0.0415560f * z);
            xyz[i + 2] = (0.0556434f * x) - (0.2040259f * y) + (1.0572252f * z);
        }
    }

    /// <summary>
    /// Gathers one row of each plane into a chunky row, for planar pages: whole-byte samples are copied, smaller ones repacked bit
    /// by bit.
    /// </summary>
    public static void Interleave(TiffPage page, ReadOnlySpan<byte> planes, long planeBytes, int rowOffset, int width, Span<byte> chunky)
    {
        int samples = page.Samples;
        int bits = page.Bits;
        if (bits % 8 == 0)
        {
            int size = bits / 8;
            for (int p = 0; p < samples; p++)
            {
                ReadOnlySpan<byte> plane = planes.Slice((int)(p * planeBytes) + rowOffset, width * size);
                if (size == 1)
                {
                    for (int x = 0; x < width; x++)
                    {
                        chunky[(x * samples) + p] = plane[x];
                    }
                }
                else
                {
                    for (int x = 0; x < width; x++)
                    {
                        plane.Slice(x * size, size).CopyTo(chunky.Slice(((x * samples) + p) * size, size));
                    }
                }
            }

            return;
        }

        chunky[..(int)(((long)width * samples * bits + 7) / 8)].Clear();
        for (int p = 0; p < samples; p++)
        {
            ReadOnlySpan<byte> plane = planes[((int)(p * planeBytes) + rowOffset)..];
            for (int x = 0; x < width; x++)
            {
                ulong v = Bits(plane, (long)x * bits, bits);
                long to = (((long)x * samples) + p) * bits;
                for (int k = bits - 1; k >= 0; k--, to++)
                {
                    if (((v >> k) & 1) != 0)
                    {
                        chunky[(int)(to >> 3)] |= (byte)(0x80 >> (int)(to & 7));
                    }
                }
            }
        }
    }
}
