using Egami.Operations.Filters;
using Egami.Operations.Resampling;

namespace Egami.Operations;

/// <summary>The neighbourhood filters of Phase 2.5.4: blurs and noise, edges and morphology, lighting and relief, and effects.</summary>
public sealed partial class RasterOps
{
    /// <summary>Below this σ the fast blur runs the exact kernel, which costs no more there.</summary>
    internal const float FastBlurFloor = 2;

    /// <summary>
    /// Blurs with a Gaussian of standard deviation <paramref name="sigma"/> pixels, computed by <paramref name="method"/>.
    /// <see cref="BlurMethod.Exact"/> is <see cref="Blur(float, ColorSpace)"/>; <see cref="BlurMethod.Fast"/> costs the same at any
    /// σ, for shadows, glows and other large blurs. Edges repeat the edge pixel.
    /// </summary>
    /// <param name="sigma">The standard deviation in pixels; 0 changes nothing.</param>
    /// <param name="colorSpace">Where pixels are blended.</param>
    /// <param name="method">How the blur is computed.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sigma"/> is negative, not finite or above 1000, or an enum value is not defined.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Filters.cs" region="fast-blur"/>
    /// </example>
    public RasterOps Blur(float sigma, ColorSpace colorSpace, BlurMethod method)
    {
        CheckSigma(sigma);
        CheckFilter(ResampleFilter.Bicubic, colorSpace);
        if (!Enum.IsDefined(method))
        {
            throw new ArgumentOutOfRangeException(nameof(method), method, "The blur method is not defined.");
        }

        if (method == BlurMethod.Exact || sigma < FastBlurFloor)
        {
            return Blur(sigma, colorSpace);
        }

        return Add(new FastBlurOperation(sigma, sigma, colorSpace == ColorSpace.Linear));
    }

    /// <summary>
    /// Replaces each channel with its median over the (2r + 1) square around the pixel, as ImageMagick's -statistic Median and
    /// the reference library's MedianBlur do: removes salt-and-pepper noise while keeping edges. Works on stored values, so 8 and 16-bit
    /// results are exact; edges repeat the edge pixel. The cost per pixel does not grow with the radius on byte types.
    /// </summary>
    /// <param name="radius">The radius in pixels, from 0 (changes nothing) to 100.</param>
    /// <param name="includeAlpha">Whether alpha gets its own median too; by default it is kept.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radius"/> is negative or above 100.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Filters.cs" region="median"/>
    /// </example>
    public RasterOps Median(int radius, bool includeAlpha = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(radius);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(radius, MedianOperation.MaxRadius);
        return radius == 0 ? this : Add(new MedianOperation(radius, includeAlpha));
    }
}
