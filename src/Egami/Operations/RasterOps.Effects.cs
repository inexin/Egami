using System.Numerics;

using Egami.Operations.Filters;
using Egami.Pixels;

namespace Egami.Operations;

/// <summary>The effects of Phase 2.5.4: vignette, glow, bloom, pixelate, oil paint, Kuwahara, spread and drop shadow.</summary>
public sealed partial class RasterOps
{
    /// <summary>
    /// Darkens (or tints) the image toward its corners, outside an ellipse fitted to the image, with a soft edge: the shape of
    /// ImageMagick's -vignette, which blurs a drawn ellipse. The mask is computed per pixel, so its cost does not depend on the
    /// softness. Pixels are mixed with the color source-over, on premultiplied stored values.
    /// </summary>
    /// <param name="radius">
    /// The ellipse's size as a fraction of the half-diagonal: at 1 the ellipse passes through the corners, at 0.7 it is about as
    /// wide as the image. The ellipse has the image's proportions.
    /// </param>
    /// <param name="softness">
    /// The share of the radius over which the edge fades, from 0 (hard) to 1: the fade is a Gaussian-blurred edge whose ±2σ spans
    /// softness × radius.
    /// </param>
    /// <param name="color">The color toward the corners, with its alpha as its strength; black and opaque when <see langword="null"/>.</param>
    /// <param name="center">The ellipse's center in the raster's pixel coordinates (0, 0 is the top-left corner of the image); the image's middle when <see langword="null"/>.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="radius"/> is outside 0..10, <paramref name="softness"/> is outside 0..1, or <paramref name="center"/> is not finite.
    /// </exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/FilterEffects.cs" region="vignette"/>
    /// </example>
    public RasterOps Vignette(float radius = 1, float softness = 0.5f, Rgba8? color = null, Vector2? center = null)
    {
        if (!(radius >= 0 && radius <= 10))
        {
            throw new ArgumentOutOfRangeException(nameof(radius), radius, "The radius must be from 0 to 10.");
        }

        if (!(softness >= 0 && softness <= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(softness), softness, "The softness must be from 0 to 1.");
        }

        if (center is Vector2 c && !(float.IsFinite(c.X) && float.IsFinite(c.Y)))
        {
            throw new ArgumentOutOfRangeException(nameof(center), center, "The center must be finite.");
        }

        Rgba8 tint = color ?? new Rgba8(0, 0, 0, 255);
        return tint.A == 0 ? this : Add(new VignetteOperation(radius, softness, tint, center));
    }

    /// <summary>
    /// Adds a glow around the image's alpha shape, as an editor's outer glow layer style: the alpha, blurred with
    /// <see cref="BlurMethod.Fast"/>, spread, and filled with <paramref name="color"/>, drawn behind the image. With
    /// <paramref name="inner"/> the glow comes in from the shape's edges instead and is drawn on the image, inside the shape only.
    /// It is exactly the chain written by hand: a layer made with a <see cref="ColorMatrix(Operations.ColorMatrix)"/> (the color, and
    /// alpha or 1 − alpha), <see cref="Blur(float, ColorSpace, BlurMethod)"/>, <see cref="Arithmetic"/> multiplying alpha by
    /// 1 / (1 − spread) and <see cref="Opacity"/> for the color's alpha, then drawn with DrawImage (the image over the layer, or
    /// the layer over the image with <see cref="BlendMode.SourceAtop"/>). An opaque image has no edge to glow from.
    /// </summary>
    /// <param name="sigma">The glow's blur in pixels, from 0 to 1000.</param>
    /// <param name="color">The glow's color, with its alpha as the glow's opacity.</param>
    /// <param name="spread">How much the glow is hardened before it fades, from 0 (a plain blur) to just under 1 (a hard edge): the blurred alpha is divided by 1 − spread.</param>
    /// <param name="inner">Whether the glow is inside the shape instead of around it.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="sigma"/> is outside 0..1000, or <paramref name="spread"/> is outside 0 to under 1.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/FilterEffects.cs" region="glow"/>
    /// </example>
    public RasterOps Glow(float sigma, Rgba8 color, float spread = 0, bool inner = false)
    {
        CheckSigma(sigma);
        if (!(spread >= 0 && spread < 1))
        {
            throw new ArgumentOutOfRangeException(nameof(spread), spread, "The spread must be from 0 to under 1.");
        }

        if (color.A == 0)
        {
            return this;
        }

        RasterOps layer = new();
        float r = color.R / 255f, g = color.G / 255f, b = color.B / 255f;
        layer.ColorMatrix(new ColorMatrix(inner
            ? [0, 0, 0, 0, r, 0, 0, 0, 0, g, 0, 0, 0, 0, b, 0, 0, 0, -1, 1]
            : [0, 0, 0, 0, r, 0, 0, 0, 0, g, 0, 0, 0, 0, b, 0, 0, 0, 1, 0]));
        layer.Blur(sigma, ColorSpace.Encoded, BlurMethod.Fast);
        if (spread > 0)
        {
            layer.Arithmetic(PixelMath.Multiply, 1 / (1 - spread), ColorChannels.Alpha);
        }

        if (color.A < 255)
        {
            layer.Opacity(color.A / 255f);
        }

        return inner
            ? Add(new LayerEffectOperation(layer.Steps, LayerPlacement.Over, BlendMode.SourceAtop, linear: false))
            : Add(new LayerEffectOperation(layer.Steps, LayerPlacement.Behind, BlendMode.SourceOver, linear: false));
    }

    /// <summary>
    /// Makes bright areas glow, as a camera's bloom: the parts of each channel above <paramref name="threshold"/>, stretched to 0..1
    /// and scaled by <paramref name="intensity"/>, blurred in linear light with <see cref="BlurMethod.Fast"/> and screened onto the
    /// image in linear light. It is exactly the chain written by hand: a layer made with one
    /// <see cref="ColorMatrix(Operations.ColorMatrix)"/> (red, green and blue times k = intensity / (1 − threshold), minus
    /// threshold × k, clamped) and <see cref="Blur(float, ColorSpace, BlurMethod)"/> in linear light, then
    /// drawn with DrawImage, <see cref="BlendMode.Screen"/>, in linear light.
    /// </summary>
    /// <param name="threshold">The stored value above which a channel blooms, from 0 to under 1.</param>
    /// <param name="sigma">The bloom's blur in pixels, from 0 to 1000.</param>
    /// <param name="intensity">How strongly the bloom is added, from 0 (nothing) to 100; values are clamped to 1 before the blur.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="threshold"/> is outside 0 to under 1, <paramref name="sigma"/> is outside 0..1000, or <paramref name="intensity"/> is outside 0..100.
    /// </exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/FilterEffects.cs" region="bloom"/>
    /// </example>
    public RasterOps Bloom(float threshold = 0.8f, float sigma = 10, float intensity = 1)
    {
        if (!(threshold >= 0 && threshold < 1))
        {
            throw new ArgumentOutOfRangeException(nameof(threshold), threshold, "The threshold must be from 0 to under 1.");
        }

        CheckSigma(sigma);
        if (!(intensity >= 0 && intensity <= 100))
        {
            throw new ArgumentOutOfRangeException(nameof(intensity), intensity, "The intensity must be from 0 to 100.");
        }

        if (intensity == 0)
        {
            return this;
        }

        // One matrix, clamped once: (value − threshold) × intensity / (1 − threshold) for red, green and blue.
        RasterOps layer = new();
        float k = intensity / (1 - threshold), o = -threshold * k;
        layer.ColorMatrix(new ColorMatrix([k, 0, 0, 0, o, 0, k, 0, 0, o, 0, 0, k, 0, o, 0, 0, 0, 1, 0]));
        layer.Blur(sigma, ColorSpace.Linear, BlurMethod.Fast);
        return Add(new LayerEffectOperation(layer.Steps, LayerPlacement.Over, BlendMode.Screen, linear: true));
    }
}
