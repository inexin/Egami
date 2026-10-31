using Egami.Formats.Av1;

namespace Egami.Tests.Formats.Av1;

/// <summary>
/// Step 2.4.8: CDEF's vector direction search gives the scalar one's direction and variance for every block, 8-bit and deeper:
/// random blocks, flat ones, extremes, edges in each direction and noise at full amplitude.
/// </summary>
public class Av1CdefDirectionTests
{
    private const int Stride = 13;

    public static TheoryData<int> Seeds() => [1, 2, 3];

    [Theory]
    [MemberData(nameof(Seeds))]
    public void BytesMatchTheScalarSearch(int seed)
    {
        foreach (byte[] block in Blocks<byte>(seed, 255, v => (byte)v))
        {
            int dir = Av1Cdef.FindDirection(block, Stride + 1, Stride, 0, out int variance);
            int expected = Av1Cdef.FindDirectionScalar(block, Stride + 1, Stride, 0, out int expectedVariance);
            Assert.Equal((expected, expectedVariance), (dir, variance));
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void DeepSamplesMatchTheScalarSearch(int seed)
    {
        foreach ((int depth, int shift) in new[] { (10, 2), (12, 4) })
        {
            foreach (ushort[] block in Blocks<ushort>(seed, (1 << depth) - 1, v => (ushort)v))
            {
                int dir = Av1Cdef.FindDirection(block, Stride + 1, Stride, shift, out int variance);
                int expected = Av1Cdef.FindDirectionScalar(block, Stride + 1, Stride, shift, out int expectedVariance);
                Assert.Equal((expected, expectedVariance), (dir, variance));
            }
        }
    }

    private static IEnumerable<T[]> Blocks<T>(int seed, int max, Func<int, T> sample)
    {
        Random random = new(seed);
        T[] Make(Func<int, int, int> value)
        {
            T[] block = new T[Stride * 10];
            for (int i = 0; i < block.Length; i++)
            {
                block[i] = sample(random.Next(max + 1));
            }

            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    block[((y + 1) * Stride) + x + 1] = sample(Math.Clamp(value(x, y), 0, max));
                }
            }

            return block;
        }

        yield return Make((_, _) => 0);
        yield return Make((_, _) => max);
        yield return Make((x, y) => ((x + y) & 1) == 0 ? 0 : max);
        for (int i = 0; i < 300; i++)
        {
            yield return Make((_, _) => random.Next(max + 1));
        }

        for (int i = 0; i < 300; i++)
        {
            // An edge at a random angle and place, with a little noise, so every direction wins somewhere.
            double angle = random.NextDouble() * Math.PI, offset = (random.NextDouble() * 8) - 4;
            int low = random.Next(max / 2), high = (max / 2) + random.Next(max / 2);
            yield return Make((x, y) => ((((x - 3.5) * Math.Cos(angle)) + ((y - 3.5) * Math.Sin(angle))) > offset ? high : low) + random.Next(-(max / 32), (max / 32) + 1));
        }
    }
}
