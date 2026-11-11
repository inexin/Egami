using Egami.Nal;
using Egami.Video.Codecs.H264;

namespace Egami.Video.H264.Tests;

/// <summary>
/// The RDOQ block pricer: its incremental prices match pricing the whole block from the slice's states (each bin in the state
/// of its context, a level's unary prefix adapting within the level), and in 4x4 blocks of levels of 1 that is the exact count.
/// </summary>
public sealed class EncoderBlockPricerTests
{
    [Theory]
    [InlineData(1, 15)]
    [InlineData(2, 16)]
    [InlineData(4, 15)]
    [InlineData(5, 64)]
    public void PricesLoweredLevelsAsTheWholeBlock(int cat, int max)
    {
        Random random = new(cat);
        H264BlockPricer pricer = new();
        byte[] ctx = new byte[460];
        int[] levels = new int[64];
        for (int round = 0; round < 400; round++)
        {
            random.NextBytes(ctx);
            for (int i = 0; i < ctx.Length; i++)
            {
                ctx[i] &= 127;
            }

            Fill(random, levels, max);
            long begun = Begin(pricer, ctx, cat, max, levels);
            Assert.Equal(Static(ctx, cat, max, levels), begun);
            if (begun == 0)
            {
                continue;
            }

            // Lower levels from the last back as RDOQ does, taking some of the changes.
            for (int k = max - 1; k >= 0; k--)
            {
                int level = levels[k];
                if (level == 0)
                {
                    continue;
                }

                int lower = random.Next(3) == 0 ? 0 : level - Math.Sign(level);
                levels[k] = lower;
                long expected = Static(ctx, cat, max, levels);
                long priced = expected == 0 ? 0 : pricer.Price(k, level);
                Assert.Equal(expected, priced);
                if (random.Next(2) == 0)
                {
                    pricer.Accept(true);
                }
                else
                {
                    levels[k] = level;
                }

                pricer.Advance(k);
            }
        }
    }

    [Fact]
    public void PricesFourByFourBlocksOfOnesExactly()
    {
        Random random = new(7);
        H264BlockPricer pricer = new();
        byte[] ctx = new byte[460];
        int[] levels = new int[16];
        for (int round = 0; round < 400; round++)
        {
            random.NextBytes(ctx);
            for (int i = 0; i < ctx.Length; i++)
            {
                ctx[i] &= 127;
            }

            // At most one level of each kind uses each level context: no adaptation to leave out.
            Array.Clear(levels);
            levels[random.Next(16)] = random.Next(2) == 0 ? 1 : -1;
            Assert.Equal(Count(ctx, 2, 16, levels), Begin(pricer, ctx, 2, 16, levels));
        }
    }

    private static long Begin(H264BlockPricer pricer, byte[] ctx, int cat, int max, int[] levels)
    {
        int[] rows = [.. Enumerable.Range(0, max).Where(i => levels[i] != 0)];
        return pricer.Begin(ctx, cat, max, levels, 0, [.. rows, .. new int[64 - rows.Length]], rows.Length);
    }

    private static void Fill(Random random, int[] levels, int max)
    {
        Array.Clear(levels);
        int used = random.Next(1, max + 1);
        for (int i = 0; i < used; i++)
        {
            int r = random.Next(10);
            int abs = r < 4 ? 0 : r < 7 ? 1 : r < 9 ? random.Next(2, 6) : random.Next(6, 300);
            levels[i] = random.Next(2) == 0 ? abs : -abs;
        }
    }

    // The model the pricer follows, written plainly: every bin in the slice's state, a unary prefix adapting within its level.
    private static long Static(byte[] ctx, int cat, int max, int[] levels)
    {
        int last = max - 1;
        while (last >= 0 && levels[last] == 0)
        {
            last--;
        }

        if (last < 0)
        {
            return 0;
        }

        (int sigBase, int lastBase, int absBase) = H264MbSyntax.ResidualContexts(cat);
        long cost = CabacCost.Bit;
        for (int i = 0; i < Math.Min(last + 1, max - 1); i++)
        {
            int sigInc = cat == 5 ? H264Tables.Significant8x8[i] : i, lastInc = cat == 5 ? H264Tables.Last8x8[i] : i;
            cost += CabacCost.Of(ctx[sigBase + sigInc], levels[i] != 0 ? 1 : 0);
            if (levels[i] != 0)
            {
                cost += CabacCost.Of(ctx[lastBase + lastInc], i == last ? 1 : 0);
            }
        }

        int eq1 = 0, gt1 = 0;
        for (int i = last; i >= 0; i--)
        {
            int abs = Math.Abs(levels[i]);
            if (abs == 0)
            {
                continue;
            }

            cost += CabacCost.Of(ctx[absBase + (gt1 != 0 ? 0 : Math.Min(4, 1 + eq1))], abs > 1 ? 1 : 0) + CabacCost.Bit;
            if (abs == 1)
            {
                eq1++;
                continue;
            }

            byte state = ctx[absBase + 5 + Math.Min(cat == 3 ? 3 : 4, gt1)];
            int prefix = Math.Min(abs - 1, 14);
            for (int p = 1; p < prefix; p++)
            {
                cost += CabacCost.Update(ref state, 1);
            }

            if (prefix < 14)
            {
                cost += CabacCost.Of(state, 0);
            }
            else
            {
                int v = abs - 15, k = 0;
                while (v >= 1 << k)
                {
                    v -= 1 << k;
                    k++;
                }

                cost += ((2 * k) + 1) * CabacCost.Bit;
            }

            gt1++;
        }

        return cost;
    }

    private static long Count(byte[] ctx, int cat, int max, int[] levels)
    {
        int n = levels.Take(max).Count(l => l != 0);
        if (n == 0)
        {
            return 0;
        }

        byte[] copy = (byte[])ctx.Clone();
        CabacCountSink sink = default;
        H264MbSyntax.BlockCabac(copy, ref sink, cat, -1, max, levels, 0, n);
        return sink.Cost + CabacCost.Bit;
    }
}
