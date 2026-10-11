using System.Numerics;

using Egami.Operations;
using Egami.Pixels;
using Egami.Quantization;

namespace Egami.DocSamples;

[Collection("Samples")]
public class Tone
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void BasicTonePanel()
    {
        using SampleFiles files = new();
        using Raster<Rgba8> raster = Raster.Load<Rgba8>("duck.png");

        #region tone
        // A basic tone panel: levels with a midtone lift, a gentle S-curve, and a warm tint in the midtones. The three steps run as
        // one pass over the pixels.
        raster.Apply(o => o
            .Level(black: 0.04f, white: 0.96f, gamma: 1.1f)
            .SigmoidalContrast(4, midpoint: 0.5f)
            .Tint(new Rgba8(255, 200, 150, 255), 0.15f), Ct);
        raster.Save("duck-toned.jpg");
        #endregion

        Assert.True(File.Exists("duck-toned.jpg"));
    }

    [Fact]
    public void PosterAndAccessibilityCheck()
    {
        using SampleFiles files = new();
        using Raster<Rgba8> raster = Raster.Load<Rgba8>("duck.png");

        #region effects
        // A two-color poster, then a check of how it reads to someone with deuteranopia.
        raster.Apply(o => o.Duotone(Rgba8.FromHex("#1b2a49"), Rgba8.FromHex("#f2c14e")).Posterize(4, DitherMode.Ordered), Ct);
        using Raster<Rgba8> preview = raster.Derive(o => o.SimulateColorBlindness(ColorVisionDeficiency.Deuteranopia), Ct);

        // High contrast for low vision: gray, lightness inverted so dark becomes light, and more contrast.
        using Raster<Rgba8> readable = raster.Derive(o => o.HighContrast(grayscale: true, ContrastInvert.Lightness, contrast: 0.3f), Ct);
        #endregion

        Assert.Equal(raster.Width, preview.Width);
        Assert.Equal(raster.Width, readable.Width);
    }

    [Fact]
    public void CurvesFromPoints()
    {
        using SampleFiles files = new();
        using Raster<Rgba8> raster = Raster.Load<Rgba8>("duck.png");

        #region curves
        // Curves as a photo editor draws them: a smooth curve through the points that never overshoots, here a mild S on all
        // three channels and a lift in the blue shadows.
        ToneCurve s = ToneCurve.FromPoints([(0, 0), (0.25f, 0.2f), (0.75f, 0.82f), (1, 1)]);
        ToneCurve blue = ToneCurve.FromPoints([(0, 0.06f), (0.5f, 0.5f), (1, 1)]);
        raster.Apply(o => o.ApplyFilter(ColorFilter.Curves(s, s, s.Then(blue))), Ct);
        #endregion

        Assert.Equal(0.5f, blue.Evaluate(0.5f), 3);
    }

    [Fact]
    public void ComposeFilters()
    {
        using SampleFiles files = new();
        using Raster<Rgba8> raster = Raster.Load<Rgba8>("duck.png");

        #region color-filter
        // A look as a value: build it once, mix it in at 70%, apply it to any image or to your own pixels.
        ColorFilter look = ColorFilter.Level(0.03f, 0.97f).Then(ColorFilter.Saturation(1.2f)).Then(ColorFilter.Tint(new Rgba8(255, 190, 140, 255), 0.2f));
        ColorFilter subtle = ColorFilter.Lerp(ColorFilter.Identity, look, 0.7f);
        raster.Apply(o => o.ApplyFilter(subtle), Ct);

        Span<Rgba8> swatch = [new Rgba8(200, 120, 80, 255), new Rgba8(40, 60, 90, 255)];
        subtle.Apply(swatch);
        #endregion

        Assert.False(look.ChangesAlpha);
    }

    [Fact]
    public void CubeFiles()
    {
        using SampleFiles files = new();
        using Raster<Rgba8> raster = Raster.Load<Rgba8>("duck.png");

        #region cube
        // Export a grade for DaVinci Resolve or Premiere, then load a .cube file and apply it at 70%.
        ColorFilter grade = ColorFilter.Curves(ToneCurve.FromPoints([(0, 0), (0.25f, 0.2f), (0.75f, 0.82f), (1, 1)]))
            .Then(ColorFilter.Saturation(1.1f));
        CubeFile.Write("my-grade.cube", grade.ToLut3D(size: 33), title: "My grade");

        Lut3D lut = CubeFile.Read("my-grade.cube");
        raster.Apply(o => o.ApplyFilter(ColorFilter.Lerp(ColorFilter.Identity, ColorFilter.FromLut(lut), 0.7f)), Ct);

        // Tables can also be built from a function, and read at any color.
        Lut3D warm = Lut3D.FromFunction(c => new Vector3(c.X, c.Y * 0.97f, c.Z * 0.9f), size: 17);
        Vector3 gray = warm.Evaluate(new Vector3(0.5f), LutInterpolation.Tetrahedral);
        #endregion

        Assert.Equal(33, lut.Size);
        Assert.Equal(0.5f, gray.X, 4);
    }

    [Fact]
    public void HaldRoundTrip()
    {
        using SampleFiles files = new();

        #region hald
        // Export the identity Hald image, grade it in any editor, and load the graded image back as a table.
        using Raster<Rgba8> identity = Raster.HaldIdentity(level: 8);   // 512 × 512, a 64³ table
        identity.Save("hald.png");

        using Raster graded = Raster.Load("hald.png");
        Lut3D fromHald = Lut3D.FromHald(graded);
        using Raster<Rgba16> back = fromHald.ToHald(level: 8);
        #endregion

        Assert.Equal(64, fromHald.Size);
        Assert.Equal(512, back.Width);
    }

    [Fact]
    public void Threshold()
    {
        using SampleFiles files = new();
        using Raster<Rgba8> raster = Raster.Load<Rgba8>("duck.png");

        #region threshold
        // Two colors: navy below half luminance, cream above; alpha is kept.
        using Raster<Rgba8> stamp = raster.Derive(o => o.Threshold(0.5f, ThresholdMode.Luminance, Rgba8.Navy, new Rgba8(250, 240, 220, 255)), Ct);
        #endregion

        Assert.Equal(raster.Width, stamp.Width);
    }

    [Fact]
    public void AutoAdjustments()
    {
        using SampleFiles files = new();
        using Raster<Rgba8> raster = Raster.Load<Rgba8>("duck.png");
        raster.Apply(o => o.Level(outputBlack: 0.25f, outputWhite: 0.7f), Ct);

        #region auto
        // Fix a dull scan: stretch it to the full range, clipping the darkest 2% and lightest 1%, then set the midtones.
        raster.Apply(o => o.Normalize().AutoGamma(), Ct);

        // Or stretch each channel on its own, which also removes a cast.
        raster.Apply(o => o.AutoLevel(linked: false), Ct);

        // Measured steps measure only their scope: here the sky alone is stretched.
        raster.Apply(o => o.Within(new Rect(0, 0, raster.Width, raster.Height / 3), sky => sky.AutoLevel()), Ct);
        #endregion

        Assert.Equal(640, raster.Width);
    }

    [Fact]
    public void MeasureOnceApplyToMany()
    {
        using SampleFiles files = new();
        using Raster<Rgba8> reference = Raster.Load<Rgba8>("duck.png");
        using Raster<Rgba8> other = Raster.Load<Rgba8>("egret.png");
        Raster<Rgba8>[] shoot = [other];

        #region measure
        // Measure once on a reference shot, then apply the same correction to the whole shoot.
        ColorFilter fix = reference.MeasureAutoLevel().Then(reference.MeasureWhiteBalance());
        foreach (Raster<Rgba8> shot in shoot)
        {
            shot.Apply(o => o.ApplyFilter(fix), Ct);
        }

        // The other measured steps have their Measure helpers too.
        ColorFilter stretch = reference.MeasureContrastStretch(0.02f, 0.01f);
        ColorFilter midtones = reference.MeasureAutoGamma();
        ColorFilter spread = reference.MeasureEqualize();
        #endregion

        Assert.NotNull(stretch.Then(midtones).Then(spread));
    }

    [Fact]
    public void WhiteBalance()
    {
        using SampleFiles files = new();
        using Raster<Rgba8> raster = Raster.Load<Rgba8>("duck.png");

        #region white-balance
        // Correct a tungsten-lit photo, as a raw editor's temperature slider does.
        raster.Apply(o => o.WhiteBalance(temperature: 3200), Ct);

        // Or pick a gray card's color and make it neutral.
        raster.Apply(o => o.WhiteBalance(new Rgba8(182, 170, 151, 255)), Ct);

        // Or let the image tell: ImageMagick's gray world, or the brightest pixels as white.
        raster.Apply(o => o.AutoWhiteBalance(WhiteBalanceMethod.GrayWorld), Ct);
        raster.Apply(o => o.AutoWhiteBalance(WhiteBalanceMethod.WhitePatch), Ct);
        #endregion

        Assert.Equal(640, raster.Width);
    }

    [Fact]
    public void Equalize()
    {
        using SampleFiles files = new();
        using Raster<Rgba8> raster = Raster.Load<Rgba8>("duck.png");

        #region equalize
        // Spread the lightness evenly without shifting colors, or each channel on its own as ImageMagick's -equalize.
        using Raster<Rgba8> even = raster.Derive(o => o.Equalize(EqualizeMode.Luminance), Ct);
        using Raster<Rgba8> strong = raster.Derive(o => o.Equalize(EqualizeMode.Channels), Ct);
        #endregion

        Assert.Equal(even.Width, strong.Width);
    }

    [Fact]
    public void LocalDetail()
    {
        using SampleFiles files = new();
        using Raster<Rgba8> raster = Raster.Load<Rgba8>("duck.png");

        #region clahe
        // Bring out detail in a flat, foggy landscape: CLAHE on 8 × 8 tiles, then ImageMagick's local contrast.
        raster.Apply(o => o.Clahe(tilesX: 8, tilesY: 8, clipLimit: 2).LocalContrast(radius: 0.1f, strength: 0.15f), Ct);
        #endregion

        Assert.Equal(640, raster.Width);
    }

    [Fact]
    public void ThresholdAPage()
    {
        using SampleFiles files = new();
        using Raster<Gray8> page = Raster.Load<Gray8>("duck.png");

        #region auto-threshold
        // Threshold a scanned page for OCR: Otsu's level for an evenly lit page, or Bradley's local threshold for uneven light.
        float level = page.FindThreshold(ThresholdMethod.Otsu);
        using Raster<Gray8> even = page.Derive(o => o.AutoThreshold(ThresholdMethod.Otsu), Ct);
        using Raster<Gray8> uneven = page.Derive(o => o.AdaptiveThreshold(limit: 0.15f), Ct);
        #endregion

        Assert.InRange(level, 0f, 1f);
        Assert.Equal(even.Width, uneven.Width);
    }
}
