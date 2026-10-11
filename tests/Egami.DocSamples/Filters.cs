using Egami.Operations;
using Egami.Pixels;

namespace Egami.DocSamples;

[Collection("Samples")]
public class Filters
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void FastBlur()
    {
        using SampleFiles files = new();
        using Raster photo = Raster.Load("duck.png");

        #region fast-blur
        // A large, soft background blur: Fast costs the same at any sigma and is within 50 dB of the exact Gaussian from sigma 3.
        using Raster soft = photo.Derive(ops => ops.Blur(40, ColorSpace.Linear, BlurMethod.Fast), Ct);
        #endregion

        Assert.Equal(photo.Width, soft.Width);
    }

    [Fact]
    public void Median()
    {
        using SampleFiles files = new();
        using Raster photo = Raster.Load("duck.png");

        #region median
        // Salt-and-pepper noise disappears and edges stay sharp; radius 2 is a 5 × 5 window.
        photo.Apply(ops => ops.Median(2), Ct);
        #endregion

        Assert.True(photo.Width > 0);
    }

    [Fact]
    public void LensBlur()
    {
        using SampleFiles files = new();
        using Raster photo = Raster.Load("duck.png");

        #region lens-blur
        // Blur the background like a wide-open lens with a six-blade aperture: bright points become hexagons.
        LensBlurOptions lens = new() { Blades = 6, Rotation = 15, HighlightGain = 4, HighlightThreshold = 0.8f };
        photo.Apply(ops => ops.Within(new Rect(0, 0, photo.Width, photo.Height / 3), o => o.LensBlur(12, lens)), Ct);
        #endregion

        Assert.True(photo.Width > 0);
    }

    [Fact]
    public void Bilateral()
    {
        using SampleFiles files = new();
        using Raster photo = Raster.Load("egret.png");

        #region bilateral
        // Smooth skin or sky while keeping edges: neighbours count less the more their color differs (0.1 on the 0..1 scale).
        using Raster smooth = photo.Derive(ops => ops.BilateralBlur(spatialSigma: 4, rangeSigma: 0.1f), Ct);
        #endregion

        Assert.Equal(photo.Height, smooth.Height);
    }

    [Fact]
    public void Directional()
    {
        using SampleFiles files = new();
        using Raster photo = Raster.Load("duck.png");

        #region directional
        using Raster moving = photo.Derive(ops => ops.MotionBlur(radius: 12, sigma: 4, angle: 15), Ct);  // a streak toward 15°
        using Raster spinning = photo.Derive(ops => ops.RotationalBlur(8), Ct);                          // 8° around the center
        using Raster zooming = photo.Derive(ops => ops.ZoomBlur(0.2f, new System.Numerics.Vector2(320, 200)), Ct);
        #endregion

        Assert.Equal(photo.Width, zooming.Width);
    }

    [Fact]
    public void Noise()
    {
        using SampleFiles files = new();
        using Raster photo = Raster.Load("duck.png");

        #region noise
        // Film grain: gray Gaussian noise at a third of ImageMagick's default strength; the same seed gives the same grain.
        photo.Apply(ops => ops.AddNoise(NoiseKind.Gaussian, 0.3f, monochrome: true, seed: 42), Ct);
        #endregion

        Assert.True(photo.Width > 0);
    }

    [Fact]
    public void Despeckle()
    {
        using SampleFiles files = new();
        using Raster scan = Raster.Load("egret.png");

        #region despeckle
        // Clean a scan: specks shrink, edges and text stay. Median(1) is the stronger alternative.
        scan.Apply(ops => ops.Despeckle(), Ct);
        #endregion

        Assert.True(scan.Width > 0);
    }
}
