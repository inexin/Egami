using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Egami.Formats.Av1;

/// <summary>
/// AV1's forward transforms for the encoder, after libaom's av1_fwd_txfm2d.c and hybrid_fwd_txfm.c (BSD 2-clause, see
/// THIRD-PARTY-NOTICES): columns, then rows, with libaom's shifts and cosine precisions per size, so the coefficients are the
/// ones libaom's encoder quantizes; the Walsh-Hadamard transform for lossless blocks. The output is column by column at the coded
/// size, as <see cref="Av1InverseTransform.Add"/> takes it: a transform 64 samples on a side keeps its 32 lowest frequencies.
/// Transforms 8 samples or more on each side run on eight columns, then eight rows, at once, with the same integer results.
/// </summary>
[System.Runtime.CompilerServices.SkipLocalsInit]
internal static partial class Av1ForwardTransform
{
    private const int NewSqrt2 = 5793;
    private const int NewSqrt2Bits = 12;

    /// <summary>av1_fwd_txfm_shift_ls: the shifts before the columns, after the columns and after the rows, per transform size.</summary>
    private static ReadOnlySpan<sbyte> Shifts =>
    [
        2, 0, 0, 2, -1, 0, 2, -2, 0, 2, -4, 0, 0, -2, -2, 2, -1, 0, 2, -1, 0, 2, -2, 0, 2, -2, 0, 2, -4, 0, 2, -4, 0, 0, -2, -2,
        2, -4, -2, 2, -1, 0, 2, -1, 0, 2, -2, 0, 2, -2, 0, 0, -2, 0, 2, -4, 0,
    ];

    /// <summary>av1_fwd_cos_bit_col[width index][height index].</summary>
    private static ReadOnlySpan<byte> CosBitCol => [13, 13, 13, 0, 0, 13, 13, 13, 12, 0, 13, 13, 13, 12, 13, 0, 13, 13, 12, 13, 0, 0, 13, 12, 13];

    /// <summary>av1_fwd_cos_bit_row[width index][height index].</summary>
    private static ReadOnlySpan<byte> CosBitRow => [13, 13, 12, 0, 0, 13, 13, 13, 12, 0, 13, 13, 12, 13, 12, 0, 12, 13, 12, 11, 0, 0, 12, 11, 10];

    /// <summary>
    /// Transforms a block of residuals into coefficients.
    /// </summary>
    /// <param name="residual">The residuals (source minus prediction), row by row.</param>
    /// <param name="stride">The residuals' stride.</param>
    /// <param name="output">The coefficients, column by column at the coded size; at least the transform's area long.</param>
    /// <param name="txSize">The transform size (<see cref="Tx"/>).</param>
    /// <param name="txType">The transform type (<see cref="TxType"/>).</param>
    /// <param name="lossless">Whether the block is lossless: the Walsh-Hadamard transform of 4x4 blocks.</param>
    internal static void Forward(ReadOnlySpan<int> residual, int stride, Span<int> output, int txSize, int txType, bool lossless)
    {
        if (lossless)
        {
            Wht4x4(residual, stride, output);
            return;
        }

        int w = Av1Tables.TxSizeWide[txSize], h = Av1Tables.TxSizeHigh[txSize];
        int wLog2 = Av1Tables.TxSizeWideLog2[txSize], hLog2 = Av1Tables.TxSizeHighLog2[txSize];
        int wIdx = wLog2 - 2, hIdx = hLog2 - 2;
        int colType = Av1Tables.VtxTab[txType], rowType = Av1Tables.HtxTab[txType];
        bool udFlip = colType == 2, lrFlip = rowType == 2;
        int shift0 = Shifts[txSize * 3], shift1 = Shifts[(txSize * 3) + 1], shift2 = Shifts[(txSize * 3) + 2];
        int cosBitCol = CosBitCol[(wIdx * 5) + hIdx], cosBitRow = CosBitRow[(wIdx * 5) + hIdx];
        bool rect2 = Math.Abs(wLog2 - hLog2) == 1;
        if (Vector256.IsHardwareAccelerated && w >= 8 && h >= 8)
        {
            ForwardVector(residual, stride, output, w, h, wIdx, hIdx, colType, rowType, udFlip, lrFlip, (shift0, shift1, shift2),
                (cosBitCol, cosBitRow), rect2);
            return;
        }

        Span<int> buffer = stackalloc int[64 * 64];
        Span<int> tempIn = stackalloc int[64];
        Span<int> tempOut = stackalloc int[64];
        for (int c = 0; c < w; c++)
        {
            for (int r = 0; r < h; r++)
            {
                tempIn[r] = residual[((udFlip ? h - r - 1 : r) * stride) + c];
            }

            RoundShiftArray(tempIn[..h], -shift0);
            Transform(hIdx, colType, tempIn, tempOut, cosBitCol);
            RoundShiftArray(tempOut[..h], -shift1);
            int column = lrFlip ? w - c - 1 : c;
            for (int r = 0; r < h; r++)
            {
                buffer[(r * w) + column] = tempOut[r];
            }
        }

        // The rows; coefficients beyond the 32 lowest frequencies of a 64-point side are dropped.
        int codedW = Math.Min(w, 32), codedH = Math.Min(h, 32);
        for (int r = 0; r < codedH; r++)
        {
            Transform(wIdx, rowType, buffer.Slice(r * w, w), tempOut, cosBitRow);
            RoundShiftArray(tempOut[..w], -shift2);
            for (int c = 0; c < codedW; c++)
            {
                int value = tempOut[c];
                if (rect2)
                {
                    value = RoundShift((long)value * NewSqrt2, NewSqrt2Bits);
                }

                output[(c * codedH) + r] = value;
            }
        }
    }

    /// <summary>
    /// The transform of <see cref="Forward"/> on vectors: the columns eight at a time, a column in each lane, so a row of eight
    /// residuals is one load; then the rows eight at a time, a row in each lane, so the coded output, column by column, takes
    /// one store per frequency.
    /// </summary>
    private static void ForwardVector(ReadOnlySpan<int> residual, int stride, Span<int> output, int w, int h, int wIdx, int hIdx,
        int colType, int rowType, bool udFlip, bool lrFlip, (int Before, int Columns, int Rows) shifts, (int Columns, int Rows) cosBits, bool rect2)
    {
        Span<int> buffer = stackalloc int[64 * 64];
        Span<Vector256<int>> tempIn = stackalloc Vector256<int>[64];
        Span<Vector256<int>> tempOut = stackalloc Vector256<int>[64];
        ref int res = ref MemoryMarshal.GetReference(residual);
        for (int c0 = 0; c0 < w; c0 += 8)
        {
            for (int r = 0; r < h; r++)
            {
                Vector256<int> v = Vector256.LoadUnsafe(ref res, (nuint)(((udFlip ? h - r - 1 : r) * stride) + c0));
                tempIn[r] = Shift(v, -shifts.Before);
            }

            TransformVector(hIdx, colType, tempIn[..h], tempOut, cosBits.Columns);
            int column = lrFlip ? w - c0 - 8 : c0;
            for (int r = 0; r < h; r++)
            {
                Vector256<int> v = Shift(tempOut[r], -shifts.Columns);
                if (lrFlip)
                {
                    v = Vector256.Shuffle(v, Vector256.Create(7, 6, 5, 4, 3, 2, 1, 0));
                }

                v.CopyTo(buffer.Slice((r * w) + column, 8));
            }
        }

        // The rows, eight at a time; coefficients beyond the 32 lowest frequencies of a 64-point side are dropped.
        int codedW = Math.Min(w, 32), codedH = Math.Min(h, 32);
        Span<int> lanes = stackalloc int[8];
        for (int r0 = 0; r0 < codedH; r0 += 8)
        {
            for (int c = 0; c < w; c++)
            {
                for (int k = 0; k < 8; k++)
                {
                    lanes[k] = buffer[((r0 + k) * w) + c];
                }

                tempIn[c] = Vector256.Create<int>(lanes);
            }

            TransformVector(wIdx, rowType, tempIn[..w], tempOut, cosBits.Rows);
            for (int c = 0; c < codedW; c++)
            {
                Vector256<int> v = Shift(tempOut[c], -shifts.Rows);
                if (rect2)
                {
                    v = Av1InverseTransform.Wide.Scale(v, NewSqrt2, NewSqrt2Bits);
                }

                v.CopyTo(output.Slice((c * codedH) + r0, 8));
            }
        }
    }

    /// <summary>Runs the one-dimensional transform of a size (1 for 8 points to 4 for 64) and type on each lane.</summary>
    private static void TransformVector(int sizeIdx, int type, ReadOnlySpan<Vector256<int>> input, Span<Vector256<int>> output, int cosBit)
    {
        switch ((sizeIdx, type))
        {
            case (1, 0): Dct8(input, output, cosBit); break;
            case (2, 0): Dct16(input, output, cosBit); break;
            case (3, 0): Dct32(input, output, cosBit); break;
            case (4, 0): Dct64(input, output, cosBit); break;
            case (1, 1 or 2): Adst8(input, output, cosBit); break;
            case (2, 1 or 2): Adst16(input, output, cosBit); break;
            case (1, 3):
                for (int i = 0; i < 8; i++)
                {
                    output[i] = input[i] * 2;
                }

                break;
            case (2, 3):
                for (int i = 0; i < 16; i++)
                {
                    output[i] = Av1InverseTransform.Wide.Scale(input[i], 2 * NewSqrt2, NewSqrt2Bits);
                }

                break;
            case (3, 3):
                for (int i = 0; i < 32; i++)
                {
                    output[i] = input[i] * 4;
                }

                break;
            default: throw new ArgumentException($"No one-dimensional transform of type {type} has {4 << sizeIdx} points.");
        }
    }

    /// <summary>av1_round_shift_array on each lane: right with rounding by <paramref name="bit"/>, or left by -<paramref name="bit"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Shift(Vector256<int> v, int bit) =>
        bit == 0 ? v : bit > 0 ? Vector256.ShiftRightArithmetic(v + Vector256.Create(1 << (bit - 1)), bit) : Vector256.ShiftLeft(v, -bit);

    /// <summary>The butterfly on each lane, exactly as the scalar one rounds: the products in 32 bits, their sum without overflow.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> HalfBtf(int w0, Vector256<int> in0, int w1, Vector256<int> in1, int bit) =>
        Av1InverseTransform.Wide.HalfBtf(w0, in0, w1, in1, bit);

    /// <summary>av1_fwht4x4: the lossless Walsh-Hadamard transform, its output scaled by 4 (UNIT_QUANT_FACTOR).</summary>
    private static void Wht4x4(ReadOnlySpan<int> input, int stride, Span<int> output)
    {
        Span<int> temp = stackalloc int[16];
        for (int i = 0; i < 4; i++)
        {
            long a1 = input[i], b1 = input[stride + i], c1 = input[(2 * stride) + i], d1 = input[(3 * stride) + i];
            a1 += b1;
            d1 -= c1;
            long e1 = (a1 - d1) >> 1;
            b1 = e1 - b1;
            c1 = e1 - c1;
            a1 -= c1;
            d1 += b1;
            temp[(i * 4) + 0] = (int)a1;
            temp[(i * 4) + 1] = (int)c1;
            temp[(i * 4) + 2] = (int)d1;
            temp[(i * 4) + 3] = (int)b1;
        }

        for (int i = 0; i < 4; i++)
        {
            long a1 = temp[i], b1 = temp[4 + i], c1 = temp[8 + i], d1 = temp[12 + i];
            a1 += b1;
            d1 -= c1;
            long e1 = (a1 - d1) >> 1;
            b1 = e1 - b1;
            c1 = e1 - c1;
            a1 -= c1;
            d1 += b1;
            output[i] = (int)(a1 * 4);
            output[4 + i] = (int)(c1 * 4);
            output[8 + i] = (int)(d1 * 4);
            output[12 + i] = (int)(b1 * 4);
        }
    }

    /// <summary>Runs the one-dimensional transform of a size (0 for 4 points to 4 for 64) and type (0 DCT, 1 and 2 ADST, 3 identity).</summary>
    private static void Transform(int sizeIdx, int type, ReadOnlySpan<int> input, Span<int> output, int cosBit)
    {
        switch ((sizeIdx, type))
        {
            case (0, 0): Dct4(input, output, cosBit); break;
            case (1, 0): Dct8(input, output, cosBit); break;
            case (2, 0): Dct16(input, output, cosBit); break;
            case (3, 0): Dct32(input, output, cosBit); break;
            case (4, 0): Dct64(input, output, cosBit); break;
            case (0, 1 or 2): Adst4(input, output, cosBit); break;
            case (1, 1 or 2): Adst8(input, output, cosBit); break;
            case (2, 1 or 2): Adst16(input, output, cosBit); break;
            case (0, 3): Identity4(input, output, cosBit); break;
            case (1, 3): Identity8(input, output, cosBit); break;
            case (2, 3): Identity16(input, output, cosBit); break;
            case (3, 3): Identity32(input, output, cosBit); break;
            default: throw new ArgumentException($"No one-dimensional transform of type {type} has {4 << sizeIdx} points.");
        }
    }

    /// <summary>av1_round_shift_array: shifts right with rounding by <paramref name="bit"/>, or left by -<paramref name="bit"/>.</summary>
    private static void RoundShiftArray(Span<int> values, int bit)
    {
        if (bit == 0)
        {
            return;
        }

        if (bit > 0)
        {
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = RoundShift(values[i], bit);
            }
        }
        else
        {
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = (int)Math.Clamp((long)values[i] << -bit, int.MinValue, int.MaxValue);
            }
        }
    }

    private static ReadOnlySpan<int> Cospi(int bit) => Av1Tables.CospiData.AsSpan((bit - 10) * 64, 64);

    private static ReadOnlySpan<int> Sinpi(int bit) => Av1Tables.SinpiData.AsSpan((bit - 10) * 5, 5);

    private static int HalfBtf(int w0, int in0, int w1, int in1, int bit)
    {
        long result = (long)unchecked(w0 * in0) + unchecked(w1 * in1);
        return (int)((result + (1L << (bit - 1))) >> bit);
    }

    private static int RoundShift(long value, int bit) => (int)((value + (1L << (bit - 1))) >> bit);
}
