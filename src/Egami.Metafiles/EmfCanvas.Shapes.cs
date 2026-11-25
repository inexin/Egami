using System.Numerics;

using Egami.Drawing;
using Egami.GdiPlus;
using Egami.Metafiles.Writing;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Metafiles;

public sealed partial class EmfCanvas
{
    /// <summary>What a shape call draws: a path, or a rectangle or oval EMF+ has records of its own for.</summary>
    private enum ShapeKind
    {
        Path,
        Rect,
        Oval,
    }

    /// <inheritdoc/>
    protected override void OnDrawPath(Path path, Stroke? stroke, Paint paint) => DrawShape(ShapeKind.Path, path, default, stroke, paint);

    /// <inheritdoc/>
    protected override void OnDrawRect(RectF rect, Stroke? stroke, Paint paint) => DrawShape(ShapeKind.Rect, null, rect, stroke, paint);

    /// <inheritdoc/>
    protected override void OnDrawOval(RectF oval, Stroke? stroke, Paint paint) => DrawShape(ShapeKind.Oval, null, oval, stroke, paint);

    /// <inheritdoc/>
    protected override void OnClear(RgbaF32 color)
    {
        if (Recording())
        {
            _recorder!.Clear(color);
            return;
        }

        RectF clip = DeviceClipBounds;
        if (_quiet > 0 || clip.IsEmpty)
        {
            return;
        }

        Rgba8 c = Color8(color, 1);
        if (_plus)
        {
            _w.Plus(0x4009, 0, _w.Data().U32(Argb(c)).Span); // EmfPlusClear: the clip's pixels become the color
        }

        if (_gdi && c.A > 0)
        {
            // GDI has no alpha: the clip's bounds filled with the opaque color.
            SetGdiWorld(Matrix3x2.Identity);
            if (WriteGdiPath(Path.Rect(clip), Matrix3x2.Identity))
            {
                SelectPen(NullPen);
                SelectBrush(SolidBrush(c));
                _w.Gdi(62, Bounds16(clip)); // EMR_FILLPATH
            }
        }

        _drawn = _drawn.IsEmpty ? clip : RectF.FromEdges(MathF.Min(_drawn.Left, clip.Left), MathF.Min(_drawn.Top, clip.Top), MathF.Max(_drawn.Right, clip.Right), MathF.Max(_drawn.Bottom, clip.Bottom));
    }

    private void DrawShape(ShapeKind kind, Path? path, RectF rect, Stroke? stroke, Paint paint)
    {
        Matrix3x2 m = Matrix;
        RectF local = path?.Bounds ?? rect;
        float scale = MathF.Sqrt(MathF.Abs(m.GetDeterminant()));
        float outset = stroke is null ? 1 : (MathF.Max(stroke.Width * scale, 1) * MathF.Max(stroke.MiterLimit, 1) / 2) + 1;
        Path Geometry() => path ?? (kind == ShapeKind.Oval ? Path.Oval(rect) : Path.Rect(rect));
        if (Recording())
        {
            NoteLayerShape(local, outset, paint);
            switch (kind, stroke)
            {
                case (ShapeKind.Rect, null):
                    _recorder!.FillRect(rect, paint);
                    break;
                case (ShapeKind.Rect, Stroke s):
                    _recorder!.StrokeRect(rect, s, paint);
                    break;
                case (ShapeKind.Oval, null):
                    _recorder!.FillOval(rect, paint);
                    break;
                case (ShapeKind.Oval, Stroke s):
                    _recorder!.StrokeOval(rect, s, paint);
                    break;
                case (_, null):
                    _recorder!.FillPath(Geometry(), paint);
                    break;
                default:
                    _recorder!.StrokePath(Geometry(), stroke, paint);
                    break;
            }

            return;
        }

        if (_quiet > 0)
        {
            return;
        }

        if (_fold < 1)
        {
            paint = paint with { Opacity = paint.Opacity * _fold };
        }

        void Draw(Canvas c)
        {
            if (stroke is null)
            {
                c.FillPath(Geometry(), paint);
            }
            else
            {
                c.StrokePath(Geometry(), stroke, paint);
            }
        }

        RgbaF32? constant = Constant(paint);
        if (Unsupported(paint, constant) is string what)
        {
            Fallback(local, outset, Draw, what);
            return;
        }

        if (paint.BlendMode == BlendMode.Destination || !Invertible(m))
        {
            return;
        }

        if (constant is RgbaF32 solid)
        {
            Rgba8 color = Color8(solid, paint.Opacity);
            if (color.A == 0)
            {
                return;
            }

            Touch(local, outset);
            if (_plus)
            {
                WritePlusShape(kind, path, rect, stroke, color, paint.Antialias, scale);
            }

            if (_gdi)
            {
                WriteGdiShape(Geometry(), stroke, color, scale);
            }

            return;
        }

        // A paint EMF+ has a brush for fills the shape, or the outline of its stroke, with that brush.
        Path? fill;
        try
        {
            fill = stroke is null ? Geometry() : stroke.IsHairline ? null : stroke.ToPath(Geometry(), MathF.Max(scale, 1e-3f), simplify: true);
        }
        catch (ArgumentException)
        {
            fill = null;
        }

        if (fill is null)
        {
            Fallback(local, outset, Draw, $"a hairline with a {paint.GetType().Name}");
            return;
        }

        if (fill.IsEmpty)
        {
            return;
        }

        if (!TryBrush(paint, fill.Bounds, paint.Opacity, out BrushPlan? plan, out string? reason))
        {
            Fallback(local, outset, Draw, reason!);
            return;
        }

        if (plan is null)
        {
            return;
        }

        Touch(local, outset);
        if (_plus)
        {
            SetPlusWorld(m);
            SetSmooth(paint.Antialias);
            SetInterpolation(plan.Interpolation);
            int brush = _w.PlusObject(1, plan.Brush);
            int shape = _w.PlusObject(3, GdiPlusData.WritePath(EvenOdd(fill)));
            _w.Plus(0x4014, (ushort)shape, _w.Data().U32((uint)brush).Span); // FillPath with a brush object
        }

        if (_gdi)
        {
            if (plan.Gradient is GdiGradient gradient)
            {
                WriteGdiGradient(fill, m, gradient);
            }
            else
            {
                // GDI has no form for this paint: its part is an image of the call.
                Fallback(local, outset, Draw, reason ?? $"a {paint.GetType().Name}", plus: false);
            }
        }
    }

    /// <summary>Why a paint has no vector form in EMF (its blend mode or blur), or null when it may have one.</summary>
    private static string? Unsupported(Paint paint, RgbaF32? constant)
    {
        if (paint.Blur is not null)
        {
            return "a paint with a blur";
        }

        return paint.BlendMode switch
        {
            BlendMode.SourceOver or BlendMode.Destination => null,
            BlendMode.Source when constant is RgbaF32 c && c.A * paint.Opacity >= 1 => null,
            _ => $"the {paint.BlendMode} blend mode",
        };
    }

    // ---- EMF+ ----

    private void WritePlusShape(ShapeKind kind, Path? path, RectF rect, Stroke? stroke, Rgba8 color, bool antialias, float scale)
    {
        SetPlusWorld(Matrix);
        SetSmooth(antialias);
        uint argb = Argb(color);
        if (stroke is null)
        {
            switch (kind)
            {
                case ShapeKind.Rect:
                    _w.Plus(0x400A, 0x8000, _w.Data().U32(argb).I32(1).F32(rect.X).F32(rect.Y).F32(rect.Width).F32(rect.Height).Span); // FillRects
                    break;
                case ShapeKind.Oval:
                    _w.Plus(0x400E, 0x8000, _w.Data().U32(argb).F32(rect.X).F32(rect.Y).F32(rect.Width).F32(rect.Height).Span); // FillEllipse
                    break;
                default:
                    {
                        int id = _w.PlusObject(3, GdiPlusData.WritePath(EvenOdd(path!)));
                        _w.Plus(0x4014, (ushort)(0x8000 | id), _w.Data().U32(argb).Span); // FillPath, the color in BrushId
                        break;
                    }
            }

            return;
        }

        int pen = _w.PlusObject(2, PlusPen(stroke, argb, scale));
        if (kind != ShapeKind.Path && stroke.Dash is null)
        {
            // Without dashes, where the outline starts does not show: the shape records of their own.
            RecordBuffer b = _w.Data();
            if (kind == ShapeKind.Rect)
            {
                _w.Plus(0x400B, (ushort)pen, b.I32(1).F32(rect.X).F32(rect.Y).F32(rect.Width).F32(rect.Height).Span); // DrawRects
            }
            else
            {
                _w.Plus(0x400F, (ushort)pen, b.F32(rect.X).F32(rect.Y).F32(rect.Width).F32(rect.Height).Span); // DrawEllipse
            }

            return;
        }

        int shape = _w.PlusObject(3, GdiPlusData.WritePath(path ?? (kind == ShapeKind.Oval ? Path.Oval(rect) : Path.Rect(rect))));
        _w.Plus(0x4015, (ushort)shape, _w.Data().U32((uint)pen).Span); // DrawPath
    }

    /// <summary>
    /// An EmfPlusPen: GDI+ pen data in world units with caps, join and miter limit, a custom dash pattern in multiples of the
    /// width when dashed, and a solid brush. A hairline is width 0, GDI+'s one-pixel pen (dashed: one device pixel wide).
    /// </summary>
    private static byte[] PlusPen(Stroke stroke, uint argb, float scale)
    {
        float[]? dashes = Intervals(stroke);
        float width = stroke.IsHairline && dashes is not null ? 1 / MathF.Max(scale, 1e-6f) : stroke.Width;
        int cap = stroke.Cap switch { LineCap.Square => GdiPlusPen.CapSquare, LineCap.Round => GdiPlusPen.CapRound, _ => GdiPlusPen.CapFlat };
        int join = stroke.Join switch { LineJoin.Bevel => GdiPlusPen.JoinBevel, LineJoin.Round => GdiPlusPen.JoinRound, _ => GdiPlusPen.JoinMiter };
        uint flags = 0x02 | 0x04 | 0x08 | 0x10; // start cap, end cap, join, miter limit
        if (dashes is not null)
        {
            flags |= 0x20 | 0x40 | 0x80 | 0x100; // line style, dashed line cap, offset, dashes
        }

        RecordBuffer b = new(96);
        b.U32(GdiPlusData.Version).U32(0).U32(flags).U32(0).F32(width); // version, type, flags, UnitWorld, width
        b.I32(cap).I32(cap).I32(join).F32(stroke.MiterLimit);
        if (dashes is not null)
        {
            // GDI+ draws a round dash cap inside the dash, so a dash of a with round ends is a dash of a + w with the gap shortened
            // by w; there is no square dash cap, so a square-capped dash is a flat one w longer that starts w / 2 earlier.
            float w = width;
            float grow = stroke.Cap == LineCap.Butt ? 0 : w;
            float phase = (stroke.Dash!.Phase + (stroke.Cap == LineCap.Square ? w / 2 : 0)) / w;
            b.I32(GdiPlusPen.DashCustom).I32(stroke.Cap == LineCap.Round ? GdiPlusPen.CapRound : GdiPlusPen.CapFlat).F32(phase).I32(dashes.Length);
            for (int i = 0; i < dashes.Length; i++)
            {
                float length = i % 2 == 0 ? dashes[i] + grow : dashes[i] - grow;
                b.F32(MathF.Max(length / w, 1e-3f));
            }
        }

        b.U32(GdiPlusData.Version).U32(0).U32(argb); // solid brush
        return b.ToArray();
    }

    /// <summary>A stroke's dash intervals, an odd count repeated to an even one; null when solid.</summary>
    private static float[]? Intervals(Stroke stroke)
    {
        if (stroke.Dash is not DashPattern dash || dash.Intervals.Count == 0)
        {
            return null;
        }

        float[] intervals = [.. dash.Intervals];
        return intervals.Length % 2 == 0 ? intervals : [.. intervals, .. intervals];
    }

    // ---- GDI ----

    private void WriteGdiShape(Path path, Stroke? stroke, Rgba8 color, float scale)
    {
        Matrix3x2 m = Matrix;
        if (!WriteGdiPath(path, m))
        {
            return;
        }

        RectF device = MapRect(path.Bounds, m);
        if (stroke is null)
        {
            SetFillMode(path.FillRule);
            SelectPen(NullPen);
            SelectBrush(SolidBrush(color));
            _w.Gdi(62, Bounds16(device)); // EMR_FILLPATH
            return;
        }

        SelectPen(GdiPen(stroke, color, scale));
        if (stroke.Join == LineJoin.Miter && _file.Miter != stroke.MiterLimit)
        {
            _w.Gdi(58, _w.Data().F32(stroke.MiterLimit).Span); // EMR_SETMITERLIMIT, a float as GDI writes it
            _file.Miter = stroke.MiterLimit;
        }

        float outset = MathF.Max(stroke.Width * scale, 1) * MathF.Max(stroke.MiterLimit, 1) / 2;
        _w.Gdi(64, Bounds16(device.Inflate(outset, outset))); // EMR_STROKEPATH
    }

    /// <summary>
    /// Writes a path bracket (EMR_BEGINPATH to EMR_ENDPATH) in sixteenths of a local unit under GDI's world transform: moves,
    /// lines and cubic Beziers (quadratic curves raised to cubics), 16-bit records when the coordinates fit. False when they do
    /// not fit 32 bits either, and nothing is written.
    /// </summary>
    private bool WriteGdiPath(Path path, Matrix3x2 m)
    {
        if (!Invertible(m))
        {
            return false;
        }

        (Vector2[] points, byte[] types) = GdiPlusData.ToPointsAndTypes(path);
        float max = 0;
        foreach (Vector2 p in points)
        {
            max = MathF.Max(max, MathF.Max(MathF.Abs(p.X), MathF.Abs(p.Y)) * Fixed);
        }

        if (!(max < 2e9f))
        {
            return false;
        }

        bool small = max <= short.MaxValue;
        SetGdiWorld(m);
        _w.Gdi(59, []); // EMR_BEGINPATH
        Vector2 start = default;
        bool open = false;
        int i = 0;
        while (i < points.Length)
        {
            int type = types[i] & 0x07;
            if (type == GdiPlusData.PointStart || !open)
            {
                start = points[i];
                MoveTo(points[i]);
                open = true;
                i++;
            }
            else
            {
                // A run of lines or of Beziers, up to the end of the figure.
                int from = i;
                while (i < points.Length && (types[i] & 0x07) == type && (i == from || (types[i - 1] & GdiPlusData.PointClose) == 0))
                {
                    i++;
                }

                if (type == GdiPlusData.PointBezier)
                {
                    i = from + ((i - from) / 3 * 3);
                    if (i == from)
                    {
                        i = points.Length; // a broken Bezier: the rest is left out
                        break;
                    }
                }

                PolyTo(points.AsSpan(from, i - from), type == GdiPlusData.PointBezier, small, m);
            }

            if (i > 0 && (types[i - 1] & GdiPlusData.PointClose) != 0)
            {
                _w.Gdi(61, []); // EMR_CLOSEFIGURE
                open = false;

                // Egami draws on from the figure's start after a close; GDI from where the next MoveTo puts it.
                if (i < points.Length && (types[i] & 0x07) != GdiPlusData.PointStart)
                {
                    MoveTo(start);
                    open = true;
                }
            }
        }

        _w.Gdi(60, []); // EMR_ENDPATH
        return true;
    }

    private void MoveTo(Vector2 p) => _w.Gdi(27, _w.Data().I32(Fix(p.X)).I32(Fix(p.Y)).Span); // EMR_MOVETOEX

    /// <summary>EMR_POLYLINETO16, EMR_POLYBEZIERTO16 or their 32-bit forms: device bounds, count, points.</summary>
    private void PolyTo(ReadOnlySpan<Vector2> points, bool bezier, bool small, Matrix3x2 m)
    {
        RectF device = RectF.Empty;
        Vector2 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (Vector2 p in points)
        {
            Vector2 d = Vector2.Transform(p, m);
            min = Vector2.Min(min, d);
            max = Vector2.Max(max, d);
        }

        device = RectF.FromEdges(min.X, min.Y, max.X, max.Y);
        RecordBuffer b = _w.Data();
        b.Bytes(Bounds16(device)).I32(points.Length);
        foreach (Vector2 p in points)
        {
            if (small)
            {
                b.I16(Fix(p.X)).I16(Fix(p.Y));
            }
            else
            {
                b.I32(Fix(p.X)).I32(Fix(p.Y));
            }
        }

        _w.Gdi(bezier ? (small ? 88 : 5) : (small ? 89 : 6), b.Span);
    }

    private static int Fix(float v) => (int)MathF.Round(v * Fixed);

    /// <summary>A RECTL of device bounds, inclusive, as records that carry bounds start.</summary>
    private static byte[] Bounds16(RectF device)
    {
        RecordBuffer b = new(16);
        if (device.IsEmpty || !device.IsFinite)
        {
            return b.I32(0).I32(0).I32(-1).I32(-1).ToArray();
        }

        static int Clamp(float v) => (int)Math.Clamp(v, -2e9f, 2e9f);
        return b.I32(Clamp(MathF.Floor(device.Left))).I32(Clamp(MathF.Floor(device.Top))).I32(Clamp(MathF.Ceiling(device.Right) - 1)).I32(Clamp(MathF.Ceiling(device.Bottom) - 1)).ToArray();
    }

    /// <summary>A solid brush of an opaque color (EMR_CREATEBRUSHINDIRECT: BS_SOLID, COLORREF, no hatch).</summary>
    private uint SolidBrush(Rgba8 color) => _w.GdiObject(39, new RecordBuffer(12).U32(0).U32(ColorRef(color)).U32(0).Span, Selected);

    /// <summary>
    /// A pen (EMR_EXTCREATEPEN): geometric, in sixteenths of a local unit, with end caps, join and a PS_USERSTYLE pattern for
    /// dashes, the phase rotated into the pattern since GDI has no dash offset; a hairline is a one-pixel cosmetic pen.
    /// </summary>
    private uint GdiPen(Stroke stroke, Rgba8 color, float scale)
    {
        RecordBuffer b = new(64);
        b.U32(0).U32(0).U32(0).U32(0); // no bitmap
        float[]? dashes = Intervals(stroke);
        if (stroke.IsHairline)
        {
            b.U32(0).U32(1).U32(0).U32(ColorRef(color)).U32(0).U32(0); // PS_COSMETIC | PS_SOLID, width 1, BS_SOLID
            return _w.GdiObject(95, b.Span, Selected);
        }

        uint style = 0x00010000; // PS_GEOMETRIC
        style |= stroke.Cap switch { LineCap.Butt => 0x200u, LineCap.Square => 0x100u, _ => 0u }; // PS_ENDCAP_FLAT, _SQUARE, _ROUND
        style |= stroke.Join switch { LineJoin.Miter => 0x2000u, LineJoin.Bevel => 0x1000u, _ => 0u }; // PS_JOIN_MITER, _BEVEL, _ROUND
        List<uint>? entries = null;
        if (dashes is not null)
        {
            style |= 7; // PS_USERSTYLE
            entries = [.. Rotate(dashes, stroke.Dash!.Phase).Select(v => (uint)Math.Clamp(MathF.Round(v * Fixed), 0, int.MaxValue))];
        }

        b.U32(style).U32((uint)Math.Max(1, Fix(stroke.Width))).U32(0).U32(ColorRef(color)).U32(0).U32((uint)(entries?.Count ?? 0));
        foreach (uint e in entries ?? [])
        {
            b.U32(e);
        }

        return _w.GdiObject(95, b.Span, Selected);
    }

    /// <summary>
    /// A dash pattern started at a phase: one period from the phase on, beginning with a dash (of length 0 when the phase falls in
    /// a gap), with a gap of 0 after a dash the phase cut in two, so it repeats as the original does.
    /// </summary>
    internal static float[] Rotate(float[] dashes, float phase)
    {
        float period = dashes.Sum();
        if (!(period > 0))
        {
            return dashes;
        }

        phase %= period;
        if (phase < 0)
        {
            phase += period;
        }

        if (phase == 0)
        {
            return dashes;
        }

        int k = 0;
        while (phase >= dashes[k])
        {
            phase -= dashes[k];
            k = (k + 1) % dashes.Length;
        }

        List<float> rotated = [];
        if (k % 2 == 1)
        {
            rotated.Add(0); // the phase falls in a gap
        }

        rotated.Add(dashes[k] - phase);
        for (int j = 1; j < dashes.Length; j++)
        {
            rotated.Add(dashes[(k + j) % dashes.Length]);
        }

        rotated.Add(phase);
        if (rotated.Count % 2 != 0)
        {
            rotated.Add(0);
        }

        return [.. rotated];
    }

    // ---- Colors ----

    private static Rgba8 Color8(RgbaF32 c, float opacity)
    {
        static byte B(float v) => (byte)MathF.Round(Math.Clamp(float.IsFinite(v) ? v : 0, 0, 1) * 255);
        return new Rgba8(B(c.R), B(c.G), B(c.B), B(c.A * opacity));
    }

    private static uint Argb(Rgba8 c) => ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;

    private static uint ColorRef(Rgba8 c) => c.R | ((uint)c.G << 8) | ((uint)c.B << 16);
}
