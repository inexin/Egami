using System.Numerics;

using Egami.Drawing.Rasterization;
using Egami.Drawing.Tests.Geometry;

namespace Egami.Drawing.Tests.Rasterization;

public class CoverageTests
{
    /// <summary>Axis-aligned rectangles with fractional edges: every pixel gets its covered area to the nearest 1/255.</summary>
    [Theory]
    [InlineData(2.25f, 3.5f, 10.4f, 6.1f)]
    [InlineData(0, 0, 16, 16)]
    [InlineData(5.9f, 5.9f, 0.2f, 0.2f)]
    [InlineData(-3.3f, 7.7f, 9, 1.4f)]
    public void RectanglesGetTheirExactArea(float x, float y, float w, float h)
    {
        byte[,] grid = Rasterize(Path.Rect(new RectF(x, y, w, h)), 24, 24);
        Vector2[] polygon = [new(x, y), new(x + w, y), new(x + w, y + h), new(x, y + h)];
        AssertMatchesPolygonArea(grid, polygon, 1);
    }

    /// <summary>
    /// Polygons with 45-degree and other slanted edges, and generated stars: within 1/255 of the exact area per pixel of the polygon
    /// as the 24.8 fixed point holds it (vertices on a 1/256-pixel grid; where an edge crosses a cell side is rounded to that grid
    /// too, which adds up to a quarter level), and within 1.5/255 of the polygon itself.
    /// </summary>
    [Fact]
    public void PolygonsGetTheirExactArea()
    {
        Vector2[] diamond = [new(10.3f, 1.2f), new(19.1f, 10), new(10.3f, 18.8f), new(1.5f, 10)];
        AssertMatchesPolygonArea(Rasterize(Path.Polygon(diamond), 24, 24), Snap(diamond), 1.25);
        Random random = new(11);
        for (int i = 0; i < 20; i++)
        {
            Vector2 center = new(12 + (float)random.NextDouble(), 12 + (float)random.NextDouble());
            Path star = Path.Star(center, 4 + (float)(random.NextDouble() * 8), 1 + (float)(random.NextDouble() * 3), 3 + random.Next(9), (float)(random.NextDouble() * 360));
            Vector2[] corners = star.Points.ToArray();
            byte[,] grid = Rasterize(star, 26, 26);
            AssertMatchesPolygonArea(grid, Snap(corners), 1.25);
            AssertMatchesPolygonArea(grid, corners, 1.5);
        }
    }

    /// <summary>Circles: each pixel within the area of the 0.1-pixel flattening, and the total within 0.1 pixel per unit of perimeter.</summary>
    [Theory]
    [InlineData(10, 10, 6)]
    [InlineData(12.3f, 11.7f, 9.4f)]
    [InlineData(5.5f, 5.5f, 0.7f)]
    public void CirclesCoverTheirArea(float cx, float cy, float r)
    {
        byte[,] grid = Rasterize(Path.Circle(new Vector2(cx, cy), r), 24, 24);
        double total = 0;
        foreach (byte b in grid)
        {
            total += b / 255.0;
        }

        Assert.InRange(total, (Math.PI * r * r) - (0.1 * 2 * Math.PI * r) - 0.5, (Math.PI * r * r) + 0.5);
        Vector2[] fine = PathTests.Flatten(Path.Circle(new Vector2(cx, cy), r), 4096);
        AssertMatchesPolygonArea(grid, fine, 26);
    }

    [Fact]
    public void AbuttingShapesSumToFullCoverage()
    {
        // Two triangles sharing a slanted diagonal, and a rectangle split at a fractional x.
        Vector2 a = new(1.3f, 2.1f), b = new(20.7f, 3.9f), c = new(18.2f, 21.4f), d = new(2.6f, 19.8f);
        AssertSumsToShape(Path.Polygon([a, b, c]), Path.Polygon([a, c, d]), Path.Polygon([a, b, c, d]));
        AssertSumsToShape(Path.Rect(new RectF(2, 2, 7.37f, 15)), Path.Rect(new RectF(9.37f, 2, 6.63f, 15)), Path.Rect(new RectF(2, 2, 14, 15)));
    }

    [Fact]
    public void FillRulesDifferOnlyWhereContoursOverlap()
    {
        Path both = new PathBuilder().AddRect(new RectF(2, 2, 16, 16)).AddRect(new RectF(6, 6, 8, 8)).ToPath();
        byte[,] nonZero = Rasterize(both, 20, 20);
        byte[,] evenOdd = Rasterize(both.WithFillRule(FillRule.EvenOdd), 20, 20);
        Assert.Equal(255, nonZero[10, 10]);
        Assert.Equal(0, evenOdd[10, 10]);
        Assert.Equal(255, evenOdd[3, 3]);

        // A self-intersecting star with overlapping winding 2 at the center.
        Vector2[] pentagram = new Vector2[5];
        for (int i = 0; i < 5; i++)
        {
            double angle = (4 * Math.PI * i / 5) - (Math.PI / 2);
            pentagram[i] = new Vector2(10 + (float)(9 * Math.Cos(angle)), 10 + (float)(9 * Math.Sin(angle)));
        }

        Assert.Equal(255, Rasterize(Path.Polygon(pentagram), 20, 20)[10, 10]);
        Assert.Equal(0, Rasterize(Path.Polygon(pentagram).WithFillRule(FillRule.EvenOdd), 20, 20)[10, 10]);
    }

    [Fact]
    public void BandsGiveIdenticalCoverage()
    {
        Path path = new PathBuilder()
            .AddCircle(new Vector2(60, 70), 50)
            .AddPath(Path.Star(new Vector2(70, 60), 55, 20, 9, 7))
            .CubicTo(new Vector2(0, 130), new Vector2(130, 0), new Vector2(10, 10))
            .Close()
            .ToPath(FillRule.EvenOdd);
        byte[,] one = Rasterize(path, 140, 140, bands: 1);
        foreach (int bands in new[] { 2, 3, 7, 16, 140 })
        {
            Assert.Equal(one, Rasterize(path, 140, 140, bands: bands));
        }
    }

    [Fact]
    public void ClipLimitsTheRowsAndColumnsDrawn()
    {
        Path path = Path.Circle(new Vector2(10, 10), 8);
        byte[,] full = Rasterize(path, 20, 20);
        byte[,] clipped = Rasterize(path, 20, 20, clip: new Rect(5, 3, 7, 9));
        for (int y = 0; y < 20; y++)
        {
            for (int x = 0; x < 20; x++)
            {
                bool inside = x >= 5 && x < 12 && y >= 3 && y < 12;
                Assert.Equal(inside ? full[y, x] : 0, clipped[y, x]);
            }
        }
    }

    [Fact]
    public void HugeCoordinatesAreClippedSafely()
    {
        Path huge = Path.Polygon([new(-1e30f, -1e30f), new(1e30f, -1e29f), new(5, 1e30f)]);
        byte[,] grid = Rasterize(huge, 16, 16);
        Assert.Equal(255, grid[8, 8]);
        Path thin = Path.Polygon([new(-9e6f, 4), new(9e6f, 4.5f), new(9e6f, 5), new(-9e6f, 4.5f)]);
        byte[,] line = Rasterize(thin, 16, 16);
        Assert.InRange(line[4, 8], 120, 135);
    }

    [Fact]
    public void AliasedFillsSamplePixelCenters()
    {
        byte[,] grid = Rasterize(Path.Rect(new RectF(2.4f, 2.6f, 5.2f, 3)), 12, 12, antialias: false);
        for (int y = 0; y < 12; y++)
        {
            for (int x = 0; x < 12; x++)
            {
                bool inside = x + 0.5f >= 2.4f && x + 0.5f < 7.6f && y + 0.5f >= 2.6f && y + 0.5f < 5.6f;
                Assert.Equal(inside ? 255 : 0, grid[y, x]);
            }
        }

        // Abutting aliased shapes never both cover a pixel, and cover the whole together.
        Vector2 a = new(1.3f, 2.1f), b = new(20.7f, 3.9f), c = new(18.2f, 21.4f), d = new(2.6f, 19.8f);
        byte[,] first = Rasterize(Path.Polygon([a, b, c]), 24, 24, antialias: false);
        byte[,] second = Rasterize(Path.Polygon([a, c, d]), 24, 24, antialias: false);
        byte[,] whole = Rasterize(Path.Polygon([a, b, c, d]), 24, 24, antialias: false);
        for (int y = 0; y < 24; y++)
        {
            for (int x = 0; x < 24; x++)
            {
                Assert.True(first[y, x] + second[y, x] <= 255);
                Assert.Equal(whole[y, x], first[y, x] + second[y, x]);
            }
        }
    }

    internal static byte[,] Rasterize(Path path, int width, int height, int bands = 1, Rect? clip = null, bool antialias = true)
    {
        EdgeList edges = new();
        edges.Clear();
        Flattener.Flatten(path, Matrix3x2.Identity, 0.1f, edges);
        using CoverageRasterizer rasterizer = new(EgamiContext.Default.Memory, 8) { ForcedBands = bands };
        GridSink sink = new(width, height);
        rasterizer.Fill(edges, clip ?? new Rect(0, 0, width, height), path.FillRule, antialias, sink, 4);
        return sink.Grid;
    }

    private static void AssertSumsToShape(Path first, Path second, Path whole)
    {
        byte[,] a = Rasterize(first, 24, 24), b = Rasterize(second, 24, 24), w = Rasterize(whole, 24, 24);
        for (int y = 0; y < 24; y++)
        {
            for (int x = 0; x < 24; x++)
            {
                Assert.InRange(a[y, x] + b[y, x] - w[y, x], -1, 1);
            }
        }
    }

    private static Vector2[] Snap(Vector2[] polygon) => [.. polygon.Select(p => new Vector2(MathF.Round(p.X * 256) / 256, MathF.Round(p.Y * 256) / 256))];

    private static void AssertMatchesPolygonArea(byte[,] grid, Vector2[] polygon, double levels)
    {
        for (int y = 0; y < grid.GetLength(0); y++)
        {
            for (int x = 0; x < grid.GetLength(1); x++)
            {
                double area = PixelArea(polygon, x, y);
                Assert.True(Math.Abs((area * 255) - grid[y, x]) <= levels, $"pixel ({x}, {y}): area {area * 255:F2}, coverage {grid[y, x]}");
            }
        }
    }

    /// <summary>The area of a simple polygon inside pixel (x, y), by clipping it to the pixel square (Sutherland–Hodgman).</summary>
    private static double PixelArea(Vector2[] polygon, int x, int y)
    {
        List<(double X, double Y)> points = [.. polygon.Select(p => ((double)p.X, (double)p.Y))];
        points = Clip(points, p => p.X - x, (a, b) => Lerp(a, b, (x - a.X) / (b.X - a.X)));
        points = Clip(points, p => x + 1 - p.X, (a, b) => Lerp(a, b, (x + 1 - a.X) / (b.X - a.X)));
        points = Clip(points, p => p.Y - y, (a, b) => Lerp(a, b, (y - a.Y) / (b.Y - a.Y)));
        points = Clip(points, p => y + 1 - p.Y, (a, b) => Lerp(a, b, (y + 1 - a.Y) / (b.Y - a.Y)));
        double area = 0;
        for (int i = 0; i < points.Count; i++)
        {
            (double X, double Y) a = points[i], b = points[(i + 1) % points.Count];
            area += (a.X * b.Y) - (b.X * a.Y);
        }

        return Math.Abs(area) / 2;

        static (double X, double Y) Lerp((double X, double Y) a, (double X, double Y) b, double t) => (a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t));
    }

    private static List<(double X, double Y)> Clip(List<(double X, double Y)> points, Func<(double X, double Y), double> inside, Func<(double X, double Y), (double X, double Y), (double X, double Y)> cut)
    {
        List<(double X, double Y)> result = [];
        for (int i = 0; i < points.Count; i++)
        {
            (double X, double Y) a = points[i], b = points[(i + 1) % points.Count];
            bool ina = inside(a) >= 0, inb = inside(b) >= 0;
            if (ina)
            {
                result.Add(a);
            }

            if (ina != inb)
            {
                result.Add(cut(a, b));
            }
        }

        return result;
    }

    /// <summary>Writes coverage into a grid and checks no pixel is written twice.</summary>
    internal sealed class GridSink(int width, int height) : ISpanSink
    {
        public byte[,] Grid { get; } = new byte[height, width];
        public bool FillsDirectly => true;


        private readonly bool[,] _written = new bool[height, width];

        public void Blend(int y, int x, Span<byte> coverage)
        {
            for (int i = 0; i < coverage.Length; i++)
            {
                Set(y, x + i, coverage[i]);
            }
        }

        public void Fill(int y, int x, int length, byte coverage, Span<byte> scratch)
        {
            Assert.True(scratch.Length >= length);
            for (int i = 0; i < length; i++)
            {
                Set(y, x + i, coverage);
            }
        }

        private void Set(int y, int x, byte value)
        {
            lock (_written)
            {
                Assert.False(_written[y, x], $"pixel ({x}, {y}) written twice");
                _written[y, x] = true;
                Grid[y, x] = value;
            }
        }
    }
}
