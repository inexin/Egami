using System.Globalization;

using Egami.Operations;
using Egami.Pixels;

namespace Egami.Tests.Operations;

/// <summary>
/// Deskew against pages turned by known angles (tools/make-geometry-references.py) and against ImageMagick's -deskew 40% on the
/// same pages, where ImageMagick finds the tilt.
/// </summary>
public class DeskewTests
{
    private static readonly string Images = Path.Combine(AppContext.BaseDirectory, "images", "geometry");
    private static readonly string References = Path.Combine(AppContext.BaseDirectory, "reference", "geometry");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Pages() => [.. Directory.GetFiles(Images, "page*.png").Select(Path.GetFileNameWithoutExtension).Select(n => n![4..])];

    [Theory]
    [MemberData(nameof(Pages))]
    public void FindsTheKnownTilt(string angle)
    {
        double tilt = double.Parse(angle, CultureInfo.InvariantCulture);
        using Raster page = Raster.Load(Path.Combine(Images, $"page{angle}.png"));
        float found = page.DetectSkew();
        Assert.True(Math.Abs(found - tilt) <= 0.1, $"tilt {tilt}: found {found}");

        // ImageMagick's -deskew prints the turn that straightens the page, the tilt's negative; it misses large tilts, so the
        // comparison holds where it finds the page's own angle.
        string line = File.ReadLines(Path.Combine(References, "manifest.txt")).Single(l => l.StartsWith($"page.deskew{angle} ", StringComparison.Ordinal));
        double magick = -double.Parse(line.Split(' ')[1], CultureInfo.InvariantCulture);
        if (Math.Abs(magick - tilt) <= 0.15)
        {
            Assert.True(Math.Abs(found - magick) <= 0.3, $"tilt {tilt}: found {found}, ImageMagick {magick}");
        }
    }

    [Fact]
    public void DeskewStraightensAndKeepsThePageSize()
    {
        using Raster<Gray8> page = Raster.Load<Gray8>(Path.Combine(Images, "page+2.00.png"));
        using Raster<Gray8> straight = page.Derive(ops => ops.Deskew(), Ct);
        Assert.Equal((page.Width, page.Height), (straight.Width, straight.Height));
        Assert.True(Math.Abs(straight.DetectSkew()) <= 0.1, $"left {straight.DetectSkew()}");

        // The uncovered corners take the top-left pixel's white.
        Assert.Equal(255, straight.GetPixel(0, 0).L);

        using Raster<Gray8> grown = page.Derive(ops => ops.Deskew(keepSize: false), Ct);
        Assert.True(grown.Width > page.Width && grown.Height > page.Height);
    }

    [Fact]
    public void PicturesAndNoiseAreLeftLevel()
    {
        using Raster<Rgba8> noise = OperationCoverageTests.Noise(400, 300, EgamiContext.Default);
        Assert.Equal(0, noise.DetectSkew());
        using Raster<Rgba8> flat = Raster.Create(300, 200, Rgba8.White);
        Assert.Equal(0, flat.DetectSkew());
        using Raster<Rgba8> photo = Raster.Load<Rgba8>(Path.Combine(AppContext.BaseDirectory, "images", "ops", "duck.png"));
        photo.Apply(ops => ops.Deskew(), Ct);
        Assert.Equal((640, 480), (photo.Width, photo.Height));
    }

    [Fact]
    public void BadArgumentsThrow()
    {
        using Raster<Rgba8> raster = Raster.Create<Rgba8>(4, 4);
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.DetectSkew(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.DetectSkew(46));
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.Apply(ops => ops.Deskew(filter: (ResampleFilter)44), Ct));
        Assert.Throws<ArgumentException>(() => raster.Apply(ops => ops.Within(new Rect(0, 0, 2, 2), s => s.Deskew()), Ct));
    }
}
