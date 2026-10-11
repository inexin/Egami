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

    /// <summary>
    /// Blurs as an out-of-focus lens does: each point spreads into the aperture's shape, a disc or a polygon of 5 to 12 blades,
    /// so small lights become discs, unlike a Gaussian's soft spots. Works in linear light by default, since a lens adds light;
    /// <see cref="LensBlurOptions.HighlightGain"/> makes bright lights bloom. The cost does not grow with the radius: the blur
    /// runs by FFT on tiles. Edges repeat the edge pixel.
    /// </summary>
    /// <param name="radius">The aperture's radius in pixels, from 0 (changes nothing) to 128.</param>
    /// <param name="options">The aperture and highlights, or <see langword="null"/> for a round aperture in linear light.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="radius"/> is negative, not finite or above 128, or an option is out of range: blades other than 0 or 5 to
    /// 12, a rotation that is not finite, a gain below 1 or not finite, a threshold outside 0..1, or an undefined color space.
    /// </exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Filters.cs" region="lens-blur"/>
    /// </example>
    public RasterOps LensBlur(float radius, LensBlurOptions? options = null)
    {
        if (!(radius >= 0 && radius <= LensBlurOperation.MaxRadius))
        {
            throw new ArgumentOutOfRangeException(nameof(radius), radius, "The radius must be from 0 to 128.");
        }

        options ??= LensBlurOptions.Default;
        if (options.Blades != 0 && (options.Blades < 5 || options.Blades > 12))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Blades, "Blades must be 0 (round) or 5 to 12.");
        }

        if (!float.IsFinite(options.Rotation))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Rotation, "The rotation must be finite.");
        }

        if (!(options.HighlightGain >= 1) || !float.IsFinite(options.HighlightGain))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.HighlightGain, "The highlight gain must be 1 or more and finite.");
        }

        if (!(options.HighlightThreshold >= 0 && options.HighlightThreshold <= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.HighlightThreshold, "The highlight threshold must be from 0 to 1.");
        }

        CheckFilter(ResampleFilter.Bicubic, options.ColorSpace);
        return radius < 0.5f ? this : Add(new LensBlurOperation(radius, options));
    }

    /// <summary>
    /// Smooths while keeping edges (Tomasi and Manduchi's bilateral filter): each neighbour counts by its distance, through a
    /// Gaussian of <paramref name="spatialSigma"/>, and by how much its color differs, through a Gaussian of
    /// <paramref name="rangeSigma"/>, so noise flattens and edges stay. The color difference is the root-mean-square difference
    /// of red, green and blue on 0..1, so on a gray image the sigmas mean what ImageMagick's -bilateral-blur
    /// WxW+(255·rangeSigma)+spatialSigma means. Alpha is averaged with the same weights; edges repeat the edge pixel.
    /// </summary>
    /// <param name="spatialSigma">The reach in pixels, above 0 and at most 50.</param>
    /// <param name="rangeSigma">How different a color may be and still count, above 0, on the 0..1 scale: 0.1 keeps most edges.</param>
    /// <param name="radius">The window's radius, or <see langword="null"/> for ⌈2 · spatialSigma⌉ as ImageMagick's width; at most 100. Above 8 the taps are taken every ⌈radius / 8⌉ pixels, so the cost stays bounded.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A sigma is not positive and finite or above its limit, or <paramref name="radius"/> is negative or above 100.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Filters.cs" region="bilateral"/>
    /// </example>
    public RasterOps BilateralBlur(float spatialSigma, float rangeSigma, int? radius = null)
    {
        if (!(spatialSigma > 0 && spatialSigma <= 50))
        {
            throw new ArgumentOutOfRangeException(nameof(spatialSigma), spatialSigma, "The spatial sigma must be above 0 and at most 50.");
        }

        if (!(rangeSigma > 0) || !float.IsFinite(rangeSigma))
        {
            throw new ArgumentOutOfRangeException(nameof(rangeSigma), rangeSigma, "The range sigma must be above 0 and finite.");
        }

        int r = radius ?? (int)Math.Ceiling(2 * spatialSigma);
        ArgumentOutOfRangeException.ThrowIfNegative(r, nameof(radius));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(r, 100, nameof(radius));
        return r == 0 ? this : Add(new BilateralBlurOperation(spatialSigma, rangeSigma, r));
    }

    /// <summary>
    /// Streaks the image along a direction, as a camera moving during the exposure, with ImageMagick's -motion-blur meaning: each
    /// pixel averages 2⌈radius⌉ + 1 samples one pixel apart, from itself toward <paramref name="angle"/>, weighted by a Gaussian of
    /// <paramref name="sigma"/> from the pixel. Samples between pixels are read bilinearly; edges repeat the edge pixel.
    /// </summary>
    /// <param name="radius">Half the streak's length in pixels, from 0 (changes nothing) to 127.</param>
    /// <param name="sigma">How fast the weights fall along the streak, above 0.</param>
    /// <param name="angle">The direction in degrees, clockwise from the x axis (y points down).</param>
    /// <param name="centered">Whether the streak runs both ways from the pixel, instead of only toward <paramref name="angle"/>.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An argument is not finite or out of range.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Filters.cs" region="directional"/>
    /// </example>
    public RasterOps MotionBlur(float radius, float sigma, float angle, bool centered = false)
    {
        if (!(radius >= 0 && radius <= 127))
        {
            throw new ArgumentOutOfRangeException(nameof(radius), radius, "The radius must be from 0 to 127.");
        }

        if (!(sigma > 0) || !float.IsFinite(sigma))
        {
            throw new ArgumentOutOfRangeException(nameof(sigma), sigma, "Sigma must be above 0 and finite.");
        }

        if (!float.IsFinite(angle))
        {
            throw new ArgumentOutOfRangeException(nameof(angle), angle, "The angle must be finite.");
        }

        return radius == 0 ? this : Add(DirectionalBlurOperation.Motion(radius, sigma, angle, centered));
    }

    /// <summary>
    /// Spins the image around <paramref name="center"/>, as ImageMagick's -rotational-blur: each pixel averages samples along an arc
    /// of <paramref name="degrees"/> centered on it, about one per pixel of arc (at most 256), read bilinearly. Pixels far from the
    /// center blur most; the center does not move.
    /// </summary>
    /// <param name="degrees">The arc in degrees, from 0 (changes nothing) to 360.</param>
    /// <param name="center">The center in pixels, pixel centers at +0.5, or <see langword="null"/> for the image's center.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="degrees"/> is outside 0..360, or the center is not finite.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Filters.cs" region="directional"/>
    /// </example>
    public RasterOps RotationalBlur(float degrees, System.Numerics.Vector2? center = null)
    {
        if (!(degrees >= 0 && degrees <= 360))
        {
            throw new ArgumentOutOfRangeException(nameof(degrees), degrees, "The arc must be from 0 to 360 degrees.");
        }

        CheckCenter(center);
        return degrees == 0 ? this : Add(DirectionalBlurOperation.Rotational(degrees, center));
    }

    /// <summary>
    /// Streaks the image along rays from <paramref name="center"/>, as a zoom during the exposure (Photoshop's radial zoom blur):
    /// each pixel averages samples on the ray through it, spanning <paramref name="amount"/> of its distance from the center, half
    /// inward and half outward, about one per pixel (at most 256). The center does not move, and a flat image stays flat.
    /// </summary>
    /// <param name="amount">The streak's length as a fraction of the distance from the center, from 0 (changes nothing) to 1.</param>
    /// <param name="center">The center in pixels, pixel centers at +0.5, or <see langword="null"/> for the image's center.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="amount"/> is outside 0..1, or the center is not finite.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Filters.cs" region="directional"/>
    /// </example>
    public RasterOps ZoomBlur(float amount, System.Numerics.Vector2? center = null)
    {
        if (!(amount >= 0 && amount <= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "The amount must be from 0 to 1.");
        }

        CheckCenter(center);
        return amount == 0 ? this : Add(DirectionalBlurOperation.Zoom(amount, center));
    }

    private static void CheckCenter(System.Numerics.Vector2? center)
    {
        if (center is System.Numerics.Vector2 c && !(float.IsFinite(c.X) && float.IsFinite(c.Y)))
        {
            throw new ArgumentOutOfRangeException(nameof(center), center, "The center must be finite.");
        }
    }

    /// <summary>
    /// Adds noise to red, green and blue (or a CMYK raster's inks), as ImageMagick's +noise with -attenuate does: film grain,
    /// sensor noise, or salt and pepper for testing filters. Alpha is never touched. The same seed gives the same pixels on any
    /// machine and at any thread count, and inside a scope an area gets the noise the whole image would get there.
    /// </summary>
    /// <param name="kind">The kind of noise.</param>
    /// <param name="amount">The strength, as ImageMagick's -attenuate, from 0 (changes nothing) to 10; 1 gives ImageMagick's default.</param>
    /// <param name="monochrome">Whether red, green and blue get the same noise, so it has no color.</param>
    /// <param name="seed">The seed: change it for different noise.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not defined, or <paramref name="amount"/> is outside 0..10.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Filters.cs" region="noise"/>
    /// </example>
    public RasterOps AddNoise(NoiseKind kind, float amount = 1, bool monochrome = false, ulong seed = 0)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "The noise kind is not defined.");
        }

        if (!(amount >= 0 && amount <= 10))
        {
            throw new ArgumentOutOfRangeException(nameof(amount), amount, "The amount must be from 0 to 10.");
        }

        return amount == 0 ? this : Add(new NoiseOperation(kind, amount, monochrome, seed));
    }

    /// <summary>
    /// Removes specks while keeping edges, by Crimmins' complementary hulling exactly as ImageMagick's -despeckle runs it: each
    /// color channel is nudged, a level at a time, toward neighbours that differ by two levels or more, in 16 passes along four
    /// directions. Alpha is kept. The image's border is compared with black, as ImageMagick does.
    /// </summary>
    /// <returns>This builder.</returns>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Filters.cs" region="despeckle"/>
    /// </example>
    public RasterOps Despeckle()
    {
        return Add(new DespeckleOperation());
    }
}
