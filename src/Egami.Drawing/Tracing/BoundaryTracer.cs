namespace Egami.Drawing.Tracing;

/// <summary>
/// One closed boundary between ink and paper, along pixel edges: its corners in order (the first is not repeated at the end),
/// with ink on the right when walking it in image coordinates (y down). Outer boundaries so run clockwise on screen and holes
/// counter-clockwise.
/// </summary>
/// <param name="X">The corners' x coordinates.</param>
/// <param name="Y">The corners' y coordinates.</param>
/// <param name="InkX">The x of an ink pixel on the boundary's first edge.</param>
/// <param name="InkY">The y of that pixel.</param>
/// <param name="Area">The signed area: positive for an outer boundary, negative for a hole.</param>
internal sealed record PixelLoop(int[] X, int[] Y, int InkX, int InkY, long Area)
{
    internal int Count => X.Length;

    internal bool IsHole => Area < 0;
}

/// <summary>
/// Splits an ink map's boundaries into closed loops of pixel edges, after the path decomposition in P. Selinger's "Potrace: a
/// polygon-based tracing algorithm" (2003), section 2.1. Each edge between an ink and a paper pixel is used by exactly one loop.
/// Where two ink pixels meet only at a corner, the walk turns so that they stay in one loop (the paper's "black" turn policy),
/// which matches the 8-connected ink of <see cref="InkMap"/>.
/// </summary>
internal static class BoundaryTracer
{
    internal static List<PixelLoop> Trace(InkMap ink, CancellationToken cancellationToken)
    {
        int w = ink.Width, h = ink.Height;

        // One flag per horizontal crack (row y from 0 to h, column x) for the loops already walked; every loop has one.
        bool[] used = new bool[checked((h + 1) * w)];
        List<PixelLoop> loops = [];
        List<int> xs = [], ys = [];
        byte[] data = ink.Data;
        byte[] paper = new byte[w];
        for (int y = 0; y <= h; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The rows on either side of crack row y (paper outside the image); only where they differ is there an edge.
            ReadOnlySpan<byte> rowAbove = y > 0 ? data.AsSpan((y - 1) * w, w) : paper;
            ReadOnlySpan<byte> rowBelow = y < h ? data.AsSpan(y * w, w) : paper;
            for (int x = 0; x < w; x++)
            {
                int same = rowAbove[x..].CommonPrefixLength(rowBelow[x..]);
                x += same;
                if (x >= w)
                {
                    break;
                }

                if (used[(y * w) + x])
                {
                    continue;
                }

                bool below = rowBelow[x] != 0;

                // Ink below: walk right from (x, y). Ink above: walk left from (x + 1, y).
                int sx = below ? x : x + 1, dx = below ? 1 : -1;
                int inkX = x, inkY = below ? y : y - 1;
                xs.Clear();
                ys.Clear();
                Walk(ink, used, sx, y, dx, 0, xs, ys);
                long area = 0;
                for (int i = 0; i < xs.Count; i++)
                {
                    int j = i + 1 == xs.Count ? 0 : i + 1;
                    area += ((long)xs[i] * ys[j]) - ((long)xs[j] * ys[i]);
                }

                loops.Add(new PixelLoop([.. xs], [.. ys], inkX, inkY, area / 2));
            }
        }

        return loops;
    }

    /// <summary>Walks one loop from a corner and a first direction until it comes back to both.</summary>
    private static void Walk(InkMap ink, bool[] used, int startX, int startY, int startDx, int startDy, List<int> xs, List<int> ys)
    {
        int w = ink.Width;
        int x = startX, y = startY, dx = startDx, dy = startDy;
        do
        {
            xs.Add(x);
            ys.Add(y);
            if (dy == 0)
            {
                used[(y * w) + (dx > 0 ? x : x - 1)] = true;
            }

            x += dx;
            y += dy;

            // Prefer a left turn (which keeps diagonal ink together), then straight on, then a right turn. Left of a walker
            // heading (dx, dy) in y-down coordinates is (dy, -dx).
            if (HasEdge(ink, x, y, dy, -dx))
            {
                (dx, dy) = (dy, -dx);
            }
            else if (!HasEdge(ink, x, y, dx, dy))
            {
                (dx, dy) = (-dy, dx);
            }
        }
        while (x != startX || y != startY || dx != startDx || dy != startDy);
    }

    /// <summary>Whether the pixel edge leaving corner (x, y) in direction (dx, dy) has ink on its right and paper on its left.</summary>
    private static bool HasEdge(InkMap ink, int x, int y, int dx, int dy)
    {
        return (dx, dy) switch
        {
            (1, 0) => ink[x, y] && !ink[x, y - 1],
            (-1, 0) => ink[x - 1, y - 1] && !ink[x - 1, y],
            (0, 1) => ink[x - 1, y] && !ink[x, y],
            _ => ink[x, y - 1] && !ink[x - 1, y - 1],
        };
    }
}
