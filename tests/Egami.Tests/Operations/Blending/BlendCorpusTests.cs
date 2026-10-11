using Egami.Pixels;

namespace Egami.Tests.Operations.Blending;

public class BlendCorpusTests
{
    [Fact]
    public void ThePairGridHoldsEveryValuePairInRed()
    {
        (Rgba8[] source, Rgba8[] destination) = BlendCorpus.PairGrid(0);
        bool[] seen = new bool[65536];
        for (int i = 0; i < source.Length; i++)
        {
            seen[(source[i].R << 8) | destination[i].R] = true;
        }

        Assert.All(seen, Assert.True);
    }

    [Fact]
    public void TheLayersHaveEveryKindOfAlpha()
    {
        (Rgba8[] source, Rgba8[] destination) = BlendCorpus.Layers(96, 64);
        foreach (Rgba8[] image in new[] { source, destination })
        {
            Assert.Contains(image, p => p.A == 0);
            Assert.Contains(image, p => p.A == 255);
            Assert.Contains(image, p => p.A is > 0 and < 255);
        }

        byte[] mask = BlendCorpus.Mask(96, 64);
        Assert.Contains(mask, m => m == 0);
        Assert.Contains(mask, m => m == 255);
        Assert.Contains(mask, m => m is > 0 and < 255);
    }
}
