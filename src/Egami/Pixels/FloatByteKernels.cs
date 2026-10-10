using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Egami.Pixels;

/// <summary>
/// SIMD conversions between scaled floats (or halves) and 8-bit channels, identical to the scalar rules: a byte becomes
/// <c>b / 255f</c>, and a float becomes <c>(byte)(clamp(v) × 255 + 0.5)</c> with NaN as 0, as <see cref="Scale.ToByte"/> does.
/// </summary>
internal static class FloatByteKernels
{
    /// <summary>Converts floats to bytes element by element.</summary>
    /// <returns>The number of elements converted; the caller converts the rest.</returns>
    internal static int ToBytes(ReadOnlySpan<float> source, Span<byte> destination, int vectorBits)
    {
        int i = 0;
        ref float src = ref MemoryMarshal.GetReference(source);
        ref byte dst = ref MemoryMarshal.GetReference(destination);
        if (vectorBits >= 256 && Vector256.IsHardwareAccelerated)
        {
            for (; i + 32 <= source.Length; i += 32)
            {
                Vector256<int> a = ToInt(Vector256.LoadUnsafe(ref src, (nuint)i));
                Vector256<int> b = ToInt(Vector256.LoadUnsafe(ref src, (nuint)(i + 8)));
                Vector256<int> c = ToInt(Vector256.LoadUnsafe(ref src, (nuint)(i + 16)));
                Vector256<int> d = ToInt(Vector256.LoadUnsafe(ref src, (nuint)(i + 24)));
                Vector256<ushort> ab = Vector256.Narrow(a.AsUInt32(), b.AsUInt32());
                Vector256<ushort> cd = Vector256.Narrow(c.AsUInt32(), d.AsUInt32());
                Vector256.Narrow(ab, cd).StoreUnsafe(ref dst, (nuint)i);
            }
        }

        if (vectorBits >= 128)
        {
            for (; i + 16 <= source.Length; i += 16)
            {
                Vector128<int> a = ToInt(Vector128.LoadUnsafe(ref src, (nuint)i));
                Vector128<int> b = ToInt(Vector128.LoadUnsafe(ref src, (nuint)(i + 4)));
                Vector128<int> c = ToInt(Vector128.LoadUnsafe(ref src, (nuint)(i + 8)));
                Vector128<int> d = ToInt(Vector128.LoadUnsafe(ref src, (nuint)(i + 12)));
                Vector128<ushort> ab = Vector128.Narrow(a.AsUInt32(), b.AsUInt32());
                Vector128<ushort> cd = Vector128.Narrow(c.AsUInt32(), d.AsUInt32());
                Vector128.Narrow(ab, cd).StoreUnsafe(ref dst, (nuint)i);
            }
        }

        return i;
    }

    /// <summary>Converts bytes to scaled float bit patterns (b / 255), element by element.</summary>
    /// <returns>The number of elements converted.</returns>
    internal static int ToFloats(ReadOnlySpan<byte> source, Span<float> destination, int vectorBits)
    {
        int i = 0;
        ref byte src = ref MemoryMarshal.GetReference(source);
        ref float dst = ref MemoryMarshal.GetReference(destination);
        if (vectorBits >= 256 && Vector256.IsHardwareAccelerated)
        {
            Vector256<float> divisor = Vector256.Create(255f);
            for (; i + 16 <= source.Length; i += 16)
            {
                (Vector256<ushort> words, _) = Vector256.Widen(Vector256.Create(Vector128.LoadUnsafe(ref src, (nuint)i), Vector128<byte>.Zero));
                (Vector256<uint> a, Vector256<uint> b) = Vector256.Widen(words);
                (Vector256.ConvertToSingle(a.AsInt32()) / divisor).StoreUnsafe(ref dst, (nuint)i);
                (Vector256.ConvertToSingle(b.AsInt32()) / divisor).StoreUnsafe(ref dst, (nuint)(i + 8));
            }
        }

        if (vectorBits >= 128)
        {
            Vector128<float> divisor = Vector128.Create(255f);
            for (; i + 16 <= source.Length; i += 16)
            {
                (Vector128<ushort> lo, Vector128<ushort> hi) = Vector128.Widen(Vector128.LoadUnsafe(ref src, (nuint)i));
                (Vector128<uint> a, Vector128<uint> b) = Vector128.Widen(lo);
                (Vector128<uint> c, Vector128<uint> d) = Vector128.Widen(hi);
                (Vector128.ConvertToSingle(a.AsInt32()) / divisor).StoreUnsafe(ref dst, (nuint)i);
                (Vector128.ConvertToSingle(b.AsInt32()) / divisor).StoreUnsafe(ref dst, (nuint)(i + 4));
                (Vector128.ConvertToSingle(c.AsInt32()) / divisor).StoreUnsafe(ref dst, (nuint)(i + 8));
                (Vector128.ConvertToSingle(d.AsInt32()) / divisor).StoreUnsafe(ref dst, (nuint)(i + 12));
            }
        }

        return i;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> ToInt(Vector256<float> v)
    {
        Vector256<float> clamped = Vector256.ConditionalSelect(Vector256.GreaterThan(v, Vector256<float>.Zero), Vector256.Min(v, Vector256<float>.One), Vector256<float>.Zero);
        return Vector256.ConvertToInt32((clamped * 255f) + Vector256.Create(0.5f));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> ToInt(Vector128<float> v)
    {
        Vector128<float> clamped = Vector128.ConditionalSelect(Vector128.GreaterThan(v, Vector128<float>.Zero), Vector128.Min(v, Vector128<float>.One), Vector128<float>.Zero);
        return Vector128.ConvertToInt32((clamped * 255f) + Vector128.Create(0.5f));
    }
}
