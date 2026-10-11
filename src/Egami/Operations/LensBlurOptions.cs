namespace Egami.Operations;

/// <summary>
/// The aperture and highlights of <see cref="RasterOps.LensBlur(float, LensBlurOptions?)"/>. The defaults give a round aperture
/// in linear light with no highlight boost.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Filters.cs" region="lens-blur"/>
/// </example>
public sealed record class LensBlurOptions
{
    /// <summary>Gets the default options.</summary>
    public static LensBlurOptions Default { get; } = new();

    /// <summary>Gets the number of aperture blades: 0 for a round aperture, else 5 to 12 for a regular polygon.</summary>
    public int Blades { get; init; }

    /// <summary>Gets the polygon's turn in degrees, clockwise; at 0 one corner points straight up.</summary>
    public float Rotation { get; init; }

    /// <summary>
    /// Gets how much brighter than the rest highlights blur: light above <see cref="HighlightThreshold"/> is multiplied by this
    /// before the blur, as a camera sees a bright point as more light than the pixel can hold, so small lights bloom into bright
    /// discs. 1, the default, changes nothing.
    /// </summary>
    public float HighlightGain { get; init; } = 1;

    /// <summary>Gets the level, 0 to 1 in the blur's color space, above which <see cref="HighlightGain"/> applies. Defaults to 0.9.</summary>
    public float HighlightThreshold { get; init; } = 0.9f;

    /// <summary>Gets where the blur adds light. Defaults to <see cref="ColorSpace.Linear"/>, as a lens adds light, not encoded values.</summary>
    public ColorSpace ColorSpace { get; init; } = ColorSpace.Linear;
}
