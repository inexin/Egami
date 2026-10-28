namespace Egami.SystemDrawing.Drawing2D;
/// <summary>A quality level, the base of the smoothing, compositing and interpolation modes.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="graphics"/>
/// </example>
public enum QualityMode
{
    /// <summary>Not a valid mode.</summary>
    Invalid = -1,

    /// <summary>The default quality.</summary>
    Default = 0,

    /// <summary>Low quality, high speed.</summary>
    Low = 1,

    /// <summary>High quality, low speed.</summary>
    High = 2,
}

/// <summary>Whether shapes and lines are antialiased. GDI+ and Egami.SystemDrawing default to <see cref="None"/>, aliased.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="graphics"/>
/// </example>
public enum SmoothingMode
{
    /// <summary>Not a valid mode.</summary>
    Invalid = -1,

    /// <summary>Aliased; the same as <see cref="None"/>.</summary>
    Default = 0,

    /// <summary>Aliased.</summary>
    HighSpeed = 1,

    /// <summary>Antialiased.</summary>
    HighQuality = 2,

    /// <summary>Aliased.</summary>
    None = 3,

    /// <summary>Antialiased.</summary>
    AntiAlias = 4,
}

/// <summary>Where pixel centers are: on whole coordinates (the default), or half a pixel off with <see cref="Half"/> and <see cref="HighQuality"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="graphics"/>
/// </example>
public enum PixelOffsetMode
{
    /// <summary>Not a valid mode.</summary>
    Invalid = -1,

    /// <summary>Pixel centers on whole coordinates.</summary>
    Default = 0,

    /// <summary>Pixel centers on whole coordinates.</summary>
    HighSpeed = 1,

    /// <summary>Pixel centers half a pixel off, so a pixel spans whole coordinates.</summary>
    HighQuality = 2,

    /// <summary>Pixel centers on whole coordinates.</summary>
    None = 3,

    /// <summary>Pixel centers half a pixel off, so a pixel spans whole coordinates.</summary>
    Half = 4,
}

/// <summary>Whether drawing blends over what is there or replaces it.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="graphics"/>
/// </example>
public enum CompositingMode
{
    /// <summary>Blend over, by alpha.</summary>
    SourceOver = 0,

    /// <summary>Replace, alpha included.</summary>
    SourceCopy = 1,
}

/// <summary>How carefully blending is done. Egami blends the same way for every value.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="graphics"/>
/// </example>
public enum CompositingQuality
{
    /// <summary>Not a valid mode.</summary>
    Invalid = -1,

    /// <summary>The default.</summary>
    Default = 0,

    /// <summary>Fast.</summary>
    HighSpeed = 1,

    /// <summary>Careful.</summary>
    HighQuality = 2,

    /// <summary>In linear light.</summary>
    GammaCorrected = 3,

    /// <summary>Assuming linear values.</summary>
    AssumeLinear = 4,
}

/// <summary>How images are sampled when drawn at another size or angle. GDI+ and Egami.SystemDrawing default to bilinear without prefiltering.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="graphics"/>
/// </example>
public enum InterpolationMode
{
    /// <summary>Not a valid mode.</summary>
    Invalid = -1,

    /// <summary>Bilinear without prefiltering.</summary>
    Default = 0,

    /// <summary>Bilinear without prefiltering.</summary>
    Low = 1,

    /// <summary>Bicubic with prefiltering.</summary>
    High = 2,

    /// <summary>Bilinear without prefiltering.</summary>
    Bilinear = 3,

    /// <summary>Bicubic without prefiltering.</summary>
    Bicubic = 4,

    /// <summary>The nearest pixel.</summary>
    NearestNeighbor = 5,

    /// <summary>Bilinear with prefiltering.</summary>
    HighQualityBilinear = 6,

    /// <summary>Bicubic with prefiltering.</summary>
    HighQualityBicubic = 7,
}
