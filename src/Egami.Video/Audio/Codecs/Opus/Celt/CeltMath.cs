// Translated from libopus 1.5.2 celt/mathops.h, celt/mathops.c, celt/pitch.h, celt/x86/pitch_sse.c and celt/bands.c (float build).
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Egami.Video.Audio.Codecs.Opus;

/// <summary>
/// The float math helpers of libopus's CELT: the bit-exact integer approximations the bit allocation relies on and the inner
/// products. The inner products add in the order of libopus's SSE versions (four lanes, then a horizontal sum), which the x86-64
/// reference build uses, so the float results match it.
/// </summary>
internal static class CeltMath
{
    /// <summary>libopus's PI constant.</summary>
    public const float Pi = 3.141592653f;

    /// <summary>FRAC_MUL16: multiplies two Q15 values, bit-exact.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int FracMul16(int a, int b) => (16384 + ((short)a * (short)b)) >> 15;

    /// <summary>celt_sqrt.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Sqrt(float x) => MathF.Sqrt(x);

    /// <summary>celt_rsqrt and celt_rsqrt_norm.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float RSqrt(float x) => 1f / MathF.Sqrt(x);

    /// <summary>celt_cos_norm: cos(pi/2 x).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float CosNorm(float x) => (float)Math.Cos((0.5f * Pi) * x);

    /// <summary>celt_exp2 without FLOAT_APPROX.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Exp2(float x) => (float)Math.Exp(0.6931471805599453094 * x);

    /// <summary>celt_lcg_rand.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint LcgRand(uint seed) => (1664525 * seed) + 1013904223;

    /// <summary>isqrt32: floor(sqrt(val)) with exact arithmetic, val greater than 0.</summary>
    public static uint Isqrt32(uint val)
    {
        uint g = 0;
        int bshift = (OpusRangeDecoder.Ilog(val) - 1) >> 1;
        uint b = 1u << bshift;
        do
        {
            uint t = ((g << 1) + b) << bshift;
            if (t <= val)
            {
                g += b;
                val -= t;
            }

            b >>= 1;
            bshift--;
        }
        while (bshift >= 0);
        return g;
    }

    /// <summary>bitexact_cos: a cos() approximation that is bit-exact on every platform.</summary>
    public static int BitexactCos(int x)
    {
        int tmp = (4096 + (x * x)) >> 13;
        int x2 = (short)tmp;
        x2 = (short)((32767 - x2) + FracMul16(x2, -7651 + FracMul16(x2, 8277 + FracMul16(-626, x2))));
        return 1 + x2;
    }

    /// <summary>bitexact_log2tan.</summary>
    public static int BitexactLog2Tan(int isin, int icos)
    {
        int lc = OpusRangeDecoder.Ilog((uint)icos);
        int ls = OpusRangeDecoder.Ilog((uint)isin);
        icos <<= 15 - lc;
        isin <<= 15 - ls;
        return ((ls - lc) * (1 << 11))
            + FracMul16(isin, FracMul16(isin, -2597) + 7932)
            - FracMul16(icos, FracMul16(icos, -2597) + 7932);
    }

    /// <summary>celt_inner_prod (the SSE order: four partial sums, a horizontal sum, then the tail).</summary>
    public static float InnerProduct(ReadOnlySpan<float> x, ReadOnlySpan<float> y, int n)
    {
        x = x[..n];
        y = y[..n];
        int i = 0;
        Vector128<float> sum = Vector128<float>.Zero;
        for (; i < n - 3; i += 4)
        {
            sum += Vector128.Create(x.Slice(i, 4)) * Vector128.Create(y.Slice(i, 4));
        }

        float xy = HorizontalSum(sum);
        for (; i < n; i++)
        {
            xy += x[i] * y[i];
        }

        return xy;
    }

    /// <summary>dual_inner_prod (SSE order).</summary>
    public static void DualInnerProduct(ReadOnlySpan<float> x, ReadOnlySpan<float> y01, ReadOnlySpan<float> y02, int n, out float xy1, out float xy2)
    {
        x = x[..n];
        y01 = y01[..n];
        y02 = y02[..n];
        int i = 0;
        Vector128<float> sum1 = Vector128<float>.Zero;
        Vector128<float> sum2 = Vector128<float>.Zero;
        for (; i < n - 3; i += 4)
        {
            Vector128<float> xi = Vector128.Create(x.Slice(i, 4));
            sum1 += xi * Vector128.Create(y01.Slice(i, 4));
            sum2 += xi * Vector128.Create(y02.Slice(i, 4));
        }

        xy1 = HorizontalSum(sum1);
        xy2 = HorizontalSum(sum2);
        for (; i < n; i++)
        {
            xy1 += x[i] * y01[i];
            xy2 += x[i] * y02[i];
        }
    }

    /// <summary>
    /// xcorr_kernel (SSE order): adds x[j] y[k + j] over j below len to sum[k] for the four lags k; y must hold len + 3 values.
    /// </summary>
    public static void XcorrKernel(ReadOnlySpan<float> x, ReadOnlySpan<float> y, Span<float> sum, int len)
    {
        Vector128<float> xsum1 = Vector128.Create((ReadOnlySpan<float>)sum[..4]);
        Vector128<float> xsum2 = Vector128<float>.Zero;
        int j = 0;
        for (; j < len - 3; j += 4)
        {
            // y[j..j+3], y[j+1..j+4], y[j+2..j+5] and y[j+3..j+6], which is what the shuffles of the SSE kernel build.
            xsum1 += Vector128.Create(x[j]) * Vector128.Create(y.Slice(j, 4));
            xsum2 += Vector128.Create(x[j + 1]) * Vector128.Create(y.Slice(j + 1, 4));
            xsum1 += Vector128.Create(x[j + 2]) * Vector128.Create(y.Slice(j + 2, 4));
            xsum2 += Vector128.Create(x[j + 3]) * Vector128.Create(y.Slice(j + 3, 4));
        }

        if (j < len)
        {
            xsum1 += Vector128.Create(x[j]) * Vector128.Create(y.Slice(j, 4));
            if (++j < len)
            {
                xsum2 += Vector128.Create(x[j]) * Vector128.Create(y.Slice(j, 4));
                if (++j < len)
                {
                    xsum1 += Vector128.Create(x[j]) * Vector128.Create(y.Slice(j, 4));
                }
            }
        }

        (xsum1 + xsum2).CopyTo(sum);
    }

    /// <summary>
    /// celt_pitch_xcorr: xcorr[i] = sum over j below len of x[j] y[i + j], for i below maxPitch. Groups of eight lags add in the
    /// order of libopus's celt_pitch_xcorr_avx2 (fused multiply-adds over eight lanes, then pairwise sums), which the x86-64
    /// reference build picks at run time on AVX2 machines; the rest use <see cref="InnerProduct"/>.
    /// </summary>
    public static void PitchXcorr(ReadOnlySpan<float> x, ReadOnlySpan<float> y, Span<float> xcorr, int len, int maxPitch)
    {
        int i = 0;
        for (; i < maxPitch - 7; i += 8)
        {
            XcorrKernel8(x, y[i..], xcorr.Slice(i, 8), len);
        }

        for (; i < maxPitch; i++)
        {
            xcorr[i] = InnerProduct(x, y[i..], len);
        }
    }

    // xcorr_kernel_avx: eight inner products of x with y shifted by 0 to 7.
    private static void XcorrKernel8(ReadOnlySpan<float> x, ReadOnlySpan<float> y, Span<float> sum, int len)
    {
        Span<Vector256<float>> acc = stackalloc Vector256<float>[8];
        acc.Clear();
        int i = 0;
        for (; i < len - 7; i += 8)
        {
            Vector256<float> x0 = Vector256.Create(x.Slice(i, 8));
            for (int k = 0; k < 8; k++)
            {
                acc[k] = Vector256.FusedMultiplyAdd(x0, Vector256.Create(y.Slice(i + k, 8)), acc[k]);
            }
        }

        if (i != len)
        {
            // The masked loads of the AVX2 kernel: lanes past len read as zero.
            int rem = len - i;
            Span<float> tmp = stackalloc float[8];
            tmp.Clear();
            x.Slice(i, rem).CopyTo(tmp);
            Vector256<float> x0 = Vector256.Create((ReadOnlySpan<float>)tmp);
            for (int k = 0; k < 8; k++)
            {
                tmp.Clear();
                y.Slice(i + k, rem).CopyTo(tmp);
                acc[k] = Vector256.FusedMultiplyAdd(x0, Vector256.Create((ReadOnlySpan<float>)tmp), acc[k]);
            }
        }

        for (int k = 0; k < 8; k++)
        {
            Vector256<float> a = acc[k];
            float f0 = a.GetElement(0) + a.GetElement(4);
            float f1 = a.GetElement(1) + a.GetElement(5);
            float f2 = a.GetElement(2) + a.GetElement(6);
            float f3 = a.GetElement(3) + a.GetElement(7);
            sum[k] = (f0 + f1) + (f2 + f3);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float HorizontalSum(Vector128<float> v) => (v.GetElement(0) + v.GetElement(2)) + (v.GetElement(1) + v.GetElement(3));
}
