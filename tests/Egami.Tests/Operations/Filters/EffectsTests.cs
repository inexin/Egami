using System.Numerics;

using Egami.Operations;
using Egami.Pixels;

namespace Egami.Tests.Operations.Filters;

/// <summary>Vignette, Glow and Bloom: Glow and Bloom against the same chains written by hand, Vignette by its shape.</summary>
public class EffectsTests
{
    private static readonly Rgba8 Gold = new(255, 190, 40, 200);

    private static CancellationToken Ct => FilterAssert.Ct;

    [Theory]
    [InlineData(0f)]
    [InlineData(0.4f)]
    public void OuterGlowIsTheLayerChainUnderTheImage(float spread)
    {
        using Raster<Rgba8> source = FilterAssert.Load("shapes");
        using Raster<Rgba8> effect = source.Derive(o => o.Glow(6, Gold, spread), Ct);

        // By hand: the layer is the color with the image's alpha, blurred fast, spread and faded; the image goes over it.
        using Raster<Rgba8> layer = source.Derive(
            o =>
            {
                o.ColorMatrix(new ColorMatrix([0, 0, 0, 0, 1, 0, 0, 0, 0, 190 / 255f, 0, 0, 0, 0, 40 / 255f, 0, 0, 0, 1, 0]))
                    .Blur(6, ColorSpace.Encoded, BlurMethod.Fast);
                if (spread > 0)
                {
                    o.Arithmetic(PixelMath.Multiply, 1 / (1 - spread), ColorChannels.Alpha);
                }

                o.Opacity(200 / 255f);
            },
            Ct);
        layer.Apply(o => o.DrawImage(source, new Point(0, 0)), Ct);
        FilterAssert.Same(layer, effect);
    }

    [Fact]
    public void InnerGlowIsTheLayerChainOverTheImage()
    {
        using Raster<Rgba8> source = FilterAssert.Load("shapes");
        using Raster<Rgba8> effect = source.Derive(o => o.Glow(4, Rgba8.White, 0.2f, inner: true), Ct);

        // By hand: white with alpha 1 − the image's, blurred and spread, drawn source-atop so it stays inside the shape.
        using Raster<Rgba8> layer = source.Derive(
            o => o.ColorMatrix(new ColorMatrix([0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, -1, 1]))
                .Blur(4, ColorSpace.Encoded, BlurMethod.Fast)
                .Arithmetic(PixelMath.Multiply, 1 / 0.8f, ColorChannels.Alpha),
            Ct);
        using Raster<Rgba8> expected = source.Derive(o => o.DrawImage(layer, new Point(0, 0), BlendMode.SourceAtop), Ct);
        FilterAssert.Same(expected, effect);
    }

    [Theory]
    [InlineData(0.8f, 10f, 1f)]
    [InlineData(0.5f, 3f, 2.5f)]
    [InlineData(0f, 1.5f, 0.5f)]
    public void BloomIsTheBrightPassScreenedInLinearLight(float threshold, float sigma, float intensity)
    {
        using Raster<Rgba8> source = FilterAssert.Load("duck");
        using Raster<Rgba8> effect = source.Derive(o => o.Bloom(threshold, sigma, intensity), Ct);

        // By hand: (value − threshold) stretched and scaled in one clamped matrix, blurred fast in linear light, screened on.
        float k = intensity / (1 - threshold), offset = -threshold * k;
        using Raster<Rgba8> layer = source.Derive(
            o => o.ColorMatrix(new ColorMatrix([k, 0, 0, 0, offset, 0, k, 0, 0, offset, 0, 0, k, 0, offset, 0, 0, 0, 1, 0]))
                .Blur(sigma, ColorSpace.Linear, BlurMethod.Fast),
            Ct);
        using Raster<Rgba8> expected = source.Derive(o => o.DrawImage(layer, new Point(0, 0), BlendMode.Screen, 1, ColorSpace.Linear), Ct);
        FilterAssert.Same(expected, effect);
    }

    [Fact]
    public void BloomOnlyBrightens()
    {
        using Raster<Rgba8> source = FilterAssert.Load("duck");
        using Raster<Rgba8> effect = source.Derive(o => o.Bloom(0.6f, 5), Ct);
        bool changed = false;
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                Rgba8 s = source.Row(y)[x], e = effect.Row(y)[x];
                Assert.True(e.R >= s.R && e.G >= s.G && e.B >= s.B, $"({x}, {y})");
                changed |= e != s;
            }
        }

        Assert.True(changed);
    }

    [Fact]
    public void VignetteKeepsTheMiddleAndColorsFarCorners()
    {
        using Raster<Rgba8> source = FilterAssert.Load("duck");
        using Raster<Rgba8> soft = source.Derive(o => o.Vignette(), Ct);
        using Raster<Rgba8> small = source.Derive(o => o.Vignette(0.3f, 0.2f, new Rgba8(10, 200, 30, 255)), Ct);
        int cx = source.Width / 2, cy = source.Height / 2;
        Assert.Equal(source.Row(cy)[cx], soft.Row(cy)[cx]);
        Assert.Equal(source.Row(cy)[cx], small.Row(cy)[cx]);
        Assert.Equal(new Rgba8(10, 200, 30, 255), small.Row(0)[0]);
        Assert.Equal(new Rgba8(10, 200, 30, 255), small.Row(source.Height - 1)[source.Width - 1]);

        // The default edge passes through the corners, where the image is half covered.
        Rgba8 corner = source.Row(0)[0], darkened = soft.Row(0)[0];
        Assert.InRange(darkened.G, (corner.G / 2) - 12, (corner.G / 2) + 12);
    }

    [Fact]
    public void VignetteFollowsItsCenterAndAlpha()
    {
        using Raster<Rgba8> source = FilterAssert.Load("duck");
        using Raster<Rgba8> moved = source.Derive(o => o.Vignette(0.3f, 0, center: new Vector2(10, 10)), Ct);
        Assert.Equal(source.Row(10)[10], moved.Row(10)[10]);
        Assert.Equal(new Rgba8(0, 0, 0, 255), moved.Row(source.Height - 1)[source.Width - 1]);

        using Raster<Rgba8> none = source.Derive(o => o.Vignette(0.2f, 0.5f, new Rgba8(255, 0, 0, 0)), Ct);
        FilterAssert.Same(source, none);

        using Raster<Rgba8> half = source.Derive(o => o.Vignette(0.1f, 0, new Rgba8(255, 255, 255, 128)), Ct);
        Rgba8 s = source.Row(0)[0], h = half.Row(0)[0];
        Assert.InRange(h.R, ((s.R * 127) + (255 * 128)) / 255 - 1, ((s.R * 127) + (255 * 128)) / 255 + 1);
    }

    [Fact]
    public void ArgumentsAreChecked()
    {
        using Raster<Rgba8> source = FilterAssert.Load("duck");
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Apply(o => o.Vignette(-0.1f), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Apply(o => o.Vignette(11), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Apply(o => o.Vignette(1, 1.5f), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Apply(o => o.Vignette(1, float.NaN), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Apply(o => o.Vignette(center: new Vector2(float.NaN, 0)), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Apply(o => o.Glow(-1, Gold), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Apply(o => o.Glow(2, Gold, 1), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Apply(o => o.Glow(2, Gold, -0.1f), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Apply(o => o.Bloom(1), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Apply(o => o.Bloom(0.5f, -2), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Apply(o => o.Bloom(0.5f, 2, 101), Ct));
    }

    public static TheoryData<string> Chains() => ["vignette", "glow", "glow-inner", "bloom"];

    private static Action<RasterOps> Chain(string name) => name switch
    {
        "vignette" => o => o.Vignette(0.7f, 0.6f, new Rgba8(30, 10, 60, 230)),
        "glow" => o => o.Glow(5, Gold, 0.2f),
        "glow-inner" => o => o.Glow(3, Rgba8.White, inner: true),
        _ => o => o.Bloom(0.5f, 6, 1.2f),
    };

    [Theory]
    [MemberData(nameof(Chains))]
    public void SameOnOneThread(string name)
    {
        FilterAssert.SameOnOneThread("shapes", Chain(name));
    }

    [Theory]
    [MemberData(nameof(Chains))]
    public void SameInsideArea(string name)
    {
        FilterAssert.SameInsideArea("shapes", new Rect(37, 21, 90, 70), Chain(name));
    }
}
