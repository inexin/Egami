namespace Egami.Operations;

/// <summary>How <see cref="RasterOps.Blur(float, ColorSpace, BlurMethod)"/> computes a Gaussian blur.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Filters.cs" region="fast-blur"/>
/// </example>
public enum BlurMethod
{
    /// <summary>The exact Gaussian kernel, radius ceil(3σ): what <see cref="RasterOps.Blur(float, ColorSpace)"/> does. Its cost grows with σ.</summary>
    Exact,

    /// <summary>
    /// Three box passes per axis with fractional end weights, whose variance equals σ² exactly (Gwosdek et al., 2011): the cost per
    /// pixel does not grow with σ, and from σ = 3 up the result is within 50 dB PSNR of <see cref="Exact"/>. Below σ = 2 it runs
    /// the exact kernel, which is as cheap there.
    /// </summary>
    Fast,
}
