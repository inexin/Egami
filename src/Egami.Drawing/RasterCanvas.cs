using System.Buffers;
using System.Numerics;

using Egami.Drawing.Geometry;
using Egami.Drawing.Rasterization;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Drawing;

/// <summary>
/// The raster backend: a canvas that draws into a raster's pixels, with exact-area antialiasing, blended through Egami's
/// Compositor in the pixel type's own precision. A clip that is a pixel-aligned rectangle stays a rectangle; any other clip
/// becomes a coverage mask over the clip's bounds. A call whose area exceeds 256 KiB of destination rows spreads its rows over
/// up to <see cref="EgamiContext.MaxParallelism"/> cores, with the same pixels at any core count.
/// <para>
/// A layer from <see cref="Canvas.SaveLayer(LayerOptions?)"/> is premultiplied in the raster's class (PremulRgba8 for 8-bit
/// rasters, PremulRgbaF32 for deeper ones) and covers only its bounds inside the clip, grown by its filter's reach; its memory
/// comes from the raster's <see cref="EgamiContext"/> and is released at <see cref="Canvas.Restore"/>.
/// </para>
/// </summary>
/// <typeparam name="TPixel">The raster's pixel type.</typeparam>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="canvas"/>
/// </example>
public sealed partial class RasterCanvas<TPixel> : Canvas
    where TPixel : unmanaged, IPixelType<TPixel>
{
    private readonly EgamiContext _context;
    private readonly CoverageRasterizer _rasterizer;
    private readonly EdgeList _edges = new();
    private readonly DrawTools _tools;
    private readonly Stack<ClipState> _clips = new();
    private readonly Stack<Layer> _layers = new();
    private RasterTarget _target;
    private ClipState _clip;

    /// <summary>Initializes a new instance of the <see cref="RasterCanvas{TPixel}"/> class over a raster's first frame.</summary>
    /// <param name="raster">The raster to draw on.</param>
    /// <param name="options">The settings, or <see langword="null"/> for <see cref="CanvasOptions.Default"/>.</param>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="canvas"/>
    /// </example>
    public RasterCanvas(Raster<TPixel> raster, CanvasOptions? options = null)
        : this(Plane(raster), raster.Context, options)
    {
    }

    internal RasterCanvas(PixelPlane<TPixel> plane, EgamiContext context, CanvasOptions? options)
        : base(plane.Width, plane.Height)
    {
        _context = context;
        Options = options ?? CanvasOptions.Default;
        _rasterizer = new CoverageRasterizer(context.Memory, context.MaxParallelism);
        _tools = new DrawTools(_rasterizer, _edges, Options, context);
        _target = new RasterTarget<TPixel>(plane);
        _clip = new ClipState(new Rect(0, 0, plane.Width, plane.Height), null);
    }

    /// <summary>Gets the canvas's settings.</summary>
    public CanvasOptions Options { get; }

    /// <summary>Gets or sets the number of row bands every call uses, for tests; 0 lets the size decide.</summary>
    internal int ForcedBands
    {
        get => _rasterizer.ForcedBands;
        set => _rasterizer.ForcedBands = value;
    }

    /// <summary>Gets the device rectangle the current layer covers, for tests; the whole raster outside layers.</summary>
    internal Rect TargetArea => _target.Area;

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        _clip.Mask?.Dispose();
        _clip = new ClipState(default, null);
        _rasterizer.Dispose();
        base.Dispose(disposing);
    }

    /// <inheritdoc/>
    protected override void OnSave() => _clips.Push(_clip);

    /// <inheritdoc/>
    protected override void OnRestore() => PopClip();

    /// <inheritdoc/>
    /// <remarks>
    /// Allocates the layer over the options' bounds inside the clip, grown by the filter's reach (the whole bounds, or the raster,
    /// when the reach is unknown), and starts it transparent, as a copy of the backdrop, or as the filtered backdrop. Inside the
    /// layer the clip is the layer's rectangle: the clip of this call applies once, when the layer is merged.
    /// </remarks>
    protected override void OnSaveLayer(LayerOptions options)
    {
        _clips.Push(_clip);
        RasterTarget parent = _target;
        Rect? bounds = options.Bounds is RectF b ? DeviceRect(b) : null;
        Rect merge = _clip.Bounds.Intersect(parent.Area);
        if (bounds is Rect limit)
        {
            merge = merge.Intersect(limit);
        }

        if (merge.IsEmpty)
        {
            _layers.Push(new Layer(parent, null, options, merge));
            _clip = new ClipState(default, null);
            return;
        }

        // Grown by the filter's reach so a blur near the clip's edge sees what is drawn just outside it.
        Rect area = merge;
        if (options.Filter is not null)
        {
            area = RasterOps.Reach(options.Filter) is OperationReach reach ? Grow(merge, reach) : (bounds ?? parent.Area);
            if (bounds is Rect limited)
            {
                area = area.Intersect(limited);
            }

            area = Union(area, merge);
        }

        RasterTarget layer = parent.CreateLayer(area, _context);
        try
        {
            if (options.Backdrop is not null)
            {
                StartFromBackdrop(parent, layer, area, options.Backdrop);
            }
            else if (!options.Isolated)
            {
                Rect shared = area.Intersect(parent.Area);
                if (!shared.IsEmpty)
                {
                    layer.CopyFrom(parent, shared);
                }
            }
        }
        catch
        {
            layer.Dispose();
            throw;
        }

        _layers.Push(new Layer(parent, layer, options, merge));
        _target = layer;
        _clip = new ClipState(area, null);
    }

    /// <inheritdoc/>
    /// <remarks>Filters the layer, multiplies it by its soft mask, and merges it with its opacity and blend mode through the restored clip.</remarks>
    protected override void OnRestoreLayer()
    {
        Layer layer = _layers.Pop();
        PopClip();
        _target = layer.Parent;
        if (layer.Target is not RasterTarget pixels)
        {
            return;
        }

        try
        {
            LayerOptions options = layer.Options;
            if (options.Filter is not null)
            {
                pixels.Filter(options.Filter);
            }

            if (options.Mask is not null)
            {
                ApplyMask(pixels, options.Mask, layer.Merge);
            }

            Rect merge = layer.Merge.Intersect(_clip.Bounds);
            if (!merge.IsEmpty)
            {
                _target.Merge(pixels, merge, _clip.Mask, options.BlendMode, options.Opacity, Options.ColorSpace);
            }
        }
        finally
        {
            pixels.Dispose();
        }
    }

    /// <inheritdoc/>
    protected override void OnClipRect(RectF rect, ClipOp op, bool antialias)
    {
        Matrix3x2 m = Matrix;
        if (op == ClipOp.Intersect && m.M12 == 0 && m.M21 == 0)
        {
            Vector2 a = Vector2.Transform(rect.Position, m), b = Vector2.Transform(new Vector2(rect.Right, rect.Bottom), m);
            RectF device = RectF.FromEdges(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y), MathF.Max(a.X, b.X), MathF.Max(a.Y, b.Y));
            if (TryPixelRect(device, antialias, out Rect pixels))
            {
                SetClip(new ClipState(_clip.Bounds.Intersect(pixels), _clip.Mask));
                return;
            }
        }

        OnClipPath(Path.Rect(rect), op, antialias);
    }

    /// <inheritdoc/>
    protected override void OnClipPath(Path path, ClipOp op, bool antialias)
    {
        Rect bounds = _clip.Bounds;
        if (bounds.IsEmpty)
        {
            return;
        }

        // The shape's coverage over the current bounds, then combined with the current clip.
        _edges.Clear();
        Flattener.Flatten(path, Matrix, Options.Tolerance, _edges);
        int size = bounds.Width * bounds.Height;
        IMemoryOwner<byte> coverage = _context.Memory.Allocate(size, ClearMode.Zero);
        try
        {
            _rasterizer.Fill(_edges, bounds, path.FillRule, antialias, new BufferSink(coverage.Memory[..size], bounds), 1);
            Span<byte> c = coverage.Memory.Span[..size];
            if (op == ClipOp.Difference)
            {
                for (int i = 0; i < c.Length; i++)
                {
                    c[i] = (byte)(255 - c[i]);
                }
            }

            if (_clip.Mask is ClipMask old)
            {
                for (int y = bounds.Y; y < bounds.Bottom; y++)
                {
                    CoverageMath.Multiply(c.Slice((y - bounds.Y) * bounds.Width, bounds.Width), old.Row(y).Slice(bounds.X - old.Bounds.X, bounds.Width));
                }
            }

            // An intersection can shrink the bounds to the shape's own.
            Rect kept = op == ClipOp.Intersect ? bounds.Intersect(_edges.Bounds.RoundOut()) : bounds;
            ClipMask mask = new(bounds, coverage);
            coverage = null!;
            SetClip(new ClipState(kept, mask));
        }
        finally
        {
            coverage?.Dispose();
        }
    }

    /// <inheritdoc/>
    /// <remarks>Fills the clip's whole pixels directly, through its mask when it has one.</remarks>
    protected override void OnDrawPaint(Paint paint)
    {
        if (_clip.Bounds.IsEmpty)
        {
            return;
        }

        RectF b = _clip.Bounds;
        _edges.Clear();
        _edges.Add(new Vector2(b.Right, b.Top), new Vector2(b.Right, b.Bottom));
        _edges.Add(new Vector2(b.Left, b.Bottom), new Vector2(b.Left, b.Top));
        FillEdges(FillRule.NonZero, antialias: true, paint with { Blur = null }, 255);
    }

    /// <inheritdoc/>
    /// <remarks>An axis-aligned rectangle with a normal <see cref="Paint.Blur"/> is blurred exactly, from the error function.</remarks>
    protected override void OnDrawRect(RectF rect, Stroke? stroke, Paint paint)
    {
        if (stroke is null && TryAnalyticBlur(paint, out MaskBlur blur, out Matrix3x2 m))
        {
            Vector2 a = Vector2.Transform(rect.Position, m), c = Vector2.Transform(new Vector2(rect.Right, rect.Bottom), m);
            RectF device = RectF.FromEdges(MathF.Min(a.X, c.X), MathF.Min(a.Y, c.Y), MathF.Max(a.X, c.X), MathF.Max(a.Y, c.Y));
            DrawAnalytic(paint, sink => MaskBlurs.FillRect(device, blur.Sigma, _clip.Bounds, sink));
            return;
        }

        base.OnDrawRect(rect, stroke, paint);
    }

    /// <inheritdoc/>
    protected override void OnDrawPath(Path path, Stroke? stroke, Paint paint)
    {
        if (_clip.Bounds.IsEmpty)
        {
            return;
        }

        Matrix3x2 m = Matrix;
        byte scale = 255;
        FillRule rule = path.FillRule;
        _edges.Clear();
        if (stroke is null)
        {
            Flattener.Flatten(path, m, Options.Tolerance, _edges);
        }
        else
        {
            if (stroke.Dash is not null)
            {
                path = stroke.Dash.Apply(path);
            }

            float deviceWidth = stroke.Width * MathF.Sqrt(MathF.Abs(m.GetDeterminant()));
            rule = FillRule.NonZero;
            if (stroke.IsHairline || deviceWidth < 1)
            {
                // A hairline is one device pixel wide whatever the transform; a thinner stroke draws as one, fainter by its width.
                scale = stroke.IsHairline ? (byte)255 : (byte)Math.Clamp((int)MathF.Round(deviceWidth * 255), 0, 255);
                if (scale == 0)
                {
                    return;
                }

                Path outline = Hairline.Outline(path.TransformForDrawing(m), stroke.Cap, Options.Tolerance);
                Flattener.Flatten(outline, Matrix3x2.Identity, Options.Tolerance, _edges);
            }
            else
            {
                float maxScale = MathF.Max(new Vector2(m.M11, m.M12).Length(), new Vector2(m.M21, m.M22).Length());
                Path outline = Stroker.Outline(path, stroke, Options.Tolerance / MathF.Max(maxScale, 1e-6f));
                Flattener.Flatten(outline, m, Options.Tolerance, _edges);
            }
        }

        FillEdges(rule, paint.Antialias, paint, scale);
    }

    /// <inheritdoc/>
    protected override void OnDrawImage(Raster image, RectF source, RectF destination, SamplingOptions sampling, Paint? paint)
    {
        if (!_clip.Bounds.IsEmpty)
        {
            _target.DrawImage(_tools, image, source, destination, Matrix, sampling, paint, _clip.Bounds, _clip.Mask);
        }
    }

    private static PixelPlane<TPixel> Plane(Raster<TPixel> raster)
    {
        ArgumentNullException.ThrowIfNull(raster);
        return raster.Frames[0].Plane;
    }

    private static Rect Union(Rect a, Rect b)
    {
        int left = Math.Min(a.X, b.X), top = Math.Min(a.Y, b.Y);
        return new Rect(left, top, Math.Max(a.Right, b.Right) - left, Math.Max(a.Bottom, b.Bottom) - top);
    }

    private static Rect Grow(Rect r, OperationReach reach)
        => new(r.X - reach.X, r.Y - reach.Y, r.Width + (2 * reach.X), r.Height + (2 * reach.Y));

    /// <summary>
    /// A device rectangle that covers whole pixels: its edges are whole numbers, or, without antialiasing, the pixels whose centers
    /// it holds.
    /// </summary>
    private static bool TryPixelRect(RectF device, bool antialias, out Rect pixels)
    {
        pixels = default;
        if (!device.IsFinite)
        {
            return false;
        }

        double l = device.Left, t = device.Top, r = device.Right, b = device.Bottom;
        if (!antialias)
        {
            (l, t, r, b) = (Math.Ceiling(l - 0.5), Math.Ceiling(t - 0.5), Math.Ceiling(r - 0.5), Math.Ceiling(b - 0.5));
        }
        else if (l != Math.Round(l) || t != Math.Round(t) || r != Math.Round(r) || b != Math.Round(b))
        {
            return false;
        }

        l = Math.Clamp(l, -1e9, 1e9);
        t = Math.Clamp(t, -1e9, 1e9);
        r = Math.Clamp(r, -1e9, 1e9);
        b = Math.Clamp(b, -1e9, 1e9);
        pixels = r > l && b > t ? new Rect((int)l, (int)t, (int)(r - l), (int)(b - t)) : default;
        return true;
    }

    /// <summary>Fills the edge list with a paint through the clip, blurred when the paint asks.</summary>
    private void FillEdges(FillRule rule, bool antialias, Paint paint, byte scale)
    {
        ISpanSink? sink = _target.CreateSink(paint, Matrix, Options.ColorSpace, _clip.Mask, scale);
        if (sink is null)
        {
            return;
        }

        if (paint.Blur is { Sigma: > 0 } blur)
        {
            MaskBlurs.Fill(_tools, _edges, rule, antialias, blur, _clip.Bounds, sink);
        }
        else
        {
            _rasterizer.Fill(_edges, _clip.Bounds, rule, antialias, sink, _target.BytesPerPixel);
        }
    }

    /// <summary>Whether a fill can take the exact blur: a normal blur, antialiased, under a scale and translation.</summary>
    private bool TryAnalyticBlur(Paint paint, out MaskBlur blur, out Matrix3x2 m)
    {
        m = Matrix;
        blur = paint.Blur ?? default;
        return paint.Blur is { Sigma: > 0, Style: BlurStyle.Normal } && paint.Antialias && m.M12 == 0 && m.M21 == 0 && !_clip.Bounds.IsEmpty;
    }

    private void DrawAnalytic(Paint paint, Action<ISpanSink> fill)
    {
        if (_target.CreateSink(paint, Matrix, Options.ColorSpace, _clip.Mask, 255) is ISpanSink sink)
        {
            fill(sink);
        }
    }

    /// <summary>A local rectangle's device bounds, rounded out to whole pixels.</summary>
    private Rect DeviceRect(RectF local)
    {
        if (local.IsEmpty)
        {
            return default;
        }

        Matrix3x2 m = Matrix;
        RectF device = RectF.FromPoints(
        [
            Vector2.Transform(new Vector2(local.Left, local.Top), m),
            Vector2.Transform(new Vector2(local.Right, local.Top), m),
            Vector2.Transform(new Vector2(local.Right, local.Bottom), m),
            Vector2.Transform(new Vector2(local.Left, local.Bottom), m),
        ]);
        return device.IsFinite ? device.Intersect(new RectF(-1e8f, -1e8f, 2e8f, 2e8f)).RoundOut() : default;
    }

    /// <summary>Starts a layer as the backdrop run through a chain, reading as far around the layer as the chain reaches.</summary>
    private void StartFromBackdrop(RasterTarget parent, RasterTarget layer, Rect area, Action<RasterOps> backdrop)
    {
        Rect read = (RasterOps.Reach(backdrop) is OperationReach reach ? Grow(area, reach) : parent.Area).Intersect(parent.Area);
        if (read.IsEmpty)
        {
            return;
        }

        using RasterTarget copy = parent.CreateLayer(read, _context);
        copy.CopyFrom(parent, read);
        copy.Filter(backdrop);
        Rect shared = area.Intersect(read);
        if (!shared.IsEmpty)
        {
            layer.CopyFrom(copy, shared);
        }
    }

    /// <summary>Draws a soft mask over the merge area and multiplies the layer by it.</summary>
    private void ApplyMask(RasterTarget layer, SoftMask mask, Rect area)
    {
        if (area.IsEmpty)
        {
            return;
        }

        using Raster<RgbaF32> drawn = Raster.Create<RgbaF32>(area.Width, area.Height, _context);
        using (RasterCanvas<RgbaF32> canvas = new(drawn, Options))
        {
            if (mask.Kind == SoftMaskKind.Luminosity)
            {
                canvas.Clear(mask.Backdrop);
            }

            canvas.SetMatrix(Matrix * Matrix3x2.CreateTranslation(-area.X, -area.Y));
            mask.Draw(canvas);
        }

        float[] values = new float[area.Width];
        Func<float, float>? transfer = mask.Transfer;
        for (int y = 0; y < area.Height; y++)
        {
            ReadOnlySpan<RgbaF32> row = drawn.Row(y);
            for (int x = 0; x < values.Length; x++)
            {
                RgbaF32 c = row[x];
                float v = mask.Kind == SoftMaskKind.Alpha ? c.A : (0.2126f * c.R) + (0.7152f * c.G) + (0.0722f * c.B);
                v = Math.Clamp(v, 0, 1);
                values[x] = transfer is null ? v : Math.Clamp(transfer(v), 0, 1);
            }

            layer.Multiply(area.Y + y, area.X, values);
        }
    }

    private void PopClip()
    {
        ClipState restored = _clips.Pop();

        // A mask made since the matching Save is referenced by nothing else.
        if (_clip.Mask is not null && !ReferenceEquals(_clip.Mask, restored.Mask))
        {
            _clip.Mask.Dispose();
        }

        _clip = restored;
    }

    private void SetClip(ClipState next)
    {
        // The current mask is dropped unless a saved state still holds it.
        if (_clip.Mask is not null && !ReferenceEquals(_clip.Mask, next.Mask) && !(_clips.Count > 0 && ReferenceEquals(_clips.Peek().Mask, _clip.Mask)))
        {
            _clip.Mask.Dispose();
        }

        _clip = next;
    }

    private readonly record struct ClipState(Rect Bounds, ClipMask? Mask);

    /// <summary>A layer in progress: what it draws over, its pixels (none when the clip was empty), its options and where it merges.</summary>
    private readonly record struct Layer(RasterTarget Parent, RasterTarget? Target, LayerOptions Options, Rect Merge);
}
