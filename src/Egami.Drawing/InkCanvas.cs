using System.Numerics;

using Egami.Colors;
using Egami.Drawing.Rasterization;
using Egami.Fonts;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Drawing;

/// <summary>How an <see cref="InkCanvas"/> turns colours that are not inks into its process inks.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="ink-canvas"/>
/// </example>
public sealed record class InkCanvasOptions
{
    private readonly ColorProfile _rgbSource = ColorProfile.Srgb;
    private readonly CanvasOptions _drawing = CanvasOptions.Default;

    /// <summary>Gets the default options: sRGB colours, relative colorimetric with black point compensation.</summary>
    public static InkCanvasOptions Default { get; } = new();

    /// <summary>Gets what RGB colours and images without a profile mean. Defaults to <see cref="ColorProfile.Srgb"/>.</summary>
    /// <exception cref="ArgumentException">The profile is not an RGB encoding.</exception>
    public ColorProfile RgbSource
    {
        get => _rgbSource;
        init => _rgbSource = value?.Model == ColorModel.Rgb ? value : throw new ArgumentException("RGB colours need an RGB encoding.", nameof(value));
    }

    /// <summary>Gets the rendering intent of conversions into the process inks. Defaults to relative colorimetric.</summary>
    public RenderingIntent Intent { get; init; } = RenderingIntent.RelativeColorimetric;

    /// <summary>Gets a value indicating whether conversions into the process inks use black point compensation. Defaults to <see langword="true"/>.</summary>
    public bool BlackPointCompensation { get; init; } = true;

    /// <summary>Gets the drawing settings shared with <see cref="RasterCanvas{TPixel}"/>: curve tolerance and colour glyphs.</summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    public CanvasOptions Drawing
    {
        get => _drawing;
        init => _drawing = value ?? throw new ArgumentNullException(nameof(value));
    }
}

/// <summary>
/// A canvas that draws into an <see cref="InkRaster"/>'s plates: printing inks instead of RGB. It shares
/// <see cref="RasterCanvas{TPixel}"/>'s rasterizer, clips and layers, so shapes, strokes and text cover exactly the same pixels;
/// where the RGB canvas blends colours, this one sets each plate. A paint's <see cref="SolidPaint.Ink"/> prints its inks by name
/// (inks the raster lacks through their appearance into the process inks); RGB colours, gradients and images convert into the
/// process inks through <see cref="InkCanvasOptions.RgbSource"/> and the ink set's profile.
/// </summary>
/// <remarks>
/// This release draws with <see cref="BlendMode.SourceOver"/> and <see cref="BlendMode.Source"/>, every plate painted (overprint
/// off), and isolated layers merged source-over. Other blend modes, overprint, group kinds and soft masks follow in later steps of
/// Egami 2.6.5 and throw <see cref="NotSupportedException"/> until then.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="ink-canvas"/>
/// </example>
public sealed class InkCanvas : Canvas
{
    private readonly RasterCanvas<RgbaF32> _inner;

    /// <summary>Initializes a new instance of the <see cref="InkCanvas"/> class over an ink raster.</summary>
    /// <param name="raster">The plates to draw on.</param>
    /// <param name="options">How other colours become inks, or <see langword="null"/> for <see cref="InkCanvasOptions.Default"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="raster"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="ink-canvas"/>
    /// </example>
    public InkCanvas(InkRaster raster, InkCanvasOptions? options = null)
        : base((raster ?? throw new ArgumentNullException(nameof(raster))).Width, raster.Height)
    {
        Raster = raster;
        Options = options ?? InkCanvasOptions.Default;
        InkResolver resolver = new(raster.Inks, Options.RgbSource, new ColorTransformOptions { Intent = Options.Intent, BlackPointCompensation = Options.BlackPointCompensation });
        _inner = new RasterCanvas<RgbaF32>(new InkTarget(raster, resolver, new Rect(0, 0, raster.Width, raster.Height), owned: false), raster.Context, Options.Drawing);
    }

    /// <summary>Gets the plates drawn on.</summary>
    public InkRaster Raster { get; }

    /// <summary>Gets the options.</summary>
    public InkCanvasOptions Options { get; }

    /// <summary>Fills the clip with an ink colour, replacing what is there: <see cref="InkColor.None"/> leaves blank paper.</summary>
    /// <param name="ink">The colour.</param>
    /// <exception cref="ArgumentNullException"><paramref name="ink"/> is <see langword="null"/>.</exception>
    public void Clear(InkColor ink) => DrawPaint(new SolidPaint(ink ?? throw new ArgumentNullException(nameof(ink))) { BlendMode = BlendMode.Source });

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc/>
    protected override void OnSave() => _inner.Save();

    /// <inheritdoc/>
    protected override void OnRestore() => _inner.Restore();

    /// <inheritdoc/>
    protected override void OnSaveLayer(LayerOptions options)
    {
        Sync();
        _inner.SaveLayer(options);
    }

    /// <inheritdoc/>
    protected override void OnRestoreLayer() => _inner.Restore();

    /// <inheritdoc/>
    protected override void OnSetMatrix(Matrix3x2 matrix) => _inner.SetMatrix(matrix);

    /// <inheritdoc/>
    protected override void OnClipRect(RectF rect, ClipOp op, bool antialias)
    {
        Sync();
        _inner.ClipRect(rect, op, antialias);
    }

    /// <inheritdoc/>
    protected override void OnClipRoundRect(RoundRect roundRect, ClipOp op, bool antialias)
    {
        Sync();
        _inner.ClipRoundRect(roundRect, op, antialias);
    }

    /// <inheritdoc/>
    protected override void OnClipPath(Path path, ClipOp op, bool antialias)
    {
        Sync();
        _inner.ClipPath(path, op, antialias);
    }

    /// <inheritdoc/>
    protected override void OnClear(RgbaF32 color)
    {
        Sync();
        _inner.Clear(color);
    }

    /// <inheritdoc/>
    protected override void OnDrawPaint(Paint paint)
    {
        Sync();
        _inner.DrawPaint(paint);
    }

    /// <inheritdoc/>
    protected override void OnDrawRect(RectF rect, Stroke? stroke, Paint paint)
    {
        Sync();
        if (stroke is null)
        {
            _inner.FillRect(rect, paint);
        }
        else
        {
            _inner.StrokeRect(rect, stroke, paint);
        }
    }

    /// <inheritdoc/>
    protected override void OnDrawRoundRect(RoundRect roundRect, Stroke? stroke, Paint paint)
    {
        Sync();
        if (stroke is null)
        {
            _inner.FillRoundRect(roundRect, paint);
        }
        else
        {
            _inner.StrokeRoundRect(roundRect, stroke, paint);
        }
    }

    /// <inheritdoc/>
    protected override void OnDrawOval(RectF oval, Stroke? stroke, Paint paint)
    {
        Sync();
        if (stroke is null)
        {
            _inner.FillOval(oval, paint);
        }
        else
        {
            _inner.StrokeOval(oval, stroke, paint);
        }
    }

    /// <inheritdoc/>
    protected override void OnDrawPath(Path path, Stroke? stroke, Paint paint)
    {
        Sync();
        if (stroke is null)
        {
            _inner.FillPath(path, paint);
        }
        else
        {
            _inner.StrokePath(path, stroke, paint);
        }
    }

    /// <inheritdoc/>
    protected override void OnDrawPoints(PointMode mode, ReadOnlySpan<Vector2> points, Stroke stroke, Paint paint)
    {
        Sync();
        _inner.DrawPoints(mode, points, stroke, paint);
    }

    /// <inheritdoc/>
    protected override void OnDrawImage(Raster image, RectF source, RectF destination, SamplingOptions sampling, Paint? paint)
    {
        Sync();
        _inner.DrawImage(image, source, destination, sampling, paint);
    }

    /// <inheritdoc/>
    protected override void OnDrawImageLattice(Raster image, Lattice lattice, RectF destination, SamplingOptions sampling, Paint? paint)
    {
        Sync();
        _inner.DrawImageLattice(image, lattice, destination, sampling, paint);
    }

    /// <inheritdoc/>
    protected override void OnDrawAtlas(Raster image, ReadOnlySpan<SpriteTransform> transforms, ReadOnlySpan<RectF> sources, ReadOnlySpan<RgbaF32> colors, BlendMode colorBlend, SamplingOptions sampling, Paint? paint)
    {
        Sync();
        _inner.DrawAtlas(image, transforms, sources, colors, colorBlend, sampling, null, paint);
    }

    /// <inheritdoc/>
    protected override void OnDrawMesh(Mesh mesh, Paint paint, BlendMode colorBlend, ReadOnlySpan<GradientStop> valueStops)
    {
        Sync();
        _inner.DrawMesh(mesh, paint, colorBlend, valueStops);
    }

    /// <inheritdoc/>
    protected override void OnDrawPatch(Patch patch, Paint paint, BlendMode colorBlend, ReadOnlySpan<GradientStop> valueStops)
    {
        Sync();
        _inner.DrawPatch(patch, paint, colorBlend, valueStops);
    }

    /// <inheritdoc/>
    protected override void OnDrawImageMask(Raster mask, RectF destination, Paint paint, SamplingOptions sampling)
    {
        Sync();
        _inner.DrawImageMask(mask, destination, paint, sampling);
    }

    /// <inheritdoc/>
    protected override void OnDrawGlyphRun(GlyphRun run, Vector2 origin, Paint paint)
    {
        Sync();
        _inner.DrawGlyphRun(run, origin, paint);
    }

    private void Sync()
    {
        if (_inner.Matrix != Matrix)
        {
            _inner.SetMatrix(Matrix);
        }
    }
}
