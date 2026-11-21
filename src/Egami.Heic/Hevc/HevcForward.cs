using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Egami.Heic.Hevc;

/// <summary>
/// The encoder's side of H.265 8.6: the forward DCT of 4x4 to 32x32 and the 4x4 DST (the transposes of the decoder's matrices,
/// with the usual stage shifts log2 + bitDepth - 9 and log2 + 6), transform skip, a dead-zone quantizer whose levels the decoder's
/// scaling (<see cref="HevcTransform.Scale"/>) turns back into the coefficients, and sign data hiding. Coefficients come out
/// scaled by 2^(15 - bitDepth - log2) against an orthonormal transform of the samples, which the quantizer and RDOQ count on.
/// </summary>
[SkipLocalsInit]
internal static class HevcForward
{
    /// <summary>The quantizer's scales by QP % 6, about 2^20 / the decoder's levelScale.</summary>
    internal static readonly int[] QuantScale = [26214, 23302, 20560, 18396, 16384, 14564];

    /// <summary>The n x n DCT matrices by log2(n), 2 to 5, rows of <see cref="HevcTransform.Matrix"/>.</summary>
    private static readonly short[][] Matrices = MakeMatrices();

    /// <summary>The 4x4 DST matrix (8-317).</summary>
    private static readonly short[] Dst = [29, 55, 74, 84, 74, 74, 0, -74, 84, -29, -74, 55, 55, -84, 74, -29];

    private static short[][] MakeMatrices()
    {
        short[][] matrices = new short[6][];
        for (int log2 = 2; log2 <= 5; log2++)
        {
            int n = 1 << log2, step = 32 >> log2;
            matrices[log2] = new short[n * n];
            for (int k = 0; k < n; k++)
            {
                for (int i = 0; i < n; i++)
                {
                    matrices[log2][(k * n) + i] = HevcTransform.Matrix[(k * step * 32) + i];
                }
            }
        }

        return matrices;
    }

    /// <summary>The n x n DCT matrices as ints, and transposed: entry x n + k is row k, column x.</summary>
    private static readonly int[][] Wide = [.. Matrices.Select(m => m is null ? null! : m.Select(v => (int)v).ToArray())];

    private static readonly int[][] Transposed = [.. Matrices.Select((m, log2) => m is null ? null! : Transpose(m, 1 << log2))];

    private static int[] Transpose(short[] m, int n)
    {
        int[] t = new int[n * n];
        for (int k = 0; k < n; k++)
        {
            for (int x = 0; x < n; x++)
            {
                t[(x * n) + k] = m[(k * n) + x];
            }
        }

        return t;
    }

    /// <summary>
    /// The forward transform of an n x n residual block (raster order) into coefficients: the DST for 4x4 luma intra blocks,
    /// the DCT otherwise. Each stage adds a matrix row (or column) times one input to eight outputs at once.
    /// </summary>
    [SkipLocalsInit]
    internal static void Transform(ReadOnlySpan<int> residual, Span<int> coefficients, int log2, int bitDepth, bool dst)
    {
        int n = 1 << log2;
        int shift1 = log2 + bitDepth - 9, shift2 = log2 + 6;
        int round1 = 1 << (shift1 - 1), round2 = 1 << (shift2 - 1);
        if (n == 4 && Vector128.IsHardwareAccelerated)
        {
            Transform4(residual, coefficients, dst ? DstWide : Wide[2], dst ? DstTransposed : Transposed[2], shift1, shift2);
            return;
        }

        if (n == 4 || !Vector256.IsHardwareAccelerated)
        {
            TransformScalar(residual, coefficients, log2, dst ? Dst : Matrices[log2], shift1, shift2);
            return;
        }

        if (n >= 16 && Avx2.IsSupported)
        {
            TransformSplit(residual, coefficients, log2, shift1, shift2);
            return;
        }

        Span<int> temp = stackalloc int[32 * 32];
        ref int t0 = ref MemoryMarshal.GetArrayDataReference(Transposed[log2]);
        ref int m0 = ref MemoryMarshal.GetArrayDataReference(Wide[log2]);
        ref int r0 = ref MemoryMarshal.GetReference(residual);
        ref int temp0 = ref MemoryMarshal.GetReference(temp);
        ref int c0 = ref MemoryMarshal.GetReference(coefficients);
        Vector256<int> add1 = Vector256.Create(round1), add2 = Vector256.Create(round2);

        // First stage, along each row: temp[y][k] = sum over x of matrix[k][x] residual[y][x].
        for (int y = 0; y < n; y++)
        {
            for (int k = 0; k < n; k += 8)
            {
                Vector256<int> acc = add1;
                for (int x = 0; x < n; x++)
                {
                    acc += Vector256.LoadUnsafe(ref t0, (nuint)((x * n) + k)) * Unsafe.Add(ref r0, (y * n) + x);
                }

                Vector256.ShiftRightArithmetic(acc, shift1).StoreUnsafe(ref temp0, (nuint)((y * n) + k));
            }
        }

        // Second stage, along each column: coefficients[k][x] = sum over y of matrix[k][y] temp[y][x].
        for (int k = 0; k < n; k++)
        {
            for (int x = 0; x < n; x += 8)
            {
                Vector256<int> acc = add2;
                for (int y = 0; y < n; y++)
                {
                    acc += Vector256.LoadUnsafe(ref temp0, (nuint)((y * n) + x)) * Unsafe.Add(ref m0, (k * n) + y);
                }

                Vector256.ShiftRightArithmetic(acc, shift2).StoreUnsafe(ref c0, (nuint)((k * n) + x));
            }
        }
    }

    /// <summary>
    /// The halves of the 16x16 and 32x32 matrices that the even/odd split uses: entry x (n/2) + j is row 2j (even) or 2j + 1 (odd)
    /// at column x &lt; n/2.
    /// </summary>
    private static readonly int[][] EvenRows = [.. Matrices.Select((m, log2) => m is null || log2 < 4 ? null! : Half(m, 1 << log2, 0))];

    private static readonly int[][] OddRows = [.. Matrices.Select((m, log2) => m is null || log2 < 4 ? null! : Half(m, 1 << log2, 1))];

    private static int[] Half(short[] m, int n, int odd)
    {
        int h = n / 2;
        int[] t = new int[h * h];
        for (int j = 0; j < h; j++)
        {
            for (int x = 0; x < h; x++)
            {
                t[(x * h) + j] = m[(((2 * j) + odd) * n) + x];
            }
        }

        return t;
    }

    /// <summary>
    /// The 16x16 or 32x32 forward DCT by one even/odd split per stage, which halves the multiplications: each row of the matrix is
    /// symmetric (even rows) or antisymmetric (odd rows) about its middle, so a stage sums the input's two halves, and takes their
    /// difference, first. The sums are the full products' exactly, so the coefficients equal <see cref="Transform"/>'s. The first
    /// stage keeps its outputs even ones first, then odd ones, and the last store puts them back in order.
    /// </summary>
    private static void TransformSplit(ReadOnlySpan<int> residual, Span<int> coefficients, int log2, int shift1, int shift2)
    {
        int n = 1 << log2, h = n >> 1;
        Span<int> temp = stackalloc int[32 * 32];
        Span<int> sums = stackalloc int[16 * 32];
        Span<int> differences = stackalloc int[16 * 32];
        ref int even0 = ref MemoryMarshal.GetArrayDataReference(EvenRows[log2]);
        ref int odd0 = ref MemoryMarshal.GetArrayDataReference(OddRows[log2]);
        ref int m0 = ref MemoryMarshal.GetArrayDataReference(Wide[log2]);
        ref int r0 = ref MemoryMarshal.GetReference(residual);
        ref int temp0 = ref MemoryMarshal.GetReference(temp);
        ref int s0 = ref MemoryMarshal.GetReference(sums);
        ref int d0 = ref MemoryMarshal.GetReference(differences);
        ref int c0 = ref MemoryMarshal.GetReference(coefficients);
        Vector256<int> add1 = Vector256.Create(1 << (shift1 - 1)), add2 = Vector256.Create(1 << (shift2 - 1));

        // First stage, along each row: the sums and differences of its halves, then the even outputs from the sums and the odd
        // ones from the differences, eight at a time.
        for (int y = 0; y < n; y++)
        {
            ref int row = ref Unsafe.Add(ref r0, y * n);
            for (int x = 0; x < h; x++)
            {
                int a = Unsafe.Add(ref row, x), b = Unsafe.Add(ref row, n - 1 - x);
                Unsafe.Add(ref s0, x) = a + b;
                Unsafe.Add(ref d0, x) = a - b;
            }

            for (int j = 0; j < h; j += 8)
            {
                Vector256<int> accEven = add1, accOdd = add1;
                for (int x = 0; x < h; x++)
                {
                    accEven += Vector256.LoadUnsafe(ref even0, (nuint)((x * h) + j)) * Unsafe.Add(ref s0, x);
                    accOdd += Vector256.LoadUnsafe(ref odd0, (nuint)((x * h) + j)) * Unsafe.Add(ref d0, x);
                }

                Vector256.ShiftRightArithmetic(accEven, shift1).StoreUnsafe(ref temp0, (nuint)((y * n) + j));
                Vector256.ShiftRightArithmetic(accOdd, shift1).StoreUnsafe(ref temp0, (nuint)((y * n) + h + j));
            }
        }

        // Second stage, along each column: the sums and differences of the top and bottom halves' rows, then each output row from
        // them, eight columns at a time.
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < n; x += 8)
            {
                Vector256<int> a = Vector256.LoadUnsafe(ref temp0, (nuint)((y * n) + x)), b = Vector256.LoadUnsafe(ref temp0, (nuint)(((n - 1 - y) * n) + x));
                (a + b).StoreUnsafe(ref s0, (nuint)((y * n) + x));
                (a - b).StoreUnsafe(ref d0, (nuint)((y * n) + x));
            }
        }

        Span<int> line = stackalloc int[32];
        ref int line0 = ref MemoryMarshal.GetReference(line);
        for (int k = 0; k < n; k++)
        {
            ref int halves = ref (k & 1) == 0 ? ref s0 : ref d0;
            for (int x = 0; x < n; x += 8)
            {
                Vector256<int> acc = add2;
                for (int y = 0; y < h; y++)
                {
                    acc += Vector256.LoadUnsafe(ref halves, (nuint)((y * n) + x)) * Unsafe.Add(ref m0, (k * n) + y);
                }

                Vector256.ShiftRightArithmetic(acc, shift2).StoreUnsafe(ref line0, (nuint)x);
            }

            // Columns back in order: the even ones are the line's first half, the odd ones its second.
            ref int output = ref Unsafe.Add(ref c0, k * n);
            for (int j = 0; j < h; j += 8)
            {
                Vector256<int> evens = Vector256.LoadUnsafe(ref line0, (nuint)j), odds = Vector256.LoadUnsafe(ref line0, (nuint)(h + j));
                Vector256<int> low = Avx2.UnpackLow(evens, odds), high = Avx2.UnpackHigh(evens, odds);
                Avx2.Permute2x128(low, high, 0x20).StoreUnsafe(ref output, (nuint)(2 * j));
                Avx2.Permute2x128(low, high, 0x31).StoreUnsafe(ref output, (nuint)((2 * j) + 8));
            }
        }
    }

    private static readonly int[] DstWide = [.. Dst.Select(v => (int)v)];

    private static readonly int[] DstTransposed = Transpose(Dst, 4);

    /// <summary>A 4x4 forward transform with each row a vector: a stage adds a matrix column times one input to a whole row.</summary>
    private static void Transform4(ReadOnlySpan<int> residual, Span<int> coefficients, int[] matrix, int[] transposed, int shift1, int shift2)
    {
        ref int r0 = ref MemoryMarshal.GetReference(residual);
        ref int t0 = ref MemoryMarshal.GetArrayDataReference(transposed);
        ref int m0 = ref MemoryMarshal.GetArrayDataReference(matrix);
        Vector128<int> c0 = Vector128.LoadUnsafe(ref t0), c1 = Vector128.LoadUnsafe(ref t0, 4), c2 = Vector128.LoadUnsafe(ref t0, 8), c3 = Vector128.LoadUnsafe(ref t0, 12);
        Vector128<int> add1 = Vector128.Create(1 << (shift1 - 1)), add2 = Vector128.Create(1 << (shift2 - 1));
        Span<Vector128<int>> rows = stackalloc Vector128<int>[4];
        for (int y = 0; y < 4; y++)
        {
            ref int row = ref Unsafe.Add(ref r0, 4 * y);
            rows[y] = Vector128.ShiftRightArithmetic(add1 + (c0 * row) + (c1 * Unsafe.Add(ref row, 1)) + (c2 * Unsafe.Add(ref row, 2)) + (c3 * Unsafe.Add(ref row, 3)), shift1);
        }

        ref int out0 = ref MemoryMarshal.GetReference(coefficients);
        for (int k = 0; k < 4; k++)
        {
            ref int m = ref Unsafe.Add(ref m0, 4 * k);
            Vector128<int> acc = add2 + (rows[0] * m) + (rows[1] * Unsafe.Add(ref m, 1)) + (rows[2] * Unsafe.Add(ref m, 2)) + (rows[3] * Unsafe.Add(ref m, 3));
            Vector128.ShiftRightArithmetic(acc, shift2).StoreUnsafe(ref out0, (nuint)(4 * k));
        }
    }

    private static void TransformScalar(ReadOnlySpan<int> residual, Span<int> coefficients, int log2, short[] matrix, int shift1, int shift2)
    {
        int n = 1 << log2;
        int round1 = 1 << (shift1 - 1), round2 = 1 << (shift2 - 1);
        Span<int> temp = stackalloc int[32 * 32];
        for (int y = 0; y < n; y++)
        {
            for (int k = 0; k < n; k++)
            {
                int sum = round1;
                for (int x = 0; x < n; x++)
                {
                    sum += matrix[(k * n) + x] * residual[(y * n) + x];
                }

                temp[(y * n) + k] = sum >> shift1;
            }
        }

        for (int k = 0; k < n; k++)
        {
            for (int x = 0; x < n; x++)
            {
                int sum = round2;
                for (int y = 0; y < n; y++)
                {
                    sum += matrix[(k * n) + y] * temp[(y * n) + x];
                }

                coefficients[(k * n) + x] = sum >> shift2;
            }
        }
    }

    /// <summary>Transform skip (the inverse of 8.6.4.2's): each residual scaled up by 2^(15 - bitDepth - log2).</summary>
    internal static void Skip(ReadOnlySpan<int> residual, Span<int> coefficients, int log2, int bitDepth)
    {
        int shift = 15 - bitDepth - log2;
        int count = 1 << (2 * log2);
        for (int i = 0; i < count; i++)
        {
            coefficients[i] = shift >= 0 ? residual[i] << shift : residual[i] >> -shift;
        }
    }

    /// <summary>Gets the quantizer's shift for a block: 14 + qP / 6 + (15 - bitDepth - log2).</summary>
    internal static int QuantShift(int qp, int log2, int bitDepth) => 14 + (qp / 6) + 15 - bitDepth - log2;

    /// <summary>
    /// Quantizes coefficients with a dead zone (rounding offset 1/3 for intra blocks), sign kept: level = sign(c) (|c| scale +
    /// offset) &gt;&gt; shift. Returns the number of levels that are not zero.
    /// </summary>
    internal static int Quantize(ReadOnlySpan<int> coefficients, Span<short> levels, int log2, int qp, int bitDepth, int rounding = 171)
    {
        int count = 1 << (2 * log2);
        int scale = QuantScale[qp % 6];
        int shift = QuantShift(qp, log2, bitDepth);
        long offset = (long)rounding << (shift - 9);
        int nonZero = 0;
        for (int i = 0; i < count; i++)
        {
            int c = coefficients[i];
            long level = ((Math.Abs((long)c) * scale) + offset) >> shift;
            level = Math.Min(level, short.MaxValue);
            levels[i] = (short)(c < 0 ? -level : level);
            nonZero += level != 0 ? 1 : 0;
        }

        return nonZero;
    }

    /// <summary>
    /// Sign data hiding (7.4.9.11 seen from the encoder): in every 4x4 sub-block whose first and last levels in scan order lie more
    /// than 3 positions apart, makes the parity of the sum of absolute levels give the first level's sign, changing one level by
    /// one where the quantization error says it costs least. <paramref name="coefficients"/> are the unquantized coefficients.
    /// </summary>
    internal static void HideSigns(ReadOnlySpan<int> coefficients, Span<short> levels, int log2, int qp, int bitDepth, int scanIdx, int subBlocks = int.MaxValue)
    {
        int n = 1 << log2;
        int scale = QuantScale[qp % 6];
        int shift = QuantShift(qp, log2, bitDepth);
        byte[] inner = HevcScan.Get(scanIdx, 2);
        int log2Sb = log2 - 2;
        byte[] outer = HevcScan.Get(scanIdx, log2Sb);
        Span<int> positions = stackalloc int[16];
        for (int i = Math.Min(1 << (2 * log2Sb), subBlocks) - 1; i >= 0; i--)
        {
            int origin = (outer[(2 * i) + 1] << 2) * n + (outer[2 * i] << 2);
            int first = -1, last = -1, sum = 0;
            for (int p = 0; p < 16; p++)
            {
                positions[p] = origin + (inner[(2 * p) + 1] * n) + inner[2 * p];
                int level = levels[positions[p]];
                if (level != 0)
                {
                    if (first < 0)
                    {
                        first = p;
                    }

                    last = p;
                    sum += Math.Abs(level);
                }
            }

            if (first < 0 || last - first <= 3)
            {
                continue;
            }

            bool negative = levels[positions[first]] < 0;
            if ((sum & 1) == (negative ? 1 : 0))
            {
                continue;
            }

            // The change of least quantization error: up where the coefficient was rounded down most, down where it was rounded up.
            long best = long.MaxValue;
            int bestP = -1, bestDelta = 0;
            for (int p = 0; p < 16; p++)
            {
                int at = positions[p];
                int c = coefficients[at];
                long exact = Math.Abs((long)c) * scale;
                int level = Math.Abs(levels[at]);
                long error = exact - ((long)level << shift);
                if (level != 0)
                {
                    long up = (1L << shift) - error;
                    if (up < best && level < short.MaxValue)
                    {
                        (best, bestP, bestDelta) = (up, p, 1);
                    }

                    // Removing the first level would move the hidden sign to another level.
                    long down = (1L << shift) + error;
                    if (!(p == first && level == 1) && down < best)
                    {
                        (best, bestP, bestDelta) = (down, p, -1);
                    }
                }
                else if (p > first || (c < 0) == negative)
                {
                    // A new level before the first takes over the hidden sign, so it must have the same sign.
                    long up = (1L << shift) - error;
                    if (up < best && c != 0)
                    {
                        (best, bestP, bestDelta) = (up, p, 1);
                    }
                }
            }

            if (bestP < 0)
            {
                continue;
            }

            int target = positions[bestP];
            int magnitude = Math.Abs(levels[target]) + bestDelta;
            bool sign = levels[target] != 0 ? levels[target] < 0 : coefficients[target] < 0;
            levels[target] = (short)(sign ? -magnitude : magnitude);
        }
    }
}
