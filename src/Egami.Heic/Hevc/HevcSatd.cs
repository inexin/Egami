using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Egami.Heic.Hevc;

/// <summary>
/// The sum of absolute Hadamard-transformed differences (SATD) of two blocks, the encoder's rough cost of an intra prediction:
/// 8x8 Walsh-Hadamard transforms of the difference, or 4x4 for a 4x4 block, normalized as (sum + 2) &gt;&gt; 2 and (sum + 1) &gt;&gt; 1.
/// With AVX2 an 8x8 block's rows are vectors: the vertical butterflies add and subtract rows, the horizontal ones pair lanes by
/// shuffles; only the sum of magnitudes is kept, so the order of the outputs does not matter.
/// </summary>
[SkipLocalsInit]
internal static class HevcSatd
{
    /// <summary>The SATD of a square block (4, 8, 16, 32 or 64 samples) at the same place in two planes of one stride.</summary>
    internal static long Compute(ushort[] a, ushort[] b, int at, int stride, int size) => Compute(a, at, stride, b, at, stride, size);

    /// <summary>The SATD of square blocks at different places in two planes with their own strides.</summary>
    internal static long Compute(ushort[] a, int atA, int strideA, ushort[] b, int atB, int strideB, int size)
    {
        if (size == 4)
        {
            return ((Avx2.IsSupported ? Hadamard4Avx2(a, atA, strideA, b, atB, strideB) : Hadamard4(a, atA, strideA, b, atB, strideB)) + 1) >> 1;
        }

        long sum = 0;
        for (int by = 0; by < size; by += 8)
        {
            for (int bx = 0; bx < size; bx += 8)
            {
                int oa = atA + (by * strideA) + bx, ob = atB + (by * strideB) + bx;
                sum += (Avx2.IsSupported ? Hadamard8Avx2(a, oa, strideA, b, ob, strideB) : Hadamard8(a, oa, strideA, b, ob, strideB)) + 2 >> 2;
            }
        }

        return sum;
    }

    private static long Hadamard8Avx2(ushort[] a, int atA, int sa, ushort[] b, int atB, int sb)
    {
        ref ushort pa = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(a), atA);
        ref ushort pb = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(b), atB);
        Vector256<int> r0 = Row(ref pa, ref pb, 0, 0), r1 = Row(ref pa, ref pb, sa, sb), r2 = Row(ref pa, ref pb, 2 * sa, 2 * sb), r3 = Row(ref pa, ref pb, 3 * sa, 3 * sb);
        Vector256<int> r4 = Row(ref pa, ref pb, 4 * sa, 4 * sb), r5 = Row(ref pa, ref pb, 5 * sa, 5 * sb), r6 = Row(ref pa, ref pb, 6 * sa, 6 * sb);
        Vector256<int> r7 = Row(ref pa, ref pb, 7 * sa, 7 * sb);

        // Vertical butterflies: rows i and i ^ 1, i ^ 2, i ^ 4.
        (r0, r1) = (r0 + r1, r0 - r1);
        (r2, r3) = (r2 + r3, r2 - r3);
        (r4, r5) = (r4 + r5, r4 - r5);
        (r6, r7) = (r6 + r7, r6 - r7);
        (r0, r2) = (r0 + r2, r0 - r2);
        (r1, r3) = (r1 + r3, r1 - r3);
        (r4, r6) = (r4 + r6, r4 - r6);
        (r5, r7) = (r5 + r7, r5 - r7);
        (r0, r4) = (r0 + r4, r0 - r4);
        (r1, r5) = (r1 + r5, r1 - r5);
        (r2, r6) = (r2 + r6, r2 - r6);
        (r3, r7) = (r3 + r7, r3 - r7);

        Vector256<int> sum = Vector256.Abs(Horizontal(r0)) + Vector256.Abs(Horizontal(r1)) + Vector256.Abs(Horizontal(r2)) + Vector256.Abs(Horizontal(r3))
            + Vector256.Abs(Horizontal(r4)) + Vector256.Abs(Horizontal(r5)) + Vector256.Abs(Horizontal(r6)) + Vector256.Abs(Horizontal(r7));
        return Vector256.Sum(sum);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Row(ref ushort pa, ref ushort pb, int offsetA, int offsetB) =>
        Avx2.ConvertToVector256Int32(Vector128.LoadUnsafe(ref pa, (nuint)offsetA)) - Avx2.ConvertToVector256Int32(Vector128.LoadUnsafe(ref pb, (nuint)offsetB));

    /// <summary>The 8-point Walsh-Hadamard transform of a row, in some order.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Horizontal(Vector256<int> v)
    {
        // Lanes i and i ^ 1: sums in the even lanes, differences in the odd.
        Vector256<int> b = Avx2.Shuffle(v, 0b10_11_00_01);
        v = Avx2.Blend(v + b, b - v, 0b1010_1010);

        // Lanes i and i ^ 2.
        b = Avx2.Shuffle(v, 0b01_00_11_10);
        v = Avx2.Blend(v + b, b - v, 0b1100_1100);

        // Lanes i and i ^ 4.
        b = Avx2.Permute2x128(v, v, 1);
        return Avx2.Blend(v + b, b - v, 0b1111_0000);
    }

    private static long Hadamard4Avx2(ushort[] a, int atA, int sa, ushort[] b, int atB, int sb)
    {
        ref ushort pa = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(a), atA);
        ref ushort pb = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(b), atB);
        Vector128<int> r0 = Row4(ref pa, ref pb, 0, 0), r1 = Row4(ref pa, ref pb, sa, sb), r2 = Row4(ref pa, ref pb, 2 * sa, 2 * sb), r3 = Row4(ref pa, ref pb, 3 * sa, 3 * sb);
        (r0, r1) = (r0 + r1, r0 - r1);
        (r2, r3) = (r2 + r3, r2 - r3);
        (r0, r2) = (r0 + r2, r0 - r2);
        (r1, r3) = (r1 + r3, r1 - r3);
        Vector128<int> sum = Vector128.Abs(Horizontal4(r0)) + Vector128.Abs(Horizontal4(r1)) + Vector128.Abs(Horizontal4(r2)) + Vector128.Abs(Horizontal4(r3));
        return Vector128.Sum(sum);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Row4(ref ushort pa, ref ushort pb, int offsetA, int offsetB) =>
        Sse41.ConvertToVector128Int32(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref Unsafe.As<ushort, byte>(ref Unsafe.Add(ref pa, offsetA)))).AsUInt16())
        - Sse41.ConvertToVector128Int32(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref Unsafe.As<ushort, byte>(ref Unsafe.Add(ref pb, offsetB)))).AsUInt16());

    /// <summary>The 4-point Walsh-Hadamard transform of a row, in some order.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Horizontal4(Vector128<int> v)
    {
        Vector128<int> b = Sse2.Shuffle(v, 0b10_11_00_01);
        v = Avx2.Blend(v + b, b - v, 0b1010);
        b = Sse2.Shuffle(v, 0b01_00_11_10);
        return Avx2.Blend(v + b, b - v, 0b1100);
    }

    private static long Hadamard8(ushort[] a, int atA, int sa, ushort[] b, int atB, int sb)
    {
        Span<int> m = stackalloc int[64];
        for (int r = 0; r < 8; r++)
        {
            for (int k = 0; k < 8; k++)
            {
                m[(r * 8) + k] = a[atA + (r * sa) + k] - b[atB + (r * sb) + k];
            }

            Butterfly(m.Slice(r * 8, 8), 1);
        }

        long sum = 0;
        Span<int> column = stackalloc int[8];
        for (int k = 0; k < 8; k++)
        {
            for (int r = 0; r < 8; r++)
            {
                column[r] = m[(r * 8) + k];
            }

            Butterfly(column, 1);
            for (int r = 0; r < 8; r++)
            {
                sum += Math.Abs(column[r]);
            }
        }

        return sum;
    }

    private static long Hadamard4(ushort[] a, int atA, int sa, ushort[] b, int atB, int sb)
    {
        Span<int> m = stackalloc int[16];
        for (int r = 0; r < 4; r++)
        {
            for (int k = 0; k < 4; k++)
            {
                m[(r * 4) + k] = a[atA + (r * sa) + k] - b[atB + (r * sb) + k];
            }

            Butterfly(m.Slice(r * 4, 4), 1);
        }

        long sum = 0;
        Span<int> column = stackalloc int[4];
        for (int k = 0; k < 4; k++)
        {
            for (int r = 0; r < 4; r++)
            {
                column[r] = m[(r * 4) + k];
            }

            Butterfly(column, 1);
            for (int r = 0; r < 4; r++)
            {
                sum += Math.Abs(column[r]);
            }
        }

        return sum;
    }

    /// <summary>An in-place Walsh-Hadamard transform of 4 or 8 values.</summary>
    private static void Butterfly(Span<int> v, int step)
    {
        for (; step < v.Length; step <<= 1)
        {
            for (int i = 0; i < v.Length; i++)
            {
                if ((i & step) == 0)
                {
                    (v[i], v[i + step]) = (v[i] + v[i + step], v[i] - v[i + step]);
                }
            }
        }
    }
}
