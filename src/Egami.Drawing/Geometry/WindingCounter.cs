using System.Numerics;

namespace Egami.Drawing.Geometry;

/// <summary>
/// Counts how often a path's outline winds around a point, on the curves themselves: each curve is cut where it turns in y, and
/// each y-monotone piece is crossed by the point's horizontal ray at most once, found by bisection. Pieces count over half-open
/// y ranges, so a ray through a vertex counts once. A point on the outline is reported separately.
/// </summary>
internal struct WindingCounter(Vector2 point, double scale)
{
    private readonly double _px = point.X;
    private readonly double _py = point.Y;
    private readonly double _epsilon = scale * 1e-6;

    /// <summary>Gets the winding number: +1 for each piece crossing the ray to the right going down, −1 going up.</summary>
    internal int Winding { get; private set; }

    /// <summary>Walks every contour, closing open ones.</summary>
    /// <returns><see langword="false"/> if the point lies on the outline.</returns>
    internal bool Walk(Path path)
    {
        ReadOnlySpan<Vector2> points = path.Points;
        int p = 0;
        Vector2 start = default, last = default;
        bool open = false;
        Span<float> ts = stackalloc float[2];
        foreach (PathVerb verb in path.Verbs)
        {
            switch (verb)
            {
                case PathVerb.Move:
                    if (open && !Line(last, start))
                    {
                        return false;
                    }

                    start = last = points[p++];
                    open = true;
                    break;
                case PathVerb.Line:
                    if (!Line(last, points[p]))
                    {
                        return false;
                    }

                    last = points[p++];
                    break;
                case PathVerb.Quad:
                    {
                        // Raised to a cubic, so one monotone walker serves both.
                        (Vector2 c1, Vector2 c2) = Curves.Elevate(last, points[p], points[p + 1]);
                        if (!Cubic(last, c1, c2, points[p + 1], ts))
                        {
                            return false;
                        }

                        last = points[p + 1];
                        p += 2;
                        break;
                    }

                case PathVerb.Cubic:
                    if (!Cubic(last, points[p], points[p + 1], points[p + 2], ts))
                    {
                        return false;
                    }

                    last = points[p + 2];
                    p += 3;
                    break;
                default:
                    if (open && !Line(last, start))
                    {
                        return false;
                    }

                    last = start;
                    open = false;
                    break;
            }
        }

        return !open || Line(last, start);
    }

    private bool Line(Vector2 a, Vector2 b)
    {
        double ax = a.X, ay = a.Y, bx = b.X, by = b.Y;
        double dx = bx - ax, dy = by - ay;
        double length2 = (dx * dx) + (dy * dy);
        double t = length2 == 0 ? 0 : Math.Clamp((((_px - ax) * dx) + ((_py - ay) * dy)) / length2, 0, 1);
        double ex = ax + (t * dx) - _px, ey = ay + (t * dy) - _py;
        if ((ex * ex) + (ey * ey) <= _epsilon * _epsilon)
        {
            return false;
        }

        if (ay <= _py && _py < by)
        {
            double x = ax + ((_py - ay) * dx / dy);
            Winding += x > _px ? 1 : 0;
        }
        else if (by <= _py && _py < ay)
        {
            double x = ax + ((_py - ay) * dx / dy);
            Winding -= x > _px ? 1 : 0;
        }

        return true;
    }

    private bool Cubic(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, Span<float> ts)
    {
        // A piece whose bounds miss the ray's row, or lie wholly left of the point, adds nothing.
        float minY = MathF.Min(MathF.Min(p0.Y, p1.Y), MathF.Min(p2.Y, p3.Y)), maxY = MathF.Max(MathF.Max(p0.Y, p1.Y), MathF.Max(p2.Y, p3.Y));
        float maxX = MathF.Max(MathF.Max(p0.X, p1.X), MathF.Max(p2.X, p3.X));
        if (_py < minY - _epsilon || _py > maxY + _epsilon || _px > maxX + _epsilon)
        {
            return true;
        }

        int n = Curves.CubicExtremaY(p0, p1, p2, p3, ts);
        double t0 = 0;
        for (int i = 0; i <= n; i++)
        {
            double t1 = i < n ? ts[i] : 1;
            if (!MonotonePiece(p0, p1, p2, p3, t0, t1))
            {
                return false;
            }

            t0 = t1;
        }

        return true;
    }

    private bool MonotonePiece(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, double t0, double t1)
    {
        double y0 = Eval(p0, p1, p2, p3, t0).Y;
        double y1 = Eval(p0, p1, p2, p3, t1).Y;
        bool down = y0 < y1;
        double lo = Math.Min(y0, y1), hi = Math.Max(y0, y1);
        if (_py < lo - _epsilon || _py > hi + _epsilon)
        {
            return true;
        }

        // The t where y crosses the point's row, clamped to the piece, then the curve's x there.
        double a = t0, b = t1;
        for (int i = 0; i < 60 && b - a > 1e-15; i++)
        {
            double m = (a + b) / 2;
            double y = Eval(p0, p1, p2, p3, m).Y;
            if ((y < _py) == down)
            {
                a = m;
            }
            else
            {
                b = m;
            }
        }

        (double x, double yAt) = Eval(p0, p1, p2, p3, (a + b) / 2);
        if (Math.Abs(x - _px) <= _epsilon && Math.Abs(yAt - _py) <= _epsilon * 4)
        {
            return false;
        }

        if (lo <= _py && _py < hi && x > _px)
        {
            Winding += down ? 1 : -1;
        }

        return true;
    }

    private static (double X, double Y) Eval(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, double t)
    {
        double u = 1 - t;
        double a = u * u * u, b = 3 * u * u * t, c = 3 * u * t * t, d = t * t * t;
        return ((a * p0.X) + (b * p1.X) + (c * p2.X) + (d * p3.X), (a * p0.Y) + (b * p1.Y) + (c * p2.Y) + (d * p3.Y));
    }
}
