using System.Numerics;

using Egami.Drawing.Rasterization;
using Egami.Drawing.Tests.Geometry;

namespace Egami.Drawing.Tests.Rasterization;

/// <summary>
/// The faster GDI+ sampler (2.7.6 task 27) gives the same coverage as the stage 2 one, for stars, circles, strokes, overlapping
/// even-odd shapes and shapes past the clip, in one band or many.
/// </summary>
public class SampledRasterizerTests
{
    public static TheoryData<int> Seeds => [1, 2, 3, 4, 5, 6, 7, 8];

    [Theory]
    [MemberData(nameof(Seeds))]
    public void EqualsTheStage2Sampler(int seed)
    {
        Random random = new(seed);
        for (int k = 0; k < 40; k++)
        {
            Path path = RandomPath(random);
            int width = 40 + random.Next(80), height = 30 + random.Next(80);
            int cx = random.Next(10), cy = random.Next(10);
            Rect clip = random.Next(3) == 0 ? new Rect(cx, cy, width - cx - random.Next(10), height - cy - random.Next(10)) : new Rect(0, 0, width, height);
            byte[,] reference = Reference(path, width, height, clip);
            foreach (int bands in new[] { 1, 3, 8 })
            {
                byte[,] fast = Fast(path, width, height, clip, bands);
                Assert.True(Same(reference, fast), $"seed {seed}, shape {k}, {bands} bands");
            }
        }
    }

    private static Path RandomPath(Random random)
    {
        float R(float lo, float hi) => lo + ((float)random.NextDouble() * (hi - lo));
        switch (random.Next(5))
        {
            case 0:
                return Path.Star(new Vector2(R(10, 60), R(10, 60)), R(4, 40), R(1, 12), 3 + random.Next(9), R(0, 360));
            case 1:
                return Path.Circle(new Vector2(R(5, 70), R(5, 70)), R(0.3f, 35));
            case 2:
                {
                    var stroke = new Stroke(R(0.6f, 6)) { Join = (LineJoin)random.Next(3), Cap = (LineCap)random.Next(3) };
                    Path line = Path.Polygon([new(R(0, 80), R(0, 80)), new(R(0, 80), R(0, 80)), new(R(0, 80), R(0, 80))], close: false);
                    return Egami.Drawing.Geometry.Stroker.Outline(line, stroke, 0.1f);
                }

            case 3:
                {
                    PathBuilder b = new();
                    b.AddCircle(new Vector2(R(20, 50), R(20, 50)), R(10, 25));
                    b.AddRect(new RectF(R(5, 40), R(5, 40), R(5, 40), R(5, 40)));
                    b.AddCircle(new Vector2(R(20, 50), R(20, 50)), R(3, 15));
                    return b.ToPath(FillRule.EvenOdd);
                }

            default:
                return Path.Rect(new RectF(R(-5, 60), R(-5, 60), R(0.1f, 50), R(0.1f, 50)));
        }
    }

    private static byte[,] Fast(Path path, int width, int height, Rect clip, int bands)
    {
        EdgeList edges = Edges(path);
        using CoverageRasterizer rasterizer = new(EgamiContext.Default.Memory, 8) { ForcedBands = bands, GdiPlus = true };
        CoverageTests.GridSink sink = new(width, height);
        rasterizer.Fill(edges, clip, path.FillRule, antialias: true, sink, 4);
        return sink.Grid;
    }

    private static byte[,] Reference(Path path, int width, int height, Rect clip)
    {
        EdgeList edges = Edges(path);
        CoverageTests.GridSink sink = new(width, height);
        if (edges.Count == 0)
        {
            return sink.Grid;
        }

        double left = Math.Max(clip.X, Math.Floor(edges.MinX)), top = Math.Max(clip.Y, Math.Floor(edges.MinY));
        double right = Math.Min(clip.Right, Math.Ceiling(edges.MaxX)), bottom = Math.Min(clip.Bottom, Math.Ceiling(edges.MaxY));
        if (!(right > left && bottom > top))
        {
            return sink.Grid;
        }

        Rect area = new((int)left, (int)top, (int)(right - left), (int)(bottom - top));
        using WorkspacePool pool = new(EgamiContext.Default.Memory);
        Workspace workspace = pool.Rent();
        new ReferenceSampledRasterizer(edges, area, path.FillRule == FillRule.EvenOdd).Band(0, area.Height, workspace, sink);
        pool.Return(workspace);
        return sink.Grid;
    }

    private static EdgeList Edges(Path path)
    {
        EdgeList edges = new();
        Flattener.Flatten(path, Matrix3x2.Identity, 0.1f, edges);
        return edges;
    }

    private static bool Same(byte[,] a, byte[,] b)
    {
        for (int y = 0; y < a.GetLength(0); y++)
        {
            for (int x = 0; x < a.GetLength(1); x++)
            {
                if (a[y, x] != b[y, x])
                {
                    return false;
                }
            }
        }

        return true;
    }
}
/// <summary>The 2.7.6 stage 2 sampler as it shipped, kept as the reference the faster one must equal.</summary>
internal sealed class ReferenceSampledRasterizer
{
    /// <summary>Samples across a pixel.</summary>
    internal const int Columns = 8;

    /// <summary>Samples down a pixel.</summary>
    internal const int Rows = 4;

    private const int Samples = Columns * Rows;

    private readonly float[] _edges;
    private readonly int[] _byTop;
    private readonly Rect _area;
    private readonly bool _evenOdd;

    internal ReferenceSampledRasterizer(EdgeList edges, Rect area, bool evenOdd)
    {
        _edges = edges.Coordinates.ToArray();
        _area = area;
        _evenOdd = evenOdd;
        _byTop = new int[edges.Count];
        float[] tops = new float[edges.Count];
        for (int i = 0; i < _byTop.Length; i++)
        {
            _byTop[i] = i;
            tops[i] = MathF.Min(_edges[(4 * i) + 1], _edges[(4 * i) + 3]);
        }

        Array.Sort(tops, _byTop);
    }

    internal void Band(int start, int end, Workspace workspace, ISpanSink sink)
    {
        List<int> active = [];
        int nextEdge = 0;
        int width = _area.Width, columns = width * Columns;
        Span<int> starts = workspace.Samples.Get(columns + 1);
        Span<byte> coverage = workspace.Coverage.Get(width);
        starts.Clear();
        for (int row = start; row < end; row++)
        {
            int first = columns, last = 0;
            for (int j = 0; j < Rows; j++)
            {
                double y = _area.Y + row + ((double)j / Rows);

                // Edges whose top is at or above the sample row join; those whose bottom is at or above it leave.
                while (nextEdge < _byTop.Length && Top(_byTop[nextEdge]) <= y)
                {
                    active.Add(_byTop[nextEdge++]);
                }

                int kept = 0;
                for (int i = 0; i < active.Count; i++)
                {
                    if (Bottom(active[i]) > y)
                    {
                        active[kept++] = active[i];
                    }
                }

                active.RemoveRange(kept, active.Count - kept);
                Span<Crossing> crossings = workspace.Crossings.Get(Math.Max(1, active.Count));
                int n = 0;
                foreach (int e in active)
                {
                    double x0 = _edges[4 * e], y0 = _edges[(4 * e) + 1], x1 = _edges[(4 * e) + 2], y1 = _edges[(4 * e) + 3];
                    if (y < Math.Min(y0, y1))
                    {
                        continue;
                    }

                    crossings[n].X = (float)(x0 + ((y - y0) * (x1 - x0) / (y1 - y0)));
                    crossings[n].Winding = y1 > y0 ? 1 : -1;
                    n++;
                }

                crossings = crossings[..n];
                crossings.Sort(static (a, b) => a.X.CompareTo(b.X));
                int winding = 0;
                for (int i = 0; i < n - 1; i++)
                {
                    winding += crossings[i].Winding;
                    bool inside = _evenOdd ? (winding & 1) != 0 : winding != 0;
                    if (!inside)
                    {
                        continue;
                    }

                    // Samples whose x lies in [a, b), counted from the area's left edge.
                    int a = Column(crossings[i].X, columns), b = Column(crossings[i + 1].X, columns);
                    if (b <= a)
                    {
                        continue;
                    }

                    starts[a]++;
                    starts[b]--;
                    first = Math.Min(first, a);
                    last = Math.Max(last, b);
                }
            }

            if (last > first)
            {
                Emit(starts, first, last, coverage, _area.Y + row, sink);
            }
        }
    }

    /// <summary>The first sample column at or right of <paramref name="x"/>, clamped to the area.</summary>
    private int Column(float x, int columns)
    {
        double c = Math.Ceiling(((double)x - _area.X) * Columns);
        return (int)Math.Clamp(c, 0, columns);
    }

    // Sums the row's samples per pixel from the span starts and ends, and sends the runs with any coverage.
    private void Emit(Span<int> starts, int first, int last, Span<byte> coverage, int y, ISpanSink sink)
    {
        // Columns outside [first, last] hold no span ends, so the running count starts at 0 at the first pixel.
        int px0 = first / Columns, px1 = (last + Columns - 1) / Columns;
        int running = 0;
        int runStart = -1;
        for (int px = px0; px < px1; px++)
        {
            int count = 0;
            for (int c = px * Columns; c < (px + 1) * Columns; c++)
            {
                running += starts[c];
                count += running;
            }

            byte alpha = (byte)(((count * 255) + (Samples / 2)) / Samples);
            if (alpha == 0)
            {
                if (runStart >= 0)
                {
                    sink.Blend(y, _area.X + runStart, coverage[runStart..px]);
                    runStart = -1;
                }

                continue;
            }

            if (runStart < 0)
            {
                runStart = px;
            }

            coverage[px] = alpha;
        }

        if (runStart >= 0)
        {
            sink.Blend(y, _area.X + runStart, coverage[runStart..px1]);
        }

        starts[first..(last + 1)].Clear();
    }

    private float Top(int e) => MathF.Min(_edges[(4 * e) + 1], _edges[(4 * e) + 3]);

    private float Bottom(int e) => MathF.Max(_edges[(4 * e) + 1], _edges[(4 * e) + 3]);
}
