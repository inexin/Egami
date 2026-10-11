using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

using Egami.Pixels;

namespace Egami;

/// <summary>
/// SIMD chroma resampling for the 32-bit paths: vertical weighting and horizontal upsampling of 10- to 16-bit chroma, the
/// downsampling filters, and the final rounding. Each handles the interior of a row and returns where it stopped; the scalar
/// rules handle the edges and the rest, and give the same values.
/// </summary>
internal static class YuvResample
{
    /// <summary>Weights two rows of samples, clamped to <paramref name="max"/>, into <paramref name="t"/>; returns the count done.</summary>
    internal static int Vertical<TSample>(ReadOnlySpan<TSample> a, ReadOnlySpan<TSample> b, int w0, int w1, int max, Span<int> t)
        where TSample : unmanaged
    {
        if (!YuvSimd.IsSupported || typeof(TSample) != typeof(ushort))
        {
            return 0;
        }

        ref ushort ra = ref Unsafe.As<TSample, ushort>(ref MemoryMarshal.GetReference(a));
        ref ushort rb = ref Unsafe.As<TSample, ushort>(ref MemoryMarshal.GetReference(b));
        ref int rt = ref MemoryMarshal.GetReference(t);
        Vector128<ushort> limit = Vector128.Create((ushort)Math.Min(max, ushort.MaxValue));
        Vector128<int> v0 = Vector128.Create(w0), v1 = Vector128.Create(w1);
        int i = 0;
        for (; i <= t.Length - 8; i += 8)
        {
            (Vector128<uint> alo, Vector128<uint> ahi) = Vector128.Widen(Vector128.Min(Vector128.LoadUnsafe(ref ra, (nuint)i), limit));
            (Vector128<uint> blo, Vector128<uint> bhi) = Vector128.Widen(Vector128.Min(Vector128.LoadUnsafe(ref rb, (nuint)i), limit));
            ((alo.AsInt32() * v0) + (blo.AsInt32() * v1)).StoreUnsafe(ref rt, (nuint)i);
            ((ahi.AsInt32() * v0) + (bhi.AsInt32() * v1)).StoreUnsafe(ref rt, (nuint)(i + 4));
        }

        return i;
    }

    /// <summary>
    /// Upsamples chroma samples 1 .. <c>n - 2</c> (those with both neighbors) of <paramref name="t"/> by the rule of
    /// <see cref="YuvConvert.Upsample.Horizontal"/>, for 4:2:0 and 4:2:2; returns the first chroma sample not done.
    /// </summary>
    internal static int Horizontal(ReadOnlySpan<int> t, Span<int> target, ChromaSiting siting, ChromaUpsampling mode)
    {
        if (!YuvSimd.IsSupported)
        {
            return 1;
        }

        ref int rt = ref MemoryMarshal.GetReference(t);
        ref int ro = ref MemoryMarshal.GetReference(target);
        int last = t.Length - 1;
        int kind = mode == ChromaUpsampling.Nearest ? 0 : siting == ChromaSiting.Center ? 1 : 2;
        Vector128<int> three = Vector128.Create(3);
        int i = 1;
        for (; i + 4 <= last; i += 4)
        {
            Vector128<int> mid = Vector128.LoadUnsafe(ref rt, (nuint)i);
            Vector128<int> even, odd;
            if (kind == 1)
            {
                Vector128<int> mid3 = mid * three;
                even = mid3 + Vector128.LoadUnsafe(ref rt, (nuint)(i - 1));
                odd = mid3 + Vector128.LoadUnsafe(ref rt, (nuint)(i + 1));
            }
            else if (kind == 2)
            {
                even = mid << 2;
                odd = (mid + Vector128.LoadUnsafe(ref rt, (nuint)(i + 1))) << 1;
            }
            else
            {
                even = odd = mid << 2;
            }

            YuvSimd.ZipLow(even, odd).StoreUnsafe(ref ro, (nuint)(2 * i));
            YuvSimd.ZipHigh(even, odd).StoreUnsafe(ref ro, (nuint)((2 * i) + 4));
        }

        return i;
    }

    /// <summary>
    /// Adds chroma sums 1 .. of one row filtered by the rule of <see cref="YuvKernelsFloat.FilterRow(ReadOnlySpan{int}, int, Span{int}, ChromaFormat, ChromaSiting, bool)"/>,
    /// for a subsampled format; returns the first sum not done.
    /// </summary>
    internal static int Filter(ReadOnlySpan<int> row, int weight, Span<int> sum, ChromaSiting siting, bool fast)
    {
        if (!YuvSimd.IsSupported)
        {
            return 1;
        }

        ref int rr = ref MemoryMarshal.GetReference(row);
        ref int rs = ref MemoryMarshal.GetReference(sum);
        Vector128<int> w = Vector128.Create(weight);
        int i = 1;

        // Sums i .. i + 3 read row[2i - 1] to row[2i + 8].
        for (; i + 4 < sum.Length && (2 * i) + 8 < row.Length; i += 4)
        {
            Vector128<int> lo = Vector128.LoadUnsafe(ref rr, (nuint)(2 * i)), hi = Vector128.LoadUnsafe(ref rr, (nuint)((2 * i) + 4));
            Vector128<int> even = UnzipEven(lo, hi), value;
            if (fast)
            {
                value = even;
            }
            else if (siting == ChromaSiting.Center)
            {
                value = even + UnzipOdd(lo, hi);
            }
            else
            {
                Vector128<int> before = UnzipEven(Vector128.LoadUnsafe(ref rr, (nuint)((2 * i) - 1)), Vector128.LoadUnsafe(ref rr, (nuint)((2 * i) + 3)));
                value = before + (even << 1) + UnzipOdd(lo, hi);
            }

            (Vector128.LoadUnsafe(ref rs, (nuint)i) + (w * value)).StoreUnsafe(ref rs, (nuint)i);
        }

        return i;
    }

    /// <summary>The float twin of <see cref="Filter(ReadOnlySpan{int}, int, Span{int}, ChromaSiting, bool)"/>, with the scalar path's order of additions.</summary>
    internal static int Filter(ReadOnlySpan<float> row, int weight, Span<float> sum, ChromaSiting siting, bool fast)
    {
        if (!YuvSimd.IsSupported)
        {
            return 1;
        }

        ref float rr = ref MemoryMarshal.GetReference(row);
        ref float rs = ref MemoryMarshal.GetReference(sum);
        Vector128<float> w = Vector128.Create((float)weight), two = Vector128.Create(2f);
        int i = 1;
        for (; i + 4 < sum.Length && (2 * i) + 8 < row.Length; i += 4)
        {
            Vector128<int> lo = Vector128.LoadUnsafe(ref rr, (nuint)(2 * i)).AsInt32(), hi = Vector128.LoadUnsafe(ref rr, (nuint)((2 * i) + 4)).AsInt32();
            Vector128<float> even = UnzipEven(lo, hi).AsSingle(), value;
            if (fast)
            {
                value = even;
            }
            else if (siting == ChromaSiting.Center)
            {
                value = even + UnzipOdd(lo, hi).AsSingle();
            }
            else
            {
                Vector128<float> before = UnzipEven(Vector128.LoadUnsafe(ref rr, (nuint)((2 * i) - 1)).AsInt32(), Vector128.LoadUnsafe(ref rr, (nuint)((2 * i) + 3)).AsInt32()).AsSingle();
                value = before + (two * even) + UnzipOdd(lo, hi).AsSingle();
            }

            (Vector128.LoadUnsafe(ref rs, (nuint)i) + (w * value)).StoreUnsafe(ref rs, (nuint)i);
        }

        return i;
    }

    /// <summary>Rounds Q15 chroma sums, <c>((sum + half) &gt;&gt; shift) + offset</c> clamped to 0..255; returns the count done.</summary>
    internal static int Round8<TSample>(ReadOnlySpan<int> sum, Span<TSample> target, int shift, int offset)
        where TSample : unmanaged
    {
        if (!YuvSimd.IsSupported)
        {
            return 0;
        }

        ref int rs = ref MemoryMarshal.GetReference(sum);
        ref byte rt = ref Unsafe.As<TSample, byte>(ref MemoryMarshal.GetReference(target));
        Vector128<int> half = Vector128.Create(1 << (shift - 1)), off = Vector128.Create(offset), top = Vector128.Create(255);
        int i = 0;
        for (; i <= sum.Length - 8; i += 8)
        {
            Vector128<int> a = Vector128.Clamp(Vector128.ShiftRightArithmetic(Vector128.LoadUnsafe(ref rs, (nuint)i) + half, shift) + off, Vector128<int>.Zero, top);
            Vector128<int> b = Vector128.Clamp(Vector128.ShiftRightArithmetic(Vector128.LoadUnsafe(ref rs, (nuint)(i + 4)) + half, shift) + off, Vector128<int>.Zero, top);
            Vector128<ushort> words = Vector128.Narrow(a.AsUInt32(), b.AsUInt32());
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref rt, i), Vector128.Narrow(words, words).AsUInt64().ToScalar());
        }

        return i;
    }

    /// <summary>Rounds float chroma sums, <c>floor(sum × scale + offset + 0.5)</c> clamped to 0..max; returns the count done.</summary>
    internal static int RoundFloat<TSample>(ReadOnlySpan<float> sum, Span<TSample> target, float scale, float offset, int max)
        where TSample : unmanaged
    {
        if (!YuvSimd.IsSupported)
        {
            return 0;
        }

        ref float rs = ref MemoryMarshal.GetReference(sum);
        ref byte rt = ref Unsafe.As<TSample, byte>(ref MemoryMarshal.GetReference(target));
        Vector128<float> s = Vector128.Create(scale), off = Vector128.Create(offset), half = Vector128.Create(0.5f), top = Vector128.Create((float)max);
        int i = 0;
        for (; i <= sum.Length - 4; i += 4)
        {
            Vector128<int> v = Vector128.ConvertToInt32(Vector128.Clamp(Vector128.Floor((Vector128.LoadUnsafe(ref rs, (nuint)i) * s) + off + half), Vector128<float>.Zero, top));
            Vector128<ushort> words = Vector128.Narrow(v.AsUInt32(), v.AsUInt32());
            if (typeof(TSample) == typeof(byte))
            {
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref rt, i), Vector128.Narrow(words, words).AsUInt32().ToScalar());
            }
            else
            {
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref rt, 2 * i), words.AsUInt64().ToScalar());
            }
        }

        return i;
    }

    /// <summary>Elements 0, 2, 4, 6 of the eight in <paramref name="a"/> and <paramref name="b"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> UnzipEven(Vector128<int> a, Vector128<int> b) =>
        Sse.IsSupported ? Sse.Shuffle(a.AsSingle(), b.AsSingle(), 0b10_00_10_00).AsInt32() : AdvSimd.Arm64.UnzipEven(a, b);

    /// <summary>Elements 1, 3, 5, 7 of the eight in <paramref name="a"/> and <paramref name="b"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> UnzipOdd(Vector128<int> a, Vector128<int> b) =>
        Sse.IsSupported ? Sse.Shuffle(a.AsSingle(), b.AsSingle(), 0b11_01_11_01).AsInt32() : AdvSimd.Arm64.UnzipOdd(a, b);
}
