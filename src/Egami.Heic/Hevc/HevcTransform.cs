using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

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
    /// most 32 terms.
    /// </remarks>
    internal static void Inverse(int[] coefficients, int[] residual, int[] scratch, int log2, int bitDepth, bool dst, int lastRow, int lastColumn)
    {
        int n = 1 << log2;
        int[] matrix = dst ? DstMatrix : Matrices[log2];
        int bdShift = 20 - bitDepth;
        Span<int> sum = stackalloc int[32];
        sum = sum[..n];

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

            ShiftClip(sum, scratch.AsSpan(x * n, n), 7);
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
        Vector128<int> max = Vector128.Create((1 << bitDepth) - 1);
        ref int r0 = ref MemoryMarshal.GetArrayDataReference(residual);
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
