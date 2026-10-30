namespace Egami.Drawing.Rasterization;

/// <summary>
/// Fills without antialiasing: a pixel is covered when its center is inside, by the fill rule. Edges are sorted by their top, and
/// each row keeps the edges that span its center line; crossings are sorted by x and walked. Edges count over half-open y ranges
/// and spans over half-open x ranges, so shapes that share an edge never both cover a pixel.
/// </summary>
internal sealed class AliasedRasterizer
{
    private readonly Edge[] _edges;
    private readonly Rect _area;
    private readonly bool _evenOdd;

    internal AliasedRasterizer(EdgeList edges, Rect area, bool evenOdd)
    {
        ReadOnlySpan<float> c = edges.Coordinates;
        _area = area;
        _evenOdd = evenOdd;
        _edges = new Edge[edges.Count];
        float[] tops = new float[edges.Count];
        for (int i = 0; i < _edges.Length; i++)
        {
            _edges[i] = new Edge(c[4 * i], c[(4 * i) + 1], c[(4 * i) + 2], c[(4 * i) + 3]);
            tops[i] = _edges[i].Top;
        }

        Array.Sort(tops, _edges);
    }

    internal void Band(int start, int end, Workspace workspace, ISpanSink sink)
    {
        // The active edges in the order they joined, kept in an array; the crossings' buffer has room for every edge.
        Edge[] edges = _edges;
        int[] active = new int[edges.Length];
        int count = 0, nextEdge = 0;
        Span<byte> scratch = workspace.Coverage.Get(_area.Width);
        Span<Crossing> buffer = workspace.Crossings.Get(Math.Max(1, edges.Length));
        for (int row = start; row < end; row++)
        {
            float center = _area.Y + row + 0.5f;

            // Edges whose top is above this center join; those whose bottom is at or above it leave.
            while (nextEdge < edges.Length && edges[nextEdge].Top <= center)
            {
                active[count++] = nextEdge++;
            }

            int kept = 0;
            for (int k = 0; k < count; k++)
            {
                if (edges[active[k]].Bottom > center)
                {
                    active[kept++] = active[k];
                }
            }

            count = kept;
            if (count == 2)
            {
                // Two crossings, as every row of a convex shape such as a line has: the pixels between them are inside by either
                // fill rule (an edge's winding is never zero), so they are the row's one run, exactly as the general walk finds.
                float x0 = edges[active[0]].CrossingAt(center), x1 = edges[active[1]].CrossingAt(center);
                if (!float.IsNaN(x0) && !float.IsNaN(x1))
                {
                    int left = Column(MathF.Min(x0, x1)), right = Column(MathF.Max(x0, x1));
                    if (right > left)
                    {
                        sink.Fill(_area.Y + row, left, right - left, 255, scratch);
                    }

                    continue;
                }
            }

            for (int k = 0; k < count; k++)
            {
                ref readonly Edge e = ref edges[active[k]];
                buffer[k].X = e.CrossingAt(center);
                buffer[k].Winding = e.Winding;
            }

            Span<Crossing> crossings = buffer[..count];
            Sort(crossings);
            int n = count;
            int winding = 0;
            int runStart = int.MinValue, runEnd = int.MinValue;
            int b = n > 0 ? Column(crossings[0].X) : 0;
            for (int i = 0; i < n - 1; i++)
            {
                // Pixels whose centers x + 0.5 lie in [a, b); each crossing's column is found once.
                int a = b;
                b = Column(crossings[i + 1].X);
                winding += crossings[i].Winding;
                bool inside = _evenOdd ? (winding & 1) != 0 : winding != 0;
                if (!inside)
                {
                    continue;
                }

                if (b <= a)
                {
                    continue;
                }

                if (a <= runEnd)
                {
                    runEnd = Math.Max(runEnd, b);
                    continue;
                }

                if (runEnd > runStart)
                {
                    sink.Fill(_area.Y + row, runStart, runEnd - runStart, 255, scratch);
                }

                runStart = a;
                runEnd = b;
            }

            if (runEnd > runStart)
            {
                sink.Fill(_area.Y + row, runStart, runEnd - runStart, 255, scratch);
            }
        }
    }

    /// <summary>
    /// The first pixel column whose center is at or right of <paramref name="x"/>, within the area: whole-number clamps for the
    /// usual coordinates, the double ones for the rest (NaN among them), with the same results.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private int Column(float x)
    {
        double c = Math.Ceiling(x - 0.5);
        return c is >= -1e9 and <= 1e9 ? Math.Clamp((int)c, _area.X, _area.Right) : (int)Math.Max(_area.X, Math.Min(_area.Right, c));
    }

    /// <summary>
    /// Sorts crossings by x: by insertion for the few most rows have, else by Span.Sort. Crossings at the same x bound no pixels
    /// between them, so their order among themselves never changes the runs.
    /// </summary>
    private static void Sort(Span<Crossing> crossings)
    {
        if (crossings.Length > 16)
        {
            crossings.Sort(static (a, b) => a.X.CompareTo(b.X));
            return;
        }

        for (int i = 1; i < crossings.Length; i++)
        {
            Crossing c = crossings[i];
            int j = i - 1;
            while (j >= 0 && crossings[j].X.CompareTo(c.X) > 0)
            {
                crossings[j + 1] = crossings[j];
                j--;
            }

            crossings[j + 1] = c;
        }
    }

    /// <summary>One edge, with what each row needs ready: its vertical extent, its direction and its run and rise in double.</summary>
    private readonly struct Edge
    {
        private readonly float _x0;
        private readonly float _y0;
        private readonly double _dx;
        private readonly double _dy;

        internal Edge(float x0, float y0, float x1, float y1)
        {
            _x0 = x0;
            _y0 = y0;
            _dx = x1 - (double)x0;
            _dy = y1 - (double)y0;
            Top = MathF.Min(y0, y1);
            Bottom = MathF.Max(y0, y1);
            Winding = y1 > y0 ? 1 : -1;
        }

        internal float Top { get; }

        internal float Bottom { get; }

        internal int Winding { get; }

        /// <summary>The x where the edge crosses the line y = <paramref name="center"/>, in double, then to float.</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal float CrossingAt(float center) => (float)(_x0 + ((center - (double)_y0) * _dx / _dy));
    }
}
