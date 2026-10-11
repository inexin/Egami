using Egami.Operations;
using Egami.Pixels;

namespace Egami.Tests.Operations.Filters;

/// <summary>DropShadow: canvas sizes, the exact method against a chain of Egami's steps, and how it works in scopes.</summary>
public class DropShadowTests
{
    private static readonly Rgba8 Shade = new(20, 30, 60, 160);

    private static CancellationToken Ct => FilterAssert.Ct;

    private static Raster<Rgba8> Logo() => Raster.Load<Rgba8>(Path.Combine(AppContext.BaseDirectory, "images", "filters", "logo.png"));

    [Theory]
    [InlineData(6, 4, 3, 160 + 3 + 15, 120 + 5 + 13)]
    [InlineData(-6, 0, 2.5f, 160 + 14 + 2, 120 + 8 + 8)]
    [InlineData(20, 30, 4, 160 + 0 + 32, 120 + 0 + 42)]
    [InlineData(0, 0, 0, 160, 120)]
    [InlineData(3.5f, -1.2f, 1.2f, 160 + 1 + 8, 120 + 5 + 3)]
    public void TheCanvasGrowsByThreeSigmaPlusTheOffset(float dx, float dy, float sigma, int width, int height)
    {
        using Raster<Rgba8> logo = Logo();
        using Raster<Rgba8> result = logo.Derive(o => o.DropShadow(dx, dy, sigma, Shade), Ct);
        Assert.Equal((width, height), (result.Width, result.Height));

        using Raster<Rgba8> kept = logo.Derive(o => o.DropShadow(dx, dy, sigma, Shade, new DropShadowOptions { Expand = false }), Ct);
        Assert.Equal((logo.Width, logo.Height), (kept.Width, kept.Height));
    }

    [Theory]
    [InlineData(6, 4, 3f, null)]
    [InlineData(-5, 7, 2f, 4f)]
    [InlineData(0, 0, 5f, null)]
    public void ExactIsWithinALevelOfTheSameChainOfSteps(int dx, int dy, float sigma, float? sigmaY)
    {
        using Raster<Rgba8> logo = Logo();
        float sy = sigmaY ?? sigma;
        DropShadowOptions options = new() { Method = BlurMethod.Exact, SigmaY = sigmaY };
        using Raster<Rgba8> result = logo.Derive(o => o.DropShadow(dx, dy, sigma, Shade, options), Ct);

        // By hand: a transparent canvas of the same size, the logo moved onto it, turned into the shadow color with its alpha
        // times the color's, blurred with the exact Gaussian per axis, then the logo drawn on top where it sits.
        int left = Math.Max(0, (int)Math.Ceiling((3 * sigma) - dx)), top = Math.Max(0, (int)Math.Ceiling((3 * sy) - dy));
        using Raster<Rgba8> expected = logo.Derive(
            o => o.Pad(result.Width, result.Height, default, Anchor.TopLeft)
                .Offset(left + dx, top + dy, EdgeMode.Transparent)
                .ColorMatrix(new ColorMatrix([0, 0, 0, 0, 20 / 255f, 0, 0, 0, 0, 30 / 255f, 0, 0, 0, 0, 60 / 255f, 0, 0, 0, 160 / 255f, 0]))
                .Convolve(ConvolutionKernel.Separable(Gaussian(sigma), Gaussian(sy)), EdgeMode.Transparent)
                .DrawImage(logo, new Point(left, top)),
            Ct);
        // Compared premultiplied: where the logo's edge is nearly transparent, its straight color is a ratio of small numbers.
        int worst = PremultipliedDifference(expected, result);
        Assert.True(worst <= 1, $"{worst} levels");
    }

    [Theory]
    [InlineData(5f)]
    [InlineData(10f)]
    public void FastIsCloseToExact(float sigma)
    {
        using Raster<Rgba8> logo = Logo();
        using Raster<Rgba8> exact = logo.Derive(o => o.DropShadow(4, 4, sigma, Rgba8.Black, new DropShadowOptions { Method = BlurMethod.Exact }), Ct);
        using Raster<Rgba8> fast = logo.Derive(o => o.DropShadow(4, 4, sigma, Rgba8.Black), Ct);
        Assert.True(FilterAssert.Psnr(exact, fast) >= 45, $"{FilterAssert.Psnr(exact, fast):F1} dB");
    }

    [Fact]
    public void ShadowOnlyLeavesTheImageOut()
    {
        using Raster<Rgba8> logo = Logo();
        using Raster<Rgba8> both = logo.Derive(o => o.DropShadow(30, 0, 0, Rgba8.Black), Ct);
        using Raster<Rgba8> only = logo.Derive(o => o.DropShadow(30, 0, 0, Rgba8.Black, new DropShadowOptions { ShadowOnly = true }), Ct);

        // A hard shadow 30 pixels right: the badge's left edge (x 10..39) has no shadow under it.
        Assert.Equal(new Rgba8(40, 110, 220, 255), both.Row(60)[20]);
        Assert.Equal(0, only.Row(60)[20].A);
        Assert.Equal(new Rgba8(0, 0, 0, 255), only.Row(60)[60]);
    }

    [Fact]
    public void AFractionalOffsetLandsBetweenPixels()
    {
        using Raster<Rgba8> logo = Logo();
        using Raster<Rgba8> half = logo.Derive(o => o.DropShadow(0.5f, 0, 0, Rgba8.Black, new DropShadowOptions { ShadowOnly = true, Expand = false }), Ct);

        // The badge's opaque left edge is at x = 10 on its straight middle rows: moved half a pixel, pixel 10 is half covered.
        Assert.InRange(half.Row(60)[10].A, 127, 128);
        Assert.Equal(255, half.Row(60)[11].A);
    }

    [Fact]
    public void ExpandIsRejectedInsideWithin()
    {
        using Raster<Rgba8> logo = Logo();
        ArgumentException error = Assert.Throws<ArgumentException>(() => logo.Apply(o => o.Within(new Rect(0, 0, 50, 50), s => s.DropShadow(2, 2, 3, Shade)), Ct));
        Assert.Contains("DropShadow", error.Message, StringComparison.Ordinal);
        Assert.Contains("size", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ArgumentsAreChecked()
    {
        using Raster<Rgba8> logo = Logo();
        Assert.Throws<ArgumentOutOfRangeException>(() => logo.Apply(o => o.DropShadow(float.NaN, 0, 2, Shade), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => logo.Apply(o => o.DropShadow(0, 20000, 2, Shade), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => logo.Apply(o => o.DropShadow(0, 0, -1, Shade), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DropShadowOptions { SigmaY = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new DropShadowOptions { Method = (BlurMethod)9 });
    }

    public static TheoryData<string> Chains() => ["fast", "exact", "only"];

    private static Action<RasterOps> Chain(string name) => name switch
    {
        "fast" => o => o.DropShadow(5, 3, 4, Shade, new DropShadowOptions { Expand = false }),
        "exact" => o => o.DropShadow(-3, 6, 2.5f, Shade, new DropShadowOptions { Expand = false, Method = BlurMethod.Exact, SigmaY = 1.5f }),
        _ => o => o.DropShadow(4, 4, 6, Shade, new DropShadowOptions { Expand = false, ShadowOnly = true }),
    };

    [Theory]
    [MemberData(nameof(Chains))]
    public void SameOnOneThread(string name)
    {
        FilterAssert.SameOnOneThread("shapes", Chain(name));
        FilterAssert.SameOnOneThread("shapes", o => o.DropShadow(7, -2, 5, Shade));
    }

    [Theory]
    [MemberData(nameof(Chains))]
    public void SameInsideArea(string name)
    {
        FilterAssert.SameInsideArea("shapes", new Rect(37, 21, 90, 70), Chain(name));
    }

    private static int PremultipliedDifference(Raster<Rgba8> expected, Raster<Rgba8> actual)
    {
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
        int worst = 0;
        for (int y = 0; y < expected.Height; y++)
        {
            for (int x = 0; x < expected.Width; x++)
            {
                Rgba8 e = expected.Row(y)[x], a = actual.Row(y)[x];
                worst = Math.Max(worst, Math.Abs(e.A - a.A));
                worst = Math.Max(worst, Math.Abs(((e.R * e.A) - (a.R * a.A)) / 255));
                worst = Math.Max(worst, Math.Abs(((e.G * e.A) - (a.G * a.A)) / 255));
                worst = Math.Max(worst, Math.Abs(((e.B * e.A) - (a.B * a.A)) / 255));
            }
        }

        return worst;
    }

    /// <summary>The normalized Gaussian of radius ceil(3σ), as Egami's exact blur weighs it.</summary>
    private static float[] Gaussian(float sigma)
    {
        int r = Math.Max(1, (int)Math.Ceiling(3 * sigma));
        double[] w = new double[(2 * r) + 1];
        for (int i = -r; i <= r; i++)
        {
            w[i + r] = Math.Exp(-(i * (double)i) / (2.0 * sigma * sigma));
        }

        double sum = w.Sum();
        return [.. w.Select(v => (float)(v / sum))];
    }
}
