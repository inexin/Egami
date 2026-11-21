using Egami.Pixels;

namespace Egami.Drawing.Tracing;

/// <summary>Whether a <see cref="TracedShape"/> is filled or drawn as a line.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Tracing.cs" region="shapes"/>
/// </example>
public enum TracedShapeKind
{
    /// <summary>The path is filled by its fill rule.</summary>
    Fill,

    /// <summary>The path is stroked with a pen of <see cref="TracedShape.StrokeWidth"/>.</summary>
    Stroke,
}

/// <summary>One shape of a <see cref="TraceResult"/>: a path and the color it is drawn in.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Tracing.cs" region="shapes"/>
/// </example>
public sealed class TracedShape
{
    /// <summary>Initializes a new instance of the <see cref="TracedShape"/> class.</summary>
    /// <param name="path">The outline, in the traced image's pixels.</param>
    /// <param name="color">The color.</param>
    /// <param name="kind">Whether the path is filled or stroked.</param>
    /// <param name="strokeWidth">The pen width of a stroked shape, in pixels; 0 for a filled one.</param>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not defined, or <paramref name="strokeWidth"/> is negative or not finite.</exception>
    public TracedShape(Path path, Rgba8 color, TracedShapeKind kind = TracedShapeKind.Fill, float strokeWidth = 0)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "The kind is not defined.");
        }

        if (!(strokeWidth >= 0 && float.IsFinite(strokeWidth)))
        {
            throw new ArgumentOutOfRangeException(nameof(strokeWidth), strokeWidth, "The stroke width must be zero or positive and finite.");
        }

        Path = path;
        Color = color;
        Kind = kind;
        StrokeWidth = strokeWidth;
    }

    /// <summary>Gets the outline, in the traced image's pixels (y down). Outer edges run clockwise and holes counter-clockwise, so either fill rule fills it the same.</summary>
    public Path Path { get; }

    /// <summary>Gets the color.</summary>
    public Rgba8 Color { get; }

    /// <summary>Gets whether the path is filled or stroked.</summary>
    public TracedShapeKind Kind { get; }

    /// <summary>Gets the pen width of a stroked shape, in pixels; 0 for a filled one.</summary>
    public float StrokeWidth { get; }

    /// <inheritdoc/>
    public override string ToString() => $"TracedShape {{ {Kind}, {Color}, {Path} }}";
}
