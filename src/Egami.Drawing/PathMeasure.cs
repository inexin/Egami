using System.Numerics;

using Egami.Drawing.Geometry;

namespace Egami.Drawing;

/// <summary>
/// Measures a path: the length of each contour, and the point, direction and pieces at any distance along it. Lengths are exact to
/// about 1e-4 units, curves included: each curve's length is integrated by adaptive Gauss–Legendre quadrature, and a distance is
/// turned back into a point on the curve by Newton steps, so long curves do not gather the errors of a flattened outline.
/// Contours of zero length are left out, as Skia's <c>SkContourMeasureIter</c> leaves them. Measuring is done once, here;
/// the queries are cheap and safe from many threads.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="measure"/>
/// </example>
public sealed class PathMeasure
{
    /// <summary>Initializes a new instance of the <see cref="PathMeasure"/> class.</summary>
    /// <param name="path">The path.</param>
    /// <param name="forceClosed">Whether to measure open contours as if closed, with the line back to their start.</param>
    public PathMeasure(Path path, bool forceClosed = false)
    {
        ArgumentNullException.ThrowIfNull(path);
        List<ContourMeasure> contours = [];
        Segment.ReadContours(path, forceClosed, (segments, _, closed, closingLine) =>
        {
            ContourMeasure? contour = ContourMeasure.Create(segments, closed, closingLine);
            if (contour is not null)
            {
                contours.Add(contour);
            }
        });

        Contours = contours;
        double total = 0;
        foreach (ContourMeasure c in contours)
        {
            total += c.ExactLength;
        }

        TotalLength = (float)total;
    }

    /// <summary>Gets the contours that have a length, in the path's order.</summary>
    public IReadOnlyList<ContourMeasure> Contours { get; }

    /// <summary>Gets the length of all the contours together.</summary>
    public float TotalLength { get; }
}

/// <summary>
/// One measured contour of a <see cref="PathMeasure"/>. Distances run from 0 at the contour's start to <see cref="Length"/> at its
/// end; distances outside are clamped to them, as in Skia.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="measure"/>
/// </example>
public sealed class ContourMeasure
{
    // Gauss–Legendre nodes and weights on [−1, 1], five points: exact for polynomials up to degree 9.
    private static readonly double[] Nodes = [0, -0.5384693101056831, 0.5384693101056831, -0.9061798459386640, 0.9061798459386640];
    private static readonly double[] Weights = [0.5688888888888889, 0.4786286704993665, 0.4786286704993665, 0.2369268850561891, 0.2369268850561891];

    private readonly Segment[] _segments;

    // Where each segment starts along the contour; one more entry at the end for the total.
    private readonly double[] _starts;

    // For each segment, the parameters and lengths (from the segment's start) where its quadrature intervals meet; lines have none.
    private readonly double[][] _ts;
    private readonly double[][] _lengths;

    // Whether the last segment is the line a close adds, which a closed whole leaves to the close.
    private readonly bool _closingLine;

    private ContourMeasure(Segment[] segments, double[] starts, double[][] ts, double[][] lengths, bool closed, bool closingLine)
    {
        _closingLine = closingLine;
        _segments = segments;
        _starts = starts;
        _ts = ts;
        _lengths = lengths;
        IsClosed = closed;
        ExactLength = starts[^1];
        Length = (float)ExactLength;
    }

    /// <summary>Gets the contour's length.</summary>
    public float Length { get; }

    /// <summary>Gets a value indicating whether the contour is closed, so its end meets its start.</summary>
    public bool IsClosed { get; }

    internal double ExactLength { get; }

    /// <summary>Returns the point at a distance along the contour.</summary>
    /// <param name="distance">The distance from the start, clamped to the contour.</param>
    /// <returns>The point.</returns>
    public Vector2 GetPosition(float distance)
    {
        (int i, double t) = Locate(distance);
        return _segments[i].Point(t).ToVector2();
    }

    /// <summary>Returns the direction of travel at a distance along the contour, as a unit vector.</summary>
    /// <param name="distance">The distance from the start, clamped to the contour.</param>
    /// <returns>The direction; where a curve's direction is undefined (a cusp), the limit from inside the curve.</returns>
    public Vector2 GetTangent(float distance)
    {
        (int i, double t) = Locate(distance);
        return _segments[i].Tangent(t).ToVector2();
    }

    /// <summary>
    /// Returns the transform that puts a shape on the contour at a distance: its x axis along the direction of travel and its origin
    /// at the point, so a shape drawn along +x follows the path.
    /// </summary>
    /// <param name="distance">The distance from the start, clamped to the contour.</param>
    /// <returns>A rotation and translation.</returns>
    public Matrix3x2 GetMatrix(float distance)
    {
        (int i, double t) = Locate(distance);
        Vector2 p = _segments[i].Point(t).ToVector2();
        Vector2 d = _segments[i].Tangent(t).ToVector2();
        return new Matrix3x2(d.X, d.Y, -d.Y, d.X, p.X, p.Y);
    }

    /// <summary>
    /// Returns the piece of the contour between two distances, as a path of one open contour that keeps the contour's lines,
    /// quadratics and cubics: curves cut at the ends are split exactly, so the piece exports as cleanly as the whole. The whole of
    /// a closed contour comes back closed.
    /// </summary>
    /// <param name="start">Where the piece starts; clamped to the contour.</param>
    /// <param name="end">Where the piece ends; clamped to the contour.</param>
    /// <returns>The piece, or an empty path when <paramref name="end"/> is not after <paramref name="start"/>.</returns>
    public Path GetSegment(float start, float end)
    {
        PathBuilder builder = new();
        AddSegment(builder, start, end);
        return builder.ToPath();
    }

    /// <summary>Adds the piece between two distances to a builder, as <see cref="GetSegment"/> describes.</summary>
    /// <param name="builder">The builder.</param>
    /// <param name="start">Where the piece starts; clamped to the contour.</param>
    /// <param name="end">Where the piece ends; clamped to the contour.</param>
    /// <param name="startWithMove">
    /// Whether the piece starts a new contour; otherwise a line joins it to the builder's current point, to chain pieces into one
    /// contour.
    /// </param>
    public void AddSegment(PathBuilder builder, float start, float end, bool startWithMove = true)
    {
        ArgumentNullException.ThrowIfNull(builder);
        AddSegment(builder, (double)start, end, startWithMove);
    }

    internal void AddSegment(PathBuilder builder, double start, double end, bool startWithMove)
    {
        start = Math.Max(0, start);
        end = Math.Min(ExactLength, end);
        if (!(end > start))
        {
            return;
        }

        (int i0, double t0) = Locate(start);
        (int i1, double t1) = Locate(end);
        if (i1 > i0 && t1 == 0)
        {
            // Ending exactly where a segment starts: end at the previous one's end instead of adding an empty piece.
            i1--;
            t1 = 1;
        }

        Vector2 first = _segments[i0].Point(t0).ToVector2();
        if (startWithMove)
        {
            builder.MoveTo(first);
        }
        else if (builder.CurrentPoint != first || builder.IsEmpty)
        {
            builder.LineTo(first);
        }

        bool whole = IsClosed && start <= 0 && end >= ExactLength && startWithMove;
        if (whole && _closingLine && i1 > i0)
        {
            i1--;
        }

        for (int i = i0; i <= i1; i++)
        {
            double a = i == i0 ? t0 : 0, b = i == i1 ? t1 : 1;
            if (b > a)
            {
                (a == 0 && b == 1 ? _segments[i] : _segments[i].Sub(a, b)).AddTo(builder);
            }
        }

        if (whole)
        {
            builder.Close();
        }
    }

    /// <summary>Measures one contour's segments; returns <see langword="null"/> when it has no length.</summary>
    internal static ContourMeasure? Create(List<Segment> segments, bool closed, bool closingLine)
    {
        // A closing line of zero length is dropped below, and then the last segment kept is the contour's own.
        closingLine &= segments.Count > 0 && segments[^1].Kind == PathVerb.Line && D2.Distance(segments[^1].P0, segments[^1].P1) > 0;
        List<Segment> kept = [];
        List<double> starts = [0];
        List<double[]> ts = [], lengths = [];
        List<double> tList = [], lList = [];
        double total = 0;
        foreach (Segment s in segments)
        {
            double length;
            if (s.Kind == PathVerb.Line)
            {
                length = D2.Distance(s.P0, s.P1);
                if (!(length > 0))
                {
                    continue;
                }

                ts.Add([]);
                lengths.Add([]);
            }
            else
            {
                tList.Clear();
                lList.Clear();
                tList.Add(0);
                lList.Add(0);
                length = Integrate(s, tList, lList);
                if (!(length > 0))
                {
                    continue;
                }

                ts.Add([.. tList]);
                lengths.Add([.. lList]);
            }

            kept.Add(s);
            total += length;
            starts.Add(total);
        }

        return kept.Count == 0 || !(total > 0) || !double.IsFinite(total) ? null : new ContourMeasure([.. kept], [.. starts], [.. ts], [.. lengths], closed, closingLine);
    }

    /// <summary>Returns the segment and parameter at a distance along the contour.</summary>
    internal (int Index, double T) Locate(double distance)
    {
        if (!(distance > 0))
        {
            return (0, 0);
        }

        if (distance >= ExactLength)
        {
            return (_segments.Length - 1, 1);
        }

        // The last segment whose start is at or before the distance.
        int lo = 0, hi = _segments.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_starts[mid] <= distance)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        Segment s = _segments[lo];
        double local = distance - _starts[lo];
        double length = _starts[lo + 1] - _starts[lo];
        if (s.Kind == PathVerb.Line)
        {
            return (lo, Math.Clamp(local / length, 0, 1));
        }

        return (lo, Invert(s, _ts[lo], _lengths[lo], local));
    }

    /// <summary>
    /// Integrates the curve's speed from 0 to 1, splitting each interval until five-point Gauss–Legendre on it agrees with the sum of
    /// its halves; records the parameter and running length where accepted intervals meet.
    /// </summary>
    private static double Integrate(Segment s, List<double> ts, List<double> lengths)
    {
        // Start from quarters, so short wiggles inside one interval cannot hide from the comparison.
        double total = 0;
        for (int i = 0; i < 4; i++)
        {
            double a = i / 4.0, b = (i + 1) / 4.0;
            total = Adaptive(s, a, b, GaussLegendre(s, a, b), total, 0, ts, lengths);
        }

        return total;
    }

    private static double Adaptive(Segment s, double a, double b, double whole, double before, int depth, List<double> ts, List<double> lengths)
    {
        double m = (a + b) / 2;
        double left = GaussLegendre(s, a, m), right = GaussLegendre(s, m, b);
        double error = Math.Abs(left + right - whole);
        if (depth >= 30 || error <= Math.Max(1e-6 * (b - a), 1e-14 * Math.Abs(whole)) || !double.IsFinite(error))
        {
            double total = before + left + right;
            ts.Add(m);
            lengths.Add(before + left);
            ts.Add(b);
            lengths.Add(total);
            return total;
        }

        double afterLeft = Adaptive(s, a, m, left, before, depth + 1, ts, lengths);
        return Adaptive(s, m, b, right, afterLeft, depth + 1, ts, lengths);
    }

    private static double GaussLegendre(Segment s, double a, double b)
    {
        double half = (b - a) / 2, mid = (a + b) / 2, sum = 0;
        for (int i = 0; i < Nodes.Length; i++)
        {
            sum += Weights[i] * s.Derivative(mid + (half * Nodes[i])).Length;
        }

        return sum * half;
    }

    /// <summary>Finds the parameter where the curve's length from its start equals <paramref name="target"/>: the interval by search, then Newton steps kept inside it.</summary>
    private static double Invert(Segment s, double[] ts, double[] lengths, double target)
    {
        int lo = 0, hi = ts.Length - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (lengths[mid] <= target)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        double a = ts[lo], b = ts[hi], la = lengths[lo], lb = lengths[hi];
        if (!(lb > la))
        {
            return a;
        }

        double t = a + ((b - a) * (target - la) / (lb - la));
        double low = a, high = b;
        for (int iteration = 0; iteration < 16; iteration++)
        {
            double f = la + GaussLegendre(s, a, t) - target;
            if (Math.Abs(f) <= 1e-9 * Math.Max(1, lb))
            {
                break;
            }

            if (f > 0)
            {
                high = t;
            }
            else
            {
                low = t;
            }

            double speed = s.Derivative(t).Length;
            double next = speed > 0 ? t - (f / speed) : double.NaN;
            t = next > low && next < high ? next : (low + high) / 2;
        }

        return Math.Clamp(t, 0, 1);
    }
}
