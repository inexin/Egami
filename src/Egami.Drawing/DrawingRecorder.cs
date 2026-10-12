using System.Numerics;

using Egami.Drawing.Recording;
using Egami.Fonts;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Drawing;

/// <summary>
/// A canvas that records instead of drawing: every hook call is kept with its arguments, and <see cref="Finish"/> returns them as
/// an immutable <see cref="Drawing"/> to replay on any canvas, at any scale, as often as needed, as Skia's SkPictureRecorder.
/// Any code that draws on a <see cref="Canvas"/> can draw into it. Paths, paints, images and nested drawings are kept by
/// reference: images must not change while the drawing is in use.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Advanced.cs" region="recording"/>
/// </example>
public sealed class DrawingRecorder : Canvas
{
    private readonly List<DrawingCall> _calls = [];
    private readonly RectF _bounds;
    private bool _finished;

    /// <summary>Initializes a new instance of the <see cref="DrawingRecorder"/> class.</summary>
    /// <param name="bounds">
    /// The drawing's bounds in its own coordinates: the clip recording starts with, and what <see cref="Drawing.ToRaster{TPixel}"/>
    /// and drawing patterns render; calls outside them are still recorded.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="bounds"/> is empty or not finite.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Advanced.cs" region="recording"/>
    /// </example>
    public DrawingRecorder(RectF bounds)
        : base(CheckBounds(bounds))
    {
        _bounds = bounds;
    }

    /// <summary>Gets the number of calls recorded so far.</summary>
    public int CallCount => _calls.Count;

    /// <summary>Ends the recording: restores what is still saved, so the drawing is balanced, and returns it. The recorder cannot be used afterwards.</summary>
    /// <returns>The drawing.</returns>
    /// <exception cref="InvalidOperationException">The recording has already finished.</exception>
    public Drawing Finish()
    {
        if (_finished)
        {
            throw new InvalidOperationException("The recording has already finished.");
        }

        RestoreToCount(1);
        _finished = true;
        Drawing drawing = new(_bounds, [.. _calls]);
        Dispose();
        return drawing;
    }

    /// <inheritdoc/>
    protected override void OnSave() => _calls.Add(SaveCall.Instance);

    /// <inheritdoc/>
    protected override void OnRestore() => _calls.Add(RestoreCall.Instance);

    /// <inheritdoc/>
    protected override void OnSaveLayer(LayerOptions options) => _calls.Add(new SaveLayerCall(options));

    /// <inheritdoc/>
    protected override void OnRestoreLayer() => _calls.Add(RestoreCall.Instance);

    /// <inheritdoc/>
    protected override void OnSetMatrix(Matrix3x2 matrix) => _calls.Add(new SetMatrixCall(matrix));

    /// <inheritdoc/>
    protected override void OnClipRect(RectF rect, ClipOp op, bool antialias) => _calls.Add(new ClipRectCall(rect, op, antialias));

    /// <inheritdoc/>
    protected override void OnClipRoundRect(RoundRect roundRect, ClipOp op, bool antialias) => _calls.Add(new ClipRoundRectCall(roundRect, op, antialias));

    /// <inheritdoc/>
    protected override void OnClipPath(Path path, ClipOp op, bool antialias) => _calls.Add(new ClipPathCall(path, op, antialias));

    /// <inheritdoc/>
    protected override void OnDrawPaint(Paint paint) => _calls.Add(new DrawPaintCall(paint));

    /// <inheritdoc/>
    protected override void OnDrawRect(RectF rect, Stroke? stroke, Paint paint) => _calls.Add(new DrawRectCall(rect, stroke, paint));

    /// <inheritdoc/>
    protected override void OnDrawRoundRect(RoundRect roundRect, Stroke? stroke, Paint paint) => _calls.Add(new DrawRoundRectCall(roundRect, stroke, paint));

    /// <inheritdoc/>
    protected override void OnDrawOval(RectF oval, Stroke? stroke, Paint paint) => _calls.Add(new DrawOvalCall(oval, stroke, paint));

    /// <inheritdoc/>
    protected override void OnDrawPath(Path path, Stroke? stroke, Paint paint) => _calls.Add(new DrawPathCall(path, stroke, paint));

    /// <inheritdoc/>
    protected override void OnDrawPoints(PointMode mode, ReadOnlySpan<Vector2> points, Stroke stroke, Paint paint) => _calls.Add(new DrawPointsCall(mode, points.ToArray(), stroke, paint));

    /// <inheritdoc/>
    protected override void OnDrawImage(Raster image, RectF source, RectF destination, SamplingOptions sampling, Paint? paint) => _calls.Add(new DrawImageCall(image, source, destination, sampling, paint));

    /// <inheritdoc/>
    protected override void OnDrawGlyphRun(GlyphRun run, Vector2 origin, Paint paint) => _calls.Add(new DrawGlyphRunCall(run, origin, paint));

    /// <inheritdoc/>
    protected override void OnDrawLattice(Raster image, Lattice lattice, RectF destination, SamplingOptions sampling, Paint? paint) => _calls.Add(new DrawLatticeCall(image, lattice, destination, sampling, paint));

    /// <inheritdoc/>
    protected override void OnDrawAtlas(Raster image, ReadOnlySpan<SpriteTransform> transforms, ReadOnlySpan<RectF> sources, ReadOnlySpan<RgbaF32> colors, BlendMode colorBlend, SamplingOptions sampling, Paint? paint)
        => _calls.Add(new DrawAtlasCall(image, transforms.ToArray(), sources.ToArray(), colors.ToArray(), colorBlend, sampling, paint));

    /// <inheritdoc/>
    protected override void OnDrawMesh(Mesh mesh, Paint paint, BlendMode colorBlend, ReadOnlySpan<GradientStop> valueStops) => _calls.Add(new DrawMeshCall(mesh, paint, colorBlend, valueStops.ToArray()));

    /// <inheritdoc/>
    protected override void OnDrawPatch(Patch patch, Paint paint, BlendMode colorBlend, ReadOnlySpan<GradientStop> valueStops) => _calls.Add(new DrawPatchCall(patch, paint, colorBlend, valueStops.ToArray()));

    /// <inheritdoc/>
    protected override void OnDrawDrawing(Drawing drawing, Matrix3x2 matrix, Paint? paint, bool cacheAsImage) => _calls.Add(new DrawDrawingCall(drawing, matrix, paint, cacheAsImage));

    private static RectF CheckBounds(RectF bounds)
    {
        if (!bounds.IsFinite || bounds.IsEmpty)
        {
            throw new ArgumentException("The bounds must be finite and not empty.", nameof(bounds));
        }

        return bounds;
    }
}
