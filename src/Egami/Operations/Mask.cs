namespace Egami.Operations;

/// <summary>Which values of a raster a <see cref="Mask"/> reads as coverage.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Scopes.cs" region="within-mask"/>
/// </example>
public enum MaskChannel
{
    /// <summary>
    /// The value of a one-channel raster (gray, or Alpha8's alpha); otherwise the alpha of a raster with alpha; otherwise the
    /// luminance.
    /// </summary>
    Auto = 0,

    /// <summary>The Rec. 709 luminance of the stored values: 0.2126 R + 0.7152 G + 0.0722 B. White is full coverage.</summary>
    Luminance = 1,

    /// <summary>The alpha: opaque is full coverage. A raster without alpha is opaque everywhere.</summary>
    Alpha = 2,

    /// <summary>The red channel; a gray raster's gray value.</summary>
    Red = 3,

    /// <summary>The green channel; a gray raster's gray value.</summary>
    Green = 4,

    /// <summary>The blue channel; a gray raster's gray value.</summary>
    Blue = 5,
}

/// <summary>
/// A raster read as coverage, 0 (none) to full, placed on the image being edited: where an operation inside
/// <see cref="RasterOps.Within(Mask, Action{RasterOps})"/> applies and how strongly. The raster may have any size and pixel type;
/// outside it coverage is 0, or full when <see cref="Inverted"/>. Its first frame is used for every frame. It must stay
/// undisposed until the chain has run.
/// </summary>
/// <param name="Source">The raster to read coverage from.</param>
/// <param name="Offset">Where the raster's top-left corner sits on the image; may be negative.</param>
/// <param name="Channel">Which values are read. Defaults to <see cref="MaskChannel.Auto"/>.</param>
/// <param name="Inverted">Whether coverage is flipped: none becomes full and full becomes none, outside the raster included.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Scopes.cs" region="within-mask"/>
/// </example>
public readonly record struct Mask(Raster Source, Point Offset = default, MaskChannel Channel = MaskChannel.Auto, bool Inverted = false);
