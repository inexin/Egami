using System.Numerics;

using Egami.Drawing.Geometry;

namespace Egami.Drawing;

/// <summary>
/// Builds a <see cref="Path"/> one command at a time. Arcs, ovals and round corners become cubic curves as they are added, so a
/// path holds only lines, quadratics and cubics. A line or curve with no contour open starts one at the last contour's start (or
/// the origin), as SVG and Skia do. Building takes time in proportion to the points added; <see cref="ToPath"/> copies them, so the
/// builder can go on or be reused.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="paths"/>
/// </example>
public sealed class PathBuilder
{
    private readonly List<Vector2> _points = [];
    private readonly List<PathVerb> _verbs = [];
    private readonly List<Vector2> _arc = [];
    private Vector2 _contourStart;
    private bool _open;

    /// <summary>Initializes a new instance of the <see cref="PathBuilder"/> class, empty.</summary>
    public PathBuilder()
    {
    }

    /// <summary>Gets the point the next command starts from: the last point added, or the contour's start after <see cref="Close"/>.</summary>
    public Vector2 CurrentPoint { get; private set; }

    /// <summary>Gets a value indicating whether nothing has been added.</summary>
    public bool IsEmpty => _verbs.Count == 0;

    /// <summary>Starts a new contour.</summary>
    /// <param name="point">Where it starts.</param>
    /// <returns>This builder.</returns>
    public PathBuilder MoveTo(Vector2 point)
    {
        // Two moves in a row: the second replaces the first, which would draw nothing.
        if (_verbs.Count > 0 && _verbs[^1] == PathVerb.Move)
        {
            _points[^1] = point;
        }
        else
        {
            _verbs.Add(PathVerb.Move);
            _points.Add(point);
        }

        _contourStart = point;
        CurrentPoint = point;
        _open = true;
        return this;
    }

    /// <summary>Starts a new contour.</summary>
    /// <param name="x">Where it starts, x.</param>
    /// <param name="y">Where it starts, y.</param>
    /// <returns>This builder.</returns>
    public PathBuilder MoveTo(float x, float y) => MoveTo(new Vector2(x, y));

    /// <summary>Adds a straight line.</summary>
    /// <param name="point">Where it ends.</param>
    /// <returns>This builder.</returns>
    public PathBuilder LineTo(Vector2 point)
    {
        EnsureContour();
        _verbs.Add(PathVerb.Line);
        _points.Add(point);
        CurrentPoint = point;
        return this;
    }

    /// <summary>Adds a straight line.</summary>
    /// <param name="x">Where it ends, x.</param>
    /// <param name="y">Where it ends, y.</param>
    /// <returns>This builder.</returns>
    public PathBuilder LineTo(float x, float y) => LineTo(new Vector2(x, y));

    /// <summary>Adds a quadratic Bézier curve.</summary>
    /// <param name="control">The control point.</param>
    /// <param name="end">Where it ends.</param>
    /// <returns>This builder.</returns>
    public PathBuilder QuadTo(Vector2 control, Vector2 end)
    {
        EnsureContour();
        _verbs.Add(PathVerb.Quad);
        _points.Add(control);
        _points.Add(end);
        CurrentPoint = end;
        return this;
    }

    /// <summary>Adds a quadratic Bézier curve.</summary>
    /// <param name="controlX">The control point, x.</param>
    /// <param name="controlY">The control point, y.</param>
    /// <param name="x">Where it ends, x.</param>
    /// <param name="y">Where it ends, y.</param>
    /// <returns>This builder.</returns>
    public PathBuilder QuadTo(float controlX, float controlY, float x, float y) => QuadTo(new Vector2(controlX, controlY), new Vector2(x, y));

    /// <summary>Adds a cubic Bézier curve.</summary>
    /// <param name="control1">The first control point.</param>
    /// <param name="control2">The second control point.</param>
    /// <param name="end">Where it ends.</param>
    /// <returns>This builder.</returns>
    public PathBuilder CubicTo(Vector2 control1, Vector2 control2, Vector2 end)
    {
        EnsureContour();
        _verbs.Add(PathVerb.Cubic);
        _points.Add(control1);
        _points.Add(control2);
        _points.Add(end);
        CurrentPoint = end;
        return this;
    }

    /// <summary>Adds a cubic Bézier curve.</summary>
    /// <param name="control1X">The first control point, x.</param>
    /// <param name="control1Y">The first control point, y.</param>
    /// <param name="control2X">The second control point, x.</param>
    /// <param name="control2Y">The second control point, y.</param>
    /// <param name="x">Where it ends, x.</param>
    /// <param name="y">Where it ends, y.</param>
    /// <returns>This builder.</returns>
    public PathBuilder CubicTo(float control1X, float control1Y, float control2X, float control2Y, float x, float y)
        => CubicTo(new Vector2(control1X, control1Y), new Vector2(control2X, control2Y), new Vector2(x, y));

    /// <summary>
    /// Adds an elliptical arc to <paramref name="end"/> in SVG's endpoint form (the <c>A</c> command), following SVG 1.1's
    /// implementation notes: radii too small to reach the end point are scaled up until they do, a zero radius draws a straight
    /// line, and an end point equal to the current point adds nothing.
    /// </summary>
    /// <param name="radii">The ellipse's x and y radius; signs are ignored.</param>
    /// <param name="rotationDegrees">The ellipse's x axis, turned clockwise from the page's x axis.</param>
    /// <param name="largeArc">Whether to take the arc of more than 180 degrees.</param>
    /// <param name="clockwise">Whether the arc runs clockwise on screen (SVG's sweep flag of 1).</param>
    /// <param name="end">Where the arc ends.</param>
    /// <returns>This builder.</returns>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="paths"/>
    /// </example>
    public PathBuilder ArcTo(Vector2 radii, float rotationDegrees, bool largeArc, bool clockwise, Vector2 end)
    {
        EnsureContour();
        Vector2 start = CurrentPoint;
        if (start == end)
        {
            return this;
        }

        double rx = Math.Abs((double)radii.X), ry = Math.Abs((double)radii.Y);
        if (rx == 0 || ry == 0 || !double.IsFinite(rx) || !double.IsFinite(ry))
        {
            return LineTo(end);
        }

        // SVG 1.1 F.6.5: move to the ellipse's own frame, where the chord's midpoint is the origin.
        double phi = rotationDegrees * Math.PI / 180;
        (double sinPhi, double cosPhi) = Math.SinCos(phi);
        double hx = (start.X - (double)end.X) / 2, hy = (start.Y - (double)end.Y) / 2;
        double x1 = (cosPhi * hx) + (sinPhi * hy);
        double y1 = (-sinPhi * hx) + (cosPhi * hy);

        // F.6.6: radii that cannot span the chord are scaled up just enough.
        double lambda = ((x1 * x1) / (rx * rx)) + ((y1 * y1) / (ry * ry));
        if (lambda > 1)
        {
            double s = Math.Sqrt(lambda);
            rx *= s;
            ry *= s;
        }

        double numerator = (rx * rx * ry * ry) - (rx * rx * y1 * y1) - (ry * ry * x1 * x1);
        double denominator = (rx * rx * y1 * y1) + (ry * ry * x1 * x1);
        double root = denominator == 0 ? 0 : Math.Sqrt(Math.Max(0, numerator / denominator));
        if (largeArc == clockwise)
        {
            root = -root;
        }

        double cx1 = root * rx * y1 / ry;
        double cy1 = -root * ry * x1 / rx;
        double cx = (cosPhi * cx1) - (sinPhi * cy1) + ((start.X + (double)end.X) / 2);
        double cy = (sinPhi * cx1) + (cosPhi * cy1) + ((start.Y + (double)end.Y) / 2);
        double theta1 = Math.Atan2((y1 - cy1) / ry, (x1 - cx1) / rx);
        double theta2 = Math.Atan2((-y1 - cy1) / ry, (-x1 - cx1) / rx);
        double sweep = theta2 - theta1;
        if (clockwise && sweep < 0)
        {
            sweep += 2 * Math.PI;
        }
        else if (!clockwise && sweep > 0)
        {
            sweep -= 2 * Math.PI;
        }

        AppendArc(new Vector2((float)cx, (float)cy), new Vector2((float)rx, (float)ry), (float)phi, theta1, sweep, end);
        return this;
    }

    /// <summary>
    /// Adds a circular arc of <paramref name="radius"/> that turns from the line towards <paramref name="corner"/> onto the line from
    /// <paramref name="corner"/> to <paramref name="end"/>, touching both, with a straight line to where the arc starts: the rounded
    /// corner of HTML canvas's and Skia's <c>arcTo</c>. When the lines are parallel or the radius is 0, a line to the corner is added.
    /// </summary>
    /// <param name="corner">The corner the arc rounds.</param>
    /// <param name="end">A point on the second line; the arc ends on that line, not at this point.</param>
    /// <param name="radius">The arc's radius.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radius"/> is negative.</exception>
    public PathBuilder ArcTo(Vector2 corner, Vector2 end, float radius)
    {
        if (radius < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(radius), radius, "The radius must not be negative.");
        }

        EnsureContour();
        Vector2 start = CurrentPoint;
        Vector2 a = start - corner, b = end - corner;
        double la = a.Length(), lb = b.Length();
        double cross = ((double)a.X * b.Y) - ((double)a.Y * b.X);
        if (radius == 0 || la == 0 || lb == 0 || Math.Abs(cross) <= 1e-12 * la * lb)
        {
            return LineTo(corner);
        }

        double cos = Math.Clamp(((double)a.X * b.X + ((double)a.Y * b.Y)) / (la * lb), -1, 1);
        double half = Math.Acos(cos) / 2;
        double distance = radius / Math.Tan(half);
        Vector2 ua = a / (float)la, ub = b / (float)lb;
        Vector2 t1 = corner + (ua * (float)distance), t2 = corner + (ub * (float)distance);
        Vector2 bisector = Vector2.Normalize(ua + ub);
        Vector2 center = corner + (bisector * (float)(radius / Math.Sin(half)));
        LineTo(t1);
        double startAngle = Math.Atan2(t1.Y - center.Y, t1.X - center.X);
        double endAngle = Math.Atan2(t2.Y - center.Y, t2.X - center.X);
        double sweep = endAngle - startAngle;
        if (sweep > Math.PI)
        {
            sweep -= 2 * Math.PI;
        }
        else if (sweep < -Math.PI)
        {
            sweep += 2 * Math.PI;
        }

        AppendArc(center, new Vector2(radius), 0, startAngle, sweep, t2);
        return this;
    }

    /// <summary>Closes the contour with a straight line back to its start. The next command starts from there.</summary>
    /// <returns>This builder.</returns>
    public PathBuilder Close()
    {
        if (_open && _verbs.Count > 0 && _verbs[^1] != PathVerb.Close)
        {
            _verbs.Add(PathVerb.Close);
        }

        _open = false;
        CurrentPoint = _contourStart;
        return this;
    }

    /// <summary>Starts a new contour at an offset from the current point.</summary>
    /// <param name="delta">The offset.</param>
    /// <returns>This builder.</returns>
    public PathBuilder RelativeMoveTo(Vector2 delta) => MoveTo(CurrentPoint + delta);

    /// <summary>Adds a straight line to an offset from the current point.</summary>
    /// <param name="delta">The offset of the end.</param>
    /// <returns>This builder.</returns>
    public PathBuilder RelativeLineTo(Vector2 delta) => LineTo(CurrentPoint + delta);

    /// <summary>Adds a quadratic curve whose points are offsets from the current point.</summary>
    /// <param name="control">The control point's offset.</param>
    /// <param name="end">The end's offset.</param>
    /// <returns>This builder.</returns>
    public PathBuilder RelativeQuadTo(Vector2 control, Vector2 end)
    {
        Vector2 p = CurrentPoint;
        return QuadTo(p + control, p + end);
    }

    /// <summary>Adds a cubic curve whose points are offsets from the current point.</summary>
    /// <param name="control1">The first control point's offset.</param>
    /// <param name="control2">The second control point's offset.</param>
    /// <param name="end">The end's offset.</param>
    /// <returns>This builder.</returns>
    public PathBuilder RelativeCubicTo(Vector2 control1, Vector2 control2, Vector2 end)
    {
        Vector2 p = CurrentPoint;
        return CubicTo(p + control1, p + control2, p + end);
    }

    /// <summary>Adds an SVG endpoint arc whose end is an offset from the current point (SVG's <c>a</c> command).</summary>
    /// <param name="radii">The ellipse's x and y radius.</param>
    /// <param name="rotationDegrees">The ellipse's x axis, turned clockwise.</param>
    /// <param name="largeArc">Whether to take the arc of more than 180 degrees.</param>
    /// <param name="clockwise">Whether the arc runs clockwise on screen.</param>
    /// <param name="end">The end's offset.</param>
    /// <returns>This builder.</returns>
    public PathBuilder RelativeArcTo(Vector2 radii, float rotationDegrees, bool largeArc, bool clockwise, Vector2 end)
        => ArcTo(radii, rotationDegrees, largeArc, clockwise, CurrentPoint + end);

    /// <summary>Adds a rectangle as a closed contour of its own, starting at the top-left corner.</summary>
    /// <param name="rect">The rectangle.</param>
    /// <param name="direction">Which way the outline runs.</param>
    /// <returns>This builder.</returns>
    public PathBuilder AddRect(RectF rect, PathDirection direction = PathDirection.Clockwise)
    {
        MoveTo(rect.Left, rect.Top);
        if (direction == PathDirection.Clockwise)
        {
            LineTo(rect.Right, rect.Top).LineTo(rect.Right, rect.Bottom).LineTo(rect.Left, rect.Bottom);
        }
        else
        {
            LineTo(rect.Left, rect.Bottom).LineTo(rect.Right, rect.Bottom).LineTo(rect.Right, rect.Top);
        }

        return Close();
    }

    /// <summary>Adds an ellipse filling a rectangle as a closed contour of four cubics, starting at the right end of its horizontal axis.</summary>
    /// <param name="oval">The rectangle the ellipse fills.</param>
    /// <param name="direction">Which way the outline runs.</param>
    /// <returns>This builder.</returns>
    public PathBuilder AddOval(RectF oval, PathDirection direction = PathDirection.Clockwise)
    {
        Vector2 c = oval.Center;
        Vector2 r = new(oval.Width / 2, oval.Height / 2);
        Vector2 k = r * Curves.QuarterKappa;
        float s = direction == PathDirection.Clockwise ? 1 : -1;
        MoveTo(c.X + r.X, c.Y);
        CubicTo(new Vector2(c.X + r.X, c.Y + (s * k.Y)), new Vector2(c.X + k.X, c.Y + (s * r.Y)), new Vector2(c.X, c.Y + (s * r.Y)));
        CubicTo(new Vector2(c.X - k.X, c.Y + (s * r.Y)), new Vector2(c.X - r.X, c.Y + (s * k.Y)), new Vector2(c.X - r.X, c.Y));
        CubicTo(new Vector2(c.X - r.X, c.Y - (s * k.Y)), new Vector2(c.X - k.X, c.Y - (s * r.Y)), new Vector2(c.X, c.Y - (s * r.Y)));
        CubicTo(new Vector2(c.X + k.X, c.Y - (s * r.Y)), new Vector2(c.X + r.X, c.Y - (s * k.Y)), new Vector2(c.X + r.X, c.Y));
        return Close();
    }

    /// <summary>Adds a circle as a closed contour.</summary>
    /// <param name="center">The center.</param>
    /// <param name="radius">The radius.</param>
    /// <param name="direction">Which way the outline runs.</param>
    /// <returns>This builder.</returns>
    public PathBuilder AddCircle(Vector2 center, float radius, PathDirection direction = PathDirection.Clockwise)
        => AddOval(new RectF(center.X - radius, center.Y - radius, 2 * radius, 2 * radius), direction);

    /// <summary>
    /// Adds a round rectangle as a closed contour: a line along each side and a quarter-ellipse cubic at each rounded corner,
    /// starting where the top-left corner's curve ends. Square corners add no curve.
    /// </summary>
    /// <param name="roundRect">The round rectangle.</param>
    /// <param name="direction">Which way the outline runs.</param>
    /// <returns>This builder.</returns>
    public PathBuilder AddRoundRect(RoundRect roundRect, PathDirection direction = PathDirection.Clockwise)
    {
        if (roundRect.Kind == RoundRectKind.Rect || roundRect.Kind == RoundRectKind.Empty)
        {
            return AddRect(roundRect.Bounds, direction);
        }

        if (roundRect.Kind == RoundRectKind.Oval)
        {
            return AddOval(roundRect.Bounds, direction);
        }

        RectF b = roundRect.Bounds;
        CornerRadii r = roundRect.Radii;

        // Corner i runs from its first point to its second, clockwise; each is (start, control1, control2, end).
        Span<Vector2> corners =
        [
            new(b.Left, b.Top + r.TopLeft.Y), new(b.Left, b.Top + r.TopLeft.Y - (r.TopLeft.Y * Curves.QuarterKappa)), new(b.Left + r.TopLeft.X - (r.TopLeft.X * Curves.QuarterKappa), b.Top), new(b.Left + r.TopLeft.X, b.Top),
            new(b.Right - r.TopRight.X, b.Top), new(b.Right - r.TopRight.X + (r.TopRight.X * Curves.QuarterKappa), b.Top), new(b.Right, b.Top + r.TopRight.Y - (r.TopRight.Y * Curves.QuarterKappa)), new(b.Right, b.Top + r.TopRight.Y),
            new(b.Right, b.Bottom - r.BottomRight.Y), new(b.Right, b.Bottom - r.BottomRight.Y + (r.BottomRight.Y * Curves.QuarterKappa)), new(b.Right - r.BottomRight.X + (r.BottomRight.X * Curves.QuarterKappa), b.Bottom), new(b.Right - r.BottomRight.X, b.Bottom),
            new(b.Left + r.BottomLeft.X, b.Bottom), new(b.Left + r.BottomLeft.X - (r.BottomLeft.X * Curves.QuarterKappa), b.Bottom), new(b.Left, b.Bottom - r.BottomLeft.Y + (r.BottomLeft.Y * Curves.QuarterKappa)), new(b.Left, b.Bottom - r.BottomLeft.Y),
        ];
        bool[] round = [r.TopLeft != Vector2.Zero, r.TopRight != Vector2.Zero, r.BottomRight != Vector2.Zero, r.BottomLeft != Vector2.Zero];
        if (direction == PathDirection.Clockwise)
        {
            MoveTo(corners[3]);
            for (int i = 1; i <= 4; i++)
            {
                int c = i % 4;
                LineTo(corners[4 * c]);
                if (round[c])
                {
                    CubicTo(corners[(4 * c) + 1], corners[(4 * c) + 2], corners[(4 * c) + 3]);
                }
            }
        }
        else
        {
            MoveTo(corners[3]);
            for (int i = 0; i < 4; i++)
            {
                int c = (4 - i) % 4;
                if (round[c])
                {
                    CubicTo(corners[(4 * c) + 2], corners[(4 * c) + 1], corners[4 * c]);
                }

                int previous = (c + 3) % 4;
                LineTo(corners[(4 * previous) + 3]);
            }
        }

        return Close();
    }

    /// <summary>
    /// Adds the arc of an ellipse as a new open contour: from <paramref name="startDegrees"/> through <paramref name="sweepDegrees"/>,
    /// where 0 is the right end of the horizontal axis and positive angles turn clockwise on screen. A sweep beyond ±360 degrees
    /// draws the whole ellipse once.
    /// </summary>
    /// <param name="oval">The rectangle the ellipse fills.</param>
    /// <param name="startDegrees">Where the arc starts.</param>
    /// <param name="sweepDegrees">How far it turns.</param>
    /// <returns>This builder.</returns>
    public PathBuilder AddArc(RectF oval, float startDegrees, float sweepDegrees)
    {
        double sweep = Math.Clamp(sweepDegrees, -360f, 360f) * Math.PI / 180;
        double start = startDegrees * Math.PI / 180;
        _arc.Clear();
        Vector2 first = Curves.ArcToCubics(oval.Center, new Vector2(oval.Width / 2, oval.Height / 2), 0, start, sweep, _arc);
        MoveTo(first);
        if (sweep == 0)
        {
            return LineTo(first);
        }

        AppendCubics(_arc);
        return this;
    }

    /// <summary>Adds a polygon through the points as a new contour.</summary>
    /// <param name="points">The corners, in order.</param>
    /// <param name="close">Whether to close the contour back to the first point.</param>
    /// <returns>This builder.</returns>
    public PathBuilder AddPolygon(ReadOnlySpan<Vector2> points, bool close = true)
    {
        if (points.IsEmpty)
        {
            return this;
        }

        MoveTo(points[0]);
        foreach (Vector2 p in points[1..])
        {
            LineTo(p);
        }

        return close ? Close() : this;
    }

    /// <summary>Adds every contour of another path.</summary>
    /// <param name="path">The path to add; its fill rule is not copied.</param>
    /// <returns>This builder.</returns>
    public PathBuilder AddPath(Path path) => AddPath(path, Matrix3x2.Identity);

    /// <summary>Adds every contour of another path, transformed.</summary>
    /// <param name="path">The path to add; its fill rule is not copied.</param>
    /// <param name="transform">The transform for its points.</param>
    /// <returns>This builder.</returns>
    public PathBuilder AddPath(Path path, Matrix3x2 transform)
    {
        ArgumentNullException.ThrowIfNull(path);
        Vector2[] points = path.Points.ToArray();
        if (!transform.IsIdentity)
        {
            for (int i = 0; i < points.Length; i++)
            {
                points[i] = Vector2.Transform(points[i], transform);
            }
        }

        int p = 0;
        Vector2 T(int i) => points[i];
        foreach (PathVerb verb in path.Verbs)
        {
            switch (verb)
            {
                case PathVerb.Move:
                    MoveTo(T(p++));
                    break;
                case PathVerb.Line:
                    LineTo(T(p++));
                    break;
                case PathVerb.Quad:
                    QuadTo(T(p), T(p + 1));
                    p += 2;
                    break;
                case PathVerb.Cubic:
                    CubicTo(T(p), T(p + 1), T(p + 2));
                    p += 3;
                    break;
                default:
                    Close();
                    break;
            }
        }

        return this;
    }

    /// <summary>Returns the path built so far, which never changes afterwards. The builder keeps its contents.</summary>
    /// <param name="fillRule">The path's fill rule.</param>
    /// <returns>The path.</returns>
    /// <exception cref="ArgumentException">A point is not a finite number.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="fillRule"/> is not defined.</exception>
    public Path ToPath(FillRule fillRule = FillRule.NonZero)
    {
        if (!Enum.IsDefined(fillRule))
        {
            throw new ArgumentOutOfRangeException(nameof(fillRule), fillRule, null);
        }

        return new Path([.. _points], [.. _verbs], fillRule);
    }

    /// <summary>Returns the path without checking its points, for outlines built from already-checked input whose arithmetic may overflow.</summary>
    internal Path ToPathUnchecked(FillRule fillRule = FillRule.NonZero) => new([.. _points], [.. _verbs], fillRule, checkFinite: false);

    /// <summary>Removes everything added, to build another path.</summary>
    /// <returns>This builder.</returns>
    public PathBuilder Reset()
    {
        _points.Clear();
        _verbs.Clear();
        _contourStart = CurrentPoint = Vector2.Zero;
        _open = false;
        return this;
    }

    private void EnsureContour()
    {
        if (!_open)
        {
            MoveTo(CurrentPoint);
        }
    }

    private void AppendArc(Vector2 center, Vector2 radii, float rotation, double start, double sweep, Vector2 end)
    {
        _arc.Clear();
        Curves.ArcToCubics(center, radii, rotation, start, sweep, _arc);

        // The last point is exactly the requested end, not its rounding through the angles.
        _arc[^1] = end;
        AppendCubics(_arc);
    }

    private void AppendCubics(List<Vector2> points)
    {
        for (int i = 0; i + 2 < points.Count; i += 3)
        {
            CubicTo(points[i], points[i + 1], points[i + 2]);
        }
    }
}
