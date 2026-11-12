using Egami.Video.Codecs.H264;

namespace Egami.Video.H264.Tests;

/// <summary>
/// The rate count's one-pass residual counter (H.264 Encoder Speed Build Plan, task 19) against the bin-by-bin count: the same
/// cost and the same context states for every block category, and the undo log puts the states back.
/// </summary>
public sealed class EncoderCabacCounterTests
{
    [Theory]
    [InlineData(0, 16)]
    [InlineData(1, 15)]
    [InlineData(2, 16)]
    [InlineData(3, 4)]
    [InlineData(4, 15)]
    [InlineData(5, 64)]
    public void CountsBlocksAsTheBinByBinCount(int cat, int max)
    {
        Random random = new(cat * 977);
        byte[] start = new byte[464];
        int[] levels = new int[max + 40];
        CabacUndoLog log = new(460);
        for (int trial = 0; trial < 3000; trial++)
        {
            for (int i = 0; i < start.Length; i++)
            {
                start[i] = (byte)random.Next(126);
            }

            // Levels at the end of the array (the scalar mask) or with non-zero neighbours past maxNumCoeff (the masked vectors).
            int at = trial % 3 == 0 ? levels.Length - max : random.Next(levels.Length - max - 4);
            Array.Fill(levels, 7);
            double density = random.NextDouble();
            int big = trial % 5 == 0 ? 400 : trial % 2 == 0 ? 20 : 3;
            int count = 0;
            for (int i = 0; i < max; i++)
            {
                int level = random.NextDouble() < density ? random.Next(1, big + 1) * (random.Next(2) == 0 ? 1 : -1) : 0;
                levels[at + i] = level;
                count += level != 0 ? 1 : 0;
            }

            if (count == 0)
            {
                levels[at + random.Next(max)] = 1;
                count = 1;
            }

            // A bin before the block, so the log also holds a chunk noted bin by bin.
            int other = random.Next(460), bin = random.Next(2);
            byte[] expected = (byte[])start.Clone();
            CabacCountSink reference = default;
            reference.Encode(ref expected[other], bin);
            H264MbSyntax.BlockCabac(expected, ref reference, cat, -1, max, levels, at, count);

            byte[] actual = (byte[])start.Clone();
            log.Begin(actual);
            CabacUndoSink counter = new(log);
            counter.Encode(ref actual[other], bin);
            H264MbSyntax.BlockCabac(actual, ref counter, cat, -1, max, levels, at, count);

            Assert.Equal(reference.Cost, counter.Cost);
            Assert.Equal(expected, actual);
            log.Restore();
            Assert.Equal(start, actual);
        }
    }

    [Fact]
    public void CountedStatesMatchTheBinByBinCount()
    {
        Random random = new(5);
        int[] levels = new int[64];
        CabacUndoLog log = new(460);
        foreach ((int cat, int max) in new[] { (0, 16), (1, 15), (2, 16), (3, 4), (4, 15), (5, 64) })
        {
            for (int trial = 0; trial < 500; trial++)
            {
                byte[] start = new byte[464];
                for (int i = 0; i < 460; i++)
                {
                    start[i] = (byte)random.Next(126);
                }

                Array.Clear(levels);
                int count = 0;
                for (int i = 0; i < max; i++)
                {
                    levels[i] = random.Next(3) == 0 ? random.Next(-30, 31) : 0;
                    count += levels[i] != 0 ? 1 : 0;
                }

                if (count == 0)
                {
                    levels[max - 1] = -2;
                    count = 1;
                }

                byte[] expected = (byte[])start.Clone();
                CabacCountSink reference = default;
                H264MbSyntax.BlockCabac(expected, ref reference, cat, -1, max, levels, 0, count);
                byte[] actual = (byte[])start.Clone();
                log.Begin(actual);
                CabacUndoSink counter = new(log);
                H264MbSyntax.BlockCabac(actual, ref counter, cat, -1, max, levels, 0, count);
                Assert.Equal(expected, actual);
                Assert.Equal(reference.Cost, counter.Cost);
            }
        }
    }
}
