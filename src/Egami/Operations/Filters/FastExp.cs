using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Egami.Operations.Filters;

/// <summary>
/// e^x for x ≤ 0, to about 2e-7 relative, with the same arithmetic in the vector and scalar forms so a pixel gets the same
/// weight whichever path computes it: x / ln 2 is split into an integer n and a fraction f in [−0.5, 0.5], 2^f comes from a
/// degree-6 polynomial, and n goes into the float's exponent.
/// </summary>
internal static class FastExp
{
    private const float Log2E = 1.44269504f;
    private const float Lowest = -87f;

    // Taylor coefficients of 2^f = e^(f ln 2): (ln 2)^k / k!.
    private const float C1 = 0.693147182f, C2 = 0.240226507f, C3 = 0.0555041087f, C4 = 0.00961812911f, C5 = 0.00133335581f, C6 = 0.000154035304f;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector256<float> Exp(Vector256<float> x)
    {
        Vector256<float> t = Vector256.Max(x, Vector256.Create(Lowest)) * Log2E;
        Vector256<float> n = Vector256.Round(t);
        Vector256<float> f = t - n;
        Vector256<float> p = (((((((((((Vector256.Create(C6) * f) + Vector256.Create(C5)) * f) + Vector256.Create(C4)) * f) + Vector256.Create(C3)) * f) + Vector256.Create(C2)) * f) + Vector256.Create(C1)) * f) + Vector256<float>.One;
        Vector256<int> bits = (Vector256.ConvertToInt32(n) + Vector256.Create(127)) << 23;
        return p * bits.AsSingle();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static float Exp(float x)
    {
        float t = Math.Max(x, Lowest) * Log2E;
        float n = MathF.Round(t);
        float f = t - n;
        float p = (((((((((((C6 * f) + C5) * f) + C4) * f) + C3) * f) + C2) * f) + C1) * f) + 1;
        return p * BitConverter.Int32BitsToSingle(((int)n + 127) << 23);
    }
}
