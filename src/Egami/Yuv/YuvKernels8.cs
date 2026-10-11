using System.Runtime.CompilerServices;

using Egami.Pixels;

namespace Egami;

/// <summary>
/// Fixed-point conversion between 8-bit YUV and the 8-bit RGB orders.
/// </summary>
/// <remarks>
/// <para>
/// To RGB: values are kept in 1/64 of a level in 16-bit lanes. Luma is <c>Y × 64</c> at full range, or
/// <c>mulhrs((Y − 16) × 128, Ky)</c> at limited range; chroma, upsampled at 16 times the sample scale, is centered and shifted to
/// <c>(c − 128) × 256</c>, so each chroma term is one <c>mulhrs(c, K)</c> with K below 2^15 for every matrix and range
/// (<c>K = coefficient × 8192</c>). <c>mulhrs(a, b) = (a × b + 2^14) &gt;&gt; 15</c> is the x86 <c>pmulhrsw</c> and Arm
/// <c>sqrdmulh</c> instruction. Sums saturate at the 16-bit range, which only ever happens beyond 0 or 255, and the result is
/// <c>(v + 32) &gt;&gt; 6</c> clamped to a byte.
/// </para>
/// <para>
/// From RGB: 32-bit sums of Q15 coefficients (the luma coefficients sum to exactly the range's scale, the chroma ones to 0, so
/// gray stays gray). Chroma is not rounded until the downsampling filter has summed it.
/// </para>
/// </remarks>
internal static partial class YuvKernels8
{
    /// <summary>Converts one row. <paramref name="cb"/> and <paramref name="cr"/> are upsampled chroma at 16 times the sample scale.</summary>
    internal static void ToRgb(ReadOnlySpan<byte> y, ReadOnlySpan<short> cb, ReadOnlySpan<short> cr, ReadOnlySpan<byte> alpha, Span<byte> target, YuvConvert.Order order, in ToRgbCoefficients k)
    {
        int done = ToRgbSimd(y, cb, cr, alpha, target, order, in k);
        ToRgbScalar(y, cb, cr, alpha, target, order, in k, done);
    }

    /// <summary>The scalar path, from pixel <paramref name="start"/>. SIMD kernels must match it exactly.</summary>
    internal static void ToRgbScalar(ReadOnlySpan<byte> y, ReadOnlySpan<short> cb, ReadOnlySpan<short> cr, ReadOnlySpan<byte> alpha, Span<byte> target, YuvConvert.Order order, in ToRgbCoefficients k, int start = 0)
    {
        for (int x = start; x < y.Length; x++)
        {
            int luma = Luma(y[x], in k);
            int r, g, b;
            if (k.Identity)
            {
                (r, g, b) = (Luma16(cr[x], in k), luma, Luma16(cb[x], in k));
            }
            else
            {
                int u = (cb[x] - 2048) << 4, v = (cr[x] - 2048) << 4;
                r = Saturate(luma + MulHrs(v, k.Kr));
                g = Saturate(Saturate(luma - MulHrs(u, k.Kgb)) - MulHrs(v, k.Kgr));
                b = Saturate(luma + MulHrs(u, k.Kb));
            }

            int o = x * order.Size;
            target[o + order.R] = Byte(r);
            target[o + order.G] = Byte(g);
            target[o + order.B] = Byte(b);
            if (order.A >= 0)
            {
                target[o + order.A] = alpha.IsEmpty ? (byte)255 : alpha[x];
            }
        }
    }

    /// <summary>
    /// Converts one row of RGB to luma (when <paramref name="luma"/> is not empty) and full-resolution chroma in Q15 (when
    /// <paramref name="cb"/> is not empty).
    /// </summary>
    internal static void FromRgb(ReadOnlySpan<byte> rgb, YuvConvert.Order order, Span<byte> luma, Span<int> cb, Span<int> cr, int width, in FromRgbCoefficients k)
    {
        int done = FromRgbSimd(rgb, order, luma, cb, cr, width, in k);
        FromRgbScalar(rgb, order, luma, cb, cr, width, in k, done);
    }

    /// <summary>The scalar path, from pixel <paramref name="start"/>. SIMD kernels must match it exactly.</summary>
    internal static void FromRgbScalar(ReadOnlySpan<byte> rgb, YuvConvert.Order order, Span<byte> luma, Span<int> cb, Span<int> cr, int width, in FromRgbCoefficients k, int start = 0)
    {
        for (int x = start; x < width; x++)
        {
            int o = x * order.Size;
            int r = rgb[o + order.R], g = rgb[o + order.G], b = rgb[o + order.B];
            if (!luma.IsEmpty)
            {
                luma[x] = (byte)Math.Clamp(((k.YR * r) + (k.YG * g) + (k.YB * b) + k.YBias) >> 15, 0, 255);
            }

            if (!cb.IsEmpty)
            {
                cb[x] = (k.BR * r) + (k.BG * g) + (k.BB * b);
                cr[x] = (k.RR * r) + (k.RG * g) + (k.RB * b);
            }
        }
    }

    /// <summary>
    /// Filters full-resolution Q15 chroma rows (slot <c>i</c> in <paramref name="rows"/>[2i] and [2i + 1]) down to one chroma row
    /// and rounds it once.
    /// </summary>
    internal static void Downsample<TSample>(
        int[][] rows, ReadOnlySpan<int> verticalWeights, int verticalWeight, int[] sums, Span<TSample> u, Span<TSample> v, int width,
        ChromaFormat format, ChromaSiting siting, bool fast, in FromRgbCoefficients k)
        where TSample : unmanaged
    {
        int chromaWidth = u.Length;
        int horizontalWeight = YuvKernelsFloat.HorizontalWeight(format, siting, fast);
        int shift = 15 + int.Log2(horizontalWeight * verticalWeight);
        int half = 1 << (shift - 1);
        for (int plane = 0; plane < 2; plane++)
        {
            Span<int> sum = sums.AsSpan(0, chromaWidth);
            sum.Clear();
            for (int r = 0; r < verticalWeights.Length; r++)
            {
                YuvKernelsFloat.FilterRow(rows[(2 * r) + plane].AsSpan(0, width), verticalWeights[r], sum, format, siting, fast);
            }

            Span<TSample> target = plane == 0 ? u : v;
            for (int i = YuvResample.Round8(sum, target, shift, k.COff); i < chromaWidth; i++)
            {
                YuvConvert.SetSample(target, i, Math.Clamp(((sum[i] + half) >> shift) + k.COff, 0, 255));
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int MulHrs(int a, int b) => ((a * b) + 0x4000) >> 15;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Saturate(int value) => Math.Clamp(value, short.MinValue, short.MaxValue);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Luma(int y, in ToRgbCoefficients k) => (k.LumaFull ? y << 6 : MulHrs((y - 16) << 7, k.Ky)) + 32;

    /// <summary>A luma-scaled channel from upsampled chroma at 16 times the sample scale, for the identity matrix.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Luma16(int c16, in ToRgbCoefficients k) => (k.LumaFull ? c16 << 2 : MulHrs((c16 - 256) << 3, k.Ky)) + 32;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Byte(int value) => (byte)Math.Clamp(value >> 6, 0, 255);

    /// <summary>The fixed-point coefficients of one <see cref="YuvColor"/> for <see cref="ToRgb"/>.</summary>
    internal readonly struct ToRgbCoefficients
    {
        internal bool Identity { get; init; }

        internal bool LumaFull { get; init; }

        internal short Ky { get; init; }

        internal short Kr { get; init; }

        internal short Kgb { get; init; }

        internal short Kgr { get; init; }

        internal short Kb { get; init; }

        internal static ToRgbCoefficients For(YuvColor color)
        {
            bool full = color.Range == YuvRange.Full;
            if (color.Matrix == YuvMatrix.Identity)
            {
                return new ToRgbCoefficients { Identity = true, LumaFull = full, Ky = Q(255.0 / 219 * 16384) };
            }

            (double kr, double kb) = YuvCoefficients.Of(color.Matrix);
            double kg = 1 - kr - kb;
            double chroma = full ? 1 : 255.0 / 224;
            return new ToRgbCoefficients
            {
                LumaFull = full,
                Ky = Q(255.0 / 219 * 16384),
                Kr = Q(2 * (1 - kr) * chroma * 8192),
                Kgb = Q(2 * (1 - kb) * kb / kg * chroma * 8192),
                Kgr = Q(2 * (1 - kr) * kr / kg * chroma * 8192),
                Kb = Q(2 * (1 - kb) * chroma * 8192),
            };

            static short Q(double value) => (short)YuvConvert.RoundHalfUp(value);
        }
    }

    /// <summary>The Q15 coefficients of one <see cref="YuvColor"/> for <see cref="FromRgb"/>, for 8-bit RGB input.</summary>
    internal readonly struct FromRgbCoefficients
    {
        internal int YR { get; init; }

        internal int YG { get; init; }

        internal int YB { get; init; }

        /// <summary>The luma offset in Q15 plus a half, for rounding.</summary>
        internal int YBias { get; init; }

        internal int BR { get; init; }

        internal int BG { get; init; }

        internal int BB { get; init; }

        internal int RR { get; init; }

        internal int RG { get; init; }

        internal int RB { get; init; }

        /// <summary>The value added to rounded chroma: 128, or for the identity matrix the luma offset.</summary>
        internal int COff { get; init; }

        internal static FromRgbCoefficients For(YuvColor color)
        {
            YuvKernelsFloat.Matrix m = YuvKernelsFloat.Matrix.For(color, 8, 255);
            (int yr, int yg, int yb) = Sum3(m.YR, m.YG, m.YB, m.YScale * 32768);
            (int br, int bg, int bb) = Sum3(m.BR, m.BG, m.BB, m.Identity ? m.YScale * 32768 : 0);
            (int rr, int rg, int rb) = Sum3(m.RR, m.RG, m.RB, m.Identity ? m.YScale * 32768 : 0);
            return new FromRgbCoefficients
            {
                YR = yr,
                YG = yg,
                YB = yb,
                YBias = ((int)m.YOff << 15) + (1 << 14),
                BR = br,
                BG = bg,
                BB = bb,
                RR = rr,
                RG = rg,
                RB = rb,
                COff = (int)m.COff,
            };

            // Q15 coefficients of a row of the matrix whose sum is exactly the rounded total, so gray converts exactly: the
            // rounding error goes to the largest coefficient.
            static (int, int, int) Sum3(double a, double b, double c, double total)
            {
                int qa = YuvConvert.RoundHalfUp(a * 32768), qb = YuvConvert.RoundHalfUp(b * 32768), qc = YuvConvert.RoundHalfUp(c * 32768);
                int error = YuvConvert.RoundHalfUp(total) - (qa + qb + qc);
                double max = Math.Max(Math.Abs(a), Math.Max(Math.Abs(b), Math.Abs(c)));
                if (Math.Abs(a) == max)
                {
                    qa += error;
                }
                else if (Math.Abs(b) == max)
                {
                    qb += error;
                }
                else
                {
                    qc += error;
                }

                return (qa, qb, qc);
            }
        }
    }
}
