using System.Buffers;
using System.Numerics;

using Egami.Pixels;

namespace Egami.Operations.Tone;

/// <summary>
/// The filters the measured tone steps resolve to, computed from the pixels inside a scope. Each follows ImageMagick's definition
/// of the matching option, so black and white points, means and threshold levels equal ImageMagick's at the same bit depth.
/// </summary>
internal static class ToneMeasures
{
    /// <summary>-normalize's fractions of pixels clipped at the black and white ends: -contrast-stretch 2%x1%, as ImageMagick 7.1 does.</summary>
    internal const float NormalizeBlack = 0.02f, NormalizeWhite = 0.01f;

    /// <summary>The share of the brightest pixels WhitePatch takes as white.</summary>
    private const double WhitePatchShare = 0.01;

    /// <summary>-auto-level: the darkest value becomes 0 and the lightest 1, over the chosen channels together or each alone.</summary>
    internal static ColorFilter AutoLevel<TPixel>(IReadOnlyList<PixelPlane<TPixel>> frames, MeasureScope scope, bool linked, EgamiContext context, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        using Histogram h = Histogram.Measure(frames, scope, PixelReader.Bins<TPixel>(), context.MaxParallelism, cancellationToken, HistogramValue.None);
        ColorChannels channels = scope.ColorChannels;
        double[] sum = h.Sum(channels);
        return PerChannel(channels, linked, c =>
        {
            double[] counts = linked ? sum : h.Channels[c];
            int low = First(counts), high = Last(counts);
            return low < 0 || high <= low ? ToneCurve.Identity : LevelCurve(low / (double)(h.Bins - 1), high / (double)(h.Bins - 1), 1);
        });
    }

    /// <summary>
    /// -contrast-stretch: the darkest <paramref name="black"/> and lightest <paramref name="white"/> fractions of the pixels clip, and
    /// the rest stretches to 0..1. Linked, the points come from the intensity histogram, as ImageMagick's synced channels.
    /// </summary>
    internal static ColorFilter ContrastStretch<TPixel>(IReadOnlyList<PixelPlane<TPixel>> frames, MeasureScope scope, float black, float white, bool linked, EgamiContext context, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        using Histogram h = Histogram.Measure(frames, scope, PixelReader.Bins<TPixel>(), context.MaxParallelism, cancellationToken);
        ColorChannels channels = scope.ColorChannels;
        double[] intensity = channels == ColorChannels.Color ? h.Channels[3] : h.Sum(channels);
        return PerChannel(channels, linked, c =>
        {
            double[] counts = linked ? intensity : h.Channels[c];
            double total = counts.Sum();
            if (total <= 0)
            {
                return ToneCurve.Identity;
            }

            double cumulative = 0;
            int low = 0;
            for (; low < counts.Length - 1; low++)
            {
                cumulative += counts[low];
                if (cumulative > black * total)
                {
                    break;
                }
            }

            cumulative = 0;
            int high = counts.Length - 1;
            for (; high > 0; high--)
            {
                cumulative += counts[high];
                if (cumulative > white * total)
                {
                    break;
                }
            }

            return high <= low ? ToneCurve.Identity : LevelCurve(low / (double)(h.Bins - 1), high / (double)(h.Bins - 1), 1);
        });
    }

    /// <summary>-auto-gamma: the gamma that moves the mean to one half.</summary>
    internal static ColorFilter AutoGamma<TPixel>(IReadOnlyList<PixelPlane<TPixel>> frames, MeasureScope scope, bool linked, EgamiContext context, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        List<double[]> parts = PixelReader.Read(frames, scope, () => new double[4], (sums, pixels, weights) =>
        {
            for (int x = 0; x < pixels.Length; x++)
            {
                double w = weights[x];
                sums[0] += pixels[x].R * w;
                sums[1] += pixels[x].G * w;
                sums[2] += pixels[x].B * w;
                sums[3] += w;
            }
        }, context.MaxParallelism, cancellationToken);
        double[] total = new double[4];
        foreach (double[] part in parts)
        {
            for (int i = 0; i < 4; i++)
            {
                total[i] += part[i];
            }
        }

        ColorChannels channels = scope.ColorChannels;
        double linkedMean = 0;
        int count = 0;
        for (int c = 0; c < 3; c++)
        {
            if (((int)channels & (1 << c)) != 0)
            {
                linkedMean += total[c];
                count++;
            }
        }

        linkedMean = total[3] > 0 ? linkedMean / (count * total[3]) : 0.5;
        return PerChannel(channels, linked, c =>
        {
            double mean = linked ? linkedMean : total[3] > 0 ? total[c] / total[3] : 0.5;
            if (!(mean > 0 && mean < 1))
            {
                return ToneCurve.Identity;
            }

            double gamma = Math.Log(mean) / Math.Log(0.5);
            return ToneCurve.FromFormula(v => ToneMath.Gamma(v, gamma));
        });
    }

    /// <summary>The white balance an automatic method finds.</summary>
    internal static ColorFilter WhiteBalance<TPixel>(IReadOnlyList<PixelPlane<TPixel>> frames, MeasureScope scope, WhiteBalanceMethod method, EgamiContext context, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (method == WhiteBalanceMethod.GrayWorld)
        {
            List<double[]> parts = PixelReader.Read(frames, scope, () => new double[3], (sums, pixels, weights) =>
            {
                for (int x = 0; x < pixels.Length; x++)
                {
                    if (weights[x] > 0)
                    {
                        Vector3 lab = LabMath.ToLab(pixels[x].R, pixels[x].G, pixels[x].B);
                        sums[0] += lab.Y * weights[x];
                        sums[1] += lab.Z * weights[x];
                        sums[2] += weights[x];
                    }
                }
            }, context.MaxParallelism, cancellationToken);
            double a = 0, b = 0, n = 0;
            foreach (double[] part in parts)
            {
                a += part[0];
                b += part[1];
                n += part[2];
            }

            return n <= 0 ? ColorFilter.Identity : ColorFilter.FromStages(new LabShiftStage((float)(a / n), (float)(b / n)));
        }

        // White patch: the brightest share of the pixels by intensity, averaged in linear light, becomes neutral.
        using Histogram h = Histogram.Measure(frames, scope, 256, context.MaxParallelism, cancellationToken);
        double[] intensity = h.Channels[3];
        double target = h.Total * WhitePatchShare, cumulative = 0;
        int cutoff = 255;
        for (; cutoff > 0; cutoff--)
        {
            cumulative += intensity[cutoff];
            if (cumulative >= target)
            {
                break;
            }
        }

        List<double[]> sums = PixelReader.Read(frames, scope, () => new double[4], (s, pixels, weights) =>
        {
            for (int x = 0; x < pixels.Length; x++)
            {
                RgbaF32 p = pixels[x];
                if (weights[x] > 0 && PixelReader.Bin((float)ToneMath.Intensity(p.R, p.G, p.B), 256) >= cutoff)
                {
                    s[0] += Srgb.ToLinear(Math.Clamp(p.R, 0, 1)) * weights[x];
                    s[1] += Srgb.ToLinear(Math.Clamp(p.G, 0, 1)) * weights[x];
                    s[2] += Srgb.ToLinear(Math.Clamp(p.B, 0, 1)) * weights[x];
                    s[3] += weights[x];
                }
            }
        }, context.MaxParallelism, cancellationToken);
        double[] mean = new double[4];
        foreach (double[] part in sums)
        {
            for (int i = 0; i < 4; i++)
            {
                mean[i] += part[i];
            }
        }

        return mean[3] <= 0 ? ColorFilter.Identity : Gains(mean[0] / mean[3], mean[1] / mean[3], mean[2] / mean[3]);
    }

    /// <summary>
    /// The filter that makes a color of linear red, green and blue neutral: each channel scaled in linear light so the color becomes
    /// the gray of its own luminance.
    /// </summary>
    internal static ColorFilter Gains(double r, double g, double b)
    {
        double y = (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
        if (!(r > 0 && g > 0 && b > 0 && y > 0))
        {
            return ColorFilter.Identity;
        }

        return ColorFilter.Curves(Gain(y / r), Gain(y / g), Gain(y / b));

        static ToneCurve Gain(double gain) => ToneCurve.FromFormula(v => Srgb.FromLinear(Math.Min(1, Srgb.ToLinear(v) * gain)));
    }

    /// <summary>
    /// Adapts from a light of <paramref name="temperature"/> kelvin, moved off the Planckian locus by <paramref name="tint"/>, to
    /// D65 with Bradford, in linear light.
    /// </summary>
    internal static ColorFilter Temperature(float temperature, float tint)
    {
        // Krystek's (1985) rational approximation of the Planckian locus in CIE 1960 uv, valid from 1,000 to 15,000 K.
        double t = temperature;
        double u = (0.860117757 + (1.54118254e-4 * t) + (1.28641212e-7 * t * t)) / (1 + (8.42420235e-4 * t) + (7.08145163e-7 * t * t));
        double v = (0.317398726 + (4.22806245e-5 * t) + (4.20481691e-8 * t * t)) / (1 - (2.89741816e-5 * t) + (1.61456053e-7 * t * t));

        // Tint moves the light along the locus normal by Duv = 0.05 × tint (positive: greener, so the correction adds magenta).
        const double h = 1;
        double u2 = (0.860117757 + (1.54118254e-4 * (t + h)) + (1.28641212e-7 * (t + h) * (t + h))) / (1 + (8.42420235e-4 * (t + h)) + (7.08145163e-7 * (t + h) * (t + h)));
        double v2 = (0.317398726 + (4.22806245e-5 * (t + h)) + (4.20481691e-8 * (t + h) * (t + h))) / (1 - (2.89741816e-5 * (t + h)) + (1.61456053e-7 * (t + h) * (t + h)));
        double du = u2 - u, dv = v2 - v, length = Math.Sqrt((du * du) + (dv * dv));
        double duv = 0.05 * tint;
        u += -dv / length * duv;
        v += du / length * duv;

        double x = 3 * u / ((2 * u) - (8 * v) + 4), y = 2 * v / ((2 * u) - (8 * v) + 4);
        Colors.Vector3d source = new(x / y, 1, (1 - x - y) / y);
        Colors.Vector3d d65 = new(0.95047, 1, 1.08883);
        Colors.Matrix3d toXyz = new(0.4123908, 0.3575843, 0.1804808, 0.2126390, 0.7151687, 0.0721923, 0.0193308, 0.1191948, 0.9505322);
        Colors.Matrix3d m = toXyz.Inverse() * Colors.Matrix3d.Bradford(source, d65) * toXyz;
        return ColorFilter.FromStages(new LinearMatrixStage(
        [
            (float)m[0, 0], (float)m[0, 1], (float)m[0, 2],
            (float)m[1, 0], (float)m[1, 1], (float)m[1, 2],
            (float)m[2, 0], (float)m[2, 1], (float)m[2, 2],
        ]));
    }

    /// <summary>
    /// -equalize: each value maps to its share of the cumulative histogram above the first bin, per channel, or of L* for
    /// <see cref="EqualizeMode.Luminance"/>, keeping a* and b*.
    /// </summary>
    internal static ColorFilter Equalize<TPixel>(IReadOnlyList<PixelPlane<TPixel>> frames, MeasureScope scope, EqualizeMode mode, EgamiContext context, List<float[]>? owned, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int bins = PixelReader.Bins<TPixel>();
        if (mode == EqualizeMode.Luminance)
        {
            // L* never falls on a pixel type's own steps, so it is binned finely for every type.
            using Histogram l = Histogram.Measure(frames, scope, 65536, context.MaxParallelism, cancellationToken, HistogramValue.Lightness);
            ToneCurve? curve = EqualizeCurve(l.Channels[3], owned);
            return curve is null ? ColorFilter.Identity : ColorFilter.FromStages(new LightnessStage(curve));
        }

        using Histogram h = Histogram.Measure(frames, scope, bins, context.MaxParallelism, cancellationToken, HistogramValue.None);
        return PerChannel(scope.ColorChannels, linked: false, c => EqualizeCurve(h.Channels[c], owned) ?? ToneCurve.Identity);
    }

    /// <summary>
    /// The equalizing curve of a histogram, or null when it has one value or none. With <paramref name="owned"/>, its table is
    /// rented and listed there, for the step that runs the curve to return when the chain ends.
    /// </summary>
    internal static ToneCurve? EqualizeCurve(double[] counts, List<float[]>? owned = null)
    {
        int bins = counts.Length;
        if (counts.Count(c => c > 0) < 2)
        {
            return null;
        }

        double total = counts.Sum(), black = counts[0];
        float[] table = owned is null ? new float[bins] : ArrayPool<float>.Shared.Rent(bins);
        owned?.Add(table);
        double cumulative = 0;
        for (int j = 0; j < bins; j++)
        {
            cumulative += counts[j];
            table[j] = (float)((cumulative - black) / (total - black));
        }

        return ToneCurve.FromFormula(v => table[PixelReader.Bin((float)v, bins)], smooth: false);
    }

    /// <summary>
    /// The threshold level a method finds, from 0 to 1, on a 256-bin histogram of what <paramref name="mode"/> measures (red,
    /// green and blue together for <see cref="ThresholdMode.Channels"/>); 0.5 when the image has one value.
    /// </summary>
    internal static int FindThresholdBin<TPixel>(IReadOnlyList<PixelPlane<TPixel>> frames, MeasureScope scope, ThresholdMethod method, ThresholdMode mode, EgamiContext context, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        using Histogram h = Histogram.Measure(frames, scope, 256, context.MaxParallelism, cancellationToken, HistogramValue.Threshold, mode);
        double[] counts = mode == ThresholdMode.Channels ? h.Sum(scope.ColorChannels) : h.Channels[3];
        int first = First(counts), last = Last(counts);
        if (first < 0 || first == last)
        {
            return -1;
        }

        return method switch
        {
            ThresholdMethod.Triangle => Thresholds.Triangle(counts),
            ThresholdMethod.Kapur => Thresholds.Kapur(counts),
            _ => Thresholds.Otsu(counts),
        };
    }

    /// <summary>The level of <see cref="FindThresholdBin"/>'s bin, from 0 to 1, or 0.5 when the image has one value.</summary>
    internal static float FindThreshold<TPixel>(IReadOnlyList<PixelPlane<TPixel>> frames, MeasureScope scope, ThresholdMethod method, ThresholdMode mode, EgamiContext context, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int bin = FindThresholdBin(frames, scope, method, mode, context, cancellationToken);
        return bin < 0 ? 0.5f : bin / 255f;
    }

    /// <summary>Curves for the chosen color channels; the others keep their values.</summary>
    private static ColorFilter PerChannel(ColorChannels channels, bool linked, Func<int, ToneCurve> curve)
    {
        ToneCurve[] curves = new ToneCurve[3];
        ToneCurve? shared = null;
        for (int c = 0; c < 3; c++)
        {
            curves[c] = ((int)channels & (1 << c)) == 0 ? ToneCurve.Identity : linked ? shared ??= curve(c) : curve(c);
        }

        return ColorFilter.Curves(curves[0], curves[1], curves[2]);
    }

    private static ToneCurve LevelCurve(double black, double white, double gamma)
    {
        return ToneCurve.FromFormula(v => ToneMath.Level(v, black, white, gamma, 0, 1));
    }

    private static int First(double[] counts)
    {
        for (int j = 0; j < counts.Length; j++)
        {
            if (counts[j] > 0)
            {
                return j;
            }
        }

        return -1;
    }

    private static int Last(double[] counts)
    {
        for (int j = counts.Length - 1; j >= 0; j--)
        {
            if (counts[j] > 0)
            {
                return j;
            }
        }

        return -1;
    }
}

/// <summary>The automatic threshold methods on a 256-bin histogram, as ImageMagick's -auto-threshold computes them; each returns a bin.</summary>
internal static class Thresholds
{
    /// <summary>Otsu (1979): the bin that maximizes the between-class variance; the first such bin on ties.</summary>
    internal static int Otsu(double[] histogram)
    {
        int n = histogram.Length - 1;
        double total = histogram.Sum();
        double[] omega = new double[n + 1], mu = new double[n + 1];
        omega[0] = histogram[0] / total;
        for (int i = 1; i <= n; i++)
        {
            double p = histogram[i] / total;
            omega[i] = omega[i - 1] + p;
            mu[i] = mu[i - 1] + (i * p);
        }

        int threshold = 0;
        double best = 0;
        for (int i = 0; i < n; i++)
        {
            if (omega[i] != 0 && omega[i] != 1)
            {
                double d = (mu[n] * omega[i]) - mu[i];
                double sigma = d * d / (omega[i] * (1 - omega[i]));
                if (sigma > best)
                {
                    best = sigma;
                    threshold = i;
                }
            }
        }

        return threshold;
    }

    /// <summary>
    /// The triangle method (Zack, Rogers and Latt, 1977): a line from the peak to the far end of the histogram, and the bin on the
    /// longer side farthest below it.
    /// </summary>
    internal static int Triangle(double[] histogram)
    {
        int n = histogram.Length - 1;
        int start = 0, end = 0, peak = 0;
        for (int i = 0; i <= n; i++)
        {
            if (histogram[i] > 0)
            {
                start = i;
                break;
            }
        }

        for (int i = n; i >= 0; i--)
        {
            if (histogram[i] > 0)
            {
                end = i;
                break;
            }
        }

        for (int i = 0; i <= n; i++)
        {
            if (histogram[i] > histogram[peak])
            {
                peak = i;
            }
        }

        double x1 = peak, y1 = histogram[peak], x2 = peak - start >= end - peak ? start : end, y2 = 0;
        double a = y1 - y2, b = x2 - x1, c = -((a * x1) + (b * y1));
        int threshold = 0;
        double best = 0;
        if (x2 == start)
        {
            for (int i = start; i < peak; i++)
            {
                double segment = (a * i) + (b * histogram[i]) + c;
                if (Math.Abs(segment) > best && segment > 0)
                {
                    threshold = i;
                    best = Math.Abs(segment);
                }
            }
        }
        else
        {
            for (int i = end; i > peak; i--)
            {
                double segment = (a * i) + (b * histogram[i]) + c;
                if (Math.Abs(segment) > best && segment < 0)
                {
                    threshold = i;
                    best = Math.Abs(segment);
                }
            }
        }

        return threshold;
    }

    /// <summary>Kapur, Sahoo and Wong (1985): the bin that maximizes the summed entropy of the classes below and above it.</summary>
    internal static int Kapur(double[] histogram)
    {
        const double epsilon = 1e-12;
        int n = histogram.Length - 1;
        double total = histogram.Sum();
        double[] p = new double[n + 1], cumulative = new double[n + 1];
        for (int i = 0; i <= n; i++)
        {
            p[i] = histogram[i] / total;
            cumulative[i] = (i > 0 ? cumulative[i - 1] : 0) + p[i];
        }

        int threshold = 0;
        double best = double.MinValue;
        for (int j = 0; j <= n; j++)
        {
            double below = 0;
            if (cumulative[j] > epsilon)
            {
                for (int i = 0; i <= j; i++)
                {
                    if (p[i] > epsilon)
                    {
                        double q = p[i] / cumulative[j];
                        below -= q * Math.Log(q);
                    }
                }
            }

            double above = 0;
            double rest = 1 - cumulative[j];
            if (rest > epsilon)
            {
                for (int i = j + 1; i <= n; i++)
                {
                    if (p[i] > epsilon)
                    {
                        double q = p[i] / rest;
                        above -= q * Math.Log(q);
                    }
                }
            }

            if (below + above > best)
            {
                best = below + above;
                threshold = j;
            }
        }

        return threshold;
    }
}
