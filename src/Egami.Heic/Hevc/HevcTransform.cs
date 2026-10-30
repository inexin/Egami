using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Egami.Heic.Hevc;

/// <summary>
/// Scaling and transformation (H.265 8.6.2 to 8.6.4): dequantization with flat or scaling-list factors, the inverse DCT of
/// 4x4 to 32x32 and the 4x4 DST, transform skip, and the residual modifications of the range extensions (rotation, implicit
/// residual DPCM, cross-component prediction).
/// </summary>
internal static class HevcTransform
{
    private static readonly int[] LevelScale = [40, 45, 51, 57, 64, 72];

    /// <summary>The 32x32 DCT matrix (8.6.4.2), transMatrix[row][column]; the smaller DCTs use every 2nd, 4th or 8th row.</summary>
    internal static readonly short[] Matrix = MakeMatrix();

    /// <summary>The 4x4 DST matrix (8-317).</summary>
    private static readonly int[] DstMatrix = [29, 55, 74, 84, 74, 74, 0, -74, 84, -29, -74, 55, 55, -84, 74, -29];

    /// <summary>The n x n DCT matrices by log2(n), 2 to 5: rows 0, 32 / n, 2 * 32 / n ... of <see cref="Matrix"/>, n columns each.</summary>
    private static readonly int[][] Matrices = MakeMatrices();

    private static int[][] MakeMatrices()
    {
        int[][] matrices = new int[6][];
        for (int log2 = 2; log2 <= 5; log2++)
        {
            int n = 1 << log2, step = 32 >> log2;
            matrices[log2] = new int[n * n];
            for (int k = 0; k < n; k++)
            {
                for (int i = 0; i < n; i++)
                {
                    matrices[log2][(k * n) + i] = Matrix[(k * step * 32) + i];
                }
            }
        }

        return matrices;
    }

    private static short[] MakeMatrix()
    {
        // The value for cos(j * pi / 64), j = 0 .. 32, as HEVC rounds it (64 at j = 0 and 16, 0 at 32).
        int[] c = new int[33];
        int[] odd32 = [90, 90, 88, 85, 82, 78, 73, 67, 61, 54, 46, 38, 31, 22, 13, 4];
        int[] odd16 = [90, 87, 80, 70, 57, 43, 25, 9];
        int[] odd8 = [89, 75, 50, 18];
        for (int i = 0; i < 16; i++)
        {
            c[(2 * i) + 1] = odd32[i];
        }

        for (int i = 0; i < 8; i++)
        {
            c[(4 * i) + 2] = odd16[i];
        }

        for (int i = 0; i < 4; i++)
        {
            c[(8 * i) + 4] = odd8[i];
        }

        c[8] = 83;
        c[24] = 36;
        c[16] = 64;
        c[0] = 64;
        c[32] = 0;
        short[] matrix = new short[32 * 32];
        for (int k = 0; k < 32; k++)
        {
            for (int n = 0; n < 32; n++)
            {
                int a = k * ((2 * n) + 1) % 128;
                if (a > 64)
                {
                    a = 128 - a;
                }

                matrix[(k * 32) + n] = (short)(a > 32 ? -c[64 - a] : c[a]);
            }
        }

        return matrix;
    }

    /// <summary>m * levelScale[qP % 6] &lt;&lt; (qP / 6) of <see cref="Scale"/> with flat scaling, m = 16.</summary>
    internal static long FlatScale(int qp) => (16L * LevelScale[qp % 6]) << (qp / 6);

    /// <summary>
    /// Scales coefficient levels in place (8.6.3): d = Clip3(-32768, 32767, (level * m * levelScale[qP % 6] &lt;&lt; (qP / 6) +
    /// (1 &lt;&lt; (bdShift - 1))) &gt;&gt; bdShift), m from the scaling factors or 16. The levels lie in rows 0 to
    /// <paramref name="lastRow"/> and columns 0 to <paramref name="lastColumn"/>.
    /// </summary>
    internal static void Scale(int[] coefficients, int log2, int qp, int bitDepth, int[]? factors, int lastRow, int lastColumn)
    {
        int n = 1 << log2;
        int bdShift = bitDepth + log2 - 5;
        long scale = (long)LevelScale[qp % 6] << (qp / 6);
        long add = 1L << (bdShift - 1);
        for (int y = 0; y <= lastRow; y++)
        {
            for (int i = y * n; i <= (y * n) + lastColumn; i++)
            {
                int level = coefficients[i];
                if (level != 0)
                {
                    long m = factors?[i] ?? 16;
                    long d = ((level * m * scale) + add) >> bdShift;
                    coefficients[i] = (int)Math.Clamp(d, short.MinValue, short.MaxValue);
                }
            }
        }
    }

    /// <summary>
    /// Transforms scaled coefficients into residuals (8.6.4.2): the inverse DST for 4x4 luma intra blocks, the inverse DCT
    /// otherwise, columns first with clipping to 16 bits between the stages. <paramref name="lastRow"/> and
    /// <paramref name="lastColumn"/> bound the non-zero coefficients.
    /// </summary>
    /// <remarks>
    /// Each stage adds coefficient times matrix row to a whole output row at once. The sums fit 32 bits: the coefficients and
    /// the first stage's outputs are 16-bit (extended precision is not decoded), the matrix entries at most 90, and there are at
    /// most 32 terms. With AVX2 the terms go two at a time: a pair of 16-bit inputs times the interleaved matrix rows of
    /// <see cref="Pairs"/> in one multiply-add.
    /// </remarks>
    internal static void Inverse(int[] coefficients, int[] residual, short[] scratch, int log2, int bitDepth, bool dst, int lastRow, int lastColumn)
    {
        int n = 1 << log2;
        int bdShift = 20 - bitDepth;
        if (!Avx2.IsSupported)
        {
            InverseScalar(coefficients, residual, log2, bitDepth, dst, lastRow, lastColumn);
            return;
        }

        short[] pairs = dst ? DstPairs : Pairs[log2];
        if (n == 4)
        {
            Inverse4(coefficients, residual, pairs, bdShift);
            return;
        }

        Span<int> inputs = stackalloc int[16];
        Span<int> rows = stackalloc int[16];

        // First stage: column x of the coefficients gives column x of the intermediate block, stored as scratch row x.
        ref int c0 = ref MemoryMarshal.GetArrayDataReference(coefficients);
        ref short s0 = ref MemoryMarshal.GetArrayDataReference(scratch);
        for (int x = 0; x <= lastColumn; x++)
        {
            int count = 0;
            for (int k = 0; k <= lastRow; k += 2)
            {
                int pair = (Unsafe.Add(ref c0, (k * n) + x) & 0xFFFF) | (Unsafe.Add(ref c0, ((k + 1) * n) + x) << 16);
                if (pair != 0)
                {
                    inputs[count] = pair;
                    rows[count++] = k >> 1;
                }
            }

            Stage(inputs[..count], rows, pairs, n, ref Unsafe.Add(ref s0, x * n), 7);
        }

        // A last column of even index leaves the odd scratch row after it unwritten; the second stage reads it as a pair's half.
        if ((lastColumn & 1) == 0)
        {
            scratch.AsSpan((lastColumn + 1) * n, n).Clear();
        }

        // Second stage: row y of the intermediate block (column y of scratch) gives row y of the residual.
        ref int r0 = ref MemoryMarshal.GetArrayDataReference(residual);
        for (int y = 0; y < n; y++)
        {
            int count = 0;
            for (int k = 0; k <= lastColumn; k += 2)
            {
                int pair = (ushort)Unsafe.Add(ref s0, (k * n) + y) | (Unsafe.Add(ref s0, ((k + 1) * n) + y) << 16);
                if (pair != 0)
                {
                    inputs[count] = pair;
                    rows[count++] = k >> 1;
                }
            }

            Stage(inputs[..count], rows, pairs, n, ref Unsafe.Add(ref r0, y * n), bdShift);
        }
    }

    /// <summary>
    /// The 4x4 transform in registers: the coefficient rows interleaved in pairs (rows 0 and 1, rows 2 and 3) times each output
    /// row's pair of matrix entries, then the intermediate rows' sample pairs times the interleaved matrix rows.
    /// </summary>
    private static void Inverse4(int[] coefficients, int[] residual, short[] pairs, int bdShift)
    {
        ref int c0 = ref MemoryMarshal.GetArrayDataReference(coefficients);
        ref short m0 = ref MemoryMarshal.GetArrayDataReference(pairs);
        Vector128<short> rows02 = Sse2.PackSignedSaturate(Vector128.LoadUnsafe(ref c0), Vector128.LoadUnsafe(ref c0, 8));
        Vector128<short> rows13 = Sse2.PackSignedSaturate(Vector128.LoadUnsafe(ref c0, 4), Vector128.LoadUnsafe(ref c0, 12));
        Vector128<short> p01 = Sse2.UnpackLow(rows02, rows13), p23 = Sse2.UnpackHigh(rows02, rows13);
        Vector128<int> m01 = Vector128.LoadUnsafe(ref m0).AsInt32(), m23 = Vector128.LoadUnsafe(ref m0, 8).AsInt32();
        Vector128<int> round = Vector128.Create(64);
        Vector128<int> g0 = (Sse2.MultiplyAddAdjacent(p01, Sse2.Shuffle(m01, 0x00).AsInt16()) + Sse2.MultiplyAddAdjacent(p23, Sse2.Shuffle(m23, 0x00).AsInt16()) + round) >> 7;
        Vector128<int> g1 = (Sse2.MultiplyAddAdjacent(p01, Sse2.Shuffle(m01, 0x55).AsInt16()) + Sse2.MultiplyAddAdjacent(p23, Sse2.Shuffle(m23, 0x55).AsInt16()) + round) >> 7;
        Vector128<int> g2 = (Sse2.MultiplyAddAdjacent(p01, Sse2.Shuffle(m01, 0xAA).AsInt16()) + Sse2.MultiplyAddAdjacent(p23, Sse2.Shuffle(m23, 0xAA).AsInt16()) + round) >> 7;
        Vector128<int> g3 = (Sse2.MultiplyAddAdjacent(p01, Sse2.Shuffle(m01, 0xFF).AsInt16()) + Sse2.MultiplyAddAdjacent(p23, Sse2.Shuffle(m23, 0xFF).AsInt16()) + round) >> 7;

        // Intermediate rows 0 and 1, and 2 and 3, clipped to 16 bits: as ints, pairs of samples (0, 1) and (2, 3) of each row.
        Vector128<int> g01 = Sse2.PackSignedSaturate(g0, g1).AsInt32(), g23 = Sse2.PackSignedSaturate(g2, g3).AsInt32();
        Vector128<short> pm01 = m01.AsInt16(), pm23 = m23.AsInt16();
        Vector128<int> add = Vector128.Create(1 << (bdShift - 1));
        ref int r0 = ref MemoryMarshal.GetArrayDataReference(residual);
        ((Sse2.MultiplyAddAdjacent(Sse2.Shuffle(g01, 0x00).AsInt16(), pm01) + Sse2.MultiplyAddAdjacent(Sse2.Shuffle(g01, 0x55).AsInt16(), pm23) + add) >> bdShift).StoreUnsafe(ref r0);
        ((Sse2.MultiplyAddAdjacent(Sse2.Shuffle(g01, 0xAA).AsInt16(), pm01) + Sse2.MultiplyAddAdjacent(Sse2.Shuffle(g01, 0xFF).AsInt16(), pm23) + add) >> bdShift).StoreUnsafe(ref r0, 4);
        ((Sse2.MultiplyAddAdjacent(Sse2.Shuffle(g23, 0x00).AsInt16(), pm01) + Sse2.MultiplyAddAdjacent(Sse2.Shuffle(g23, 0x55).AsInt16(), pm23) + add) >> bdShift).StoreUnsafe(ref r0, 8);
        ((Sse2.MultiplyAddAdjacent(Sse2.Shuffle(g23, 0xAA).AsInt16(), pm01) + Sse2.MultiplyAddAdjacent(Sse2.Shuffle(g23, 0xFF).AsInt16(), pm23) + add) >> bdShift).StoreUnsafe(ref r0, 12);
    }

    /// <summary>One output row of a transform stage: the sum of each input pair times its interleaved matrix rows, rounded and shifted.</summary>
    private static void Stage(ReadOnlySpan<int> inputs, ReadOnlySpan<int> rows, short[] pairs, int n, ref short destination, int shift)
    {
        ref short m0 = ref MemoryMarshal.GetArrayDataReference(pairs);
        if (n == 4)
        {
            Vector128<int> sum = Vector128.Create(1 << (shift - 1));
            for (int i = 0; i < inputs.Length; i++)
            {
                sum += Sse2.MultiplyAddAdjacent(Vector128.Create(inputs[i]).AsInt16(), Vector128.LoadUnsafe(ref m0, (nuint)(rows[i] * 8)));
            }

            Unsafe.WriteUnaligned(ref Unsafe.As<short, byte>(ref destination), Sse2.PackSignedSaturate(sum >> shift, sum >> shift).AsUInt64().ToScalar());
            return;
        }

        for (int c = 0; c < n; c += 8)
        {
            Vector256<int> sum = Vector256.Create(1 << (shift - 1));
            for (int i = 0; i < inputs.Length; i++)
            {
                sum += Avx2.MultiplyAddAdjacent(Vector256.Create(inputs[i]).AsInt16(), Vector256.LoadUnsafe(ref m0, (nuint)((rows[i] * 2 * n) + (2 * c))));
            }

            sum >>= shift;
            Sse2.PackSignedSaturate(sum.GetLower(), sum.GetUpper()).StoreUnsafe(ref destination, (nuint)c);
        }
    }

    /// <summary>As <see cref="Stage(ReadOnlySpan{int}, ReadOnlySpan{int}, short[], int, ref short, int)"/>, into 32-bit residuals without clipping.</summary>
    private static void Stage(ReadOnlySpan<int> inputs, ReadOnlySpan<int> rows, short[] pairs, int n, ref int destination, int shift)
    {
        ref short m0 = ref MemoryMarshal.GetArrayDataReference(pairs);
        if (n == 4)
        {
            Vector128<int> sum = Vector128.Create(1 << (shift - 1));
            for (int i = 0; i < inputs.Length; i++)
            {
                sum += Sse2.MultiplyAddAdjacent(Vector128.Create(inputs[i]).AsInt16(), Vector128.LoadUnsafe(ref m0, (nuint)(rows[i] * 8)));
            }

            (sum >> shift).StoreUnsafe(ref destination);
            return;
        }

        for (int c = 0; c < n; c += 8)
        {
            Vector256<int> sum = Vector256.Create(1 << (shift - 1));
            for (int i = 0; i < inputs.Length; i++)
            {
                sum += Avx2.MultiplyAddAdjacent(Vector256.Create(inputs[i]).AsInt16(), Vector256.LoadUnsafe(ref m0, (nuint)((rows[i] * 2 * n) + (2 * c))));
            }

            (sum >> shift).StoreUnsafe(ref destination, (nuint)c);
        }
    }

    /// <summary>
    /// The n x n DCT matrices by log2(n) with their rows interleaved in pairs: entry 2 (p n + j) is row 2p, column j, and the
    /// entry after it row 2p + 1, column j.
    /// </summary>
    private static readonly short[][] Pairs = MakePairs();

    private static readonly short[] DstPairs = Interleave(DstMatrix, 4);

    private static short[][] MakePairs()
    {
        short[][] pairs = new short[6][];
        for (int log2 = 2; log2 <= 5; log2++)
        {
            pairs[log2] = Interleave(Matrices[log2], 1 << log2);
        }

        return pairs;
    }

    private static short[] Interleave(int[] matrix, int n)
    {
        short[] pairs = new short[n * n];
        for (int p = 0; p < n / 2; p++)
        {
            for (int j = 0; j < n; j++)
            {
                pairs[2 * ((p * n) + j)] = (short)matrix[(2 * p * n) + j];
                pairs[(2 * ((p * n) + j)) + 1] = (short)matrix[(((2 * p) + 1) * n) + j];
            }
        }

        return pairs;
    }

    private static void InverseScalar(int[] coefficients, int[] residual, int log2, int bitDepth, bool dst, int lastRow, int lastColumn)
    {
        int n = 1 << log2;
        int[] matrix = dst ? DstMatrix : Matrices[log2];
        int bdShift = 20 - bitDepth;
        Span<int> sum = stackalloc int[32];
        sum = sum[..n];
        Span<int> scratch = stackalloc int[32 * 32];

        // First stage: column x of the coefficients gives column x of the intermediate block, stored as scratch row x.
        for (int x = 0; x <= lastColumn; x++)
        {
            sum.Clear();
            for (int k = 0; k <= lastRow; k++)
            {
                int d = coefficients[(k * n) + x];
                if (d != 0)
                {
                    MultiplyAdd(sum, matrix.AsSpan(k * n, n), d);
                }
            }

            ShiftClip(sum, scratch.Slice(x * n, n), 7);
        }

        // Second stage: row y of the intermediate block (column y of scratch) gives row y of the residual.
        for (int y = 0; y < n; y++)
        {
            sum.Clear();
            for (int k = 0; k <= lastColumn; k++)
            {
                int g = scratch[(k * n) + y];
                if (g != 0)
                {
                    MultiplyAdd(sum, matrix.AsSpan(k * n, n), g);
                }
            }

            Shift(sum, residual.AsSpan(y * n, n), bdShift);
        }
    }

    /// <summary>sum += row * factor; the length is a multiple of 4.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void MultiplyAdd(Span<int> sum, ReadOnlySpan<int> row, int factor)
    {
        ref int s0 = ref MemoryMarshal.GetReference(sum);
        ref int r0 = ref MemoryMarshal.GetReference(row);
        int i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            Vector256<int> f = Vector256.Create(factor);
            for (; i + 8 <= sum.Length; i += 8)
            {
                (Vector256.LoadUnsafe(ref s0, (nuint)i) + (Vector256.LoadUnsafe(ref r0, (nuint)i) * f)).StoreUnsafe(ref s0, (nuint)i);
            }
        }

        Vector128<int> f4 = Vector128.Create(factor);
        for (; i < sum.Length; i += 4)
        {
            (Vector128.LoadUnsafe(ref s0, (nuint)i) + (Vector128.LoadUnsafe(ref r0, (nuint)i) * f4)).StoreUnsafe(ref s0, (nuint)i);
        }
    }

    /// <summary>destination = Clip3(-32768, 32767, (sum + (1 &lt;&lt; (shift - 1))) &gt;&gt; shift).</summary>
    private static void ShiftClip(ReadOnlySpan<int> sum, Span<int> destination, int shift)
    {
        ref int s0 = ref MemoryMarshal.GetReference(sum);
        ref int d0 = ref MemoryMarshal.GetReference(destination);
        Vector128<int> round = Vector128.Create(1 << (shift - 1));
        Vector128<int> low = Vector128.Create((int)short.MinValue), high = Vector128.Create((int)short.MaxValue);
        for (int i = 0; i < sum.Length; i += 4)
        {
            Vector128<int> v = Vector128.ShiftRightArithmetic(Vector128.LoadUnsafe(ref s0, (nuint)i) + round, shift);
            Vector128.Min(Vector128.Max(v, low), high).StoreUnsafe(ref d0, (nuint)i);
        }
    }

    /// <summary>destination = (sum + (1 &lt;&lt; (shift - 1))) &gt;&gt; shift.</summary>
    private static void Shift(ReadOnlySpan<int> sum, Span<int> destination, int shift)
    {
        ref int s0 = ref MemoryMarshal.GetReference(sum);
        ref int d0 = ref MemoryMarshal.GetReference(destination);
        Vector128<int> round = Vector128.Create(1 << (shift - 1));
        for (int i = 0; i < sum.Length; i += 4)
        {
            Vector128.ShiftRightArithmetic(Vector128.LoadUnsafe(ref s0, (nuint)i) + round, shift).StoreUnsafe(ref d0, (nuint)i);
        }
    }

    /// <summary>Transform skip (8.6.4.2): r = (d &lt;&lt; tsShift + round) &gt;&gt; bdShift, tsShift = 5 + log2.</summary>
    internal static void Skip(int[] coefficients, int[] residual, int log2, int bitDepth)
    {
        int n = 1 << log2;
        int tsShift = 5 + log2, bdShift = 20 - bitDepth;
        long round = 1L << (bdShift - 1);
        for (int i = 0; i < n * n; i++)
        {
            residual[i] = (int)((((long)coefficients[i] << tsShift) + round) >> bdShift);
        }
    }

    /// <summary>Rotates a residual block by 180 degrees (transform_skip_rotation_enabled_flag, 8.6.2).</summary>
    internal static void Rotate(int[] block, int log2)
    {
        int count = 1 << (2 * log2);
        Array.Reverse(block, 0, count);
    }

    /// <summary>Residual DPCM (8.6.8): accumulates the residual along rows (horizontal, mode 10) or columns (vertical, 26).</summary>
    internal static void Dpcm(int[] residual, int log2, bool vertical)
    {
        int n = 1 << log2;
        if (vertical)
        {
            for (int y = 1; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    residual[(y * n) + x] += residual[((y - 1) * n) + x];
                }
            }
        }
        else
        {
            for (int y = 0; y < n; y++)
            {
                for (int x = 1; x < n; x++)
                {
                    residual[(y * n) + x] += residual[(y * n) + x - 1];
                }
            }
        }
    }

    /// <summary>Adds a residual to the predicted samples in the plane, clipping to the bit depth.</summary>
    internal static void Add(ushort[] plane, int at, int stride, int[] residual, int log2, int bitDepth)
    {
        int n = 1 << log2;
        ref int r0 = ref MemoryMarshal.GetArrayDataReference(residual);
        if (n >= 8 && Avx2.IsSupported)
        {
            Vector256<int> max8 = Vector256.Create((1 << bitDepth) - 1);
            ref ushort p8 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(plane), at);
            for (int y = 0; y < n; y++)
            {
                ref ushort row = ref Unsafe.Add(ref p8, y * stride);
                for (int x = 0; x < n; x += 8)
                {
                    Vector256<int> predicted = Avx2.ConvertToVector256Int32(Vector128.LoadUnsafe(ref row, (nuint)x));
                    // The pack clips below at 0; above, the bit depth's maximum.
                    Vector256<int> sum = Vector256.Min(predicted + Vector256.LoadUnsafe(ref r0, (nuint)((y * n) + x)), max8);
                    Sse41.PackUnsignedSaturate(sum.GetLower(), sum.GetUpper()).StoreUnsafe(ref row, (nuint)x);
                }
            }

            return;
        }

        Vector128<int> max = Vector128.Create((1 << bitDepth) - 1);
        for (int y = 0; y < n; y++)
        {
            Span<ushort> row = plane.AsSpan(at + (y * stride), n);
            ref ushort p0 = ref MemoryMarshal.GetReference(row);
            for (int x = 0; x < n; x += 4)
            {
                Vector128<int> predicted = Vector128.WidenLower(Vector64.LoadUnsafe(ref p0, (nuint)x).ToVector128Unsafe()).AsInt32();
                Vector128<int> sum = Vector128.Min(Vector128.Max(predicted + Vector128.LoadUnsafe(ref r0, (nuint)((y * n) + x)), Vector128<int>.Zero), max);
                Vector64<ushort> narrow = Vector128.Narrow(sum.AsUInt32(), sum.AsUInt32()).GetLower();
                narrow.StoreUnsafe(ref p0, (nuint)x);
            }
        }
    }
}
