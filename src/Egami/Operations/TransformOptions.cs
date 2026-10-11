using Egami.Pixels;

namespace Egami.Operations;

/// <summary>
/// How a transform or warp samples the source: the filter, the color space it blends in, what samples outside the source read,
/// and the background color.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Transforms.cs" region="transform-options"/>
/// </example>
public record class SamplingOptions
{
    private readonly ResampleFilter _filter = ResampleFilter.Bicubic;
    private readonly ColorSpace _colorSpace = ColorSpace.Encoded;
    private readonly EdgeMode _edges = EdgeMode.Transparent;

    /// <summary>
    /// Gets the filter. Where the image keeps or gains size, each output pixel blends the filter's 2D kernel around its source
    /// point, as Rotate does; where it shrinks or tilts away, the filter weighs an ellipse that covers the pixel's footprint
    /// (elliptical weighted averaging), so fine detail fades instead of aliasing. Defaults to <see cref="ResampleFilter.Bicubic"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not defined.</exception>
    public ResampleFilter Filter
    {
        get => _filter;
        init => _filter = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "Not a defined filter.");
    }

    /// <summary>Gets the color space the filter blends in. Defaults to <see cref="ColorSpace.Encoded"/>, as for Resize.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not defined.</exception>
    public ColorSpace ColorSpace
    {
        get => _colorSpace;
        init => _colorSpace = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "Not a defined color space.");
    }

    /// <summary>
    /// Gets what samples outside the source read. <see cref="EdgeMode.Transparent"/> (the default) reads <see cref="Background"/>,
    /// so the image's edges blend smoothly into it; <see cref="EdgeMode.Clamp"/>, <see cref="EdgeMode.Mirror"/> and
    /// <see cref="EdgeMode.Wrap"/> extend the image, so a Wrap transform fills the whole canvas with a tiling.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not defined.</exception>
    public EdgeMode Edges
    {
        get => _edges;
        init => _edges = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "Not a defined edge mode.");
    }

    /// <summary>
    /// Gets the color of canvas the image does not reach and of samples outside it under <see cref="EdgeMode.Transparent"/>.
    /// Defaults to transparent black.
    /// </summary>
    public Rgba8 Background { get; init; }
}

/// <summary>
/// How a transform samples the source and how large its result is: <see cref="SamplingOptions"/> plus a viewport.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Transforms.cs" region="transform-options"/>
/// </example>
public sealed record class TransformOptions : SamplingOptions
{
    /// <summary>
    /// Gets the rectangle of the transformed plane to keep, in destination coordinates, or <see langword="null"/> (the default) for
    /// the bounding box of the transformed image, rounded to whole pixels and centered on it, where the matrix's translation only
    /// moves the image within the box. <c>new Rect(0, 0, width, height)</c> with the input's size keeps the size and the
    /// translation, as ImageMagick's <c>-distort</c> does, and lets the transform run inside <c>Within</c>.
    /// </summary>
    public Rect? Viewport { get; init; }
}
