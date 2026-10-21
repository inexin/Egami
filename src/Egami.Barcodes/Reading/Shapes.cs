using System.Numerics;

namespace Egami.Barcodes.Reading;

/// <summary>
/// A connected group of dark pixels: its size, bounding box and, for each of its rows, the centers of its first and last
/// pixel there, which hold its convex hull.
/// </summary>
internal sealed class Component
{
    public int Count;
    public int Left = int.MaxValue, Top = int.MaxValue, Right = -1, Bottom = -1;
    public List<Vector2> Ends { get; } = [];

    // The row whose ends are last in Ends, its first column, and whether its last pixel is in Ends yet.
    internal int EndsRow = -1, EndsRowStart;
    internal bool EndsRowRight;

    public int Width => Right - Left + 1;

    public int Height => Bottom - Top + 1;
}

/// <summary>Geometry for the 2D detectors: connected components, convex hulls, minimum-area rectangles and line fits.</summary>
internal static class Shapes
{
    /// <summary>Labels 8-connected dark components with a union-find over row runs; keeps those at least <paramref name="minimum"/> pixels across.</summary>
    public static List<Component> Components(BitImage bits, int minimum)
    {
        int width = bits.Width, height = bits.Height;

        // Each dark run of each row: its row, first and last-plus-one column, and label; runs touching (diagonals too) a run
        // of the row above join its label.
        var runs = new List<(int Y, int Start, int End, int Label)>();
        var parent = new List<int> { 0 };
        int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }

            return x;
        }

        int above = 0, aboveEnd = 0;
        int[] lengths = new int[width + 2];
        for (int y = 0; y < height; y++)
        {
            int rowStart = runs.Count, count = Binarizer.IntRuns(bits.Row(y), lengths), at = lengths[0];
            for (int i = 1; i < count; i += 2)
            {
                // Odd runs are dark (runs start light).
                int start = at, end = at + lengths[i];
                at = end + (i + 1 < count ? lengths[i + 1] : 0);

                int label = 0;
                while (above < aboveEnd && runs[above].End < start)
                {
                    above++;
                }

                for (int k = above; k < aboveEnd && runs[k].Start <= end; k++)
                {
                    int other = Find(runs[k].Label);
                    if (label == 0)
                    {
                        label = other;
                    }
                    else if (other != label)
                    {
                        int a = Math.Min(label, other), b = Math.Max(label, other);
                        parent[b] = a;
                        label = a;
                    }
                }

                if (label == 0)
                {
                    label = parent.Count;
                    parent.Add(label);
                }

                runs.Add((y, start, end, label));
            }

            (above, aboveEnd) = (rowStart, runs.Count);
        }

        // Bounding boxes by root first, in the order the roots are first met; only components large enough get their ends.
        int labels = parent.Count;
        int[] roots = new int[runs.Count], left = new int[labels], right = new int[labels], top = new int[labels], bottom = new int[labels];
        var order = new List<int>();
        left.AsSpan().Fill(int.MaxValue);
        right.AsSpan().Fill(-1);
        for (int i = 0; i < runs.Count; i++)
        {
            (int y, int start, int end, int label) = runs[i];
            int root = roots[i] = Find(label);
            if (right[root] < 0)
            {
                order.Add(root);
                top[root] = y;
            }

            left[root] = Math.Min(left[root], start);
            right[root] = Math.Max(right[root], end - 1);
            bottom[root] = y;
        }

        var components = new Component?[labels];
        foreach (int root in order)
        {
            if (right[root] - left[root] + 1 >= minimum && bottom[root] - top[root] + 1 >= minimum)
            {
                components[root] = new Component { Left = left[root], Right = right[root], Top = top[root], Bottom = bottom[root] };
            }
        }

        for (int i = 0; i < runs.Count; i++)
        {
            if (components[roots[i]] is not Component c)
            {
                continue;
            }

            (int y, int start, int end, _) = runs[i];
            c.Count += end - start;

            // Runs come row by row and left to right: the row's first run gives its left end, its last run its right end.
            if (c.EndsRow != y)
            {
                c.Ends.Add(new Vector2(start + 0.5f, y + 0.5f));
                (c.EndsRow, c.EndsRowStart, c.EndsRowRight) = (y, start, false);
            }

            if (end - 1 > c.EndsRowStart)
            {
                var last = new Vector2(end - 0.5f, y + 0.5f);
                if (c.EndsRowRight)
                {
                    c.Ends[^1] = last;
                }
                else
                {
                    c.Ends.Add(last);
                    c.EndsRowRight = true;
                }
            }
        }

        return [.. order.Select(root => components[root]).OfType<Component>().OrderByDescending(c => c.Count)];
    }

    /// <summary>The convex hull of points, counterclockwise in image coordinates (Andrew's monotone chain).</summary>
    public static List<Vector2> Hull(List<Vector2> points)
    {
        Vector2[] array = [.. points];
        Array.Sort(array, static (a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
        var sorted = new List<Vector2>(array.Length);
        foreach (Vector2 p in array)
        {
            if (sorted.Count == 0 || sorted[^1] != p)
            {
                sorted.Add(p);
            }
        }

        if (sorted.Count < 3)
        {
            return sorted;
        }

        static float Cross(Vector2 o, Vector2 a, Vector2 b) => ((a.X - o.X) * (b.Y - o.Y)) - ((a.Y - o.Y) * (b.X - o.X));
        var hull = new List<Vector2>();
        foreach (Vector2 p in sorted)
        {
            while (hull.Count >= 2 && Cross(hull[^2], hull[^1], p) <= 0)
            {
                hull.RemoveAt(hull.Count - 1);
            }

            hull.Add(p);
        }

        int lower = hull.Count + 1;
        for (int i = sorted.Count - 2; i >= 0; i--)
        {
            Vector2 p = sorted[i];
            while (hull.Count >= lower && Cross(hull[^2], hull[^1], p) <= 0)
            {
                hull.RemoveAt(hull.Count - 1);
            }

            hull.Add(p);
        }

        hull.RemoveAt(hull.Count - 1);
        return hull;
    }

    /// <summary>The minimum-area rectangle around a convex hull: its four corners in hull order.</summary>
    public static Vector2[] MinimumRectangle(List<Vector2> hull)
    {
        Vector2[] best = [];
        float bestArea = float.MaxValue;
        for (int i = 0; i < hull.Count; i++)
        {
            Vector2 edge = hull[(i + 1) % hull.Count] - hull[i];
            if (edge == Vector2.Zero)
            {
                continue;
            }

            Vector2 u = Vector2.Normalize(edge), v = new(-u.Y, u.X);
            float minU = float.MaxValue, maxU = float.MinValue, minV = float.MaxValue, maxV = float.MinValue;
            foreach (Vector2 p in hull)
            {
                float a = Vector2.Dot(p, u), b = Vector2.Dot(p, v);
                minU = MathF.Min(minU, a);
                maxU = MathF.Max(maxU, a);
                minV = MathF.Min(minV, b);
                maxV = MathF.Max(maxV, b);
            }

            float area = (maxU - minU) * (maxV - minV);
            if (area < bestArea)
            {
                bestArea = area;
                best = [(u * minU) + (v * minV), (u * maxU) + (v * minV), (u * maxU) + (v * maxV), (u * minU) + (v * maxV)];
            }
        }

        return best;
    }

    /// <summary>A line fitted to points by total least squares: a point on it and its unit direction.</summary>
    public static (Vector2 Point, Vector2 Direction)? FitLine(List<Vector2> points)
    {
        if (points.Count < 2)
        {
            return null;
        }

        Vector2 mean = Vector2.Zero;
        foreach (Vector2 p in points)
        {
            mean += p;
        }

        mean /= points.Count;
        double xx = 0, xy = 0, yy = 0;
        foreach (Vector2 p in points)
        {
            Vector2 d = p - mean;
            xx += d.X * d.X;
            xy += d.X * d.Y;
            yy += d.Y * d.Y;
        }

        double angle = 0.5 * Math.Atan2(2 * xy, xx - yy);
        return (mean, new Vector2((float)Math.Cos(angle), (float)Math.Sin(angle)));
    }

    /// <summary>Where two lines meet, or null when they are parallel.</summary>
    public static Vector2? Intersect((Vector2 Point, Vector2 Direction) a, (Vector2 Point, Vector2 Direction) b)
    {
        float denominator = (a.Direction.X * b.Direction.Y) - (a.Direction.Y * b.Direction.X);
        if (MathF.Abs(denominator) < 1e-4f)
        {
            return null;
        }

        Vector2 d = b.Point - a.Point;
        float t = ((d.X * b.Direction.Y) - (d.Y * b.Direction.X)) / denominator;
        return a.Point + (a.Direction * t);
    }
}
