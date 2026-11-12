using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Egami.Video.Codecs.H264;

/// <summary>
/// The encoder's block costs with SSE2 where the CPU has it: SAD, SATD (the 4x4 Hadamard transform of the differences), the
/// squared error and the rounded mean of two predictions. Each gives exactly what the scalar code gives.
/// </summary>
internal static class H264EncodeSimd
{
    /// <summary>The sum of absolute differences of a w by h block (w a multiple of 4).</summary>
    public static long Sad(byte[] a, int atA, int strideA, byte[] b, int atB, int strideB, int w, int h)
    {
        ref byte ra = ref MemoryMarshal.GetArrayDataReference(a);
        ref byte rb = ref MemoryMarshal.GetArrayDataReference(b);
        if (Sse2.IsSupported)
        {
            Vector128<ulong> sum = Vector128<ulong>.Zero;
            for (int y = 0; y < h; y++)
            {
                int pa = atA + (y * strideA), pb = atB + (y * strideB);
                for (int x = 0; x < w;)
                {
                    if (w - x >= 16)
                    {
                        sum += Sse2.SumAbsoluteDifferences(Vector128.LoadUnsafe(ref ra, (nuint)(pa + x)), Vector128.LoadUnsafe(ref rb, (nuint)(pb + x))).AsUInt64();
                        x += 16;
                    }
                    else if (w - x >= 8)
                    {
                        sum += Sse2.SumAbsoluteDifferences(Load8(ref ra, pa + x), Load8(ref rb, pb + x)).AsUInt64();
                        x += 8;
                    }
                    else
                    {
                        sum += Sse2.SumAbsoluteDifferences(Load4(ref ra, pa + x), Load4(ref rb, pb + x)).AsUInt64();
                        x += 4;
                    }
                }
            }

            return (long)(sum.GetElement(0) + sum.GetElement(1));
        }

        long total = 0;
        for (int y = 0; y < h; y++)
        {
            int pa = atA + (y * strideA), pb = atB + (y * strideB);
            for (int x = 0; x < w; x++)
            {
                total += Math.Abs(a[pa + x] - b[pb + x]);
            }
        }

        return total;
    }

    /// <summary>
    /// The SADs of one w by h block (w a multiple of 4) against four blocks of one plane at once, each source row loaded once;
    /// each sum is what <see cref="Sad"/> gives.
    /// </summary>
    public static void Sad4(byte[] a, int atA, int strideA, byte[] b, int b0, int b1, int b2, int b3, int strideB, int w, int h, Span<long> sums)
    {
        if (!Sse2.IsSupported)
        {
            sums[0] = Sad(a, atA, strideA, b, b0, strideB, w, h);
            sums[1] = Sad(a, atA, strideA, b, b1, strideB, w, h);
            sums[2] = Sad(a, atA, strideA, b, b2, strideB, w, h);
            sums[3] = Sad(a, atA, strideA, b, b3, strideB, w, h);
            return;
        }

        ref byte ra = ref MemoryMarshal.GetArrayDataReference(a);
        ref byte rb = ref MemoryMarshal.GetArrayDataReference(b);
        Vector128<ulong> s0 = Vector128<ulong>.Zero, s1 = s0, s2 = s0, s3 = s0;
        for (int y = 0; y < h; y++)
        {
            int pa = atA + (y * strideA), pb = y * strideB;
            for (int x = 0; x < w;)
            {
                int o = pb + x;
                if (w - x >= 16)
                {
                    Vector128<byte> v = Vector128.LoadUnsafe(ref ra, (nuint)(pa + x));
                    s0 += Sse2.SumAbsoluteDifferences(v, Vector128.LoadUnsafe(ref rb, (nuint)(b0 + o))).AsUInt64();
                    s1 += Sse2.SumAbsoluteDifferences(v, Vector128.LoadUnsafe(ref rb, (nuint)(b1 + o))).AsUInt64();
                    s2 += Sse2.SumAbsoluteDifferences(v, Vector128.LoadUnsafe(ref rb, (nuint)(b2 + o))).AsUInt64();
                    s3 += Sse2.SumAbsoluteDifferences(v, Vector128.LoadUnsafe(ref rb, (nuint)(b3 + o))).AsUInt64();
                    x += 16;
                }
                else if (w - x >= 8)
                {
                    Vector128<byte> v = Load8(ref ra, pa + x);
                    s0 += Sse2.SumAbsoluteDifferences(v, Load8(ref rb, b0 + o)).AsUInt64();
                    s1 += Sse2.SumAbsoluteDifferences(v, Load8(ref rb, b1 + o)).AsUInt64();
                    s2 += Sse2.SumAbsoluteDifferences(v, Load8(ref rb, b2 + o)).AsUInt64();
                    s3 += Sse2.SumAbsoluteDifferences(v, Load8(ref rb, b3 + o)).AsUInt64();
                    x += 8;
                }
                else
                {
                    Vector128<byte> v = Load4(ref ra, pa + x);
                    s0 += Sse2.SumAbsoluteDifferences(v, Load4(ref rb, b0 + o)).AsUInt64();
                    s1 += Sse2.SumAbsoluteDifferences(v, Load4(ref rb, b1 + o)).AsUInt64();
                    s2 += Sse2.SumAbsoluteDifferences(v, Load4(ref rb, b2 + o)).AsUInt64();
                    s3 += Sse2.SumAbsoluteDifferences(v, Load4(ref rb, b3 + o)).AsUInt64();
                    x += 4;
                }
            }
        }

        sums[0] = (long)(s0.GetElement(0) + s0.GetElement(1));
        sums[1] = (long)(s1.GetElement(0) + s1.GetElement(1));
        sums[2] = (long)(s2.GetElement(0) + s2.GetElement(1));
        sums[3] = (long)(s3.GetElement(0) + s3.GetElement(1));
    }

    /// <summary>
    /// The SATD of a w by h block (multiples of 4): the sum of the absolute 4x4 Hadamard coefficients of the differences, halved.
    /// </summary>
    public static long Satd(byte[] src, int at, int stride, byte[] pred, int predAt, int predStride, int width, int height)
    {
        if (!Sse2.IsSupported)
        {
            return SatdScalar(src, at, stride, pred, predAt, predStride, width, height);
        }

        ref byte rs = ref MemoryMarshal.GetArrayDataReference(src);
        ref byte rp = ref MemoryMarshal.GetArrayDataReference(pred);
        if (Avx2.IsSupported && (width & 15) == 0)
        {
            // Four 4x4 blocks side by side: each 128-bit lane holds two, as below.
            Vector256<int> wide = Vector256<int>.Zero;
            for (int by = 0; by < height; by += 4)
            {
                for (int bx = 0; bx < width; bx += 16)
                {
                    int s = at + (by * stride) + bx, p = predAt + (by * predStride) + bx;
                    Vector256<short> r0 = Diff16(ref rs, s, ref rp, p);
                    Vector256<short> r1 = Diff16(ref rs, s + stride, ref rp, p + predStride);
                    Vector256<short> r2 = Diff16(ref rs, s + (2 * stride), ref rp, p + (2 * predStride));
                    Vector256<short> r3 = Diff16(ref rs, s + (3 * stride), ref rp, p + (3 * predStride));
                    wide += Hadamard4(r0, r1, r2, r3);
                }
            }

            return Vector256.Sum(wide) >> 1;
        }

        if (Avx2.IsSupported && width == 8 && (height & 7) == 0)
        {
            // Two 4x4 blocks side by side in each 128-bit lane: rows 0-3 of the 8x8 in the low lane, rows 4-7 in the high one.
            Vector256<int> wide = Vector256<int>.Zero;
            int s4 = 4 * stride, p4 = 4 * predStride;
            for (int by = 0; by < height; by += 8)
            {
                int s = at + (by * stride), p = predAt + (by * predStride);
                Vector256<short> r0 = Diff8x2(ref rs, s, s + s4, ref rp, p, p + p4);
                Vector256<short> r1 = Diff8x2(ref rs, s + stride, s + stride + s4, ref rp, p + predStride, p + predStride + p4);
                Vector256<short> r2 = Diff8x2(ref rs, s + (2 * stride), s + (2 * stride) + s4, ref rp, p + (2 * predStride), p + (2 * predStride) + p4);
                Vector256<short> r3 = Diff8x2(ref rs, s + (3 * stride), s + (3 * stride) + s4, ref rp, p + (3 * predStride), p + (3 * predStride) + p4);
                wide += Hadamard4(r0, r1, r2, r3);
            }

            return Vector256.Sum(wide) >> 1;
        }

        Vector128<int> sum = Vector128<int>.Zero;
        for (int by = 0; by < height; by += 4)
        {
            for (int bx = 0; bx < width; bx += 8)
            {
                // Two 4x4 blocks side by side (the right one empty at the edge of a 4-wide block): one row of each per vector.
                bool pair = width - bx >= 8;
                int s = at + (by * stride) + bx, p = predAt + (by * predStride) + bx;
                Vector128<short> r0 = Diff(ref rs, s, ref rp, p, pair);
                Vector128<short> r1 = Diff(ref rs, s + stride, ref rp, p + predStride, pair);
                Vector128<short> r2 = Diff(ref rs, s + (2 * stride), ref rp, p + (2 * predStride), pair);
                Vector128<short> r3 = Diff(ref rs, s + (3 * stride), ref rp, p + (3 * predStride), pair);
                sum += Hadamard2(r0, r1, r2, r3);
            }
        }

        return Vector128.Sum(sum) >> 1;
    }

    /// <summary>The sum of squared differences of a w by h block of two planes with one layout.</summary>
    public static long Sse(byte[] a, byte[] b, int at, int stride, int width, int height)
    {
        if (Sse2.IsSupported && width == 4)
        {
            ref byte ra4 = ref MemoryMarshal.GetArrayDataReference(a);
            ref byte rb4 = ref MemoryMarshal.GetArrayDataReference(b);
            Vector128<int> sum4 = Vector128<int>.Zero;
            for (int y = 0; y < height; y++)
            {
                int row = at + (y * stride);
                Vector128<short> d = Sse2.Subtract(Widen(Load4(ref ra4, row)), Widen(Load4(ref rb4, row)));
                sum4 += Sse2.MultiplyAddAdjacent(d, d);
            }

            return Vector128.Sum(sum4.AsUInt32());
        }

        if (!Sse2.IsSupported || (width & 7) != 0)
        {
            long total = 0;
            for (int y = 0; y < height; y++)
            {
                int row = at + (y * stride);
                for (int x = 0; x < width; x++)
                {
                    int e = a[row + x] - b[row + x];
                    total += e * e;
                }
            }

            return total;
        }

        ref byte ra = ref MemoryMarshal.GetArrayDataReference(a);
        ref byte rb = ref MemoryMarshal.GetArrayDataReference(b);
        Vector128<int> sum = Vector128<int>.Zero;
        for (int y = 0; y < height; y++)
        {
            int row = at + (y * stride);
            for (int x = 0; x < width; x += 8)
            {
                Vector128<short> d = Sse2.Subtract(Widen(Load8(ref ra, row + x)), Widen(Load8(ref rb, row + x)));
                sum += Sse2.MultiplyAddAdjacent(d, d);
            }
        }

        return Vector128.Sum(sum.AsUInt32());
    }

    /// <summary>The rounded mean (a + b + 1) >> 1 of a w by h block of two predictions, into <paramref name="a"/>.</summary>
    public static void Average(byte[] a, int atA, int strideA, byte[] b, int atB, int strideB, byte[] dst, int dstAt, int dstStride, int w, int h)
    {
        ref byte ra = ref MemoryMarshal.GetArrayDataReference(a);
        ref byte rb = ref MemoryMarshal.GetArrayDataReference(b);
        ref byte rd = ref MemoryMarshal.GetArrayDataReference(dst);
        if (Sse2.IsSupported && (w == 16 || w == 8))
        {
            for (int y = 0; y < h; y++)
            {
                int pa = atA + (y * strideA), pb = atB + (y * strideB), pd = dstAt + (y * dstStride);
                if (w == 16)
                {
                    Sse2.Average(Vector128.LoadUnsafe(ref ra, (nuint)pa), Vector128.LoadUnsafe(ref rb, (nuint)pb)).StoreUnsafe(ref rd, (nuint)pd);
                }
                else
                {
                    Unsafe.WriteUnaligned(ref Unsafe.Add(ref rd, pd), Sse2.Average(Load8(ref ra, pa), Load8(ref rb, pb)).AsUInt64().ToScalar());
                }
            }

            return;
        }

        for (int y = 0; y < h; y++)
        {
            int pa = atA + (y * strideA), pb = atB + (y * strideB), pd = dstAt + (y * dstStride);
            for (int x = 0; x < w; x++)
            {
                dst[pd + x] = (byte)((a[pa + x] + b[pb + x] + 1) >> 1);
            }
        }
    }

    /// <summary>The largest absolute value of a span.</summary>
    public static int MaxAbs(ReadOnlySpan<int> values)
    {
        int i = 0, max = 0;
        if (Vector256.IsHardwareAccelerated && values.Length >= 8)
        {
            Vector256<int> m = Vector256<int>.Zero;
            ref int start = ref MemoryMarshal.GetReference(values);
            for (; i + 8 <= values.Length; i += 8)
            {
                m = Vector256.Max(m, Vector256.Abs(Vector256.LoadUnsafe(ref start, (nuint)i)));
            }

            max = Math.Max(Math.Max(Math.Max(m[0], m[1]), Math.Max(m[2], m[3])), Math.Max(Math.Max(m[4], m[5]), Math.Max(m[6], m[7])));
        }

        for (; i < values.Length; i++)
        {
            max = Math.Max(max, Math.Abs(values[i]));
        }

        return max;
    }

    internal static long SatdScalar(byte[] src, int at, int stride, byte[] pred, int predAt, int predStride, int width, int height)
    {
        long sum = 0;
        Span<int> d = stackalloc int[16];
        for (int by = 0; by < height; by += 4)
        {
            for (int bx = 0; bx < width; bx += 4)
            {
                for (int y = 0; y < 4; y++)
                {
                    int s = at + ((by + y) * stride) + bx, p = predAt + ((by + y) * predStride) + bx;
                    int a0 = src[s] - pred[p], a1 = src[s + 1] - pred[p + 1], a2 = src[s + 2] - pred[p + 2], a3 = src[s + 3] - pred[p + 3];
                    int s01 = a0 + a1, d01 = a0 - a1, s23 = a2 + a3, d23 = a2 - a3;
                    d[(y * 4) + 0] = s01 + s23;
                    d[(y * 4) + 1] = s01 - s23;
                    d[(y * 4) + 2] = d01 - d23;
                    d[(y * 4) + 3] = d01 + d23;
                }

                for (int x = 0; x < 4; x++)
                {
                    int a0 = d[x], a1 = d[4 + x], a2 = d[8 + x], a3 = d[12 + x];
                    int s01 = a0 + a1, d01 = a0 - a1, s23 = a2 + a3, d23 = a2 - a3;
                    sum += Math.Abs(s01 + s23) + Math.Abs(s01 - s23) + Math.Abs(d01 - d23) + Math.Abs(d01 + d23);
                }
            }
        }

        return sum >> 1;
    }

    /// <summary>
    /// The sums of absolute 4x4 Hadamard coefficients of two blocks, lanes 0-3 of the rows one block and lanes 4-7 the other,
    /// spread over four int lanes.
    /// </summary>
    private static Vector128<int> Hadamard2(Vector128<short> r0, Vector128<short> r1, Vector128<short> r2, Vector128<short> r3)
    {
        // Down the columns.
        Vector128<short> s01 = r0 + r1, d01 = r0 - r1, s23 = r2 + r3, d23 = r2 - r3;
        Vector128<short> v0 = s01 + s23, v1 = s01 - s23, v2 = d01 - d23, v3 = d01 + d23;

        // Transpose each block: per block, columns 0|1 and 2|3 of the rows above.
        Vector128<short> a = Sse2.UnpackLow(v0, v1), b = Sse2.UnpackLow(v2, v3);
        Vector128<short> c = Sse2.UnpackHigh(v0, v1), d = Sse2.UnpackHigh(v2, v3);
        Vector128<short> col01A = Sse2.UnpackLow(a.AsInt32(), b.AsInt32()).AsInt16(), col23A = Sse2.UnpackHigh(a.AsInt32(), b.AsInt32()).AsInt16();
        Vector128<short> col01B = Sse2.UnpackLow(c.AsInt32(), d.AsInt32()).AsInt16(), col23B = Sse2.UnpackHigh(c.AsInt32(), d.AsInt32()).AsInt16();
        return Across(col01A, col23A) + Across(col01B, col23B);
    }

    /// <summary><see cref="Hadamard2"/> on four blocks, two in each 128-bit lane.</summary>
    private static Vector256<int> Hadamard4(Vector256<short> r0, Vector256<short> r1, Vector256<short> r2, Vector256<short> r3)
    {
        Vector256<short> s01 = r0 + r1, d01 = r0 - r1, s23 = r2 + r3, d23 = r2 - r3;
        Vector256<short> v0 = s01 + s23, v1 = s01 - s23, v2 = d01 - d23, v3 = d01 + d23;
        Vector256<short> a = Avx2.UnpackLow(v0, v1), b = Avx2.UnpackLow(v2, v3);
        Vector256<short> c = Avx2.UnpackHigh(v0, v1), d = Avx2.UnpackHigh(v2, v3);
        Vector256<short> col01A = Avx2.UnpackLow(a.AsInt32(), b.AsInt32()).AsInt16(), col23A = Avx2.UnpackHigh(a.AsInt32(), b.AsInt32()).AsInt16();
        Vector256<short> col01B = Avx2.UnpackLow(c.AsInt32(), d.AsInt32()).AsInt16(), col23B = Avx2.UnpackHigh(c.AsInt32(), d.AsInt32()).AsInt16();
        return Across4(col01A, col23A) + Across4(col01B, col23B);
    }

    private static Vector256<int> Across4(Vector256<short> c01, Vector256<short> c23)
    {
        Vector256<short> s = c01 + c23, t = c01 - c23;
        Vector256<short> u0 = Avx2.UnpackLow(s.AsInt64(), t.AsInt64()).AsInt16(), u1 = Avx2.UnpackHigh(s.AsInt64(), t.AsInt64()).AsInt16();
        Vector256<short> e = Vector256.Abs(u0 + u1), f = Vector256.Abs(u0 - u1);
        return Avx2.MultiplyAddAdjacent(e, Vector256<short>.One) + Avx2.MultiplyAddAdjacent(f, Vector256<short>.One);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<short> Diff8x2(ref byte s, int at0, int at1, ref byte p, int pAt0, int pAt1) =>
        Avx2.Subtract(
            Avx2.ConvertToVector256Int16(Vector128.Create(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref s, at0)), Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref s, at1))).AsByte()),
            Avx2.ConvertToVector256Int16(Vector128.Create(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref p, pAt0)), Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref p, pAt1))).AsByte()));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Diff8(ref byte s, int at, ref byte p, int pAt) =>
        Vector128.WidenLower(Load8(ref s, at)).AsInt16() - Vector128.WidenLower(Load8(ref p, pAt)).AsInt16();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<short> Diff16(ref byte s, int at, ref byte p, int pAt) =>
        Avx2.Subtract(Avx2.ConvertToVector256Int16(Vector128.LoadUnsafe(ref s, (nuint)at)), Avx2.ConvertToVector256Int16(Vector128.LoadUnsafe(ref p, (nuint)pAt)));

    /// <summary>Across the rows of one block, given its columns 0|1 and 2|3; the absolute coefficients summed in pairs.</summary>
    private static Vector128<int> Across(Vector128<short> c01, Vector128<short> c23)
    {
        Vector128<short> s = c01 + c23, t = c01 - c23;
        Vector128<short> u0 = Sse2.UnpackLow(s.AsInt64(), t.AsInt64()).AsInt16(), u1 = Sse2.UnpackHigh(s.AsInt64(), t.AsInt64()).AsInt16();
        Vector128<short> e = Vector128.Abs(u0 + u1), f = Vector128.Abs(u0 - u1);
        return Sse2.MultiplyAddAdjacent(e, Vector128<short>.One) + Sse2.MultiplyAddAdjacent(f, Vector128<short>.One);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Diff(ref byte s, int at, ref byte p, int pAt, bool pair) => pair
        ? Sse2.Subtract(Widen(Load8(ref s, at)), Widen(Load8(ref p, pAt)))
        : Sse2.Subtract(Widen(Load4(ref s, at)), Widen(Load4(ref p, pAt)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Widen(Vector128<byte> v) => Sse2.UnpackLow(v, Vector128<byte>.Zero).AsInt16();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Load8(ref byte r, int at) => Vector128.CreateScalar(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref r, at))).AsByte();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Load4(ref byte r, int at) => Vector128.CreateScalar(Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref r, at))).AsByte();

    /// <summary>
    /// The SA8D of a block (sides multiples of 8): the magnitudes of each 8x8 block's Hadamard coefficients of the differences,
    /// a quarter of their sum (rounded) per 8x8 block.
    /// </summary>
    public static long Sa8d(byte[] src, int at, int stride, byte[] pred, int predAt, int predStride, int width, int height)
    {
        // Differences of ±255 grow at most 64 times through the 8x8 Hadamard: 16320 fits a short.
        ref byte rs = ref MemoryMarshal.GetArrayDataReference(src);
        ref byte rp = ref MemoryMarshal.GetArrayDataReference(pred);
        long total = 0;
        if (Avx2.IsSupported && (width & 15) == 0)
        {
            // Two 8x8 blocks side by side, one in each 128-bit lane.
            for (int by = 0; by < height; by += 8)
            {
                for (int bx = 0; bx < width; bx += 16)
                {
                    int s = at + (by * stride) + bx, p = predAt + (by * predStride) + bx;
                    Vector256<short> r0 = Diff16(ref rs, s, ref rp, p), r1 = Diff16(ref rs, s + stride, ref rp, p + predStride);
                    Vector256<short> r2 = Diff16(ref rs, s + (2 * stride), ref rp, p + (2 * predStride));
                    Vector256<short> r3 = Diff16(ref rs, s + (3 * stride), ref rp, p + (3 * predStride));
                    Vector256<short> r4 = Diff16(ref rs, s + (4 * stride), ref rp, p + (4 * predStride));
                    Vector256<short> r5 = Diff16(ref rs, s + (5 * stride), ref rp, p + (5 * predStride));
                    Vector256<short> r6 = Diff16(ref rs, s + (6 * stride), ref rp, p + (6 * predStride));
                    Vector256<short> r7 = Diff16(ref rs, s + (7 * stride), ref rp, p + (7 * predStride));
                    Hadamard8(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
                    Transpose8(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
                    Hadamard8(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
                    // A pair of magnitudes is at most 32640 and still fits a short; four would not.
                    Vector256<short> one = Vector256<short>.One;
                    Vector256<int> sum = Avx2.MultiplyAddAdjacent(Vector256.Abs(r0) + Vector256.Abs(r1), one) + Avx2.MultiplyAddAdjacent(Vector256.Abs(r2) + Vector256.Abs(r3), one)
                        + Avx2.MultiplyAddAdjacent(Vector256.Abs(r4) + Vector256.Abs(r5), one) + Avx2.MultiplyAddAdjacent(Vector256.Abs(r6) + Vector256.Abs(r7), one);
                    total += ((Vector128.Sum(sum.GetLower()) + 2) >> 2) + ((Vector128.Sum(sum.GetUpper()) + 2) >> 2);
                }
            }

            return total;
        }

        if (Sse2.IsSupported || AdvSimd.Arm64.IsSupported)
        {
            for (int by = 0; by < height; by += 8)
            {
                for (int bx = 0; bx < width; bx += 8)
                {
                    int s = at + (by * stride) + bx, p = predAt + (by * predStride) + bx;
                    Vector128<short> r0 = Diff8(ref rs, s, ref rp, p), r1 = Diff8(ref rs, s + stride, ref rp, p + predStride);
                    Vector128<short> r2 = Diff8(ref rs, s + (2 * stride), ref rp, p + (2 * predStride));
                    Vector128<short> r3 = Diff8(ref rs, s + (3 * stride), ref rp, p + (3 * predStride));
                    Vector128<short> r4 = Diff8(ref rs, s + (4 * stride), ref rp, p + (4 * predStride));
                    Vector128<short> r5 = Diff8(ref rs, s + (5 * stride), ref rp, p + (5 * predStride));
                    Vector128<short> r6 = Diff8(ref rs, s + (6 * stride), ref rp, p + (6 * predStride));
                    Vector128<short> r7 = Diff8(ref rs, s + (7 * stride), ref rp, p + (7 * predStride));
                    Hadamard8(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
                    Transpose8(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);
                    Hadamard8(ref r0, ref r1, ref r2, ref r3, ref r4, ref r5, ref r6, ref r7);

                    // A pair of magnitudes is at most 32640 and still fits a short; widen before adding more.
                    Vector128<short> e0 = Vector128.Abs(r0) + Vector128.Abs(r1), e1 = Vector128.Abs(r2) + Vector128.Abs(r3);
                    Vector128<short> e2 = Vector128.Abs(r4) + Vector128.Abs(r5), e3 = Vector128.Abs(r6) + Vector128.Abs(r7);
                    Vector128<int> sum = Vector128.WidenLower(e0.AsUInt16()).AsInt32() + Vector128.WidenUpper(e0.AsUInt16()).AsInt32()
                        + Vector128.WidenLower(e1.AsUInt16()).AsInt32() + Vector128.WidenUpper(e1.AsUInt16()).AsInt32()
                        + Vector128.WidenLower(e2.AsUInt16()).AsInt32() + Vector128.WidenUpper(e2.AsUInt16()).AsInt32()
                        + Vector128.WidenLower(e3.AsUInt16()).AsInt32() + Vector128.WidenUpper(e3.AsUInt16()).AsInt32();
                    total += (Vector128.Sum(sum) + 2) >> 2;
                }
            }

            return total;
        }

        return Sa8dScalar(src, at, stride, pred, predAt, predStride, width, height);
    }

    internal static long Sa8dScalar(byte[] src, int at, int stride, byte[] pred, int predAt, int predStride, int width, int height)
    {
        Span<int> d = stackalloc int[64];
        long total = 0;
        for (int by = 0; by < height; by += 8)
        {
            for (int bx = 0; bx < width; bx += 8)
            {
                for (int y = 0; y < 8; y++)
                {
                    int s = at + ((by + y) * stride) + bx, p = predAt + ((by + y) * predStride) + bx;
                    Span<int> row = d.Slice(y * 8, 8);
                    for (int x = 0; x < 8; x++)
                    {
                        row[x] = src[s + x] - pred[p + x];
                    }

                    Hadamard8(row, 1);
                }

                long sum = 0;
                for (int x = 0; x < 8; x++)
                {
                    sum += Hadamard8(d[x..], 8);
                }

                total += (sum + 2) >> 2;
            }
        }

        return total;
    }

    /// <summary>The 8-point Hadamard transform down eight rows, lane by lane.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Hadamard8(ref Vector256<short> r0, ref Vector256<short> r1, ref Vector256<short> r2, ref Vector256<short> r3,
        ref Vector256<short> r4, ref Vector256<short> r5, ref Vector256<short> r6, ref Vector256<short> r7)
    {
        Vector256<short> a0 = r0 + r1, a1 = r0 - r1, a2 = r2 + r3, a3 = r2 - r3, a4 = r4 + r5, a5 = r4 - r5, a6 = r6 + r7, a7 = r6 - r7;
        Vector256<short> b0 = a0 + a2, b1 = a1 + a3, b2 = a0 - a2, b3 = a1 - a3, b4 = a4 + a6, b5 = a5 + a7, b6 = a4 - a6, b7 = a5 - a7;
        r0 = b0 + b4;
        r1 = b1 + b5;
        r2 = b2 + b6;
        r3 = b3 + b7;
        r4 = b0 - b4;
        r5 = b1 - b5;
        r6 = b2 - b6;
        r7 = b3 - b7;
    }

    /// <inheritdoc cref="Hadamard8(ref Vector256{short}, ref Vector256{short}, ref Vector256{short}, ref Vector256{short}, ref Vector256{short}, ref Vector256{short}, ref Vector256{short}, ref Vector256{short})"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Hadamard8(ref Vector128<short> r0, ref Vector128<short> r1, ref Vector128<short> r2, ref Vector128<short> r3,
        ref Vector128<short> r4, ref Vector128<short> r5, ref Vector128<short> r6, ref Vector128<short> r7)
    {
        Vector128<short> a0 = r0 + r1, a1 = r0 - r1, a2 = r2 + r3, a3 = r2 - r3, a4 = r4 + r5, a5 = r4 - r5, a6 = r6 + r7, a7 = r6 - r7;
        Vector128<short> b0 = a0 + a2, b1 = a1 + a3, b2 = a0 - a2, b3 = a1 - a3, b4 = a4 + a6, b5 = a5 + a7, b6 = a4 - a6, b7 = a5 - a7;
        r0 = b0 + b4;
        r1 = b1 + b5;
        r2 = b2 + b6;
        r3 = b3 + b7;
        r4 = b0 - b4;
        r5 = b1 - b5;
        r6 = b2 - b6;
        r7 = b3 - b7;
    }

    /// <summary>Transposes eight rows of eight shorts in each 128-bit lane.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose8(ref Vector256<short> r0, ref Vector256<short> r1, ref Vector256<short> r2, ref Vector256<short> r3,
        ref Vector256<short> r4, ref Vector256<short> r5, ref Vector256<short> r6, ref Vector256<short> r7)
    {
        Vector256<int> t0 = Avx2.UnpackLow(r0, r1).AsInt32(), t1 = Avx2.UnpackHigh(r0, r1).AsInt32();
        Vector256<int> t2 = Avx2.UnpackLow(r2, r3).AsInt32(), t3 = Avx2.UnpackHigh(r2, r3).AsInt32();
        Vector256<int> t4 = Avx2.UnpackLow(r4, r5).AsInt32(), t5 = Avx2.UnpackHigh(r4, r5).AsInt32();
        Vector256<int> t6 = Avx2.UnpackLow(r6, r7).AsInt32(), t7 = Avx2.UnpackHigh(r6, r7).AsInt32();
        Vector256<long> u0 = Avx2.UnpackLow(t0, t2).AsInt64(), u1 = Avx2.UnpackHigh(t0, t2).AsInt64();
        Vector256<long> u2 = Avx2.UnpackLow(t1, t3).AsInt64(), u3 = Avx2.UnpackHigh(t1, t3).AsInt64();
        Vector256<long> u4 = Avx2.UnpackLow(t4, t6).AsInt64(), u5 = Avx2.UnpackHigh(t4, t6).AsInt64();
        Vector256<long> u6 = Avx2.UnpackLow(t5, t7).AsInt64(), u7 = Avx2.UnpackHigh(t5, t7).AsInt64();
        r0 = Avx2.UnpackLow(u0, u4).AsInt16();
        r1 = Avx2.UnpackHigh(u0, u4).AsInt16();
        r2 = Avx2.UnpackLow(u1, u5).AsInt16();
        r3 = Avx2.UnpackHigh(u1, u5).AsInt16();
        r4 = Avx2.UnpackLow(u2, u6).AsInt16();
        r5 = Avx2.UnpackHigh(u2, u6).AsInt16();
        r6 = Avx2.UnpackLow(u3, u7).AsInt16();
        r7 = Avx2.UnpackHigh(u3, u7).AsInt16();
    }

    /// <summary>Transposes eight rows of eight shorts (SSE2 or Arm64).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose8(ref Vector128<short> r0, ref Vector128<short> r1, ref Vector128<short> r2, ref Vector128<short> r3,
        ref Vector128<short> r4, ref Vector128<short> r5, ref Vector128<short> r6, ref Vector128<short> r7)
    {
        Vector128<int> t0 = ZipLow(r0, r1).AsInt32(), t1 = ZipHigh(r0, r1).AsInt32();
        Vector128<int> t2 = ZipLow(r2, r3).AsInt32(), t3 = ZipHigh(r2, r3).AsInt32();
        Vector128<int> t4 = ZipLow(r4, r5).AsInt32(), t5 = ZipHigh(r4, r5).AsInt32();
        Vector128<int> t6 = ZipLow(r6, r7).AsInt32(), t7 = ZipHigh(r6, r7).AsInt32();
        Vector128<long> u0 = ZipLow(t0, t2).AsInt64(), u1 = ZipHigh(t0, t2).AsInt64();
        Vector128<long> u2 = ZipLow(t1, t3).AsInt64(), u3 = ZipHigh(t1, t3).AsInt64();
        Vector128<long> u4 = ZipLow(t4, t6).AsInt64(), u5 = ZipHigh(t4, t6).AsInt64();
        Vector128<long> u6 = ZipLow(t5, t7).AsInt64(), u7 = ZipHigh(t5, t7).AsInt64();
        r0 = ZipLow(u0, u4).AsInt16();
        r1 = ZipHigh(u0, u4).AsInt16();
        r2 = ZipLow(u1, u5).AsInt16();
        r3 = ZipHigh(u1, u5).AsInt16();
        r4 = ZipLow(u2, u6).AsInt16();
        r5 = ZipHigh(u2, u6).AsInt16();
        r6 = ZipLow(u3, u7).AsInt16();
        r7 = ZipHigh(u3, u7).AsInt16();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> ZipLow(Vector128<short> a, Vector128<short> b) => Sse2.IsSupported ? Sse2.UnpackLow(a, b) : AdvSimd.Arm64.ZipLow(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> ZipHigh(Vector128<short> a, Vector128<short> b) => Sse2.IsSupported ? Sse2.UnpackHigh(a, b) : AdvSimd.Arm64.ZipHigh(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> ZipLow(Vector128<int> a, Vector128<int> b) => Sse2.IsSupported ? Sse2.UnpackLow(a, b) : AdvSimd.Arm64.ZipLow(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> ZipHigh(Vector128<int> a, Vector128<int> b) => Sse2.IsSupported ? Sse2.UnpackHigh(a, b) : AdvSimd.Arm64.ZipHigh(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<long> ZipLow(Vector128<long> a, Vector128<long> b) => Sse2.IsSupported ? Sse2.UnpackLow(a, b) : AdvSimd.Arm64.ZipLow(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<long> ZipHigh(Vector128<long> a, Vector128<long> b) => Sse2.IsSupported ? Sse2.UnpackHigh(a, b) : AdvSimd.Arm64.ZipHigh(a, b);

    // An 8-point Hadamard transform in place, of the values step apart; returns the sum of the outputs' magnitudes.
    private static int Hadamard8(Span<int> v, int step)
    {
        int a0 = v[0] + v[step], a1 = v[0] - v[step], a2 = v[2 * step] + v[3 * step], a3 = v[2 * step] - v[3 * step];
        int a4 = v[4 * step] + v[5 * step], a5 = v[4 * step] - v[5 * step], a6 = v[6 * step] + v[7 * step], a7 = v[6 * step] - v[7 * step];
        int b0 = a0 + a2, b1 = a1 + a3, b2 = a0 - a2, b3 = a1 - a3, b4 = a4 + a6, b5 = a5 + a7, b6 = a4 - a6, b7 = a5 - a7;
        v[0] = b0 + b4;
        v[step] = b1 + b5;
        v[2 * step] = b2 + b6;
        v[3 * step] = b3 + b7;
        v[4 * step] = b0 - b4;
        v[5 * step] = b1 - b5;
        v[6 * step] = b2 - b6;
        v[7 * step] = b3 - b7;
        return Math.Abs(b0 + b4) + Math.Abs(b1 + b5) + Math.Abs(b2 + b6) + Math.Abs(b3 + b7) + Math.Abs(b0 - b4) + Math.Abs(b1 - b5)
            + Math.Abs(b2 - b6) + Math.Abs(b3 - b7);
    }
}
