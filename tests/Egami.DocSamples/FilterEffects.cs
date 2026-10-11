using Egami.Operations;
using Egami.Pixels;

namespace Egami.DocSamples;

[Collection("Samples")]
public class FilterEffects
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A transparent canvas with an opaque blue badge in it, as a logo.</summary>
    private static Raster<Rgba8> Logo()
    {
        Raster<Rgba8> logo = Raster.Create<Rgba8>(200, 120);
        logo.Apply(ops => ops.FillColor(new Rgba8(40, 110, 220, 255), new Rect(40, 30, 120, 60)), Ct);
        return logo;
    }

    [Fact]
    public void Vignette()
    {
        using SampleFiles files = new();
        using Raster photo = Raster.Load("duck.png");

        #region vignette
        // Darken the corners: the edge of the ellipse passes through the corners and fades over half the radius.
        using Raster dark = photo.Derive(ops => ops.Vignette(), Ct);

        // A tighter, softer, warm-white vignette, off-center toward the subject.
        using Raster light = photo.Derive(
            ops => ops.Vignette(0.6f, softness: 0.8f, color: new Rgba8(255, 245, 230, 255), center: new System.Numerics.Vector2(photo.Width * 0.4f, photo.Height * 0.5f)),
            Ct);
        #endregion

        Assert.Equal(photo.Width, dark.Width);
    }

    [Fact]
    public void Glow()
    {
        using SampleFiles files = new();
        using Raster<Rgba8> logo = Logo();

        #region glow
        // A soft yellow glow around the badge, behind it; spread hardens it before it fades.
        using Raster outer = logo.Derive(ops => ops.Glow(8, new Rgba8(255, 210, 0, 230), spread: 0.3f), Ct);

        // A white glow coming in from the badge's edges, inside it only.
        using Raster inner = logo.Derive(ops => ops.Glow(5, Rgba8.White, inner: true), Ct);
        #endregion

        Assert.Equal(logo.Width, outer.Width);
    }

    [Fact]
    public void Bloom()
    {
        using SampleFiles files = new();
        using Raster photo = Raster.Load("egret.png");

        #region bloom
        // Highlights above 80% bleed light into their surroundings, added in linear light as a lens does.
        photo.Apply(ops => ops.Bloom(threshold: 0.8f, sigma: 12, intensity: 1.5f), Ct);
        #endregion

        Assert.True(photo.Width > 0);
    }

    [Fact]
    public void Artistic()
    {
        using SampleFiles files = new();
        using Raster photo = Raster.Load("duck.png");

        #region pixelate
        // 12-pixel cells, each the average of what it covers.
        using Raster blocks = photo.Derive(ops => ops.Pixelate(12), Ct);
        #endregion

        #region oil-paint
        using Raster painted = photo.Derive(ops => ops.OilPaint(radius: 4, levels: 20), Ct);
        using Raster magick = photo.Derive(ops => ops.OilPaint(3, levels: 256), Ct);   // ImageMagick's -paint 3, pixel for pixel
        #endregion

        #region kuwahara
        // Flattens texture into patches while keeping edges; a small blur first, as ImageMagick's -kuwahara 4x1.
        using Raster smooth = photo.Derive(ops => ops.Kuwahara(4, sigma: 1), Ct);
        #endregion

        #region spread
        // Frosted glass: every pixel takes a neighbour's value up to 3 pixels away; the same seed gives the same result.
        using Raster frosted = photo.Derive(ops => ops.Spread(3, seed: 42), Ct);
        #endregion

        Assert.Equal(photo.Width, frosted.Width);
    }

    [Fact]
    public void DropShadow()
    {
        using SampleFiles files = new();
        using Raster<Rgba8> logo = Logo();

        #region drop-shadow
        // Skia's DropShadow: 6 px right and down, blurred with sigma 8, black at 55%; the canvas grows to hold the shadow.
        using Raster shadowed = logo.Derive(ops => ops.DropShadow(6, 6, 8, new Rgba8(0, 0, 0, 140)), Ct);

        // Keep the size, or keep only the shadow (as Skia's DropShadowOnly and ImageMagick's -shadow).
        using Raster same = logo.Derive(ops => ops.DropShadow(4, 4, 3, Rgba8.Black, new DropShadowOptions { Expand = false }), Ct);
        using Raster shadow = logo.Derive(ops => ops.DropShadow(0, 10, 12, Rgba8.Black, new DropShadowOptions { ShadowOnly = true, SigmaY = 6 }), Ct);
        #endregion

        Assert.True(shadowed.Width > logo.Width);
        Assert.Equal(logo.Width, same.Width);
    }

    [Fact]
    public void Cookbook()
    {
        using SampleFiles files = new();
        using Raster<Rgba8> logo = Logo();
        using Raster photo = Raster.Load("duck.png");

        #region logo-shadow
        // A logo on a page: a soft shadow under it, then the result flattened onto white for a JPEG.
        logo.Apply(ops => ops
            .DropShadow(5, 7, 6, new Rgba8(0, 0, 0, 110))
            .Flatten(Rgba8.White), Ct);
        logo.Save("logo-shadow.jpg");
        #endregion

        #region blur-face
        // Hide a face: pixelate only the rectangle around it, with the grid starting at its corner so cells line up with it.
        Rect face = new(100, 40, 72, 64);
        photo.Apply(ops => ops.Within(face, o => o.Pixelate(12, new Point(face.X, face.Y))), Ct);
        #endregion

        #region oil-look
        // A painted look: a light blur to calm noise, the oil paint, then a touch more saturation and a vignette.
        using Raster painting = photo.Derive(ops => ops
            .Blur(0.8f)
            .OilPaint(radius: 4, levels: 24)
            .Saturation(1.15f)
            .Vignette(0.9f, softness: 0.6f), Ct);
        painting.Save("painting.png");
        #endregion

        Assert.True(File.Exists("painting.png"));
    }
}
