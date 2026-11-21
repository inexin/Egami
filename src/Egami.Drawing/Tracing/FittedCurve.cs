namespace Egami.Drawing.Tracing;

/// <summary>A point or vector in pixel coordinates, in doubles for fitting.</summary>
internal readonly record struct Vec(double X, double Y)
{
    internal double Length => Math.Sqrt((X * X) + (Y * Y));

    public static Vec operator +(Vec a, Vec b) => new(a.X + b.X, a.Y + b.Y);

    public static Vec operator -(Vec a, Vec b) => new(a.X - b.X, a.Y - b.Y);

    public static Vec operator -(Vec a) => new(-a.X, -a.Y);

    public static Vec operator *(Vec a, double s) => new(a.X * s, a.Y * s);

    public static Vec operator *(double s, Vec a) => new(a.X * s, a.Y * s);

    internal static double Dot(Vec a, Vec b) => (a.X * b.X) + (a.Y * b.Y);

    internal static double Cross(Vec a, Vec b) => (a.X * b.Y) - (a.Y * b.X);

    internal Vec Normalize()
    {
        double length = Length;
        return length > 1e-12 ? new Vec(X / length, Y / length) : default;
    }
}

/// <summary>One piece of a fitted curve: a line or a cubic Bézier to <see cref="End"/>.</summary>
internal readonly record struct CurveSegment(bool IsCubic, Vec C1, Vec C2, Vec End);

/// <summary>A fitted outline or chain: a start point and the lines and cubics that follow it.</summary>
internal sealed class FittedCurve
{
    internal FittedCurve(Vec start, List<CurveSegment> segments, bool closed)
    {
        Start = start;
        Segments = segments;
        Closed = closed;
    }

    internal Vec Start { get; }

    internal List<CurveSegment> Segments { get; }

    /// <summary>Gets whether the curve ends where it starts and is a whole outline.</summary>
    internal bool Closed { get; }

    internal Vec End => Segments.Count > 0 ? Segments[^1].End : Start;

    /// <summary>The same curve run the other way.</summary>
    internal FittedCurve Reversed()
    {
        List<CurveSegment> segments = new(Segments.Count);
        for (int i = Segments.Count - 1; i >= 0; i--)
        {
            CurveSegment s = Segments[i];
            Vec end = i > 0 ? Segments[i - 1].End : Start;
            segments.Add(new CurveSegment(s.IsCubic, s.C2, s.C1, end));
        }

        return new FittedCurve(End, segments, Closed);
    }

    /// <summary>Adds the segments to a path builder, starting a contour first when <paramref name="move"/> is set.</summary>
    internal void AddTo(PathBuilder builder, bool move)
    {
        if (move)
        {
            builder.MoveTo((float)Start.X, (float)Start.Y);
        }

        foreach (CurveSegment s in Segments)
        {
            if (s.IsCubic)
            {
                builder.CubicTo((float)s.C1.X, (float)s.C1.Y, (float)s.C2.X, (float)s.C2.Y, (float)s.End.X, (float)s.End.Y);
            }
            else
            {
                builder.LineTo((float)s.End.X, (float)s.End.Y);
            }
        }
    }
}
