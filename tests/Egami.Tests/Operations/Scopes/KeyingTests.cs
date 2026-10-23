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
    public void ChromaKeyClearsTheScreenChromaAndRampsWithBlend()
    {
        // The key, a darker and a lighter patch of the same screen, a gray and a red.
        Rgba8 key = new(0, 177, 64, 255);
        using Raster<Rgba8> source = Raster.Create<Rgba8>(5, 1);
        source.Row(0)[0] = key;
        source.Row(0)[1] = new Rgba8(0, 120, 43, 255);
        source.Row(0)[2] = new Rgba8(60, 200, 90, 255);
        source.Row(0)[3] = new Rgba8(128, 128, 128, 255);
        source.Row(0)[4] = new Rgba8(255, 0, 0, 255);
        using Raster<Rgba8> hard = source.Derive(ops => ops.ChromaKey(key, 0.1f, 0), Ct);
        Assert.Equal([0, 0, 0, 255, 255], hard.Row(0).ToArray().Select(p => (int)p.A));
        Assert.Equal(new Rgba8(0, 120, 43, 0), hard.GetPixel(1, 0));

        // The gray lies at about 0.3 of the largest chroma distance; blend 0.3 from similarity 0.1 gives alpha (d − 0.1) / 0.3.
        using Raster<Rgba8> soft = source.Derive(ops => ops.ChromaKey(key, 0.1f, 0.3f), Ct);
        double d = ChromaDistance(key, new Rgba8(128, 128, 128, 255));
        Assert.InRange(d, 0.25, 0.35);
        Assert.Equal((d - 0.1) / 0.3 * 255, soft.GetPixel(3, 0).A, 1.0);
        Assert.Equal(255, soft.GetPixel(4, 0).A);

        // The darker patch lies within 0.1 by chroma distance, which is why the hard key above cleared it.
        Assert.True(ChromaDistance(key, new Rgba8(0, 120, 43, 255)) < 0.1);
        using Raster<Rgba8> everything = source.Derive(ops => ops.ChromaKey(key, 1, 0), Ct);
        Assert.All(everything.Row(0).ToArray(), p => Assert.Equal(0, p.A));
    }

    [Fact]
    public void ChromaKeyScalesExistingAlphaAndLeavesOpaqueTypesAlone()
    {
        Rgba8 key = new(0, 0, 255, 255);
        using Raster<Rgba8> source = Raster.Create(2, 1, new Rgba8(250, 250, 250, 128));
        source.Row(0)[0] = new Rgba8(0, 0, 255, 128);
        using Raster<Rgba8> keyed = source.Derive(ops => ops.ChromaKey(key, 0.05f, 0), Ct);
        Assert.Equal(0, keyed.GetPixel(0, 0).A);
        Assert.Equal(128, keyed.GetPixel(1, 0).A);

        // Inside a scope only the area is keyed and despilled.
        using Raster<Rgba8> scoped = Raster.Create(2, 1, new Rgba8(0, 0, 255, 255));
        scoped.Apply(ops => ops.Within(new Rect(1, 0, 1, 1), o => o.ChromaKey(key, 0.05f, 0).Despill(DespillColor.Blue)), Ct);
        Assert.Equal(new Rgba8(0, 0, 255, 255), scoped.GetPixel(0, 0));
        Assert.Equal(new Rgba8(0, 0, 0, 0), scoped.GetPixel(1, 0));

        using Raster<Rgb8> opaque = Raster.Create(2, 1, new Rgb8(0, 0, 255));
        using Raster<Rgb8> same = opaque.Derive(ops => ops.ChromaKey(key, 0.5f), Ct);
        Assert.Equal(opaque.Row(0).ToArray(), same.Row(0).ToArray());
    }

    [Fact]
    public void DespillHoldsTheScreenChannelToTheOtherTwo()
    {
        using Raster<Rgba8> source = Raster.Create<Rgba8>(4, 1);
        source.Row(0)[0] = new Rgba8(100, 200, 80, 255);
        source.Row(0)[1] = new Rgba8(128, 128, 128, 255);
        source.Row(0)[2] = new Rgba8(200, 100, 150, 255);
        source.Row(0)[3] = new Rgba8(80, 100, 220, 40);

        // Green is limited to the average of red and blue (90) with mix 0, their larger (100) with mix 1, and halfway by default.
        Assert.Equal(new Rgba8(100, 90, 80, 255), Despilled(DespillColor.Green, 0, 0, 0));
        Assert.Equal(new Rgba8(100, 100, 80, 255), Despilled(DespillColor.Green, 1, 0, 0));
        using Raster<Rgba8> standard = source.Derive(ops => ops.Despill(), Ct);
        Assert.Equal(new Rgba8(100, 95, 80, 255), standard.GetPixel(0, 0));
        Assert.Equal(source.GetPixel(1, 0), standard.GetPixel(1, 0));
        Assert.Equal(source.GetPixel(2, 0), standard.GetPixel(2, 0));
        Assert.Equal(source.GetPixel(3, 0), standard.GetPixel(3, 0));

        // Expand lowers the limit further; a blue screen limits blue to red and green, and alpha stays as it was.
        Assert.Equal(new Rgba8(100, 50, 80, 255), Despilled(DespillColor.Green, 1, 0.5f, 0));
        Assert.Equal(new Rgba8(80, 100, 95, 40), Despilled(DespillColor.Blue, 0.5f, 0, 3));

        Rgba8 Despilled(DespillColor screen, float mix, float expand, int x)
        {
            using Raster<Rgba8> result = source.Derive(ops => ops.Despill(screen, mix, expand), Ct);
            return result.GetPixel(x, 0);
        }
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
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Apply(ops => ops.ChromaKey(Rgba8.Lime, 1.5f), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Apply(ops => ops.ChromaKey(Rgba8.Lime, 0.1f, float.NaN), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Apply(ops => ops.Despill((DespillColor)7), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Apply(ops => ops.Despill(DespillColor.Green, -0.1f), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Apply(ops => ops.Despill(DespillColor.Green, 0.5f, 2), Ct));
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

    /// <summary>The chroma distance the test expects, from Rec. 709 written out here, as a share of the green-magenta distance.</summary>
    private static double ChromaDistance(Rgba8 a, Rgba8 b)
    {
        const double kr = 0.2126, kb = 0.0722, kg = 1 - kr - kb;
        static (double Cb, double Cr) Chroma(Rgba8 p)
        {
            double r = p.R / 255.0, g = p.G / 255.0, b = p.B / 255.0, y = (kr * r) + (kg * g) + (kb * b);
            return ((b - y) / (2 * (1 - kb)), (r - y) / (2 * (1 - kr)));
        }

        (double cb1, double cr1) = Chroma(a);
        (double cb2, double cr2) = Chroma(b);
        double max = 2 * Math.Sqrt(Math.Pow(kg / (2 * (1 - kb)), 2) + Math.Pow(kg / (2 * (1 - kr)), 2));
        return Math.Sqrt(Math.Pow(cb1 - cb2, 2) + Math.Pow(cr1 - cr2, 2)) / max;
    }
}
