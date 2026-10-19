using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Egami.Formats.Heif;

/// <summary>
/// Converts decoded HEIF planes to RGB (or gray) with alpha, as libavif 1.4.2 does without libyuv (reformat.c, alpha.c and colr.c,
/// BSD 2-clause, see THIRD-PARTY-NOTICES), so Load equals avifdec's output sample for sample: single-precision lookup tables
/// for the range, Kr and Kb from H.273 (derived from the primaries for code 12), subsampled chroma upsampled bilinearly with
/// 9/16, 3/16, 3/16 and 1/16 weights, and alpha premultiplication undone in floating point, or on the stored values for 4:4:4.
/// Samples come out at 8 bits for 8-bit planes and at 16 bits otherwise.
/// </summary>
internal sealed class HeifRgbConverter : IHeifRgbConverter
{
    private readonly HeifPlanes _color;
    private readonly HeifPlanes? _alpha;
    private readonly Mode _mode;
    private readonly float _kr;
    private readonly float _kg;
    private readonly float _kb;
    private readonly float[] _tableY;
    private readonly float[] _tableUv;
    private readonly int _yuvMax;
    private readonly int _rgbMax;
    private readonly bool _floatUnmultiply;
    private readonly bool _storedUnmultiply;
    private readonly float _biasY;
    private readonly float _rangeY;
    private readonly float _biasUv;
    private readonly float _rangeUv;

    /// <summary>Prepares the conversion of <paramref name="color"/> (and <paramref name="alpha"/>'s luma as alpha).</summary>
    /// <param name="color">The color planes.</param>
    /// <param name="alpha">The alpha planes, the same size, or null.</param>
    /// <param name="premultiplied">Whether the color is premultiplied by the alpha.</param>
    /// <param name="codePoints">The color's H.273 code points and range.</param>
    internal HeifRgbConverter(HeifPlanes color, HeifPlanes? alpha, bool premultiplied, Colors.ColorCodePoints codePoints)
    {
        _color = color;
        _alpha = alpha;
        int depth = color.BitDepth;
        _yuvMax = (1 << depth) - 1;
        OutputDepth = depth > 8 ? 16 : 8;
        _rgbMax = (1 << OutputDepth) - 1;
        Channels = (color.Monochrome ? 1 : 3) + (alpha is null ? 0 : 1);
        int matrix = codePoints.MatrixCoefficients;
        _mode = matrix switch
        {
            0 => Mode.Identity,
            8 => Mode.YCgCo,
            15 => Mode.YCgCoRe,
            16 => Mode.YCgCoRo,
            _ => Mode.Coefficients,
        };
        (_kr, _kg, _kb) = _mode == Mode.Coefficients ? Coefficients(matrix, codePoints.Primaries) : (0f, 0f, 0f);
        Supported = matrix is not (3 or 10 or 11 or 13 or 14) and < 17 && !(matrix is 8 or 15 or 16 && !codePoints.FullRange)
            && !(matrix == 0 && color.Chroma is Pixels.ChromaFormat.Yuv420 or Pixels.ChromaFormat.Yuv422);

        bool limited = !codePoints.FullRange;
        float biasY = limited ? 16 << (depth - 8) : 0f;
        float biasUv = 1 << (depth - 1);
        float rangeY = limited ? 219 << (depth - 8) : _yuvMax;
        float rangeUv = limited ? 224 << (depth - 8) : _yuvMax;
        (_biasY, _rangeY, _biasUv, _rangeUv) = (biasY, rangeY, biasUv, rangeUv);
        _tableY = new float[_yuvMax + 1];
        for (int cp = 0; cp <= _yuvMax; cp++)
        {
            _tableY[cp] = ((float)cp - biasY) / rangeY;
        }

        if (_mode == Mode.Identity)
        {
            _tableUv = _tableY;
        }
        else
        {
            _tableUv = new float[_yuvMax + 1];
            for (int cp = 0; cp <= _yuvMax; cp++)
            {
                _tableUv[cp] = ((float)cp - biasUv) / rangeUv;
            }
        }

        // libavif's fast paths (4:4:4 with the matrix coefficients, or 8-bit full-range identity) undo premultiplication on the
        // stored values afterwards; its general path does it in floating point before storing.
        bool fast = !color.Monochrome && color.Chroma == Pixels.ChromaFormat.Yuv444
            && (_mode == Mode.Coefficients || (_mode == Mode.Identity && depth == 8 && codePoints.FullRange));
        _storedUnmultiply = premultiplied && alpha is not null && fast;
        _floatUnmultiply = premultiplied && alpha is not null && !fast;
    }

    private enum Mode
    {
        Coefficients,
        Identity,
        YCgCo,
        YCgCoRe,
        YCgCoRo,
    }

    /// <summary>Gets the bits of an output sample: 8 or 16.</summary>
    public int OutputDepth { get; }

    /// <summary>Gets the samples a pixel has: gray or RGB, then alpha if any.</summary>
    public int Channels { get; }

    /// <summary>
    /// Gets whether libavif converts this matrix; it refuses the reserved and constant-luminance codes (and YCgCo at limited range),
    /// which are converted here with the nearest matrix it has.
    /// </summary>
    public bool Supported { get; }

    /// <summary>
    /// Converts <paramref name="width"/> pixels of row <paramref name="y"/> from column <paramref name="x"/> into
    /// <paramref name="target"/>, as interleaved samples of <see cref="OutputDepth"/> bits (bytes, or native-endian 16-bit words).
    /// </summary>
    public void Write(int y, int x, int width, Span<byte> target)
    {
        if (_mode == Mode.Coefficients && !_color.Monochrome && !_floatUnmultiply)
        {
            WriteCoefficients(y, x, width, target);
            return;
        }

        int[] samples = System.Buffers.ArrayPool<int>.Shared.Rent(width * Channels);
        try
        {
            Span<int> row = samples.AsSpan(0, width * Channels);
            Row(y, x, width, row);
            if (OutputDepth > 8)
            {
                Span<ushort> words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(target);
                for (int i = 0; i < row.Length; i++)
                {
                    words[i] = (ushort)row[i];
                }
            }
            else
            {
                for (int i = 0; i < row.Length; i++)
                {
                    target[i] = (byte)row[i];
                }
            }
        }
        finally
        {
            System.Buffers.ArrayPool<int>.Shared.Return(samples);
        }
    }

    /// <summary>
    /// The common case of <see cref="Write"/>, color through Kr and Kb without premultiplication undone in floating point: the
    /// chroma rows' lookups and weighted terms computed once a row, with the operations in libavif's order so the result is the same.
    /// </summary>
    private void WriteCoefficients(int y, int x, int width, Span<byte> target)
    {
        HeifPlanes c = _color;
        int ssx = c.SubsamplingX, ssy = c.SubsamplingY, max = _yuvMax;
        ushort[] yPlane = c.Samples[0], uPlane = c.Samples[1], vPlane = c.Samples[2];
        int yRow = y * c.Strides[0], uvStride = c.Strides[1];
        int uvRow = (y >> ssy) * uvStride;
        bool subsampled = (ssx | ssy) != 0;
        float[] tableY = _tableY, tableUv = _tableUv;
        float cR = 2 * (1 - _kr), cB = 2 * (1 - _kb), kR = _kr * (1 - _kr), kB = _kb * (1 - _kb), kg = _kg;
        float maxF = _rgbMax;
        bool wide = OutputDepth > 8;
        Span<ushort> words = wide ? System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(target) : default;
        ushort[]? aPlane = _alpha?.Samples[0];
        int aRow = _alpha is null ? 0 : y * _alpha.Strides[0];
        int channels = Channels;

        // For subsampled chroma, the four weighted terms of each chroma sample in the row and in the adjacent row.
        int first = 0, count = 0;
        float[]? terms = null;
        if (subsampled)
        {
            int adjRow = 0;
            if (!(y == 0 || (y == c.Height - 1 && (y & 1) != 0) || ssy == 0))
            {
                adjRow = (y & 1) != 0 ? uvStride : -uvStride;
            }

            first = Math.Max(0, (x >> ssx) - 1);
            int last = Math.Min(c.ChromaWidth - 1, ((x + width - 1) >> ssx) + 1);
            count = last - first + 1;
            terms = System.Buffers.ArrayPool<float>.Shared.Rent(8 * count);
            for (int k = 0; k < count; k++)
            {
                int at = uvRow + first + k;
                float u = tableUv[Math.Min(uPlane[at], max)], ua = tableUv[Math.Min(uPlane[at + adjRow], max)];
                float v = tableUv[Math.Min(vPlane[at], max)], va = tableUv[Math.Min(vPlane[at + adjRow], max)];
                terms[k] = u * (9.0f / 16.0f);
                terms[count + k] = u * (3.0f / 16.0f);
                terms[(2 * count) + k] = ua * (3.0f / 16.0f);
                terms[(3 * count) + k] = ua * (1.0f / 16.0f);
                terms[(4 * count) + k] = v * (9.0f / 16.0f);
                terms[(5 * count) + k] = v * (3.0f / 16.0f);
                terms[(6 * count) + k] = va * (3.0f / 16.0f);
                terms[(7 * count) + k] = va * (1.0f / 16.0f);
            }
        }

        try
        {
            if (aPlane is null && Vector256.IsHardwareAccelerated && width >= 8)
            {
                WriteVector(y, x, width, target, terms, first, count);
                return;
            }

            for (int i = 0, at = 0; i < width; i++, at += channels)
            {
                int px = x + i;
                float yf = tableY[Math.Min(yPlane[yRow + px], max)];
                float cb, cr;
                if (subsampled)
                {
                    int k = (px >> ssx) - first;
                    int adj = px == 0 || (px == c.Width - 1 && (px & 1) != 0) || ssx == 0 ? 0 : (px & 1) != 0 ? 1 : -1;
                    float[] t = terms!;
                    cb = ((t[k] + t[count + k + adj]) + t[(2 * count) + k]) + t[(3 * count) + k + adj];
                    cr = ((t[(4 * count) + k] + t[(5 * count) + k + adj]) + t[(6 * count) + k]) + t[(7 * count) + k + adj];
                }
                else
                {
                    cb = tableUv[Math.Min(uPlane[uvRow + px], max)];
                    cr = tableUv[Math.Min(vPlane[uvRow + px], max)];
                }

                float r = Math.Clamp(yf + (cR * cr), 0f, 1f);
                float b = Math.Clamp(yf + (cB * cb), 0f, 1f);
                float g = Math.Clamp(yf - ((2 * ((kR * cr) + (kB * cb))) / kg), 0f, 1f);
                int ri = (int)(0.5f + (r * maxF)), gi = (int)(0.5f + (g * maxF)), bi = (int)(0.5f + (b * maxF));
                int alpha = -1;
                if (aPlane is not null)
                {
                    alpha = Alpha(aPlane[aRow + px]);
                    if (_storedUnmultiply && alpha < _rgbMax)
                    {
                        (ri, gi, bi) = alpha == 0 ? (0, 0, 0) : (Unmultiplied(ri, alpha), Unmultiplied(gi, alpha), Unmultiplied(bi, alpha));
                    }
                }

                if (wide)
                {
                    words[at] = (ushort)ri;
                    words[at + 1] = (ushort)gi;
                    words[at + 2] = (ushort)bi;
                    if (alpha >= 0)
                    {
                        words[at + 3] = (ushort)alpha;
                    }
                }
                else
                {
                    target[at] = (byte)ri;
                    target[at + 1] = (byte)gi;
                    target[at + 2] = (byte)bi;
                    if (alpha >= 0)
                    {
                        target[at + 3] = (byte)alpha;
                    }
                }
            }
        }
        finally
        {
            if (terms is not null)
            {
                System.Buffers.ArrayPool<float>.Shared.Return(terms);
            }
        }
    }

    /// <summary>
    /// <see cref="WriteCoefficients"/> without alpha, eight pixels at a time: the lookups worked out as the tables were, then the
    /// same single-precision operations in the same order, so every sample is the scalar one.
    /// </summary>
    private void WriteVector(int y, int x, int width, Span<byte> target, float[]? terms, int first, int count)
    {
        HeifPlanes c = _color;
        int ssx = c.SubsamplingX;
        ushort[] yPlane = c.Samples[0], uPlane = c.Samples[1], vPlane = c.Samples[2];
        int yRow = (y * c.Strides[0]) + x, uvRow = ((y >> c.SubsamplingY) * c.Strides[1]) + x;
        float[] cbs = System.Buffers.ArrayPool<float>.Shared.Rent(width), crs = System.Buffers.ArrayPool<float>.Shared.Rent(width);
        try
        {
            if (terms is not null)
            {
                // The bilinear chroma of each pixel, summed as WriteCoefficients sums it.
                for (int i = 0; i < width; i++)
                {
                    int px = x + i;
                    int k = (px >> ssx) - first;
                    int adj = px == 0 || (px == c.Width - 1 && (px & 1) != 0) || ssx == 0 ? 0 : (px & 1) != 0 ? 1 : -1;
                    cbs[i] = ((terms[k] + terms[count + k + adj]) + terms[(2 * count) + k]) + terms[(3 * count) + k + adj];
                    crs[i] = ((terms[(4 * count) + k] + terms[(5 * count) + k + adj]) + terms[(6 * count) + k]) + terms[(7 * count) + k + adj];
                }
            }

            Vector256<int> max = Vector256.Create(_yuvMax);
            Vector256<float> biasY = Vector256.Create(_biasY), rangeY = Vector256.Create(_rangeY);
            Vector256<float> biasUv = Vector256.Create(_biasUv), rangeUv = Vector256.Create(_rangeUv);
            Vector256<float> cR = Vector256.Create(2 * (1 - _kr)), cB = Vector256.Create(2 * (1 - _kb));
            Vector256<float> kR = Vector256.Create(_kr * (1 - _kr)), kB = Vector256.Create(_kb * (1 - _kb)), kg = Vector256.Create(_kg);
            Vector256<float> two = Vector256.Create(2f), half = Vector256.Create(0.5f), one = Vector256<float>.One;
            Vector256<float> maxF = Vector256.Create((float)_rgbMax);
            ref ushort ys = ref MemoryMarshal.GetArrayDataReference(yPlane);
            ref ushort us = ref MemoryMarshal.GetArrayDataReference(uPlane);
            ref ushort vs = ref MemoryMarshal.GetArrayDataReference(vPlane);
            ref float cbRef = ref MemoryMarshal.GetArrayDataReference(cbs);
            ref float crRef = ref MemoryMarshal.GetArrayDataReference(crs);
            bool wide = OutputDepth > 8;
            Span<ushort> words = wide ? MemoryMarshal.Cast<byte, ushort>(target) : default;
            Span<int> rgb = stackalloc int[24];
            ref int rgbRef = ref MemoryMarshal.GetReference(rgb);
            for (int i = 0; ; i += 8)
            {
                // The last eight pixels overlap the ones before them rather than leave a remainder.
                i = Math.Min(i, width - 8);
                Vector256<float> yf = (Vector256.ConvertToSingle(Sample(ref ys, yRow + i, max)) - biasY) / rangeY;
                Vector256<float> cb, cr;
                if (terms is not null)
                {
                    cb = Vector256.LoadUnsafe(ref cbRef, (nuint)i);
                    cr = Vector256.LoadUnsafe(ref crRef, (nuint)i);
                }
                else
                {
                    cb = (Vector256.ConvertToSingle(Sample(ref us, uvRow + i, max)) - biasUv) / rangeUv;
                    cr = (Vector256.ConvertToSingle(Sample(ref vs, uvRow + i, max)) - biasUv) / rangeUv;
                }

                Vector256<float> r = Vector256.Min(Vector256.Max(yf + (cR * cr), Vector256<float>.Zero), one);
                Vector256<float> b = Vector256.Min(Vector256.Max(yf + (cB * cb), Vector256<float>.Zero), one);
                Vector256<float> g = Vector256.Min(Vector256.Max(yf - ((two * ((kR * cr) + (kB * cb))) / kg), Vector256<float>.Zero), one);
                Vector256.ConvertToInt32(half + (r * maxF)).StoreUnsafe(ref rgbRef, 0);
                Vector256.ConvertToInt32(half + (g * maxF)).StoreUnsafe(ref rgbRef, 8);
                Vector256.ConvertToInt32(half + (b * maxF)).StoreUnsafe(ref rgbRef, 16);
                for (int k = 0, at = i * 3; k < 8; k++, at += 3)
                {
                    if (wide)
                    {
                        words[at] = (ushort)rgb[k];
                        words[at + 1] = (ushort)rgb[8 + k];
                        words[at + 2] = (ushort)rgb[16 + k];
                    }
                    else
                    {
                        target[at] = (byte)rgb[k];
                        target[at + 1] = (byte)rgb[8 + k];
                        target[at + 2] = (byte)rgb[16 + k];
                    }
                }

                if (i == width - 8)
                {
                    break;
                }
            }
        }
        finally
        {
            System.Buffers.ArrayPool<float>.Shared.Return(cbs);
            System.Buffers.ArrayPool<float>.Shared.Return(crs);
        }

        // Eight samples widened to 32 bits, at most the largest code value (as the scalar path's lookups clamp them).
        static Vector256<int> Sample(ref ushort plane, int at, Vector256<int> max) =>
            Vector256.Min(Vector256.WidenLower(Vector128.LoadUnsafe(ref plane, (nuint)at).ToVector256()).AsInt32(), max);
    }

    /// <summary>Converts <paramref name="width"/> pixels of row <paramref name="y"/> from column <paramref name="x"/> into <paramref name="row"/> (<see cref="Channels"/> samples each).</summary>
    public void Row(int y, int x, int width, Span<int> row)
    {
        HeifPlanes c = _color;
        bool mono = c.Monochrome;
        int ssx = c.SubsamplingX, ssy = c.SubsamplingY;
        ushort[] yPlane = c.Samples[0];
        int yRow = y * c.Strides[0];
        ushort[]? uPlane = mono ? null : c.Samples[1], vPlane = mono ? null : c.Samples[2];
        int uvStride = mono ? 0 : c.Strides[1];
        int uvJ = y >> ssy;
        bool subsampled = !mono && (ssx | ssy) != 0;
        int adjRow = 0;
        if (subsampled && !(y == 0 || (y == c.Height - 1 && (y & 1) != 0) || ssy == 0))
        {
            adjRow = (y & 1) != 0 ? uvStride : -uvStride;
        }

        ushort[]? aPlane = _alpha?.Samples[0];
        int aRow = _alpha is null ? 0 : y * _alpha.Strides[0];
        float rgbMaxF = _rgbMax;
        int channels = Channels;
        for (int i = 0, at = 0; i < width; i++, at += channels)
        {
            int px = x + i;
            int unormY = Math.Min(yPlane[yRow + px], _yuvMax);
            float yf = _tableY[unormY];
            float cb = 0.5f, cr = 0.5f;
            if (!mono)
            {
                int uvI = px >> ssx;
                int center = (uvJ * uvStride) + uvI;
                if (!subsampled)
                {
                    cb = _tableUv[Math.Min(uPlane![center], _yuvMax)];
                    cr = _tableUv[Math.Min(vPlane![center], _yuvMax)];
                }
                else
                {
                    int adjCol = px == 0 || (px == c.Width - 1 && (px & 1) != 0) || ssx == 0 ? 0 : (px & 1) != 0 ? 1 : -1;
                    cb = Bilinear(uPlane!, center, adjCol, adjRow);
                    cr = Bilinear(vPlane!, center, adjCol, adjRow);
                }
            }

            float rc, gc = 0f, bc = 0f;
            if (mono)
            {
                rc = Math.Clamp(yf, 0f, 1f);
            }
            else
            {
                float r, g, b;
                switch (_mode)
                {
                    case Mode.Identity:
                        g = yf;
                        b = cb;
                        r = cr;
                        break;
                    case Mode.YCgCo:
                        float t = yf - cb;
                        g = yf + cb;
                        b = t - cr;
                        r = t + cr;
                        break;
                    case Mode.YCgCoRe:
                    case Mode.YCgCoRo:
                        (r, g, b) = Reversible(unormY, cb, cr);
                        break;
                    default:
                        r = yf + ((2 * (1 - _kr)) * cr);
                        b = yf + ((2 * (1 - _kb)) * cb);
                        g = yf - ((2 * ((_kr * (1 - _kr) * cr) + (_kb * (1 - _kb) * cb))) / _kg);
                        break;
                }

                rc = Math.Clamp(r, 0f, 1f);
                gc = Math.Clamp(g, 0f, 1f);
                bc = Math.Clamp(b, 0f, 1f);
            }

            if (_floatUnmultiply)
            {
                int unormA = Math.Min(aPlane![aRow + px], _yuvMax);
                float ac = Math.Clamp(unormA / (float)_yuvMax, 0f, 1f);
                if (ac == 0f)
                {
                    (rc, gc, bc) = (0f, 0f, 0f);
                }
                else if (ac < 1f)
                {
                    rc = Math.Min(rc / ac, 1f);
                    gc = Math.Min(gc / ac, 1f);
                    bc = Math.Min(bc / ac, 1f);
                }
            }

            row[at] = (int)(0.5f + (rc * rgbMaxF));
            if (!mono)
            {
                row[at + 1] = (int)(0.5f + (gc * rgbMaxF));
                row[at + 2] = (int)(0.5f + (bc * rgbMaxF));
            }

            if (aPlane is not null)
            {
                int alpha = Alpha(aPlane[aRow + px]);
                row[at + channels - 1] = alpha;
                if (_storedUnmultiply)
                {
                    Unmultiply(row.Slice(at, channels - 1), alpha);
                }
            }
        }
    }

    /// <summary>Gets Kr, Kg and Kb for a matrix code, as libavif's avifCalcYUVCoefficients: BT.601's for codes it has no entry for.</summary>
    internal static (float Kr, float Kg, float Kb) Coefficients(int matrix, int primaries)
    {
        (float kr, float kb) = matrix switch
        {
            1 => (0.2126f, 0.0722f),
            4 => (0.30f, 0.11f),
            5 or 6 => (0.299f, 0.114f),
            7 => (0.212f, 0.087f),
            9 or 10 => (0.2627f, 0.0593f),
            12 or 13 => FromPrimaries(primaries),
            _ => (0.299f, 0.114f),
        };
        return (kr, 1.0f - kr - kb, kb);
    }

    /// <summary>Derives Kr and Kb from the primaries (H.273 equations 32 to 37), with BT.709's for an unknown code.</summary>
    private static (float Kr, float Kb) FromPrimaries(int code)
    {
        ReadOnlySpan<float> p = code switch
        {
            4 => [0.67f, 0.33f, 0.21f, 0.71f, 0.14f, 0.08f, 0.310f, 0.316f],
            5 => [0.64f, 0.33f, 0.29f, 0.60f, 0.15f, 0.06f, 0.3127f, 0.3290f],
            6 or 7 => [0.630f, 0.340f, 0.310f, 0.595f, 0.155f, 0.070f, 0.3127f, 0.3290f],
            8 => [0.681f, 0.319f, 0.243f, 0.692f, 0.145f, 0.049f, 0.310f, 0.316f],
            9 => [0.708f, 0.292f, 0.170f, 0.797f, 0.131f, 0.046f, 0.3127f, 0.3290f],
            10 => [1.0f, 0.0f, 0.0f, 1.0f, 0.0f, 0.0f, 0.3333f, 0.3333f],
            11 => [0.680f, 0.320f, 0.265f, 0.690f, 0.150f, 0.060f, 0.314f, 0.351f],
            12 => [0.680f, 0.320f, 0.265f, 0.690f, 0.150f, 0.060f, 0.3127f, 0.3290f],
            22 => [0.630f, 0.340f, 0.295f, 0.605f, 0.155f, 0.077f, 0.3127f, 0.3290f],
            _ => [0.64f, 0.33f, 0.3f, 0.6f, 0.15f, 0.06f, 0.3127f, 0.329f],
        };
        float rX = p[0], rY = p[1], gX = p[2], gY = p[3], bX = p[4], bY = p[5], wX = p[6], wY = p[7];
        float rZ = 1.0f - (rX + rY);
        float gZ = 1.0f - (gX + gY);
        float bZ = 1.0f - (bX + bY);
        float wZ = 1.0f - (wX + wY);
        float denominator = wY * ((rX * ((gY * bZ) - (bY * gZ))) + (gX * ((bY * rZ) - (rY * bZ))) + (bX * ((rY * gZ) - (gY * rZ))));
        float kr = (rY * ((wX * ((gY * bZ) - (bY * gZ))) + (wY * ((bX * gZ) - (gX * bZ))) + (wZ * ((gX * bY) - (bX * gY))))) / denominator;
        float kb = (bY * ((wX * ((rY * gZ) - (gY * rZ))) + (wY * ((gX * rZ) - (rX * gZ))) + (wZ * ((rX * gY) - (gX * rY))))) / denominator;
        return (kr, kb);
    }

    private float Bilinear(ushort[] plane, int center, int adjCol, int adjRow)
    {
        float[] t = _tableUv;
        int max = _yuvMax;
        return (t[Math.Min(plane[center], max)] * (9.0f / 16.0f)) + (t[Math.Min(plane[center + adjCol], max)] * (3.0f / 16.0f))
            + (t[Math.Min(plane[center + adjRow], max)] * (3.0f / 16.0f)) + (t[Math.Min(plane[center + adjCol + adjRow], max)] * (1.0f / 16.0f));
    }

    /// <summary>YCgCo-R (H.273 equations 62 to 65): lossless integers, at two (Re) or one (Ro) bit fewer than the planes, scaled to the output depth.</summary>
    private (float R, float G, float B) Reversible(int y, float cb, float cr)
    {
        int bits = _color.BitDepth - (_mode == Mode.YCgCoRe ? 2 : 1);
        int max = (1 << bits) - 1;
        int cg = (int)MathF.Floor((cb * _yuvMax) + 0.5f);
        int co = (int)MathF.Floor((cr * _yuvMax) + 0.5f);
        int t = y - (cg >> 1);
        float g = Math.Clamp(t + cg, 0, max);
        float b = Math.Clamp(t - (co >> 1), 0, max);
        float r = Math.Clamp(b + co, 0, max);
        return (r / max, g / max, b / max);
    }

    /// <summary>Gets an alpha sample at the output depth, rescaled as libavif's avifReformatAlpha does.</summary>
    private int Alpha(int sample)
    {
        int depth = _alpha!.BitDepth;
        if (depth == OutputDepth)
        {
            return sample;
        }

        float alpha = sample / (float)((1 << depth) - 1);
        return Math.Clamp((int)(0.5f + (alpha * _rgbMax)), 0, _rgbMax);
    }

    /// <summary>Undoes premultiplication on stored samples, as libavif's avifRGBImageUnpremultiplyAlpha.</summary>
    private void Unmultiply(Span<int> color, int alpha)
    {
        if (alpha >= _rgbMax)
        {
            return;
        }

        for (int i = 0; i < color.Length; i++)
        {
            color[i] = alpha == 0 ? 0 : Unmultiplied(color[i], alpha);
        }
    }

    /// <summary>Divides a stored sample by its alpha (below the maximum and above 0), rounding as libavif does.</summary>
    private int Unmultiplied(int sample, int alpha)
    {
        float maxF = _rgbMax;
        return (int)Math.Min(MathF.Floor((sample * maxF / alpha) + 0.5f), maxF);
    }
}

/// <summary>Converts decoded HEIF planes to rows of RGB (or gray) samples with alpha, as one reference decoder does.</summary>
internal interface IHeifRgbConverter
{
    /// <summary>Gets the bits of an output sample: 8 or 16.</summary>
    int OutputDepth { get; }

    /// <summary>Gets the samples a pixel has: gray or RGB, then alpha if any.</summary>
    int Channels { get; }

    /// <summary>Gets whether the reference decoder converts this matrix; others are converted with the nearest one it has.</summary>
    bool Supported { get; }

    /// <summary>
    /// Converts <paramref name="width"/> pixels of row <paramref name="y"/> from column <paramref name="x"/> into
    /// <paramref name="target"/>, as interleaved samples of <see cref="OutputDepth"/> bits (bytes, or native-endian 16-bit words).
    /// </summary>
    void Write(int y, int x, int width, Span<byte> target);

    /// <summary>Converts <paramref name="width"/> pixels of row <paramref name="y"/> from column <paramref name="x"/> into <paramref name="row"/> (<see cref="Channels"/> samples each).</summary>
    void Row(int y, int x, int width, Span<int> row);
}

/// <summary>What a conversion to RGB works on.</summary>
/// <param name="Color">The color planes.</param>
/// <param name="Alpha">The alpha planes, the same size, or null.</param>
/// <param name="Premultiplied">Whether the color is premultiplied by the alpha.</param>
/// <param name="CodePoints">The color's H.273 code points and range.</param>
/// <param name="Layer">Whether the planes are a layer of an overlay.</param>
internal readonly record struct HeifConversion(HeifPlanes Color, HeifPlanes? Alpha, bool Premultiplied, Colors.ColorCodePoints CodePoints, bool Layer);
