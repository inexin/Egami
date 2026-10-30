namespace Egami.Drawing.Rasterization;

/// <summary>
/// Fills without antialiasing: a pixel is covered when its center is inside, by the fill rule. Edges are sorted by their top, and
/// each row keeps the edges that span its center line; crossings are sorted by x and walked. Edges count over half-open y ranges
/// and spans over half-open x ranges, so shapes that share an edge never both cover a pixel.
/// </summary>
internal sealed class AliasedRasterizer
{
    private readonly float[] _edges;
    private readonly int[] _byTop;
    private readonly Rect _area;
    private readonly bool _evenOdd;

    internal AliasedRasterizer(EdgeList edges, Rect area, bool evenOdd)
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
        // The active edges in the order they joined, kept in an array; the crossings' buffer has room for every edge.
        int[] active = new int[_byTop.Length];
        int count = 0, nextEdge = 0;
        Span<byte> scratch = workspace.Coverage.Get(_area.Width);
        Span<Crossing> buffer = workspace.Crossings.Get(Math.Max(1, _byTop.Length));
        for (int row = start; row < end; row++)
        {
            float center = _area.Y + row + 0.5f;

            // Edges whose top is above this center join; those whose bottom is at or above it leave.
            while (nextEdge < _byTop.Length && Top(_byTop[nextEdge]) <= center)
            {
                active[count++] = _byTop[nextEdge++];
            }

            int kept = 0;
            for (int k = 0; k < count; k++)
            {
                if (Bottom(active[k]) > center)
                {
                    active[kept++] = active[k];
                }
            }

            count = kept;
            int n = 0;
            for (int k = 0; k < count; k++)
            {
                int e = active[k];
                float x0 = _edges[4 * e], y0 = _edges[(4 * e) + 1], x1 = _edges[(4 * e) + 2], y1 = _edges[(4 * e) + 3];
                if (center < MathF.Min(y0, y1))
                {
                    continue;
                }

                buffer[n].X = (float)(x0 + ((center - (double)y0) * (x1 - (double)x0) / (y1 - (double)y0)));
                buffer[n].Winding = y1 > y0 ? 1 : -1;
                n++;
            }

            Span<Crossing> crossings = buffer[..n];
            Sort(crossings);
            int winding = 0;
            int runStart = int.MinValue, runEnd = int.MinValue;
            for (int i = 0; i < n - 1; i++)
            {
                winding += crossings[i].Winding;
                bool inside = _evenOdd ? (winding & 1) != 0 : winding != 0;
                if (!inside)
                {
                    continue;
                }

                // Pixels whose centers x + 0.5 lie in [a, b).
                int a = (int)Math.Max(_area.X, Math.Min(_area.Right, Math.Ceiling(crossings[i].X - 0.5)));
                int b = (int)Math.Max(_area.X, Math.Min(_area.Right, Math.Ceiling(crossings[i + 1].X - 0.5)));
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

    private float Top(int e) => MathF.Min(_edges[(4 * e) + 1], _edges[(4 * e) + 3]);

    private float Bottom(int e) => MathF.Max(_edges[(4 * e) + 1], _edges[(4 * e) + 3]);
}
