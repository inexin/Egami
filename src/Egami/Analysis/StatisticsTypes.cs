namespace Egami.Analysis;

/// <summary>
/// The statistics of one channel over a frame, on the 0 to 1 scale of the channel values, as ImageMagick's identify -verbose
/// reports them.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Analysis.cs" region="statistics"/>
/// </example>
public readonly record struct ChannelStatistics
{
    /// <summary>Gets the smallest value.</summary>
    public double Min { get; internal init; }

    /// <summary>Gets the largest value.</summary>
    public double Max { get; internal init; }

    /// <summary>Gets the mean.</summary>
    public double Mean { get; internal init; }

    /// <summary>Gets the sample standard deviation, √(Σ(x − mean)² / (n − 1)), or 0 for a single pixel.</summary>
    public double StdDev { get; internal init; }

    /// <summary>Gets the skewness: the third central moment divided by <see cref="StdDev"/>³, 0 when every value is equal.</summary>
    public double Skewness { get; internal init; }

    /// <summary>Gets the excess kurtosis: the fourth central moment divided by <see cref="StdDev"/>⁴, minus 3; 0 when every value is equal.</summary>
    public double Kurtosis { get; internal init; }

    /// <summary>
    /// Gets the Shannon entropy of the values at 16-bit precision divided by the most it could be for the number of distinct
    /// values present, so 1 when every distinct value is equally common and 0 when there is one value.
    /// </summary>
    public double Entropy { get; internal init; }
}

/// <summary>The statistics of each channel of a frame, from <see cref="Raster.Statistics"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Analysis.cs" region="statistics"/>
/// </example>
public sealed class RasterStatistics
{
    internal RasterStatistics(ChannelStatistics red, ChannelStatistics green, ChannelStatistics blue, ChannelStatistics alpha, ChannelStatistics luma, long pixels)
    {
        (Red, Green, Blue, Alpha, Luma, PixelCount) = (red, green, blue, alpha, luma, pixels);
    }

    /// <summary>Gets red's statistics; a gray image's gray value.</summary>
    public ChannelStatistics Red { get; }

    /// <summary>Gets green's statistics.</summary>
    public ChannelStatistics Green { get; }

    /// <summary>Gets blue's statistics.</summary>
    public ChannelStatistics Blue { get; }

    /// <summary>Gets alpha's statistics: all 1 for an image without alpha.</summary>
    public ChannelStatistics Alpha { get; }

    /// <summary>Gets the statistics of the Rec. 709 luma of the stored values, 0.2126 R + 0.7152 G + 0.0722 B, as Grayscale computes it.</summary>
    public ChannelStatistics Luma { get; }

    /// <summary>Gets how many pixels were measured.</summary>
    public long PixelCount { get; }
}

/// <summary>
/// How many pixels of a frame have each value of one channel, in equal bins over 0 to 1, from <see cref="Raster.Histogram"/>.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Analysis.cs" region="histogram"/>
/// </example>
public sealed class Histogram
{
    private readonly long[] _counts;

    internal Histogram(long[] counts)
    {
        _counts = counts;
        Total = counts.Sum();
    }

    /// <summary>Gets the count of each bin: bin i holds values from i / Bins up to (i + 1) / Bins, and the last bin also holds 1.</summary>
    public IReadOnlyList<long> Counts => _counts;

    /// <summary>Gets the number of bins.</summary>
    public int Bins => _counts.Length;

    /// <summary>Gets the number of pixels counted.</summary>
    public long Total { get; }

    /// <summary>
    /// Returns the value below which a fraction <paramref name="p"/> of the pixels lie: the level i / (Bins − 1) of the first bin
    /// at which the running count reaches p × <see cref="Total"/>. With 256 bins on 8-bit data, the level is the 8-bit value / 255.
    /// </summary>
    /// <param name="p">The fraction, from 0 to 1.</param>
    /// <returns>The level, from 0 to 1.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="p"/> is outside 0 to 1.</exception>
    public double Percentile(double p)
    {
        if (!(p >= 0 && p <= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(p), p, "The fraction must be from 0 to 1.");
        }

        double target = Math.Max(p * Total, 1);
        long running = 0;
        for (int i = 0; i < _counts.Length; i++)
        {
            running += _counts[i];
            if (running >= target)
            {
                return _counts.Length == 1 ? 0 : (double)i / (_counts.Length - 1);
            }
        }

        return 1;
    }

    /// <summary>Gets the median level, <see cref="Percentile"/>(0.5): the lower middle value when the count is even.</summary>
    public double Median => Percentile(0.5);
}
