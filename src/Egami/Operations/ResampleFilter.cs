namespace Egami.Operations;

/// <summary>The kernel a resize or rotation uses to blend source pixels.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Geometry.cs" region="filters"/>
/// </example>
public enum ResampleFilter
{
    /// <summary>Keys cubic with a = −0.5 (Catmull-Rom): sharp, with slight ringing at hard edges. The default.</summary>
    Bicubic,

    /// <summary>Picks the closest source pixel without blending. Exact and fastest; blocky when enlarging, aliased when shrinking.</summary>
    Nearest,

    /// <summary>Averages the source pixels each output pixel covers. Good for large reductions of flat artwork.</summary>
    Box,

    /// <summary>A triangle (tent) kernel: smooth and soft, with no ringing.</summary>
    Bilinear,

    /// <summary>Mitchell–Netravali with B = C = 1/3: softer than Bicubic, with less ringing.</summary>
    Mitchell,

    /// <summary>A three-lobe windowed sinc: the sharpest, with the most ringing, and the slowest.</summary>
    Lanczos3,

    /// <summary>
    /// A two-lobe windowed sinc, sinc(x)·sinc(x / 2) out to 2 pixels, as ImageMagick's Lanczos2: nearly as sharp as Lanczos3 with
    /// less ringing, and faster.
    /// </summary>
    Lanczos2,

    /// <summary>
    /// The Hermite cubic (B = C = 0), 2|x|³ − 3|x|² + 1 out to 1 pixel: smooth like Bilinear when enlarging, without its corners,
    /// and no ringing.
    /// </summary>
    Hermite,

    /// <summary>
    /// A Gaussian with σ = ½, exp(−2x²), out to 2 pixels, as ImageMagick's Gaussian filter: soft, with no ringing at all.
    /// </summary>
    Gaussian,

    /// <summary>The cubic B-spline (B = 1, C = 0) out to 2 pixels: the smoothest cubic, with no ringing and noticeable blur.</summary>
    Spline,
}

/// <summary>The color space in which resizing, blurs and grayscale blend colors: the stored sRGB-encoded values, or linear light.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Geometry.cs" region="filters"/>
/// </example>
public enum ColorSpace
{
    /// <summary>
    /// Blends the stored sRGB-encoded values, as most libraries and browsers do. The default: faster, and what most people expect.
    /// </summary>
    Encoded,

    /// <summary>
    /// Converts to linear light, blends, and converts back. Physically correct: fine light detail on dark backgrounds keeps its
    /// brightness when reduced.
    /// </summary>
    Linear,
}
