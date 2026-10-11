using System.Numerics;

using Egami.Drawing.Rasterization;

namespace Egami.Drawing.Geometry;

/// <summary>
/// Outlines a path with a pen, as the SVG 1.1 and ISO 32000-1 8.4.3 stroke definitions describe, giving a path to fill with the
/// nonzero rule. Each contour is first cut into straight pieces: curves into lines short enough that both their own flatness and
/// the flatness of their offset half a width away stay within the tolerance. Each piece is then offset by half the width on both
/// sides. Corners take the pen's join on their outer side; on the inner side the outline runs through the corner point itself, so
/// the small loop it makes winds the same way as the stroke and nonzero coverage fills it rather than cutting a notch. An open
/// contour runs out along one side, around the end cap, back along the other and around the start cap; a closed one becomes two
/// loops, one per side, running opposite ways so the hole stays empty.
/// </summary>
internal static class Stroker
{
    /// <summary>Points closer than this, relative to the half-width, count as the same point.</summary>
    private const float Epsilon = 1e-5f;

    internal static Path Outline(Path path, Stroke stroke, float tolerance)
    {
        float half = stroke.Width / 2;
        PathBuilder builder = new();
        if (half <= 0 || path.Points.Length == 0)
        {
            return builder.ToPath();
        }

        // A contour counts once it has a line, curve or close: a lone move draws nothing, even with round caps.
        List<Vector2> points = [];
        List<bool> smooth = [];
        ReadOnlySpan<Vector2> source = path.Points;
        int p = 0;
        bool closed = false, drawn = false;
        foreach (PathVerb verb in path.Verbs)
        {
            drawn |= verb != PathVerb.Move;
            switch (verb)
            {
                case PathVerb.Move:
                    if (drawn)
                    {
                        Contour(points, smooth, closed, stroke, half, tolerance, builder);
                    }

                    points.Clear();
                    smooth.Clear();
                    closed = drawn = false;
                    points.Add(source[p++]);
                    smooth.Add(false);
                    break;
                case PathVerb.Line:
                    Add(points, smooth, source[p++], half, false);
                    break;
                case PathVerb.Quad:
                    {
                        (Vector2 c1, Vector2 c2) = Curves.Elevate(points[^1], source[p], source[p + 1]);
                        Curve(points, smooth, points[^1], c1, c2, source[p + 1], half, tolerance);
                        p += 2;
                        break;
                    }

                case PathVerb.Cubic:
                    Curve(points, smooth, points[^1], source[p], source[p + 1], source[p + 2], half, tolerance);
                    p += 3;
                    break;
                default:
                    closed = true;
                    break;
            }
        }

        if (drawn)
        {
            Contour(points, smooth, closed, stroke, half, tolerance, builder);
        }

        return builder.ToPath();
    }

    private static void Add(List<Vector2> points, List<bool> smooth, Vector2 point, float half, bool isSmooth)
    {
        if (Vector2.DistanceSquared(points[^1], point) > (Epsilon * half) * (Epsilon * half))
        {
            points.Add(point);
            smooth.Add(isSmooth);
        }
    }

    /// <summary>
    /// Cuts a cubic into lines: as many as Wang's formula needs for the tolerance, and enough that each turns by at most the angle
    /// whose sagitta at half the width is the tolerance. A Bézier curve turns no more than its control polygon, so the polygon's
    /// turning bounds the curve's.
    /// </summary>
    private static void Curve(List<Vector2> points, List<bool> smooth, Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float half, float tolerance)
    {
        int n = Flattener.CubicCount(p0, p1, p2, p3, tolerance);
        double turn = Angle(p0, p1, p2) + Angle(p1, p2, p3);
        double maxStep = half > tolerance ? 2 * Math.Acos(Math.Clamp(1 - (tolerance / half), -1, 1)) : Math.PI / 4;
        maxStep = Math.Min(maxStep, Math.PI / 4);
        n = Math.Max(n, (int)Math.Min(4096, Math.Ceiling(turn / maxStep)));
        for (int i = 1; i <= n; i++)
        {
            // Points inside a curve are smooth: their joins follow the offset curve (round), whatever the pen's join.
            Add(points, smooth, i == n ? p3 : Curves.Cubic(p0, p1, p2, p3, i / (float)n), half, i < n);
        }
    }

    /// <summary>The angle between the directions a→b and b→c, skipping zero-length legs.</summary>
    private static double Angle(Vector2 a, Vector2 b, Vector2 c)
    {
        Vector2 u = b - a, v = c - b;
        if (u == Vector2.Zero || v == Vector2.Zero)
        {
            return u == Vector2.Zero && v == Vector2.Zero ? 0 : Math.PI / 2;
        }

        double cross = ((double)u.X * v.Y) - ((double)u.Y * v.X), dot = ((double)u.X * v.X) + ((double)u.Y * v.Y);
        return Math.Abs(Math.Atan2(cross, dot));
    }

    private static void Contour(List<Vector2> points, List<bool> smooth, bool closed, Stroke stroke, float half, float tolerance, PathBuilder builder)
    {
        if (points.Count == 0)
        {
            return;
        }

        if (closed && points.Count > 2 && Vector2.DistanceSquared(points[^1], points[0]) <= (Epsilon * half) * (Epsilon * half))
        {
            points.RemoveAt(points.Count - 1);
            smooth.RemoveAt(smooth.Count - 1);
        }

        if (points.Count == 1)
        {
            Dot(points[0], stroke.Cap, half, tolerance, builder);
            return;
        }

        if (closed && points.Count == 2)
        {
            // A closed contour of two points goes out and back: an open line with joins where caps would be.
            closed = false;
            points.Add(points[0]);
            smooth.Add(false);
        }

        if (closed)
        {
            Loop(points, smooth, +1, stroke, half, tolerance, builder);
            Loop(points, smooth, -1, stroke, half, tolerance, builder);
            return;
        }

        int n = points.Count;
        Vector2 first = Direction(points[0], points[1]), last = Direction(points[n - 2], points[n - 1]);
        builder.MoveTo(points[0] + (half * Normal(first)));
        for (int i = 1; i < n - 1; i++)
        {
            Join(builder, points[i], Direction(points[i - 1], points[i]), Direction(points[i], points[i + 1]), +1, JoinAt(stroke, smooth[i], Shortest(points, i - 1, i, i + 1)), half, tolerance);
        }

        builder.LineTo(points[n - 1] + (half * Normal(last)));
        Cap(builder, points[n - 1], last, stroke.Cap, half, tolerance);
        for (int i = n - 2; i > 0; i--)
        {
            Join(builder, points[i], -Direction(points[i], points[i + 1]), -Direction(points[i - 1], points[i]), +1, JoinAt(stroke, smooth[i], Shortest(points, i - 1, i, i + 1)), half, tolerance);
        }

        builder.LineTo(points[0] - (half * Normal(first)));
        Cap(builder, points[0], -first, stroke.Cap, half, tolerance);
        builder.Close();
    }

    /// <summary>One side of a closed contour, as a closed loop: <paramref name="side"/> +1 runs forward, −1 backward, each on its normal's side.</summary>
    private static void Loop(List<Vector2> points, List<bool> smooth, int side, Stroke stroke, float half, float tolerance, PathBuilder builder)
    {
        int n = points.Count;
        bool started = false;
        for (int k = 0; k < n; k++)
        {
            // Forward visits 0, 1, ..., n−1; backward visits 0, n−1, ..., 1. Each vertex joins the legs into and out of it.
            int i = side > 0 ? k : (n - k) % n;
            int before = side > 0 ? (i + n - 1) % n : (i + 1) % n;
            int after = side > 0 ? (i + 1) % n : (i + n - 1) % n;
            Vector2 d0 = Direction(points[before], points[i]), d1 = Direction(points[i], points[after]);
            if (!started)
            {
                builder.MoveTo(points[i] + (half * Normal(d0)));
                started = true;
                JoinTail(builder, points[i], d0, d1, JoinAt(stroke, smooth[i], Shortest(points, before, i, after)), half, tolerance);
                continue;
            }

            Join(builder, points[i], d0, d1, +1, JoinAt(stroke, smooth[i], Shortest(points, before, i, after)), half, tolerance);
        }

        builder.Close();
    }

    /// <summary>
    /// Adds the corner at <paramref name="point"/> on the normal's side of travel (direction <paramref name="d0"/> in, <paramref name="d1"/>
    /// out): the end of the incoming offset, the join, and the start of the outgoing offset.
    /// </summary>
    private static void Join(PathBuilder builder, Vector2 point, Vector2 d0, Vector2 d1, int side, Pen pen, float half, float tolerance)
    {
        Vector2 a = point + (side * half * Normal(d0));
        builder.LineTo(a);
        JoinTail(builder, point, d0, d1, pen, half, tolerance);
    }

    /// <summary>The join after the incoming offset's end, ending at the outgoing offset's start.</summary>
    private static void JoinTail(PathBuilder builder, Vector2 point, Vector2 d0, Vector2 d1, Pen pen, float half, float tolerance)
    {
        Vector2 n0 = Normal(d0), n1 = Normal(d1);
        Vector2 b = point + (half * n1);
        float cross = (d0.X * d1.Y) - (d0.Y * d1.X), dot = Vector2.Dot(d0, d1);
        if (MathF.Abs(cross) < 1e-6f && dot > 0)
        {
            // Straight on.
            builder.LineTo(b);
            return;
        }

        // The normal's side is outside the turn when cross < 0 (a counterclockwise turn on screen), and also when the path doubles
        // back on itself.
        bool outer = cross < 0 || (MathF.Abs(cross) < 1e-6f && dot <= 0);
        if (!outer)
        {
            // Inside the turn the two offset lines cross at h·tan(θ/2) before the corner. When both segments are longer than that,
            // the crossing is the exact inner corner; otherwise the outline runs through the corner point itself, whose small loop
            // winds the same way as the stroke and is filled over.
            float overshoot = half * MathF.Abs(cross) / (1 + dot);
            if (overshoot <= pen.Shortest)
            {
                builder.LineTo(point + (half * (n0 + n1) / (1 + dot)));
            }
            else
            {
                builder.LineTo(point);
            }

            builder.LineTo(b);
            return;
        }

        switch (pen.Join)
        {
            case LineJoin.Round:
                {
                    Vector2 bulge = n0 + n1;
                    Arc(builder, point, n0, n1, bulge.LengthSquared() > 1e-8f ? bulge : d0, half, tolerance);
                    break;
                }

            case LineJoin.Miter:
                {
                    // The miter's length over the width is 1 / cos(θ/2), θ the turn; past the limit it becomes a bevel.
                    float cosHalf = MathF.Sqrt(MathF.Max(0, (1 + dot) / 2));
                    if (cosHalf > 1e-6f && 1 / cosHalf <= pen.MiterLimit)
                    {
                        Vector2 bisector = Vector2.Normalize(n0 + n1);
                        builder.LineTo(point + (bisector * (half / cosHalf)));
                    }

                    builder.LineTo(b);
                    break;
                }

            default:
                builder.LineTo(b);
                break;
        }
    }

    /// <summary>The cap at the end of a line running in <paramref name="direction"/>, from its offset on the normal's side to the other.</summary>
    private static void Cap(PathBuilder builder, Vector2 point, Vector2 direction, LineCap cap, float half, float tolerance)
    {
        Vector2 normal = Normal(direction);
        switch (cap)
        {
            case LineCap.Round:
                Arc(builder, point, normal, -normal, direction, half, tolerance);
                break;
            case LineCap.Square:
                builder.LineTo(point + (half * (normal + direction)));
                builder.LineTo(point + (half * (direction - normal)));
                builder.LineTo(point - (half * normal));
                break;
            default:
                builder.LineTo(point - (half * normal));
                break;
        }
    }

    /// <summary>A zero-length contour: a circle for round caps, a square for square caps, nothing for butt caps, as SVG says.</summary>
    private static void Dot(Vector2 point, LineCap cap, float half, float tolerance, PathBuilder builder)
    {
        if (cap == LineCap.Round)
        {
            Vector2 start = point + new Vector2(half, 0);
            builder.MoveTo(start);
            Arc(builder, point, Vector2.UnitX, -Vector2.UnitX, Vector2.UnitY, half, tolerance);
            Arc(builder, point, -Vector2.UnitX, Vector2.UnitX, -Vector2.UnitY, half, tolerance);
            builder.Close();
        }
        else if (cap == LineCap.Square)
        {
            builder.AddRect(new RectF(point.X - half, point.Y - half, 2 * half, 2 * half));
        }
    }

    /// <summary>
    /// An arc of radius <paramref name="half"/> around <paramref name="center"/> from direction <paramref name="from"/> to
    /// <paramref name="to"/>, bulging towards <paramref name="through"/>, as a polyline within the tolerance.
    /// </summary>
    private static void Arc(PathBuilder builder, Vector2 center, Vector2 from, Vector2 to, Vector2 through, float half, float tolerance)
    {
        double a0 = Math.Atan2(from.Y, from.X);
        double sweep = Wrap(Math.Atan2(to.Y, to.X) - a0);
        double toMid = Wrap(Math.Atan2(through.Y, through.X) - a0);

        // Of the two ways round, take the one that passes the bulge direction.
        bool passes = Math.Sign(toMid) == Math.Sign(sweep) && Math.Abs(toMid) <= Math.Abs(sweep);
        if (!passes || Math.Abs(Math.Abs(sweep) - Math.PI) < 1e-6)
        {
            sweep = toMid >= 0 ? (2 * Math.PI) - Math.Abs(sweep) : -((2 * Math.PI) - Math.Abs(sweep));
            if (Math.Abs(Math.Abs(sweep) - Math.PI) < 1e-6)
            {
                sweep = toMid >= 0 ? Math.PI : -Math.PI;
            }
        }

        double step = half > tolerance ? 2 * Math.Acos(1 - (tolerance / half)) : Math.PI / 2;
        int n = Math.Max(1, (int)Math.Min(1024, Math.Ceiling(Math.Abs(sweep) / step)));
        for (int i = 1; i <= n; i++)
        {
            (double s, double c) = Math.SinCos(a0 + (sweep * i / n));
            builder.LineTo(center + new Vector2((float)(half * c), (float)(half * s)));
        }
    }

    /// <summary>The join at a point: the pen's own at corners, round where the point lies inside a curve.</summary>
    private static Pen JoinAt(Stroke stroke, bool smooth, float shortest) => new(smooth ? LineJoin.Round : stroke.Join, stroke.MiterLimit, shortest);

    /// <summary>The shorter of the two segments meeting at <paramref name="at"/>.</summary>
    private static float Shortest(List<Vector2> points, int before, int at, int after)
        => MathF.Min(Vector2.Distance(points[before], points[at]), Vector2.Distance(points[at], points[after]));

    /// <summary>Wraps an angle to (−π, π].</summary>
    private static double Wrap(double angle)
    {
        while (angle <= -Math.PI)
        {
            angle += 2 * Math.PI;
        }

        while (angle > Math.PI)
        {
            angle -= 2 * Math.PI;
        }

        return angle;
    }

    private static Vector2 Direction(Vector2 a, Vector2 b) => Vector2.Normalize(b - a);

    /// <summary>The normal (−dy, dx): on a y-down screen, the right-hand side of travel.</summary>
    private static Vector2 Normal(Vector2 direction) => new(-direction.Y, direction.X);

    /// <summary>The join settings at one corner.</summary>
    /// <param name="Join">The join.</param>
    /// <param name="MiterLimit">The miter limit.</param>
    /// <param name="Shortest">The shorter of the two segments meeting there.</param>
    private readonly record struct Pen(LineJoin Join, float MiterLimit, float Shortest);
}
