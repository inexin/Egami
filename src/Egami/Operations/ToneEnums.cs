namespace Egami.Operations;

/// <summary>What a threshold compares with its level.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Tone.cs" region="threshold"/>
/// </example>
public enum ThresholdMode
{
    /// <summary>The pixel's luminance (Rec. 709 weights on the stored values): the whole pixel becomes one of the two colors. The default.</summary>
    Luminance,

    /// <summary>Each of red, green and blue on its own, as ImageMagick's -threshold with -channel: each channel takes the matching channel of one of the two colors.</summary>
    Channels,

    /// <summary>The pixel's HSL saturation, so vivid pixels take the upper color and grays the lower one.</summary>
    Saturation,

    /// <summary>The larger of the pixel's two YCbCr chroma components, as a distance from neutral from 0 to 1.</summary>
    MaxChroma,
}

/// <summary>How <see cref="RasterOps.HighContrast"/> inverts, as Skia's high-contrast filter does.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Tone.cs" region="effects"/>
/// </example>
public enum ContrastInvert
{
    /// <summary>No inversion. The default.</summary>
    None,

    /// <summary>Each of red, green and blue becomes 1 − value, which also turns hues around.</summary>
    Brightness,

    /// <summary>HSL lightness becomes 1 − lightness and hue and saturation stay, so dark text on light becomes light on dark in the same colors.</summary>
    Lightness,
}

/// <summary>A color vision deficiency to simulate.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Tone.cs" region="effects"/>
/// </example>
public enum ColorVisionDeficiency
{
    /// <summary>Missing or shifted long-wavelength (red) cones; a severity below 1 is protanomaly.</summary>
    Protanopia,

    /// <summary>Missing or shifted medium-wavelength (green) cones, the most common kind; a severity below 1 is deuteranomaly.</summary>
    Deuteranopia,

    /// <summary>Missing or shifted short-wavelength (blue) cones; a severity below 1 is tritanomaly.</summary>
    Tritanopia,

    /// <summary>No color vision: only luminance is seen; a severity below 1 mixes toward it.</summary>
    Achromatopsia,
}

/// <summary>How a <see cref="Lut3D"/> reads between its grid points.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Tone.cs" region="cube"/>
/// </example>
public enum LutInterpolation
{
    /// <summary>
    /// Splits each grid cell into six tetrahedra and blends four points (Kasson and others, 1995), as DaVinci Resolve and OpenColorIO
    /// do: grays stay exactly on the gray axis. The default.
    /// </summary>
    Tetrahedral,

    /// <summary>Blends the eight corners of the cell, as ImageMagick's -hald-clut does.</summary>
    Trilinear,

    /// <summary>Takes the nearest grid point, for tables meant as exact maps.</summary>
    Nearest,
}

/// <summary>How <see cref="RasterOps.AutoThreshold"/> and <see cref="Raster.FindThreshold"/> choose a level from the histogram.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Tone.cs" region="auto-threshold"/>
/// </example>
public enum ThresholdMethod
{
    /// <summary>Otsu (1979): the level that best separates two classes, by between-class variance. The default, and the best for printed pages.</summary>
    Otsu,

    /// <summary>The triangle method (Zack, Rogers and Latt, 1977): the level farthest from the line from the histogram's peak to its far end, for images with one dominant peak.</summary>
    Triangle,

    /// <summary>Kapur, Sahoo and Wong (1985): the level that maximizes the summed entropy of the two classes.</summary>
    Kapur,
}

/// <summary>How <see cref="RasterOps.AutoWhiteBalance"/> finds the cast to remove.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Tone.cs" region="white-balance"/>
/// </example>
public enum WhiteBalanceMethod
{
    /// <summary>
    /// Gray world in Lab, as ImageMagick's -white-balance: the mean a* and b* of the image, weighted by lightness, are taken out of
    /// every pixel. The default.
    /// </summary>
    GrayWorld,

    /// <summary>The brightest 1% of the pixels is taken to be white, and the channels are scaled in linear light so it becomes neutral.</summary>
    WhitePatch,
}

/// <summary>What <see cref="RasterOps.Equalize"/> equalizes.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Tone.cs" region="equalize"/>
/// </example>
public enum EqualizeMode
{
    /// <summary>Lightness: L* of Lab is equalized and a* and b* are kept, so colors do not shift. The default.</summary>
    Luminance,

    /// <summary>Red, green and blue, each by its own histogram, as ImageMagick's -equalize does: stronger, but it shifts colors.</summary>
    Channels,
}
