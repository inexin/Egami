using Egami.Operations.Tone;
using Egami.Pixels;

namespace Egami.Operations;

/// <summary>The tone steps and color matrices as filters.</summary>
public sealed partial class ColorFilter
{
    /// <summary>
    /// Returns ImageMagick's -level and +level in one filter: <paramref name="black"/>..<paramref name="white"/> is stretched to
    /// 0..1 (values outside it clip), raised to 1/<paramref name="gamma"/>, and mapped onto
    /// <paramref name="outputBlack"/>..<paramref name="outputWhite"/>. Red, green and blue change; alpha is kept.
    /// </summary>
    /// <param name="black">The input that becomes <paramref name="outputBlack"/>, from 0 to 1.</param>
    /// <param name="white">The input that becomes <paramref name="outputWhite"/>, from 0 to 1 and not equal to <paramref name="black"/>; below it inverts.</param>
    /// <param name="gamma">The midtone gamma, above 0: above 1 brightens the midtones.</param>
    /// <param name="outputBlack">The output for <paramref name="black"/>, from 0 to 1.</param>
    /// <param name="outputWhite">The output for <paramref name="white"/>, from 0 to 1; below <paramref name="outputBlack"/> inverts.</param>
    /// <returns>The filter.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A value is out of range, or <paramref name="black"/> equals <paramref name="white"/>.</exception>
    public static ColorFilter Level(float black = 0, float white = 1, float gamma = 1, float outputBlack = 0, float outputWhite = 1)
    {
        CheckUnit(black);
        CheckUnit(white);
        CheckUnit(outputBlack);
        CheckUnit(outputWhite);
        CheckPositive(gamma, nameof(gamma));
        if (black == white)
        {
            throw new ArgumentOutOfRangeException(nameof(white), white, "The white point must differ from the black point.");
        }

        if (black == 0 && white == 1 && gamma == 1 && outputBlack == 0 && outputWhite == 1)
        {
            return Identity;
        }

        return Curves(ToneCurve.FromFormula(v => ToneMath.Level(v, black, white, gamma, outputBlack, outputWhite)));
    }

    /// <summary>Returns ImageMagick's -gamma: each of red, green and blue becomes v^(1/<paramref name="gamma"/>), so 2.2 brightens.</summary>
    /// <param name="gamma">The gamma, above 0; 1 changes nothing.</param>
    /// <returns>The filter.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="gamma"/> is not a positive finite number.</exception>
    public static ColorFilter Gamma(float gamma)
    {
        CheckPositive(gamma, nameof(gamma));
        return gamma == 1 ? Identity : Curves(ToneCurve.FromFormula(v => ToneMath.Gamma(v, gamma)));
    }

    /// <summary>
    /// Returns ImageMagick's -sigmoidal-contrast (or +sigmoidal-contrast when <paramref name="increase"/> is false): an S-shaped
    /// logistic curve through <paramref name="midpoint"/>, scaled so black and white stay, which adds contrast without clipping.
    /// </summary>
    /// <param name="contrast">How steep the curve is, 0 or more: 0 changes nothing, 3 is gentle, 10 strong.</param>
    /// <param name="midpoint">Where the curve is steepest, from 0 to 1. Defaults to 0.5.</param>
    /// <param name="increase">Whether contrast increases (the default) or decreases, by the inverse curve.</param>
    /// <returns>The filter.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="contrast"/> is negative or not finite, or <paramref name="midpoint"/> is outside 0..1.</exception>
    public static ColorFilter SigmoidalContrast(float contrast, float midpoint = 0.5f, bool increase = true)
    {
        if (!(contrast >= 0) || !float.IsFinite(contrast))
        {
            throw new ArgumentOutOfRangeException(nameof(contrast), contrast, "The contrast must be finite and 0 or more.");
        }

        CheckUnit(midpoint);
        return contrast == 0 ? Identity : Curves(ToneCurve.FromFormula(v => ToneMath.Sigmoidal(v, contrast, midpoint, increase)));
    }

    /// <summary>
    /// Returns ImageMagick's -posterize without dithering: each of red, green and blue is rounded to <paramref name="levels"/> evenly
    /// spaced values. Dithered posterizing looks at neighbours, so it is only a step: <see cref="RasterOps.Posterize"/>.
    /// </summary>
    /// <param name="levels">Values per channel, from 2 to 256.</param>
    /// <returns>The filter.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="levels"/> is outside 2 to 256.</exception>
    public static ColorFilter Posterize(int levels)
    {
        CheckLevels(levels);
        return Curves(ToneCurve.FromFormula(v => ToneMath.Posterize(v, levels), smooth: false));
    }

    /// <summary>Returns ImageMagick's -solarize: red, green and blue values above <paramref name="threshold"/> are inverted.</summary>
    /// <param name="threshold">The level above which values invert, from 0 to 1. Defaults to 0.5.</param>
    /// <returns>The filter.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="threshold"/> is outside 0..1.</exception>
    public static ColorFilter Solarize(float threshold = 0.5f)
    {
        CheckUnit(threshold);
        return threshold == 1 ? Identity : Curves(ToneCurve.FromFormula(v => ToneMath.Solarize(v, threshold), smooth: false));
    }

    /// <summary>
    /// Returns a two-color threshold: pixels whose measured value lies above <paramref name="level"/> take <paramref name="above"/>'s
    /// red, green and blue, the others <paramref name="below"/>'s, and alpha is kept. In <see cref="ThresholdMode.Channels"/> each
    /// channel is measured and replaced on its own.
    /// </summary>
    /// <param name="level">The level, from 0 to 1. Defaults to 0.5.</param>
    /// <param name="mode">What is measured. Defaults to <see cref="ThresholdMode.Luminance"/>.</param>
    /// <param name="below">The color at or below the level, or <see langword="null"/> (the default) for black.</param>
    /// <param name="above">The color above the level, or <see langword="null"/> (the default) for white.</param>
    /// <returns>The filter.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is outside 0..1, or <paramref name="mode"/> is not defined.</exception>
    public static ColorFilter Threshold(float level = 0.5f, ThresholdMode mode = ThresholdMode.Luminance, Rgba8? below = null, Rgba8? above = null)
    {
        CheckUnit(level);
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "The threshold mode is not defined.");
        }

        return ThresholdAt(ThresholdStage.Quantum(level), mode, below ?? Rgba8.Black, above ?? Rgba8.White);
    }

    /// <summary>A threshold at a level in 16-bit units, for the measured steps.</summary>
    internal static ColorFilter ThresholdAt(double level, ThresholdMode mode, Rgba8 below, Rgba8 above)
    {
        if (mode != ThresholdMode.Channels)
        {
            return FromStages(new ThresholdStage(mode, level, below, above));
        }

        return Curves(Step(below.R, above.R), Step(below.G, above.G), Step(below.B, above.B));

        ToneCurve Step(byte from, byte to) => ToneCurve.FromFormula(v => ThresholdStage.Above(ThresholdStage.Sample(v), level) ? to / 255.0 : from / 255.0, smooth: false);
    }

    /// <summary>
    /// Returns ImageMagick's -colorize: red, green and blue are blended toward <paramref name="color"/> by <paramref name="amount"/>;
    /// the color's alpha is ignored and the pixel's kept.
    /// </summary>
    /// <param name="color">The color to blend toward.</param>
    /// <param name="amount">The blend, from 0 (no change) to 1 (flat <paramref name="color"/>).</param>
    /// <returns>The filter.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="amount"/> is outside 0..1.</exception>
    public static ColorFilter Colorize(Rgba8 color, float amount)
    {
        CheckUnit(amount);
        float k = 1 - amount;
        return Matrix(new ColorMatrix(
        [
            k, 0, 0, 0, amount * color.R / 255f,
            0, k, 0, 0, amount * color.G / 255f,
            0, 0, k, 0, amount * color.B / 255f,
            0, 0, 0, 1, 0,
        ]));
    }

    /// <summary>
    /// Returns ImageMagick's -tint: midtones are pushed toward <paramref name="color"/> along a parabola that is largest at mid-gray,
    /// and black and white stay as they are.
    /// </summary>
    /// <param name="color">The tint; its alpha is ignored.</param>
    /// <param name="amount">The strength, from 0 to 1. Defaults to 1.</param>
    /// <returns>The filter.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="amount"/> is outside 0..1.</exception>
    public static ColorFilter Tint(Rgba8 color, float amount = 1)
    {
        CheckUnit(amount);
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        double intensity = ToneMath.Intensity(r, g, b);
        return Curves(
            ToneCurve.FromFormula(v => ToneMath.Tint(v, r, intensity, amount)),
            ToneCurve.FromFormula(v => ToneMath.Tint(v, g, intensity, amount)),
            ToneCurve.FromFormula(v => ToneMath.Tint(v, b, intensity, amount)));
    }

    /// <summary>
    /// Returns a duotone: each pixel's luminance is mapped onto the line from <paramref name="shadows"/> to
    /// <paramref name="highlights"/>, as ImageMagick's -grayscale followed by +level-colors. The colors' alpha is ignored.
    /// </summary>
    /// <param name="shadows">The color for black.</param>
    /// <param name="highlights">The color for white.</param>
    /// <param name="weights">The luma weights. Defaults to <see cref="LumaWeights.Rec709"/>.</param>
    /// <returns>The filter.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="weights"/> is not defined.</exception>
    public static ColorFilter Duotone(Rgba8 shadows, Rgba8 highlights, LumaWeights weights = LumaWeights.Rec709)
    {
        (float wr, float wg, float wb) = Operations.ColorMatrix.Weights(weights);
        float[] values = new float[20];
        ReadOnlySpan<float> low = [shadows.R / 255f, shadows.G / 255f, shadows.B / 255f];
        ReadOnlySpan<float> high = [highlights.R / 255f, highlights.G / 255f, highlights.B / 255f];
        for (int row = 0; row < 3; row++)
        {
            float span = high[row] - low[row];
            values[(row * 5) + 0] = span * wr;
            values[(row * 5) + 1] = span * wg;
            values[(row * 5) + 2] = span * wb;
            values[(row * 5) + 4] = low[row];
        }

        values[18] = 1;
        return Matrix(new ColorMatrix(values));
    }

    /// <summary>
    /// Returns Skia's high-contrast accessibility filter: optionally gray, then optionally inverted in brightness or lightness, then
    /// contrast around mid-gray, all in linear light as Skia runs it.
    /// </summary>
    /// <param name="grayscale">Whether colors become gray first.</param>
    /// <param name="invert">How to invert. Defaults to <see cref="ContrastInvert.None"/>.</param>
    /// <param name="contrast">The contrast, from −1 (flat gray) through 0 (no change) to 1 (black and white).</param>
    /// <returns>The filter.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="invert"/> is not defined, or <paramref name="contrast"/> is outside −1..1.</exception>
    public static ColorFilter HighContrast(bool grayscale = false, ContrastInvert invert = ContrastInvert.None, float contrast = 0)
    {
        if (!Enum.IsDefined(invert))
        {
            throw new ArgumentOutOfRangeException(nameof(invert), invert, "The inversion is not defined.");
        }

        if (!(contrast >= -1 && contrast <= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(contrast), contrast, "The contrast must be from -1 to 1.");
        }

        return !grayscale && invert == ContrastInvert.None && contrast == 0 ? Identity : FromStages(new HighContrastStage(grayscale, invert, contrast));
    }

    /// <summary>
    /// Returns how the colors look to someone with a color vision deficiency, from the matrices of Machado, Oliveira and Fernandes
    /// (2009), applied in linear light: severity 1 is the full -opia, values below it the milder -anomaly.
    /// </summary>
    /// <param name="kind">The deficiency.</param>
    /// <param name="severity">From 0 (normal vision) to 1. Defaults to 1.</param>
    /// <returns>The filter.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not defined, or <paramref name="severity"/> is outside 0..1.</exception>
    public static ColorFilter SimulateColorBlindness(ColorVisionDeficiency kind, float severity = 1)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "The deficiency is not defined.");
        }

        CheckUnit(severity);
        return severity == 0 ? Identity : FromStages(new LinearMatrixStage(ToneMath.ColorBlindness(kind, severity)));
    }

    /// <summary>Returns <see cref="Operations.ColorMatrix.Brightness"/> as a filter.</summary>
    /// <param name="amount">The factor, 0 or more.</param>
    /// <returns>The filter.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="amount"/> is negative or not finite.</exception>
    public static ColorFilter Brightness(float amount) => Matrix(Operations.ColorMatrix.Brightness(amount));

    /// <summary>Returns <see cref="Operations.ColorMatrix.Contrast"/> as a filter.</summary>
    /// <param name="amount">The factor, 0 or more.</param>
    /// <returns>The filter.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="amount"/> is negative or not finite.</exception>
    public static ColorFilter Contrast(float amount) => Matrix(Operations.ColorMatrix.Contrast(amount));

    /// <summary>Returns <see cref="Operations.ColorMatrix.Saturation"/> as a filter.</summary>
    /// <param name="amount">The factor, 0 or more.</param>
    /// <returns>The filter.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="amount"/> is negative or not finite.</exception>
    public static ColorFilter Saturation(float amount) => Matrix(Operations.ColorMatrix.Saturation(amount));

    /// <summary>Returns <see cref="Operations.ColorMatrix.HueRotate"/> as a filter.</summary>
    /// <param name="degrees">The angle.</param>
    /// <returns>The filter.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="degrees"/> is not finite.</exception>
    public static ColorFilter HueRotate(float degrees) => Matrix(Operations.ColorMatrix.HueRotate(degrees));

    /// <summary>Returns <see cref="Operations.ColorMatrix.Grayscale"/> as a filter, on the stored values.</summary>
    /// <param name="mode">The luma weights. Defaults to <see cref="LumaWeights.Rec709"/>.</param>
    /// <param name="amount">0 changes nothing, 1 gives full gray.</param>
    /// <returns>The filter.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range.</exception>
    public static ColorFilter Grayscale(LumaWeights mode = LumaWeights.Rec709, float amount = 1) => Matrix(Operations.ColorMatrix.Grayscale(mode, amount));

    /// <summary>Returns <see cref="Operations.ColorMatrix.Sepia"/> as a filter.</summary>
    /// <param name="amount">0 changes nothing, 1 gives full sepia.</param>
    /// <returns>The filter.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="amount"/> is outside 0..1.</exception>
    public static ColorFilter Sepia(float amount = 1) => Matrix(Operations.ColorMatrix.Sepia(amount));

    /// <summary>Returns <see cref="Operations.ColorMatrix.Invert"/> as a filter.</summary>
    /// <param name="amount">0 changes nothing, 1 inverts fully.</param>
    /// <returns>The filter.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="amount"/> is outside 0..1.</exception>
    public static ColorFilter Invert(float amount = 1) => Matrix(Operations.ColorMatrix.Invert(amount));

    /// <summary>Returns <see cref="Operations.ColorMatrix.Opacity"/> as a filter.</summary>
    /// <param name="amount">0 makes pixels transparent, 1 changes nothing.</param>
    /// <returns>The filter.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="amount"/> is outside 0..1.</exception>
    public static ColorFilter Opacity(float amount) => Matrix(Operations.ColorMatrix.Opacity(amount));

    internal static void CheckLevels(int levels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(levels, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(levels, 256);
    }

    private static void CheckPositive(float value, string name)
    {
        if (!(value > 0) || !float.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(name, value, "The value must be a positive finite number.");
        }
    }
}
