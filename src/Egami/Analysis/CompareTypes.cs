using Egami.Pixels;

namespace Egami.Analysis;

/// <summary>How <see cref="Raster.Compare"/> and <see cref="Raster.Difference"/> treat alpha.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Analysis.cs" region="compare"/>
/// </example>
public enum AlphaCompare
{
    /// <summary>
    /// Colors are multiplied by their alpha before they are compared, so two fully transparent pixels are equal whatever color is
    /// stored under them: what a viewer sees. ImageMagick 7's compare works this way too.
    /// </summary>
    Premultiplied = 0,

    /// <summary>The stored channels are compared as they are, so a different color under a transparent pixel counts.</summary>
    Straight = 1,
}

/// <summary>Options for <see cref="Raster.Compare"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Analysis.cs" region="compare"/>
/// </example>
public sealed record CompareOptions
{
    private readonly AlphaCompare _alpha;
    private readonly float _fuzz;
    private readonly int _frame;

    /// <summary>Gets the default options: premultiplied alpha, no fuzz, no SSIM, the whole first frame.</summary>
    public static CompareOptions Default { get; } = new();

    /// <summary>Gets how alpha is treated. Defaults to <see cref="AlphaCompare.Premultiplied"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not defined.</exception>
    public AlphaCompare Alpha
    {
        get => _alpha;
        init => _alpha = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The alpha mode is not defined.");
    }

    /// <summary>
    /// Gets how far a channel may differ, from 0 to 1, before its pixel counts in <see cref="CompareResult.DifferentPixels"/>:
    /// ImageMagick's AE metric with -fuzz. Defaults to 0, so any difference counts.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 1.</exception>
    public float Fuzz
    {
        get => _fuzz;
        init => _fuzz = value is >= 0 and <= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The fuzz must be from 0 to 1.");
    }

    /// <summary>
    /// Gets whether SSIM and DSSIM are computed too. They cost about as much as four blurs, so they are off by default and
    /// <see cref="ChannelDifference.Ssim"/> is NaN.
    /// </summary>
    public bool Ssim { get; init; }

    /// <summary>Gets the rectangle compared, or <see langword="null"/> (the default) for the whole image.</summary>
    public Rect? Region { get; init; }

    /// <summary>Gets the frame compared, 0 (the default) for the first.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int Frame
    {
        get => _frame;
        init => _frame = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The frame must not be negative.");
    }
}

/// <summary>
/// How much one channel of two images differs, on the 0 to 1 scale of the channel values, so 8-bit and 16-bit results line up.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Analysis.cs" region="compare"/>
/// </example>
public readonly record struct ChannelDifference
{
    /// <summary>Gets the mean absolute error: the average of |a − b|.</summary>
    public double Mae { get; internal init; }

    /// <summary>Gets the mean squared error: the average of (a − b)².</summary>
    public double Mse { get; internal init; }

    /// <summary>Gets the root mean squared error, √<see cref="Mse"/>.</summary>
    public double Rmse => Math.Sqrt(Mse);

    /// <summary>Gets the peak signal-to-noise ratio in decibels, 10·log₁₀(1 / <see cref="Mse"/>), or +∞ when the channels are equal.</summary>
    public double Psnr => Mse == 0 ? double.PositiveInfinity : 10 * Math.Log10(1 / Mse);

    /// <summary>Gets the largest |a − b| of any pixel (ImageMagick's PAE).</summary>
    public double PeakError { get; internal init; }

    /// <summary>
    /// Gets the structural similarity (Wang et al. 2004), 1 for equal channels, or NaN unless <see cref="CompareOptions.Ssim"/> was set.
    /// </summary>
    public double Ssim { get; internal init; }

    /// <summary>Gets the structural dissimilarity, (1 − <see cref="Ssim"/>) / 2, or NaN when SSIM was not computed.</summary>
    public double Dssim => (1 - Ssim) / 2;
}

/// <summary>What <see cref="Raster.Compare"/> found: each channel's difference, the overall one, and how many pixels differ.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Analysis.cs" region="compare"/>
/// </example>
public sealed class CompareResult
{
    internal CompareResult(ChannelDifference red, ChannelDifference green, ChannelDifference blue, ChannelDifference alpha, bool withAlpha, long different, long pixels)
    {
        Red = red;
        Green = green;
        Blue = blue;
        Alpha = alpha;
        DifferentPixels = different;
        PixelCount = pixels;
        int count = withAlpha ? 4 : 3;
        double mae = red.Mae + green.Mae + blue.Mae + (withAlpha ? alpha.Mae : 0);
        double mse = red.Mse + green.Mse + blue.Mse + (withAlpha ? alpha.Mse : 0);
        double ssim = red.Ssim + green.Ssim + blue.Ssim + (withAlpha ? alpha.Ssim : 0);
        double peak = Math.Max(Math.Max(red.PeakError, green.PeakError), Math.Max(blue.PeakError, withAlpha ? alpha.PeakError : 0));
        Overall = new ChannelDifference { Mae = mae / count, Mse = mse / count, PeakError = peak, Ssim = ssim / count };
    }

    /// <summary>Gets the red channel's difference (premultiplied by alpha unless <see cref="AlphaCompare.Straight"/>).</summary>
    public ChannelDifference Red { get; }

    /// <summary>Gets the green channel's difference.</summary>
    public ChannelDifference Green { get; }

    /// <summary>Gets the blue channel's difference.</summary>
    public ChannelDifference Blue { get; }

    /// <summary>Gets the alpha channel's difference: zero when neither image has alpha.</summary>
    public ChannelDifference Alpha { get; }

    /// <summary>
    /// Gets the difference over every channel: the mean of the color channels' errors, plus alpha's when either image has alpha,
    /// as ImageMagick's "all" line; <see cref="ChannelDifference.PeakError"/> is the largest of them.
    /// </summary>
    public ChannelDifference Overall { get; }

    /// <summary>Gets how many pixels have a channel that differs by more than <see cref="CompareOptions.Fuzz"/> (ImageMagick's AE).</summary>
    public long DifferentPixels { get; }

    /// <summary>Gets how many pixels were compared.</summary>
    public long PixelCount { get; }

    /// <summary>Gets whether every compared channel of every pixel is equal.</summary>
    public bool IsIdentical => Overall.PeakError == 0;
}

/// <summary>What <see cref="Raster.Difference"/> draws.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Analysis.cs" region="difference"/>
/// </example>
public enum DifferenceStyle
{
    /// <summary>
    /// ImageMagick compare's picture: pixels that differ in <see cref="DifferenceOptions.HighlightColor"/> and the rest in
    /// <see cref="DifferenceOptions.LowlightColor"/>, both over a faded copy of the first image flattened onto white.
    /// </summary>
    Highlight = 0,

    /// <summary>|a − b| of each color channel, times <see cref="DifferenceOptions.Amplify"/>, as an opaque image: black where equal.</summary>
    Absolute = 1,
}

/// <summary>Options for <see cref="Raster.Difference"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Analysis.cs" region="difference"/>
/// </example>
public sealed record DifferenceOptions
{
    private readonly AlphaCompare _alpha;
    private readonly float _fuzz;
    private readonly float _opacity = 0.2f;
    private readonly float _amplify = 1;
    private readonly int _frame;

    /// <summary>Gets the default options.</summary>
    public static DifferenceOptions Default { get; } = new();

    /// <summary>Gets how alpha is treated. Defaults to <see cref="AlphaCompare.Premultiplied"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not defined.</exception>
    public AlphaCompare Alpha
    {
        get => _alpha;
        init => _alpha = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The alpha mode is not defined.");
    }

    /// <summary>Gets how far a channel may differ, from 0 to 1, and still count as equal for <see cref="DifferenceStyle.Highlight"/>. Defaults to 0.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 1.</exception>
    public float Fuzz
    {
        get => _fuzz;
        init => _fuzz = value is >= 0 and <= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The fuzz must be from 0 to 1.");
    }

    /// <summary>Gets the color of pixels that differ. Defaults to ImageMagick's red, #F1001E.</summary>
    public Rgba8 HighlightColor { get; init; } = new(0xF1, 0x00, 0x1E, 0xFF);

    /// <summary>Gets the color of pixels that are equal. Defaults to white.</summary>
    public Rgba8 LowlightColor { get; init; } = new(0xFF, 0xFF, 0xFF, 0xFF);

    /// <summary>
    /// Gets how much of the first image shows through both colors, from 0 (none: a plain mask) to 1. Defaults to 0.2, as
    /// ImageMagick's colors at 80% opacity.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 1.</exception>
    public float LowlightOpacity
    {
        get => _opacity;
        init => _opacity = value is >= 0 and <= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The opacity must be from 0 to 1.");
    }

    /// <summary>Gets the factor <see cref="DifferenceStyle.Absolute"/> multiplies differences by, so small ones show. Defaults to 1.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not finite or not positive.</exception>
    public float Amplify
    {
        get => _amplify;
        init => _amplify = float.IsFinite(value) && value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The factor must be finite and greater than 0.");
    }

    /// <summary>Gets the frame compared, 0 (the default) for the first.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int Frame
    {
        get => _frame;
        init => _frame = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The frame must not be negative.");
    }
}
