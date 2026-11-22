using Egami.Drawing.Tracing;
using Egami.Pixels;

namespace Egami.Drawing.Tests.Tracing;

/// <summary>Each stage of grayscale and color tracing, and the cubic fitter, on inputs whose answers are known.</summary>
public class ColorStageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void BlendsBetweenNeighboursAreFound()
    {
        // Black, a half blend, white across a row: the middle pixel is a blend, the ends are not.
        byte[] rgb = Rgb(3, 3, (x, _) => x == 0 ? 0 : x == 1 ? 128 : 255);
        Assert.True(LabelImage.IsBlend(rgb, 3, 3, 1, 1));

        // 128 is a shade nearer white: the pixel takes the white side's label.
        Assert.Equal((1 * 3) + 2, LabelImage.BlendSource(rgb, 3, 3, 1, 1));

        // A thin dark line between two white pixels is no blend: its neighbours are alike.
        byte[] line = Rgb(3, 3, (x, _) => x == 1 ? 0 : 255);
        Assert.False(LabelImage.IsBlend(line, 3, 3, 1, 1));
    }

    [Fact]
    public void PaletteLeavesOutEdgeBlends()
    {
        // Two flat colors with a column of blends between them: two colors come back, not three.
        byte[] rgb = Rgb(30, 10, (x, _) => x < 14 ? 30 : x == 14 ? 140 : 230);
        Rgba8[] palette = LabelImage.BuildPalette(rgb, 30, 10, 8, true, Ct);
        Assert.Equal(2, palette.Length);
        Assert.Contains(palette, c => c.R == 30);
        Assert.Contains(palette, c => c.R == 230);
    }

    [Fact]
    public void PaletteKeepsSmallColorsBesideANoisyLargeOne()
    {
        // A large mid gray wobbling by up to 12 levels, with a small dark and a small light square: three colors, the squares'
        // own among them, rather than the noise taking the palette entries.
        Random random = new(5);
        byte[] rgb = Rgb(60, 60, (x, y) => x is >= 5 and < 11 && y is >= 5 and < 11 ? 20 : x is >= 40 and < 46 && y is >= 40 and < 46 ? 240 : 128 + random.Next(-12, 13));
        Rgba8[] palette = LabelImage.BuildPalette(rgb, 60, 60, 3, true, Ct);
        Assert.Equal(3, palette.Length);
        Assert.Contains(palette, c => Math.Abs(c.R - 20) <= 2);
        Assert.Contains(palette, c => Math.Abs(c.R - 240) <= 2);
    }

    [Fact]
    public void SoftEdgesGetABandAndHardOnesDoNot()
    {
        // A blend three pixels wide between two grays gets a band of the middle gray; a one-pixel antialiased blend does not.
        using Raster<Rgba8> soft = Gray(30, 20, x => x < 14 ? 30 : x == 14 ? 80 : x == 15 ? 130 : x == 16 ? 180 : 230);
        TraceResult traced = soft.Trace(new TraceOptions { Mode = TraceMode.Grayscale });
        Assert.Equal(3, traced.Palette.Count);
        Assert.Contains(traced.Palette, c => Math.Abs(c.R - 130) <= 2);

        using Raster<Rgba8> hard = Gray(30, 20, x => x < 14 ? 30 : x == 14 ? 130 : 230);
        Assert.Equal(2, hard.Trace(new TraceOptions { Mode = TraceMode.Grayscale }).Palette.Count);

        // A fixed palette is kept as given.
        Rgba8[] fixedColors = [new Rgba8(30, 30, 30, 255), new Rgba8(230, 230, 230, 255)];
        Assert.Equal(2, soft.Trace(new TraceOptions { Mode = TraceMode.Grayscale, Palette = fixedColors }).Palette.Count);

        static Raster<Rgba8> Gray(int width, int height, Func<int, int> level)
        {
            Raster<Rgba8> raster = Raster.Create(width, height, Rgba8.White);
            for (int y = 0; y < height; y++)
            {
                Span<Rgba8> row = raster.Row(y);
                for (int x = 0; x < width; x++)
                {
                    byte v = (byte)level(x);
                    row[x] = new Rgba8(v, v, v, 255);
                }
            }

            return raster;
        }
    }

    [Fact]
    public void PaletteStopsAtTheColorsThereAre()
    {
        byte[] rgb = Rgb(40, 40, (x, y) => ((x / 10) + (y / 10)) % 3 * 100);
        Assert.Equal(3, LabelImage.BuildPalette(rgb, 40, 40, 16, false, Ct).Length);
        Assert.Equal(2, LabelImage.BuildPalette(rgb, 40, 40, 2, false, Ct).Length);
    }

    [Fact]
    public void RegionsAreFourConnected()
    {
        LabelImage image = Labels("ab", "ba");
        int[] regions = new int[4];
        Assert.Equal(4, image.LabelRegions(regions, Ct));
        Assert.Equal([0, 1, 2, 3], regions);
    }

    [Fact]
    public void NoiseJoinsTheNeighbourSharingMostEdges()
    {
        // The lone c touches a on three sides and b on one: it becomes a.
        LabelImage image = Labels(
            "aaab",
            "acbb",
            "aabb");
        image.RemoveNoise(2, Ct);
        Assert.Equal(0, image.Labels[5]);
    }

    [Fact]
    public void WalkKeepsDiagonalRegionsApart()
    {
        LabelImage image = Labels("ab", "ba");
        int[] regions = new int[4];
        image.LabelRegions(regions, Ct);
        RegionTracer tracer = new(image, regions);
        List<EdgeLoop> loops = tracer.Walk([0, -1, -1, 0], Ct);
        Assert.Equal(2, loops.Count);
        Assert.All(loops, l => Assert.Equal(4, l.Directions.Length));
        Assert.All(loops, l => Assert.Equal(1, l.Area));
        Assert.True(tracer.IsJunction(1, 1));
        Assert.False(tracer.IsJunction(0, 0));
    }

    [Fact]
    public void EdgesMoveByTheBlend()
    {
        // Black, then a pixel a quarter of the way to white, then white: the edge sits a quarter pixel into the blend pixel.
        LabelImage image = new(3, 1, [new Rgba8(0, 0, 0, 255), new Rgba8(255, 255, 255, 255)]);
        image.Labels[2] = 1;
        image.Labels[1] = 1;
        image.Rgb[3] = image.Rgb[4] = image.Rgb[5] = 191;
        image.Rgb[6] = image.Rgb[7] = image.Rgb[8] = 255;
        RegionTracer tracer = new(image, [0, 1, 1]);
        Assert.Equal(0.25, tracer.Offset(0, 0, 1, 0), 2);

        // Hard edges stay in the middle of the pixel edge.
        image.Rgb[3] = image.Rgb[4] = image.Rgb[5] = 255;
        Assert.Equal(0, tracer.Offset(0, 0, 1, 0));
    }

    [Fact]
    public void StaircaseReadsAsOnePointPerStep()
    {
        // A closed loop around a staircase of steps two wide: its hard steps give one point each, fewer than its edges.
        LabelImage image = Labels(
            "a.......",
            "aaa.....",
            "aaaaa...",
            "aaaaaaa.");
        int[] regions = new int[image.Labels.Length];
        image.LabelRegions(regions, Ct);
        RegionTracer tracer = new(image, regions);
        EdgeLoop loop = tracer.Walk([0, -1], Ct)[0];
        (int X, int Y)[] vertices = RegionTracer.Vertices(loop);
        Vec[] points = tracer.Read([.. vertices.Select(v => v.X)], [.. vertices.Select(v => v.Y)], loop.Directions, true);
        Assert.Contains(new Vec(2, 1), points);
        Assert.Contains(new Vec(4, 2), points);
        Assert.DoesNotContain(new Vec(1.5, 1), points);

        // Where two sides meet, the pixel corner itself.
        Assert.Contains(new Vec(0, 4), points);
    }

    [Fact]
    public void CubicFitterRestoresCornersAndSnapsLines()
    {
        // The middles of a 10 by 6 rectangle's pixel edges: four lines through its exact corners.
        List<Vec> points = [];
        for (int x = 0; x < 10; x++)
        {
            points.Add(new Vec(x + 0.5, 0));
        }

        for (int y = 0; y < 6; y++)
        {
            points.Add(new Vec(10, y + 0.5));
        }

        for (int x = 10; x > 0; x--)
        {
            points.Add(new Vec(x - 0.5, 6));
        }

        for (int y = 6; y > 0; y--)
        {
            points.Add(new Vec(0, y - 0.5));
        }

        FittedCurve curve = CubicFitter.FitClosed([.. points], new CurveSettings(0.5, Math.PI / 3, true, true));
        Assert.Equal(4, curve.Segments.Count);
        Assert.All(curve.Segments, s => Assert.False(s.IsCubic));
        Assert.Equal(
            [new Vec(0, 0), new Vec(10, 0), new Vec(0, 6), new Vec(10, 6)],
            curve.Segments.Select(s => s.End).Order(Comparer<Vec>.Create((a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X))));
    }

    [Fact]
    public void CubicFitterSnapsCirclesAndArcs()
    {
        Vec[] circle = [.. Enumerable.Range(0, 80).Select(i => new Vec(20 + (12 * Math.Cos(i * Math.PI / 40)), 20 + (12 * Math.Sin(i * Math.PI / 40))))];
        FittedCurve round = CubicFitter.FitClosed(circle, new CurveSettings(0.5, Math.PI / 3, true, true));
        Assert.Equal(4, round.Segments.Count);
        Vec mid = CubicFitter.Bezier(round.Start, round.Segments[0].C1, round.Segments[0].C2, round.Segments[0].End, 0.5);
        Assert.Equal(12, (mid - new Vec(20, 20)).Length, 1);

        // Without arcs the circle is still curves within tolerance.
        FittedCurve free = CubicFitter.FitClosed(circle, new CurveSettings(0.5, Math.PI / 3, true, false));
        Assert.All(free.Segments, s => Assert.True(s.IsCubic));

        // An open half circle between fixed ends is one arc of two quarter cubics.
        Vec[] half = [.. Enumerable.Range(0, 41).Select(i => new Vec(20 + (12 * Math.Cos(i * Math.PI / 40)), 20 + (12 * Math.Sin(i * Math.PI / 40))))];
        FittedCurve arc = CubicFitter.FitOpen(half, new CurveSettings(0.5, Math.PI / 3, true, true));
        Assert.Equal(2, arc.Segments.Count);
        Assert.Equal(half[0], arc.Start);
        Assert.Equal(half[^1], arc.End);
    }

    [Fact]
    public void SchneiderFitsWithinTolerance()
    {
        // A sine wave: no circle, no line, so cubics, each within the tolerance of the points.
        Vec[] wave = [.. Enumerable.Range(0, 120).Select(i => new Vec(i * 0.5, 8 * Math.Sin(i * 0.05)))];
        CurveSettings settings = new(0.2, Math.PI / 3, true, true);
        FittedCurve curve = CubicFitter.FitOpen(wave, settings);
        Assert.InRange(curve.Segments.Count, 1, 12);
        Vec start = curve.Start;
        double worst = 0;
        foreach (Vec point in wave)
        {
            double nearest = double.MaxValue;
            Vec from = start;
            foreach (CurveSegment s in curve.Segments)
            {
                for (int k = 0; k <= 1000; k++)
                {
                    Vec on = s.IsCubic ? CubicFitter.Bezier(from, s.C1, s.C2, s.End, k / 1000.0) : from + ((s.End - from) * (k / 1000.0));
                    nearest = Math.Min(nearest, (on - point).Length);
                }

                from = s.End;
            }

            worst = Math.Max(worst, nearest);
        }

        Assert.True(worst < 0.25, $"worst {worst}");
    }

    [Fact]
    public void CornersSettingMapsToAnAngle()
    {
        Assert.Equal(Math.PI, CurveSettings.From(TraceOptions.Default with { Corners = 0 }).CornerAngle, 9);
        Assert.Equal(Math.PI / 3, CurveSettings.From(TraceOptions.Default).CornerAngle, 9);
        Assert.Equal(0, CurveSettings.From(TraceOptions.Default with { Corners = 1 }).CornerAngle, 9);
        Assert.Equal(0.5, CurveSettings.From(TraceOptions.Default).Tolerance, 9);
    }

    [Fact]
    public void ReversedCurveRunsBack()
    {
        FittedCurve curve = new(new Vec(0, 0), [new CurveSegment(false, default, default, new Vec(1, 0)), new CurveSegment(true, new Vec(2, 0), new Vec(3, 1), new Vec(3, 2))], false);
        FittedCurve back = curve.Reversed();
        Assert.Equal(new Vec(3, 2), back.Start);
        Assert.Equal(new CurveSegment(true, new Vec(3, 1), new Vec(2, 0), new Vec(1, 0)), back.Segments[0]);
        Assert.Equal(new Vec(0, 0), back.End);
    }

    private static byte[] Rgb(int width, int height, Func<int, int, int> gray)
    {
        byte[] rgb = new byte[width * height * 3];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = 3 * ((y * width) + x);
                rgb[i] = rgb[i + 1] = rgb[i + 2] = (byte)gray(x, y);
            }
        }

        return rgb;
    }

    /// <summary>A label image from rows of letters: 'a' is label 0, 'b' 1 and so on; '.' is the last label.</summary>
    private static LabelImage Labels(params string[] rows)
    {
        Rgba8[] palette = [new(0, 0, 0, 255), new(255, 0, 0, 255), new(0, 0, 255, 255), new(255, 255, 255, 255)];
        LabelImage image = new(rows[0].Length, rows.Length, palette);
        for (int y = 0; y < rows.Length; y++)
        {
            for (int x = 0; x < rows[0].Length; x++)
            {
                int i = (y * rows[0].Length) + x;
                char c = rows[y][x];
                byte label = c == '.' ? (byte)3 : (byte)(c - 'a');
                image.Labels[i] = label;
                image.Rgb[3 * i] = palette[label].R;
                image.Rgb[(3 * i) + 1] = palette[label].G;
                image.Rgb[(3 * i) + 2] = palette[label].B;
            }
        }

        return image;
    }
}
