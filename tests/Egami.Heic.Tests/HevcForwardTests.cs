using Egami.Heic.Hevc;

namespace Egami.Heic.Tests;

public class HevcForwardTests
{
    private static readonly int[] Dst = [29, 55, 74, 84, 74, 74, 0, -74, 84, -29, -74, 55, 55, -84, 74, -29];

    /// <summary>The forward DCT (and 4x4 DST) equals the two matrix products done by hand, with the stage shifts, for any residual.</summary>
    [Theory]
    [InlineData(2, 8, false)]
    [InlineData(2, 8, true)]
    [InlineData(2, 10, true)]
    [InlineData(3, 8, false)]
    [InlineData(4, 8, false)]
    [InlineData(5, 8, false)]
    [InlineData(4, 10, false)]
    [InlineData(5, 10, false)]
    [InlineData(5, 12, false)]
    public void MatchesTheMatrixProductsDoneByHand(int log2, int bitDepth, bool dst)
    {
        int n = 1 << log2, step = 32 >> log2;
        int M(int k, int x) => dst ? Dst[(k * 4) + x] : HevcTransform.Matrix[(k * step * 32) + x];
        int shift1 = log2 + bitDepth - 9, shift2 = log2 + 6;
        Random random = new((log2 * 100) + bitDepth);
        for (int trial = 0; trial < 20; trial++)
        {
            int max = trial == 0 ? (1 << bitDepth) - 1 : random.Next(1, 1 << bitDepth);
            int[] residual = new int[n * n];
            for (int i = 0; i < residual.Length; i++)
            {
                residual[i] = trial == 0 ? (i % 3 == 0 ? -max : max) : random.Next(-max, max + 1);
            }

            long[] temp = new long[n * n];
            for (int y = 0; y < n; y++)
            {
                for (int k = 0; k < n; k++)
                {
                    long sum = 1L << (shift1 - 1);
                    for (int x = 0; x < n; x++)
                    {
                        sum += M(k, x) * (long)residual[(y * n) + x];
                    }

                    temp[(y * n) + k] = sum >> shift1;
                }
            }

            int[] expected = new int[n * n];
            for (int k = 0; k < n; k++)
            {
                for (int x = 0; x < n; x++)
                {
                    long sum = 1L << (shift2 - 1);
                    for (int y = 0; y < n; y++)
                    {
                        sum += M(k, y) * temp[(y * n) + x];
                    }

                    expected[(k * n) + x] = (int)(sum >> shift2);
                }
            }

            int[] coefficients = new int[n * n];
            HevcForward.Transform(residual, coefficients, log2, bitDepth, dst);
            Assert.Equal(expected, coefficients);
        }
    }
}
