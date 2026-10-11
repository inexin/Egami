using Egami.Operations.Tone;
using Egami.Pixels;

namespace Egami.Operations;

/// <summary>Adjustments that read the image first: auto levels, white balance, equalization and thresholds.</summary>
public sealed partial class RasterOps
{
    /// <summary>
    /// Stretches the darkest value to 0 and the lightest to 1, as ImageMagick's -auto-level. The chain runs up to this step, the
    /// result is measured, and the step becomes a <see cref="Level"/>; inside <see cref="Within(Rect, Action{RasterOps})"/>,
    /// <see cref="Within(Mask, Action{RasterOps})"/> or <see cref="OnChannels"/> only that scope is measured. Transparent pixels do
    /// not count.
    /// </summary>
    /// <param name="linked">
    /// Whether red, green and blue share one black and one white point (the default, which keeps colors), or each gets its own,
    /// which also removes a cast.
    /// </param>
    /// <param name="perFrame">Whether each frame is measured alone, as ImageMagick does, instead of all frames together so an animation does not flicker.</param>
    /// <returns>This builder.</returns>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Tone.cs" region="auto"/>
    /// </example>
    public RasterOps AutoLevel(bool linked = true, bool perFrame = false)
    {
        return Add(new AutoLevelStep(linked, perFrame));
    }

    /// <summary>
    /// Clips the darkest <paramref name="black"/> and lightest <paramref name="white"/> fractions of the pixels, then stretches the
    /// rest to 0..1, as ImageMagick's -contrast-stretch. Linked, the points come from the luminance histogram, as ImageMagick's
    /// synced channels. Measured like <see cref="AutoLevel"/>.
    /// </summary>
    /// <param name="black">The fraction of pixels that becomes black, from 0 to 1.</param>
    /// <param name="white">The fraction that becomes white, from 0 to 1; with <paramref name="black"/> less than 1.</param>
    /// <param name="linked">Whether the channels share one pair of points (the default) or each gets its own.</param>
    /// <param name="perFrame">Whether each frame is measured alone.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A fraction is outside 0..1, or the two add up to 1 or more.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Tone.cs" region="auto"/>
    /// </example>
    public RasterOps ContrastStretch(float black, float white, bool linked = true, bool perFrame = false)
    {
        ColorFilter.CheckUnit(black);
        ColorFilter.CheckUnit(white);
        if (!(black + white < 1))
        {
            throw new ArgumentOutOfRangeException(nameof(white), white, "The two fractions must add up to less than 1.");
        }

        return Add(new ContrastStretchStep(black, white, linked, perFrame));
    }

    /// <summary>
    /// <see cref="ContrastStretch"/> at ImageMagick's -normalize points: the darkest 2% of the pixels become black and the lightest
    /// 1% white.
    /// </summary>
    /// <param name="linked">Whether the channels share one pair of points (the default) or each gets its own.</param>
    /// <param name="perFrame">Whether each frame is measured alone.</param>
    /// <returns>This builder.</returns>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Tone.cs" region="auto"/>
    /// </example>
    public RasterOps Normalize(bool linked = true, bool perFrame = false)
    {
        return Add(new ContrastStretchStep(ToneMeasures.NormalizeBlack, ToneMeasures.NormalizeWhite, linked, perFrame));
    }

    /// <summary>
    /// Applies the <see cref="Gamma"/> that moves the image's mean to 0.5, as ImageMagick's -auto-gamma; an image whose mean is 0
    /// or 1 is left alone. Measured like <see cref="AutoLevel"/>.
    /// </summary>
    /// <param name="linked">Whether the channels share one gamma from their joint mean (the default) or each gets its own.</param>
    /// <param name="perFrame">Whether each frame is measured alone.</param>
    /// <returns>This builder.</returns>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Tone.cs" region="auto"/>
    /// </example>
    public RasterOps AutoGamma(bool linked = true, bool perFrame = false)
    {
        return Add(new AutoGammaStep(linked, perFrame));
    }

    /// <summary>
    /// Makes <paramref name="neutral"/> a neutral gray of its own luminance by scaling red, green and blue in linear light, as a
    /// photo editor's white-balance picker does.
    /// </summary>
    /// <param name="neutral">A color that should be gray, such as a gray card's pixels.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A channel of <paramref name="neutral"/> is 0, so no gain can make it gray.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Tone.cs" region="white-balance"/>
    /// </example>
    public RasterOps WhiteBalance(Rgba8 neutral)
    {
        if (neutral.R == 0 || neutral.G == 0 || neutral.B == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(neutral), neutral, "Every channel of the neutral color must be above 0.");
        }

        return ApplyFilter(ToneMeasures.Gains(Srgb.ToLinear(neutral.R / 255.0), Srgb.ToLinear(neutral.G / 255.0), Srgb.ToLinear(neutral.B / 255.0)));
    }

    /// <summary>
    /// Corrects for a light of <paramref name="temperature"/> kelvin, as a raw editor's temperature and tint sliders: the light's
    /// white is adapted to D65 with the Bradford transform, in linear light. Use the light the photo was taken under: 3200 for
    /// tungsten makes a tungsten-lit photo neutral.
    /// </summary>
    /// <param name="temperature">The light's correlated color temperature, from 1,500 to 15,000 kelvin; 6504 changes almost nothing.</param>
    /// <param name="tint">
    /// The light's offset from a black body's color, from −1 to 1 (Δuv of ±0.05): positive for a greener light, which the correction
    /// turns magenta, as Lightroom's positive tint.
    /// </param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Tone.cs" region="white-balance"/>
    /// </example>
    public RasterOps WhiteBalance(float temperature, float tint = 0)
    {
        if (!(temperature >= 1500 && temperature <= 15000))
        {
            throw new ArgumentOutOfRangeException(nameof(temperature), temperature, "The temperature must be from 1,500 to 15,000 kelvin.");
        }

        if (!(tint >= -1 && tint <= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(tint), tint, "The tint must be from -1 to 1.");
        }

        return ApplyFilter(ToneMeasures.Temperature(temperature, tint));
    }

    /// <summary>
    /// Removes a color cast it measures, by <paramref name="method"/>. Measured like <see cref="AutoLevel"/>.
    /// </summary>
    /// <param name="method">How the cast is found. Defaults to <see cref="WhiteBalanceMethod.GrayWorld"/>, ImageMagick's -white-balance.</param>
    /// <param name="perFrame">Whether each frame is measured alone.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="method"/> is not defined.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Tone.cs" region="white-balance"/>
    /// </example>
    public RasterOps AutoWhiteBalance(WhiteBalanceMethod method = WhiteBalanceMethod.GrayWorld, bool perFrame = false)
    {
        CheckDefined(method, nameof(method));
        return Add(new AutoWhiteBalanceStep(method, perFrame));
    }

    /// <summary>
    /// Spreads the values evenly by histogram equalization: of L* by default, keeping a* and b* so colors hold, or of each channel
    /// as ImageMagick's -equalize. Measured like <see cref="AutoLevel"/>; a flat image is left alone.
    /// </summary>
    /// <param name="mode">What is equalized. Defaults to <see cref="EqualizeMode.Luminance"/>.</param>
    /// <param name="perFrame">Whether each frame is measured alone.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is not defined.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Tone.cs" region="equalize"/>
    /// </example>
    public RasterOps Equalize(EqualizeMode mode = EqualizeMode.Luminance, bool perFrame = false)
    {
        CheckDefined(mode, nameof(mode));
        return Add(new EqualizeStep(mode, perFrame));
    }

    /// <summary>
    /// Contrast-limited adaptive histogram equalization (Zuiderveld, 1994) on L*, as ImageMagick's -clahe: each of
    /// <paramref name="tilesX"/> × <paramref name="tilesY"/> tiles gets its own clipped, equalized curve, blended smoothly between tile
    /// centers, so local detail comes up without the noise of plain equalization. a* and b* are kept. Each frame is its own.
    /// </summary>
    /// <param name="tilesX">Tiles across, from 1 to 256. Defaults to 8.</param>
    /// <param name="tilesY">Tiles down, from 1 to 256. Defaults to 8.</param>
    /// <param name="clipLimit">How far a tile's histogram may rise, as a multiple of its mean bin (2 is mild, 4 strong); 0 does not clip.</param>
    /// <param name="bins">The histogram bins, from 2 to 4,096. Defaults to 256.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range.</exception>
    /// <remarks>The lightness plane, 4 bytes a pixel, counts against MaxTotalBytes. Inside a rectangle or mask, the scope is its image.</remarks>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Tone.cs" region="clahe"/>
    /// </example>
    public RasterOps Clahe(int tilesX = 8, int tilesY = 8, float clipLimit = 2, int bins = 256)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(tilesX, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(tilesX, 256);
        ArgumentOutOfRangeException.ThrowIfLessThan(tilesY, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(tilesY, 256);
        ArgumentOutOfRangeException.ThrowIfLessThan(bins, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bins, 4096);
        if (!(clipLimit >= 0) || !float.IsFinite(clipLimit))
        {
            throw new ArgumentOutOfRangeException(nameof(clipLimit), clipLimit, "The clip limit must be finite and 0 or more.");
        }

        return Add(new ClaheOperation(tilesX, tilesY, clipLimit, bins));
    }

    /// <summary>
    /// Brings out local detail, as ImageMagick's -local-contrast: L* is pushed away from a wide blur of itself by
    /// <paramref name="strength"/>, so large-scale light changes less than detail. a* and b* are kept.
    /// </summary>
    /// <param name="radius">The blur radius as a fraction of 20% of the longer side, above 0 and at most 1; the default 0.1 blurs over 2%.</param>
    /// <param name="strength">How strongly detail is raised, 0 or more; the default 0.125 is ImageMagick's 12.5%.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range.</exception>
    /// <remarks>Two lightness planes, 4 bytes a pixel each, count against MaxTotalBytes. Inside a rectangle or mask, the scope is its image.</remarks>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Tone.cs" region="clahe"/>
    /// </example>
    public RasterOps LocalContrast(float radius = 0.1f, float strength = 0.125f)
    {
        if (!(radius > 0 && radius <= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(radius), radius, "The radius must be above 0 and at most 1.");
        }

        if (!(strength >= 0) || !float.IsFinite(strength))
        {
            throw new ArgumentOutOfRangeException(nameof(strength), strength, "The strength must be finite and 0 or more.");
        }

        return strength == 0 ? this : Add(new LocalContrastOperation(radius, strength));
    }

    /// <summary>
    /// Thresholds at the level <paramref name="method"/> finds in the histogram, as ImageMagick's -auto-threshold; see
    /// <see cref="Raster.FindThreshold"/>. Measured like <see cref="AutoLevel"/>.
    /// </summary>
    /// <param name="method">How the level is found. Defaults to <see cref="ThresholdMethod.Otsu"/>.</param>
    /// <param name="mode">What is measured. Defaults to <see cref="ThresholdMode.Luminance"/>.</param>
    /// <param name="below">The lower color, or <see langword="null"/> for black.</param>
    /// <param name="above">The upper color, or <see langword="null"/> for white.</param>
    /// <param name="perFrame">Whether each frame is measured alone.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An enum value is not defined.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Tone.cs" region="auto-threshold"/>
    /// </example>
    public RasterOps AutoThreshold(ThresholdMethod method = ThresholdMethod.Otsu, ThresholdMode mode = ThresholdMode.Luminance, Rgba8? below = null, Rgba8? above = null, bool perFrame = false)
    {
        CheckDefined(method, nameof(method));
        CheckDefined(mode, nameof(mode));
        return Add(new AutoThresholdStep(method, mode, below, above, perFrame));
    }

    /// <summary>
    /// Thresholds each pixel against the mean of the window around it (Bradley and Roth, 2007), as the reference library's AdaptiveThreshold: a
    /// pixel is dark when its luminance is more than <paramref name="limit"/> below its window's mean, so uneven lighting on a scanned
    /// page does not swallow text. Alpha is kept.
    /// </summary>
    /// <param name="limit">How far below the local mean a pixel must be to count as dark, from 0 to 1. Defaults to 0.15.</param>
    /// <param name="window">The window's side in pixels, at least 1, or <see langword="null"/> (the default) for an eighth of the width.</param>
    /// <param name="below">The dark color, or <see langword="null"/> for black.</param>
    /// <param name="above">The light color, or <see langword="null"/> for white.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An argument is out of range.</exception>
    /// <remarks>A luminance plane, 4 bytes a pixel, counts against MaxTotalBytes. Inside a rectangle or mask, the scope is its image.</remarks>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Tone.cs" region="auto-threshold"/>
    /// </example>
    public RasterOps AdaptiveThreshold(float limit = 0.15f, int? window = null, Rgba8? below = null, Rgba8? above = null)
    {
        ColorFilter.CheckUnit(limit);
        if (window is int w)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(w, 1, nameof(window));
        }

        return Add(new AdaptiveThresholdOperation(limit, window, below ?? Rgba8.Black, above ?? Rgba8.White));
    }

    private static void CheckDefined<T>(T value, string name)
        where T : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(name, value, "The value is not defined.");
        }
    }
}
