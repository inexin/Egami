namespace Egami;

/// <summary>How chroma is brought up to luma resolution when a <see cref="YuvRaster"/> converts to RGB.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/PlanarYuv.cs" region="options"/>
/// </example>
public enum ChromaUpsampling
{
    /// <summary>
    /// Interpolates between neighboring chroma samples, honoring the siting: the 3:1 triangle filter JPEG and WebP decoders use
    /// for centered chroma, and the average of the two neighbors between co-sited samples.
    /// </summary>
    Bilinear,

    /// <summary>Repeats each chroma sample over the luma samples it covers. Faster, with visible color fringes on edges.</summary>
    Nearest,
}

/// <summary>How chroma is reduced to the chroma resolution when a raster converts to a subsampled <see cref="YuvRaster"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/PlanarYuv.cs" region="options"/>
/// </example>
public enum ChromaDownsampling
{
    /// <summary>
    /// Averages with the filter that matches the siting: a 2 × 2 box for centered chroma, a [1, 2, 1] filter across co-sited
    /// columns or rows. Edges repeat the last sample.
    /// </summary>
    Average,

    /// <summary>Takes the chroma of the top-left pixel of each block, with no filter.</summary>
    Fast,
}

/// <summary>The chroma filters for conversions between a <see cref="YuvRaster"/> and a <see cref="Raster"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/PlanarYuv.cs" region="options"/>
/// </example>
public sealed record YuvConvertOptions
{
    /// <summary>Gets the defaults: <see cref="ChromaUpsampling.Bilinear"/> and <see cref="ChromaDownsampling.Average"/>.</summary>
    public static YuvConvertOptions Default { get; } = new();

    /// <summary>Gets how chroma is upsampled on the way to RGB. Defaults to <see cref="ChromaUpsampling.Bilinear"/>.</summary>
    public ChromaUpsampling Upsampling { get; init; }

    /// <summary>Gets how chroma is downsampled on the way from RGB. Defaults to <see cref="ChromaDownsampling.Average"/>.</summary>
    public ChromaDownsampling Downsampling { get; init; }
}
