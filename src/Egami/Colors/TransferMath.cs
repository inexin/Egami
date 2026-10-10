using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Egami.Colors;

/// <summary>
/// Vectorized log2, exp2 and power for the transfer curves, accurate to about 2e-7 relative over the normal float range. Plain
/// arithmetic only (no fused multiply-add), so every CPU gives the same bits.
/// </summary>
internal static class TransferMath
{
    private const float Sqrt2 = 1.41421356f;
    private const float InverseLn2Times2 = 2.88539008f;   // 2 / ln 2

    /// <summary>x^y for x ≥ 0; 0 for x ≤ 0.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<float> Pow(Vector256<float> x, float y)
    {
        Vector256<float> result = Exp2(Log2(x) * y);
        return Vector256.ConditionalSelect(Vector256.GreaterThan(x, Vector256<float>.Zero), result, Vector256<float>.Zero);
    }

    /// <summary>e^x.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<float> Exp(Vector256<float> x) => Exp2(x * 1.44269504f);

    /// <summary>ln x for x &gt; 0.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector256<float> Log(Vector256<float> x) => Log2(x) * 0.693147181f;

    /// <summary>log2 x for x &gt; 0: the exponent, plus an odd series in t = (m − 1)/(m + 1) for the mantissa m in [√½, √2).</summary>
    public static Vector256<float> Log2(Vector256<float> x)
    {
        Vector256<int> bits = x.AsInt32();
        Vector256<int> exponent = Vector256.ShiftRightLogical(bits, 23) - Vector256.Create(127);
        Vector256<float> m = (bits & Vector256.Create(0x007F_FFFF) | Vector256.Create(0x3F80_0000)).AsSingle();
        Vector256<float> big = Vector256.GreaterThan(m, Vector256.Create(Sqrt2));
        m = Vector256.ConditionalSelect(big, m * 0.5f, m);
        exponent += big.AsInt32() & Vector256.Create(1);

        Vector256<float> t = (m - Vector256<float>.One) / (m + Vector256<float>.One);
        Vector256<float> t2 = t * t;
        Vector256<float> series = ((((((((t2 * (1f / 11)) + Vector256.Create(1f / 9)) * t2) + Vector256.Create(1f / 7)) * t2) + Vector256.Create(1f / 5)) * t2) + Vector256.Create(1f / 3)) * t2;
        Vector256<float> logM = ((t * series) + t) * InverseLn2Times2;
        return Vector256.ConvertToSingle(exponent) + logM;
    }

    /// <summary>2^x: 2 to the nearest whole power, times a Taylor series for the rest, which lies in [−½, ½].</summary>
    public static Vector256<float> Exp2(Vector256<float> x)
    {
        x = Vector256.Min(Vector256.Max(x, Vector256.Create(-126f)), Vector256.Create(127f));
        Vector256<float> n = Vector256.Round(x);
        Vector256<float> f = (x - n) * 0.693147181f;
        Vector256<float> p = Vector256.Create(1f / 5040);
        p = (p * f) + Vector256.Create(1f / 720);
        p = (p * f) + Vector256.Create(1f / 120);
        p = (p * f) + Vector256.Create(1f / 24);
        p = (p * f) + Vector256.Create(1f / 6);
        p = (p * f) + Vector256.Create(0.5f);
        p = (p * f) + Vector256<float>.One;
        p = (p * f) + Vector256<float>.One;
        Vector256<float> scale = Vector256.ShiftLeft(Vector256.ConvertToInt32(n) + Vector256.Create(127), 23).AsSingle();
        return p * scale;
    }
}
