using Egami.Video.Codecs.Encoding;

namespace Egami.Video.Tests.Codecs;

/// <summary>The macroblock tree: how much later frames reuse each frame's content.</summary>
public class MacroblockTreeTests
{
    [Fact]
    public void StillContentIsWorthMoreTheMoreFramesReuseIt()
    {
        // Four frames of the same picture: each predicts all but a tenth of its intra cost from the frame before.
        BlockCosts[] frames = [.. Enumerable.Range(0, 4).Select(_ => Frame(2, 2, intra: 1000, inter: 100))];
        double[] reuse = Run(frames, [false, false, false, false], 2, 2);
        Assert.Equal(0, reuse[3]);
        Assert.Equal(Math.Log2(1.9), reuse[2], 6);
        Assert.Equal(Math.Log2(1 + (1.9 * 0.9)), reuse[1], 6);
        Assert.True(reuse[0] > reuse[1]);
    }

    [Fact]
    public void NothingPropagatesAcrossAKeyframeOrFromIntraBlocks()
    {
        BlockCosts[] frames = [Frame(2, 2, 1000, 100), Frame(2, 2, 1000, 100), Frame(2, 2, 1000, 1000)];
        double[] reuse = Run(frames, [false, true, false], 2, 2);
        Assert.Equal(0, reuse[0]);
        Assert.Equal(0, reuse[1]);
    }

    [Fact]
    public void AVectorSpreadsTheAmountOverTheBlocksItOverlaps()
    {
        BlockCosts first = Frame(2, 2, 1000, 1000), second = Frame(2, 2, 1000, 1000);
        second.Inter[0] = 0;
        second.Vectors[0] = 4;
        second.Vectors[1] = 4;
        double[] reuse = Run([first, second], [false, false], 2, 2);

        // A quarter of the block's 1000 lands on each of the four.
        Assert.Equal(Math.Log2(1.25), reuse[0], 6);
    }

    private static double[] Run(BlockCosts[] frames, bool[] keys, int columns, int rows)
    {
        double[] reuse = new double[frames.Length];
        MacroblockTree.Reuse(frames, keys, columns, rows, reuse);
        return reuse;
    }

    private static BlockCosts Frame(int columns, int rows, int intra, int inter)
    {
        BlockCosts f = new(columns * rows);
        Array.Fill(f.Intra, intra);
        Array.Fill(f.Inter, inter);
        return f;
    }
}
