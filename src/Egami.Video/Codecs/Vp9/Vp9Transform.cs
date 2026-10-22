using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Egami.Video.Codecs.Vp9;

/// <summary>
/// VP9's inverse transforms (vp9_idct.c and vpx_dsp/inv_txfm.c): rows then columns of the one-dimensional DCT and ADST, the final
/// rounding shift, and the sum with the prediction clipped to the bit depth. A DC-only DCT block takes libvpx's shortcut, which
/// gives the same values.
/// </summary>
[SkipLocalsInit]
internal static partial class Vp9Transform
{
    private const int C1 = 16364;
    private const int C2 = 16305;
    private const int C3 = 16207;
    private const int C4 = 16069;
    private const int C5 = 15893;
    private const int C6 = 15679;
    private const int C7 = 15426;
    private const int C8 = 15137;
    private const int C9 = 14811;
    private const int C10 = 14449;
    private const int C11 = 14053;
    private const int C12 = 13623;
    private const int C13 = 13160;
    private const int C14 = 12665;
    private const int C15 = 12140;
    private const int C16 = 11585;
    private const int C17 = 11003;
    private const int C18 = 10394;
    private const int C19 = 9760;
    private const int C20 = 9102;
    private const int C21 = 8423;
    private const int C22 = 7723;
    private const int C23 = 7005;
    private const int C24 = 6270;
    private const int C25 = 5520;
    private const int C26 = 4756;
    private const int C27 = 3981;
    private const int C28 = 3196;
    private const int C29 = 2404;
    private const int C30 = 1606;
    private const int C31 = 804;
    private const int S1 = 5283;
    private const int S2 = 9929;
    private const int S3 = 13377;
    private const int S4 = 15212;

    /// <summary>dct_const_round_shift: rounds away the 14 fraction bits of the cosine constants.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long Rs(long value) => (value + (1 << 13)) >> 14;

    private delegate void Transform1D(ReadOnlySpan<int> input, Span<int> output);

    private static readonly Transform1D[] Dct = [Idct4, Idct8, Idct16, Idct32];

    private static readonly Transform1D[] Adst = [Iadst4, Iadst8, Iadst16, Idct32];

    /// <summary>
    /// Inverse transforms <paramref name="coefficients"/> (raster order, the block's width per row) and adds the result to
    /// <paramref name="dst"/>. <paramref name="txType"/> is DCT_DCT, ADST_DCT (vertical ADST), DCT_ADST or ADST_ADST.
    /// </summary>
    public static void InverseAdd<T>(ReadOnlySpan<int> coefficients, Span<T> dst, int stride, int txSize, int txType, int eob, int bitDepth)
        where T : unmanaged, IBinaryInteger<T>
    {
        if (typeof(T) == typeof(byte) && Vp9TransformSimd.IsSupported)
        {
            Vp9TransformSimd.InverseAdd(coefficients, MemoryMarshal.Cast<T, byte>(dst), stride, txSize, txType, eob);
            return;
        }

        int n = 4 << txSize;
        int shift = txSize switch { 0 => 4, 1 => 5, _ => 6 };
        int max = (1 << bitDepth) - 1;
        if (txType == Vp9Tables.DctDct && eob == 1)
        {
            // DC only (vpx_idctNxN_1_add).
            int dc = (int)Rs(coefficients[0] * (long)C16);
            dc = (int)Rs(dc * (long)C16);
            int a1 = (dc + (1 << (shift - 1))) >> shift;
            for (int y = 0; y < n; y++)
            {
                Span<T> row = dst.Slice(y * stride, n);
                for (int x = 0; x < n; x++)
                {
                    row[x] = T.CreateTruncating(Math.Clamp(int.CreateTruncating(row[x]) + a1, 0, max));
                }
            }

            return;
        }

        Transform1D rows = txType is 0 or 1 ? Dct[txSize] : Adst[txSize];
        Transform1D cols = txType is 0 or 2 ? Dct[txSize] : Adst[txSize];
        Span<int> output = stackalloc int[32 * 32];
        Span<int> tempIn = stackalloc int[32];
        Span<int> tempOut = stackalloc int[32];
        for (int i = 0; i < n; i++)
        {
            ReadOnlySpan<int> input = coefficients.Slice(i * n, n);
            Span<int> outRow = output.Slice(i * n, n);
            if (txSize == Vp9Tables.Tx32x32 && !AnyNonZero(input))
            {
                outRow.Clear();
            }
            else
            {
                rows(input, outRow);
            }
        }

        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                tempIn[j] = output[(j * n) + i];
            }

            cols(tempIn[..n], tempOut[..n]);
            for (int j = 0; j < n; j++)
            {
                ref T pixel = ref dst[(j * stride) + i];
                int value = (tempOut[j] + (1 << (shift - 1))) >> shift;
                pixel = T.CreateTruncating(Math.Clamp(int.CreateTruncating(pixel) + value, 0, max));
            }
        }
    }

    /// <summary>One row's transform, the DCT or the ADST of a size (32 has only the DCT).</summary>
    internal static void Row(ReadOnlySpan<int> input, Span<int> output, int txSize, bool adst) =>
        (adst ? Adst : Dct)[txSize](input, output);

    /// <summary>The lossless 4x4 inverse Walsh-Hadamard transform (vpx_iwht4x4_16_add and its DC-only form, which agree).</summary>
    public static void Iwht4x4Add<T>(ReadOnlySpan<int> input, Span<T> dst, int stride, int eob, int bitDepth)
        where T : unmanaged, IBinaryInteger<T>
    {
        _ = eob;
        int max = (1 << bitDepth) - 1;
        Span<int> output = stackalloc int[16];
        for (int i = 0; i < 4; i++)
        {
            long a1 = input[(i * 4) + 0] >> 2;
            long c1 = input[(i * 4) + 1] >> 2;
            long d1 = input[(i * 4) + 2] >> 2;
            long b1 = input[(i * 4) + 3] >> 2;
            a1 += c1;
            d1 -= b1;
            long e1 = (a1 - d1) >> 1;
            b1 = e1 - b1;
            c1 = e1 - c1;
            a1 -= b1;
            d1 += c1;
            output[(i * 4) + 0] = (int)a1;
            output[(i * 4) + 1] = (int)b1;
            output[(i * 4) + 2] = (int)c1;
            output[(i * 4) + 3] = (int)d1;
        }

        for (int i = 0; i < 4; i++)
        {
            long a1 = output[i];
            long c1 = output[4 + i];
            long d1 = output[8 + i];
            long b1 = output[12 + i];
            a1 += c1;
            d1 -= b1;
            long e1 = (a1 - d1) >> 1;
            b1 = e1 - b1;
            c1 = e1 - c1;
            a1 -= b1;
            d1 += c1;
            Add(ref dst[i], (int)a1, max);
            Add(ref dst[stride + i], (int)b1, max);
            Add(ref dst[(2 * stride) + i], (int)c1, max);
            Add(ref dst[(3 * stride) + i], (int)d1, max);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Add<T>(ref T pixel, int value, int max)
        where T : unmanaged, IBinaryInteger<T> =>
        pixel = T.CreateTruncating(Math.Clamp(int.CreateTruncating(pixel) + value, 0, max));

    private static bool AnyNonZero(ReadOnlySpan<int> values)
    {
        // libvpx ORs the row into an int16_t for 8-bit and a tran_low_t above; for valid streams both mean "any nonzero".
        foreach (int v in values)
        {
            if (v != 0)
            {
                return true;
            }
        }

        return false;
    }
}
