using System.Numerics;

using Egami.Drawing.Geometry;
using Egami.Operations;

namespace Egami.Drawing;

/// <summary>
/// An outline of contours made of lines, quadratic and cubic Bézier curves, with the <see cref="Egami.Drawing.FillRule"/> that says which
/// points it covers when filled. A path never changes after it is built, so one path can be drawn from many threads and kept by any
/// canvas backend. Coordinates are float pixels, y down. Build one with <see cref="PathBuilder"/> or a shape helper such as
/// <see cref="Circle"/>.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="paths"/>
/// </example>
public sealed partial class Path
{
    private readonly Vector2[] _points;
    private readonly PathVerb[] _verbs;
    private RectF? _tightBounds;
    private bool? _convex;
    private Path[]? _contours;

    internal Path(Vector2[] points, PathVerb[] verbs, FillRule fillRule, bool checkFinite = true)
    {
        if (checkFinite)
        {
            foreach (Vector2 p in points)
            {
                if (!float.IsFinite(p.X) || !float.IsFinite(p.Y))
                {
                    throw new ArgumentException("A path's points must be finite numbers.", nameof(points));
                }
            }
        }

        _points = points;
        _verbs = verbs;
        FillRule = fillRule;
        Bounds = RectF.FromPoints(points);
        foreach (PathVerb verb in verbs)
        {
            if (verb is PathVerb.Line or PathVerb.Quad or PathVerb.Cubic)
            {
                IsEmpty = false;
                return;
            }
        }

        IsEmpty = true;
    }

    /// <summary>Gets a path with no contours.</summary>
    public static Path Empty { get; } = new([], [], FillRule.NonZero);

    /// <summary>Gets the points, in order: one for each move and line, two for each quadratic and three for each cubic.</summary>
    public ReadOnlySpan<Vector2> Points => _points;

    /// <summary>Gets the commands, in order.</summary>
    public ReadOnlySpan<PathVerb> Verbs => _verbs;

    /// <summary>Gets which points the path covers when filled.</summary>
    public FillRule FillRule { get; }

    /// <summary>Gets a value indicating whether the path draws nothing: it has no line or curve.</summary>
    public bool IsEmpty { get; }

    /// <summary>Gets the bounds of every point, control points included: cheap, and never smaller than the outline.</summary>
    public RectF Bounds { get; }

    /// <summary>Gets the exact bounds of the outline: curves are measured at their extremes, not their control points.</summary>
    public RectF TightBounds => _tightBounds ??= ComputeTightBounds();

    /// <summary>
    /// Gets a value indicating whether the path is one convex contour: every turn goes the same way and it winds around once. A
    /// path that is a line, a point or empty counts as convex.
    /// </summary>
    public bool IsConvex => _convex ??= ComputeConvex();

    /// <summary>Gets the contours, each as a path of its own with this path's fill rule.</summary>
    public IReadOnlyList<Path> Contours => _contours ??= SplitContours();

    /// <summary>
    /// Returns whether the filled path covers a point, by the winding number of the outline itself, curves included, and the fill
    /// rule. Open contours count as closed. A point exactly on the outline counts as inside, as in Skia.
    /// </summary>
    /// <param name="point">The point.</param>
    /// <returns><see langword="true"/> if the point is covered.</returns>
    public bool Contains(Vector2 point)
    {
        if (IsEmpty || !(point.X >= Bounds.Left && point.X <= Bounds.Right && point.Y >= Bounds.Top && point.Y <= Bounds.Bottom))
        {
            return false;
        }

        WindingCounter counter = new(point, Math.Max(1, Math.Max(Math.Abs(Bounds.Left), Math.Max(Math.Abs(Bounds.Right), Math.Max(Math.Abs(Bounds.Top), Math.Abs(Bounds.Bottom))))));
        if (!counter.Walk(this))
        {
            return true;
        }

        return FillRule == FillRule.NonZero ? counter.Winding != 0 : (counter.Winding & 1) != 0;
    }

    /// <summary>Returns the path with every point transformed; the fill rule is kept.</summary>
    /// <param name="matrix">The affine transform.</param>
    /// <returns>The transformed path.</returns>
    public Path Transform(Matrix3x2 matrix)
    {
        if (matrix.IsIdentity)
        {
            return this;
        }

        Vector2[] points = new Vector2[_points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            points[i] = Vector2.Transform(_points[i], matrix);
        }

        return new Path(points, _verbs, FillRule);
    }

    /// <summary>Transforms for drawing: points that overflow to infinity are kept, for the rasterizer to clip.</summary>
    internal Path TransformForDrawing(Matrix3x2 matrix)
    {
        Vector2[] points = new Vector2[_points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            points[i] = Vector2.Transform(_points[i], matrix);
        }

        return new Path(points, _verbs, FillRule, checkFinite: false);
    }

    /// <summary>
    /// Returns the path under a perspective transform. Lines stay lines; curves are split until each piece's projected cubic stays
    /// within a hundredth of a unit of the true projection, since a projected Bézier curve is no longer polynomial.
    /// </summary>
    /// <param name="homography">The transform.</param>
    /// <returns>The transformed path.</returns>
    /// <exception cref="ArgumentException">A point maps to infinity or behind the viewer.</exception>
    public Path Transform(Homography homography)
    {
        if (homography.IsAffine)
        {
            return Transform(new Matrix3x2(
                (float)(homography.M11 / homography.M33), (float)(homography.M12 / homography.M33),
                (float)(homography.M21 / homography.M33), (float)(homography.M22 / homography.M33),
                (float)(homography.M31 / homography.M33), (float)(homography.M32 / homography.M33)));
        }

        return PerspectivePath.Transform(this, homography);
    }

    /// <summary>Returns the path with each contour running the other way; contours keep their order and closed state.</summary>
    /// <returns>The reversed path.</returns>
    public Path Reverse()
    {
        PathBuilder builder = new();
        foreach (Path contour in Contours)
        {
            ReverseContour(contour, builder);
        }

        return builder.ToPath(FillRule);
    }

    /// <summary>Returns the path an effect makes of this one, such as its dashes or its corners rounded.</summary>
    /// <param name="effect">The effect.</param>
    /// <returns>The new path.</returns>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="effects"/>
    /// </example>
    public Path Apply(PathEffect effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        return effect.Apply(this);
    }

    /// <summary>Returns the same outline with another fill rule.</summary>
    /// <param name="fillRule">The fill rule.</param>
    /// <returns>The path; this one when the rule is the same.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="fillRule"/> is not defined.</exception>
    public Path WithFillRule(FillRule fillRule)
    {
        if (!Enum.IsDefined(fillRule))
        {
            throw new ArgumentOutOfRangeException(nameof(fillRule), fillRule, null);
        }

        return fillRule == FillRule ? this : new Path(_points, _verbs, fillRule);
    }

    /// <inheritdoc/>
    public override string ToString() => $"Path {{ {_verbs.Length} verbs, {_points.Length} points, {FillRule}, Bounds = {Bounds} }}";

    private RectF ComputeTightBounds()
    {
        if (_points.Length == 0)
        {
            return RectF.Empty;
        }

        Vector2 min = new(float.MaxValue), max = new(float.MinValue);
        Span<float> ts = stackalloc float[4];
        int p = 0;
        Vector2 last = default;
        foreach (PathVerb verb in _verbs)
        {
            switch (verb)
            {
                case PathVerb.Move:
                case PathVerb.Line:
                    last = _points[p++];
                    Add(last);
                    break;
                case PathVerb.Quad:
                    {
                        Vector2 c = _points[p], e = _points[p + 1];
                        int n = Curves.QuadExtrema(last, c, e, ts);
                        for (int i = 0; i < n; i++)
                        {
                            Add(Curves.Quad(last, c, e, ts[i]));
                        }

                        Add(e);
                        last = e;
                        p += 2;
                        break;
                    }

                case PathVerb.Cubic:
                    {
                        Vector2 c1 = _points[p], c2 = _points[p + 1], e = _points[p + 2];
                        int n = Curves.CubicExtrema(last, c1, c2, e, ts);
                        for (int i = 0; i < n; i++)
                        {
                            Add(Curves.Cubic(last, c1, c2, e, ts[i]));
                        }

                        Add(e);
                        last = e;
                        p += 3;
                        break;
                    }
            }
        }

        return RectF.FromEdges(min.X, min.Y, max.X, max.Y);

        void Add(Vector2 v)
        {
            min = Vector2.Min(min, v);
            max = Vector2.Max(max, v);
        }
    }

    private bool ComputeConvex()
    {
        if (Contours.Count > 1)
        {
            // Two contours are convex only when one of them draws nothing.
            int drawn = 0;
            foreach (Path contour in Contours)
            {
                drawn += contour.IsEmpty ? 0 : 1;
            }

            if (drawn > 1)
            {
                return false;
            }
        }

        // Every point in order, control points included, around the closed polygon: the turns must all go one way, and the
        // direction may reverse at most twice per axis, which rules out stars and spirals whose turns all go one way.
        List<Vector2> polygon = [];
        foreach (Vector2 point in _points)
        {
            if (polygon.Count == 0 || polygon[^1] != point)
            {
                polygon.Add(point);
            }
        }

        while (polygon.Count > 1 && polygon[^1] == polygon[0])
        {
            polygon.RemoveAt(polygon.Count - 1);
        }

        if (polygon.Count < 3)
        {
            return true;
        }

        int sign = 0;
        int flipsX = 0, flipsY = 0;
        float lastDx = 0, lastDy = 0;
        int n = polygon.Count;
        for (int i = 0; i < n + 1; i++)
        {
            Vector2 a = polygon[i % n], b = polygon[(i + 1) % n], c = polygon[(i + 2) % n];
            Vector2 ab = b - a, bc = c - b;
            double cross = ((double)ab.X * bc.Y) - ((double)ab.Y * bc.X);
            double scale = (double)ab.Length() * bc.Length();
            if (Math.Abs(cross) > scale * 1e-6)
            {
                int s = Math.Sign(cross);
                if (sign != 0 && s != sign)
                {
                    return false;
                }

                sign = s;
            }

            if (i < n)
            {
                if (ab.X != 0)
                {
                    flipsX += lastDx != 0 && MathF.Sign(ab.X) != MathF.Sign(lastDx) ? 1 : 0;
                    lastDx = ab.X;
                }

                if (ab.Y != 0)
                {
                    flipsY += lastDy != 0 && MathF.Sign(ab.Y) != MathF.Sign(lastDy) ? 1 : 0;
                    lastDy = ab.Y;
                }
            }
        }

        // The polygon closes, so count the flip from the last edge back to the first too.
        Vector2 first = polygon[1] - polygon[0];
        flipsX += lastDx != 0 && first.X != 0 && MathF.Sign(first.X) != MathF.Sign(lastDx) ? 1 : 0;
        flipsY += lastDy != 0 && first.Y != 0 && MathF.Sign(first.Y) != MathF.Sign(lastDy) ? 1 : 0;
        return flipsX <= 2 && flipsY <= 2;
    }

    private Path[] SplitContours()
    {
        List<Path> contours = [];
        int p = 0, startVerb = 0, startPoint = 0;
        for (int v = 0; v <= _verbs.Length; v++)
        {
            if (v == _verbs.Length || (_verbs[v] == PathVerb.Move && v > startVerb))
            {
                if (v > startVerb)
                {
                    contours.Add(new Path(_points[startPoint..p], _verbs[startVerb..v], FillRule));
                }

                startVerb = v;
                startPoint = p;
            }

            if (v < _verbs.Length)
            {
                p += PointCount(_verbs[v]);
            }
        }

        return contours.Count == 1 && contours[0]._verbs.Length == _verbs.Length ? [this] : [.. contours];
    }

    internal static int PointCount(PathVerb verb) => verb switch
    {
        PathVerb.Move or PathVerb.Line => 1,
        PathVerb.Quad => 2,
        PathVerb.Cubic => 3,
        _ => 0,
    };

    private static void ReverseContour(Path contour, PathBuilder builder)
    {
        ReadOnlySpan<Vector2> points = contour.Points;
        ReadOnlySpan<PathVerb> verbs = contour.Verbs;
        if (points.IsEmpty)
        {
            return;
        }

        bool closed = verbs[^1] == PathVerb.Close;
        builder.MoveTo(points[^1]);
        int p = points.Length - 1;
        for (int v = verbs.Length - 1; v > 0; v--)
        {
            switch (verbs[v])
            {
                case PathVerb.Line:
                    builder.LineTo(points[p - 1]);
                    p -= 1;
                    break;
                case PathVerb.Quad:
                    builder.QuadTo(points[p - 1], points[p - 2]);
                    p -= 2;
                    break;
                case PathVerb.Cubic:
                    builder.CubicTo(points[p - 1], points[p - 2], points[p - 3]);
                    p -= 3;
                    break;
            }
        }

        if (closed)
        {
            builder.Close();
        }
    }
}
