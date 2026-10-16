namespace Egami.Formats.Av1;

/// <summary>
/// AV1's inverse transforms, after libaom's av1_inv_txfm2d.c and idct.c (BSD 2-clause, see THIRD-PARTY-NOTICES): rows, then
/// columns, then the residual added to the prediction and clipped; the Walsh-Hadamard transform for lossless blocks.
/// </summary>
// Every stack buffer here is written before it is read (as in libaom, whose arrays are not initialized either), so skipping
// the zeroing is safe and saves it on each transform block.
[System.Runtime.CompilerServices.SkipLocalsInit]
internal static partial class Av1InverseTransform
{
    private const int CosBit = 12;
    private const int NewSqrt2 = 5793;
    private const int NewInvSqrt2 = 2896;
    private const int NewSqrt2Bits = 12;

    /// <summary>av1_inv_txfm_shift_ls: the rounding after the rows of each transform size (the columns always shift by 4).</summary>
    private static ReadOnlySpan<byte> RowShift => [0, 1, 2, 2, 2, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2];

    private static ReadOnlySpan<int> Cospi => Av1Tables.CospiData.AsSpan(128, 64);

    private static ReadOnlySpan<int> Sinpi => Av1Tables.SinpiData.AsSpan(10, 5);

    /// <summary>
    /// Adds the inverse transform of a block's coefficients to the prediction at <paramref name="offset"/>.
    /// </summary>
    /// <param name="coeffs">The coefficients, column by column at the coded size (64-point sizes code 32).</param>
    /// <param name="txSize">The transform size.</param>
    /// <param name="txType">The transform type.</param>
    /// <param name="lossless">Whether the block is lossless (the Walsh-Hadamard transform).</param>
    /// <param name="eob">The end of block.</param>
    /// <param name="dst">The plane.</param>
    /// <param name="offset">Where the block's top left sample is.</param>
    /// <param name="stride">The plane's stride.</param>
    /// <param name="bitDepth">The bit depth.</param>
    internal static void Add(int[] coeffs, int txSize, int txType, bool lossless, int eob, ushort[] dst, int offset, int stride, int bitDepth)
    {
        if (lossless)
        {
            if (eob > 1)
            {
                Wht4x4(coeffs, dst, offset, stride, bitDepth);
            }
            else
            {
                Wht4x4Dc(coeffs, dst, offset, stride, bitDepth);
            }

            return;
        }

        int w = Av1Tables.TxSizeWide[txSize], h = Av1Tables.TxSizeHigh[txSize];
        int adjusted = Av1Tables.AdjustedTxSize(txSize);
        int codedW = Av1Tables.TxSizeWide[adjusted], codedH = Av1Tables.TxSizeHigh[adjusted];
        int rangeRow = bitDepth == 8 ? 16 : bitDepth == 10 ? 18 : 20;
        int rangeCol = bitDepth == 12 ? 18 : 16;
        int colType = Av1Tables.VtxTab[txType], rowType = Av1Tables.HtxTab[txType];
        bool udFlip = colType == 2, lrFlip = rowType == 2;
        int rowShift = RowShift[txSize];
        bool rect2 = w == 2 * h || h == 2 * w;
        int rowClamp = bitDepth + 8, colClamp = Math.Max(bitDepth + 6, 16);
        int max = (1 << bitDepth) - 1;

        Span<int> buf = stackalloc int[w * h];
        Span<int> tempIn = stackalloc int[64];
        Span<int> tempOut = stackalloc int[64];
        for (int r = 0; r < h; r++)
        {
            Span<int> row = buf.Slice(r * w, w);
            if (r >= codedH)
            {
                row.Clear();
                continue;
            }

            bool any = false;
            for (int c = 0; c < w; c++)
            {
                int v = c < codedW ? coeffs[(c * codedH) + r] : 0;
                if (rect2)
                {
                    v = RoundShift((long)v * NewInvSqrt2, NewSqrt2Bits);
                }

                tempIn[c] = Clamp(v, rowClamp);
                any |= v != 0;
            }

            if (!any)
            {
                row.Clear();
                continue;
            }

            Transform(rowType, w, tempIn[..w], row, rangeRow);
            if (rowShift > 0)
            {
                for (int c = 0; c < w; c++)
                {
                    row[c] = RoundShift(row[c], rowShift);
                }
            }
        }

        for (int c = 0; c < w; c++)
        {
            int source = lrFlip ? w - 1 - c : c;
            for (int r = 0; r < h; r++)
            {
                tempIn[r] = Clamp(buf[(r * w) + source], colClamp);
            }

            Transform(colType, h, tempIn[..h], tempOut[..h], rangeCol);
            for (int r = 0; r < h; r++)
            {
                int residual = RoundShift(tempOut[udFlip ? h - 1 - r : r], 4);
                int at = offset + (r * stride) + c;
                dst[at] = (ushort)Math.Clamp(dst[at] + residual, 0, max);
            }
        }
    }

    private static void Transform(int type1d, int n, ReadOnlySpan<int> input, Span<int> output, int range)
    {
        switch (type1d)
        {
            case 0:
                switch (n)
                {
                    case 4: Dct4(input, output, range); break;
                    case 8: Dct8(input, output, range); break;
                    case 16: Dct16(input, output, range); break;
                    case 32: Dct32(input, output, range); break;
                    default: Dct64(input, output, range); break;
                }

                break;
            case 3:
                switch (n)
                {
                    case 4: Identity4(input, output, range); break;
                    case 8: Identity8(input, output, range); break;
                    case 16: Identity16(input, output, range); break;
                    default: Identity32(input, output, range); break;
                }

                break;
            default:
                switch (n)
                {
                    case 4: Adst4(input, output, range); break;
                    case 8: Adst8(input, output, range); break;
                    default: Adst16(input, output, range); break;
                }

                break;
        }
    }

    private static int HalfBtf(int w0, int in0, int w1, int in1, int bit)
    {
        long result = (long)unchecked(w0 * in0) + unchecked(w1 * in1);
        return (int)((result + (1L << (bit - 1))) >> bit);
    }

    private static int RoundShift(long value, int bit) => (int)((value + (1L << (bit - 1))) >> bit);

    private static int Clamp(int value, int bit) => Math.Clamp(value, -(1 << (bit - 1)), (1 << (bit - 1)) - 1);

    /// <summary>av1_highbd_iwht4x4_16_add.</summary>
    private static void Wht4x4(int[] input, ushort[] dst, int offset, int stride, int bitDepth)
    {
        Span<int> output = stackalloc int[16];
        int max = (1 << bitDepth) - 1;
        for (int i = 0; i < 4; i++)
        {
            int a1 = input[i] >> 2, c1 = input[4 + i] >> 2, d1 = input[8 + i] >> 2, b1 = input[12 + i] >> 2;
            a1 += c1;
            d1 -= b1;
            int e1 = (a1 - d1) >> 1;
            b1 = e1 - b1;
            c1 = e1 - c1;
            a1 -= b1;
            d1 += c1;
            output[i] = a1;
            output[4 + i] = b1;
            output[8 + i] = c1;
            output[12 + i] = d1;
        }

        for (int i = 0; i < 4; i++)
        {
            int a1 = output[(4 * i) + 0], c1 = output[(4 * i) + 1], d1 = output[(4 * i) + 2], b1 = output[(4 * i) + 3];
            a1 += c1;
            d1 -= b1;
            int e1 = (a1 - d1) >> 1;
            b1 = e1 - b1;
            c1 = e1 - c1;
            a1 -= b1;
            d1 += c1;
            int at = offset + i;
            dst[at] = (ushort)Math.Clamp(dst[at] + a1, 0, max);
            dst[at + stride] = (ushort)Math.Clamp(dst[at + stride] + b1, 0, max);
            dst[at + (2 * stride)] = (ushort)Math.Clamp(dst[at + (2 * stride)] + c1, 0, max);
            dst[at + (3 * stride)] = (ushort)Math.Clamp(dst[at + (3 * stride)] + d1, 0, max);
        }
    }

    /// <summary>av1_highbd_iwht4x4_1_add: only the DC coefficient.</summary>
    private static void Wht4x4Dc(int[] input, ushort[] dst, int offset, int stride, int bitDepth)
    {
        Span<int> tmp = stackalloc int[4];
        int max = (1 << bitDepth) - 1;
        int a1 = input[0] >> 2;
        int e1 = a1 >> 1;
        a1 -= e1;
        tmp[0] = a1;
        tmp[1] = tmp[2] = tmp[3] = e1;
        for (int i = 0; i < 4; i++)
        {
            e1 = tmp[i] >> 1;
            a1 = tmp[i] - e1;
            int at = offset + i;
            dst[at] = (ushort)Math.Clamp(dst[at] + a1, 0, max);
            dst[at + stride] = (ushort)Math.Clamp(dst[at + stride] + e1, 0, max);
            dst[at + (2 * stride)] = (ushort)Math.Clamp(dst[at + (2 * stride)] + e1, 0, max);
            dst[at + (3 * stride)] = (ushort)Math.Clamp(dst[at + (3 * stride)] + e1, 0, max);
        }
    }
}
