using Egami.Operations;
using Egami.Pixels;

namespace Egami.Tests.Operations.Scopes;

public sealed class KeyingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void MakeTransparentClearsCloseColorsAndRampsWithSoftness()
    {
        using Raster<Rgba8> source = Raster.Create<Rgba8>(4, 1);
        source.Row(0)[0] = new Rgba8(0, 255, 0, 255);
        source.Row(0)[1] = new Rgba8(10, 245, 10, 255);
        source.Row(0)[2] = new Rgba8(60, 200, 60, 255);
        source.Row(0)[3] = new Rgba8(255, 0, 0, 255);
        using Raster<Rgba8> hard = source.Derive(ops => ops.MakeTransparent(new Rgba8(0, 255, 0, 255), 0.05f), Ct);
        Assert.Equal([0, 0, 255, 255], hard.Row(0).ToArray().Select(p => (int)p.A));
        Assert.Equal(new Rgba8(10, 245, 10, 0), hard.GetPixel(1, 0));

        // d for pixel 2 is sqrt(2·60² + 55²)/255/√3 ≈ 0.22; softness 0.3 from tolerance 0.05 gives alpha (0.22 − 0.05) / 0.3 ≈ 0.57.
        using Raster<Rgba8> soft = source.Derive(ops => ops.MakeTransparent(new Rgba8(0, 255, 0, 255), 0.05f, 0.3f), Ct);
        double d = Math.Sqrt(((60 * 60) + (55 * 55) + (60 * 60)) / 3.0) / 255;
        Assert.Equal((d - 0.05) / 0.3 * 255, soft.GetPixel(2, 0).A, 1.0);
        Assert.Equal(255, soft.GetPixel(3, 0).A);
        Assert.Equal(0, soft.GetPixel(0, 0).A);
    }

    [Fact]
    public void ReplaceColorAndMasks()
    {
        using Raster<Rgba8> source = Raster.Create(3, 1, new Rgba8(100, 100, 100, 255));
        source.Row(0)[1] = new Rgba8(103, 98, 100, 255);
        source.Row(0)[2] = new Rgba8(200, 0, 0, 255);
        using Raster<Rgba8> replaced = source.Derive(ops => ops.ReplaceColor(new Rgba8(100, 100, 100, 255), Rgba8.Blue, 0.02f), Ct);
        Assert.Equal([Rgba8.Blue, Rgba8.Blue, new Rgba8(200, 0, 0, 255)], replaced.Row(0).ToArray());
        using Raster<Gray8> mask = source.ColorMask(new Rgba8(100, 100, 100, 255), 0.02f);
        Assert.Equal([255, 255, 0], mask.Row(0).ToArray().Select(p => (int)p.L));
        using Raster<Gray8> exact = source.ColorMask(new Rgba8(100, 100, 100, 255));
        Assert.Equal([255, 0, 0], exact.Row(0).ToArray().Select(p => (int)p.L));
    }

    [Fact]
    public void FloodFillSpreadsByConnectivity()
    {
        // A diagonal line of white on black: four-connected stops at the first pixel, eight-connected runs down the line.
        using Raster<Rgba8> source = Raster.Create(5, 5, Rgba8.Black);
        for (int i = 0; i < 5; i++)
        {
            source.Row(i)[i] = Rgba8.White;
        }

        using Raster<Rgba8> four = source.Derive(ops => ops.FloodFill(new Point(0, 0), Rgba8.Red), Ct);
        Assert.Equal(Rgba8.Red, four.GetPixel(0, 0));
        Assert.Equal(Rgba8.White, four.GetPixel(1, 1));
        using Raster<Rgba8> eight = source.Derive(ops => ops.FloodFill(new Point(0, 0), Rgba8.Red, 0, Connectivity.Eight), Ct);
        Assert.Equal(Rgba8.Red, eight.GetPixel(4, 4));
        Assert.Equal(Rgba8.Black, eight.GetPixel(1, 0));

        // The black background, four-connected, goes around the line on both sides but not across it.
        using Raster<Gray8> wand = source.FloodMask(new Point(4, 0));
        Assert.Equal(255, wand.GetPixel(1, 0).L);
        Assert.Equal(0, wand.GetPixel(0, 1).L);
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Apply(ops => ops.FloodFill(new Point(5, 0), Rgba8.Red), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.FloodMask(new Point(-1, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Apply(ops => ops.MakeTransparent(Rgba8.Red, 1.5f), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Apply(ops => ops.MakeTransparent(Rgba8.Red, 0.1f, -1), Ct));
    }

    [Fact]
    public void FloodFillsVisitedSetCountsAgainstTheBudget()
    {
        Egami.Tests.Memory.MemoryPeak peak = Egami.Tests.Memory.PeakMemory.Measure(context =>
        {
            using Raster<Rgba8> raster = Raster.Create(1000, 800, Rgba8.Black, context);
            raster.Apply(ops => ops.FloodFill(new Point(10, 10), Rgba8.Red), Ct);
        });
        Assert.True(peak.PeakPixelBytes <= (1000 * 800 * 4) + (1000 * 800 / 8) + 4096, peak.ToString());
        Assert.True(peak.PeakPixelBytes > 1000 * 800 * 4, peak.ToString());
    }

    [Fact]
    public void SelectByColorThenEditOnlyThat()
    {
        using Raster<Rgba8> source = Raster.Create(4, 1, new Rgba8(50, 50, 200, 255));
        source.Row(0)[3] = new Rgba8(200, 50, 50, 255);
        using Raster<Gray8> blue = source.ColorMask(new Rgba8(50, 50, 200, 255), 0.1f);
        source.Apply(ops => ops.Within(new Mask(blue), o => o.Invert()), Ct);
        Assert.Equal(new Rgba8(205, 205, 55, 255), source.GetPixel(0, 0));
        Assert.Equal(new Rgba8(200, 50, 50, 255), source.GetPixel(3, 0));
    }
}
