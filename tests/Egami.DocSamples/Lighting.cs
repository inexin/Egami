using System.Numerics;

using Egami.Operations;
using Egami.Pixels;

namespace Egami.DocSamples;

[Collection("Samples")]
public class Lighting
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Lights()
    {
        using SampleFiles files = new();
        using Raster<Rgba8> logo = Logo();

        #region lights
        // Three light sources, as SVG and Skia have them. Positions are pixels: x right, y down, z up out of the image.
        Light sun = new DistantLight(azimuth: 225, elevation: 40);              // from the top left, 40° up
        Light bulb = new PointLight(new Vector3(60, 20, 80)) { Color = new Rgba8(255, 240, 210, 255) };
        Light spot = new SpotLight(new Vector3(0, 0, 150), target: new Vector3(120, 60, 0), exponent: 4, coneAngle: 30);

        // Two ways a surface reflects: matte (N·L) or shiny (a highlight, (N·H)^shininess).
        LightingModel matte = LightingModel.Diffuse(kd: 1.2f);
        LightingModel shiny = LightingModel.Specular(ks: 1, shininess: 20);

        using Raster<Rgba8> map = logo.Derive(ops => ops.Lighting(sun, matte), Ct);   // the light map, opaque
        using Raster<Rgba8> glints = logo.Derive(ops => ops.Lighting(bulb, shiny), Ct); // alpha = brightest channel
        using Raster<Rgba8> pool = logo.Derive(ops => ops.Lighting(spot, matte), Ct);
        #endregion

        Assert.Equal(255, map.Row(0)[0].A);
        Assert.Equal(logo.Width, pool.Width);
    }

    [Fact]
    public void LightingAsHeightField()
    {
        using SampleFiles files = new();
        using Raster photo = Raster.Load("duck.png");

        #region lighting
        // A photo's luminance as the height: light areas stand up. SurfaceScale is the height of white, in pixels.
        LightingOptions relief = new() { Height = HeightSource.Luminance, SurfaceScale = 6 };
        using Raster lit = photo.Derive(ops => ops.Lighting(new DistantLight(300, 50), LightingModel.Diffuse(), relief), Ct);

        // The same light blended onto the photo instead of replacing it.
        using Raster shaded = photo.Derive(ops => ops.Lighting(new DistantLight(300, 50), LightingModel.Diffuse(1.3f), relief with { Blend = BlendMode.Overlay }), Ct);
        #endregion

        Assert.Equal(photo.Width, shaded.Width);
    }

    [Fact]
    public void Bevel()
    {
        using SampleFiles files = new();
        using Raster<Rgba8> logo = Logo();

        #region bevel
        // A logo on a transparent canvas: alpha is the height, so soften its edges first to give them a slope.
        logo.Apply(ops => ops
            .Blur(2.5f)
            .Lighting(new DistantLight(225, 35), LightingModel.Diffuse(1.4f),
                new LightingOptions { SurfaceScale = 5, Blend = BlendMode.Multiply })      // shade the slopes
            .Lighting(new DistantLight(225, 35), LightingModel.Specular(0.8f, 24),
                new LightingOptions { SurfaceScale = 5, Blend = BlendMode.Screen }), Ct); // and add a highlight
        logo.Save("bevel.png");
        #endregion

        Assert.Equal(0, logo.Row(0)[0].A);
    }

    [Fact]
    public void EmbossedText()
    {
        using SampleFiles files = new();
        using Raster page = Raster.Load("scan.tif");

        #region emboss
        // Gray relief: flat paper becomes mid gray, strokes rise toward the light at the top left.
        using Raster embossed = page.Derive(ops => ops.Emboss(angle: 135, depth: 3, sigma: 1), Ct);

        // Each color channel on its own, softer and deeper.
        using Raster colored = page.Derive(ops => ops.Emboss(angle: 45, depth: 6, sigma: 2, grayscale: false), Ct);
        #endregion

        Assert.Equal(page.Width, embossed.Width);
    }

    [Fact]
    public void Shade()
    {
        using SampleFiles files = new();
        using Raster photo = Raster.Load("egret.png");

        #region shade
        // ImageMagick's -shade 30x30 (identical at 8 bits) and +shade 120x45.
        using Raster gray = photo.Derive(ops => ops.Shade(azimuth: 30, elevation: 30), Ct);
        using Raster colored = photo.Derive(ops => ops.Shade(azimuth: 120, elevation: 45, colored: true), Ct);
        #endregion

        Assert.Equal(photo.Height, colored.Height);
    }

    /// <summary>A blue rounded badge on a transparent canvas.</summary>
    private static Raster<Rgba8> Logo()
    {
        Raster<Rgba8> logo = Raster.Create<Rgba8>(240, 120);
        logo.Apply(ops => ops.FillColor(new Rgba8(40, 110, 220, 255), new Rect(20, 20, 200, 80)), Ct);
        return logo;
    }
}
