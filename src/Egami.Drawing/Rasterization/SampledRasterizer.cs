namespace Egami.Drawing.Rasterization;

/// <summary>
/// Fills with GDI+'s antialiasing (<see cref="Antialiasing.GdiPlus"/>), as measured from its ramps in 2.7.6: each pixel has
/// 8 × 4 point samples at (x + i/8, y + j/4) from its top-left corner, and its coverage is the share of samples inside, rounded to
/// 1/255. A sample row keeps the edges whose half-open y range holds it; a sample is inside when its x lies in a covered span's
/// half-open range [left, right), so shapes that share an edge never both count a sample. Every row is computed from the edges
/// alone, so the result is the same in any band and at any core count.
/// </summary>
internal sealed class SampledRasterizer
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

    internal SampledRasterizer(EdgeList edges, Rect area, bool evenOdd)
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
