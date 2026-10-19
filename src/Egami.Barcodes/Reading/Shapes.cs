using System.Numerics;

namespace Egami.Barcodes.Reading;

/// <summary>A connected group of dark pixels: its size, bounding box and boundary pixels (dark pixels next to light ones).</summary>
internal sealed class Component
{
    public int Count;
    public int Left = int.MaxValue, Top = int.MaxValue, Right = -1, Bottom = -1;
    public List<Vector2> Boundary { get; } = [];

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
        int[] labels = new int[width * height];
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

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (!bits[x, y])
                {
                    continue;
                }

                int label = 0;
                for (int k = 0; k < 4; k++)
                {
                    // Left, top-left, top, top-right: the neighbours already labelled.
                    int nx = x + (k == 0 ? -1 : k - 2), ny = y + (k == 0 ? 0 : -1);
                    if ((uint)nx >= (uint)width || ny < 0)
                    {
                        continue;
                    }

                    int other = labels[(ny * width) + nx];
                    if (other == 0)
                    {
                        continue;
                    }

                    if (label == 0)
                    {
                        label = Find(other);
                    }
                    else
                    {
                        int a = Find(label), b = Find(other);
                        if (a != b)
                        {
                            parent[Math.Max(a, b)] = Math.Min(a, b);
                            label = Math.Min(a, b);
                        }
                    }
                }

                if (label == 0)
                {
                    label = parent.Count;
                    parent.Add(label);
                }

                labels[(y * width) + x] = label;
            }
        }

        var components = new Dictionary<int, Component>();
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int label = labels[(y * width) + x];
                if (label == 0)
                {
                    continue;
                }

                int root = Find(label);
                if (!components.TryGetValue(root, out Component? c))
                {
                    components[root] = c = new Component();
                }

                c.Count++;
                c.Left = Math.Min(c.Left, x);
                c.Right = Math.Max(c.Right, x);
                c.Top = Math.Min(c.Top, y);
                c.Bottom = Math.Max(c.Bottom, y);
                if (!bits.IsDark(x - 1, y) || !bits.IsDark(x + 1, y) || !bits.IsDark(x, y - 1) || !bits.IsDark(x, y + 1))
                {
                    c.Boundary.Add(new Vector2(x + 0.5f, y + 0.5f));
                }
            }
        }

        return [.. components.Values.Where(c => c.Width >= minimum && c.Height >= minimum).OrderByDescending(c => c.Count)];
    }

    /// <summary>The convex hull of points, counterclockwise in image coordinates (Andrew's monotone chain).</summary>
    public static List<Vector2> Hull(List<Vector2> points)
    {
        var sorted = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
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
