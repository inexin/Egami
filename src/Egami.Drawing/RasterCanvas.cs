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
/// </summary>
/// <typeparam name="TPixel">The raster's pixel type.</typeparam>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="canvas"/>
/// </example>
public sealed class RasterCanvas<TPixel> : Canvas
    where TPixel : unmanaged, IPixelType<TPixel>
{
    private readonly PixelPlane<TPixel> _plane;
    private readonly EgamiContext _context;
    private readonly CoverageRasterizer _rasterizer;
    private readonly EdgeList _edges = new();
    private readonly Stack<ClipState> _clips = new();
    private ClipState _clip;

    /// <summary>Initializes a new instance of the <see cref="RasterCanvas{TPixel}"/> class over a raster's first frame.</summary>
    /// <param name="raster">The raster to draw on.</param>
    /// <param name="options">The settings, or <see langword="null"/> for <see cref="CanvasOptions.Default"/>.</param>
    public RasterCanvas(Raster<TPixel> raster, CanvasOptions? options = null)
        : this(Plane(raster), raster.Context, options)
    {
    }

    internal RasterCanvas(PixelPlane<TPixel> plane, EgamiContext context, CanvasOptions? options)
        : base(plane.Width, plane.Height)
    {
        _plane = plane;
        _context = context;
        Options = options ?? CanvasOptions.Default;
        _rasterizer = new CoverageRasterizer(context.Memory, context.MaxParallelism);
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
    protected override void OnRestore()
    {
        ClipState restored = _clips.Pop();

        // A mask made since the matching Save is referenced by nothing else.
        if (_clip.Mask is not null && !ReferenceEquals(_clip.Mask, restored.Mask))
        {
            _clip.Mask.Dispose();
        }

        _clip = restored;
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
        if (_clip.Bounds.IsEmpty || paint is not SolidPaint solid)
        {
            return;
        }

        RectF b = _clip.Bounds;
        _edges.Clear();
        _edges.Add(new Vector2(b.Right, b.Top), new Vector2(b.Right, b.Bottom));
        _edges.Add(new Vector2(b.Left, b.Bottom), new Vector2(b.Left, b.Top));
        SolidSink<TPixel> sink = new(_plane, solid, Options.ColorSpace, _clip.Mask, 255);
        _rasterizer.Fill(_edges, _clip.Bounds, FillRule.NonZero, antialias: true, sink, System.Runtime.CompilerServices.Unsafe.SizeOf<TPixel>());
    }

    /// <inheritdoc/>
    protected override void OnDrawPath(Path path, Stroke? stroke, Paint paint)
    {
        if (_clip.Bounds.IsEmpty || paint is not SolidPaint solid)
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

                Path device = path.Transform(m);
                Path outline = Stroker.Outline(device, stroke with { Width = 1 }, Options.Tolerance);
                Flattener.Flatten(outline, Matrix3x2.Identity, Options.Tolerance, _edges);
            }
            else
            {
                float maxScale = MathF.Max(new Vector2(m.M11, m.M12).Length(), new Vector2(m.M21, m.M22).Length());
                Path outline = Stroker.Outline(path, stroke, Options.Tolerance / MathF.Max(maxScale, 1e-6f));
                Flattener.Flatten(outline, m, Options.Tolerance, _edges);
            }
        }

        SolidSink<TPixel> sink = new(_plane, solid, Options.ColorSpace, _clip.Mask, scale);
        _rasterizer.Fill(_edges, _clip.Bounds, rule, paint.Antialias, sink, System.Runtime.CompilerServices.Unsafe.SizeOf<TPixel>());
    }

    /// <inheritdoc/>
    protected override void OnDrawImage(Raster image, RectF source, RectF destination, SamplingOptions sampling, Paint? paint)
    {
        Rect clip = _clip.Bounds;
        if (clip.IsEmpty)
        {
            return;
        }

        BlendMode mode = paint?.BlendMode ?? BlendMode.SourceOver;
        float opacity = paint?.Opacity ?? 1;
        bool antialias = paint?.Antialias ?? true;
        ColorSpace colorSpace = Options.ColorSpace;

        // Image pixel (u, v) → device: scale and move the source rectangle onto the destination, then the canvas transform.
        float sx = destination.Width / source.Width, sy = destination.Height / source.Height;
        Matrix3x2 toDevice = Matrix3x2.CreateTranslation(-source.X, -source.Y) * Matrix3x2.CreateScale(sx, sy)
            * Matrix3x2.CreateTranslation(destination.X, destination.Y) * Matrix;

        Raster<TPixel>? converted = image as Raster<TPixel>;
        bool owned = converted is null;
        converted ??= image.As<TPixel>();
        try
        {
            if (IsIntegerPlacement(toDevice, source, out Point offset))
            {
                DrawPlaced(converted, source.RoundOut(), offset, clip, mode, opacity, colorSpace);
                return;
            }

            // The destination's device outline gives the edge coverage; the transform runs over the part of it inside the clip.
            Path area = Path.Rect(destination).Transform(Matrix);
            _edges.Clear();
            Flattener.Flatten(area, Matrix3x2.Identity, Options.Tolerance, _edges);
            Rect viewport = clip.Intersect(_edges.Bounds.RoundOut());
            if (viewport.IsEmpty)
            {
                return;
            }

            // The source is cut to the whole pixels it uses, so samples near its edges read only pixels inside it.
            Rect crop = source.RoundOut();
            Matrix3x2 cropToDevice = Matrix3x2.CreateTranslation(crop.X, crop.Y) * toDevice;
            TransformOptions options = new()
            {
                Filter = sampling.Filter,
                Edges = sampling.Edges == EdgeMode.Transparent ? EdgeMode.Clamp : sampling.Edges,
                Background = sampling.Background,
                ColorSpace = sampling.ColorSpace,
                Viewport = viewport,
            };
            using Raster<TPixel> placed = converted.Derive(ops =>
            {
                if (crop != new Rect(0, 0, converted.Width, converted.Height))
                {
                    ops.Crop(crop);
                }

                ops.Transform(cropToDevice, options);
            });
            ImageSink<TPixel> sink = new(_plane, placed, viewport.Position, mode, opacity, colorSpace, _clip.Mask);
            _rasterizer.Fill(_edges, viewport, FillRule.NonZero, antialias, sink, System.Runtime.CompilerServices.Unsafe.SizeOf<TPixel>());
        }
        finally
        {
            if (owned)
            {
                converted.Dispose();
            }
        }
    }

    private static PixelPlane<TPixel> Plane(Raster<TPixel> raster)
    {
        ArgumentNullException.ThrowIfNull(raster);
        return raster.Frames[0].Plane;
    }

    /// <summary>Whether the image maps onto whole device pixels one to one: no scale, no turn, a whole-pixel shift.</summary>
    private static bool IsIntegerPlacement(Matrix3x2 m, RectF source, out Point offset)
    {
        offset = default;
        if (m.M11 != 1 || m.M22 != 1 || m.M12 != 0 || m.M21 != 0 || m.M31 != MathF.Round(m.M31) || m.M32 != MathF.Round(m.M32))
        {
            return false;
        }

        if (source.X != MathF.Round(source.X) || source.Y != MathF.Round(source.Y) || source.Width != MathF.Round(source.Width) || source.Height != MathF.Round(source.Height))
        {
            return false;
        }

        offset = new Point((int)m.M31, (int)m.M32);
        return true;
    }

    /// <summary>Blends source rows straight onto the destination, as 2.5.1's DrawImage does, through the clip mask when there is one.</summary>
    private void DrawPlaced(Raster<TPixel> image, Rect source, Point offset, Rect clip, BlendMode mode, float opacity, ColorSpace colorSpace)
    {
        // The source rectangle lands at its own position plus the offset.
        Rect target = new Rect(source.X + offset.X, source.Y + offset.Y, source.Width, source.Height).Intersect(clip);
        if (target.IsEmpty)
        {
            return;
        }

        byte[]? scratch = _clip.Mask is null ? null : ArrayPool<byte>.Shared.Rent(target.Width);
        try
        {
            for (int y = target.Y; y < target.Bottom; y++)
            {
                ReadOnlySpan<TPixel> from = image.Row(y - offset.Y).Slice(target.X - offset.X, target.Width);
                Span<TPixel> to = _plane.Row(y).Slice(target.X, target.Width);
                if (_clip.Mask is ClipMask mask)
                {
                    Span<byte> coverage = scratch.AsSpan(0, target.Width);
                    mask.Row(y).Slice(target.X - mask.Bounds.X, target.Width).CopyTo(coverage);
                    Compositor.Blend(mode, from, to, coverage, opacity, colorSpace);
                }
                else
                {
                    Compositor.Blend(mode, from, to, opacity, colorSpace);
                }
            }
        }
        finally
        {
            if (scratch is not null)
            {
                ArrayPool<byte>.Shared.Return(scratch);
            }
        }
    }

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
}
