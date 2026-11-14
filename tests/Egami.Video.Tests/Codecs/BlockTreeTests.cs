using Egami.Video.Codecs.Encoding;

namespace Egami.Video.Tests.Codecs;

/// <summary>The block macroblock tree: how much later pictures reuse each block, along the coded structure.</summary>
public class BlockTreeTests
{
    [Fact]
    public void AStillPyramidPassesBiPredictedSharesHalfToEachSide()
    {
        // A keyframe at 0 and a run of three B pictures before P picture 4, every block predicting all but a tenth of its
        // intra cost of 1000; the B pictures from both sides. Coding order: 0, 4, 2, 1, 3.
        TreePicture[] plan =
        [
            new(0, -1, -1, null),
            new(4, 0, -1, Costs(2, 2, mode: 0)),
            new(2, 0, 4, Costs(2, 2, mode: 2)),
            new(1, 0, 2, Costs(2, 2, mode: 2)),
            new(3, 2, 4, Costs(2, 2, mode: 2)),
        ];
        Dictionary<int, double[]> carried = Run(plan, 4);

        // The leaves take nothing; each gives 900 to the middle B picture, half of it from either side.
        Assert.All(carried[1], c => Assert.Equal(0, c));
        Assert.All(carried[3], c => Assert.Equal(0, c));
        Assert.All(carried[2], c => Assert.Equal(900, c, 6));

        // The middle passes (1000 + 900) * 0.9 = 1710, half to each anchor; P picture 4 also has half of leaf 3's 900.
        Assert.All(carried[4], c => Assert.Equal(450 + 855, c, 6));
        Assert.All(carried[0], c => Assert.Equal(450 + 855 + ((1000 + 1305) * 0.9), c, 6));
    }

    [Fact]
    public void APanLandsWhereTheVectorPoints()
    {
        // Two blocks side by side; the P picture's left block finds its content one block to the right in its reference, and
        // the right block's vector leaves the picture.
        TreeCosts p = Costs(2, 1, mode: 0);
        p.Vectors0[0] = 8;
        p.Vectors0[2] = 8;
        Dictionary<int, double[]> carried = Run([new(0, -1, -1, null), new(1, 0, -1, p)], 2);
        Assert.Equal([0, 900], carried[0]);
    }

    [Fact]
    public void NothingCrossesAKeyframeOrLeavesAnIntraBlock()
    {
        // P picture 1 from keyframe 0, then keyframe 2 (a cut) and P picture 3 from it; one of picture 3's blocks is intra.
        TreeCosts last = Costs(2, 1, mode: 0);
        last.Modes[1] = TreeCosts.IntraMode;
        last.Inter[1] = last.Intra[1];
        Dictionary<int, double[]> carried = Run([new(0, -1, -1, null), new(1, 0, -1, Costs(2, 1, mode: 0)), new(2, -1, -1, null), new(3, 2, -1, last)], 2);
        Assert.Equal([0, 0], carried[1]);
        Assert.Equal([900, 0], carried[2]);
    }

    [Fact]
    public void AnOffsetIsMinusTheStrengthTimesTheLogOfTheReuse()
    {
        double[] offsets = new double[2];
        double mean = BlockTree.Offsets([1000, 1000], [1000, 0], 0.5, offsets);
        Assert.Equal([-0.5, 0], offsets);
        Assert.Equal(-0.25, mean);
    }

    private static Dictionary<int, double[]> Run(TreePicture[] plan, int blocks)
    {
        Dictionary<int, double[]> carried = plan.ToDictionary(p => p.Index, _ => new double[blocks]);
        int columns = blocks == 4 ? 2 : blocks;
        BlockTree.Propagate(plan, columns, blocks / columns, carried);
        return carried;
    }

    private static TreeCosts Costs(int columns, int rows, byte mode)
    {
        TreeCosts costs = new(columns * rows) { Bi = mode == 2 };
        Array.Fill(costs.Intra, 1000);
        Array.Fill(costs.Inter, 100);
        Array.Fill(costs.Modes, mode);
        return costs;
    }
}
