using Egami.Operations;
using Egami.Pixels;

namespace Egami.Tests.Operations;

public class MipChainTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void LevelsHalveRoundingUpDownToOnePixel()
    {
        using Raster<Rgba8> source = OperationCoverageTests.Noise(37, 10, EgamiContext.Default);
        using MipChain chain = MipChain.Build(source);
        Assert.Same(source, chain.Levels[0]);
        Assert.Equal([(37, 10), (19, 5), (10, 3), (5, 2), (3, 1), (2, 1), (1, 1)], chain.Levels.Select(l => (l.Width, l.Height)));
    }

    [Fact]
    public void LevelsAveragePremultipliedBlocks()
    {
        using Raster<Rgba8> source = Raster.Create<Rgba8>(3, 2);
        source.Row(0)[0] = new Rgba8(200, 0, 0, 255);
        source.Row(0)[1] = new Rgba8(0, 0, 0, 0);
        source.Row(1)[0] = new Rgba8(100, 0, 0, 255);
        source.Row(1)[1] = new Rgba8(0, 0, 0, 0);
        source.Row(0)[2] = new Rgba8(10, 20, 30, 255);
        source.Row(1)[2] = new Rgba8(30, 40, 50, 255);
        using MipChain chain = MipChain.Build(source);
        Raster<Rgba8> level = (Raster<Rgba8>)chain.Levels[1];

        // Transparent pixels add no color: the block is half covered by (150, 0, 0); the odd column averages its two pixels.
        Assert.Equal(new Rgba8(150, 0, 0, 128), level.GetPixel(0, 0));
        Assert.Equal(new Rgba8(20, 30, 40, 255), level.GetPixel(1, 0));
    }

    [Fact]
    public void ResizeFromALevelIsCloseToADirectResize()
    {
        using Raster<Rgba8> photo = Raster.Load<Rgba8>(Path.Combine(AppContext.BaseDirectory, "images", "ops", "duck.png"));
        using MipChain chain = MipChain.Build(photo);
        foreach ((int w, int h) in new[] { (160, 120), (100, 75), (64, 48), (300, 225) })
        {
            using Raster<Rgba8> fromChain = (Raster<Rgba8>)chain.Resize(w, h);
            using Raster<Rgba8> direct = photo.Derive(ops => ops.Resize(w, h), Ct);
            double psnr = TransformTests.Psnr(direct, fromChain, inset: 0);
            Assert.True(psnr >= 40, $"{w}×{h}: PSNR {psnr:0.0} dB");
        }

        using Raster fitted = chain.Fit(100, 100);
        using Raster fit = photo.Derive(ops => ops.Fit(100, 100), Ct);
        Assert.Equal((fit.Width, fit.Height), (fitted.Width, fitted.Height));
        using Raster kept = chain.Fit(1000, 1000, enlarge: false);
        Assert.Equal((640, 480), (kept.Width, kept.Height));
    }

    [Fact]
    public void AnimationsKeepTheirFramesAndTheSourceIsNotDisposed()
    {
        using Raster<Rgba8> animation = Raster.Create(16, 8, Rgba8.Red);
        animation.Frames.Add(animation.Frames[0]).Row(0).Fill(Rgba8.Blue);
        MipChain chain = MipChain.Build(animation, ColorSpace.Linear);
        Assert.All(chain.Levels, l => Assert.Equal(2, l.FrameCount));
        chain.Dispose();
        Assert.Equal(Rgba8.Red, animation.GetPixel(0, 0));
        Assert.Throws<ObjectDisposedException>(() => chain.Levels);
        Assert.Throws<ObjectDisposedException>(() => chain.Resize(2, 2));
    }

    [Fact]
    public void BadArgumentsThrow()
    {
        using Raster<Rgba8> raster = Raster.Create<Rgba8>(4, 4);
        Assert.Throws<ArgumentNullException>(() => MipChain.Build(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => MipChain.Build(raster, (ColorSpace)5));
        using MipChain chain = MipChain.Build(raster);
        Assert.Throws<ArgumentOutOfRangeException>(() => chain.Resize(0, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => chain.Resize(2, 2, (ResampleFilter)77));
    }
}
