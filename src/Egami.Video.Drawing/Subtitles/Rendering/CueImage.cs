using System.Numerics;

using Egami.Pixels;

namespace Egami.Video.Subtitles.Rendering;

/// <summary>
/// A drawn cue: straight RGBA pixels in a rectangle of the frame, with the first and last visible pixel of each row, and the
/// same picture turned into Y, U and V samples for the last frame format it was blended into.
/// </summary>
internal sealed class CueImage
{
    private YuvOverlay? _overlay;

    private CueImage(int x, int y, int width, int height, Rgba8[] pixels, int[] rowStart, int[] rowEnd)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
        Pixels = pixels;
        RowStart = rowStart;
        RowEnd = rowEnd;
    }

    public int X { get; }

    public int Y { get; }

    public int Width { get; }

    public int Height { get; }

    public Rgba8[] Pixels { get; }

    /// <summary>The first visible pixel of each row, relative to <see cref="X"/>; equal to <see cref="RowEnd"/> for an empty row.</summary>
    public int[] RowStart { get; }

    /// <summary>One past the last visible pixel of each row.</summary>
    public int[] RowEnd { get; }

    /// <summary>Keeps the visible part of a drawn raster placed at (x, y), or returns <see langword="null"/> when nothing shows.</summary>
    public static CueImage? From(Raster<Rgba8> raster, int x, int y)
    {
        int top = -1, bottom = -1, left = int.MaxValue, right = -1;
        for (int row = 0; row < raster.Height; row++)
        {
            Span<Rgba8> pixels = raster.Row(row);
            int first = -1, last = -1;
            for (int i = 0; i < pixels.Length; i++)
            {
                if (pixels[i].A != 0)
                {
                    if (first < 0)
                    {
                        first = i;
                    }

                    last = i;
                }
            }

            if (first >= 0)
            {
                top = top < 0 ? row : top;
                bottom = row;
                left = Math.Min(left, first);
                right = Math.Max(right, last);
            }
        }

        if (top < 0)
        {
            return null;
        }

        int width = right - left + 1, height = bottom - top + 1;
        var image = new Rgba8[width * height];
        var rowStart = new int[height];
        var rowEnd = new int[height];
        for (int row = 0; row < height; row++)
        {
            Span<Rgba8> source = raster.Row(top + row).Slice(left, width);
            source.CopyTo(image.AsSpan(row * width, width));
            int first = 0, last = width;
            while (first < width && source[first].A == 0)
            {
                first++;
            }

            while (last > first && source[last - 1].A == 0)
            {
                last--;
            }

            rowStart[row] = first;
            rowEnd[row] = last;
        }

        return new CueImage(x + left, y + top, width, height, image, rowStart, rowEnd);
    }

    /// <summary>Blends the cue into planes, converting it to their format first if the last planes had another.</summary>
    public void BlendInto(YuvRaster raster, in YuvFormatKey key)
    {
        if (_overlay is null || !_overlay.Key.Equals(key))
        {
            _overlay = new YuvOverlay(this, key);
        }

        if (raster is YuvRaster<byte> bytes)
        {
            _overlay.Blend(bytes);
        }
        else if (raster is YuvRaster<ushort> words)
        {
            _overlay.Blend(words);
        }
    }

    /// <summary>Blends the cue over an RGBA raster, in its encoded values, as players blend subtitles.</summary>
    public void BlendInto(Raster<Rgba8> target)
    {
        int rows = Math.Min(Height, target.Height - Y);
        for (int row = Math.Max(0, -Y); row < rows; row++)
        {
            int start = Math.Max(RowStart[row], -X), end = Math.Min(RowEnd[row], target.Width - X);
            if (end <= start)
            {
                continue;
            }

            ReadOnlySpan<Rgba8> source = Pixels.AsSpan((row * Width) + start, end - start);
            Span<Rgba8> destination = target.Row(Y + row).Slice(X + start, end - start);
            for (int i = 0; i < source.Length; i++)
            {
                Rgba8 s = source[i];
                if (s.A == 0)
                {
                    continue;
                }

                ref Rgba8 d = ref destination[i];
                if (s.A == 255)
                {
                    d = s;
                }
                else if (d.A == 255)
                {
                    int w = s.A + (s.A >> 7);
                    d.R = (byte)(d.R + ((((s.R - d.R) * w) + 128) >> 8));
                    d.G = (byte)(d.G + ((((s.G - d.G) * w) + 128) >> 8));
                    d.B = (byte)(d.B + ((((s.B - d.B) * w) + 128) >> 8));
                }
                else
                {
                    // Source over a translucent pixel, in straight alpha.
                    int under = d.A * (255 - s.A) / 255;
                    int alpha = s.A + under;
                    d.R = (byte)(((s.R * s.A) + (d.R * under) + (alpha / 2)) / alpha);
                    d.G = (byte)(((s.G * s.A) + (d.G * under) + (alpha / 2)) / alpha);
                    d.B = (byte)(((s.B * s.A) + (d.B * under) + (alpha / 2)) / alpha);
                    d.A = (byte)alpha;
                }
            }
        }
    }
}

/// <summary>What the Y, U and V samples of a cue depend on: matrix, range, bit depth, chroma layout and transfer.</summary>
internal readonly record struct YuvFormatKey(YuvMatrix Matrix, YuvRange Range, int BitDepth, ChromaFormat Chroma, HdrTransfer Transfer, bool Bt2020Primaries);

/// <summary>The transfer a frame's samples are in, as far as the subtitle colors care.</summary>
internal enum HdrTransfer
{
    /// <summary>Standard dynamic range: the colors go in as they are.</summary>
    Sdr,

    /// <summary>SMPTE ST 2084 (PQ): white at 203 cd/m².</summary>
    Pq,

    /// <summary>ARIB STD-B67 (HLG): white at 75% signal, the 203 cd/m² reference of ITU-R BT.2408.</summary>
    Hlg,
}

/// <summary>
/// A cue as Y, U and V samples in one frame format, with its alpha at luma and at chroma resolution, ready to blend straight
/// into the planes: each sample moves towards the cue's by its alpha.
/// </summary>
internal sealed class YuvOverlay
{
    private readonly int _x, _y, _width, _height;
    private readonly ushort[] _luma;
    private readonly byte[] _alpha;
    private readonly int[] _rowStart, _rowEnd;
    private readonly int _cx, _cy, _cWidth, _cHeight;
    private readonly ushort[] _u = [], _v = [];
    private readonly byte[] _cAlpha = [];
    private readonly int[] _cRowStart = [], _cRowEnd = [];
    private readonly int _max;

    public YuvOverlay(CueImage image, YuvFormatKey key)
    {
        Key = key;
        _x = image.X;
        _y = image.Y;
        _width = image.Width;
        _height = image.Height;
        _rowStart = image.RowStart;
        _rowEnd = image.RowEnd;
        _max = (1 << key.BitDepth) - 1;
        int count = _width * _height;
        _luma = new ushort[count];
        _alpha = new byte[count];
        var cb = new float[count];
        var cr = new float[count];
        var converter = new ColorConverter(key);
        Rgba8 last = default;
        (ushort Y, float Cb, float Cr) value = default;
        bool haveLast = false;
        for (int i = 0; i < count; i++)
        {
            Rgba8 p = image.Pixels[i];
            _alpha[i] = p.A;
            if (p.A == 0)
            {
                continue;
            }

            if (!haveLast || p.R != last.R || p.G != last.G || p.B != last.B)
            {
                value = converter.Convert(p);
                last = p;
                haveLast = true;
            }

            _luma[i] = value.Y;
            cb[i] = value.Cb;
            cr[i] = value.Cr;
        }

        (int sx, int sy) = Subsampling(key.Chroma);
        if (sx < 0)
        {
            return;
        }

        // Chroma: each sample takes the mean alpha of the luma samples it covers and their alpha-weighted color.
        _cx = _x >> sx;
        _cy = _y >> sy;
        _cWidth = ((_x + _width - 1) >> sx) - _cx + 1;
        _cHeight = ((_y + _height - 1) >> sy) - _cy + 1;
        int cCount = _cWidth * _cHeight, covered = 1 << (sx + sy);
        _u = new ushort[cCount];
        _v = new ushort[cCount];
        _cAlpha = new byte[cCount];
        _cRowStart = new int[_cHeight];
        _cRowEnd = new int[_cHeight];
        for (int row = 0; row < _cHeight; row++)
        {
            int first = _cWidth, last2 = 0;
            for (int column = 0; column < _cWidth; column++)
            {
                double sumA = 0, sumB = 0, sumR = 0;
                for (int dy = 0; dy < 1 << sy; dy++)
                {
                    int ly = ((_cy + row) << sy) + dy - _y;
                    if ((uint)ly >= (uint)_height)
                    {
                        continue;
                    }

                    for (int dx = 0; dx < 1 << sx; dx++)
                    {
                        int lx = ((_cx + column) << sx) + dx - _x;
                        if ((uint)lx >= (uint)_width)
                        {
                            continue;
                        }

                        int i = (ly * _width) + lx;
                        int a = _alpha[i];
                        sumA += a;
                        sumB += a * cb[i];
                        sumR += a * cr[i];
                    }
                }

                int c = (row * _cWidth) + column;
                int alpha = (int)((sumA + (covered / 2)) / covered);
                _cAlpha[c] = (byte)alpha;
                if (alpha > 0)
                {
                    _u[c] = converter.Chroma(sumB / sumA);
                    _v[c] = converter.Chroma(sumR / sumA);
                    first = Math.Min(first, column);
                    last2 = column + 1;
                }
            }

            _cRowStart[row] = Math.Min(first, last2);
            _cRowEnd[row] = last2;
        }
    }

    public YuvFormatKey Key { get; }

    public void Blend<T>(YuvRaster<T> raster)
        where T : unmanaged, IBinaryInteger<T>
    {
        BlendPlane(raster.Y, _x, _y, _width, _height, _luma, _alpha, _rowStart, _rowEnd);
        if (raster.U is not null && _u.Length > 0)
        {
            BlendPlane(raster.U, _cx, _cy, _cWidth, _cHeight, _u, _cAlpha, _cRowStart, _cRowEnd);
            BlendPlane(raster.V!, _cx, _cy, _cWidth, _cHeight, _v, _cAlpha, _cRowStart, _cRowEnd);
        }

        if (raster.A is YuvPlane<T> alphaPlane)
        {
            // The burned-in text is part of the picture, so it is as opaque as it is drawn.
            BlendOpaque(alphaPlane);
        }
    }

    private void BlendOpaque<T>(YuvPlane<T> plane)
        where T : unmanaged, IBinaryInteger<T>
    {
        int rows = Math.Min(_height, plane.Height - _y);
        for (int row = Math.Max(0, -_y); row < rows; row++)
        {
            int start = Math.Max(_rowStart[row], -_x), end = Math.Min(_rowEnd[row], plane.Width - _x);
            Span<T> destination = plane.Row(_y + row);
            ReadOnlySpan<byte> alpha = _alpha.AsSpan(row * _width, _width);
            for (int i = start; i < end; i++)
            {
                int w = alpha[i] + (alpha[i] >> 7);
                int d = int.CreateTruncating(destination[_x + i]);
                destination[_x + i] = T.CreateTruncating(d + ((((_max - d) * w) + 128) >> 8));
            }
        }
    }

    private static void BlendPlane<T>(YuvPlane<T> plane, int x0, int y0, int width, int height, ushort[] values, byte[] alphas, int[] rowStart, int[] rowEnd)
        where T : unmanaged, IBinaryInteger<T>
    {
        int rows = Math.Min(height, plane.Height - y0);
        for (int row = Math.Max(0, -y0); row < rows; row++)
        {
            int start = Math.Max(rowStart[row], -x0), end = Math.Min(rowEnd[row], plane.Width - x0);
            if (end <= start)
            {
                continue;
            }

            Span<T> destination = plane.Row(y0 + row).Slice(x0 + start, end - start);
            ReadOnlySpan<ushort> source = values.AsSpan((row * width) + start, end - start);
            ReadOnlySpan<byte> alpha = alphas.AsSpan((row * width) + start, end - start);
            for (int i = 0; i < destination.Length; i++)
            {
                int a = alpha[i];
                if (a == 0)
                {
                    continue;
                }

                if (a == 255)
                {
                    destination[i] = T.CreateTruncating(source[i]);
                    continue;
                }

                int w = a + (a >> 7);
                int d = int.CreateTruncating(destination[i]);
                destination[i] = T.CreateTruncating(d + ((((source[i] - d) * w) + 128) >> 8));
            }
        }
    }

    /// <summary>Returns the log2 of the chroma subsampling across and down, or (-1, -1) for luma only.</summary>
    private static (int X, int Y) Subsampling(ChromaFormat format) => format switch
    {
        ChromaFormat.Yuv420 => (1, 1),
        ChromaFormat.Yuv422 => (1, 0),
        ChromaFormat.Yuv444 => (0, 0),
        ChromaFormat.Yuv411 => (2, 0),
        ChromaFormat.Yuv410 => (2, 2),
        _ => (-1, -1),
    };
}

/// <summary>
/// Turns the subtitle's sRGB colors into a frame's samples: for HDR, into linear light with white at 203 cd/m², BT.2020
/// primaries, and PQ or HLG; then the frame's matrix, range and bit depth.
/// </summary>
internal sealed class ColorConverter
{
    private const double M1 = 2610.0 / 16384, M2 = 2523.0 / 4096 * 128, C1 = 3424.0 / 4096, C2 = 2413.0 / 4096 * 32, C3 = 2392.0 / 4096 * 32;
    private const double HlgA = 0.17883277, HlgB = 0.28466892, HlgC = 0.55991073;

    private readonly YuvFormatKey _key;
    private readonly double _kr, _kb;
    private readonly double _scale;
    private readonly int _max;
    private readonly double _hlgWhite = (Math.Exp((0.75 - HlgC) / HlgA) + HlgB) / 12;

    public ColorConverter(YuvFormatKey key)
    {
        _key = key;
        (_kr, _kb) = key.Matrix switch
        {
            YuvMatrix.Bt601 => (0.299, 0.114),
            YuvMatrix.Bt2020Ncl => (0.2627, 0.0593),
            YuvMatrix.Identity => (0, 0),
            _ => (0.2126, 0.0722),
        };
        _max = (1 << key.BitDepth) - 1;
        _scale = 1 << (key.BitDepth - 8);
    }

    /// <summary>Returns the luma sample and the two color differences, -0.5 to 0.5, of a color.</summary>
    public (ushort Y, float Cb, float Cr) Convert(Rgba8 color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        if (_key.Transfer != HdrTransfer.Sdr || _key.Bt2020Primaries)
        {
            (r, g, b) = Encode(Linear(r), Linear(g), Linear(b));
        }

        if (_key.Matrix == YuvMatrix.Identity)
        {
            return (Luma(g), (float)(b - 0.5), (float)(r - 0.5));
        }

        double y = (_kr * r) + ((1 - _kr - _kb) * g) + (_kb * b);
        return (Luma(y), (float)((b - y) / (2 * (1 - _kb))), (float)((r - y) / (2 * (1 - _kr))));
    }

    /// <summary>Returns the chroma sample of a color difference.</summary>
    public ushort Chroma(double difference)
    {
        if (_key.Matrix == YuvMatrix.Identity)
        {
            return Luma(difference + 0.5);
        }

        double value = _key.Range == YuvRange.Limited ? (128 + (224 * difference)) * _scale : (difference * _max) + (1 << (_key.BitDepth - 1));
        return (ushort)Math.Clamp(Math.Round(value), 0, _max);
    }

    private ushort Luma(double value)
    {
        double sample = _key.Range == YuvRange.Limited ? (16 + (219 * value)) * _scale : value * _max;
        return (ushort)Math.Clamp(Math.Round(sample), 0, _max);
    }

    /// <summary>sRGB's electro-optical transfer: the encoded value to linear light, 0 to 1.</summary>
    private static double Linear(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);

    private (double R, double G, double B) Encode(double r, double g, double b)
    {
        if (_key.Bt2020Primaries)
        {
            // BT.709 to BT.2020 primaries (ITU-R BT.2087).
            (r, g, b) = ((0.6274 * r) + (0.3293 * g) + (0.0433 * b), (0.0691 * r) + (0.9195 * g) + (0.0114 * b), (0.0164 * r) + (0.0880 * g) + (0.8956 * b));
        }

        return _key.Transfer switch
        {
            HdrTransfer.Pq => (Pq(r), Pq(g), Pq(b)),
            HdrTransfer.Hlg => (Hlg(r), Hlg(g), Hlg(b)),
            _ => (Gamma(r), Gamma(g), Gamma(b)),
        };
    }

    private static double Gamma(double v) => v <= 0.0031308 ? v * 12.92 : (1.055 * Math.Pow(v, 1 / 2.4)) - 0.055;

    /// <summary>PQ's inverse EOTF of a linear value whose 1 is 203 cd/m².</summary>
    private static double Pq(double v)
    {
        double l = Math.Pow(Math.Max(0, v) * 203 / 10000, M1);
        return Math.Pow((C1 + (C2 * l)) / (1 + (C3 * l)), M2);
    }

    /// <summary>HLG's OETF of a linear value whose 1 is the 75% reference white.</summary>
    private double Hlg(double v)
    {
        double e = Math.Max(0, v) * _hlgWhite;
        return e <= 1.0 / 12 ? Math.Sqrt(3 * e) : (HlgA * Math.Log((12 * e) - HlgB)) + HlgC;
    }
}
