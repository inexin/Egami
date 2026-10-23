using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Egami.Video.Codecs.Vp9;

/// <summary>
/// The 8-bit form of <see cref="Vp9Transform.Forward"/>, with the same values: the column transforms run a column per 32-bit
/// lane, eight columns at a time (four for 4x4), then each eight rows are transposed so a lane holds a row, transformed, and
/// transposed back.
/// </summary>
internal static partial class Vp9TransformSimd
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Hrs(Vector256<int> value) =>
        Vector256.ShiftRightArithmetic(value + Vector256<int>.One + Vector256.ShiftRightLogical(value, 31), 2);

    /// <summary>Forward transforms an 8-bit residual block (rows <paramref name="stride"/> apart) into <paramref name="output"/> in raster order.</summary>
    [SkipLocalsInit]
    public static void Forward(ReadOnlySpan<short> residual, int stride, Span<int> output, int txSize, int txType)
    {
        int n = 4 << txSize;
        bool colAdst = (txType & 1) != 0 && txSize != Vp9Tables.Tx32x32;
        bool rowAdst = (txType & 2) != 0 && txSize != Vp9Tables.Tx32x32;
        if (n == 4)
        {
            Forward4(residual, stride, output, colAdst, rowAdst);
            return;
        }

        Span<int> temp = stackalloc int[32 * 32];
        Span<Vector256<int>> input = stackalloc Vector256<int>[32];
        Span<Vector256<int>> result = stackalloc Vector256<int>[32];
        for (int c = 0; c < n; c += 8)
        {
            for (int j = 0; j < n; j++)
            {
                ReadOnlySpan<short> row = residual.Slice((j * stride) + c, 8);
                input[j] = Vector256.Create(row[0], row[1], row[2], row[3], row[4], row[5], row[6], row[7]) * 4;
            }

            ForwardTransform(input[..n], result[..n], txSize, colAdst, false);
            for (int j = 0; j < n; j++)
            {
                Vector256<int> v = result[j];
                if (txSize == Vp9Tables.Tx16x16)
                {
                    v = Vector256.ShiftRightArithmetic(v + Vector256<int>.One + Vector256.ShiftRightLogical(v, 31), 2);
                }
                else if (txSize == Vp9Tables.Tx32x32)
                {
                    v = Vector256.ShiftRightArithmetic(v + Vector256<int>.One + Vector256.ShiftRightLogical(-v, 31), 2);
                }

                v.CopyTo(temp.Slice((j * n) + c, 8));
            }
        }

        for (int r = 0; r < n; r += 8)
        {
            for (int c = 0; c < n; c += 8)
            {
                Transpose8x8(temp[((r * n) + c)..], n, input.Slice(c, 8));
            }

            ForwardTransform(input[..n], result[..n], txSize, rowAdst, true);
            if (txSize == Vp9Tables.Tx8x8)
            {
                for (int k = 0; k < n; k++)
                {
                    Vector256<int> v = result[k];
                    result[k] = Vector256.ShiftRightArithmetic(v + Vector256.ShiftRightLogical(v, 31), 1);
                }
            }

            for (int c = 0; c < n; c += 8)
            {
                TransposeBack8x8(result.Slice(c, 8), output[((r * n) + c)..], n);
            }
        }
    }

    private static void ForwardTransform(ReadOnlySpan<Vector256<int>> input, Span<Vector256<int>> output, int txSize, bool adst, bool round)
    {
        switch (txSize)
        {
            case Vp9Tables.Tx8x8:
                if (adst)
                {
                    Fadst8(input, output);
                }
                else
                {
                    Fdct8(input, output);
                }

                break;
            case Vp9Tables.Tx16x16:
                if (adst)
                {
                    Fadst16(input, output);
                }
                else
                {
                    Fdct16(input, output);
                }

                break;
            default:
                Fdct32(input, output, round);
                break;
        }
    }

    [SkipLocalsInit]
    private static void Forward4(ReadOnlySpan<short> residual, int stride, Span<int> output, bool colAdst, bool rowAdst)
    {
        Span<Vector128<int>> input = stackalloc Vector128<int>[4];
        Span<Vector128<int>> result = stackalloc Vector128<int>[4];
        for (int j = 0; j < 4; j++)
        {
            ReadOnlySpan<short> row = residual.Slice(j * stride, 4);
            input[j] = Vector128.Create(row[0], row[1], row[2], row[3]) * 16;
        }

        if (residual[0] != 0)
        {
            input[0] += Vector128.Create(1, 0, 0, 0);
        }

        if (colAdst)
        {
            Fadst4(input, result);
        }
        else
        {
            Fdct4(input, result);
        }

        Transpose4x4(result[0], result[1], result[2], result[3], input);
        if (rowAdst)
        {
            Fadst4(input, result);
        }
        else
        {
            Fdct4(input, result);
        }

        Transpose4x4(result[0], result[1], result[2], result[3], input);
        for (int j = 0; j < 4; j++)
        {
            Vector128.ShiftRightArithmetic(input[j] + Vector128<int>.One, 2).CopyTo(output.Slice(j * 4, 4));
        }
    }
}
