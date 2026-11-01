using Egami.Heic.Hevc;

namespace Egami.Heic.Tests;

public class HevcSatdTests
{
    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    public void MatchesTheTransformDoneByHand(int size)
    {
        Random random = new(size);
        int stride = 70;
        ushort[] a = new ushort[stride * 40], b = new ushort[stride * 40];
        for (int i = 0; i < a.Length; i++)
        {
            a[i] = (ushort)random.Next(4096);
            b[i] = (ushort)random.Next(4096);
        }

        int at = (3 * stride) + 5;
        int t = size == 4 ? 4 : 8;
        long expected = 0;
        for (int by = 0; by < size; by += t)
        {
            for (int bx = 0; bx < size; bx += t)
            {
                long sum = 0;
                for (int u = 0; u < t; u++)
                {
                    for (int v = 0; v < t; v++)
                    {
                        long coefficient = 0;
                        for (int y = 0; y < t; y++)
                        {
                            for (int x = 0; x < t; x++)
                            {
                                int sign = (System.Numerics.BitOperations.PopCount((uint)(u & y)) + System.Numerics.BitOperations.PopCount((uint)(v & x))) % 2 == 0 ? 1 : -1;
                                int i = at + ((by + y) * stride) + bx + x;
                                coefficient += sign * (a[i] - b[i]);
                            }
                        }

                        sum += Math.Abs(coefficient);
                    }
                }

                expected += t == 4 ? (sum + 1) >> 1 : (sum + 2) >> 2;
            }
        }

        Assert.Equal(expected, HevcSatd.Compute(a, b, at, stride, size));
    }
}
