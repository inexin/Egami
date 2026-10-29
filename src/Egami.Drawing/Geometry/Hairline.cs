using System.Numerics;

using Egami.Drawing.Rasterization;

namespace Egami.Drawing.Geometry;

/// <summary>
/// Outlines a device-space path as a hairline: each straight piece becomes a parallelogram one pixel thick across its major axis
/// (one pixel tall for a mostly horizontal piece, one wide for a mostly vertical one), as Skia's antialiased hairlines are, so a
/// hairline covers one pixel per column or row at any angle. Round and square caps extend open ends by half a pixel. All
/// parallelograms wind the same way, so nonzero filling joins them without doubling.
/// </summary>
internal static class Hairline
{
    internal static Path Outline(Path device, LineCap cap, float tolerance) => Outline(device, cap, tolerance, gdiPlus: false);

    /// <summary>
    /// Outlines a device-space path as GDI+ draws pens of 1.5 device pixels or less (<see cref="Antialiasing.GdiPlus"/>), as
    /// measured in 2.7.6: each straight piece swept by the diamond |dx| + |dy| ≤ 1/2, which is one pixel thick across the major
    /// axis at any angle and ends in points half a pixel past each end. Caps do not apply. Every hexagon winds the same way.
    /// </summary>
    internal static Path GdiPlusOutline(Path device, float tolerance) => Outline(device, LineCap.Butt, tolerance, gdiPlus: true);

    private static Path Outline(Path device, LineCap cap, float tolerance, bool gdiPlus)
    {
        PathBuilder builder = new();
        List<Vector2> points = [];
        ReadOnlySpan<Vector2> p = device.Points;
        int i = 0;
        bool closed = false;
        foreach (PathVerb verb in device.Verbs)
        {
            switch (verb)
            {
                case PathVerb.Move:
                    Contour(points, closed, cap, gdiPlus, builder);
                    points.Clear();
                    closed = false;
                    points.Add(p[i++]);
                    break;
                case PathVerb.Line:
                    points.Add(p[i++]);
                    break;
                case PathVerb.Quad:
                    Flattener.Quad(points[^1], p[i], p[i + 1], tolerance, points);
                    i += 2;
                    break;
                case PathVerb.Cubic:
                    Flattener.Cubic(points[^1], p[i], p[i + 1], p[i + 2], tolerance, points);
                    i += 3;
                    break;
                default:
                    closed = true;
                    break;
            }
        }

        Contour(points, closed, cap, gdiPlus, builder);
        return builder.ToPathUnchecked();
    }

    private static void Contour(List<Vector2> points, bool closed, LineCap cap, bool gdiPlus, PathBuilder builder)
    {
        if (points.Count == 0)
        {
            return;
        }

        if (closed)
        {
            points.Add(points[0]);
        }

        if (gdiPlus)
        {
            Diamonds(points, builder);
            return;
        }

        if (points.Count == 1 || (points.Count == 2 && points[0] == points[1]))
        {
            // A zero-length hairline is a one-pixel dot with round or square caps.
            if (cap != LineCap.Butt)
            {
                builder.AddRect(new RectF(points[0].X - 0.5f, points[0].Y - 0.5f, 1, 1));
            }

            return;
        }

        int last = points.Count - 1;
        for (int k = 0; k < last; k++)
        {
            Vector2 a = points[k], b = points[k + 1];
            if (a == b)
            {
                continue;
            }

            Vector2 d = b - a;
            bool major = MathF.Abs(d.X) >= MathF.Abs(d.Y);
            if (!closed && cap != LineCap.Butt)
            {
                Vector2 extend = Vector2.Normalize(d) * 0.5f;
                if (k == 0)
                {
                    a -= extend;
                }

                if (k == last - 1)
                {
                    b += extend;
                }
            }

            Vector2 o = major ? new Vector2(0, 0.5f) : new Vector2(0.5f, 0);
            Vector2 p0 = a + o, p1 = b + o, p2 = b - o, p3 = a - o;

            // Every piece clockwise on screen, so overlaps at the joins add up under nonzero instead of cancelling.
            float area = ((p1.X - p0.X) * (p2.Y - p0.Y)) - ((p1.Y - p0.Y) * (p2.X - p0.X));
            if (area < 0)
            {
                builder.MoveTo(p0).LineTo(p3).LineTo(p2).LineTo(p1).Close();
            }
            else
            {
                builder.MoveTo(p0).LineTo(p1).LineTo(p2).LineTo(p3).Close();
            }
        }
    }

    // Each piece swept by the half-pixel diamond: the convex hull of the diamonds at its two ends, a hexagon. A zero-length piece
    // draws nothing. All hulls wind the same way.
    private static void Diamonds(List<Vector2> points, PathBuilder builder)
    {
        Span<Vector2> corners = stackalloc Vector2[8];
        Span<Vector2> hull = stackalloc Vector2[9];
        for (int k = 0; k < points.Count - 1; k++)
        {
            Vector2 a = points[k], b = points[k + 1];
            if (a == b)
            {
                continue;
            }

            Diamond(a, corners);
            Diamond(b, corners[4..]);
            int n = ConvexHull(corners, hull);
            builder.MoveTo(hull[0]);
            for (int i = 1; i < n; i++)
            {
                builder.LineTo(hull[i]);
            }

            builder.Close();
        }

        static void Diamond(Vector2 p, Span<Vector2> d)
        {
            d[0] = p + new Vector2(0.5f, 0);
            d[1] = p + new Vector2(0, 0.5f);
            d[2] = p - new Vector2(0.5f, 0);
            d[3] = p - new Vector2(0, 0.5f);
        }
    }

    // Andrew's monotone chain over a few points: the hull in order, clockwise on screen (y down), without collinear points.
    private static int ConvexHull(Span<Vector2> p, Span<Vector2> hull)
    {
        p.Sort(static (u, v) => u.X != v.X ? u.X.CompareTo(v.X) : u.Y.CompareTo(v.Y));
        int k = 0;
        for (int i = 0; i < p.Length; i++)
        {
            while (k >= 2 && Cross(hull[k - 2], hull[k - 1], p[i]) <= 0)
            {
                k--;
            }

            hull[k++] = p[i];
        }

        for (int i = p.Length - 2, lower = k + 1; i >= 0; i--)
        {
            while (k >= lower && Cross(hull[k - 2], hull[k - 1], p[i]) <= 0)
            {
                k--;
            }

            hull[k++] = p[i];
        }

        return k - 1;

        static float Cross(Vector2 o, Vector2 a, Vector2 b) => ((a.X - o.X) * (b.Y - o.Y)) - ((a.Y - o.Y) * (b.X - o.X));
    }
}
