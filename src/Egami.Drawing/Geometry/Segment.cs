using System.Numerics;

namespace Egami.Drawing.Geometry;

/// <summary>
/// One line, quadratic or cubic Bézier piece of a contour in double precision: evaluation, derivatives, exact sub-curves by
/// blossoming (the polar form), and reversal. Measuring, dashing, effects and boolean operations all work on these.
/// </summary>
internal readonly record struct Segment
{
    private Segment(PathVerb kind, D2 p0, D2 p1, D2 p2, D2 p3)
    {
        Kind = kind;
        P0 = p0;
        P1 = p1;
        P2 = p2;
        P3 = p3;
    }

    /// <summary>Gets <see cref="PathVerb.Line"/>, <see cref="PathVerb.Quad"/> or <see cref="PathVerb.Cubic"/>.</summary>
    public PathVerb Kind { get; }

    /// <summary>Gets the start.</summary>
    public D2 P0 { get; }

    /// <summary>Gets the end of a line, else the first control point.</summary>
    public D2 P1 { get; }

    /// <summary>Gets the end of a quadratic or the second control point of a cubic.</summary>
    public D2 P2 { get; }

    /// <summary>Gets the end of a cubic.</summary>
    public D2 P3 { get; }

    /// <summary>Gets the end point.</summary>
    public D2 End => Kind switch
    {
        PathVerb.Line => P1,
        PathVerb.Quad => P2,
        _ => P3,
    };

    /// <summary>Gets the number of points after the start: 1, 2 or 3.</summary>
    public int Degree => Kind switch
    {
        PathVerb.Line => 1,
        PathVerb.Quad => 2,
        _ => 3,
    };

    public static Segment Line(D2 p0, D2 p1) => new(PathVerb.Line, p0, p1, p1, p1);

    public static Segment Quad(D2 p0, D2 p1, D2 p2) => new(PathVerb.Quad, p0, p1, p2, p2);

    public static Segment Cubic(D2 p0, D2 p1, D2 p2, D2 p3) => new(PathVerb.Cubic, p0, p1, p2, p3);

    /// <summary>Returns the control point <paramref name="i"/> (0 is the start, <see cref="Degree"/> the end).</summary>
    public D2 this[int i] => i switch
    {
        0 => P0,
        1 => P1,
        2 => P2,
        _ => P3,
    };

    public D2 Point(double t)
    {
        double u = 1 - t;
        return Kind switch
        {
            PathVerb.Line => D2.Lerp(P0, P1, t),
            PathVerb.Quad => (u * u * P0) + (2 * u * t * P1) + (t * t * P2),
            _ => (u * u * u * P0) + (3 * u * u * t * P1) + (3 * u * t * t * P2) + (t * t * t * P3),
        };
    }

    /// <summary>Returns the derivative with respect to t.</summary>
    public D2 Derivative(double t)
    {
        double u = 1 - t;
        return Kind switch
        {
            PathVerb.Line => P1 - P0,
            PathVerb.Quad => 2 * ((u * (P1 - P0)) + (t * (P2 - P1))),
            _ => 3 * ((u * u * (P1 - P0)) + (2 * u * t * (P2 - P1)) + (t * t * (P3 - P2))),
        };
    }

    /// <summary>Returns the second derivative with respect to t.</summary>
    public D2 SecondDerivative(double t) => Kind switch
    {
        PathVerb.Line => default,
        PathVerb.Quad => 2 * (P2 - (2 * P1) + P0),
        _ => 6 * (((1 - t) * (P2 - (2 * P1) + P0)) + (t * (P3 - (2 * P2) + P1))),
    };

    /// <summary>
    /// Returns the direction of travel at <paramref name="t"/>, unit length. Where the derivative vanishes (a control point on an
    /// end point, or a cusp) the direction is the limit from inside the curve, and failing that the chord's.
    /// </summary>
    public D2 Tangent(double t)
    {
        D2 d = Derivative(t);
        double scale = Math.Max(1e-300, Hull());
        if (d.Length > scale * 1e-12)
        {
            return d.Normalized();
        }

        // At a vanishing derivative the curve leaves along the second (or third) difference of its control points.
        if (Kind != PathVerb.Line)
        {
            D2 inward = t < 0.5 ? Derivative(t + 1e-6) : Derivative(t - 1e-6);
            if (inward.Length > scale * 1e-15)
            {
                return inward.Normalized();
            }
        }

        D2 chord = End - P0;
        return chord.LengthSquared > 0 ? chord.Normalized() : new D2(1, 0);
    }

    /// <summary>Returns the piece between <paramref name="t0"/> and <paramref name="t1"/> (which may be reversed), exactly, by blossoming.</summary>
    public Segment Sub(double t0, double t1)
    {
        switch (Kind)
        {
            case PathVerb.Line:
                return Line(t0 == 0 ? P0 : t0 == 1 ? P1 : Point(t0), t1 == 0 ? P0 : t1 == 1 ? P1 : Point(t1));
            case PathVerb.Quad:
                return Quad(t0 == 0 ? P0 : t0 == 1 ? P2 : Blossom2(t0, t0), Blossom2(t0, t1), t1 == 0 ? P0 : t1 == 1 ? P2 : Blossom2(t1, t1));
            default:
                return Cubic(
                    t0 == 0 ? P0 : t0 == 1 ? P3 : Blossom3(t0, t0, t0),
                    Blossom3(t0, t0, t1),
                    Blossom3(t0, t1, t1),
                    t1 == 0 ? P0 : t1 == 1 ? P3 : Blossom3(t1, t1, t1));
        }
    }

    /// <summary>Returns the same curve run backwards.</summary>
    public Segment Reversed() => Kind switch
    {
        PathVerb.Line => Line(P1, P0),
        PathVerb.Quad => Quad(P2, P1, P0),
        _ => Cubic(P3, P2, P1, P0),
    };

    /// <summary>Returns the segment with its start and end replaced, keeping the control points (for snapping ends together).</summary>
    public Segment WithEnds(D2 start, D2 end) => Kind switch
    {
        PathVerb.Line => Line(start, end),
        PathVerb.Quad => Quad(start, P1, end),
        _ => Cubic(start, P1, P2, end),
    };

    /// <summary>Returns the largest coordinate magnitude of the control points, a scale for tolerances.</summary>
    public double Hull()
    {
        double m = Math.Max(Math.Max(Math.Abs(P0.X), Math.Abs(P0.Y)), Math.Max(Math.Abs(P1.X), Math.Abs(P1.Y)));
        if (Kind != PathVerb.Line)
        {
            m = Math.Max(m, Math.Max(Math.Abs(P2.X), Math.Abs(P2.Y)));
        }

        if (Kind == PathVerb.Cubic)
        {
            m = Math.Max(m, Math.Max(Math.Abs(P3.X), Math.Abs(P3.Y)));
        }

        return m;
    }

    /// <summary>Returns the bounds of the control points, which contain the curve.</summary>
    public (D2 Min, D2 Max) ControlBounds()
    {
        D2 min = new(Math.Min(P0.X, P1.X), Math.Min(P0.Y, P1.Y)), max = new(Math.Max(P0.X, P1.X), Math.Max(P0.Y, P1.Y));
        if (Kind != PathVerb.Line)
        {
            min = new D2(Math.Min(min.X, P2.X), Math.Min(min.Y, P2.Y));
            max = new D2(Math.Max(max.X, P2.X), Math.Max(max.Y, P2.Y));
        }

        if (Kind == PathVerb.Cubic)
        {
            min = new D2(Math.Min(min.X, P3.X), Math.Min(min.Y, P3.Y));
            max = new D2(Math.Max(max.X, P3.X), Math.Max(max.Y, P3.Y));
        }

        return (min, max);
    }

    /// <summary>Returns whether every control point lies within <paramref name="tolerance"/> of the chord, so the piece is straight.</summary>
    public bool IsFlat(double tolerance)
    {
        if (Kind == PathVerb.Line)
        {
            return true;
        }

        D2 chord = End - P0;
        double length = chord.Length;
        if (length == 0)
        {
            return D2.Distance(P1, P0) <= tolerance && D2.Distance(P2, P0) <= tolerance;
        }

        D2 p0 = P0, end = End;
        bool Near(D2 p) => Math.Abs(D2.Cross(chord, p - p0)) / length <= tolerance
            && D2.Dot(chord, p - p0) >= -tolerance * length && D2.Dot(chord, p - end) <= tolerance * length;
        return Near(P1) && (Kind == PathVerb.Quad || Near(P2));
    }

    /// <summary>Adds the segment after its start to a builder, as floats.</summary>
    public void AddTo(PathBuilder builder)
    {
        switch (Kind)
        {
            case PathVerb.Line:
                builder.LineTo(P1.ToVector2());
                break;
            case PathVerb.Quad:
                builder.QuadTo(P1.ToVector2(), P2.ToVector2());
                break;
            default:
                builder.CubicTo(P1.ToVector2(), P2.ToVector2(), P3.ToVector2());
                break;
        }
    }

    /// <summary>Reads every segment of a path, contour by contour, with each contour's closing line when it is closed.</summary>
    /// <param name="path">The path.</param>
    /// <param name="forceClosed">Whether to close open contours too.</param>
    /// <param name="onContour">
    /// Called with each contour's segments, its start, whether it is closed, and whether its last segment is the closing line added
    /// here; the list is reused.
    /// </param>
    public static void ReadContours(Path path, bool forceClosed, Action<List<Segment>, Vector2, bool, bool> onContour)
    {
        List<Segment> segments = [];
        ReadOnlySpan<Vector2> points = path.Points;
        int p = 0;
        Vector2 start = default, last = default;
        bool open = false, closed = false;
        foreach (PathVerb verb in path.Verbs)
        {
            switch (verb)
            {
                case PathVerb.Move:
                    Flush();
                    start = last = points[p++];
                    open = true;
                    closed = false;
                    break;
                case PathVerb.Line:
                    segments.Add(Line(last, points[p]));
                    last = points[p++];
                    break;
                case PathVerb.Quad:
                    segments.Add(Quad(last, points[p], points[p + 1]));
                    last = points[p + 1];
                    p += 2;
                    break;
                case PathVerb.Cubic:
                    segments.Add(Cubic(last, points[p], points[p + 1], points[p + 2]));
                    last = points[p + 2];
                    p += 3;
                    break;
                default:
                    closed = true;
                    Flush();
                    last = start;
                    break;
            }
        }

        Flush();

        void Flush()
        {
            if (!open)
            {
                return;
            }

            bool isClosed = closed || forceClosed;
            bool added = isClosed && last != start;
            if (added)
            {
                segments.Add(Line(last, start));
            }

            onContour(segments, start, isClosed, added);
            segments.Clear();
            open = false;
        }
    }

    private D2 Blossom2(double a, double b)
    {
        D2 q0 = D2.Lerp(P0, P1, a), q1 = D2.Lerp(P1, P2, a);
        return D2.Lerp(q0, q1, b);
    }

    private D2 Blossom3(double a, double b, double c)
    {
        D2 q0 = D2.Lerp(P0, P1, a), q1 = D2.Lerp(P1, P2, a), q2 = D2.Lerp(P2, P3, a);
        D2 r0 = D2.Lerp(q0, q1, b), r1 = D2.Lerp(q1, q2, b);
        return D2.Lerp(r0, r1, c);
    }
}
