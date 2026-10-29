namespace Egami.Drawing.Rasterization;

/// <summary>
/// Fills with GDI+'s antialiasing (<see cref="Antialiasing.GdiPlus"/>), as measured from its ramps in 2.7.6: each pixel has
/// 8 × 4 point samples at (x + i/8, y + j/4) from its top-left corner, and its coverage is the share of samples inside, rounded to
/// 1/255. A sample row keeps the edges whose half-open y range holds it; a sample is inside when its x lies in a covered span's
/// half-open range [left, right), so shapes that share an edge never both count a sample. Every row is computed from the edges
/// alone, so the result is the same in any band and at any core count.
/// <para>
/// One instance serves a canvas's fills: <see cref="Prepare"/> sorts a fill's edges into arrays kept between fills, and each band
/// keeps its active edges and per-pixel counts in its workspace, so a warm fill allocates nothing. A span adds whole pixels through
/// a running count and only its two end pixels sample by sample.
/// </para>
/// </summary>
internal sealed class SampledRasterizer
{
    /// <summary>Samples across a pixel.</summary>
    internal const int Columns = 8;

    /// <summary>Samples down a pixel.</summary>
    internal const int Rows = 4;

    private const int Samples = Columns * Rows;

    /// <summary>Constant runs at least this long go to <see cref="ISpanSink.Fill"/>, as the exact rasterizer sends them.</summary>
    private const int FillRun = 16;

    /// <summary>Coverage for each count of samples inside, 0 to 32: n · 255 / 32, rounded half up.</summary>
    private static readonly byte[] Alphas = BuildAlphas();

    private float[] _edges = [];
    private float[] _tops = [];
    private float[] _bottoms = [];
    private int[] _byTop = [];
    private int _count;
    private Rect _area;
    private bool _evenOdd;

    /// <summary>Takes a fill's edges, sorted by their tops, for the bands that follow.</summary>
    internal void Prepare(EdgeList edges, Rect area, bool evenOdd)
    {
        _area = area;
        _evenOdd = evenOdd;
        _count = edges.Count;
        ReadOnlySpan<float> c = edges.Coordinates;
        if (_edges.Length < c.Length)
        {
            _edges = new float[c.Length];
        }

        if (_byTop.Length < _count)
        {
            _byTop = new int[_count];
            _tops = new float[_count];
            _bottoms = new float[_count];
        }

        c.CopyTo(_edges);
        for (int i = 0; i < _count; i++)
        {
            _byTop[i] = i;
            _tops[i] = MathF.Min(_edges[(4 * i) + 1], _edges[(4 * i) + 3]);
        }

        Array.Sort(_tops, _byTop, 0, _count);
        for (int i = 0; i < _count; i++)
        {
            _bottoms[i] = MathF.Max(_edges[(4 * i) + 1], _edges[(4 * i) + 3]);
        }
    }

    internal void Band(int start, int end, Workspace workspace, ISpanSink sink)
    {
        int width = _area.Width, columns = width * Columns;

        // The band's scratch in one buffer: the active edges; per pixel the change in its count of fully covered sample rows (one
        // past the last pixel too); and the samples of the pixels where a span starts or ends.
        // A sample row of n active edges has at most n / 2 spans, and each span changes the counts of at most three pixels.
        int maxPoints = (3 * Rows * ((_count / 2) + 1)) + 1;
        Span<int> scratch = workspace.Samples.Get(_count + (2 * width) + 1 + maxPoints);
        Span<int> active = scratch[.._count];
        Span<int> full = scratch.Slice(_count, width + 1);
        Span<int> part = scratch.Slice(_count + width + 1, width);
        Span<int> points = scratch.Slice(_count + (2 * width) + 1, maxPoints);
        full.Clear();
        part.Clear();

        // A row's coverage, then room for a fill's own scratch.
        Span<byte> bytes = workspace.Coverage.Get((2 * width) + FillRun);
        Span<byte> coverage = bytes[..width], fill = bytes[width..];
        int activeCount = 0, nextEdge = 0;
        for (int row = start; row < end; row++)
        {
            int pointCount = 0;
            for (int j = 0; j < Rows; j++)
            {
                double y = _area.Y + row + ((double)j / Rows);

                // Edges whose top is at or above the sample row join; those whose bottom is at or above it leave.
                while (nextEdge < _count && _tops[nextEdge] <= y)
                {
                    active[activeCount++] = _byTop[nextEdge++];
                }

                int kept = 0;
                for (int i = 0; i < activeCount; i++)
                {
                    int e = active[i];
                    if (_bottoms[e] > y)
                    {
                        active[kept++] = e;
                    }
                }

                activeCount = kept;
                Span<Crossing> crossings = workspace.Crossings.Get(Math.Max(1, activeCount));
                int n = 0;
                for (int i = 0; i < activeCount; i++)
                {
                    int e = active[i];
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
                SortByX(crossings);
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

                    int pa = a / Columns, pb = b / Columns;
                    points[pointCount++] = pa;
                    if (pa == pb)
                    {
                        part[pa] += b - a;
                    }
                    else
                    {
                        part[pa] += Columns - (a % Columns);
                        full[pa + 1] += Columns;
                        full[pb] -= Columns;
                        points[pointCount++] = pa + 1;
                        if (b % Columns != 0)
                        {
                            part[pb] += b % Columns;
                        }

                        // A span to the area's right edge ends at the marker one past the last pixel.
                        points[pointCount++] = pb;
                    }
                }
            }

            if (pointCount > 0)
            {
                Emit(full, part, points[..pointCount], coverage, fill, _area.Y + row, sink);
            }
        }
    }

    /// <summary>The first sample column at or right of <paramref name="x"/>, clamped to the area.</summary>
    private int Column(float x, int columns)
    {
        double c = Math.Ceiling(((double)x - _area.X) * Columns);
        return (int)Math.Clamp(c, 0, columns);
    }

    // Sends a row's coverage from the pixels whose counts changed: each holds its own count, and the pixels up to the next one all
    // hold the running count of whole sample rows, so they are one constant run. Long constant runs go as fills, the rest as blends;
    // the counts are cleared as they are read.
    private void Emit(Span<int> full, Span<int> part, Span<int> points, Span<byte> coverage, Span<byte> fill, int y, ISpanSink sink)
    {
        SortInts(points);
        Runs runs = new(coverage, fill, sink, y, _area.X);
        int running = 0;
        for (int k = 0; k < points.Length; k++)
        {
            int px = points[k];
            if (px == part.Length)
            {
                break;
            }

            running += full[px];
            byte alpha = Alphas[running + part[px]];
            full[px] = 0;
            part[px] = 0;
            runs.Add(px, 1, alpha);

            // Skip repeats of this point, then the pixels up to the next one share the running count.
            while (k + 1 < points.Length && points[k + 1] == px)
            {
                k++;
            }

            if (k + 1 < points.Length && points[k + 1] > px + 1)
            {
                runs.Add(px + 1, points[k + 1] - px - 1, Alphas[running]);
            }
        }

        runs.Flush();
        full[^1] = 0;
    }

    /// <summary>Collects a row's constant runs: zero ends the pending blend, a long run fills, the rest joins the blend.</summary>
    private ref struct Runs(Span<byte> coverage, Span<byte> fill, ISpanSink sink, int y, int x0)
    {
        private readonly Span<byte> _coverage = coverage;
        private readonly Span<byte> _fill = fill;
        private int _pending = -1;
        private int _length;

        internal void Add(int at, int count, byte alpha)
        {
            if (alpha == 0 || (count >= FillRun && (alpha != 255 || sink.FillsDirectly)))
            {
                Flush();
                if (alpha != 0)
                {
                    sink.Fill(y, x0 + at, count, alpha, _fill);
                }

                return;
            }

            if (_pending < 0)
            {
                _pending = at;
            }

            _coverage.Slice(_length, count).Fill(alpha);
            _length += count;
        }

        internal void Flush()
        {
            if (_pending >= 0)
            {
                sink.Blend(y, x0 + _pending, _coverage[.._length]);
            }

            _pending = -1;
            _length = 0;
        }
    }

    private static void SortInts(Span<int> values)
    {
        if (values.Length > 32)
        {
            values.Sort();
            return;
        }

        for (int i = 1; i < values.Length; i++)
        {
            int v = values[i];
            int j = i - 1;
            while (j >= 0 && values[j] > v)
            {
                values[j + 1] = values[j];
                j--;
            }

            values[j + 1] = v;
        }
    }

    // Insertion sort for the few crossings a sample row has; a library sort past that.
    private static void SortByX(Span<Crossing> crossings)
    {
        if (crossings.Length > 32)
        {
            crossings.Sort(static (a, b) => a.X.CompareTo(b.X));
            return;
        }

        for (int i = 1; i < crossings.Length; i++)
        {
            Crossing c = crossings[i];
            int j = i - 1;
            while (j >= 0 && crossings[j].X > c.X)
            {
                crossings[j + 1] = crossings[j];
                j--;
            }

            crossings[j + 1] = c;
        }
    }

    private static byte[] BuildAlphas()
    {
        byte[] alphas = new byte[Samples + 1];
        for (int n = 0; n <= Samples; n++)
        {
            alphas[n] = (byte)(((n * 255) + (Samples / 2)) / Samples);
        }

        return alphas;
    }
}
