namespace Egami.Operations;

/// <summary>The kernel a resize or rotation uses to blend source pixels.</summary>
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
}

/// <summary>The space in which resampling blends colors.</summary>
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
