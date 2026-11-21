using Egami.Pixels;

namespace Egami.Drawing.Tracing;

/// <summary>
/// A closed walk along pixel edges with its owner's pixels on the right: the lattice point it starts at and one direction per
/// edge (0 right, 1 down, 2 left, 3 up, y down). Outer edges run clockwise on screen, holes counter-clockwise.
/// </summary>
internal sealed record EdgeLoop(int StartX, int StartY, byte[] Directions, int Owner, long Area)
{
    internal bool IsHole => Area < 0;
}

/// <summary>
/// Walks the boundaries of sets of 4-connected regions and places points on them: the middle of each pixel edge, moved across
/// the edge by the blend of the two pixels beside it so an antialiased or blurred edge lands where it was drawn.
/// </summary>
internal sealed class RegionTracer
{
    internal static readonly int[] Dx = [1, 0, -1, 0];
    internal static readonly int[] Dy = [0, 1, 0, -1];

    private readonly LabelImage _image;

    internal RegionTracer(LabelImage image, int[] regions)
    {
        _image = image;
        Regions = regions;
    }

    internal int Width => _image.Width;

    internal int Height => _image.Height;

    /// <summary>Gets the region of each pixel.</summary>
    internal int[] Regions { get; }

    /// <summary>The region of a pixel, or -1 outside the image.</summary>
    internal int RegionAt(int x, int y) => (uint)x < (uint)Width && (uint)y < (uint)Height ? Regions[(y * Width) + x] : -1;

    /// <summary>The pixel on the right of the edge leaving lattice point (x, y) in direction d.</summary>
    internal static (int X, int Y) Right(int x, int y, int d) => d switch
    {
        0 => (x, y),
        1 => (x - 1, y),
        2 => (x - 1, y - 1),
        _ => (x, y - 1),
    };

    /// <summary>The pixel on the left of the edge leaving lattice point (x, y) in direction d.</summary>
    internal static (int X, int Y) Left(int x, int y, int d) => d switch
    {
        0 => (x, y - 1),
        1 => (x, y),
        2 => (x - 1, y),
        _ => (x - 1, y - 1),
    };

    /// <summary>
    /// Walks every boundary of every owner: <paramref name="owner"/> gives each region's owner, or -1 for none. Loops come in the
    /// order a row-by-row scan meets their top edge. Where two of an owner's pixels touch only at a corner the walk turns
    /// toward them, so they stay apart (4-connected).
    /// </summary>
    internal List<EdgeLoop> Walk(int[] owner, CancellationToken cancellationToken)
    {
        int w = Width, h = Height;
        ulong[] used = new ulong[((4L * (w + 1) * (h + 1)) + 63) / 64];
        List<EdgeLoop> loops = [];
        List<byte> directions = [];
        for (int y = 0; y < h; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < w; x++)
            {
                int o = owner[Regions[(y * w) + x]];
                if (o < 0 || Owner(owner, x, y - 1) == o || IsUsed(used, x, y, 0))
                {
                    continue;
                }

                directions.Clear();
                int cx = x, cy = y, d = 0;
                long area2 = 0;
                do
                {
                    Use(used, cx, cy, d);
                    directions.Add((byte)d);
                    area2 += ((long)cx * Dy[d]) - ((long)Dx[d] * cy);
                    cx += Dx[d];
                    cy += Dy[d];

                    // Right turn first, then straight, then left.
                    int right = (d + 1) & 3, left = (d + 3) & 3;
                    d = HasEdge(owner, o, cx, cy, right) ? right : HasEdge(owner, o, cx, cy, d) ? d : left;
                }
                while (cx != x || cy != y || d != 0);

                loops.Add(new EdgeLoop(x, y, [.. directions], o, area2 / 2));
            }
        }

        return loops;
    }

    /// <summary>The lattice points of a loop, one per edge: the point each edge leaves from.</summary>
    internal static (int X, int Y)[] Vertices(EdgeLoop loop)
    {
        (int X, int Y)[] vertices = new (int, int)[loop.Directions.Length];
        int x = loop.StartX, y = loop.StartY;
        for (int i = 0; i < vertices.Length; i++)
        {
            vertices[i] = (x, y);
            x += Dx[loop.Directions[i]];
            y += Dy[loop.Directions[i]];
        }

        return vertices;
    }

    /// <summary>
    /// The points of a run of edges: one per edge, except on a hard staircase. There each step (a straight run between a turn one
    /// way and a turn the other way, with no blend on either side) gives one point at its middle, which lies on the line the
    /// staircase draws, where the middles of its edges zigzag half a pixel either side of it.
    /// </summary>
    /// <param name="x">The lattice x of each edge's start.</param>
    /// <param name="y">The lattice y of each edge's start.</param>
    /// <param name="directions">Each edge's direction.</param>
    /// <param name="closed">Whether the run is a whole loop; an open run keeps every point of its first and last stretch.</param>
    internal Vec[] Read(ReadOnlySpan<int> x, ReadOnlySpan<int> y, ReadOnlySpan<byte> directions, bool closed)
    {
        int n = directions.Length;
        Vec[] edge = new Vec[n];
        bool[] hard = new bool[n];
        for (int i = 0; i < n; i++)
        {
            int d = directions[i];
            edge[i] = EdgePoint(x[i], y[i], d);
            hard[i] = edge[i] == new Vec(x[i] + (0.5 * Dx[d]), y[i] + (0.5 * Dy[d]));
        }

        // Find where the direction changes; a loop in one direction (impossible) or a run with no change keeps every point.
        int start = -1;
        for (int i = 0; i < n && start < 0; i++)
        {
            if (directions[i] != directions[(i + n - 1) % n])
            {
                start = i;
            }
        }

        if (closed && start < 0)
        {
            return edge;
        }

        // The straight runs, each a staircase step (turns of opposite sense at its ends, no blend) or not.
        List<(int Start, int Length, bool Staircase, bool Hard)> runs = [];
        if (!closed)
        {
            start = 0;
        }

        for (int at = 0; at < n;)
        {
            int s = (start + at) % n, length = 1;
            while (at + length < n && directions[(s + length) % n] == directions[s])
            {
                length++;
            }

            int e = (s + length - 1) % n;
            bool all = true;
            for (int i = 0; i < length; i++)
            {
                all &= hard[(s + i) % n];
            }

            bool interior = closed || (at > 0 && at + length < n);
            bool staircase = all && interior && Sense(directions[(s + n - 1) % n], directions[s]) != Sense(directions[e], directions[(e + 1) % n]);
            runs.Add((s, length, staircase, all));
            at += length;
        }

        List<Vec> points = new(n + runs.Count);
        for (int r = 0; r < runs.Count; r++)
        {
            (int s, int length, bool staircase, bool all) = runs[r];
            int d = directions[s];
            if (staircase)
            {
                points.Add(new Vec(x[s] + (0.5 * length * Dx[d]), y[s] + (0.5 * length * Dy[d])));
                continue;
            }

            // Between two hard runs that are no staircase steps lies a pixel corner: keep it, so a short side keeps its corners.
            bool previousSide = closed ? !runs[(r + runs.Count - 1) % runs.Count].Staircase && runs[(r + runs.Count - 1) % runs.Count].Hard : r > 0 && !runs[r - 1].Staircase && runs[r - 1].Hard;
            if (all && previousSide && runs.Count > 1)
            {
                points.Add(new Vec(x[s], y[s]));
            }

            for (int i = 0; i < length; i++)
            {
                points.Add(edge[(s + i) % n]);
            }
        }

        return [.. points];
    }

    /// <summary>1 for a right turn from direction a to b, -1 for a left turn.</summary>
    private static int Sense(int a, int b) => ((b - a) & 3) == 1 ? 1 : -1;

    /// <summary>
    /// The point on the edge leaving lattice point (x, y) in direction d: its middle, moved across the edge by where the colors of
    /// the pixels on either side say the edge between their two palette colors lies. A hard edge stays in the middle, so a
    /// staircase is read as the line through its steps.
    /// </summary>
    internal Vec EdgePoint(int x, int y, int d)
    {
        Vec middle = new(x + (0.5 * Dx[d]), y + (0.5 * Dy[d]));
        (int px, int py) = Right(x, y, d);
        (int qx, int qy) = Left(x, y, d);
        double offset = Offset(px, py, qx, qy);
        return offset == 0 ? middle : middle + (new Vec(Dy[d], -Dx[d]) * offset);
    }

    /// <summary>
    /// How far the edge between pixel p (inside) and pixel q lies past their shared side toward q, from -1/2 to 1/2: each pixel's
    /// share of the other's palette color, read along the line between the two colors, when the pixel is a clean blend of them.
    /// </summary>
    internal double Offset(int px, int py, int qx, int qy)
    {
        int a = _image[px, py], b = _image[qx, qy];
        if (a < 0 || b < 0 || a == b)
        {
            return 0;
        }

        Rgba8 ca = _image.Palette[a], cb = _image.Palette[b];
        double dr = cb.R - ca.R, dg = cb.G - ca.G, db = cb.B - ca.B;
        double length = (dr * dr) + (dg * dg) + (db * db);
        if (length < 32 * 32)
        {
            return 0;
        }

        double tp = Share(px, py, ca, dr, dg, db, length, 0);
        double tq = Share(qx, qy, ca, dr, dg, db, length, 1);
        return Math.Clamp(1 - tp - tq, -0.5, 0.5);
    }

    /// <summary>A pixel's share of the second color, 0 to 1, or <paramref name="fallback"/> when the pixel is no clean blend.</summary>
    private double Share(int x, int y, Rgba8 ca, double dr, double dg, double db, double length, double fallback)
    {
        int i = 3 * ((y * Width) + x);
        byte[] rgb = _image.Rgb;
        double pr = rgb[i] - ca.R, pg = rgb[i + 1] - ca.G, pb = rgb[i + 2] - ca.B;
        double t = ((pr * dr) + (pg * dg) + (pb * db)) / length;
        double er = pr - (t * dr), eg = pg - (t * dg), eb = pb - (t * db);
        if ((er * er) + (eg * eg) + (eb * eb) > (0.05 * length) + 100)
        {
            return fallback;
        }

        return Math.Clamp(t, 0, 1);
    }

    /// <summary>
    /// Whether lattice point (x, y) is a junction: three or more regions meet there (the outside counting as one), or two meet
    /// corner to corner.
    /// </summary>
    internal bool IsJunction(int x, int y)
    {
        int a = RegionAt(x - 1, y - 1), b = RegionAt(x, y - 1), c = RegionAt(x - 1, y), d = RegionAt(x, y);
        int distinct = 1 + (b != a ? 1 : 0) + (c != a && c != b ? 1 : 0) + (d != a && d != b && d != c ? 1 : 0);
        return distinct >= 3 || (distinct == 2 && a == d && b == c);
    }

    /// <summary>A key for the edge leaving lattice point (x, y) in direction d.</summary>
    internal long EdgeKey(int x, int y, int d) => ((((long)y * (Width + 1)) + x) * 4) + d;

    /// <summary>The key of the same edge walked the other way.</summary>
    internal long ReverseKey(int x, int y, int d) => EdgeKey(x + Dx[d], y + Dy[d], (d + 2) & 3);

    private int Owner(int[] owner, int x, int y)
    {
        int region = RegionAt(x, y);
        return region < 0 ? -1 : owner[region];
    }

    private bool HasEdge(int[] owner, int o, int x, int y, int d)
    {
        (int rx, int ry) = Right(x, y, d);
        (int lx, int ly) = Left(x, y, d);
        return Owner(owner, rx, ry) == o && Owner(owner, lx, ly) != o;
    }

    private bool IsUsed(ulong[] used, int x, int y, int d)
    {
        long key = EdgeKey(x, y, d);
        return (used[key >> 6] & (1UL << (int)(key & 63))) != 0;
    }

    private void Use(ulong[] used, int x, int y, int d)
    {
        long key = EdgeKey(x, y, d);
        used[key >> 6] |= 1UL << (int)(key & 63);
    }
}
