using Egami.Formats;

namespace Egami.Svg;

/// <summary>Options for reading SVG through <see cref="SvgFormat"/> with <c>Raster.Load</c> and <c>Raster.Probe</c>.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Svg.cs" region="svg-format"/>
/// </example>
public sealed record SvgDecodeOptions : ReaderOptions
{
    /// <summary>
    /// Gets how many raster pixels each of the document's pixels becomes: 2 draws an icon at twice its size. The raster is the
    /// document's size times this, rounded up. Defaults to 1.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive and finite.</exception>
    public float Scale
    {
        get;
        init => field = value > 0 && float.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The scale must be positive and finite.");
    } = 1;
}
