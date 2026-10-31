using Egami.Colors;

namespace Egami.Analysis;

/// <summary>
/// Butteraugli, libjxl's psychovisual distance (butteraugli.cc, translated; see THIRD-PARTY-NOTICES): both images go through
/// an opsin model of the retina into an XYB-like space, are split into four frequency bands, and each band's difference is
/// weighed (a line-shaped "Malta" filter for edges, squared differences for the rest) and masked by the activity around it.
/// The result is a map of distances, one a pixel, at full and at half resolution mixed; the score is the map's largest value
/// and the 3-norm its p-norm. Images are linear sRGB planes, 1.0 at the intensity target (80 nits for SDR).
/// </summary>
internal sealed class Butteraugli
{
    private const double WMfMalta = 37.0819870399;
    private const double Norm1Mf = 130262059.556;
    private const double WMfMaltaX = 8246.75321353;
    private const double Norm1MfX = 1009002.70582;
    private const double WHfMalta = 18.7237414387;
    private const double Norm1Hf = 4498534.45232;
    private const double WHfMaltaX = 6923.99476109;
    private const double Norm1HfX = 8051.15833247;
    private const double WUhfMalta = 1.10039032555;
    private const double Norm1Uhf = 71.7800275169;
    private const double WUhfMaltaX = 173.5;
    private const double Norm1UhfX = 5.0;

    private static readonly double[] WMul = [400.0, 1.50815703118, 0, 2150.0, 10.6195433239, 16.2176043152, 29.2353797994, 0.844626970982, 0.703646627719];

    // log(80) / log(255): the good-quality threshold scaled to the 80-nit intensity target.
    private const float IntensityTargetNormalizationHack = 0.79079917404f;
    private const float InternalGoodQualityThreshold = 17.83f * IntensityTargetNormalizationHack;
    private const float GlobalScale = (float)(1.0 / InternalGoodQualityThreshold);

    private readonly int _width;
    private readonly int _height;
    private readonly float _intensityTarget;
    private readonly Psycho? _reference;
    private readonly Butteraugli? _half;

    /// <summary>The frequency bands of an image: ultra-high and high for X and Y, middle and low for X, Y and B.</summary>
    private sealed class Psycho
    {
        internal float[][] Uhf = new float[2][];
        internal float[][] Hf = new float[2][];
        internal float[][] Mf = new float[3][];
        internal float[][] Lf = new float[3][];
    }

    /// <summary>Prepares the comparison against <paramref name="reference"/>: three linear planes of <paramref name="width"/> × <paramref name="height"/>.</summary>
    internal Butteraugli(float[][] reference, int width, int height, float intensityTarget = 80f)
        : this(reference, width, height, intensityTarget, true)
    {
    }

    private Butteraugli(float[][] reference, int width, int height, float intensityTarget, bool withHalf)
    {
        _width = width;
        _height = height;
        _intensityTarget = intensityTarget;
        if (width < 8 || height < 8)
        {
            return;
        }

        _reference = Separate(Opsin(reference, width, height, intensityTarget), width, height);

        // libjxl builds the half-size comparator the same way (and its own half, which Diffmap never uses).
        if (withHalf)
        {
            (float[][] half, int hw, int hh) = SubSample2x(reference, width, height);
            _half = new Butteraugli(half, hw, hh, intensityTarget, false);
        }
    }

    /// <summary>
    /// Gives the distance map between two linear images of any size (images under 8 pixels either way are padded to 8, as
    /// libjxl does), one value a pixel.
    /// </summary>
    internal static float[] Diffmap(float[][] reference, float[][] distorted, int width, int height, float intensityTarget = 80f)
    {
        if (width >= 8 && height >= 8)
        {
            return new Butteraugli(reference, width, height, intensityTarget).Diffmap(distorted);
        }

        // ButteraugliDiffmapSmall: the image repeats its edge out to 8 × 8, centered.
        int xBorder = width < 8 ? (8 - width) / 2 : 0;
        int yBorder = height < 8 ? (8 - height) / 2 : 0;
        int sw = Math.Max(8, width), sh = Math.Max(8, height);
        float[][] Scale(float[][] planes)
        {
            float[][] scaled = new float[3][];
            for (int c = 0; c < 3; c++)
            {
                scaled[c] = new float[sw * sh];
                for (int y = 0; y < sh; y++)
                {
                    int y2 = Math.Min(height - 1, y > yBorder ? y - yBorder : 0);
                    for (int x = 0; x < sw; x++)
                    {
                        int x2 = Math.Min(width - 1, x > xBorder ? x - xBorder : 0);
                        scaled[c][(y * sw) + x] = planes[c][(y2 * width) + x2];
                    }
                }
            }

            return scaled;
        }

        float[] map = new Butteraugli(Scale(reference), sw, sh, intensityTarget).Diffmap(Scale(distorted));
        float[] result = new float[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                result[(y * width) + x] = map[((y + yBorder) * sw) + x + xBorder];
            }
        }

        return result;
    }

    /// <summary>Gives the distance map from the reference to <paramref name="distorted"/>, three linear planes of the same size.</summary>
    internal float[] Diffmap(float[][] distorted)
    {
        int w = _width, h = _height;
        if (_reference is null)
        {
            return new float[w * h];
        }

        float[] result = DiffmapPsycho(Separate(Opsin(distorted, w, h, _intensityTarget), w, h));
        if (_half is { _reference: not null } half)
        {
            (float[][] sub, _, _) = SubSample2x(distorted, w, h);
            float[] subResult = half.DiffmapPsycho(Separate(Opsin(sub, half._width, half._height, _intensityTarget), half._width, half._height));

            // AddSupersampled2x: fewer errors show in the averaged image, so the full-size map gives way a little.
            const double Mixing = 0.3;
            const double Weight = 0.5;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w) + x;
                    result[i] = (float)(result[i] * (1.0 - (Mixing * Weight)));
                    result[i] = (float)(result[i] + (Weight * subResult[((y / 2) * half._width) + (x / 2)]));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Compares a region of two rasters (JxlButteraugliComparator with ComputeScore): the colors made linear, and with alpha
    /// blended over black and over white, the larger distance counting. Gives the distance and the 3-norm.
    /// </summary>
    internal static (double Distance, double Norm) Compare(Raster a, Raster b, Rect region, int frame, bool withAlpha)
    {
        int w = region.Width, h = region.Height;
        (float[][] ca, float[] alphaA) = Linear(a, region, frame);
        (float[][] cb, float[] alphaB) = Linear(b, region, frame);
        if (!withAlpha)
        {
            float[] map = new Butteraugli(ca, w, h).Diffmap(cb);
            return (Score(map), PNorm(map));
        }

        float[] maps = new float[w * h];
        float distance = 0;
        foreach (float background in (ReadOnlySpan<float>)[0f, 1f])
        {
            float[] m = new Butteraugli(Blend(ca, alphaA, background), w, h).Diffmap(Blend(cb, alphaB, background));
            distance = Math.Max(distance, Score(m));
            for (int i = 0; i < maps.Length; i++)
            {
                maps[i] = Math.Max(maps[i], m[i]);
            }
        }

        return (distance, PNorm(maps));
    }

    /// <summary>Reads a region as three linear planes and the alpha plane.</summary>
    private static (float[][] Color, float[] Alpha) Linear(Raster raster, Rect region, int frame)
    {
        int w = region.Width, h = region.Height;
        TransferFunction transfer = raster.Metadata.ColorSpace == Metadata.ColorSpaceHint.Linear ? TransferFunction.Linear : raster.Metadata.GetColorProfile().Transfer ?? TransferFunction.Srgb;
        float[][] color = [new float[w * h], new float[w * h], new float[w * h]];
        float[] alpha = new float[w * h];
        RgbaD[] row = new RgbaD[w];
        using RowReader reader = new(raster, frame, w);
        for (int y = 0; y < h; y++)
        {
            reader.Read(region.Y + y, region.X, row);
            for (int x = 0; x < w; x++)
            {
                int i = (y * w) + x;
                color[0][i] = (float)transfer.ToLinear(row[x].R);
                color[1][i] = (float)transfer.ToLinear(row[x].G);
                color[2][i] = (float)transfer.ToLinear(row[x].B);
                alpha[i] = (float)row[x].A;
            }
        }

        return (color, alpha);
    }

    /// <summary>AlphaBlend: blends over a gray background in sRGB-encoded values, giving linear ones.</summary>
    private static float[][] Blend(float[][] color, float[] alpha, float backgroundLinear)
    {
        static double ToSrgb(double v) => v <= 0 ? 0 : v >= 1 ? 1 : v <= 0.0031308 ? v * 12.92 : (Math.Pow(v, 1.0 / 2.4) * 1.055) - 0.055;
        static double FromSrgb(double v) => v <= 0 ? 0 : v <= 0.04045 ? v / 12.92 : v >= 1 ? 1 : Math.Pow((v + 0.055) / 1.055, 2.4);
        float background = (float)ToSrgb(backgroundLinear);
        float[][] result = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            float[] o = result[c] = new float[alpha.Length];
            for (int i = 0; i < o.Length; i++)
            {
                float al = alpha[i];
                o[i] = al <= 0f ? backgroundLinear : al >= 1f ? color[c][i] : (float)FromSrgb((al * (float)ToSrgb(color[c][i])) + ((1.0f - al) * background));
            }
        }

        return result;
    }

    /// <summary>Gets the score of a distance map: its largest value.</summary>
    internal static float Score(ReadOnlySpan<float> map)
    {
        float max = 0;
        foreach (float v in map)
        {
            max = Math.Max(max, v);
        }

        return max;
    }

    /// <summary>Gets a distance map's <paramref name="p"/>-norm (ComputeDistanceP): the mean of its p-, 2p- and 4p-norms.</summary>
    internal static double PNorm(ReadOnlySpan<float> map, double p = 3)
    {
        if (map.Length == 0)
        {
            return 0;
        }

        double onePerPixels = 1.0 / map.Length;
        Span<double> sums = stackalloc double[3];
        sums.Clear();
        foreach (float v in map)
        {
            double d = p == 3 ? (double)v * v * v : Math.Pow(v, p);
            sums[0] += d;
            d *= d;
            sums[1] += d;
            d *= d;
            sums[2] += d;
        }

        double result = 0;
        for (int i = 0; i < 3; i++)
        {
            result += Math.Pow(onePerPixels * sums[i], 1.0 / (p * (1 << i)));
        }

        return result / 3.0;
    }

    /// <summary>Halves an image (SubSample2x): each pixel the mean of four, an odd last row or column of two.</summary>
    private static (float[][] Planes, int Width, int Height) SubSample2x(float[][] planes, int width, int height)
    {
        int xs = (width + 1) / 2, ys = (height + 1) / 2;
        float[][] result = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            float[] o = result[c] = new float[xs * ys];
            float[] p = planes[c];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    o[((y / 2) * xs) + (x / 2)] += 0.25f * p[(y * width) + x];
                }
            }

            if ((width & 1) != 0)
            {
                for (int y = 0; y < ys; y++)
                {
                    o[(y * xs) + xs - 1] *= 2.0f;
                }
            }

            if ((height & 1) != 0)
            {
                for (int x = 0; x < xs; x++)
                {
                    o[((ys - 1) * xs) + x] *= 2.0f;
                }
            }
        }

        return (result, xs, ys);
    }

    // ---- Blur ----

    /// <summary>ComputeKernel: a Gaussian truncated at 2.25 σ.</summary>
    private static float[] Kernel(float sigma)
    {
        const float M = 2.25f;
        double scaler = -1.0 / (2.0 * sigma * sigma);
        int diff = Math.Max(1, (int)(M * Math.Abs(sigma)));
        float[] kernel = new float[(2 * diff) + 1];
        for (int i = -diff; i <= diff; i++)
        {
            kernel[i + diff] = (float)Math.Exp(scaler * i * i);
        }

        return kernel;
    }

    /// <summary>
    /// Blurs a plane (Blur): five-tap kernels as one separable pass with mirrored edges, the others as two passes that each
    /// filter across and transpose, renormalizing the weights at the edges.
    /// </summary>
    private static void Blur(float[] input, int width, int height, float sigma, float[] output)
    {
        float[] kernel = Kernel(sigma);
        if (kernel.Length == 5 && !ReferenceEquals(input, output))
        {
            float sum = 0;
            foreach (float k in kernel)
            {
                sum += k;
            }

            float scale = 1.0f / sum;
            Separable5(input, width, height, kernel[2] * scale, kernel[1] * scale, kernel[0] * scale, output);
            return;
        }

        float[] transposed = new float[width * height];
        ConvolveTranspose(input, width, height, kernel, transposed);
        ConvolveTranspose(transposed, height, width, kernel, output);
    }

    private static int Mirror(int x, int size)
    {
        while ((uint)x >= (uint)size)
        {
            x = x < 0 ? -x - 1 : (2 * size) - 1 - x;
        }

        return x;
    }

    /// <summary>Separable5: a symmetric 5 × 5 filter, the same weights down and across, with mirrored edges.</summary>
    private static void Separable5(float[] input, int width, int height, float w0, float w1, float w2, float[] output)
    {
        Parallel.For(0, height, y =>
        {
            int t2 = Mirror(y - 2, height) * width, t1 = Mirror(y - 1, height) * width, m = y * width;
            int b1 = Mirror(y + 1, height) * width, b2 = Mirror(y + 2, height) * width;
            for (int x = 0; x < width; x++)
            {
                int l2 = Mirror(x - 2, width), l1 = Mirror(x - 1, width), r1 = Mirror(x + 1, width), r2 = Mirror(x + 2, width);
                float Horz(int row) => MathF.FusedMultiplyAdd(input[row + l2] + input[row + r2], w2, MathF.FusedMultiplyAdd(input[row + l1] + input[row + r1], w1, input[row + x] * w0));
                float conv0 = Horz(m) * w0;
                float conv1 = MathF.FusedMultiplyAdd(Horz(t1) + Horz(b1), w1, conv0);
                output[m + x] = MathF.FusedMultiplyAdd(Horz(t2) + Horz(b2), w2, conv1);
            }
        });
    }

    /// <summary>ConvolutionWithTranspose: filters each row across and writes it as a column of <paramref name="output"/> (height × width).</summary>
    private static void ConvolveTranspose(float[] input, int width, int height, float[] kernel, float[] output)
    {
        int len = kernel.Length, offset = len / 2;
        float weight = 0;
        foreach (float k in kernel)
        {
            weight += k;
        }

        float scaleNoBorder = 1.0f / weight;
        int border1 = Math.Min(width, offset);
        int border2 = width > offset ? width - offset : 0;
        float[] scaled = new float[offset + 1];
        for (int i = 0; i <= offset; i++)
        {
            scaled[i] = kernel[i] * scaleNoBorder;
        }

        Parallel.For(0, height, y =>
        {
            int row = y * width;
            Span<float> sums = stackalloc float[4];
            for (int x = border1; x < border2; x++)
            {
                int at = row + x - offset;
                sums.Clear();
                for (int i = 0; i < offset; i++)
                {
                    sums[i & 3] += (input[at + i] + input[at + len - 1 - i]) * scaled[i];
                }

                output[(x * height) + y] = (input[at + offset] * scaled[offset]) + sums[0] + sums[1] + sums[2] + sums[3];
            }
        });

        for (int x = 0; x < border1; x++)
        {
            BorderColumn(input, width, height, kernel, x, output);
        }

        for (int x = Math.Max(border2, border1); x < width; x++)
        {
            BorderColumn(input, width, height, kernel, x, output);
        }
    }

    /// <summary>ConvolveBorderColumn: column <paramref name="x"/> near an edge, with the kernel's weights inside the image only.</summary>
    private static void BorderColumn(float[] input, int width, int height, float[] kernel, int x, float[] output)
    {
        int offset = kernel.Length / 2;
        int minX = x < offset ? 0 : x - offset;
        int maxX = Math.Min(width - 1, x + offset);
        float weight = 0;
        for (int j = minX; j <= maxX; j++)
        {
            weight += kernel[j - x + offset];
        }

        float scale = 1.0f / weight;
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            float sum = 0;
            for (int j = minX; j <= maxX; j++)
            {
                sum += input[row + j] * kernel[j - x + offset];
            }

            output[(x * height) + y] = sum * scale;
        }
    }

    // ---- Opsin dynamics ----

    private const float Mix0 = 0.29956550340058319f, Mix1 = 0.63373087833825936f, Mix2 = 0.077705617820981968f, Mix3 = 1.7557483643287353f;
    private const float Mix4 = 0.22158691104574774f, Mix5 = 0.69391388044116142f, Mix6 = 0.0987313588422f, Mix7 = 1.7557483643287353f;
    private const float Mix8 = 0.02f, Mix9 = 0.02f, Mix10 = 0.20480129041026129f, Mix11 = 12.226454707163354f;

    /// <summary>OpsinAbsorbance: the three cone responses as mixes of R, G and B.</summary>
    private static (float, float, float) Absorbance(float r, float g, float b) => (
        MathF.FusedMultiplyAdd(Mix0, r, MathF.FusedMultiplyAdd(Mix1, g, MathF.FusedMultiplyAdd(Mix2, b, Mix3))),
        MathF.FusedMultiplyAdd(Mix4, r, MathF.FusedMultiplyAdd(Mix5, g, MathF.FusedMultiplyAdd(Mix6, b, Mix7))),
        MathF.FusedMultiplyAdd(Mix8, r, MathF.FusedMultiplyAdd(Mix9, g, MathF.FusedMultiplyAdd(Mix10, b, Mix11))));

    /// <summary>Gamma: a log curve that also works for HDR; negative light is clamped to zero.</summary>
    private static float Gamma(float v)
    {
        const float RetMul = 19.245013259874995f * 0.6931471805599453f;
        const float RetAdd = -23.16046239805755f;
        v = Math.Max(v, 0);
        return MathF.FusedMultiplyAdd(RetMul, Formats.Jxl.JxlFastMath.Log2(v + 9.9710635769299145f), RetAdd);
    }

    /// <summary>
    /// OpsinDynamicsImage: each pixel's cone responses scaled by the sensitivity the blurred neighbourhood sets (the gamma
    /// curve's slope there), then mixed into X (red − green), Y (red + green) and B.
    /// </summary>
    private static float[][] Opsin(float[][] rgb, int width, int height, float intensityTarget)
    {
        float[][] blurred = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            blurred[c] = new float[width * height];
            Blur(rgb[c], width, height, 1.2f, blurred[c]);
        }

        float[][] xyb = [new float[width * height], new float[width * height], new float[width * height]];
        Parallel.For(0, height, y =>
        {
            const float Min = 1e-4f;
            for (int i = y * width, end = i + width; i < end; i++)
            {
                (float p0, float p1, float p2) = Absorbance(blurred[0][i] * intensityTarget, blurred[1][i] * intensityTarget, blurred[2][i] * intensityTarget);
                p0 = Math.Max(Math.Max(p0, Mix3), Min);
                p1 = Math.Max(Math.Max(p1, Mix7), Min);
                p2 = Math.Max(Math.Max(p2, Mix11), Min);
                float s0 = Math.Max(Gamma(p0) / p0, Min);
                float s1 = Math.Max(Gamma(p1) / p1, Min);
                float s2 = Math.Max(Gamma(p2) / p2, Min);
                (float c0, float c1, float c2) = Absorbance(rgb[0][i] * intensityTarget, rgb[1][i] * intensityTarget, rgb[2][i] * intensityTarget);
                c0 = Math.Max(c0 * s0, 1.7557483643287353f);
                c1 = Math.Max(c1 * s1, 1.7557483643287353f);
                c2 = Math.Max(c2 * s2, 12.226454707163354f);
                xyb[0][i] = c0 - c1;
                xyb[1][i] = c0 + c1;
                xyb[2][i] = c2;
            }
        });

        return xyb;
    }

    // ---- Frequency bands ----

    private static float MaximumClamp(float v, float maxVal)
    {
        const float Mul = 0.724216145665f;
        float ifPos = MathF.FusedMultiplyAdd(v - maxVal, Mul, maxVal);
        float ifNeg = MathF.FusedMultiplyAdd(v + maxVal, Mul, -maxVal);
        float posOrV = v >= maxVal ? ifPos : v;
        return v < -maxVal ? ifNeg : posOrV;
    }

    private static float RemoveRangeAroundZero(float w, float x) => x > w ? x - w : x < -w ? x + w : 0;

    private static float AmplifyRangeAroundZero(float w, float x) => x > w ? x + w : x < -w ? x - w : x + x;

    /// <summary>SeparateFrequencies: the low band (blur σ 7.16, in "vals" space), the middle (σ 3.22), the high (σ 1.56) and what is left.</summary>
    private static Psycho Separate(float[][] xyb, int width, int height)
    {
        int n = width * height;
        Psycho ps = new();

        // SeparateLFAndMF.
        for (int c = 0; c < 3; c++)
        {
            ps.Lf[c] = new float[n];
            ps.Mf[c] = new float[n];
            Blur(xyb[c], width, height, 7.15593339443f, ps.Lf[c]);
            for (int i = 0; i < n; i++)
            {
                ps.Mf[c][i] = xyb[c][i] - ps.Lf[c][i];
            }
        }

        // XybLowFreqToVals.
        for (int i = 0; i < n; i++)
        {
            float b = MathF.FusedMultiplyAdd(-0.362267051518f, ps.Lf[1][i], ps.Lf[2][i]);
            ps.Lf[2][i] = b * 49.87984651440f;
            ps.Lf[0][i] *= 33.832837186260f;
            ps.Lf[1][i] *= 14.458268100570f;
        }

        // SeparateMFAndHF.
        const float SigmaHf = 3.22489901262f;
        for (int c = 0; c < 3; c++)
        {
            if (c == 2)
            {
                Blur(ps.Mf[2], width, height, SigmaHf, ps.Mf[2]);
                break;
            }

            ps.Hf[c] = (float[])ps.Mf[c].Clone();
            Blur(ps.Mf[c], width, height, SigmaHf, ps.Mf[c]);
            float[] mf = ps.Mf[c], hf = ps.Hf[c];
            for (int i = 0; i < n; i++)
            {
                hf[i] -= mf[i];
                mf[i] = c == 0 ? RemoveRangeAroundZero(0.29f, mf[i]) : AmplifyRangeAroundZero(0.1f, mf[i]);
            }
        }

        // SuppressXByY: red-green high frequencies count less where intensity changes.
        {
            const float Suppress = 46.0f;
            const float S = 0.653020556257f;
            const float OneMinusS = (float)(1.0 - 0.653020556257);
            float[] hy = ps.Hf[1], hx = ps.Hf[0];
            for (int i = 0; i < n; i++)
            {
                float scaler = MathF.FusedMultiplyAdd(Suppress / MathF.FusedMultiplyAdd(hy[i], hy[i], Suppress), OneMinusS, S);
                hx[i] = scaler * hx[i];
            }
        }

        // SeparateHFAndUHF.
        for (int c = 0; c < 2; c++)
        {
            ps.Uhf[c] = (float[])ps.Hf[c].Clone();
            Blur(ps.Hf[c], width, height, 1.56416327805f, ps.Hf[c]);
            float[] hf = ps.Hf[c], uhf = ps.Uhf[c];
            for (int i = 0; i < n; i++)
            {
                if (c == 0)
                {
                    float h = hf[i];
                    uhf[i] -= h;
                    hf[i] = RemoveRangeAroundZero(1.5f, h);
                    uhf[i] = RemoveRangeAroundZero(0.04f, uhf[i]);
                }
                else
                {
                    float h = MaximumClamp(hf[i], 28.4691806922f);
                    float u = MaximumClamp(uhf[i] - h, 5.19175294647f);
                    uhf[i] = u * 2.69313763794f;
                    h *= 2.155f;
                    hf[i] = AmplifyRangeAroundZero(0.132f, h);
                }
            }
        }

        return ps;
    }

    // ---- Differences ----

    private float[] DiffmapPsycho(Psycho pi1)
    {
        Psycho pi0 = _reference!;
        int w = _width, h = _height, n = w * h;
        const float HfAsymmetry = 1.0f;
        const float XMul = 1.0f;
        float[][] ac = [new float[n], new float[n], new float[n]];
        float[][] dc = [new float[n], new float[n], new float[n]];
        float[] diffs = new float[n];
        Malta(false, pi0.Uhf[1], pi1.Uhf[1], WUhfMalta * HfAsymmetry, WUhfMalta / HfAsymmetry, Norm1Uhf, diffs, ac[1], w, h);
        Malta(false, pi0.Uhf[0], pi1.Uhf[0], WUhfMaltaX * HfAsymmetry, WUhfMaltaX / HfAsymmetry, Norm1UhfX, diffs, ac[0], w, h);
        Malta(true, pi0.Hf[1], pi1.Hf[1], WHfMalta * Math.Sqrt(HfAsymmetry), WHfMalta / Math.Sqrt(HfAsymmetry), Norm1Hf, diffs, ac[1], w, h);
        Malta(true, pi0.Hf[0], pi1.Hf[0], WHfMaltaX * Math.Sqrt(HfAsymmetry), WHfMaltaX / Math.Sqrt(HfAsymmetry), Norm1HfX, diffs, ac[0], w, h);
        Malta(true, pi0.Mf[1], pi1.Mf[1], WMfMalta, WMfMalta, Norm1Mf, diffs, ac[1], w, h);
        Malta(true, pi0.Mf[0], pi1.Mf[0], WMfMaltaX, WMfMaltaX, Norm1MfX, diffs, ac[0], w, h);

        for (int c = 0; c < 3; c++)
        {
            if (c < 2)
            {
                L2DiffAsymmetric(pi0.Hf[c], pi1.Hf[c], (float)(WMul[c] * HfAsymmetry), (float)(WMul[c] / HfAsymmetry), ac[c]);
            }

            L2Diff(pi0.Mf[c], pi1.Mf[c], (float)WMul[3 + c], ac[c], accumulate: true);
            L2Diff(pi0.Lf[c], pi1.Lf[c], (float)WMul[6 + c], dc[c], accumulate: false);
        }

        float[] mask = Mask(pi0, pi1, w, h, ac[1]);
        float[] result = new float[n];
        for (int i = 0; i < n; i++)
        {
            float val = mask[i];
            float maskVal = (float)MaskY(val);
            float dcMaskVal = (float)MaskDcY(val);
            float dc0 = dc[0][i] * XMul, ac0 = ac[0][i] * XMul;
            result[i] = MathF.Sqrt((dc0 * dcMaskVal) + (dc[1][i] * dcMaskVal) + (dc[2][i] * dcMaskVal) + ((ac0 * maskVal) + (ac[1][i] * maskVal) + (ac[2][i] * maskVal)));
        }

        return result;
    }

    /// <summary>L2Diff and SetL2Diff: adds (or sets) the weighted squared difference.</summary>
    private static void L2Diff(float[] i0, float[] i1, float w, float[] diffmap, bool accumulate)
    {
        if (w == 0)
        {
            return;
        }

        for (int i = 0; i < i0.Length; i++)
        {
            float d = i0[i] - i1[i];
            diffmap[i] = accumulate ? MathF.FusedMultiplyAdd(d * d, w, diffmap[i]) : d * d * w;
        }
    }

    /// <summary>L2DiffAsymmetric: the squared difference, plus a penalty when the distorted value drifts out of 0.4 to 1 times the original's.</summary>
    private static void L2DiffAsymmetric(float[] i0, float[] i1, float w0gt1, float w0lt1, float[] diffmap)
    {
        if (w0gt1 == 0 && w0lt1 == 0)
        {
            return;
        }

        float vw0gt1 = (float)(w0gt1 * 0.8), vw0lt1 = (float)(w0lt1 * 0.8);
        for (int i = 0; i < i0.Length; i++)
        {
            float val0 = i0[i], val1 = i1[i];
            float diff = val0 - val1;
            float total = MathF.FusedMultiplyAdd(diff * diff, vw0gt1, diffmap[i]);
            float fabs0 = Math.Abs(val0);
            float tooSmall = 0.4f * fabs0;
            float tooBig = fabs0;
            float v = val0 < 0
                ? val1 > -tooSmall ? val1 + tooSmall : val1 < -tooBig ? -val1 - tooBig : 0
                : val1 < tooSmall ? tooSmall - val1 : val1 > tooBig ? val1 - tooBig : 0;
            diffmap[i] = MathF.FusedMultiplyAdd(vw0lt1, v * v, total);
        }
    }

    /// <summary>
    /// MaltaDiffMap and MaltaDiffMapLF: the scaled difference, with half-open penalties, summed along sixteen short lines
    /// through each pixel; the sum of their squares is added to <paramref name="blockDiffAc"/>.
    /// </summary>
    private static void Malta(bool lowFrequency, float[] lum0, float[] lum1, double w0gt1, double w0lt1, double norm1, float[] diffs, float[] blockDiffAc, int width, int height)
    {
        const double Len = 3.75;
        double mulli = lowFrequency ? 0.611612573796 : 0.39905817637;
        const float Weight0 = 0.5f;
        const float Weight1 = 0.33f;
        double wPre0gt1 = mulli * Math.Sqrt(Weight0 * w0gt1) / ((Len * 2) + 1);
        double wPre0lt1 = mulli * Math.Sqrt(Weight1 * w0lt1) / ((Len * 2) + 1);
        float norm20gt1 = (float)(wPre0gt1 * norm1);
        float norm20lt1 = (float)(wPre0lt1 * norm1);
        float norm1F = (float)norm1;

        // The differences, in an image padded with 4 zeros all round, so every line stays inside.
        const int Pad = 4;
        int stride = width + (2 * Pad);
        float[] padded = new float[stride * (height + (2 * Pad))];
        for (int y = 0; y < height; y++)
        {
            int row = y * width, prow = ((y + Pad) * stride) + Pad;
            for (int x = 0; x < width; x++)
            {
                float r0 = lum0[row + x], r1 = lum1[row + x];
                float absval = 0.5f * (Math.Abs(r0) + Math.Abs(r1));
                float diff = r0 - r1;
                float scaler = norm20gt1 / (norm1F + absval);
                float d = scaler * diff;
                float scaler2 = norm20lt1 / (norm1F + absval);
                double fabs0 = Math.Abs(r0);
                double tooSmall = 0.55 * fabs0;
                double tooBig = 1.05 * fabs0;
                if (r0 < 0)
                {
                    if (r1 > -tooSmall)
                    {
                        d = (float)(d - (scaler2 * (r1 + tooSmall)));
                    }
                    else if (r1 < -tooBig)
                    {
                        d = (float)(d + (scaler2 * (-r1 - tooBig)));
                    }
                }
                else
                {
                    if (r1 < tooSmall)
                    {
                        d = (float)(d + (scaler2 * (tooSmall - r1)));
                    }
                    else if (r1 > tooBig)
                    {
                        d = (float)(d - (scaler2 * (r1 - tooBig)));
                    }
                }

                padded[prow + x] = d;
            }
        }

        Parallel.For(0, height, y =>
        {
            int prow = ((y + Pad) * stride) + Pad;
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                blockDiffAc[row + x] += lowFrequency ? MaltaUnitLf(padded, prow + x, stride) : MaltaUnit(padded, prow + x, stride);
            }
        });
    }

    private static float Sum(float a, float b, float c, float d) => (a + b) + (c + d);

    private static float Sum(float a, float b, float c, float d, float e) => Sum(a, b, c, d + e);

    private static float Sum(float a, float b, float c, float d, float e, float f, float g) => Sum(a, b, c, Sum(d, e, f, g));

    private static float Sum(float a, float b, float c, float d, float e, float f, float g, float h, float i) => (Sum(a, b, c, d) + Sum(e, f, g, h)) + i;

    /// <summary>MaltaUnit for the low frequencies: sixteen lines of five samples, two apart.</summary>
    private static float MaltaUnitLf(float[] p, int d, int xs)
    {
        int xs3 = 3 * xs;
        float center = p[d];
        float s = Sum(p[d - 4], p[d - 2], center, p[d + 2], p[d + 4]);
        float r = s * s;
        static float Add(float sum, float acc) => MathF.FusedMultiplyAdd(sum, sum, acc);
        r = Add(Sum(p[d - xs3 - xs], p[d - xs - xs], center, p[d + xs + xs], p[d + xs3 + xs]), r);
        r = Add(Sum(p[d - xs3 - 3], p[d - xs - xs - 2], center, p[d + xs + xs + 2], p[d + xs3 + 3]), r);
        r = Add(Sum(p[d - xs3 + 3], p[d - xs - xs + 2], center, p[d + xs + xs - 2], p[d + xs3 - 3]), r);
        r = Add(Sum(p[d - xs3 - xs + 1], p[d - xs - xs + 1], center, p[d + xs + xs - 1], p[d + xs3 + xs - 1]), r);
        r = Add(Sum(p[d - xs3 - xs - 1], p[d - xs - xs - 1], center, p[d + xs + xs + 1], p[d + xs3 + xs + 1]), r);
        r = Add(Sum(p[d - 4 - xs], p[d - 2 - xs], center, p[d + 2 + xs], p[d + 4 + xs]), r);
        r = Add(Sum(p[d - 4 + xs], p[d - 2 + xs], center, p[d + 2 - xs], p[d + 4 - xs]), r);
        r = Add(Sum(p[d - xs3 - 2], p[d - xs - xs - 1], center, p[d + xs + xs + 1], p[d + xs3 + 2]), r);
        r = Add(Sum(p[d - xs3 + 2], p[d - xs - xs + 1], center, p[d + xs + xs - 1], p[d + xs3 - 2]), r);
        r = Add(Sum(p[d - xs - xs - 3], p[d - xs - 2], center, p[d + xs + 2], p[d + xs + xs + 3]), r);
        r = Add(Sum(p[d - xs - xs + 3], p[d - xs + 2], center, p[d + xs - 2], p[d + xs + xs - 3]), r);
        r = Add(Sum(p[d + xs + xs - 4], p[d + xs - 2], center, p[d - xs + 2], p[d - xs - xs + 4]), r);
        r = Add(Sum(p[d - xs - xs - 4], p[d - xs - 2], center, p[d + xs + 2], p[d + xs + xs + 4]), r);
        r = Add(Sum(p[d - xs3 - xs - 2], p[d - xs - xs - 1], center, p[d + xs + xs + 1], p[d + xs3 + xs + 2]), r);
        r = Add(Sum(p[d - xs3 - xs + 2], p[d - xs - xs + 1], center, p[d + xs + xs - 1], p[d + xs3 + xs - 2]), r);
        return r;
    }

    /// <summary>MaltaUnit: sixteen lines of seven or nine neighbouring samples.</summary>
    private static float MaltaUnit(float[] p, int d, int xs)
    {
        int xs3 = 3 * xs;
        float center = p[d];
        float s = Sum(p[d - 4], p[d - 3], p[d - 2], p[d - 1], center, p[d + 1], p[d + 2], p[d + 3], p[d + 4]);
        float r = s * s;
        static float Add(float sum, float acc) => MathF.FusedMultiplyAdd(sum, sum, acc);
        r = Add(Sum(p[d - xs3 - xs], p[d - xs3], p[d - xs - xs], p[d - xs], center, p[d + xs], p[d + xs + xs], p[d + xs3], p[d + xs3 + xs]), r);
        r = Add(Sum(p[d - xs3 - 3], p[d - xs - xs - 2], p[d - xs - 1], center, p[d + xs + 1], p[d + xs + xs + 2], p[d + xs3 + 3]), r);
        r = Add(Sum(p[d - xs3 + 3], p[d - xs - xs + 2], p[d - xs + 1], center, p[d + xs - 1], p[d + xs + xs - 2], p[d + xs3 - 3]), r);
        r = Add(Sum(p[d - xs3 - xs + 1], p[d - xs3 + 1], p[d - xs - xs + 1], p[d - xs], center, p[d + xs], p[d + xs + xs - 1], p[d + xs3 - 1], p[d + xs3 + xs - 1]), r);
        r = Add(Sum(p[d - xs3 - xs - 1], p[d - xs3 - 1], p[d - xs - xs - 1], p[d - xs], center, p[d + xs], p[d + xs + xs + 1], p[d + xs3 + 1], p[d + xs3 + xs + 1]), r);
        r = Add(Sum(p[d - 4 - xs], p[d - 3 - xs], p[d - 2 - xs], p[d - 1], center, p[d + 1], p[d + 2 + xs], p[d + 3 + xs], p[d + 4 + xs]), r);
        r = Add(Sum(p[d - 4 + xs], p[d - 3 + xs], p[d - 2 + xs], p[d - 1], center, p[d + 1], p[d + 2 - xs], p[d + 3 - xs], p[d + 4 - xs]), r);
        r = Add(Sum(p[d - xs3 - 2], p[d - xs - xs - 1], p[d - xs - 1], center, p[d + xs + 1], p[d + xs + xs + 1], p[d + xs3 + 2]), r);
        r = Add(Sum(p[d - xs3 + 2], p[d - xs - xs + 1], p[d - xs + 1], center, p[d + xs - 1], p[d + xs + xs - 1], p[d + xs3 - 2]), r);
        r = Add(Sum(p[d - xs - xs - 3], p[d - xs - 2], p[d - xs - 1], center, p[d + xs + 1], p[d + xs + 2], p[d + xs + xs + 3]), r);
        r = Add(Sum(p[d - xs - xs + 3], p[d - xs + 2], p[d - xs + 1], center, p[d + xs - 1], p[d + xs - 2], p[d + xs + xs - 3]), r);
        r = Add(Sum(p[d + xs - 4], p[d + xs - 3], p[d + xs - 2], p[d - 1], center, p[d + 1], p[d - xs + 2], p[d - xs + 3], p[d - xs + 4]), r);
        r = Add(Sum(p[d - xs - 4], p[d - xs - 3], p[d - xs - 2], p[d - 1], center, p[d + 1], p[d + xs + 2], p[d + xs + 3], p[d + xs + 4]), r);
        r = Add(Sum(p[d - xs3 - xs - 1], p[d - xs3 - 1], p[d - xs - xs - 1], p[d - xs], center, p[d + xs], p[d + xs + xs + 1], p[d + xs3 + 1], p[d + xs3 + xs + 1]), r);
        r = Add(Sum(p[d - xs3 - xs + 1], p[d - xs3 + 1], p[d - xs - xs + 1], p[d - xs], center, p[d + xs], p[d + xs + xs - 1], p[d + xs3 - 1], p[d + xs3 + xs - 1]), r);
        return r;
    }

    // ---- Masking ----

    /// <summary>
    /// MaskPsychoImage and Mask: the X and Y high-frequency activity of each image, compressed, blurred and eroded toward its
    /// smooth neighbours; the reference's is the mask, and the squared difference of the two blurred ones adds to <paramref name="diffAc"/>.
    /// </summary>
    private static float[] Mask(Psycho pi0, Psycho pi1, int width, int height, float[] diffAc)
    {
        int n = width * height;
        const float Mul = 6.19424080439f;
        const float Bias = 12.61050594197f;
        const float Radius = 2.7f;
        float[] Precompute(Psycho ps)
        {
            float[] result = new float[n];
            float bias = Mul * Bias;
            float sqrtBias = MathF.Sqrt(bias);
            for (int i = 0; i < n; i++)
            {
                // CombineChannelsForMasking.
                float xdiff = (ps.Uhf[0][i] + ps.Hf[0][i]) * 2.5f;
                float ydiff = (ps.Uhf[1][i] * 0.4f) + (ps.Hf[1][i] * 0.4f);
                float m = MathF.Sqrt((xdiff * xdiff) + (ydiff * ydiff));

                // DiffPrecompute.
                result[i] = MathF.Sqrt((Mul * Math.Abs(m)) + bias) - sqrtBias;
            }

            return result;
        }

        float[] diff0 = Precompute(pi0), diff1 = Precompute(pi1);
        float[] blurred0 = new float[n], blurred1 = new float[n];
        Blur(diff0, width, height, Radius, blurred0);
        FuzzyErosion(blurred0, width, height, diff0);
        Blur(diff1, width, height, Radius, blurred1);
        for (int i = 0; i < n; i++)
        {
            const float MaskToErrorMul = 10.0f;
            float diff = blurred0[i] - blurred1[i];
            diffAc[i] += MaskToErrorMul * diff * diff;
        }

        return diff0;
    }

    /// <summary>FuzzyErosion: a pixel's value mixed with the two smallest of its eight neighbours three pixels away.</summary>
    private static void FuzzyErosion(float[] from, int width, int height, float[] to)
    {
        const int Step = 3;
        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                float min0 = from[(y * width) + x];
                float min1 = 2 * min0;
                float min2 = min1;
                void Store(float v)
                {
                    if (v < min2)
                    {
                        if (v < min0)
                        {
                            min2 = min1;
                            min1 = min0;
                            min0 = v;
                        }
                        else if (v < min1)
                        {
                            min2 = min1;
                            min1 = v;
                        }
                        else
                        {
                            min2 = v;
                        }
                    }
                }

                if (x >= Step)
                {
                    Store(from[(y * width) + x - Step]);
                    if (y >= Step)
                    {
                        Store(from[((y - Step) * width) + x - Step]);
                    }

                    if (y < height - Step)
                    {
                        Store(from[((y + Step) * width) + x - Step]);
                    }
                }

                if (x < width - Step)
                {
                    Store(from[(y * width) + x + Step]);
                    if (y >= Step)
                    {
                        Store(from[((y - Step) * width) + x + Step]);
                    }

                    if (y < height - Step)
                    {
                        Store(from[((y + Step) * width) + x + Step]);
                    }
                }

                if (y >= Step)
                {
                    Store(from[((y - Step) * width) + x]);
                }

                if (y < height - Step)
                {
                    Store(from[((y + Step) * width) + x]);
                }

                to[(y * width) + x] = (0.45f * min0) + (0.3f * min1) + (0.25f * min2);
            }
        });
    }

    private static double MaskY(double delta)
    {
        const double Offset = 0.829591754942;
        const double Scaler = 0.451936922203;
        const double Mul = 2.5485944793;
        double c = Mul / ((Scaler * delta) + Offset);
        double retval = GlobalScale * (1.0 + c);
        return retval * retval;
    }

    private static double MaskDcY(double delta)
    {
        const double Offset = 0.20025578522;
        const double Scaler = 3.87449418804;
        const double Mul = 0.505054525019;
        double c = Mul / ((Scaler * delta) + Offset);
        double retval = GlobalScale * (1.0 + c);
        return retval * retval;
    }
}
