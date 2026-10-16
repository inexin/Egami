using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Egami.Formats.Jxl;

/// <summary>
/// libjxl's fast power function (FastPowf: 2 to the log2 of the base times the exponent, each a small rational polynomial,
/// relative error about 3e-5), which its renderer uses for the BT.709, gamma and DCI curves, so output matches djxl's. Eight
/// samples at a time with AVX and FMA; the scalar form does the same operations. Translated from libjxl (see
/// THIRD-PARTY-NOTICES).
/// </summary>
internal static class JxlFastMath
{
    private const float LogP0 = -1.8503833400518310E-06f;
    private const float LogP1 = 1.4287160470083755E+00f;
    private const float LogP2 = 7.4245873327820566E-01f;
    private const float LogQ0 = 9.9032814277590719E-01f;
    private const float LogQ1 = 1.0096718572241148E+00f;
    private const float LogQ2 = 1.7409343003366853E-01f;

    /// <summary>Gets whether the curves run eight samples at a time.</summary>
    internal static bool Vectorized => Fma.IsSupported && Avx2.IsSupported;

    internal static float Log2(float x)
    {
        int bits = BitConverter.SingleToInt32Bits(x);
        int expBits = bits - 0x3f2aaaab;
        int shifted = expBits >> 23;
        float mantissa = BitConverter.Int32BitsToSingle(bits - (shifted << 23)) - 1;
        float yp = MathF.FusedMultiplyAdd(MathF.FusedMultiplyAdd(LogP2, mantissa, LogP1), mantissa, LogP0);
        float yq = MathF.FusedMultiplyAdd(MathF.FusedMultiplyAdd(LogQ2, mantissa, LogQ1), mantissa, LogQ0);
        return (yp / yq) + shifted;
    }

    internal static float Pow2(float x)
    {
        float floor = MathF.Floor(x);
        float exp = BitConverter.Int32BitsToSingle(((int)floor + 127) << 23);
        float frac = x - floor;
        float num = frac + 1.01749063e+01f;
        num = MathF.FusedMultiplyAdd(num, frac, 4.88687798e+01f);
        num = MathF.FusedMultiplyAdd(num, frac, 9.85506591e+01f);
        num *= exp;
        float den = MathF.FusedMultiplyAdd(frac, 2.10242958e-01f, -2.22328856e-02f);
        den = MathF.FusedMultiplyAdd(den, frac, -1.94414990e+01f);
        den = MathF.FusedMultiplyAdd(den, frac, 9.85506633e+01f);
        return num / den;
    }

    internal static float Pow(float x, float exponent) => Pow2(Log2(x) * exponent);

    internal static Vector256<float> Log2(Vector256<float> x)
    {
        Vector256<int> bits = x.AsInt32();
        Vector256<int> expBits = bits - Vector256.Create(0x3f2aaaab);
        Vector256<int> shifted = Avx2.ShiftRightArithmetic(expBits, 23);
        Vector256<float> mantissa = (bits - Avx2.ShiftLeftLogical(shifted, 23)).AsSingle() - Vector256<float>.One;
        Vector256<float> yp = Fma.MultiplyAdd(Fma.MultiplyAdd(Vector256.Create(LogP2), mantissa, Vector256.Create(LogP1)), mantissa, Vector256.Create(LogP0));
        Vector256<float> yq = Fma.MultiplyAdd(Fma.MultiplyAdd(Vector256.Create(LogQ2), mantissa, Vector256.Create(LogQ1)), mantissa, Vector256.Create(LogQ0));
        return (yp / yq) + Avx.ConvertToVector256Single(shifted);
    }

    internal static Vector256<float> Pow2(Vector256<float> x)
    {
        Vector256<float> floor = Avx.Floor(x);
        Vector256<float> exp = Avx2.ShiftLeftLogical(Avx.ConvertToVector256Int32WithTruncation(floor) + Vector256.Create(127), 23).AsSingle();
        Vector256<float> frac = x - floor;
        Vector256<float> num = frac + Vector256.Create(1.01749063e+01f);
        num = Fma.MultiplyAdd(num, frac, Vector256.Create(4.88687798e+01f));
        num = Fma.MultiplyAdd(num, frac, Vector256.Create(9.85506591e+01f));
        num *= exp;
        Vector256<float> den = Fma.MultiplyAdd(frac, Vector256.Create(2.10242958e-01f), Vector256.Create(-2.22328856e-02f));
        den = Fma.MultiplyAdd(den, frac, Vector256.Create(-1.94414990e+01f));
        den = Fma.MultiplyAdd(den, frac, Vector256.Create(9.85506633e+01f));
        return num / den;
    }

    internal static Vector256<float> Pow(Vector256<float> x, Vector256<float> exponent) => Pow2(Log2(x) * exponent);

    /// <summary>The BT.709 curve as libjxl's Op709: 4.5·x up to 0.018, else 1.099·x^0.45 − 0.099 with the fast power.</summary>
    internal static void Encode709(Span<float> plane)
    {
        int i = 0;
        if (Vectorized)
        {
            ref float start = ref MemoryMarshal.GetReference(plane);
            for (; i + 8 <= plane.Length; i += 8)
            {
                Vector256<float> x = Vector256.LoadUnsafe(ref start, (nuint)i);
                Vector256<float> hi = Fma.MultiplyAdd(Vector256.Create(1.099f), Pow(x, Vector256.Create(0.45f)), Vector256.Create(-0.099f));
                Vector256.ConditionalSelect(Vector256.LessThanOrEqual(x, Vector256.Create(0.018f)), x * Vector256.Create(4.5f), hi).StoreUnsafe(ref start, (nuint)i);
            }
        }

        for (; i < plane.Length; i++)
        {
            float x = plane[i];
            plane[i] = x <= 0.018f ? x * 4.5f : MathF.FusedMultiplyAdd(1.099f, Pow(x, 0.45f), -0.099f);
        }
    }

    /// <summary>A gamma curve as libjxl's OpGamma: 0 at or below 10^-5, else x to the power <paramref name="exponent"/> with the fast power.</summary>
    internal static void EncodeGamma(Span<float> plane, float exponent)
    {
        int i = 0;
        if (Vectorized)
        {
            ref float start = ref MemoryMarshal.GetReference(plane);
            for (; i + 8 <= plane.Length; i += 8)
            {
                Vector256<float> x = Vector256.LoadUnsafe(ref start, (nuint)i);
                Vector256<float> p = Pow(x, Vector256.Create(exponent));
                Vector256.ConditionalSelect(Vector256.LessThanOrEqual(x, Vector256.Create(1e-5f)), Vector256<float>.Zero, p).StoreUnsafe(ref start, (nuint)i);
            }
        }

        for (; i < plane.Length; i++)
        {
            float x = plane[i];
            plane[i] = x <= 1e-5f ? 0 : Pow(x, exponent);
        }
    }
}
