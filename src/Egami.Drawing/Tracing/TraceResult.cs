using Egami.Pixels;

namespace Egami.Drawing.Tracing;

/// <summary>
/// The shapes a trace found, back to front, in the traced image's pixels. Draw them on any <see cref="Canvas"/>, record them as a
/// <see cref="Picture"/>, or (with Egami.Svg) save them as an SVG file.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Tracing.cs" region="trace"/>
/// </example>
public sealed class TraceResult
{
    /// <summary>Initializes a new instance of the <see cref="TraceResult"/> class.</summary>
    /// <param name="shapes">The shapes, back to front.</param>
    /// <param name="width">The traced image's width in pixels.</param>
    /// <param name="height">The traced image's height in pixels.</param>
    /// <exception cref="ArgumentNullException"><paramref name="shapes"/> or one of its shapes is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> or <paramref name="height"/> is not positive.</exception>
    public TraceResult(IEnumerable<TracedShape> shapes, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(shapes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        TracedShape[] list = [.. shapes];
        foreach (TracedShape shape in list)
        {
            ArgumentNullException.ThrowIfNull(shape, nameof(shapes));
        }

        Shapes = list;
        Palette = [.. list.Select(shape => shape.Color).Distinct()];
        Width = width;
        Height = height;
    }

    /// <summary>Gets the shapes, back to front: draw them in this order.</summary>
    public IReadOnlyList<TracedShape> Shapes { get; }

    /// <summary>Gets the shapes' colors, each once, in the order the shapes first use them: back to front.</summary>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Tracing.cs" region="color"/>
    /// </example>
    public IReadOnlyList<Rgba8> Palette { get; }

    /// <summary>Gets the traced image's width in pixels.</summary>
    public int Width { get; }

    /// <summary>Gets the traced image's height in pixels.</summary>
    public int Height { get; }

    /// <summary>Draws the shapes on a canvas, back to front, in the canvas's current transform and clip.</summary>
    /// <param name="canvas">The canvas.</param>
    /// <exception cref="ArgumentNullException"><paramref name="canvas"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Tracing.cs" region="draw"/>
    /// </example>
    public void Draw(Canvas canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        foreach (TracedShape shape in Shapes)
        {
            if (shape.Kind == TracedShapeKind.Stroke)
            {
                canvas.StrokePath(shape.Path, new Stroke(shape.StrokeWidth), shape.Color);
            }
            else
            {
                canvas.FillPath(shape.Path, shape.Color);
            }
        }
    }

    /// <summary>Records the shapes as a picture of the traced image's size, to replay on any canvas or save as a drawing file.</summary>
    /// <returns>The picture.</returns>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Tracing.cs" region="draw"/>
    /// </example>
    public Picture ToPicture()
    {
        PictureRecorder recorder = new(new RectF(0, 0, Width, Height));
        Draw(recorder);
        return recorder.Finish();
    }

    /// <summary>Returns the filled shapes of one color as one path, or <see cref="Path.Empty"/> when there are none.</summary>
    /// <param name="color">The color.</param>
    /// <returns>The path, with the non-zero fill rule.</returns>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Tracing.cs" region="shapes"/>
    /// </example>
    public Path ToPath(Rgba8 color)
    {
        PathBuilder builder = new();
        foreach (TracedShape shape in Shapes)
        {
            if (shape.Kind == TracedShapeKind.Fill && shape.Color == color)
            {
                builder.AddPath(shape.Path);
            }
        }

        return builder.IsEmpty ? Path.Empty : builder.ToPath();
    }

    /// <inheritdoc/>
    public override string ToString() => $"TraceResult {{ {Shapes.Count} shapes, {Width}x{Height} }}";
}
