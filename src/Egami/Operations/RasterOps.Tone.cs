using Egami.Operations.Tone;
using Egami.Pixels;
using Egami.Quantization;

namespace Egami.Operations;

/// <summary>Tone curves, color effects and color filters.</summary>
public sealed partial class RasterOps
{
    /// <summary>
    /// Runs a <see cref="ColorFilter"/> on every pixel. Adjacent filters, tone steps and color matrices run as one pass over the
    /// image, clamping between steps as running them one by one would.
    /// </summary>
    /// <param name="filter">The filter.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="filter"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Tone.cs" region="color-filter"/>
    /// </example>
    public RasterOps ApplyFilter(ColorFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return Add(new FilterOperation(filter));
    }

    /// <summary>
    /// Stretches <paramref name="black"/>..<paramref name="white"/> to <paramref name="outputBlack"/>..<paramref name="outputWhite"/>
    /// with a midtone gamma, as ImageMagick's -level and +level. See <see cref="ColorFilter.Level"/>.
    /// </summary>
    /// <param name="black">The input that becomes <paramref name="outputBlack"/>, from 0 to 1.</param>
    /// <param name="white">The input that becomes <paramref name="outputWhite"/>, from 0 to 1.</param>
    /// <param name="gamma">The midtone gamma, above 0: above 1 brightens.</param>
    /// <param name="outputBlack">The output for <paramref name="black"/>, from 0 to 1.</param>
    /// <param name="outputWhite">The output for <paramref name="white"/>, from 0 to 1.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A value is out of range, or <paramref name="black"/> equals <paramref name="white"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Tone.cs" region="tone"/>
    /// </example>
    public RasterOps Level(float black = 0, float white = 1, float gamma = 1, float outputBlack = 0, float outputWhite = 1)
    {
        return ApplyFilter(ColorFilter.Level(black, white, gamma, outputBlack, outputWhite));
    }

    /// <summary>Raises red, green and blue to 1/<paramref name="gamma"/>, as ImageMagick's -gamma: 2.2 brightens. See <see cref="ColorFilter.Gamma"/>.</summary>
    /// <param name="gamma">The gamma, above 0.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="gamma"/> is not a positive finite number.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Tone.cs" region="tone"/>
    /// </example>
    public RasterOps Gamma(float gamma)
    {
        return ApplyFilter(ColorFilter.Gamma(gamma));
    }

    /// <summary>Adds (or removes) contrast with ImageMagick's scaled logistic curve, which never clips. See <see cref="ColorFilter.SigmoidalContrast"/>.</summary>
    /// <param name="contrast">How steep the curve is, 0 or more: 3 is gentle, 10 strong.</param>
    /// <param name="midpoint">Where the curve is steepest, from 0 to 1. Defaults to 0.5.</param>
    /// <param name="increase">Whether contrast increases (the default) or decreases.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Tone.cs" region="tone"/>
    /// </example>
    public RasterOps SigmoidalContrast(float contrast, float midpoint = 0.5f, bool increase = true)
    {
        return ApplyFilter(ColorFilter.SigmoidalContrast(contrast, midpoint, increase));
    }

    /// <summary>
    /// Rounds red, green and blue to <paramref name="levels"/> evenly spaced values each, as ImageMagick's -posterize, optionally
    /// dithered: ordered dithering adds a fixed 8 × 8 pattern before rounding, Floyd–Steinberg spreads each value's rounding error to
    /// its neighbours. Without dithering it is <see cref="ColorFilter.Posterize"/>.
    /// </summary>
    /// <param name="levels">Values per channel, from 2 to 256.</param>
    /// <param name="dither">The dithering. Defaults to <see cref="DitherMode.None"/>.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="levels"/> is outside 2 to 256, or <paramref name="dither"/> is not defined.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Tone.cs" region="effects"/>
    /// </example>
    public RasterOps Posterize(int levels, DitherMode dither = DitherMode.None)
    {
        ColorFilter.CheckLevels(levels);
        if (!Enum.IsDefined(dither))
        {
            throw new ArgumentOutOfRangeException(nameof(dither), dither, "The dither mode is not defined.");
        }

        return dither == DitherMode.None ? ApplyFilter(ColorFilter.Posterize(levels)) : Add(new PosterizeOperation(levels, dither));
    }

    /// <summary>Inverts red, green and blue values above <paramref name="threshold"/>, as ImageMagick's -solarize. See <see cref="ColorFilter.Solarize"/>.</summary>
    /// <param name="threshold">The level, from 0 to 1. Defaults to 0.5.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="threshold"/> is outside 0..1.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Tone.cs" region="effects"/>
    /// </example>
    public RasterOps Solarize(float threshold = 0.5f)
    {
        return ApplyFilter(ColorFilter.Solarize(threshold));
    }

    /// <summary>
    /// Makes a two-color image: pixels above <paramref name="level"/> take <paramref name="above"/>, the others
    /// <paramref name="below"/>, with alpha kept. See <see cref="ColorFilter.Threshold"/>.
    /// </summary>
    /// <param name="level">The level, from 0 to 1. Defaults to 0.5.</param>
    /// <param name="mode">What is measured. Defaults to <see cref="ThresholdMode.Luminance"/>.</param>
    /// <param name="below">The lower color, or <see langword="null"/> for black.</param>
    /// <param name="above">The upper color, or <see langword="null"/> for white.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is outside 0..1, or <paramref name="mode"/> is not defined.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Tone.cs" region="threshold"/>
    /// </example>
    public RasterOps Threshold(float level = 0.5f, ThresholdMode mode = ThresholdMode.Luminance, Rgba8? below = null, Rgba8? above = null)
    {
        return ApplyFilter(ColorFilter.Threshold(level, mode, below, above));
    }

    /// <summary>Blends red, green and blue toward <paramref name="color"/>, as ImageMagick's -colorize. See <see cref="ColorFilter.Colorize"/>.</summary>
    /// <param name="color">The color.</param>
    /// <param name="amount">The blend, from 0 to 1.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="amount"/> is outside 0..1.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Tone.cs" region="effects"/>
    /// </example>
    public RasterOps Colorize(Rgba8 color, float amount)
    {
        return ApplyFilter(ColorFilter.Colorize(color, amount));
    }

    /// <summary>Pushes midtones toward <paramref name="color"/> and keeps black and white, as ImageMagick's -tint. See <see cref="ColorFilter.Tint"/>.</summary>
    /// <param name="color">The tint.</param>
    /// <param name="amount">The strength, from 0 to 1. Defaults to 1.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="amount"/> is outside 0..1.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Tone.cs" region="tone"/>
    /// </example>
    public RasterOps Tint(Rgba8 color, float amount = 1)
    {
        return ApplyFilter(ColorFilter.Tint(color, amount));
    }

    /// <summary>Maps luminance onto the line from <paramref name="shadows"/> to <paramref name="highlights"/>. See <see cref="ColorFilter.Duotone"/>.</summary>
    /// <param name="shadows">The color for black.</param>
    /// <param name="highlights">The color for white.</param>
    /// <param name="weights">The luma weights. Defaults to <see cref="LumaWeights.Rec709"/>.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="weights"/> is not defined.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Tone.cs" region="effects"/>
    /// </example>
    public RasterOps Duotone(Rgba8 shadows, Rgba8 highlights, LumaWeights weights = LumaWeights.Rec709)
    {
        return ApplyFilter(ColorFilter.Duotone(shadows, highlights, weights));
    }

    /// <summary>Skia's high-contrast accessibility filter. See <see cref="ColorFilter.HighContrast"/>.</summary>
    /// <param name="grayscale">Whether colors become gray first.</param>
    /// <param name="invert">How to invert. Defaults to <see cref="ContrastInvert.None"/>.</param>
    /// <param name="contrast">The contrast, from −1 to 1.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Tone.cs" region="effects"/>
    /// </example>
    public RasterOps HighContrast(bool grayscale = false, ContrastInvert invert = ContrastInvert.None, float contrast = 0)
    {
        return ApplyFilter(ColorFilter.HighContrast(grayscale, invert, contrast));
    }

    /// <summary>Shows how the image looks with a color vision deficiency. See <see cref="ColorFilter.SimulateColorBlindness"/>.</summary>
    /// <param name="kind">The deficiency.</param>
    /// <param name="severity">From 0 (normal vision) to 1. Defaults to 1.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Tone.cs" region="effects"/>
    /// </example>
    public RasterOps SimulateColorBlindness(ColorVisionDeficiency kind, float severity = 1)
    {
        return ApplyFilter(ColorFilter.SimulateColorBlindness(kind, severity));
    }
}
