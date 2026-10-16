namespace Egami.Formats.Heif;

/// <summary>
/// Converts decoded HEIF planes to RGB (or gray) with alpha, as libavif 1.4.2 does without libyuv (reformat.c, alpha.c and colr.c,
/// BSD 2-clause, see THIRD-PARTY-NOTICES), so Load equals avifdec's output sample for sample: single-precision lookup tables
/// for the range, Kr and Kb from H.273 (derived from the primaries for code 12), subsampled chroma upsampled bilinearly with
/// 9/16, 3/16, 3/16 and 1/16 weights, and alpha premultiplication undone in floating point, or on the stored values for 4:4:4.
/// Samples come out at 8 bits for 8-bit planes and at 16 bits otherwise.
/// </summary>
internal sealed class HeifRgbConverter
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
    internal int OutputDepth { get; }

    /// <summary>Gets the samples a pixel has: gray or RGB, then alpha if any.</summary>
    internal int Channels { get; }

    /// <summary>
    /// Gets whether libavif converts this matrix; it refuses the reserved and constant-luminance codes (and YCgCo at limited range),
    /// which are converted here with the nearest matrix it has.
    /// </summary>
    internal bool Supported { get; }

    /// <summary>Converts <paramref name="width"/> pixels of row <paramref name="y"/> from column <paramref name="x"/> into <paramref name="row"/> (<see cref="Channels"/> samples each).</summary>
    internal void Row(int y, int x, int width, Span<int> row)
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

        float maxF = _rgbMax;
        for (int i = 0; i < color.Length; i++)
        {
            color[i] = alpha == 0 ? 0 : (int)Math.Min(MathF.Floor((color[i] * maxF / alpha) + 0.5f), maxF);
        }
    }
}
