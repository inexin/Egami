using System.Numerics;

using Egami.Drawing;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Metafiles.Playback;

/// <summary>Shapes, pens, fills and path brackets.</summary>
internal sealed partial class GdiPlayer
{
    /// <summary>The path bracket being built (between BeginPath and EndPath), in the canvas's device space.</summary>
    private PathBuilder? _bracket;

    /// <summary>The path EndPath closed, in device space, until a path operation uses it.</summary>
    private PathBuilder? _path;

    private FillRule Rule => _dc.PolyFillMode == 2 ? FillRule.NonZero : FillRule.EvenOdd;

    // ---- Paint ----

    /// <summary>
    /// The paint for a color under the current ROP2 (design question 7): copy draws the color, black and white draw those, NOT and
    /// XOR invert through Difference, NOP draws nothing (null); the rest draw as copy with one warning.
    /// </summary>
    private SolidPaint? RopPaint(Rgba8 color)
    {
        switch (_dc.Rop2)
        {
            case 13: // R2_COPYPEN
                return new SolidPaint(color) { Antialias = !_aliased };
            case 11: // R2_NOP
                return null;
            case 1: // R2_BLACK
                return new SolidPaint(new Rgba8(0, 0, 0, 255)) { Antialias = !_aliased };
            case 16: // R2_WHITE
                return new SolidPaint(new Rgba8(255, 255, 255, 255)) { Antialias = !_aliased };
            case 6: // R2_NOT
                return new SolidPaint(new Rgba8(255, 255, 255, 255)) { Antialias = !_aliased, BlendMode = BlendMode.Difference };
            case 7: // R2_XORPEN
                return new SolidPaint(color) { Antialias = !_aliased, BlendMode = BlendMode.Difference };
            default:
                WarnOnce("Metafile.RasterOperation", $"The mix mode (ROP2 {_dc.Rop2}) has no Egami equivalent; it is drawn as a copy (design question 7).", key: "rop2:" + _dc.Rop2);
                return new SolidPaint(color) { Antialias = !_aliased };
        }
    }

    /// <summary>The current brush's paint, or null for the null brush (and for what the brush stages add later).</summary>
    private Paint? BrushPaint()
    {
        GdiBrush brush = _dc.Brush;
        return brush.Style switch
        {
            GdiBrush.Solid => RopPaint(brush.Color),
            GdiBrush.Null => null,
            _ => PatternPaint(brush),
        };
    }

    /// <summary>The current pen as a stroke in device pixels with its paint, or false for the null pen.</summary>
    private bool PenStroke(out Egami.Drawing.Stroke stroke, out Paint paint, out float[]? dashes)
    {
        GdiPen pen = _dc.Pen;
        stroke = Egami.Drawing.Stroke.Hairline;
        paint = null!;
        dashes = null;
        if (pen.Style == GdiPen.Null || RopPaint(pen.Color) is not Paint p)
        {
            return false;
        }

        paint = p;
        float width = pen.Width * Scale;
        bool cosmetic = !pen.Geometric && (pen.Width <= 1 || width < 1);
        int style = pen.Style;
        if (!pen.Geometric && pen.Width > 1 && style is GdiPen.Dash or GdiPen.Dot or GdiPen.DashDot or GdiPen.DashDotDot)
        {
            style = GdiPen.Solid; // CreatePen draws wide styled pens solid
        }

        float unit = cosmetic ? 1 : width;
        dashes = style switch
        {
            GdiPen.Dash => cosmetic ? [18, 6] : [3 * unit, unit],
            GdiPen.Dot => cosmetic ? [3, 3] : [unit, unit],
            GdiPen.DashDot => cosmetic ? [9, 6, 3, 6] : [3 * unit, unit, unit, unit],
            GdiPen.DashDotDot => cosmetic ? [9, 3, 3, 3, 3, 3] : [3 * unit, unit, unit, unit, unit, unit],
            GdiPen.Alternate => [1, 1],
            GdiPen.UserStyle when pen.Dashes is { Length: > 0 } user => [.. user.Select(d => Math.Max(d * (pen.Geometric ? Scale : 1), 0.01f))],
            _ => null,
        };
        if (dashes is not null && (dashes.Length % 2 != 0 || dashes.Sum() <= 0))
        {
            dashes = dashes.Length % 2 != 0 ? [.. dashes, .. dashes] : null;
        }

        stroke = cosmetic
            ? new Egami.Drawing.Stroke(0) { Dash = dashes is null ? null : new DashPattern(dashes) }
            : new Egami.Drawing.Stroke(width)
            {
                Cap = pen.Cap switch { 1 => LineCap.Square, 2 => LineCap.Butt, _ => LineCap.Round },
                Join = pen.Join switch { 1 => LineJoin.Bevel, 2 => LineJoin.Miter, _ => LineJoin.Round },
                MiterLimit = Math.Max(1, _dc.MiterLimit),
                Dash = dashes is null ? null : new DashPattern(dashes),
            };
        return true;
    }

    // ---- Drawing in device space ----

    /// <summary>Fills (when closed) and strokes a figure given in device space, or adds it to the open path bracket.</summary>
    private void Draw(PathBuilder device, bool closed, bool fill = true)
    {
        if (_bracket is not null)
        {
            Continue(_bracket, device.ToPath());
            return;
        }

        Path path = device.ToPath(Rule);
        if (!Sane(path))
        {
            Insane();
            return;
        }

        if (closed && fill && BrushPaint() is Paint brush)
        {
            _canvas.FillPath(path, brush);
        }

        StrokeDevice(path);
    }

    /// <summary>
    /// Adds a figure to the path bracket; one that starts where the bracket's current figure ends (LineTo, PolylineTo,
    /// PolyBezierTo, ArcTo) continues that figure instead of starting a new one, as GDI builds paths.
    /// </summary>
    private static void Continue(PathBuilder bracket, Path path)
    {
        ReadOnlySpan<Vector2> points = path.Points;
        int p = 0;
        bool first = true;
        foreach (PathVerb verb in path.Verbs)
        {
            switch (verb)
            {
                case PathVerb.Move:
                    if (!(first && !bracket.IsEmpty && Vector2.DistanceSquared(bracket.CurrentPoint, points[p]) <= 1f))
                    {
                        bracket.MoveTo(points[p]);
                    }

                    p++;
                    break;
                case PathVerb.Line:
                    bracket.LineTo(points[p++]);
                    break;
                case PathVerb.Quad:
                    bracket.QuadTo(points[p], points[p + 1]);
                    p += 2;
                    break;
                case PathVerb.Cubic:
                    bracket.CubicTo(points[p], points[p + 1], points[p + 2]);
                    p += 3;
                    break;
                case PathVerb.Close:
                    bracket.Close();
                    break;
            }

            first = false;
        }
    }

    /// <summary>Strokes a device-space path with the current pen; an opaque background fills the gaps of a styled pen first.</summary>
    private void StrokeDevice(Path path)
    {
        if (!PenStroke(out Egami.Drawing.Stroke stroke, out Paint paint, out float[]? dashes))
        {
            return;
        }

        if (dashes is not null && _dc.BkMode == 2)
        {
            _canvas.StrokePath(path, stroke with { Dash = null }, new SolidPaint(_dc.BkColor) { Antialias = !_aliased });
        }

        _canvas.StrokePath(path, stroke, paint);
    }

    /// <summary>A logical figure built by a callback, transformed to device space (curves are not snapped, only shifted to pixel centers).</summary>
    private PathBuilder Curve(Action<PathBuilder> build)
    {
        PathBuilder logical = new();
        build(logical);
        Path p = logical.ToPath().Transform(Full * Matrix3x2.CreateTranslation(Center));
        return new PathBuilder().AddPath(p);
    }

    /// <summary>Whether the logical-to-device transform keeps axes aligned, so rectangles stay rectangles.</summary>
    private bool AxisAligned => Full.M12 == 0 && Full.M21 == 0;

    /// <summary>
    /// A logical box's device rectangle for Rectangle (<paramref name="rectangle"/>) or Ellipse and the arcs, aliased the way
    /// GDI places them when it scales (measured against PlayEnhMetaFile): an ellipse's box rounds to whole pixels, halves down,
    /// and loses its right and bottom pixel; a rectangle's left edge takes the first pixel at or after the edge, and its right
    /// edge the last pixel before the edge moved by (1 - scale) / 2. The result runs through the border pixels' centers.
    /// Antialiased, the box is the transformed one.
    /// </summary>
    private RectF DeviceRect(float left, float top, float right, float bottom, bool rectangle, float inset = 0)
    {
        float l, t, r, bt;
        if (_aliased && _deviceAxisAligned)
        {
            // Border pixels in reference device space: the box rounded, its right and bottom pixel excluded.
            Vector2 a = Snap(Vector2.Transform(new Vector2(left, top), PageWorld)), b = Snap(Vector2.Transform(new Vector2(right, bottom), PageWorld));
            Vector2 lo = Vector2.Min(a, b), hi = Vector2.Max(a, b) - Vector2.One;

            // Then in output pixels after the playback scale.
            Vector2 s = Vector2.Abs(_deviceScale), o = (Vector2.One - s) / 2;
            Vector2 first = (lo * _deviceScale) + _deviceOffset, last = ((hi + Vector2.One) * _deviceScale) + _deviceOffset;
            (first, last) = (Vector2.Min(first, last), Vector2.Max(first, last));
            if (rectangle)
            {
                (l, t) = (MathF.Ceiling(first.X - 1e-4f), MathF.Ceiling(first.Y - 1e-4f));
                (r, bt) = (MathF.Ceiling(last.X + o.X - 1e-4f) - 1, MathF.Ceiling(last.Y + o.Y - 1e-4f) - 1);
            }
            else
            {
                (l, t) = (MathF.Ceiling(first.X - 0.5f), MathF.Ceiling(first.Y - 0.5f));
                (r, bt) = (MathF.Ceiling(last.X - 0.5f) - 1, MathF.Ceiling(last.Y - 0.5f) - 1);
            }

            (l, t, r, bt) = (l + 0.5f, t + 0.5f, r + 0.5f, bt + 0.5f);
        }
        else
        {
            Vector2 a = Vector2.Transform(new Vector2(left, top), Full), b = Vector2.Transform(new Vector2(right, bottom), Full);
            (l, t, r, bt) = (MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y), MathF.Max(a.X, b.X), MathF.Max(a.Y, b.Y));
        }

        l += inset;
        t += inset;
        r -= inset;
        bt -= inset;
        return new RectF(l, t, Math.Max(0, r - l), Math.Max(0, bt - t));
    }

    /// <summary>Half the pen width in device pixels for PS_INSIDEFRAME, which keeps a wide pen inside the figure's box.</summary>
    private float InsideFrame => _dc.Pen.Style == GdiPen.InsideFrame && _dc.Pen.Width > 1 ? _dc.Pen.Width * Scale / 2 : 0;

    private void Rectangle(float l, float t, float r, float b)
    {
        if (AxisAligned)
        {
            Draw(new PathBuilder().AddRect(DeviceRect(l, t, r, b, rectangle: true, InsideFrame)), closed: true);
        }
        else
        {
            Polygon([new(l, t), new(r, t), new(r, b), new(l, b)]);
        }
    }

    private void Ellipse(float l, float t, float r, float b)
    {
        if (AxisAligned)
        {
            Draw(new PathBuilder().AddOval(DeviceRect(l, t, r, b, rectangle: false, InsideFrame)), closed: true);
        }
        else
        {
            Draw(Curve(p => p.AddOval(new RectF(l, t, r - l, b - t))), closed: true);
        }
    }

    private void RoundRect(float l, float t, float r, float b, float w, float h)
    {
        if (AxisAligned)
        {
            RectF rect = DeviceRect(l, t, r, b, rectangle: true, InsideFrame);
            Vector2 radii = Vector2.Abs(Vector2.TransformNormal(new Vector2(w, h), Full)) / 2;
            radii = Vector2.Min(radii, new Vector2(rect.Width, rect.Height) / 2);
            Draw(new PathBuilder().AddRoundRect(new RoundRect(rect, radii.X, radii.Y)), closed: true);
        }
        else
        {
            Draw(Curve(p => p.AddRoundRect(new RoundRect(new RectF(l, t, r - l, b - t), Math.Abs(w) / 2, Math.Abs(h) / 2))), closed: true);
        }
    }

    /// <summary>Arc, Chord and Pie (kind 0, 1, 2) and ArcTo (3): the ellipse in the box, from the ray to the start point to the ray to the end point, in the arc direction.</summary>
    private void Arc(int kind, float l, float t, float r, float b, Vector2 start, Vector2 end)
    {
        RectF box = new(MathF.Min(l, r), MathF.Min(t, b), MathF.Abs(r - l), MathF.Abs(b - t));
        if (_aliased && AxisAligned)
        {
            // The box keeps GDI's excluded right and bottom edges, as Ellipse does.
            box = new RectF(box.X, box.Y, Math.Max(0, box.Width - (1 / MathF.Abs(Full.M11))), Math.Max(0, box.Height - (1 / MathF.Abs(Full.M22))));
        }

        Vector2 center = box.Center, radii = new(box.Width / 2, box.Height / 2);
        if (radii.X <= 0 || radii.Y <= 0)
        {
            return;
        }

        float a0 = Angle(start - center, radii), a1 = Angle(end - center, radii);

        // GDI's counterclockwise is in device space (y down): decreasing angles there. A mirrored mapping turns that around.
        Matrix3x2 m = Full;
        bool mirrored = (m.M11 * m.M22) - (m.M12 * m.M21) < 0;
        bool clockwise = _dc.ArcClockwise ^ mirrored;
        float sweep = clockwise ? Mod360(a1 - a0) : -Mod360(a0 - a1);
        if (sweep == 0)
        {
            sweep = clockwise ? 360 : -360;
        }

        Vector2 first = center + (radii * new Vector2(MathF.Cos(a0 * MathF.PI / 180), MathF.Sin(a0 * MathF.PI / 180)));
        PathBuilder logical = new();
        if (kind == 3)
        {
            logical.MoveTo(_dc.Position).LineTo(first);
        }
        else if (kind == 2)
        {
            logical.MoveTo(center).LineTo(first);
        }
        else
        {
            logical.MoveTo(first);
        }

        AddArc(logical, center, radii, a0, sweep);
        if (kind is 1 or 2)
        {
            logical.Close();
        }

        Vector2 last = center + (radii * new Vector2(MathF.Cos((a0 + sweep) * MathF.PI / 180), MathF.Sin((a0 + sweep) * MathF.PI / 180)));
        if (kind == 3)
        {
            _dc.Position = last;
        }

        Path device = logical.ToPath().Transform(Full * Matrix3x2.CreateTranslation(Center));
        Draw(new PathBuilder().AddPath(device), closed: kind is 1 or 2);
    }

    /// <summary>Adds an elliptical arc continuing the current contour.</summary>
    private static void AddArc(PathBuilder builder, Vector2 center, Vector2 radii, float startDegrees, float sweepDegrees)
    {
        int segments = Math.Max(1, (int)MathF.Ceiling(MathF.Abs(sweepDegrees) / 90));
        float step = sweepDegrees / segments * MathF.PI / 180, a = startDegrees * MathF.PI / 180;
        float k = 4f / 3 * MathF.Tan(step / 4);
        for (int i = 0; i < segments; i++)
        {
            float c0 = MathF.Cos(a), s0 = MathF.Sin(a), c1 = MathF.Cos(a + step), s1 = MathF.Sin(a + step);
            builder.CubicTo(
                center + (radii * new Vector2(c0 - (k * s0), s0 + (k * c0))),
                center + (radii * new Vector2(c1 + (k * s1), s1 - (k * c1))),
                center + (radii * new Vector2(c1, s1)));
            a += step;
        }
    }

    /// <summary>The ellipse's parameter angle, in degrees, of the point where a ray from the center crosses it.</summary>
    private static float Angle(Vector2 ray, Vector2 radii) => MathF.Atan2(ray.Y / radii.Y, ray.X / radii.X) * 180 / MathF.PI;

    private static float Mod360(float a)
    {
        a %= 360;
        return a < 0 ? a + 360 : a;
    }

    private void AngleArc(Vector2 center, float radius, float start, float sweep)
    {
        // Angles are counterclockwise from the x axis in logical units (y down on screen), so negate them for the y-down parameter.
        Vector2 radii = new(radius);
        Vector2 first = center + (radii * new Vector2(MathF.Cos(-start * MathF.PI / 180), MathF.Sin(-start * MathF.PI / 180)));
        PathBuilder logical = new PathBuilder().MoveTo(_dc.Position).LineTo(first);
        AddArc(logical, center, radii, -start, -sweep);
        _dc.Position = center + (radii * new Vector2(MathF.Cos(-(start + sweep) * MathF.PI / 180), MathF.Sin(-(start + sweep) * MathF.PI / 180)));
        Draw(new PathBuilder().AddPath(logical.ToPath().Transform(Full * Matrix3x2.CreateTranslation(Center))), closed: false);
    }

    private PathBuilder Points(ReadOnlySpan<Vector2> points, bool close, PathBuilder? into = null)
    {
        PathBuilder builder = into ?? new PathBuilder();
        if (points.IsEmpty)
        {
            return builder;
        }

        Vector2 c = Center;
        builder.MoveTo(Dev(points[0]) + c);
        foreach (Vector2 p in points[1..])
        {
            builder.LineTo(Dev(p) + c);
        }

        if (close)
        {
            builder.Close();
        }

        return builder;
    }

    private void Polygon(ReadOnlySpan<Vector2> points)
    {
        if (points.Length >= 2)
        {
            Draw(Points(points, close: true), closed: true);
        }
    }

    private void Polyline(ReadOnlySpan<Vector2> points)
    {
        if (points.Length >= 2)
        {
            Draw(Points(points, close: false), closed: false);
        }
    }

    private void PolylineTo(ReadOnlySpan<Vector2> points)
    {
        if (points.IsEmpty)
        {
            return;
        }

        Vector2[] all = [_dc.Position, .. points];
        _dc.Position = points[^1];
        Polyline(all);
    }

    private void PolyPolygon(IReadOnlyList<Vector2[]> polygons)
    {
        PathBuilder builder = new();
        foreach (Vector2[] polygon in polygons)
        {
            if (polygon.Length >= 2)
            {
                Points(polygon, close: true, builder);
            }
        }

        Draw(builder, closed: true);
    }

    private void PolyPolyline(IReadOnlyList<Vector2[]> lines)
    {
        PathBuilder builder = new();
        foreach (Vector2[] line in lines)
        {
            if (line.Length >= 2)
            {
                Points(line, close: false, builder);
            }
        }

        Draw(builder, closed: false);
    }

    private void PolyBezier(ReadOnlySpan<Vector2> points, bool to)
    {
        Vector2[] all = to ? [_dc.Position, .. points] : points.ToArray();
        if (all.Length < 4)
        {
            if (to && points.Length > 0)
            {
                _dc.Position = points[^1];
            }

            return;
        }

        Vector2 c = Center;
        PathBuilder builder = new PathBuilder().MoveTo(Vector2.Transform(all[0], Full) + c);
        for (int i = 1; i + 2 < all.Length; i += 3)
        {
            builder.CubicTo(Vector2.Transform(all[i], Full) + c, Vector2.Transform(all[i + 1], Full) + c, Vector2.Transform(all[i + 2], Full) + c);
        }

        if (to)
        {
            _dc.Position = all[^1];
        }

        Draw(builder, closed: false);
    }

    /// <summary>PolyDraw: PT_MOVETO 6, PT_LINETO 2, PT_BEZIERTO 4, each optionally with PT_CLOSEFIGURE 1.</summary>
    private void PolyDraw(ReadOnlySpan<Vector2> points, ReadOnlySpan<byte> types)
    {
        Vector2 c = Center;
        PathBuilder builder = new PathBuilder().MoveTo(Vector2.Transform(_dc.Position, Full) + c);
        int n = Math.Min(points.Length, types.Length);
        for (int i = 0; i < n; i++)
        {
            Vector2 p = Vector2.Transform(points[i], Full) + c;
            switch (types[i] & ~1)
            {
                case 6:
                    builder.MoveTo(p);
                    break;
                case 2:
                    builder.LineTo(p);
                    break;
                case 4 when i + 2 < n:
                    builder.CubicTo(p, Vector2.Transform(points[i + 1], Full) + c, Vector2.Transform(points[i + 2], Full) + c);
                    i += 2;
                    break;
                default:
                    return;
            }

            if ((types[i] & 1) != 0)
            {
                builder.Close();
            }

            _dc.Position = points[i];
        }

        Draw(builder, closed: false);
    }

    private void LineTo(Vector2 to)
    {
        Vector2 from = _dc.Position;
        _dc.Position = to;
        if (_bracket is not null)
        {
            Vector2 c = Center;
            if (_bracket.IsEmpty)
            {
                _bracket.MoveTo(Dev(from) + c);
            }

            _bracket.LineTo(Dev(to) + c);
            return;
        }

        Polyline([from, to]);
    }

    private void MoveTo(Vector2 to)
    {
        _dc.Position = to;
        if (_bracket is not null)
        {
            _bracket.MoveTo(Dev(to) + Center);
        }
    }

    private void SetPixel(Vector2 at, Rgba8 color)
    {
        Vector2 p = Dev(at);
        _canvas.FillRect(new RectF(p.X, p.Y, 1, 1), new SolidPaint(color) { Antialias = false });
    }

    // ---- Path brackets ----

    private void BeginPath() => _bracket = new PathBuilder();

    private void EndPath()
    {
        _path = _bracket;
        _bracket = null;
    }

    private void CloseFigure() => _bracket?.Close();

    private void AbortPath()
    {
        _bracket = null;
        _path = null;
    }

    /// <summary>Takes the closed path; GDI discards it after a fill, stroke or clip.</summary>
    private Path? TakePath()
    {
        Path? path = _path?.ToPath(Rule);
        _path = null;
        if (path is not null && !Sane(path))
        {
            Insane();
            return null;
        }

        return path;
    }

    private void FillPath(bool stroke)
    {
        if (TakePath() is not Path path)
        {
            return;
        }

        if (BrushPaint() is Paint brush)
        {
            _canvas.FillPath(path, brush);
        }

        if (stroke)
        {
            StrokeDevice(path);
        }
    }

    private void StrokePath()
    {
        if (TakePath() is Path path)
        {
            StrokeDevice(path);
        }
    }

    /// <summary>WidenPath: the path becomes the outline the current pen would draw.</summary>
    private void WidenPath()
    {
        if (_path is null || !PenStroke(out Egami.Drawing.Stroke stroke, out _, out _))
        {
            return;
        }

        Path outline = (stroke.IsHairline ? stroke with { Width = 1 } : stroke).ToPath(_path.ToPath(Rule));
        _path = new PathBuilder().AddPath(outline);
    }

    /// <summary>FlattenPath: curves become lines. Egami's fills and strokes flatten anyway, so the path is kept as it is.</summary>
    private static void FlattenPath()
    {
    }
}
