using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Egami.Video.Codecs.Vp9;

/// <summary>
/// The VP9 encoder's pixel kernels: residuals, squared errors, sums of absolute differences and variance, with Vector128 and
/// AVX2 forms for 8-bit samples (the same values as the scalar loops).
/// </summary>
internal static class Vp9EncoderSimd
{
    private static bool Avx2Ok => Avx2.IsSupported;

    /// <summary>Writes source minus prediction, <paramref name="w"/> by <paramref name="h"/>, packed <paramref name="w"/> a row.</summary>
    public static void Subtract(ReadOnlySpan<byte> source, int sourceOffset, int sourceStride, ReadOnlySpan<byte> pred, int predOffset, int predStride,
        Span<short> residual, int w, int h)
    {
        if (w >= 16 && Avx2Ok)
        {
            ref byte s0 = ref MemoryMarshal.GetReference(source);
            ref byte p0 = ref MemoryMarshal.GetReference(pred);
            ref short r0 = ref MemoryMarshal.GetReference(residual);
            if (source.Length < sourceOffset + ((h - 1) * sourceStride) + w || pred.Length < predOffset + ((h - 1) * predStride) + w ||
                residual.Length < w * h)
            {
                throw new ArgumentOutOfRangeException(nameof(w));
            }

            for (int y = 0; y < h; y++)
            {
                ref byte s = ref Unsafe.Add(ref s0, sourceOffset + (y * sourceStride));
                ref byte p = ref Unsafe.Add(ref p0, predOffset + (y * predStride));
                ref short r = ref Unsafe.Add(ref r0, y * w);
                int x = 0;
                for (; x + 16 <= w; x += 16)
                {
                    Vector256<short> a = Avx2.ConvertToVector256Int16(Vector128.LoadUnsafe(ref s, (nuint)x));
                    Vector256<short> b = Avx2.ConvertToVector256Int16(Vector128.LoadUnsafe(ref p, (nuint)x));
                    (a - b).StoreUnsafe(ref r, (nuint)x);
                }

                for (; x < w; x++)
                {
                    Unsafe.Add(ref r, x) = (short)(Unsafe.Add(ref s, x) - Unsafe.Add(ref p, x));
                }
            }

            return;
        }

        for (int y = 0; y < h; y++)
        {
            ReadOnlySpan<byte> s = source.Slice(sourceOffset + (y * sourceStride), w);
            ReadOnlySpan<byte> p = pred.Slice(predOffset + (y * predStride), w);
            Span<short> r = residual.Slice(y * w, w);
            for (int x = 0; x < w; x++)
            {
                r[x] = (short)(s[x] - p[x]);
            }
        }
    }

    /// <summary>The sum of squares of residuals.</summary>
    public static long SumSquares(ReadOnlySpan<short> values)
    {
        long sum = 0;
        int i = 0;
        if (Avx2Ok && values.Length >= 16)
        {
            ref short v0 = ref MemoryMarshal.GetReference(values);
            Vector256<int> acc = Vector256<int>.Zero;
            int chunk = 0;
            for (; i + 16 <= values.Length; i += 16)
            {
                Vector256<short> v = Vector256.LoadUnsafe(ref v0, (nuint)i);
                acc += Avx2.MultiplyAddAdjacent(v, v);
                if (++chunk == 1024)
                {
                    sum += Wide(acc);
                    acc = Vector256<int>.Zero;
                    chunk = 0;
                }
            }

            sum += Wide(acc);
        }

        for (; i < values.Length; i++)
        {
            sum += values[i] * values[i];
        }

        return sum;
    }

    private static long Wide(Vector256<int> v)
    {
        (Vector256<long> lo, Vector256<long> hi) = Vector256.Widen(v);
        return Vector256.Sum(lo + hi);
    }

    /// <summary>The squared error between two rows.</summary>
    public static long SseRow<T>(ReadOnlySpan<T> a, ReadOnlySpan<T> b)
        where T : unmanaged, IBinaryInteger<T>
    {
        if (typeof(T) == typeof(byte))
        {
            return Sse8(MemoryMarshal.Cast<T, byte>(a), 0, 0, MemoryMarshal.Cast<T, byte>(b), 0, 0, a.Length, 1);
        }

        long sum = 0;
        for (int x = 0; x < a.Length; x++)
        {
            long d = long.CreateTruncating(a[x]) - long.CreateTruncating(b[x]);
            sum += d * d;
        }

        return sum;
    }

    /// <summary>The squared error between two blocks.</summary>
    public static long Sse<T>(T[] a, int aOffset, int aStride, T[] b, int bOffset, int bStride, int w, int h)
        where T : unmanaged, IBinaryInteger<T>
    {
        if (typeof(T) == typeof(byte))
        {
            return Sse8(MemoryMarshal.Cast<T, byte>(a.AsSpan()), aOffset, aStride, MemoryMarshal.Cast<T, byte>(b.AsSpan()), bOffset, bStride, w, h);
        }

        long sum = 0;
        for (int y = 0; y < h; y++)
        {
            ReadOnlySpan<T> ra = a.AsSpan(aOffset + (y * aStride), w), rb = b.AsSpan(bOffset + (y * bStride), w);
            for (int x = 0; x < w; x++)
            {
                long d = long.CreateTruncating(ra[x]) - long.CreateTruncating(rb[x]);
                sum += d * d;
            }
        }

        return sum;
    }

    private static long Sse8(ReadOnlySpan<byte> a, int aOffset, int aStride, ReadOnlySpan<byte> b, int bOffset, int bStride, int w, int h)
    {
        if (w >= 16 && Avx2Ok)
        {
            if (a.Length < aOffset + ((h - 1) * aStride) + w || b.Length < bOffset + ((h - 1) * bStride) + w)
            {
                throw new ArgumentOutOfRangeException(nameof(w));
            }

            ref byte a0 = ref MemoryMarshal.GetReference(a);
            ref byte b0 = ref MemoryMarshal.GetReference(b);
            long total = 0;
            for (int y = 0; y < h; y++)
            {
                ref byte ra = ref Unsafe.Add(ref a0, aOffset + (y * aStride));
                ref byte rb = ref Unsafe.Add(ref b0, bOffset + (y * bStride));
                Vector256<int> acc = Vector256<int>.Zero;
                int x = 0;
                for (; x + 16 <= w; x += 16)
                {
                    Vector256<short> d = Avx2.ConvertToVector256Int16(Vector128.LoadUnsafe(ref ra, (nuint)x)) -
                        Avx2.ConvertToVector256Int16(Vector128.LoadUnsafe(ref rb, (nuint)x));
                    acc += Avx2.MultiplyAddAdjacent(d, d);
                }

                for (; x < w; x++)
                {
                    int d = Unsafe.Add(ref ra, x) - Unsafe.Add(ref rb, x);
                    total += d * d;
                }

                total += Vector256.Sum(acc);
            }

            return total;
        }

        long sum = 0;
        for (int y = 0; y < h; y++)
        {
            ReadOnlySpan<byte> ra = a.Slice(aOffset + (y * aStride), w), rb = b.Slice(bOffset + (y * bStride), w);
            int row = 0;
            for (int x = 0; x < w; x++)
            {
                int d = ra[x] - rb[x];
                row += d * d;
            }

            sum += row;
        }

        return sum;
    }

    /// <summary>The sum of absolute differences between two blocks.</summary>
    public static long Sad<T>(T[] a, int aOffset, int aStride, T[] b, int bOffset, int bStride, int w, int h)
        where T : unmanaged, IBinaryInteger<T>
    {
        if (typeof(T) == typeof(byte))
        {
            return Sad8(MemoryMarshal.Cast<T, byte>(a.AsSpan()), aOffset, aStride, MemoryMarshal.Cast<T, byte>(b.AsSpan()), bOffset, bStride, w, h);
        }

        long sum = 0;
        for (int y = 0; y < h; y++)
        {
            ReadOnlySpan<T> ra = a.AsSpan(aOffset + (y * aStride), w), rb = b.AsSpan(bOffset + (y * bStride), w);
            for (int x = 0; x < w; x++)
            {
                sum += Math.Abs(long.CreateTruncating(ra[x]) - long.CreateTruncating(rb[x]));
            }
        }

        return sum;
    }

    private static long Sad8(ReadOnlySpan<byte> a, int aOffset, int aStride, ReadOnlySpan<byte> b, int bOffset, int bStride, int w, int h)
    {
        if (w >= 8 && Sse2.IsSupported)
        {
            if (a.Length < aOffset + ((h - 1) * aStride) + w || b.Length < bOffset + ((h - 1) * bStride) + w)
            {
                throw new ArgumentOutOfRangeException(nameof(w));
            }

            ref byte a0 = ref MemoryMarshal.GetReference(a);
            ref byte b0 = ref MemoryMarshal.GetReference(b);
            if (w == 8)
            {
                Vector128<ushort> acc8 = Vector128<ushort>.Zero;
                for (int y = 0; y < h; y++)
                {
                    Vector128<byte> va = Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref a0, aOffset + (y * aStride)))).AsByte();
                    Vector128<byte> vb = Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref b0, bOffset + (y * bStride)))).AsByte();
                    acc8 += Sse2.SumAbsoluteDifferences(va, vb);
                }

                return acc8.AsInt64().ToScalar();
            }

            if (w >= 32 && (w & 31) == 0 && Avx2Ok)
            {
                Vector256<long> acc = Vector256<long>.Zero;
                for (int y = 0; y < h; y++)
                {
                    ref byte ra = ref Unsafe.Add(ref a0, aOffset + (y * aStride));
                    ref byte rb = ref Unsafe.Add(ref b0, bOffset + (y * bStride));
                    for (int x = 0; x < w; x += 32)
                    {
                        acc += Avx2.SumAbsoluteDifferences(Vector256.LoadUnsafe(ref ra, (nuint)x), Vector256.LoadUnsafe(ref rb, (nuint)x)).AsInt64();
                    }
                }

                return Vector256.Sum(acc);
            }

            Vector128<long> acc16 = Vector128<long>.Zero;
            long tail = 0;
            for (int y = 0; y < h; y++)
            {
                ref byte ra = ref Unsafe.Add(ref a0, aOffset + (y * aStride));
                ref byte rb = ref Unsafe.Add(ref b0, bOffset + (y * bStride));
                int x = 0;
                for (; x + 16 <= w; x += 16)
                {
                    acc16 += Sse2.SumAbsoluteDifferences(Vector128.LoadUnsafe(ref ra, (nuint)x), Vector128.LoadUnsafe(ref rb, (nuint)x)).AsInt64();
                }

                for (; x < w; x++)
                {
                    tail += Math.Abs(Unsafe.Add(ref ra, x) - Unsafe.Add(ref rb, x));
                }
            }

            return Vector128.Sum(acc16) + tail;
        }

        long sum = 0;
        for (int y = 0; y < h; y++)
        {
            ReadOnlySpan<byte> ra = a.Slice(aOffset + (y * aStride), w), rb = b.Slice(bOffset + (y * bStride), w);
            int row = 0;
            for (int x = 0; x < w; x++)
            {
                row += Math.Abs(ra[x] - rb[x]);
            }

            sum += row;
        }

        return sum;
    }

    /// <summary>The variance of a block's samples (the mean of squares less the square of the mean).</summary>
    public static long Variance<T>(T[] a, int offset, int stride, int w, int h)
        where T : unmanaged, IBinaryInteger<T>
    {
        long sum = 0, squares = 0;
        for (int y = 0; y < h; y++)
        {
            ReadOnlySpan<T> r = a.AsSpan(offset + (y * stride), w);
            for (int x = 0; x < w; x++)
            {
                long v = long.CreateTruncating(r[x]);
                sum += v;
                squares += v * v;
            }
        }

        long n = (long)w * h;
        return (squares - (sum * sum / n)) / n;
    }
}
