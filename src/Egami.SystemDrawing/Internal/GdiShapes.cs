using System.Numerics;

using Egami.GdiPlus;

namespace Egami.SystemDrawing.Internal;

/// <summary>
/// GDI+'s shapes as points and point types: rectangles, elliptical arcs as cubic Beziers of at most 90 degrees (angles measured
/// clockwise on the ellipse itself, not on its circle), pies, polygons, Beziers and cardinal splines with tension.
/// </summary>
internal sealed class GdiShapes
{
    private readonly List<Vector2> _points = [];
    private readonly List<byte> _types = [];

    public int Count => _points.Count;

    public ReadOnlySpan<Vector2> Points => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_points);

    public ReadOnlySpan<byte> Types => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_types);

    /// <summary>Builds an Egami path from the points, filled by a rule.</summary>
    public Egami.Drawing.Path ToPath(Egami.Drawing.FillRule rule = Egami.Drawing.FillRule.EvenOdd) =>
        _points.Count == 0 ? Egami.Drawing.Path.Empty : GdiPlusData.BuildPath(Points, Types, rule);

    /// <summary>Starts a new figure at the next point.</summary>
    public void Start(Vector2 p) => Add(p, GdiPlusData.PointStart);

    public void Line(Vector2 p) => Add(p, GdiPlusData.PointLine);

    public void Bezier(Vector2 c1, Vector2 c2, Vector2 end)
    {
        Add(c1, GdiPlusData.PointBezier);
        Add(c2, GdiPlusData.PointBezier);
        Add(end, GdiPlusData.PointBezier);
    }

    /// <summary>Closes the figure: the close flag on its last point.</summary>
    public void Close()
    {
        if (_types.Count > 0)
        {
            _types[^1] |= GdiPlusData.PointClose;
        }
    }

    public void Add(Vector2 p, byte type)
    {
        _points.Add(p);
        _types.Add(type);
    }

    /// <summary>A rectangle: top-left, top-right, bottom-right, bottom-left, closed.</summary>
    public void Rectangle(float x, float y, float width, float height)
    {
        Start(new Vector2(x, y));
        Line(new Vector2(x + width, y));
        Line(new Vector2(x + width, y + height));
        Line(new Vector2(x, y + height));
        Close();
    }

    /// <summary>
    /// A rectangle with rounded corners: a quarter of a <paramref name="cornerWidth"/> by <paramref name="cornerHeight"/> ellipse at
    /// each corner, clockwise from the top-right one, closed; a plain rectangle when either is zero.
    /// </summary>
    public void RoundedRectangle(float x, float y, float width, float height, float cornerWidth, float cornerHeight)
    {
        cornerWidth = Math.Min(MathF.Abs(cornerWidth), MathF.Abs(width));
        cornerHeight = Math.Min(MathF.Abs(cornerHeight), MathF.Abs(height));
        if (cornerWidth == 0 || cornerHeight == 0)
        {
            Rectangle(x, y, width, height);
            return;
        }

        Arc(x + width - cornerWidth, y, cornerWidth, cornerHeight, 270, 90, start: true);
        Arc(x + width - cornerWidth, y + height - cornerHeight, cornerWidth, cornerHeight, 0, 90, start: false);
        Arc(x, y + height - cornerHeight, cornerWidth, cornerHeight, 90, 90, start: false);
        Arc(x, y, cornerWidth, cornerHeight, 180, 90, start: false);
        Close();
    }

    /// <summary>A whole ellipse: four quarter arcs from the right-hand point, clockwise, closed.</summary>
    public void Ellipse(float x, float y, float width, float height)
    {
        Arc(x, y, width, height, 0, 360, start: true);
        Close();
    }

    /// <summary>
    /// An arc of the ellipse in a rectangle, from <paramref name="startAngle"/> through <paramref name="sweepAngle"/> degrees,
    /// clockwise for positive sweeps, as GDI+ builds it: angles measured on the ellipse, cubic Beziers of 90 degrees of the
    /// ellipse's own (parametric) angle from the start, the remainder last, nothing for a zero sweep. <paramref name="start"/>
    /// begins a new figure; otherwise the arc's first point joins the figure with a line.
    /// </summary>
    public void Arc(float x, float y, float width, float height, float startAngle, float sweepAngle, bool start)
    {
        if (sweepAngle == 0)
        {
            return;
        }

        float rx = width / 2, ry = height / 2;
        Vector2 center = new(x + rx, y + ry);
        sweepAngle = Math.Clamp(sweepAngle, -360, 360);
        double from = Parametric(startAngle, rx, ry);
        double to = Parametric(startAngle + sweepAngle, rx, ry);
        if (sweepAngle > 0 && to < from)
        {
            to += 2 * Math.PI;
        }
        else if (sweepAngle < 0 && to > from)
        {
            to -= 2 * Math.PI;
        }

        if (MathF.Abs(sweepAngle) >= 360)
        {
            to = from + (Math.Sign(sweepAngle) * 2 * Math.PI);
        }

        Vector2 Point(double t) => center + new Vector2((float)(rx * Math.Cos(t)), (float)(ry * Math.Sin(t)));
        Vector2 first = Point(from);
        if (start || _points.Count == 0)
        {
            Start(first);
        }
        else if (first != _points[^1])
        {
            Line(first);
        }

        double sweep = to - from, quarter = Math.Sign(sweep) * Math.PI / 2;
        int segments = Math.Max(1, (int)Math.Ceiling((Math.Abs(sweep) / (Math.PI / 2)) - 1e-6));
        for (int i = 0; i < segments; i++)
        {
            double a = from + (i * quarter), b = i + 1 == segments ? to : a + quarter;
            double k = 4.0 / 3 * Math.Tan((b - a) / 4);
            Vector2 p0 = Point(a), p3 = Point(b);
            Vector2 d0 = new((float)(-rx * Math.Sin(a)), (float)(ry * Math.Cos(a)));
            Vector2 d3 = new((float)(-rx * Math.Sin(b)), (float)(ry * Math.Cos(b)));
            Bezier(p0 + (d0 * (float)k), p3 - (d3 * (float)k), p3);
        }
    }

    /// <summary>A pie: the center, a line to the arc's start, the arc, closed back to the center.</summary>
    public void Pie(float x, float y, float width, float height, float startAngle, float sweepAngle)
    {
        Start(new Vector2(x + (width / 2), y + (height / 2)));
        Arc(x, y, width, height, startAngle, sweepAngle, start: false);
        Close();
    }

    /// <summary>A polyline, closed when asked.</summary>
    public void Lines(ReadOnlySpan<Vector2> points, bool close)
    {
        for (int i = 0; i < points.Length; i++)
        {
            if (i == 0)
            {
                Start(points[0]);
            }
            else
            {
                Line(points[i]);
            }
        }

        if (close)
        {
            Close();
        }
    }

    /// <summary>Joined cubic Beziers: a start point, then three points each.</summary>
    public void Beziers(ReadOnlySpan<Vector2> points)
    {
        if (points.Length < 4 || (points.Length - 1) % 3 != 0)
        {
            throw GdiErrors.InvalidParameter();
        }

        Start(points[0]);
        for (int i = 1; i + 2 < points.Length; i += 3)
        {
            Bezier(points[i], points[i + 1], points[i + 2]);
        }
    }

    /// <summary>
    /// A cardinal spline through points as cubic Beziers: the control points of the segment from p[i] to p[i + 1] lie along
    /// p[i + 1] - p[i - 1] and p[i + 2] - p[i], scaled by tension / 3 (0.5, the default, is a Catmull-Rom spline). An open curve
    /// repeats its end points; a closed one wraps around and closes.
    /// </summary>
    public void Curve(ReadOnlySpan<Vector2> points, int offset, int segments, float tension, bool closed)
    {
        int n = points.Length;
        if (n < 2 || (closed && n < 3))
        {
            throw GdiErrors.InvalidParameter();
        }

        float t = tension / 3;
        if (closed)
        {
            offset = 0;
            segments = n;
        }

        Start(At(points, offset, closed));
        for (int i = offset; i < offset + segments; i++)
        {
            Vector2 p0 = At(points, i - 1, closed), p1 = At(points, i, closed), p2 = At(points, i + 1, closed), p3 = At(points, i + 2, closed);
            Bezier(p1 + ((p2 - p0) * t), p2 - ((p3 - p1) * t), p2);
        }

        if (closed)
        {
            Close();
        }
    }

    /// <summary>A point of a curve: wrapped around when closed, the end points repeated when open.</summary>
    private static Vector2 At(ReadOnlySpan<Vector2> points, int i, bool closed) =>
        closed ? points[((i % points.Length) + points.Length) % points.Length] : points[Math.Clamp(i, 0, points.Length - 1)];

    /// <summary>The parametric angle of the point on the ellipse at a geometric angle, in radians.</summary>
    private static double Parametric(float degrees, float rx, float ry)
    {
        double a = degrees * Math.PI / 180;
        if (rx == 0 || ry == 0)
        {
            return a;
        }

        // Keep the same number of whole turns so sweeps of more than a turn stay continuous.
        double turns = Math.Floor(a / (2 * Math.PI));
        double r = a - (turns * 2 * Math.PI);
        double t = Math.Atan2(Math.Sin(r) / ry, Math.Cos(r) / rx);
        if (t < 0)
        {
            t += 2 * Math.PI;
        }

        // The parametric angle lies in the same quadrant as the geometric one; rounding near a whole turn must not wrap it.
        if (t - r > Math.PI)
        {
            t -= 2 * Math.PI;
        }
        else if (r - t > Math.PI)
        {
            t += 2 * Math.PI;
        }

        return t + (turns * 2 * Math.PI);
    }
}
