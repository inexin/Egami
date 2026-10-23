using System.Runtime.CompilerServices;

namespace Egami.Video.Codecs.Vp9;

/// <summary>
/// VP9's forward transforms for the encoder, the two-dimensional passes of libvpx 1.14.1's vp9_fht4x4_c, vp9_fht8x8_c,
/// vp9_fht16x16_c, vpx_fdct32x32_c and vpx_fdct32x32_rd_c and vp9_fwht4x4_c (BSD-3; see THIRD-PARTY-NOTICES) around the
/// one-dimensional transforms in Vp9Transform.Forward.OneD.cs. Any forward transform makes a valid stream; these scale their
/// output as the inverse transforms expect, so the quantizer's steps mean what they say.
/// </summary>
internal static partial class Vp9Transform
{
    private delegate void Forward1D(ReadOnlySpan<int> input, Span<int> output);

    private static readonly Forward1D[] ForwardDct = [Fdct4, Fdct8, Fdct16];

    private static readonly Forward1D[] ForwardAdst = [Fadst4, Fadst8, Fadst16];

    /// <summary>half_round_shift: a rounding shift by 2 that rounds halves away from zero.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long Hrs(long value) => (value + 1 + (value < 0 ? 1 : 0)) >> 2;

    /// <summary>
    /// Forward transforms the residual block at <paramref name="residual"/> (rows <paramref name="stride"/> apart) into
    /// <paramref name="output"/> in raster order. <paramref name="txType"/> is DCT_DCT, ADST_DCT (vertical ADST), DCT_ADST or
    /// ADST_ADST; 32x32 is always DCT, and <paramref name="lowPrecision"/> takes libvpx's rd variant, which rounds the row pass's
    /// intermediates.
    /// </summary>
    [SkipLocalsInit]
    public static void Forward(ReadOnlySpan<short> residual, int stride, Span<int> output, int txSize, int txType, bool lowPrecision = false)
    {
        if (txSize == 3)
        {
            Forward32(residual, stride, output, lowPrecision);
            return;
        }

        int n = 4 << txSize;
        Forward1D cols = (txType & 1) != 0 ? ForwardAdst[txSize] : ForwardDct[txSize];
        Forward1D rows = (txType & 2) != 0 ? ForwardAdst[txSize] : ForwardDct[txSize];
        Span<int> temp = stackalloc int[16 * 16];
        Span<int> tempIn = stackalloc int[16];
        Span<int> tempOut = stackalloc int[16];
        tempIn = tempIn[..n];
        tempOut = tempOut[..n];
        int inputScale = txSize == 0 ? 16 : 4;
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                tempIn[j] = residual[(j * stride) + i] * inputScale;
            }

            if (txSize == 0 && i == 0 && tempIn[0] != 0)
            {
                tempIn[0]++;
            }

            cols(tempIn, tempOut);
            if (txSize == 2)
            {
                for (int j = 0; j < n; j++)
                {
                    int v = tempOut[j];
                    temp[(j * n) + i] = (v + 1 + (v < 0 ? 1 : 0)) >> 2;
                }
            }
            else
            {
                for (int j = 0; j < n; j++)
                {
                    temp[(j * n) + i] = tempOut[j];
                }
            }
        }

        for (int i = 0; i < n; i++)
        {
            rows(temp.Slice(i * n, n), tempOut);
            Span<int> row = output.Slice(i * n, n);
            switch (txSize)
            {
                case 0:
                    for (int j = 0; j < n; j++)
                    {
                        row[j] = (tempOut[j] + 1) >> 2;
                    }

                    break;
                case 1:
                    for (int j = 0; j < n; j++)
                    {
                        int v = tempOut[j];
                        row[j] = (v + (v < 0 ? 1 : 0)) >> 1;
                    }

                    break;
                default:
                    tempOut.CopyTo(row);
                    break;
            }
        }
    }

    [SkipLocalsInit]
    private static void Forward32(ReadOnlySpan<short> residual, int stride, Span<int> output, bool lowPrecision)
    {
        Span<long> temp = stackalloc long[32 * 32];
        Span<long> tempIn = stackalloc long[32];
        Span<long> tempOut = stackalloc long[32];
        for (int i = 0; i < 32; i++)
        {
            for (int j = 0; j < 32; j++)
            {
                tempIn[j] = residual[(j * stride) + i] * 4;
            }

            Fdct32(tempIn, tempOut, false);
            for (int j = 0; j < 32; j++)
            {
                long v = tempOut[j];
                temp[(j * 32) + i] = (v + 1 + (v > 0 ? 1 : 0)) >> 2;
            }
        }

        for (int i = 0; i < 32; i++)
        {
            Fdct32(temp.Slice(i * 32, 32), tempOut, lowPrecision);
            Span<int> row = output.Slice(i * 32, 32);
            if (lowPrecision)
            {
                for (int j = 0; j < 32; j++)
                {
                    row[j] = (int)tempOut[j];
                }
            }
            else
            {
                for (int j = 0; j < 32; j++)
                {
                    long v = tempOut[j];
                    row[j] = (int)((v + 1 + (v < 0 ? 1 : 0)) >> 2);
                }
            }
        }
    }

    /// <summary>The lossless forward Walsh-Hadamard transform (vp9_fwht4x4_c).</summary>
    public static void ForwardWht(ReadOnlySpan<short> residual, int stride, Span<int> output)
    {
        for (int i = 0; i < 4; i++)
        {
            long a1 = residual[i], b1 = residual[stride + i], c1 = residual[(2 * stride) + i], d1 = residual[(3 * stride) + i];
            a1 += b1;
            d1 -= c1;
            long e1 = (a1 - d1) >> 1;
            b1 = e1 - b1;
            c1 = e1 - c1;
            a1 -= c1;
            d1 += b1;
            output[i] = (int)a1;
            output[4 + i] = (int)c1;
            output[8 + i] = (int)d1;
            output[12 + i] = (int)b1;
        }

        for (int i = 0; i < 4; i++)
        {
            Span<int> row = output.Slice(i * 4, 4);
            long a1 = row[0], b1 = row[1], c1 = row[2], d1 = row[3];
            a1 += b1;
            d1 -= c1;
            long e1 = (a1 - d1) >> 1;
            b1 = e1 - b1;
            c1 = e1 - c1;
            a1 -= c1;
            d1 += b1;
            row[0] = (int)(a1 * 4);
            row[1] = (int)(c1 * 4);
            row[2] = (int)(d1 * 4);
            row[3] = (int)(b1 * 4);
        }
    }
}
