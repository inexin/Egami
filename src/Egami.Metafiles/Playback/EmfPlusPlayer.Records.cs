using System.Numerics;

using Egami.Drawing;
using Egami.Pixels;

using static Egami.Metafiles.Reading.MetafileReader;

namespace Egami.Metafiles.Playback;

/// <summary>The EMF+ record dispatch ([MS-EMFPLUS] 2.3).</summary>
internal sealed partial class EmfPlusPlayer
{
    private const ushort Compressed = 0x4000, Relative = 0x0800, ColorBrush = 0x8000;

    private void PlayRecord(ushort type, ushort flags, ReadOnlySpan<byte> d)
    {
        int at = 0;
        switch (type)
        {
            case 0x4001: // Header
            case 0x4002: // EndOfFile
            case 0x4003: // Comment
            case 0x4005: // MultiFormatStart
            case 0x4006: // MultiFormatSection
            case 0x4007: // MultiFormatEnd
            case 0x401F: // SetTextRenderingHint (below)
            case 0x4020: // SetTextContrast
            case 0x4024: // SetCompositingQuality
            case 0x401D: // SetRenderingOrigin
                if (type == 0x401F)
                {
                    _state = _state with { TextHint = flags & 0xFF };
                }

                break;
            case 0x4008: // Object
                StoreObject(flags, d);
                break;
            case 0x4009: // Clear
                _canvas.DrawPaint(new SolidPaint(PlusObjectReader.Argb(U32(d, 0))) { BlendMode = Operations.BlendMode.Source });
                break;
            case 0x400A: // FillRects
                {
                    PlusFill paint = BrushPaint(flags, U32(d, 0));
                    int n = Bounded(I32(d, 4), d.Length - 8, (flags & Compressed) != 0 ? 8 : 16);
                    at = 8;
                    PathBuilder b = new();
                    for (int i = 0; i < n; i++)
                    {
                        b.AddRect(PlusObjectReader.Rect(d, ref at, (flags & Compressed) != 0));
                    }

                    Fill(b.ToPath(FillRule.NonZero), paint);
                    break;
                }

            case 0x400B: // DrawRects
                {
                    int n = Bounded(I32(d, 0), d.Length - 4, (flags & Compressed) != 0 ? 8 : 16);
                    at = 4;
                    PathBuilder b = new();
                    for (int i = 0; i < n; i++)
                    {
                        b.AddRect(PlusObjectReader.Rect(d, ref at, (flags & Compressed) != 0));
                    }

                    Stroke(b.ToPath(), Pen(flags));
                    break;
                }

            case 0x400C: // FillPolygon
                {
                    PlusFill paint = BrushPaint(flags, U32(d, 0));
                    at = 8;
                    Vector2[] points = PlusObjectReader.ReadPoints(d, ref at, I32(d, 4), (flags & Compressed) != 0, (flags & Relative) != 0);
                    Fill(Path.Polygon(points).WithFillRule((flags & 0x2000) != 0 ? FillRule.NonZero : FillRule.EvenOdd), paint);
                    break;
                }

            case 0x400D: // DrawLines
                {
                    at = 4;
                    Vector2[] points = PlusObjectReader.ReadPoints(d, ref at, I32(d, 0), (flags & Compressed) != 0, (flags & Relative) != 0);
                    Stroke(Path.Polygon(points, close: (flags & 0x2000) != 0), Pen(flags));
                    break;
                }

            case 0x400E: // FillEllipse
                Fill(Path.Oval(Rect(d, 4, flags)), BrushPaint(flags, U32(d, 0)));
                break;
            case 0x400F: // DrawEllipse
                Stroke(Path.Oval(Rect(d, 0, flags)), Pen(flags));
                break;
            case 0x4010: // FillPie
                Fill(Path.Pie(Rect(d, 12, flags), F32(d, 4), F32(d, 8)), BrushPaint(flags, U32(d, 0)));
                break;
            case 0x4011: // DrawPie
                Stroke(Path.Pie(Rect(d, 8, flags), F32(d, 0), F32(d, 4)), Pen(flags));
                break;
            case 0x4012: // DrawArc
                Stroke(Path.Arc(Rect(d, 8, flags), F32(d, 0), F32(d, 4)), Pen(flags));
                break;
            case 0x4013: // FillRegion
                if (_objects[flags & 0xFF] is PlusRegion region)
                {
                    Fill(region.Path ?? Path.Rect(new RectF(-1e6f, -1e6f, 2e6f, 2e6f)), BrushPaint(flags, U32(d, 0)));
                }

                break;
            case 0x4014: // FillPath
                if (_objects[flags & 0xFF] is PlusPath fill)
                {
                    Fill(fill.Path, BrushPaint(flags, U32(d, 0)));
                }

                break;
            case 0x4015: // DrawPath
                if (_objects[flags & 0xFF] is PlusPath draw)
                {
                    Stroke(draw.Path, _objects[U32(d, 0) & 0xFF] as PlusPen);
                }

                break;
            case 0x4016: // FillClosedCurve
                {
                    PlusFill paint = BrushPaint(flags, U32(d, 0));
                    at = 12;
                    Vector2[] points = PlusObjectReader.ReadPoints(d, ref at, I32(d, 8), (flags & Compressed) != 0, (flags & Relative) != 0);
                    Fill(Cardinal(points, F32(d, 4), closed: true).WithFillRule((flags & 0x2000) != 0 ? FillRule.NonZero : FillRule.EvenOdd), paint);
                    break;
                }

            case 0x4017: // DrawClosedCurve
                {
                    at = 8;
                    Vector2[] points = PlusObjectReader.ReadPoints(d, ref at, I32(d, 4), (flags & Compressed) != 0, (flags & Relative) != 0);
                    Stroke(Cardinal(points, F32(d, 0), closed: true), Pen(flags));
                    break;
                }

            case 0x4018: // DrawCurve: Tension, Offset, NumSegments, Count, points
                {
                    at = 16;
                    Vector2[] points = PlusObjectReader.ReadPoints(d, ref at, I32(d, 12), (flags & Compressed) != 0, (flags & Relative) != 0);
                    int offset = Math.Clamp(I32(d, 4), 0, Math.Max(0, points.Length - 1)), segments = Math.Clamp(I32(d, 8), 0, Math.Max(0, points.Length - 1 - offset));
                    Stroke(Cardinal(points, F32(d, 0), closed: false, offset, segments), Pen(flags));
                    break;
                }

            case 0x4019: // DrawBeziers
                {
                    at = 4;
                    Vector2[] points = PlusObjectReader.ReadPoints(d, ref at, I32(d, 0), (flags & Compressed) != 0, (flags & Relative) != 0);
                    if (points.Length >= 4)
                    {
                        PathBuilder b = new PathBuilder().MoveTo(points[0]);
                        for (int i = 1; i + 2 < points.Length; i += 3)
                        {
                            b.CubicTo(points[i], points[i + 1], points[i + 2]);
                        }

                        Stroke(b.ToPath(), Pen(flags));
                    }

                    break;
                }

            case 0x401A: // DrawImage
            case 0x401B: // DrawImagePoints
                DrawImage(type, flags, d);
                break;
            case 0x401C: // DrawString
                DrawString(flags, d);
                break;
            case 0x4036: // DrawDriverString
                DrawDriverString(flags, d);
                break;
            case 0x401E: // SetAntiAliasMode: the smoothing mode in bits 1 to 7
                _state = _state with { Smoothing = (flags >> 1) & 0x7F };
                break;
            case 0x4021: // SetInterpolationMode
                _state = _state with { Interpolation = flags & 0xFF };
                break;
            case 0x4022: // SetPixelOffsetMode
                _state = _state with { PixelOffset = flags & 0xFF };
                break;
            case 0x4023: // SetCompositingMode
                _state = _state with { Compositing = flags & 0xFF };
                break;
            case 0x4025: // Save
                _saved[I32(d, 0)] = _state;
                break;
            case 0x4026: // Restore
            case 0x4029: // EndContainer
                if (_saved.Remove(I32(d, 0), out State? restored))
                {
                    bool clip = !ReferenceEquals(restored.Clip, _state.Clip);
                    _state = restored;
                    if (clip)
                    {
                        ApplyClip();
                    }
                }

                break;
            case 0x4027: // BeginContainer: page unit in the flags, destination and source rectangles, stack index
                {
                    RectF dest = new(F32(d, 0), F32(d, 4), F32(d, 8), F32(d, 12)), src = new(F32(d, 16), F32(d, 20), F32(d, 24), F32(d, 28));
                    _saved[I32(d, 32)] = _state;
                    float unit = UnitScale((flags >> 8) & 0xFF) / UnitScale(_state.PageUnit) / _state.PageScale;
                    Matrix3x2 map = src.Width != 0 && src.Height != 0
                        ? Matrix3x2.CreateTranslation(-src.X, -src.Y) * Matrix3x2.CreateScale(dest.Width / src.Width * unit, dest.Height / src.Height * unit) * Matrix3x2.CreateTranslation(dest.X * unit, dest.Y * unit)
                        : Matrix3x2.Identity;
                    _state = _state with { Container = map * _state.World * _state.Container, World = Matrix3x2.Identity };
                    break;
                }

            case 0x4028: // BeginContainerNoParams
                _saved[I32(d, 0)] = _state;
                _state = _state with { Container = _state.World * _state.Container, World = Matrix3x2.Identity };
                break;
            case 0x402A: // SetWorldTransform
                {
                    Matrix3x2 m = Matrix(d, 0);
                    if (IsFinite(m))
                    {
                        _state = _state with { World = m };
                    }

                    break;
                }

            case 0x402B: // ResetWorldTransform
                _state = _state with { World = Matrix3x2.Identity };
                break;
            case 0x402C: // MultiplyWorldTransform
                Transform(Matrix(d, 0), flags);
                break;
            case 0x402D: // TranslateWorldTransform
                Transform(Matrix3x2.CreateTranslation(F32(d, 0), F32(d, 4)), flags);
                break;
            case 0x402E: // ScaleWorldTransform
                Transform(Matrix3x2.CreateScale(F32(d, 0), F32(d, 4)), flags);
                break;
            case 0x402F: // RotateWorldTransform
                Transform(Matrix3x2.CreateRotation(F32(d, 0) * MathF.PI / 180), flags);
                break;
            case 0x4030: // SetPageTransform
                {
                    float scale = F32(d, 0);
                    _state = _state with { PageUnit = flags & 0xFF, PageScale = float.IsFinite(scale) && scale > 0 ? scale : 1 };
                    break;
                }

            case 0x4031: // ResetClip
                _state = _state with { Clip = null };
                ApplyClip();
                break;
            case 0x4032: // SetClipRect
                SetClip(Path.Rect(new RectF(F32(d, 0), F32(d, 4), F32(d, 8), F32(d, 12))), (flags >> 8) & 0xF);
                break;
            case 0x4033: // SetClipPath
                if (_objects[flags & 0xFF] is PlusPath clipPath)
                {
                    SetClip(clipPath.Path, (flags >> 8) & 0xF);
                }

                break;
            case 0x4034: // SetClipRegion
                if (_objects[flags & 0xFF] is PlusRegion clipRegion)
                {
                    SetClip(clipRegion.Path, (flags >> 8) & 0xF);
                }

                break;
            case 0x4035: // OffsetClip
                if (_state.Clip is Path current)
                {
                    Vector2 v = Vector2.TransformNormal(new Vector2(F32(d, 0), F32(d, 4)), Full);
                    _state = _state with { Clip = current.Transform(Matrix3x2.CreateTranslation(v)) };
                    ApplyClip();
                }

                break;
            case 0x4038: // SerializableObject: an image effect for the next DrawImage
                StoreEffect(d);
                break;
            default:
                Skip($"EMF+ type 0x{type:X4}");
                break;
        }
    }

    private static int Bounded(int count, int available, int size) =>
        count < 0 || count > available / size ? throw new ArgumentException("A count does not fit the record.") : count;

    private static RectF Rect(ReadOnlySpan<byte> d, int at, ushort flags) => PlusObjectReader.Rect(d, ref at, (flags & Compressed) != 0);

    private PlusPen? Pen(ushort flags) => _objects[flags & 0xFF] as PlusPen;

    /// <summary>Puts an object in the table: its type in bits 8 to 14 of the flags, its index in the low byte.</summary>
    private void StoreObject(ushort flags, ReadOnlySpan<byte> d)
    {
        int index = flags & 0xFF, type = (flags >> 8) & 0x7F;
        if (index >= _objects.Length)
        {
            return;
        }

        try
        {
            _objects[index] = PlusObjectReader.Read(type, d, LoadMetafile, LoadImage);
        }
        catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            _objects[index] = null;
            WarnOnce("Metafile.BadObject", $"An EMF+ object (type {type}) cannot be read; what uses it is not drawn: {e.Message}", "object:" + type);
        }
    }

    // ---- Cardinal splines ----

    /// <summary>
    /// A cardinal spline through the points as cubic Beziers: GDI+ takes each segment's control points a third of the tension
    /// times the neighbours' difference from its ends (tension 0 gives straight lines, 0.5 the default curve).
    /// </summary>
    private static Path Cardinal(Vector2[] points, float tension, bool closed, int offset = 0, int segments = -1)
    {
        PathBuilder b = new();
        int n = points.Length;
        if (n < 2)
        {
            return b.ToPath();
        }

        if (segments < 0)
        {
            segments = closed ? n : n - 1;
        }

        float t = tension / 3;
        Vector2 P(int i) => closed ? points[((i % n) + n) % n] : points[Math.Clamp(i, 0, n - 1)];
        b.MoveTo(P(offset));
        for (int i = offset; i < offset + segments; i++)
        {
            Vector2 p0 = P(i - 1), p1 = P(i), p2 = P(i + 1), p3 = P(i + 2);
            b.CubicTo(p1 + ((p2 - p0) * t), p2 - ((p3 - p1) * t), p2);
        }

        if (closed)
        {
            b.Close();
        }

        return b.ToPath();
    }

    // ---- Fill and stroke ----

    private void Fill(Path world, PlusFill fill)
    {
        if (!GdiPlayer.Sane(world.Transform(Full)))
        {
            WarnOnce("Metafile.BadRecord", "A shape lies more than a million pixels away; it is not drawn.", "insane");
            return;
        }

        if (fill.Mesh is Mesh mesh)
        {
            int count = _canvas.Save();
            try
            {
                _canvas.ClipPath(world.Transform(Full), ClipOp.Intersect, antialias: Smooth);
                _canvas.DrawMesh(mesh, new SolidPaint(new Rgba8(255, 255, 255, 255)) { Antialias = Smooth });
            }
            finally
            {
                _canvas.RestoreToCount(count);
            }

            return;
        }

        if (fill.Paint is Paint paint)
        {
            _canvas.FillPath(world.Transform(Full), paint);
        }
    }
}
