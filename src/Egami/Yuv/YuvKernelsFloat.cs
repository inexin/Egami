using System.Runtime.CompilerServices;

using Egami.Pixels;

namespace Egami;

/// <summary>
/// Single-float conversion between YUV at any depth and 8- or 16-bit RGB: every case <see cref="YuvKernels8"/> does not cover.
/// Single floats carry 24 bits, so even 16-bit output is within a small fraction of a level of the exact result. The SIMD
/// kernels do the same operations in the same order, so they give the same samples.
/// </summary>
internal static partial class YuvKernelsFloat
{
    /// <summary>Converts one row to 8-bit RGB. <paramref name="cb"/> and <paramref name="cr"/> are at 16 times the sample scale.</summary>
    internal static void ToRgb<TSample>(ReadOnlySpan<TSample> y, ReadOnlySpan<int> cb, ReadOnlySpan<int> cr, ReadOnlySpan<TSample> alpha, int max, Span<byte> target, YuvConvert.Order order, in ToRgbCoefficients k)
        where TSample : unmanaged
    {
        int done = ToRgbSimd(y, cb, cr, alpha, max, target, order, in k);
        ToRgbScalar(y, cb, cr, alpha, max, target, order, in k, done);
    }

    /// <summary>Converts one row to 16-bit RGB. <paramref name="cb"/> and <paramref name="cr"/> are at 16 times the sample scale.</summary>
    internal static void ToRgb<TSample>(ReadOnlySpan<TSample> y, ReadOnlySpan<int> cb, ReadOnlySpan<int> cr, ReadOnlySpan<TSample> alpha, int max, Span<ushort> target, YuvConvert.Order order, in ToRgbCoefficients k)
        where TSample : unmanaged
    {
        int done = ToRgbSimd(y, cb, cr, alpha, max, target, order, in k);
        ToRgbScalar(y, cb, cr, alpha, max, target, order, in k, done);
    }

    /// <summary>The scalar path, from pixel <paramref name="start"/>, for 8- or 16-bit output elements.</summary>
    internal static void ToRgbScalar<TSample, TOut>(ReadOnlySpan<TSample> y, ReadOnlySpan<int> cb, ReadOnlySpan<int> cr, ReadOnlySpan<TSample> alpha, int max, Span<TOut> target, YuvConvert.Order order, in ToRgbCoefficients k, int start = 0)
        where TSample : unmanaged
        where TOut : unmanaged
    {
        for (int x = start; x < y.Length; x++)
        {
            float luma = (YuvConvert.Sample(y, x, max) - k.YOff) * k.YScale;
            float r, g, b;
            float u = (cb[x] - k.COff) * k.CScale, v = (cr[x] - k.COff) * k.CScale;
            if (k.Identity)
            {
                (r, g, b) = (v, luma, u);
            }
            else
            {
                r = luma + (v * k.Kr);
                g = luma - (u * k.Kgb) - (v * k.Kgr);
                b = luma + (u * k.Kb);
            }

            int o = x * order.Size;
            Set(target, o + order.R, Round(r, k.OutMax));
            Set(target, o + order.G, Round(g, k.OutMax));
            Set(target, o + order.B, Round(b, k.OutMax));
            if (order.A >= 0)
            {
                Set(target, o + order.A, alpha.IsEmpty ? k.OutMax : (int)(((2L * YuvConvert.Sample(alpha, x, max) * k.OutMax) + max) / (2L * max)));
            }
        }
    }

    /// <summary>
    /// Converts one row of 8-bit RGB to luma (when <paramref name="luma"/> is not empty) and full-resolution chroma in sample
    /// units, centered, unrounded (when <paramref name="cb"/> is not empty).
    /// </summary>
    internal static void FromRgb<TSample>(ReadOnlySpan<byte> rgb, YuvConvert.Order order, Span<TSample> luma, Span<float> cb, Span<float> cr, int width, int max, in FromRgbCoefficients k)
        where TSample : unmanaged
    {
        int done = FromRgbSimd(rgb, order, luma, cb, cr, width, max, in k);
        FromRgbScalar(rgb, order, luma, cb, cr, width, max, in k, done);
    }

    /// <summary>Converts one row of 16-bit RGB. See the 8-bit overload.</summary>
    internal static void FromRgb<TSample>(ReadOnlySpan<ushort> rgb, YuvConvert.Order order, Span<TSample> luma, Span<float> cb, Span<float> cr, int width, int max, in FromRgbCoefficients k)
        where TSample : unmanaged
    {
        int done = FromRgbSimd(rgb, order, luma, cb, cr, width, max, in k);
        FromRgbScalar(rgb, order, luma, cb, cr, width, max, in k, done);
    }

    /// <summary>The scalar path, from pixel <paramref name="start"/>.</summary>
    internal static void FromRgbScalar<TIn, TSample>(ReadOnlySpan<TIn> rgb, YuvConvert.Order order, Span<TSample> luma, Span<float> cb, Span<float> cr, int width, int max, in FromRgbCoefficients k, int start = 0)
        where TIn : unmanaged
        where TSample : unmanaged
    {
        for (int x = start; x < width; x++)
        {
            int o = x * order.Size;
            float r = Get(rgb, o + order.R), g = Get(rgb, o + order.G), b = Get(rgb, o + order.B);
            if (!luma.IsEmpty)
            {
                YuvConvert.SetSample(luma, x, Round((r * k.YR) + (g * k.YG) + (b * k.YB) + k.YOff, max));
            }

            if (!cb.IsEmpty)
            {
                cb[x] = (r * k.BR) + (g * k.BG) + (b * k.BB);
                cr[x] = (r * k.RR) + (g * k.RG) + (b * k.RB);
            }
        }
    }

    /// <summary>Filters full-resolution chroma rows down to one chroma row and rounds it once.</summary>
    internal static void Downsample<TSample>(
        float[][] rows, ReadOnlySpan<int> verticalWeights, int verticalWeight, float[] sums, Span<TSample> u, Span<TSample> v, int width,
        ChromaFormat format, ChromaSiting siting, bool fast, int max, in FromRgbCoefficients k)
        where TSample : unmanaged
    {
        int chromaWidth = u.Length;
        float scale = 1f / (HorizontalWeight(format, siting, fast) * verticalWeight);
        for (int plane = 0; plane < 2; plane++)
        {
            Span<float> sum = sums.AsSpan(0, chromaWidth);
            sum.Clear();
            for (int r = 0; r < verticalWeights.Length; r++)
            {
                FilterRow(rows[(2 * r) + plane].AsSpan(0, width), verticalWeights[r], sum, format, siting, fast);
            }

            Span<TSample> target = plane == 0 ? u : v;
            for (int i = YuvResample.RoundFloat(sum, target, scale, k.COff, max); i < chromaWidth; i++)
            {
                YuvConvert.SetSample(target, i, Round((sum[i] * scale) + k.COff, max));
            }
        }
    }

    /// <summary>Returns the sum of the horizontal downsampling weights: 1, 2 or 4.</summary>
    internal static int HorizontalWeight(ChromaFormat format, ChromaSiting siting, bool fast) =>
        format == ChromaFormat.Yuv444 || fast ? 1 : siting == ChromaSiting.Center ? 2 : 4;

    /// <summary>Adds one full-resolution row, filtered horizontally and times <paramref name="weight"/>, to the chroma sums.</summary>
    internal static void FilterRow(ReadOnlySpan<int> row, int weight, Span<int> sum, ChromaFormat format, ChromaSiting siting, bool fast)
    {
        int last = row.Length - 1;
        int done = format == ChromaFormat.Yuv444 ? 0 : YuvResample.Filter(row, weight, sum, siting, fast);
        for (int i = 0; i < sum.Length; i++)
        {
            if (i == 1 && done > 1)
            {
                i = done;
                if (i >= sum.Length)
                {
                    break;
                }
            }

            int value;
            if (format == ChromaFormat.Yuv444)
            {
                value = row[i];
            }
            else if (fast)
            {
                value = row[2 * i];
            }
            else if (siting == ChromaSiting.Center)
            {
                value = row[2 * i] + row[Math.Min((2 * i) + 1, last)];
            }
            else
            {
                value = row[Math.Max((2 * i) - 1, 0)] + (2 * row[2 * i]) + row[Math.Min((2 * i) + 1, last)];
            }

            sum[i] += weight * value;
        }
    }

    /// <summary>Adds one full-resolution row, filtered horizontally and times <paramref name="weight"/>, to the chroma sums.</summary>
    internal static void FilterRow(ReadOnlySpan<float> row, int weight, Span<float> sum, ChromaFormat format, ChromaSiting siting, bool fast)
    {
        int last = row.Length - 1;
        int done = format == ChromaFormat.Yuv444 ? 0 : YuvResample.Filter(row, weight, sum, siting, fast);
        for (int i = 0; i < sum.Length; i++)
        {
            if (i == 1 && done > 1)
            {
                i = done;
                if (i >= sum.Length)
                {
                    break;
                }
            }

            float value;
            if (format == ChromaFormat.Yuv444)
            {
                value = row[i];
            }
            else if (fast)
            {
                value = row[2 * i];
            }
            else if (siting == ChromaSiting.Center)
            {
                value = row[2 * i] + row[Math.Min((2 * i) + 1, last)];
            }
            else
            {
                value = row[Math.Max((2 * i) - 1, 0)] + (2 * row[2 * i]) + row[Math.Min((2 * i) + 1, last)];
            }

            sum[i] += weight * value;
        }
    }

    /// <summary>Rounds half up and clamps to 0..<paramref name="max"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Round(float value, int max) => (int)Math.Clamp(MathF.Floor(value + 0.5f), 0f, max);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Get<TIn>(ReadOnlySpan<TIn> row, int i)
        where TIn : unmanaged
    {
        return typeof(TIn) == typeof(byte)
            ? Unsafe.As<TIn, byte>(ref Unsafe.AsRef(in row[i]))
            : Unsafe.As<TIn, ushort>(ref Unsafe.AsRef(in row[i]));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Set<TOut>(Span<TOut> row, int i, int value)
        where TOut : unmanaged
    {
        if (typeof(TOut) == typeof(byte))
        {
            Unsafe.As<TOut, byte>(ref row[i]) = (byte)value;
        }
        else
        {
            Unsafe.As<TOut, ushort>(ref row[i]) = (ushort)value;
        }
    }

    /// <summary>
    /// The matrix of one <see cref="YuvColor"/> at one bit depth, in double precision, from RGB codes 0..<c>inMax</c> to samples:
    /// the luma and chroma rows, the luma scale (the sum of the luma row) and offsets. The fixed-point and float coefficients are
    /// both rounded from it.
    /// </summary>
    internal readonly record struct Matrix(
        bool Identity, double YR, double YG, double YB, double YScale, double YOff,
        double BR, double BG, double BB, double RR, double RG, double RB, double COff)
    {
        internal static Matrix For(YuvColor color, int bitDepth, int inMax)
        {
            bool full = color.Range == YuvRange.Full;
            double depthScale = 1 << (bitDepth - 8);
            double lumaScale = (full ? (1 << bitDepth) - 1 : 219 * depthScale) / inMax;
            double lumaOffset = full ? 0 : 16 * depthScale;
            if (color.Matrix == YuvMatrix.Identity)
            {
                return new Matrix(true, 0, lumaScale, 0, lumaScale, lumaOffset, 0, 0, lumaScale, lumaScale, 0, 0, lumaOffset);
            }

            (double kr, double kb) = YuvCoefficients.Of(color.Matrix);
            double kg = 1 - kr - kb;
            double chromaScale = (full ? (1 << bitDepth) - 1 : 224 * depthScale) / inMax;
            double cb = chromaScale / (2 * (1 - kb)), cr = chromaScale / (2 * (1 - kr));
            return new Matrix(
                false, kr * lumaScale, kg * lumaScale, kb * lumaScale, lumaScale, lumaOffset,
                -kr * cb, -kg * cb, (1 - kb) * cb, (1 - kr) * cr, -kg * cr, -kb * cr, 1 << (bitDepth - 1));
        }
    }

    /// <summary>The float coefficients of one <see cref="YuvColor"/> and depth for <see cref="ToRgb{TSample}(ReadOnlySpan{TSample}, ReadOnlySpan{int}, ReadOnlySpan{int}, ReadOnlySpan{TSample}, int, Span{byte}, YuvConvert.Order, in ToRgbCoefficients)"/>.</summary>
    internal readonly struct ToRgbCoefficients
    {
        internal bool Identity { get; init; }

        /// <summary>The luma offset, in samples.</summary>
        internal float YOff { get; init; }

        /// <summary>Output levels per luma sample.</summary>
        internal float YScale { get; init; }

        /// <summary>The chroma center at 16 times the sample scale (for identity, the luma offset).</summary>
        internal float COff { get; init; }

        /// <summary>Output levels per chroma step at 16 times the sample scale.</summary>
        internal float CScale { get; init; }

        internal float Kr { get; init; }

        internal float Kgb { get; init; }

        internal float Kgr { get; init; }

        internal float Kb { get; init; }

        internal int OutMax { get; init; }

        internal static ToRgbCoefficients For(YuvColor color, int bitDepth, int outMax)
        {
            bool full = color.Range == YuvRange.Full;
            double depthScale = 1 << (bitDepth - 8);
            double lumaRange = full ? (1 << bitDepth) - 1 : 219 * depthScale;
            double lumaOffset = full ? 0 : 16 * depthScale;
            if (color.Matrix == YuvMatrix.Identity)
            {
                return new ToRgbCoefficients
                {
                    Identity = true,
                    YOff = (float)lumaOffset,
                    YScale = (float)(outMax / lumaRange),
                    COff = (float)(16 * lumaOffset),
                    CScale = (float)(outMax / lumaRange / 16),
                    OutMax = outMax,
                };
            }

            (double kr, double kb) = YuvCoefficients.Of(color.Matrix);
            double kg = 1 - kr - kb;
            double chromaRange = full ? (1 << bitDepth) - 1 : 224 * depthScale;
            return new ToRgbCoefficients
            {
                YOff = (float)lumaOffset,
                YScale = (float)(outMax / lumaRange),
                COff = 16 << (bitDepth - 1),
                CScale = (float)(outMax / chromaRange / 16),
                Kr = (float)(2 * (1 - kr)),
                Kgb = (float)(2 * (1 - kb) * kb / kg),
                Kgr = (float)(2 * (1 - kr) * kr / kg),
                Kb = (float)(2 * (1 - kb)),
                OutMax = outMax,
            };
        }
    }

    /// <summary>The float coefficients of one <see cref="YuvColor"/> and depth for <c>FromRgb</c>, from RGB codes.</summary>
    internal readonly struct FromRgbCoefficients
    {
        internal float YR { get; init; }

        internal float YG { get; init; }

        internal float YB { get; init; }

        /// <summary>The luma offset in samples.</summary>
        internal float YOff { get; init; }

        internal float BR { get; init; }

        internal float BG { get; init; }

        internal float BB { get; init; }

        internal float RR { get; init; }

        internal float RG { get; init; }

        internal float RB { get; init; }

        /// <summary>The value added to filtered chroma before rounding.</summary>
        internal float COff { get; init; }

        internal static FromRgbCoefficients For(YuvColor color, int bitDepth, int inMax)
        {
            Matrix m = Matrix.For(color, bitDepth, inMax);
            return new FromRgbCoefficients
            {
                YR = (float)m.YR,
                YG = (float)m.YG,
                YB = (float)m.YB,
                YOff = (float)m.YOff,
                BR = (float)m.BR,
                BG = (float)m.BG,
                BB = (float)m.BB,
                RR = (float)m.RR,
                RG = (float)m.RG,
                RB = (float)m.RB,
                COff = (float)m.COff,
            };
        }
    }
}
