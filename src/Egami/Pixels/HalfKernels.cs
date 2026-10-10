using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Egami.Pixels;

/// <summary>
/// SIMD conversions between <see cref="Half"/> and <see cref="float"/> bit patterns, identical to <c>(float)h</c> and
/// <c>(Half)f</c> for every input: subnormals, infinities and NaN payloads included. Integer bit manipulation only, so they
/// run on any hardware with integer SIMD (IEEE 754-2008, binary16 and binary32).
/// </summary>
internal static class HalfKernels
{
    private const uint SignBit = 0x8000_0000;
    private const uint SingleInfinity = 0x7F80_0000;
    private const uint SingleQuietBit = 0x0040_0000;

    // Half to float: shifting the exponent and mantissa into place and multiplying by 2^112 re-biases the exponent exactly,
    // normals and subnormals alike.
    private const uint TwoTo112 = (127 + 112) << 23;

    // Float to half (round to nearest even, after Giesen's float_to_half_fast3_rtne).
    private const uint HalfOverflow = (127 + 16) << 23;     // 65536: at or above, the result is infinity (or NaN)
    private const uint HalfNormalMin = 113 << 23;           // 2^-14: below, the result is subnormal or zero
    private const uint SubnormalMagic = 126 << 23;          // 0.5: adding it rounds to the half subnormal grid
    private const uint Rebias = 0xC800_0FFF;                // ((15 - 127) << 23) + 0xFFF, wrapping

    /// <summary>Converts half bit patterns to float bit patterns.</summary>
    /// <returns>The number of values converted; the caller converts the rest.</returns>
    internal static int ToSingle(ReadOnlySpan<ushort> source, Span<uint> destination, int vectorBits)
    {
        int i = 0;
        ref ushort src = ref MemoryMarshal.GetReference(source);
        ref uint dst = ref MemoryMarshal.GetReference(destination);
        if (vectorBits >= 256 && Vector256.IsHardwareAccelerated)
        {
            for (; i + 16 <= source.Length; i += 16)
            {
                (Vector256<uint> lower, Vector256<uint> upper) = Vector256.Widen(Vector256.LoadUnsafe(ref src, (nuint)i));
                ToSingle(lower).StoreUnsafe(ref dst, (nuint)i);
                ToSingle(upper).StoreUnsafe(ref dst, (nuint)(i + 8));
            }
        }

        if (vectorBits >= 128)
        {
            for (; i + 8 <= source.Length; i += 8)
            {
                (Vector128<uint> lower, Vector128<uint> upper) = Vector128.Widen(Vector128.LoadUnsafe(ref src, (nuint)i));
                ToSingle(lower).StoreUnsafe(ref dst, (nuint)i);
                ToSingle(upper).StoreUnsafe(ref dst, (nuint)(i + 4));
            }
        }

        return i;
    }

    /// <summary>Converts float bit patterns to half bit patterns, rounding to nearest even.</summary>
    /// <returns>The number of values converted; the caller converts the rest.</returns>
    internal static int ToHalf(ReadOnlySpan<uint> source, Span<ushort> destination, int vectorBits)
    {
        int i = 0;
        ref uint src = ref MemoryMarshal.GetReference(source);
        ref ushort dst = ref MemoryMarshal.GetReference(destination);
        if (vectorBits >= 256 && Vector256.IsHardwareAccelerated)
        {
            for (; i + 16 <= source.Length; i += 16)
            {
                Vector256<uint> lower = ToHalf(Vector256.LoadUnsafe(ref src, (nuint)i));
                Vector256<uint> upper = ToHalf(Vector256.LoadUnsafe(ref src, (nuint)(i + 8)));
                Vector256.Narrow(lower, upper).StoreUnsafe(ref dst, (nuint)i);
            }
        }

        if (vectorBits >= 128)
        {
            for (; i + 8 <= source.Length; i += 8)
            {
                Vector128<uint> lower = ToHalf(Vector128.LoadUnsafe(ref src, (nuint)i));
                Vector128<uint> upper = ToHalf(Vector128.LoadUnsafe(ref src, (nuint)(i + 4)));
                Vector128.Narrow(lower, upper).StoreUnsafe(ref dst, (nuint)i);
            }
        }

        return i;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<uint> ToSingle(Vector256<uint> half)
    {
        Vector256<uint> sign = (half & Vector256.Create(0x8000u)) << 16;
        Vector256<uint> magnitude = half & Vector256.Create(0x7FFFu);
        Vector256<uint> bits = (Vector256.AsSingle(magnitude << 13) * Vector256.Create(TwoTo112).AsSingle()).AsUInt32();
        Vector256<uint> special = Vector256.GreaterThanOrEqual(magnitude, Vector256.Create(0x7C00u));
        Vector256<uint> nan = Vector256.GreaterThan(magnitude, Vector256.Create(0x7C00u));
        bits |= special & Vector256.Create(SingleInfinity);
        bits |= nan & Vector256.Create(SingleQuietBit);
        return bits | sign;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> ToSingle(Vector128<uint> half)
    {
        Vector128<uint> sign = (half & Vector128.Create(0x8000u)) << 16;
        Vector128<uint> magnitude = half & Vector128.Create(0x7FFFu);
        Vector128<uint> bits = (Vector128.AsSingle(magnitude << 13) * Vector128.Create(TwoTo112).AsSingle()).AsUInt32();
        Vector128<uint> special = Vector128.GreaterThanOrEqual(magnitude, Vector128.Create(0x7C00u));
        Vector128<uint> nan = Vector128.GreaterThan(magnitude, Vector128.Create(0x7C00u));
        bits |= special & Vector128.Create(SingleInfinity);
        bits |= nan & Vector128.Create(SingleQuietBit);
        return bits | sign;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<uint> ToHalf(Vector256<uint> single)
    {
        Vector256<uint> sign = single & Vector256.Create(SignBit);
        Vector256<uint> f = single ^ sign;

        Vector256<uint> isNaN = Vector256.GreaterThan(f, Vector256.Create(SingleInfinity));
        Vector256<uint> nanResult = Vector256.Create(0x7E00u) | ((f >> 13) & Vector256.Create(0x3FFu));
        Vector256<uint> large = Vector256.ConditionalSelect(isNaN, nanResult, Vector256.Create(0x7C00u));

        Vector256<uint> magic = Vector256.Create(SubnormalMagic);
        Vector256<uint> small = (f.AsSingle() + magic.AsSingle()).AsUInt32() - magic;

        Vector256<uint> odd = (f >> 13) & Vector256<uint>.One;
        Vector256<uint> normal = (f + Vector256.Create(Rebias) + odd) >> 13;

        Vector256<uint> result = Vector256.ConditionalSelect(
            Vector256.GreaterThanOrEqual(f, Vector256.Create(HalfOverflow)),
            large,
            Vector256.ConditionalSelect(Vector256.LessThan(f, Vector256.Create(HalfNormalMin)), small, normal));
        return result | (sign >> 16);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> ToHalf(Vector128<uint> single)
    {
        Vector128<uint> sign = single & Vector128.Create(SignBit);
        Vector128<uint> f = single ^ sign;

        Vector128<uint> isNaN = Vector128.GreaterThan(f, Vector128.Create(SingleInfinity));
        Vector128<uint> nanResult = Vector128.Create(0x7E00u) | ((f >> 13) & Vector128.Create(0x3FFu));
        Vector128<uint> large = Vector128.ConditionalSelect(isNaN, nanResult, Vector128.Create(0x7C00u));

        Vector128<uint> magic = Vector128.Create(SubnormalMagic);
        Vector128<uint> small = (f.AsSingle() + magic.AsSingle()).AsUInt32() - magic;

        Vector128<uint> odd = (f >> 13) & Vector128<uint>.One;
        Vector128<uint> normal = (f + Vector128.Create(Rebias) + odd) >> 13;

        Vector128<uint> result = Vector128.ConditionalSelect(
            Vector128.GreaterThanOrEqual(f, Vector128.Create(HalfOverflow)),
            large,
            Vector128.ConditionalSelect(Vector128.LessThan(f, Vector128.Create(HalfNormalMin)), small, normal));
        return result | (sign >> 16);
    }
}
