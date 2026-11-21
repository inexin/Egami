using Egami.Drawing.Tracing;
using Egami.Pixels;

namespace Egami.Drawing.Tests.Tracing;

/// <summary>Each stage of the black-and-white tracer on bitmaps whose outlines are known.</summary>
public class TracingStageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void ThresholdSplitsByIntensityOverWhite()
    {
        using Raster<Rgba8> raster = Raster.Create(4, 1, Rgba8.White);
        raster.SetPixel(0, 0, Rgba8.Black);
        raster.SetPixel(1, 0, new Rgba8(0, 0, 0, 0));      // transparent black is paper
        raster.SetPixel(2, 0, new Rgba8(0, 0, 0, 200));    // mostly opaque black is ink
        raster.SetPixel(3, 0, new Rgba8(100, 100, 100, 255));
        InkMap ink = InkMap.FromRaster(raster, 0.5f, Ct);
        Assert.Equal([1, 0, 1, 1], ink.Pixels.ToArray());

        InkMap dark = InkMap.FromRaster(raster, 0.25f, Ct);
        Assert.Equal([1, 0, 1, 0], dark.Pixels.ToArray());
    }

    [Fact]
    public void LabelsInkEightConnectedAndPaperFourConnected()
    {
        // Ink on a diagonal is one region; the paper on either side of it is two.
        InkMap ink = Map(
            "#..",
            ".#.",
            "..#");
        int[] labels = new int[9];
        Assert.Equal(3, ink.Label(labels, Ct));
        Assert.Equal(labels[0], labels[4]);
        Assert.Equal(labels[4], labels[8]);
        Assert.NotEqual(labels[1], labels[3]);
    }

    [Fact]
    public void NoiseMergesSmallRegionsSmallestFirst()
    {
        // A speck inside a small hole: the speck merges into the hole first, and then the grown hole (25 pixels) fills.
        string[] rows =
        [
            "#########",
            "#########",
            "##.....##",
            "##.....##",
            "##..#..##",
            "##.....##",
            "##.....##",
            "#########",
            "#########",
        ];
        InkMap ink = Map(rows);
        ink.RemoveNoise(2, Ct);
        Assert.Equal(56, Count(ink.Pixels, 1));
        Assert.False(ink[4, 4]);

        InkMap filled = Map(rows);
        filled.RemoveNoise(26, Ct);
        Assert.Equal(81, Count(filled.Pixels, 1));
    }

    [Fact]
    public void NoiseKeepsRegionsWithoutNeighbours()
    {
        InkMap one = Map("#");
        one.RemoveNoise(int.MaxValue, Ct);
        Assert.True(one[0, 0]);

        InkMap none = Map("...");
        none.RemoveNoise(int.MaxValue, Ct);
        Assert.Equal(0, Count(none.Pixels, 1));
    }

    [Fact]
    public void NoiseZeroOrOneKeepsEverything()
    {
        InkMap ink = Map("#.#", "...", "#.#");
        ink.RemoveNoise(0, Ct);
        ink.RemoveNoise(1, Ct);
        Assert.Equal(4, Count(ink.Pixels, 1));
        ink.RemoveNoise(2, Ct);
        Assert.Equal(0, Count(ink.Pixels, 1));
    }

    [Fact]
    public void OnePixelIsOneClockwiseLoop()
    {
        List<PixelLoop> loops = BoundaryTracer.Trace(Map("#"), Ct);
        PixelLoop loop = Assert.Single(loops);
        Assert.Equal([0, 1, 1, 0], loop.X);
        Assert.Equal([0, 0, 1, 1], loop.Y);
        Assert.Equal(1, loop.Area);
        Assert.False(loop.IsHole);
    }

    [Fact]
    public void HoleRunsCounterClockwise()
    {
        List<PixelLoop> loops = BoundaryTracer.Trace(Map("###", "#.#", "###"), Ct);
        Assert.Equal(2, loops.Count);
        Assert.Equal(9, loops[0].Area);
        Assert.Equal(-1, loops[1].Area);
        Assert.True(loops[1].IsHole);
    }

    [Fact]
    public void DiagonalInkStaysInOneLoop()
    {
        List<PixelLoop> loops = BoundaryTracer.Trace(Map("#.", ".#"), Ct);
        PixelLoop loop = Assert.Single(loops);
        Assert.Equal(2, loop.Area);
        Assert.Equal(8, loop.Count);

        // The paper of a checkerboard is enclosed at its diagonals but stays outside: one loop for the ink.
        Assert.Single(BoundaryTracer.Trace(Map("#.#", ".#.", "#.#"), Ct));
    }

    [Fact]
    public void EmptyAndFullImages()
    {
        Assert.Empty(BoundaryTracer.Trace(Map("...", "..."), Ct));
        PixelLoop full = Assert.Single(BoundaryTracer.Trace(Map("###", "###"), Ct));
        Assert.Equal(6, full.Area);
    }

    [Fact]
    public void EveryPixelEdgeIsUsedOnce()
    {
        Random random = new(7);
        string[] rows = new string[24];
        for (int y = 0; y < rows.Length; y++)
        {
            rows[y] = new string([.. Enumerable.Range(0, 31).Select(_ => random.Next(3) == 0 ? '#' : '.')]);
        }

        InkMap ink = Map(rows);
        List<PixelLoop> loops = BoundaryTracer.Trace(ink, Ct);
        long edges = 0;
        for (int y = 0; y <= ink.Height; y++)
        {
            for (int x = 0; x <= ink.Width; x++)
            {
                edges += x < ink.Width && ink[x, y] != ink[x, y - 1] ? 1 : 0;
                edges += y < ink.Height && ink[x, y] != ink[x - 1, y] ? 1 : 0;
            }
        }

        Assert.Equal(edges, loops.Sum(l => (long)l.Count));
        Assert.Equal(Count(ink.Pixels, 1), loops.Sum(l => l.Area));
    }

    [Fact]
    public void RectangleFitsFourCorners()
    {
        string[] rows = [.. Enumerable.Range(0, 8).Select(y => y is 0 or 7 ? new string('.', 14) : "." + new string('#', 12) + ".")];
        PixelLoop loop = Assert.Single(BoundaryTracer.Trace(Map(rows), Ct));
        (double X, double Y)[] polygon = PolygonFitter.Fit(loop);
        Assert.Equal(4, polygon.Length);
        double[] xs = [.. polygon.Select(p => p.X).Order()], ys = [.. polygon.Select(p => p.Y).Order()];
        Assert.Equal([1, 1, 13, 13], xs.Select(v => Math.Round(v, 6)));
        Assert.Equal([1, 1, 7, 7], ys.Select(v => Math.Round(v, 6)));
    }

    [Fact]
    public void StaircaseFitsFewSides()
    {
        // A right triangle with a staircase hypotenuse of slope 1/2: three sides, not one per step.
        string[] rows = [.. Enumerable.Range(0, 10).Select(y => new string('#', 2 * (y + 1)).PadRight(20, '.'))];
        PixelLoop loop = Assert.Single(BoundaryTracer.Trace(Map(rows), Ct));
        (double X, double Y)[] polygon = PolygonFitter.Fit(loop);
        Assert.InRange(polygon.Length, 3, 5);
    }

    [Fact]
    public void PossibleSegmentsReachAtLeastOneStep()
    {
        PixelLoop loop = Assert.Single(BoundaryTracer.Trace(Map("#"), Ct));
        int[] reach = PolygonFitter.PossibleSegments(loop);
        Assert.All(Enumerable.Range(0, 4), i => Assert.Equal(i + 1, reach[i]));
    }

    [Fact]
    public void CornerStaysInsideItsSquare()
    {
        // Lines far away pull the corner to the edge of its square, never past it.
        (double x, double y) = PolygonFitter.Minimize(1, 0, 3, 0, 1, -3);
        Assert.Equal(0.5, x, 9);
        Assert.Equal(-0.5, y, 9);
        (x, y) = PolygonFitter.Minimize(1, 0, 0.25, 0, 1, 0.1);
        Assert.Equal(0.25, x, 9);
        Assert.Equal(0.1, y, 9);

        // Parallel lines: the point between them nearest the corner.
        (x, y) = PolygonFitter.Minimize(0, 1, 0.2, 0, 1, 0.4);
        Assert.Equal(0, x, 9);
        Assert.Equal(0.3, y, 9);
    }

    [Fact]
    public void AlphaGrowsWithSharpnessAndSideLength()
    {
        // A right angle with long sides is a corner at the default threshold of 1; with short sides it is rounded.
        Assert.True(CurveFitter.Alpha((0, 10), (0, 0), (10, 0)) > 1);
        Assert.True(CurveFitter.Alpha((0, 1), (0, 0), (1, 0)) < 1);

        // A shallow bend is rounded; a straight run gives 0.
        Assert.True(CurveFitter.Alpha((-10, 1), (0, 0), (10, 1)) < 1);
        Assert.Equal(0, CurveFitter.Alpha((-10, 0), (0, 0), (10, 0)));
    }

    internal static InkMap Map(params string[] rows)
    {
        InkMap map = new(rows[0].Length, rows.Length);
        for (int y = 0; y < rows.Length; y++)
        {
            for (int x = 0; x < rows[y].Length; x++)
            {
                map.Pixels[(y * map.Width) + x] = rows[y][x] == '#' ? (byte)1 : (byte)0;
            }
        }

        return map;
    }

    private static int Count(Span<byte> pixels, byte value)
    {
        int count = 0;
        foreach (byte b in pixels)
        {
            count += b == value ? 1 : 0;
        }

        return count;
    }
}
