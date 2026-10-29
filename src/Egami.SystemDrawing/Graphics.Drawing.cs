using System.ComponentModel;
using System.Numerics;

using Egami.Drawing;
using Egami.GdiPlus;
using Egami.Operations;
using Egami.Pixels;
using Egami.SystemDrawing.Drawing2D;
using Egami.SystemDrawing.Imaging;
using Egami.SystemDrawing.Internal;

namespace Egami.SystemDrawing;

using Point = System.Drawing.Point;

/// <summary>The clip, and the cores every Draw, Fill and DrawImage overload forwards to.</summary>
public sealed partial class Graphics
{
    /// <summary>GDI+'s bounds of an infinite clip, in device pixels.</summary>
    private static readonly RectangleF Infinite = new(-4194304, -4194304, 8388608, 8388608);

    /// <summary>Gets or sets the clip, in world coordinates; a copy either way.</summary>
    public Region Clip
    {
        get => new(State.Clip?.Transform(ToWorld));
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            SetClip(value, CombineMode.Replace);
        }
    }

    /// <summary>Gets the bounds of the clip, in world coordinates.</summary>
    public RectangleF ClipBounds => Bounds(State.Clip, ToWorld);

    /// <summary>Gets the bounds of the clip and the bitmap together, in world coordinates.</summary>
    public RectangleF VisibleClipBounds => Bounds(GdiPlusRegions.Combine(EffectiveClip, Device, GdiPlusRegions.Intersect), ToWorld);

    /// <summary>Gets a value indicating whether the clip is empty.</summary>
    public bool IsClipEmpty => State.Clip is Path clip && Empty(clip);

    /// <summary>Gets a value indicating whether nothing of the bitmap is inside the clip.</summary>
    public bool IsVisibleClipEmpty => GdiPlusRegions.Combine(EffectiveClip, Device, GdiPlusRegions.Intersect) is Path visible && Empty(visible);

    /// <summary>Device pixels back to world coordinates.</summary>
    private Matrix3x2 ToWorld => Matrix3x2.Invert(ImageTransform, out Matrix3x2 back) ? back : Matrix3x2.Identity;

    /// <summary>The bitmap's area, a pixel wider each way, in world coordinates as shapes are mapped.</summary>
    private Path DeviceInWorld => Path.Rect(new RectF(-1, -1, _image.Width + 2, _image.Height + 2))
        .Transform(Matrix3x2.Invert(ShapeTransform, out Matrix3x2 back) ? back : Matrix3x2.Identity);

    /// <summary>The bitmap's area in device pixels.</summary>
    private Path Device => Path.Rect(new RectF(0, 0, _image.Width, _image.Height));

    /// <summary>Makes the clip another graphics' clip.</summary>
    /// <param name="g">The other graphics.</param>
    public void SetClip(Graphics g) => SetClip(g, CombineMode.Replace);

    /// <summary>Combines the clip with another graphics' clip.</summary>
    /// <param name="g">The other graphics.</param>
    /// <param name="combineMode">How to combine.</param>
    public void SetClip(Graphics g, CombineMode combineMode)
    {
        ArgumentNullException.ThrowIfNull(g);
        Combine(g.State.Clip, combineMode);
    }

    /// <summary>Makes the clip a rectangle.</summary>
    /// <param name="rect">The rectangle, in world coordinates.</param>
    public void SetClip(Rectangle rect) => SetClip((RectangleF)rect, CombineMode.Replace);

    /// <summary>Combines the clip with a rectangle.</summary>
    /// <param name="rect">The rectangle, in world coordinates.</param>
    /// <param name="combineMode">How to combine.</param>
    public void SetClip(Rectangle rect, CombineMode combineMode) => SetClip((RectangleF)rect, combineMode);

    /// <summary>Makes the clip a rectangle.</summary>
    /// <param name="rect">The rectangle, in world coordinates.</param>
    public void SetClip(RectangleF rect) => SetClip(rect, CombineMode.Replace);

    /// <summary>Combines the clip with a rectangle.</summary>
    /// <param name="rect">The rectangle, in world coordinates.</param>
    /// <param name="combineMode">How to combine.</param>
    public void SetClip(RectangleF rect, CombineMode combineMode) => Combine(Region.Rect(rect).Transform(ImageTransform), combineMode);

    /// <summary>Makes the clip a path's area.</summary>
    /// <param name="path">The path, in world coordinates.</param>
    public void SetClip(GraphicsPath path) => SetClip(path, CombineMode.Replace);

    /// <summary>Combines the clip with a path's area.</summary>
    /// <param name="path">The path, in world coordinates.</param>
    /// <param name="combineMode">How to combine.</param>
    public void SetClip(GraphicsPath path, CombineMode combineMode)
    {
        ArgumentNullException.ThrowIfNull(path);
        Combine(path.ToPath(ImageTransform), combineMode);
    }

    /// <summary>Strokes a path with a pen; each figure is stroked on its own, closed ones joined all round.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="path">The path.</param>
    public void DrawPath(Pen pen, GraphicsPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        Stroke(pen, path.ToPath());
    }

    /// <summary>Fills a path with a brush, by its fill mode.</summary>
    /// <param name="brush">The brush.</param>
    /// <param name="path">The path.</param>
    public void FillPath(Brush brush, GraphicsPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        Fill(brush, path.ToPath());
    }

    /// <summary>Combines the clip with a region.</summary>
    /// <param name="region">The region, in world coordinates.</param>
    /// <param name="combineMode">How to combine.</param>
    public void SetClip(Region region, CombineMode combineMode)
    {
        ArgumentNullException.ThrowIfNull(region);
        Combine(region.Shape?.Transform(ImageTransform), combineMode);
    }

    /// <summary>Intersects the clip with a rectangle.</summary>
    /// <param name="rect">The rectangle, in world coordinates.</param>
    public void IntersectClip(Rectangle rect) => SetClip((RectangleF)rect, CombineMode.Intersect);

    /// <summary>Intersects the clip with a rectangle.</summary>
    /// <param name="rect">The rectangle, in world coordinates.</param>
    public void IntersectClip(RectangleF rect) => SetClip(rect, CombineMode.Intersect);

    /// <summary>Intersects the clip with a region.</summary>
    /// <param name="region">The region, in world coordinates.</param>
    public void IntersectClip(Region region) => SetClip(region, CombineMode.Intersect);

    /// <summary>Removes a rectangle from the clip.</summary>
    /// <param name="rect">The rectangle, in world coordinates.</param>
    public void ExcludeClip(Rectangle rect) => SetClip((RectangleF)rect, CombineMode.Exclude);

    /// <summary>Removes a region from the clip.</summary>
    /// <param name="region">The region, in world coordinates.</param>
    public void ExcludeClip(Region region) => SetClip(region, CombineMode.Exclude);

    /// <summary>Removes the clip.</summary>
    public void ResetClip() => State.Clip = null;

    /// <summary>Moves the clip.</summary>
    /// <param name="dx">The horizontal distance, in world units.</param>
    /// <param name="dy">The vertical distance, in world units.</param>
    public void TranslateClip(float dx, float dy)
    {
        Vector2 d = Vector2.TransformNormal(new Vector2(dx, dy), ImageTransform);
        State.Clip = State.Clip is Path clip ? Snap(clip.Transform(Matrix3x2.CreateTranslation(d))) : null;
    }

    /// <summary>Moves the clip.</summary>
    /// <param name="dx">The horizontal distance, in world units.</param>
    /// <param name="dy">The vertical distance, in world units.</param>
    public void TranslateClip(int dx, int dy) => TranslateClip((float)dx, dy);

    /// <summary>Gets the translation of the transform and the clip.</summary>
    /// <param name="offset">The translation.</param>
    public void GetContextInfo(out PointF offset) => GetContextInfo(out offset, out _);

    /// <summary>Gets the translation of the transform and the clip.</summary>
    /// <param name="offset">The translation.</param>
    /// <param name="clip">The clip in world coordinates, or null when there is none.</param>
    public void GetContextInfo(out PointF offset, out Region? clip)
    {
        Matrix3x2 m = State.World * State.Container;
        offset = new PointF(m.M31, m.M32);
        clip = State.Clip is null ? null : Clip;
    }

    /// <summary>Not supported: the GDI+ context as Windows objects. Always throws.</summary>
    /// <returns>Never returns.</returns>
    [Obsolete(Internal.Diagnostics.HandlesMessage, DiagnosticId = Internal.Diagnostics.Handles, UrlFormat = Internal.Diagnostics.Url)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public object GetContextInfo() => throw GdiErrors.WindowsOnly("Graphics.GetContextInfo()", "Use GetContextInfo(out PointF, out Region).");

    /// <summary>Gets whether a point is inside the bitmap and the clip.</summary>
    /// <param name="point">The point, in world coordinates.</param>
    /// <returns><see langword="true"/> when visible.</returns>
    public bool IsVisible(PointF point)
    {
        // The pixel the point rounds to, as GDI+ tests it.
        Vector2 d = Vector2.Transform(new Vector2(point.X, point.Y), ImageTransform);
        Vector2 p = new(MathF.Floor(d.X + 0.5f) + 0.5f, MathF.Floor(d.Y + 0.5f) + 0.5f);
        return p.X >= 0 && p.Y >= 0 && p.X < _image.Width && p.Y < _image.Height && (EffectiveClip is not Path clip || clip.Contains(p));
    }

    /// <summary>Gets whether any of a rectangle is inside the bitmap and the clip.</summary>
    /// <param name="rect">The rectangle, in world coordinates.</param>
    /// <returns><see langword="true"/> when visible.</returns>
    public bool IsVisible(RectangleF rect)
    {
        Path device = Region.Rect(rect).Transform(ImageTransform);
        return GdiPlusRegions.Combine(GdiPlusRegions.Combine(EffectiveClip, Device, GdiPlusRegions.Intersect), device, GdiPlusRegions.Intersect) is Path visible && !Empty(visible);
    }

    /// <summary>Strokes a world-space shape with a pen, as GDI+ strokes: at least a pixel wide, with its caps, dashes and alignment.</summary>
    internal void Stroke(Pen pen, Path world)
    {
        ArgumentNullException.ThrowIfNull(pen);
        if (world.IsEmpty)
        {
            return;
        }

        Matrix3x2 full = ShapeTransform;
        GdiPlusPen geometry = pen.Geometry();

        // The pen's own transform shapes its nib: the line is stroked in pen space and the outline mapped back, so a pen scaled
        // 3 by 1 draws three times as wide across vertical lines as across horizontal ones.
        Matrix3x2 nib = geometry.Transform with { M31 = 0, M32 = 0 };
        if (!nib.IsIdentity && Matrix3x2.Invert(nib, out Matrix3x2 toPen))
        {
            world = world.Transform(toPen);
            full = nib * full;
        }

        float scale = MathF.Sqrt(MathF.Abs(full.GetDeterminant()));
        float width = GdiPlusPens.DeviceWidth(geometry.Width, scale);
        GdiPlusPenOutline outline = GdiPlusPens.Outline(world, geometry, width);
        Path body = outline.Outline.WithFillRule(FillRule.NonZero).Transform(full);
        Path? clip = outline.Clip?.Transform(full);
        Path[] caps = [.. outline.Caps.Select(c => c.Transform(full))];
        RectF bounds = body.Bounds;
        foreach (Path cap in caps)
        {
            bounds = Union(bounds, cap.Bounds);
        }

        if (!Sane(bounds))
        {
            return;
        }

        BrushFill fill = pen.PaintBrush.Fill(this);
        Draw(bounds, canvas =>
        {
            if (clip is not null)
            {
                canvas.ClipPath(clip, ClipOp.Intersect, antialias: Smooth);
            }

            Paint(canvas, body, fill);
            foreach (Path cap in caps)
            {
                Paint(canvas, cap, fill);
            }
        });
    }

    /// <summary>Fills a world-space shape with a brush.</summary>
    internal void Fill(Brush brush, Path world)
    {
        ArgumentNullException.ThrowIfNull(brush);
        if (world.IsEmpty)
        {
            return;
        }

        Path device = world.Transform(ShapeTransform);
        if (!Sane(device.Bounds))
        {
            return;
        }

        BrushFill fill = brush.Fill(this);
        if (fill.Paint is Paint plain && AxisRect(device) is RectF rect)
        {
            // An unrotated rectangle fills through the canvas' rectangle path, which needs no edge list.
            Paint paint = plain with { Antialias = Smooth, BlendMode = Blend };
            Draw(rect, canvas => canvas.FillRect(rect, paint));
            return;
        }

        Draw(device.Bounds, canvas => Paint(canvas, device, fill));
    }

    /// <summary>The rectangle a path is when it is one closed figure of four axis-aligned sides, else null.</summary>
    private static RectF? AxisRect(Path path)
    {
        ReadOnlySpan<PathVerb> verbs = path.Verbs;
        ReadOnlySpan<Vector2> p = path.Points;
        if (verbs.Length is < 4 or > 6 || verbs[0] != PathVerb.Move || p.Length is < 4 or > 5)
        {
            return null;
        }

        for (int i = 1; i < verbs.Length; i++)
        {
            if (verbs[i] is not (PathVerb.Line or PathVerb.Close))
            {
                return null;
            }
        }

        if (p.Length == 5 && p[4] != p[0])
        {
            return null;
        }

        bool horizontalFirst = p[0].Y == p[1].Y && p[1].X == p[2].X && p[2].Y == p[3].Y && p[3].X == p[0].X;
        bool verticalFirst = p[0].X == p[1].X && p[1].Y == p[2].Y && p[2].X == p[3].X && p[3].Y == p[0].Y;
        if (!horizontalFirst && !verticalFirst)
        {
            return null;
        }

        RectF b = path.Bounds;
        return b.Width > 0 && b.Height > 0 ? b : null;
    }

    /// <summary>
    /// Draws part of an image so that its top-left, top-right and bottom-left corners land on three world points. The source is in
    /// <paramref name="unit"/>s at the image's resolution; like shapes, images get GDI+'s pixel offset.
    /// </summary>
    internal void DrawImageCore(Image image, PointF p0, PointF p1, PointF p2, RectangleF source, GraphicsUnit unit, ImageAttributes? attributes, DrawImageAbort? abort = null, IntPtr data = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        Alive();
        if (image is not (Bitmap or Metafile))
        {
            throw new NotSupportedException("Egami.SystemDrawing draws bitmaps and metafiles.");
        }

        if (unit is < GraphicsUnit.World or > GraphicsUnit.Millimeter)
        {
            throw GdiErrors.InvalidParameter();
        }

        float sx = SourceScale(unit, image.HorizontalResolution), sy = SourceScale(unit, image.VerticalResolution);
        RectF src = new(source.X * sx, source.Y * sy, source.Width * sx, source.Height * sy);
        if (abort is not null && abort(data))
        {
            // GDI+ reports an aborted draw as an error.
            throw GdiErrors.Generic();
        }

        if (src.Width == 0 || src.Height == 0)
        {
            return;
        }

        Matrix3x2 map = Matrix3x2.CreateTranslation(-src.X, -src.Y)
            * new Matrix3x2((p1.X - p0.X) / src.Width, (p1.Y - p0.Y) / src.Width, (p2.X - p0.X) / src.Height, (p2.Y - p0.Y) / src.Height, p0.X, p0.Y);
        Matrix3x2 full = map * ShapeTransform;
        RectF bounds = Path.Rect(src).Transform(full).Bounds;
        if (!Sane(bounds))
        {
            return;
        }

        if (image is Metafile metafile)
        {
            DrawMetafile(metafile, map, src);
            return;
        }

        // 32-bit and 24-bit bitmaps are sampled in place, no copy, unless attributes change their colors or the bitmap is the one drawn on.
        Bitmap bitmap = (Bitmap)image;
        bool view = attributes is null && !ReferenceEquals(bitmap, _image) && bitmap.PixelFormat is PixelFormat.Format32bppArgb or PixelFormat.Format32bppPArgb or PixelFormat.Format24bppRgb;
        using Raster pixels = view ? bitmap.AsRaster() : bitmap.ToRgba8();
        Raster drawn = attributes?.State.Apply((Raster<Rgba8>)pixels, GdiPlusImageAttributes.TypeBitmap) ?? pixels;
        try
        {
            // Without a wrap mode GDI+ samples transparency beyond the image, so its edges fade; a wrap mode tiles it instead.
            int wrap = attributes is { WrapSet: true } ? attributes.State.WrapMode : GdiPlusBrushes.WrapClamp;
            (TileMode tx, TileMode ty) = GdiPlusBrushes.Wrap(wrap);
            Rgba8 clamp = wrap == GdiPlusBrushes.WrapClamp && attributes is { WrapSet: true } ? attributes.State.ClampColor : default;
            SamplingOptions sampling = new() { Filter = Filter };
            Draw(bounds, canvas =>
            {
                canvas.Concat(full);
                if (clamp.A != 0)
                {
                    // The clamp color fills only what lies outside the image.
                    int count = canvas.Save();
                    canvas.ClipRect(new RectF(0, 0, drawn.Width, drawn.Height), ClipOp.Difference, antialias: false);
                    canvas.FillRect(src, new SolidPaint(clamp) { Antialias = Smooth, BlendMode = Blend });
                    canvas.RestoreToCount(count);
                }

                // Without the half-pixel offset GDI+ puts source pixel centers, like device ones, on whole coordinates.
                Matrix3x2 centers = Offset ? Matrix3x2.Identity : Matrix3x2.CreateTranslation(-0.5f, -0.5f);
                canvas.FillRect(src, new ImagePaint(drawn, tx, ty, sampling) { LocalMatrix = centers, Antialias = Smooth, BlendMode = Blend });
            });
        }
        finally
        {
            if (!ReferenceEquals(drawn, pixels))
            {
                drawn.Dispose();
            }
        }
    }

    /// <summary>
    /// Plays a metafile as vectors: its frame (in pixels at its own resolution) mapped by the source-to-destination map, cut to
    /// the source rectangle. Metafile records follow GDI's own pixel rules, which the player keeps, so no pixel offset is added.
    /// </summary>
    private void DrawMetafile(Metafile metafile, Matrix3x2 map, RectF src)
    {
        Matrix3x2 full = map * ImageTransform;
        RectF bounds = Path.Rect(src).Transform(full).Bounds;
        if (!Sane(bounds))
        {
            return;
        }

        RectangleF frame = metafile.FrameBounds;
        Draw(bounds, canvas =>
        {
            canvas.Concat(full);
            canvas.ClipRect(src, ClipOp.Intersect, antialias: false);
            metafile.Picture.Draw(canvas, new RectF(frame.X, frame.Y, frame.Width, frame.Height));
        });
    }

    /// <summary>
    /// Draws a whole image at a world point at its physical size: its pixels at its resolution, in page units at the graphics'
    /// resolution, as GDI+ draws DrawImage(image, x, y).
    /// </summary>
    internal void DrawImageAt(Image image, float x, float y, RectangleF? source = null, GraphicsUnit unit = GraphicsUnit.Pixel)
    {
        ArgumentNullException.ThrowIfNull(image);
        RectangleF src = source ?? WholeSource(image);
        float sx = SourceScale(unit, image.HorizontalResolution), sy = SourceScale(unit, image.VerticalResolution);
        float page = PageFactor;
        float w = src.Width * sx * DpiX / image.HorizontalResolution / page, h = src.Height * sy * DpiY / image.VerticalResolution / page;
        DrawImageCore(image, new PointF(x, y), new PointF(x + w, y), new PointF(x, y + h), src, unit, null);
    }

    /// <summary>Draws an image into a world rectangle.</summary>
    internal void DrawImageRect(Image image, RectangleF dest, RectangleF? source = null, GraphicsUnit unit = GraphicsUnit.Pixel, ImageAttributes? attributes = null, DrawImageAbort? abort = null, IntPtr data = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        DrawImageCore(image, new PointF(dest.X, dest.Y), new PointF(dest.Right, dest.Y), new PointF(dest.X, dest.Bottom), source ?? WholeSource(image), unit, attributes, abort, data);
    }

    /// <summary>Draws an image onto a parallelogram given by three world points: top-left, top-right and bottom-left.</summary>
    internal void DrawImagePoints(Image image, ReadOnlySpan<PointF> points, RectangleF? source = null, GraphicsUnit unit = GraphicsUnit.Pixel, ImageAttributes? attributes = null, DrawImageAbort? abort = null, IntPtr data = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (points.Length != 3)
        {
            throw GdiErrors.InvalidParameter();
        }

        DrawImageCore(image, points[0], points[1], points[2], source ?? WholeSource(image), unit, attributes, abort, data);
    }

    /// <summary>
    /// The source rectangle of a whole image, as GDI+ takes it from GetBounds: a bitmap's pixels from (0, 0), a metafile's frame
    /// where it lies, which need not start at (0, 0).
    /// </summary>
    private static RectangleF WholeSource(Image image) => image is Metafile metafile ? metafile.FrameBounds : new RectangleF(0, 0, image.Width, image.Height);

    /// <summary>Pixels per source unit at an image's resolution.</summary>
    private static float SourceScale(GraphicsUnit unit, float dpi) => unit switch
    {
        GraphicsUnit.Point => dpi / 72,
        GraphicsUnit.Inch => dpi,
        GraphicsUnit.Document => dpi / 300,
        GraphicsUnit.Millimeter => dpi / 25.4f,
        _ => 1,
    };

    private static RectF Union(RectF a, RectF b)
    {
        float x0 = MathF.Min(a.X, b.X), y0 = MathF.Min(a.Y, b.Y);
        return new RectF(x0, y0, MathF.Max(a.Right, b.Right) - x0, MathF.Max(a.Bottom, b.Bottom) - y0);
    }

    /// <summary>Whether a device area is finite and within a million pixels; GDI+ draws nothing further out.</summary>
    private static bool Sane(RectF b) =>
        float.IsFinite(b.X) && float.IsFinite(b.Y) && float.IsFinite(b.Width) && float.IsFinite(b.Height)
        && MathF.Abs(b.X) < 1e6f && MathF.Abs(b.Y) < 1e6f && b.Width < 2e6f && b.Height < 2e6f;

    private static bool Empty(Path path) => path.IsEmpty || path.Bounds.Width <= 0 || path.Bounds.Height <= 0;

    private static RectangleF Bounds(Path? shape, Matrix3x2 m)
    {
        if (shape is null)
        {
            RectangleF i = Infinite;
            Vector2 a = Vector2.Transform(new Vector2(i.X, i.Y), m), b = Vector2.Transform(new Vector2(i.Right, i.Bottom), m);
            return RectangleF.FromLTRB(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y), MathF.Max(a.X, b.X), MathF.Max(a.Y, b.Y));
        }

        if (Empty(shape))
        {
            return RectangleF.Empty;
        }

        // As GDI+: the device bounds of the clip's pixels, as a rectangle mapped back to world coordinates.
        RectF r = shape.Bounds;
        Vector2 p0 = Vector2.Transform(new Vector2(r.X, r.Y), m), p1 = Vector2.Transform(new Vector2(r.Right, r.Y), m);
        Vector2 p2 = Vector2.Transform(new Vector2(r.X, r.Bottom), m), p3 = Vector2.Transform(new Vector2(r.Right, r.Bottom), m);
        Vector2 min = Vector2.Min(Vector2.Min(p0, p1), Vector2.Min(p2, p3)), max = Vector2.Max(Vector2.Max(p0, p1), Vector2.Max(p2, p3));
        return RectangleF.FromLTRB(min.X, min.Y, max.X, max.Y);
    }

    /// <summary>The sampling filter for the interpolation mode: nearest, bilinear or bicubic.</summary>
    internal ResampleFilter Filter => State.Interpolation switch
    {
        InterpolationMode.NearestNeighbor => ResampleFilter.Nearest,
        InterpolationMode.Bicubic or InterpolationMode.HighQualityBicubic or InterpolationMode.High => ResampleFilter.Bicubic,
        _ => ResampleFilter.Bilinear,
    };

    private void Combine(Path? device, CombineMode mode)
    {
        if (mode is < CombineMode.Replace or > CombineMode.Complement)
        {
            throw GdiErrors.InvalidParameter();
        }

        // GDI+ keeps the clip as device pixels.
        Path? clip = mode == CombineMode.Replace ? device : GdiPlusRegions.Combine(State.Clip, device, (int)mode);
        State.Clip = clip is null ? null : Snap(clip);
    }

    /// <summary>A device shape as the pixels GDI+ clips to: those whose centers it holds, the centers half a pixel in with the pixel offset.</summary>
    private Path Snap(Path device) => PixelScans.Snap(Offset ? device.Transform(Matrix3x2.CreateTranslation(-0.5f, -0.5f)) : device);

    /// <summary>Fills a device-space shape with a brush's paint, or its mesh clipped to the shape.</summary>
    private void Paint(Canvas canvas, Path device, BrushFill fill)
    {
        if (fill.Mesh is Mesh mesh)
        {
            int count = canvas.Save();
            try
            {
                canvas.ClipPath(device, ClipOp.Intersect, antialias: Smooth);
                canvas.DrawMesh(mesh, new SolidPaint(new Rgba8(255, 255, 255, 255)) { Antialias = Smooth, BlendMode = Blend });
            }
            finally
            {
                canvas.RestoreToCount(count);
            }
        }
        else if (fill.Paint is Paint paint)
        {
            canvas.FillPath(device, paint with { Antialias = Smooth, BlendMode = Blend });
        }
    }
}
