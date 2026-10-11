using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Egami.Pixels;

/// <summary>
/// SIMD premultiplication of 8-bit pixels with alpha last (RGBA or BGRA, the order does not matter), identical to
/// the scalar <c>Premultiply</c> class: round(c × a / 255) with an exact multiply-shift, and round(c × 255 / a) with a float division,
/// which is exact here because every quotient lies at least 1/510 from a rounding boundary or exactly on one.
/// </summary>
internal static class PremulKernels
{
    private const uint DivideBy255 = 0x8081;

    /// <summary>Premultiplies pixels.</summary>
    /// <returns>The number of pixels converted; the caller converts the rest.</returns>
    internal static int Premultiply(ReadOnlySpan<uint> source, Span<uint> destination, int vectorBits)
    {
        int i = 0;
        ref uint src = ref MemoryMarshal.GetReference(source);
        ref uint dst = ref MemoryMarshal.GetReference(destination);
        if (vectorBits >= 256 && Vector256.IsHardwareAccelerated)
        {
            for (; i + 8 <= source.Length; i += 8)
            {
                Vector256<uint> p = Vector256.LoadUnsafe(ref src, (nuint)i);
                (Vector256.GreaterThanOrEqualAll(p, Vector256.Create(0xFF000000u)) ? p : Premultiply(p)).StoreUnsafe(ref dst, (nuint)i);
            }
        }

        if (vectorBits >= 128)
        {
            for (; i + 4 <= source.Length; i += 4)
            {
                Premultiply(Vector128.LoadUnsafe(ref src, (nuint)i)).StoreUnsafe(ref dst, (nuint)i);
            }
        }

        return i;
    }

    /// <summary>Unpremultiplies pixels.</summary>
    /// <returns>The number of pixels converted; the caller converts the rest.</returns>
    internal static int Unpremultiply(ReadOnlySpan<uint> source, Span<uint> destination, int vectorBits)
    {
        int i = 0;
        ref uint src = ref MemoryMarshal.GetReference(source);
        ref uint dst = ref MemoryMarshal.GetReference(destination);
        if (vectorBits >= 256 && Vector256.IsHardwareAccelerated)
        {
            for (; i + 8 <= source.Length; i += 8)
            {
                Vector256<uint> p = Vector256.LoadUnsafe(ref src, (nuint)i);
                Vector256<uint> result = Vector256.GreaterThanOrEqualAll(p, Vector256.Create(0xFF000000u)) ? p
                    : Vector256.LessThanAll(p, Vector256.Create(0x01000000u)) ? Vector256<uint>.Zero : Unpremultiply(p);
                result.StoreUnsafe(ref dst, (nuint)i);
            }
        }

        if (vectorBits >= 128)
        {
            for (; i + 4 <= source.Length; i += 4)
            {
                Unpremultiply(Vector128.LoadUnsafe(ref src, (nuint)i)).StoreUnsafe(ref dst, (nuint)i);
            }
        }

        return i;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<uint> Premultiply(Vector256<uint> p)
    {
        Vector256<uint> bytes = Vector256.Create(0xFFu);
        Vector256<uint> a = p >> 24;
        Vector256<uint> half = Vector256.Create(127u);
        Vector256<uint> divide = Vector256.Create(DivideBy255);
        Vector256<uint> c0 = ((((p & bytes) * a) + half) * divide) >> 23;
        Vector256<uint> c1 = (((((p >> 8) & bytes) * a) + half) * divide) >> 23;
        Vector256<uint> c2 = (((((p >> 16) & bytes) * a) + half) * divide) >> 23;
        return c0 | (c1 << 8) | (c2 << 16) | (a << 24);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> Premultiply(Vector128<uint> p)
    {
        Vector128<uint> bytes = Vector128.Create(0xFFu);
        Vector128<uint> a = p >> 24;
        Vector128<uint> half = Vector128.Create(127u);
        Vector128<uint> divide = Vector128.Create(DivideBy255);
        Vector128<uint> c0 = ((((p & bytes) * a) + half) * divide) >> 23;
        Vector128<uint> c1 = (((((p >> 8) & bytes) * a) + half) * divide) >> 23;
        Vector128<uint> c2 = (((((p >> 16) & bytes) * a) + half) * divide) >> 23;
        return c0 | (c1 << 8) | (c2 << 16) | (a << 24);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<uint> Unpremultiply(Vector256<uint> p)
    {
        Vector256<uint> bytes = Vector256.Create(0xFFu);
        Vector256<uint> a = p >> 24;
        Vector256<float> af = Vector256.ConvertToSingle(a.AsInt32());
        Vector256<uint> zero = Vector256.Equals(a, Vector256<uint>.Zero);
        Vector256<uint> c0 = Over(Vector256.Min(p & bytes, a), af);
        Vector256<uint> c1 = Over(Vector256.Min((p >> 8) & bytes, a), af);
        Vector256<uint> c2 = Over(Vector256.Min((p >> 16) & bytes, a), af);
        return Vector256.AndNot(c0 | (c1 << 8) | (c2 << 16), zero) | (a << 24);

        static Vector256<uint> Over(Vector256<uint> c, Vector256<float> alpha)
        {
            Vector256<float> q = (Vector256.ConvertToSingle((c * 255).AsInt32()) / alpha) + Vector256.Create(0.5f);
            return Vector256.ConvertToInt32(Vector256.Floor(q)).AsUInt32() & Vector256.Create(0xFFu);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> Unpremultiply(Vector128<uint> p)
    {
        Vector128<uint> bytes = Vector128.Create(0xFFu);
        Vector128<uint> a = p >> 24;
        Vector128<float> af = Vector128.ConvertToSingle(a.AsInt32());
        Vector128<uint> zero = Vector128.Equals(a, Vector128<uint>.Zero);
        Vector128<uint> c0 = Over(Vector128.Min(p & bytes, a), af);
        Vector128<uint> c1 = Over(Vector128.Min((p >> 8) & bytes, a), af);
        Vector128<uint> c2 = Over(Vector128.Min((p >> 16) & bytes, a), af);
        return Vector128.AndNot(c0 | (c1 << 8) | (c2 << 16), zero) | (a << 24);

        static Vector128<uint> Over(Vector128<uint> c, Vector128<float> alpha)
        {
            Vector128<float> q = (Vector128.ConvertToSingle((c * 255).AsInt32()) / alpha) + Vector128.Create(0.5f);
            return Vector128.ConvertToInt32(Vector128.Floor(q)).AsUInt32() & Vector128.Create(0xFFu);
        }
    }
}
