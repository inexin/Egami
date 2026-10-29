using System.Numerics;

using Egami.Drawing;
using Egami.GdiPlus;
using Egami.SystemDrawing.Internal;

namespace Egami.SystemDrawing.Drawing2D;

using Point = System.Drawing.Point;

/// <summary>Flattening, widening, warping, bounds and hit tests.</summary>
public sealed partial class GraphicsPath
{
    /// <summary>GDI+'s default flatness: the furthest a line may stray from the curve it replaces, in device units.</summary>
    private const float DefaultFlatness = 0.25f;

    /// <summary>Replaces every Bezier with lines.</summary>
    public void Flatten() => Flatten(null, DefaultFlatness);

    /// <summary>Transforms the path, then replaces every Bezier with lines.</summary>
    /// <param name="matrix">The transform, or null.</param>
    public void Flatten(Matrix? matrix) => Flatten(matrix, DefaultFlatness);

    /// <summary>
    /// Transforms the path, then replaces every Bezier with lines that stray at most <paramref name="flatness"/> from it: a power of
    /// two of equal steps along each curve, as many as GDI+ uses.
    /// </summary>
    /// <param name="matrix">The transform, or null.</param>
    /// <param name="flatness">The furthest a line may stray.</param>
    public void Flatten(Matrix? matrix, float flatness)
    {
        if (matrix is not null)
        {
            Transform(matrix);
        }

        (List<PointF> points, List<byte> types) = Flattened(Points, Types, flatness);
        Replace(points, types);
    }

    /// <summary>Replaces the path with the outline a pen would draw along it, filled nonzero.</summary>
    /// <param name="pen">The pen.</param>
    public void Widen(Pen pen) => Widen(pen, null, DefaultFlatness);

    /// <summary>Transforms the path, then replaces it with the outline a pen would draw along it.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="matrix">The transform, or null.</param>
    public void Widen(Pen pen, Matrix? matrix) => Widen(pen, matrix, DefaultFlatness);

    /// <summary>Transforms the path, then replaces it with the outline a pen would draw along it, curves flattened.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="matrix">The transform, or null.</param>
    /// <param name="flatness">The furthest a line may stray from a curve.</param>
    public void Widen(Pen pen, Matrix? matrix, float flatness)
    {
        ArgumentNullException.ThrowIfNull(pen);
        if (matrix is not null)
        {
            Transform(matrix);
        }

        if (_points.Count == 0)
        {
            return;
        }

        Path outline = Outline(pen);
        (Vector2[] p, byte[] t) = GdiPlusData.ToPointsAndTypes(outline);
        (List<PointF> points, List<byte> types) = Flattened(ToPointF(p), t, flatness);

        // A closed figure does not repeat its first point at its end.
        foreach ((int start, int end) in Figures(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(types)).AsEnumerable().Reverse())
        {
            if (end > start + 1 && (types[end] & Close) != 0 && points[end] == points[start])
            {
                points.RemoveAt(end);
                types.RemoveAt(end);
                types[end - 1] |= Close;
            }
        }

        Replace(points, types);
        FillMode = FillMode.Winding;
    }

    /// <summary>Maps the path from a rectangle onto a quadrilateral, by perspective.</summary>
    /// <param name="destPoints">Where the top-left, top-right, bottom-left and bottom-right corners go; three points make a parallelogram.</param>
    /// <param name="srcRect">The rectangle.</param>
    public void Warp(PointF[] destPoints, RectangleF srcRect) => Warp(destPoints, srcRect, null, WarpMode.Perspective, DefaultFlatness);

    /// <summary>Transforms the path, then maps it from a rectangle onto a quadrilateral, by perspective.</summary>
    /// <param name="destPoints">Where the top-left, top-right, bottom-left and bottom-right corners go.</param>
    /// <param name="srcRect">The rectangle.</param>
    /// <param name="matrix">The transform, or null.</param>
    public void Warp(PointF[] destPoints, RectangleF srcRect, Matrix? matrix) => Warp(destPoints, srcRect, matrix, WarpMode.Perspective, DefaultFlatness);

    /// <summary>Transforms the path, then maps it from a rectangle onto a quadrilateral.</summary>
    /// <param name="destPoints">Where the top-left, top-right, bottom-left and bottom-right corners go.</param>
    /// <param name="srcRect">The rectangle.</param>
    /// <param name="matrix">The transform, or null.</param>
    /// <param name="warpMode">Perspective or bilinear.</param>
    public void Warp(PointF[] destPoints, RectangleF srcRect, Matrix? matrix, WarpMode warpMode) => Warp(destPoints, srcRect, matrix, warpMode, DefaultFlatness);

    /// <summary>Transforms the path, then maps it from a rectangle onto a quadrilateral, curves flattened first.</summary>
    /// <param name="destPoints">Where the top-left, top-right, bottom-left and bottom-right corners go.</param>
    /// <param name="srcRect">The rectangle.</param>
    /// <param name="matrix">The transform, or null.</param>
    /// <param name="warpMode">Perspective or bilinear.</param>
    /// <param name="flatness">The furthest a line may stray from a curve.</param>
    public void Warp(PointF[] destPoints, RectangleF srcRect, Matrix? matrix, WarpMode warpMode, float flatness)
    {
        ArgumentNullException.ThrowIfNull(destPoints);
        Warp(destPoints.AsSpan(), srcRect, matrix, warpMode, flatness);
    }

    /// <summary>Transforms the path, then maps it from a rectangle onto a quadrilateral, curves flattened first.</summary>
    /// <param name="destPoints">Where the top-left, top-right, bottom-left and bottom-right corners go; three points make a parallelogram.</param>
    /// <param name="srcRect">The rectangle.</param>
    /// <param name="matrix">The transform, or null.</param>
    /// <param name="warpMode">Perspective or bilinear.</param>
    /// <param name="flatness">The furthest a line may stray from a curve.</param>
    /// <exception cref="ArgumentException">Fewer than three points, or an empty rectangle.</exception>
    public void Warp(ReadOnlySpan<PointF> destPoints, RectangleF srcRect, Matrix? matrix, WarpMode warpMode, float flatness)
    {
        if (destPoints.Length < 3 || srcRect.Width == 0 || srcRect.Height == 0)
        {
            throw GdiErrors.InvalidParameter();
        }

        if (matrix is not null)
        {
            Transform(matrix);
        }

        if (_types.Exists(t => (t & 0x07) == (byte)PathPointType.Bezier))
        {
            Flatten(null, flatness);
        }

        Vector2 p0 = V(destPoints[0]), p1 = V(destPoints[1]), p2 = V(destPoints[2]);
        Vector2 p3 = destPoints.Length > 3 ? V(destPoints[3]) : p1 + p2 - p0;
        Func<float, float, Vector2> map = warpMode == WarpMode.Bilinear || destPoints.Length == 3
            ? (u, v) => (p0 * (1 - u) * (1 - v)) + (p1 * u * (1 - v)) + (p2 * (1 - u) * v) + (p3 * u * v)
            : Perspective(p0, p1, p2, p3);
        for (int i = 0; i < _points.Count; i++)
        {
            float u = (_points[i].X - srcRect.X) / srcRect.Width, v = (_points[i].Y - srcRect.Y) / srcRect.Height;
            Vector2 q = map(u, v);
            _points[i] = new PointF(q.X, q.Y);
        }
    }

    /// <summary>The bounds of the points.</summary>
    /// <returns>The bounds, or an empty rectangle for an empty path.</returns>
    public RectangleF GetBounds() => GetBounds(null, null);

    /// <summary>The bounds of the points, their corners transformed.</summary>
    /// <param name="matrix">The transform, or null.</param>
    /// <returns>The bounds.</returns>
    public RectangleF GetBounds(Matrix? matrix) => GetBounds(matrix, null);

    /// <summary>
    /// The bounds of the points, their corners transformed, grown for a pen as GDI+ grows them: by half the width times the miter
    /// limit for mitered joins, and by the width for the others.
    /// </summary>
    /// <param name="matrix">The transform, or null.</param>
    /// <param name="pen">The pen, or null.</param>
    /// <returns>The bounds.</returns>
    public RectangleF GetBounds(Matrix? matrix, Pen? pen)
    {
        if (_points.Count == 0)
        {
            return RectangleF.Empty;
        }

        Vector2 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (PointF p in _points)
        {
            min = Vector2.Min(min, V(p));
            max = Vector2.Max(max, V(p));
        }

        if (matrix is not null)
        {
            // GDI+ transforms the bounds' corners, not the points.
            Matrix3x2 m = matrix.MatrixElements;
            Vector2 a = Vector2.Transform(min, m), b = Vector2.Transform(new Vector2(max.X, min.Y), m);
            Vector2 c = Vector2.Transform(new Vector2(min.X, max.Y), m), d = Vector2.Transform(max, m);
            (min, max) = (Vector2.Min(Vector2.Min(a, b), Vector2.Min(c, d)), Vector2.Max(Vector2.Max(a, b), Vector2.Max(c, d)));
        }

        if (pen is not null)
        {
            float half = pen.Width / 2;
            float grow = pen.LineJoin is LineJoin.Miter or LineJoin.MiterClipped ? half * Math.Max(1, pen.MiterLimit) : half * 2;
            min -= new Vector2(grow);
            max += new Vector2(grow);
        }

        return RectangleF.FromLTRB(min.X, min.Y, max.X, max.Y);
    }

    /// <summary>Whether a point is inside the path: the pixel it rounds to, as GDI+ tests it.</summary>
    /// <param name="x">The x.</param>
    /// <param name="y">The y.</param>
    /// <returns><see langword="true"/> when inside.</returns>
    public bool IsVisible(float x, float y) => Hit(ToPath(), x, y);

    /// <summary>Whether a point is inside the path.</summary>
    /// <param name="point">The point.</param>
    /// <returns><see langword="true"/> when inside.</returns>
    public bool IsVisible(PointF point) => IsVisible(point.X, point.Y);

    /// <summary>Whether a point is inside the path; the graphics is not used.</summary>
    /// <param name="x">The x.</param>
    /// <param name="y">The y.</param>
    /// <param name="graphics">A graphics, or null.</param>
    /// <returns><see langword="true"/> when inside.</returns>
    public bool IsVisible(float x, float y, Graphics? graphics) => IsVisible(x, y);

    /// <summary>Whether a point is inside the path; the graphics is not used.</summary>
    /// <param name="pt">The point.</param>
    /// <param name="graphics">A graphics, or null.</param>
    /// <returns><see langword="true"/> when inside.</returns>
    public bool IsVisible(PointF pt, Graphics? graphics) => IsVisible(pt.X, pt.Y);

    /// <summary>Whether a point is inside the path.</summary>
    /// <param name="x">The x.</param>
    /// <param name="y">The y.</param>
    /// <returns><see langword="true"/> when inside.</returns>
    public bool IsVisible(int x, int y) => IsVisible((float)x, y);

    /// <summary>Whether a point is inside the path.</summary>
    /// <param name="point">The point.</param>
    /// <returns><see langword="true"/> when inside.</returns>
    public bool IsVisible(Point point) => IsVisible((float)point.X, point.Y);

    /// <summary>Whether a point is inside the path; the graphics is not used.</summary>
    /// <param name="x">The x.</param>
    /// <param name="y">The y.</param>
    /// <param name="graphics">A graphics, or null.</param>
    /// <returns><see langword="true"/> when inside.</returns>
    public bool IsVisible(int x, int y, Graphics? graphics) => IsVisible((float)x, y);

    /// <summary>Whether a point is inside the path; the graphics is not used.</summary>
    /// <param name="pt">The point.</param>
    /// <param name="graphics">A graphics, or null.</param>
    /// <returns><see langword="true"/> when inside.</returns>
    public bool IsVisible(Point pt, Graphics? graphics) => IsVisible((float)pt.X, pt.Y);

    /// <summary>Whether a point is under the outline a pen would draw along the path.</summary>
    /// <param name="x">The x.</param>
    /// <param name="y">The y.</param>
    /// <param name="pen">The pen.</param>
    /// <returns><see langword="true"/> when under the outline.</returns>
    public bool IsOutlineVisible(float x, float y, Pen pen)
    {
        ArgumentNullException.ThrowIfNull(pen);
        return _points.Count > 0 && Hit(Outline(pen), x, y);
    }

    /// <summary>Whether a point is under the outline a pen would draw along the path.</summary>
    /// <param name="point">The point.</param>
    /// <param name="pen">The pen.</param>
    /// <returns><see langword="true"/> when under the outline.</returns>
    public bool IsOutlineVisible(PointF point, Pen pen) => IsOutlineVisible(point.X, point.Y, pen);

    /// <summary>Whether a point is under the outline a pen would draw along the path; the graphics is not used.</summary>
    /// <param name="x">The x.</param>
    /// <param name="y">The y.</param>
    /// <param name="pen">The pen.</param>
    /// <param name="graphics">A graphics, or null.</param>
    /// <returns><see langword="true"/> when under the outline.</returns>
    public bool IsOutlineVisible(float x, float y, Pen pen, Graphics? graphics) => IsOutlineVisible(x, y, pen);

    /// <summary>Whether a point is under the outline a pen would draw along the path; the graphics is not used.</summary>
    /// <param name="pt">The point.</param>
    /// <param name="pen">The pen.</param>
    /// <param name="graphics">A graphics, or null.</param>
    /// <returns><see langword="true"/> when under the outline.</returns>
    public bool IsOutlineVisible(PointF pt, Pen pen, Graphics? graphics) => IsOutlineVisible(pt.X, pt.Y, pen);

    /// <summary>Whether a point is under the outline a pen would draw along the path.</summary>
    /// <param name="x">The x.</param>
    /// <param name="y">The y.</param>
    /// <param name="pen">The pen.</param>
    /// <returns><see langword="true"/> when under the outline.</returns>
    public bool IsOutlineVisible(int x, int y, Pen pen) => IsOutlineVisible((float)x, y, pen);

    /// <summary>Whether a point is under the outline a pen would draw along the path.</summary>
    /// <param name="point">The point.</param>
    /// <param name="pen">The pen.</param>
    /// <returns><see langword="true"/> when under the outline.</returns>
    public bool IsOutlineVisible(Point point, Pen pen) => IsOutlineVisible((float)point.X, point.Y, pen);

    /// <summary>Whether a point is under the outline a pen would draw along the path; the graphics is not used.</summary>
    /// <param name="x">The x.</param>
    /// <param name="y">The y.</param>
    /// <param name="pen">The pen.</param>
    /// <param name="graphics">A graphics, or null.</param>
    /// <returns><see langword="true"/> when under the outline.</returns>
    public bool IsOutlineVisible(int x, int y, Pen pen, Graphics? graphics) => IsOutlineVisible((float)x, y, pen);

    /// <summary>Whether a point is under the outline a pen would draw along the path; the graphics is not used.</summary>
    /// <param name="pt">The point.</param>
    /// <param name="pen">The pen.</param>
    /// <param name="graphics">A graphics, or null.</param>
    /// <returns><see langword="true"/> when under the outline.</returns>
    public bool IsOutlineVisible(Point pt, Pen pen, Graphics? graphics) => IsOutlineVisible((float)pt.X, pt.Y, pen);

    /// <summary>The outline a pen draws along the path, its caps included, filled nonzero, in the path's coordinates.</summary>
    internal Path Outline(Pen pen)
    {
        GdiPlusPen geometry = pen.Geometry();
        float width = Math.Max(geometry.Width * MathF.Sqrt(MathF.Abs(geometry.Transform.GetDeterminant())), 1e-3f);
        GdiPlusPenOutline outline = GdiPlusPens.Outline(ToPath(), geometry, width);
        Path body = outline.Clip is Path clip ? outline.Outline.WithFillRule(FillRule.NonZero).Intersect(clip) : outline.Outline.WithFillRule(FillRule.NonZero);
        return outline.Caps.Count == 0 ? body : Path.Combine([body, .. outline.Caps], PathOp.Union);
    }

    /// <summary>Whether the pixel a point rounds to is one of the shape's.</summary>
    private static bool Hit(Path shape, float x, float y) =>
        PixelScans.Contains(shape, (int)MathF.Floor(x + 0.5f), (int)MathF.Floor(y + 0.5f));

    /// <summary>The perspective map of the unit square onto four points (top-left, top-right, bottom-left, bottom-right).</summary>
    private static Func<float, float, Vector2> Perspective(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3)
    {
        // The projective map taking (0,0), (1,0), (0,1), (1,1) to p0, p1, p2, p3 (Heckbert's square-to-quad).
        double sx = p0.X - p1.X - p2.X + p3.X, sy = p0.Y - p1.Y - p2.Y + p3.Y;
        double a, b, c, d, e, f, g, h;
        if (Math.Abs(sx) < 1e-12 && Math.Abs(sy) < 1e-12)
        {
            a = p1.X - p0.X;
            b = p2.X - p0.X;
            c = p0.X;
            d = p1.Y - p0.Y;
            e = p2.Y - p0.Y;
            f = p0.Y;
            g = h = 0;
        }
        else
        {
            double dx1 = p1.X - p3.X, dx2 = p2.X - p3.X, dy1 = p1.Y - p3.Y, dy2 = p2.Y - p3.Y;
            double den = (dx1 * dy2) - (dx2 * dy1);
            g = ((sx * dy2) - (dx2 * sy)) / den;
            h = ((dx1 * sy) - (sx * dy1)) / den;
            a = p1.X - p0.X + (g * p1.X);
            b = p2.X - p0.X + (h * p2.X);
            c = p0.X;
            d = p1.Y - p0.Y + (g * p1.Y);
            e = p2.Y - p0.Y + (h * p2.Y);
            f = p0.Y;
        }

        return (u, v) =>
        {
            double w = (g * u) + (h * v) + 1;
            return new Vector2((float)(((a * u) + (b * v) + c) / w), (float)(((d * u) + (e * v) + f) / w));
        };
    }

    /// <summary>Points and types with every Bezier replaced by a power of two of equal steps, enough to stay within the flatness.</summary>
    private static (List<PointF> Points, List<byte> Types) Flattened(ReadOnlySpan<PointF> points, ReadOnlySpan<byte> types, float flatness)
    {
        flatness = Math.Max(flatness, 1e-3f);
        List<PointF> p = new(points.Length);
        List<byte> t = new(types.Length);
        for (int i = 0; i < points.Length; i++)
        {
            if ((types[i] & 0x07) == (byte)PathPointType.Bezier && i + 2 < points.Length && i > 0)
            {
                Vector2 a = V(points[i - 1]), c1 = V(points[i]), c2 = V(points[i + 1]), b = V(points[i + 2]);
                float dd = Math.Max((a - (2 * c1) + c2).Length(), (c1 - (2 * c2) + b).Length());
                int n = 1;
                while (n < 1024 && n * n * 4 * flatness < 3 * dd)
                {
                    n *= 2;
                }

                byte flags = (byte)(types[i + 2] & ~0x07);
                for (int k = 1; k <= n; k++)
                {
                    float s = (float)k / n, u = 1 - s;
                    Vector2 q = (u * u * u * a) + (3 * u * u * s * c1) + (3 * u * s * s * c2) + (s * s * s * b);
                    p.Add(new PointF(q.X, q.Y));
                    t.Add(k == n ? (byte)(LineType | flags) : LineType);
                }

                i += 2;
                continue;
            }

            p.Add(points[i]);
            t.Add(types[i]);
        }

        return (p, t);
    }

    private static PointF[] ToPointF(Vector2[] v)
    {
        PointF[] p = new PointF[v.Length];
        for (int i = 0; i < p.Length; i++)
        {
            p[i] = new PointF(v[i].X, v[i].Y);
        }

        return p;
    }

    private static Vector2 V(PointF p) => new(p.X, p.Y);

    private void Replace(List<PointF> points, List<byte> types)
    {
        _points.Clear();
        _points.AddRange(points);
        _types.Clear();
        _types.AddRange(types);
    }
}
