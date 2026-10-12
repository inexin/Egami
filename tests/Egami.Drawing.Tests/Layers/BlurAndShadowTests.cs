using System.Numerics;

using Egami.Operations;
using Egami.Pixels;

namespace Egami.Drawing.Tests.Layers;

public class BlurAndShadowTests
{
    /// <summary>
    /// The analytic rectangle and round rectangle are the true pixel means of a Gaussian blur, so they are checked against Egami's
    /// exact blur of the shape's coverage: a rectangle within one level from σ = 2 and two below (where the exact blur's sampled
    /// kernel and the pixel mean part); a round rectangle within two, and four at σ = 1.5, because the general path's corners are
    /// flattened to chords up to 0.1 pixel inside the arcs while the analytic ones are exact. Against the general path's fast
    /// blur, whose box cascade is up to five levels from a true Gaussian, they stay within six.
    /// </summary>
    [Theory]
    [InlineData(1.5f, 2, 4)]
    [InlineData(4, 1, 2)]
    [InlineData(12, 1, 2)]
    public void TheAnalyticBlursMatchAnExactBlurOfTheCoverage(float sigma, int rectTolerance, int roundTolerance)
    {
        SolidPaint paint = new(Rgba8.Black) { Blur = new MaskBlur(sigma) };
        RectF rect = new(30.25f, 20.5f, 47.5f, 31);
        RoundRect round = new(rect, new CornerRadii(new Vector2(12, 8), new Vector2(3, 3), new Vector2(15, 15), Vector2.Zero));
        foreach ((string name, Action<Drawing.Canvas> analytic, Path path, int tolerance) in new (string, Action<Drawing.Canvas>, Path, int)[]
        {
            ("rect", c => c.FillRect(rect, paint), Path.Rect(rect), rectTolerance),
            ("round rect", c => c.FillRoundRect(round, paint), Path.RoundRect(round), roundTolerance),
        })
        {
            Rgba8[] drawn = Draw(analytic, transparent: true);
            Rgba8[] exact = Blurred(path, sigma, BlurMethod.Exact);
            Rgba8[] general = Draw(c => c.FillPath(path, paint), transparent: true);
            Assert.True(MaxDifference(drawn, exact) <= tolerance, $"{name} σ {sigma}: {MaxDifference(drawn, exact)} levels from the exact blur");
            Assert.True(MaxDifference(drawn, general) <= 6, $"{name} σ {sigma}: {MaxDifference(drawn, general)} levels from the fast blur");
            Assert.True(MaxDifference(Blurred(path, sigma, BlurMethod.Fast), general) <= 1, $"{name} σ {sigma}: the general path is not the fast blur of the coverage");
        }
    }

    [Fact]
    public void BlurStylesKeepWhatTheySay()
    {
        Path star = Path.Circle(new Vector2(54, 40), 25);
        Rgba8[] normal = Draw(c => c.FillPath(star, new SolidPaint(Rgba8.Black) { Blur = new MaskBlur(3) }));
        Rgba8[] solid = Draw(c => c.FillPath(star, new SolidPaint(Rgba8.Black) { Blur = new MaskBlur(3, BlurStyle.Solid) }));
        Rgba8[] outer = Draw(c => c.FillPath(star, new SolidPaint(Rgba8.Black) { Blur = new MaskBlur(3, BlurStyle.Outer) }));
        Rgba8[] inner = Draw(c => c.FillPath(star, new SolidPaint(Rgba8.Black) { Blur = new MaskBlur(3, BlurStyle.Inner) }));
        int center = (40 * 108) + 54, outside = (40 * 108) + 54 + 28;
        Assert.Equal(0, solid[center].R);
        Assert.Equal(255, outer[center].R);
        Assert.True(outer[outside].R < 255);
        Assert.Equal(255, inner[outside].R);
        Assert.True(normal[outside].R < 255);
        Assert.True(inner[center].R < 30);
    }

    [Fact]
    public void ABlurIsInDevicePixelsWhateverTheTransform()
    {
        Rgba8[] plain = Draw(c => c.FillPath(Path.Rect(new RectF(20, 20, 60, 40)), new SolidPaint(Rgba8.Black) { Blur = new MaskBlur(4) }));
        Rgba8[] scaled = Draw(c =>
        {
            c.Scale(2);
            c.FillPath(Path.Rect(new RectF(10, 10, 30, 20)), new SolidPaint(Rgba8.Black) { Blur = new MaskBlur(4) });
        });
        Assert.True(MaxDifference(plain, scaled) <= 1);
    }

    [Fact]
    public void BlurredFillsAreTheSameAtOneBandAndAtMany()
    {
        SolidPaint paint = new(Rgba8.Black) { Blur = new MaskBlur(5, BlurStyle.Outer) };
        using Raster<Rgba8> one = Raster.Create(600, 500, Rgba8.White), many = Raster.Create(600, 500, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = one.CreateCanvas())
        {
            canvas.ForcedBands = 1;
            canvas.FillCircle(new Vector2(300, 250), 200, paint);
        }

        using (RasterCanvas<Rgba8> canvas = many.CreateCanvas())
        {
            canvas.ForcedBands = 6;
            canvas.FillCircle(new Vector2(300, 250), 200, paint);
        }

        for (int y = 0; y < 500; y += 7)
        {
            Assert.True(one.Row(y).SequenceEqual(many.Row(y)));
        }
    }

    [Fact]
    public void AShadowFallsAroundAndBelowTheShape()
    {
        Path card = Path.RoundRect(new RoundRect(new RectF(30, 20, 48, 30), 6));
        Rgba8[] low = Draw(c => c.DrawShadow(card, new ShadowOptions { Elevation = 4 }));
        Rgba8[] high = Draw(c => c.DrawShadow(card, new ShadowOptions { Elevation = 24 }));
        Rgba8[] see = Draw(c => c.DrawShadow(card, new ShadowOptions { Elevation = 24, Transparent = true }));

        // Under the opaque card nothing is drawn; a see-through card has its shadow under it too.
        int under = (35 * 108) + 54;
        Assert.Equal(Rgba8.White, high[under]);
        Assert.True(see[under].R < 255);

        // Higher casts a wider, softer shadow that reaches further below the card.
        int below = (58 * 108) + 54;
        Assert.True(high[below].R < low[below].R);
        Assert.Equal(Rgba8.White, Draw(c => c.DrawShadow(card, new ShadowOptions { Elevation = 0 }))[below]);
    }

    /// <summary>The shape's sharp coverage blurred by one of Egami's blurs, as black over transparent.</summary>
    private static Rgba8[] Blurred(Path path, float sigma, BlurMethod method)
    {
        using Raster<Rgba8> sharp = Raster.Create<Rgba8>(108, 80);
        using (RasterCanvas<Rgba8> canvas = sharp.CreateCanvas())
        {
            canvas.FillPath(path, Rgba8.Black);
        }

        using Raster<Rgba8> blurred = sharp.Derive(o => o.Blur(sigma, ColorSpace.Encoded, method), TestContext.Current.CancellationToken);
        Rgba8[] pixels = new Rgba8[108 * 80];
        for (int y = 0; y < 80; y++)
        {
            blurred.Row(y).CopyTo(pixels.AsSpan(y * 108, 108));
        }

        return pixels;
    }

    private static Rgba8[] Draw(Action<Drawing.Canvas> draw, bool transparent = false)
    {
        using Raster<Rgba8> raster = Raster.Create(108, 80, transparent ? default : Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
        {
            draw(canvas);
        }

        Rgba8[] pixels = new Rgba8[108 * 80];
        for (int y = 0; y < 80; y++)
        {
            raster.Row(y).CopyTo(pixels.AsSpan(y * 108, 108));
        }

        return pixels;
    }

    private static int MaxDifference(Rgba8[] a, Rgba8[] b)
    {
        int max = 0;
        for (int i = 0; i < a.Length; i++)
        {
            max = Math.Max(max, Math.Max(Math.Abs(a[i].R - b[i].R), Math.Abs(a[i].A - b[i].A)));
        }

        return max;
    }
}
