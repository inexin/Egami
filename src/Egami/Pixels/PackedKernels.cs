using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Egami.Pixels;

/// <summary>
/// SIMD conversions between <see cref="Bgr565"/>, <see cref="Bgr555"/> and <see cref="Bgra5551"/> and the 8-bit four-channel
/// layouts, identical to the scalar path: both directions round to nearest, with exact multiply-shift divisions by 255, 31 and 63.
/// </summary>
internal static class PackedKernels
{
    // x / 255 == (x * 0x8081) >> 23 for every x below 2^16; here x is at most 255 * 63 + 127.
    private const uint DivideBy255 = 0x8081;

    // x / 31 == (x * 8457) >> 18 for x up to 31 * 255 + 15, and x / 63 == (x * 16645) >> 20 for x up to 63 * 255 + 31.
    private const uint DivideBy31 = 8457;
    private const uint DivideBy63 = 16645;

    /// <summary>Widens 5-6-5 pixels to 8-bit RGBA (or BGRA when <paramref name="bgra"/> is set) with opaque alpha.</summary>
    /// <returns>The number of pixels converted; the caller converts the rest.</returns>
    internal static int Widen565(ReadOnlySpan<ushort> source, Span<uint> destination, bool bgra, int vectorBits)
    {
        int i = 0;
        ref ushort src = ref MemoryMarshal.GetReference(source);
        ref uint dst = ref MemoryMarshal.GetReference(destination);
        if (vectorBits >= 256 && Vector256.IsHardwareAccelerated)
        {
            for (; i + 16 <= source.Length; i += 16)
            {
                (Vector256<uint> lower, Vector256<uint> upper) = Vector256.Widen(Vector256.LoadUnsafe(ref src, (nuint)i));
                Widen(lower, bgra).StoreUnsafe(ref dst, (nuint)i);
                Widen(upper, bgra).StoreUnsafe(ref dst, (nuint)(i + 8));
            }
        }

        if (vectorBits >= 128)
        {
            for (; i + 8 <= source.Length; i += 8)
            {
                (Vector128<uint> lower, Vector128<uint> upper) = Vector128.Widen(Vector128.LoadUnsafe(ref src, (nuint)i));
                Widen(lower, bgra).StoreUnsafe(ref dst, (nuint)i);
                Widen(upper, bgra).StoreUnsafe(ref dst, (nuint)(i + 4));
            }
        }

        return i;
    }

    /// <summary>Narrows 8-bit RGBA (or BGRA when <paramref name="bgra"/> is set) pixels to 5-6-5, dropping alpha.</summary>
    /// <returns>The number of pixels converted; the caller converts the rest.</returns>
    internal static int Narrow565(ReadOnlySpan<uint> source, Span<ushort> destination, bool bgra, int vectorBits)
    {
        int i = 0;
        ref uint src = ref MemoryMarshal.GetReference(source);
        ref ushort dst = ref MemoryMarshal.GetReference(destination);
        if (vectorBits >= 256 && Vector256.IsHardwareAccelerated)
        {
            for (; i + 16 <= source.Length; i += 16)
            {
                Vector256<uint> lower = Narrow(Vector256.LoadUnsafe(ref src, (nuint)i), bgra);
                Vector256<uint> upper = Narrow(Vector256.LoadUnsafe(ref src, (nuint)(i + 8)), bgra);
                Vector256.Narrow(lower, upper).StoreUnsafe(ref dst, (nuint)i);
            }
        }

        if (vectorBits >= 128)
        {
            for (; i + 8 <= source.Length; i += 8)
            {
                Vector128<uint> lower = Narrow(Vector128.LoadUnsafe(ref src, (nuint)i), bgra);
                Vector128<uint> upper = Narrow(Vector128.LoadUnsafe(ref src, (nuint)(i + 4)), bgra);
                Vector128.Narrow(lower, upper).StoreUnsafe(ref dst, (nuint)i);
            }
        }

        return i;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<uint> Widen(Vector256<uint> v, bool bgra)
    {
        Vector256<uint> r = v >> 11;
        Vector256<uint> g = (v >> 5) & Vector256.Create(63u);
        Vector256<uint> b = v & Vector256.Create(31u);
        r = (((r * 255) + Vector256.Create(15u)) * Vector256.Create(DivideBy31)) >> 18;
        g = (((g * 255) + Vector256.Create(31u)) * Vector256.Create(DivideBy63)) >> 20;
        b = (((b * 255) + Vector256.Create(15u)) * Vector256.Create(DivideBy31)) >> 18;
        return bgra
            ? b | (g << 8) | (r << 16) | Vector256.Create(0xFF00_0000u)
            : r | (g << 8) | (b << 16) | Vector256.Create(0xFF00_0000u);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> Widen(Vector128<uint> v, bool bgra)
    {
        Vector128<uint> r = v >> 11;
        Vector128<uint> g = (v >> 5) & Vector128.Create(63u);
        Vector128<uint> b = v & Vector128.Create(31u);
        r = (((r * 255) + Vector128.Create(15u)) * Vector128.Create(DivideBy31)) >> 18;
        g = (((g * 255) + Vector128.Create(31u)) * Vector128.Create(DivideBy63)) >> 20;
        b = (((b * 255) + Vector128.Create(15u)) * Vector128.Create(DivideBy31)) >> 18;
        return bgra
            ? b | (g << 8) | (r << 16) | Vector128.Create(0xFF00_0000u)
            : r | (g << 8) | (b << 16) | Vector128.Create(0xFF00_0000u);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<uint> Narrow(Vector256<uint> p, bool bgra)
    {
        Vector256<uint> bytes = Vector256.Create(0xFFu);
        Vector256<uint> first = p & bytes;
        Vector256<uint> g = (p >> 8) & bytes;
        Vector256<uint> third = (p >> 16) & bytes;
        Vector256<uint> r = bgra ? third : first;
        Vector256<uint> b = bgra ? first : third;
        Vector256<uint> half = Vector256.Create(127u);
        Vector256<uint> divide = Vector256.Create(DivideBy255);
        Vector256<uint> r5 = (((r * 31) + half) * divide) >> 23;
        Vector256<uint> g6 = (((g * 63) + half) * divide) >> 23;
        Vector256<uint> b5 = (((b * 31) + half) * divide) >> 23;
        return (r5 << 11) | (g6 << 5) | b5;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> Narrow(Vector128<uint> p, bool bgra)
    {
        Vector128<uint> bytes = Vector128.Create(0xFFu);
        Vector128<uint> first = p & bytes;
        Vector128<uint> g = (p >> 8) & bytes;
        Vector128<uint> third = (p >> 16) & bytes;
        Vector128<uint> r = bgra ? third : first;
        Vector128<uint> b = bgra ? first : third;
        Vector128<uint> half = Vector128.Create(127u);
        Vector128<uint> divide = Vector128.Create(DivideBy255);
        Vector128<uint> r5 = (((r * 31) + half) * divide) >> 23;
        Vector128<uint> g6 = (((g * 63) + half) * divide) >> 23;
        Vector128<uint> b5 = (((b * 31) + half) * divide) >> 23;
        return (r5 << 11) | (g6 << 5) | b5;
    }

    /// <summary>
    /// Widens 5-5-5 pixels to 8-bit RGBA (or BGRA when <paramref name="bgra"/> is set): bit 15 becomes alpha 0 or 255 when
    /// <paramref name="alpha"/> is set, else alpha is opaque.
    /// </summary>
    /// <returns>The number of pixels converted; the caller converts the rest.</returns>
    internal static int Widen555(ReadOnlySpan<ushort> source, Span<uint> destination, bool bgra, bool alpha, int vectorBits)
    {
        int i = 0;
        ref ushort src = ref MemoryMarshal.GetReference(source);
        ref uint dst = ref MemoryMarshal.GetReference(destination);
        if (vectorBits >= 256 && Vector256.IsHardwareAccelerated)
        {
            for (; i + 16 <= source.Length; i += 16)
            {
                (Vector256<uint> lower, Vector256<uint> upper) = Vector256.Widen(Vector256.LoadUnsafe(ref src, (nuint)i));
                Widen555(lower, bgra, alpha).StoreUnsafe(ref dst, (nuint)i);
                Widen555(upper, bgra, alpha).StoreUnsafe(ref dst, (nuint)(i + 8));
            }
        }

        if (vectorBits >= 128)
        {
            for (; i + 8 <= source.Length; i += 8)
            {
                (Vector128<uint> lower, Vector128<uint> upper) = Vector128.Widen(Vector128.LoadUnsafe(ref src, (nuint)i));
                Widen555(lower, bgra, alpha).StoreUnsafe(ref dst, (nuint)i);
                Widen555(upper, bgra, alpha).StoreUnsafe(ref dst, (nuint)(i + 4));
            }
        }

        return i;
    }

    /// <summary>
    /// Narrows 8-bit RGBA (or BGRA when <paramref name="bgra"/> is set) pixels to 5-5-5: alpha 128 and above sets bit 15 when
    /// <paramref name="alpha"/> is set, else bit 15 is 0.
    /// </summary>
    /// <returns>The number of pixels converted; the caller converts the rest.</returns>
    internal static int Narrow555(ReadOnlySpan<uint> source, Span<ushort> destination, bool bgra, bool alpha, int vectorBits)
    {
        int i = 0;
        ref uint src = ref MemoryMarshal.GetReference(source);
        ref ushort dst = ref MemoryMarshal.GetReference(destination);
        if (vectorBits >= 256 && Vector256.IsHardwareAccelerated)
        {
            for (; i + 16 <= source.Length; i += 16)
            {
                Vector256<uint> lower = Narrow555(Vector256.LoadUnsafe(ref src, (nuint)i), bgra, alpha);
                Vector256<uint> upper = Narrow555(Vector256.LoadUnsafe(ref src, (nuint)(i + 8)), bgra, alpha);
                Vector256.Narrow(lower, upper).StoreUnsafe(ref dst, (nuint)i);
            }
        }

        if (vectorBits >= 128)
        {
            for (; i + 8 <= source.Length; i += 8)
            {
                Vector128<uint> lower = Narrow555(Vector128.LoadUnsafe(ref src, (nuint)i), bgra, alpha);
                Vector128<uint> upper = Narrow555(Vector128.LoadUnsafe(ref src, (nuint)(i + 4)), bgra, alpha);
                Vector128.Narrow(lower, upper).StoreUnsafe(ref dst, (nuint)i);
            }
        }

        return i;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<uint> Widen555(Vector256<uint> v, bool bgra, bool alpha)
    {
        Vector256<uint> five = Vector256.Create(31u);
        Vector256<uint> r = (v >> 10) & five;
        Vector256<uint> g = (v >> 5) & five;
        Vector256<uint> b = v & five;
        r = (((r * 255) + Vector256.Create(15u)) * Vector256.Create(DivideBy31)) >> 18;
        g = (((g * 255) + Vector256.Create(15u)) * Vector256.Create(DivideBy31)) >> 18;
        b = (((b * 255) + Vector256.Create(15u)) * Vector256.Create(DivideBy31)) >> 18;
        Vector256<uint> a = alpha ? (Vector256<uint>.Zero - (v >> 15)) & Vector256.Create(0xFF00_0000u) : Vector256.Create(0xFF00_0000u);
        return bgra ? b | (g << 8) | (r << 16) | a : r | (g << 8) | (b << 16) | a;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<uint> Narrow555(Vector256<uint> p, bool bgra, bool alpha)
    {
        Vector256<uint> bytes = Vector256.Create(0xFFu);
        Vector256<uint> first = p & bytes;
        Vector256<uint> g = (p >> 8) & bytes;
        Vector256<uint> third = (p >> 16) & bytes;
        Vector256<uint> r = bgra ? third : first;
        Vector256<uint> b = bgra ? first : third;
        Vector256<uint> half = Vector256.Create(127u);
        Vector256<uint> divide = Vector256.Create(DivideBy255);
        Vector256<uint> r5 = (((r * 31) + half) * divide) >> 23;
        Vector256<uint> g5 = (((g * 31) + half) * divide) >> 23;
        Vector256<uint> b5 = (((b * 31) + half) * divide) >> 23;
        Vector256<uint> a1 = alpha ? (p >> 31) << 15 : Vector256<uint>.Zero;
        return a1 | (r5 << 10) | (g5 << 5) | b5;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> Widen555(Vector128<uint> v, bool bgra, bool alpha)
    {
        Vector128<uint> five = Vector128.Create(31u);
        Vector128<uint> r = (v >> 10) & five;
        Vector128<uint> g = (v >> 5) & five;
        Vector128<uint> b = v & five;
        r = (((r * 255) + Vector128.Create(15u)) * Vector128.Create(DivideBy31)) >> 18;
        g = (((g * 255) + Vector128.Create(15u)) * Vector128.Create(DivideBy31)) >> 18;
        b = (((b * 255) + Vector128.Create(15u)) * Vector128.Create(DivideBy31)) >> 18;
        Vector128<uint> a = alpha ? (Vector128<uint>.Zero - (v >> 15)) & Vector128.Create(0xFF00_0000u) : Vector128.Create(0xFF00_0000u);
        return bgra ? b | (g << 8) | (r << 16) | a : r | (g << 8) | (b << 16) | a;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> Narrow555(Vector128<uint> p, bool bgra, bool alpha)
    {
        Vector128<uint> bytes = Vector128.Create(0xFFu);
        Vector128<uint> first = p & bytes;
        Vector128<uint> g = (p >> 8) & bytes;
        Vector128<uint> third = (p >> 16) & bytes;
        Vector128<uint> r = bgra ? third : first;
        Vector128<uint> b = bgra ? first : third;
        Vector128<uint> half = Vector128.Create(127u);
        Vector128<uint> divide = Vector128.Create(DivideBy255);
        Vector128<uint> r5 = (((r * 31) + half) * divide) >> 23;
        Vector128<uint> g5 = (((g * 31) + half) * divide) >> 23;
        Vector128<uint> b5 = (((b * 31) + half) * divide) >> 23;
        Vector128<uint> a1 = alpha ? (p >> 31) << 15 : Vector128<uint>.Zero;
        return a1 | (r5 << 10) | (g5 << 5) | b5;
    }
}
