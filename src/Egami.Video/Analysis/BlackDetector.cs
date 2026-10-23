namespace Egami.Video.Analysis;

/// <summary>
/// Finds black stretches: runs of frames where nearly every luma sample is near black, lasting at least
/// <see cref="BlackOptions.MinDuration"/>. It keeps ffmpeg's blackdetect measure and defaults, with the pixel threshold a fraction
/// of the stream's own range so 8-bit and 10-bit files behave alike.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-video-detectors"/>
/// </example>
public sealed class BlackDetector : IVideoDetector
{
    private readonly BlackOptions _options;
    private readonly RangeBuilder _ranges;
    private readonly List<FrameValue> _series = [];
    private long _count;
    private MediaTime _lastEnd;
    private bool _complete;

    /// <summary>Creates a detector.</summary>
    /// <param name="options">The thresholds, or <see langword="null"/> for <see cref="BlackOptions.Default"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-video-detectors"/>
    /// </example>
    public BlackDetector(BlackOptions? options = null)
    {
        _options = options ?? BlackOptions.Default;
        _options.Validate();
        _ranges = new RangeBuilder(_options.MinDuration);
    }

    /// <summary>Gets the options the detector runs with.</summary>
    public BlackOptions Options => _options;

    /// <summary>Gets the black ranges found so far; each range's value is the lowest black ratio in it.</summary>
    public IReadOnlyList<DetectedRange> Ranges => _ranges.Ranges;

    /// <summary>Gets every frame's black ratio, when <see cref="BlackOptions.KeepSeries"/> is set; otherwise empty.</summary>
    public IReadOnlyList<FrameValue> Series => _series;

    /// <summary>Gets the share of the last frame's luma samples that counted as black, from 0 to 1.</summary>
    public double LastRatio { get; private set; }

    /// <summary>Gets how many frames the detector has taken.</summary>
    public long FrameCount => _count;

    /// <summary>Takes the next frame.</summary>
    /// <param name="frame">The frame, in presentation order.</param>
    /// <exception cref="InvalidOperationException"><see cref="Complete"/> was called.</exception>
    public void Add(YuvVideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (_complete)
        {
            throw new InvalidOperationException("The detector is complete; create a new one for more frames.");
        }

        double ratio = Ratio(frame.Raster);
        LastRatio = ratio;
        if (_options.KeepSeries)
        {
            _series.Add(new FrameValue(_count, frame.Timestamp, ratio));
        }

        _count++;
        if (ratio >= _options.PictureRatio)
        {
            _ranges.Extend(frame.Timestamp, ratio, Math.Min);
        }
        else
        {
            _ranges.Close(frame.Timestamp);
        }

        _lastEnd = frame.Duration is { } duration ? frame.Timestamp + duration : frame.Timestamp;
    }

    /// <summary>Ends a black range that runs to the end of the video.</summary>
    /// <param name="endTime">When the video ends.</param>
    public void Complete(MediaTime endTime)
    {
        if (_complete)
        {
            return;
        }

        _complete = true;
        _ranges.Close(endTime > _lastEnd ? endTime : _lastEnd);
    }

    private double Ratio(YuvRaster raster)
    {
        VideoLevels levels = new(raster);
        int threshold = (int)(levels.Black + (_options.PixelThreshold * (levels.White - levels.Black)));
        return raster switch
        {
            YuvRaster<byte> bytes => (double)PlaneMath.CountAtMost(bytes.Y, threshold) / ((long)bytes.Y.Width * bytes.Y.Height),
            YuvRaster<ushort> words => (double)PlaneMath.CountAtMost(words.Y, threshold) / ((long)words.Y.Width * words.Y.Height),
            _ => throw new NotSupportedException($"Planes of {raster.GetType().Name} are not supported."),
        };
    }
}

/// <summary>What <see cref="BlackDetector"/> counts as black. The defaults are ffmpeg blackdetect's.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-video-detectors"/>
/// </example>
public sealed record BlackOptions
{
    /// <summary>Gets the default options.</summary>
    public static BlackOptions Default { get; } = new();

    /// <summary>
    /// Gets how far above black level, as a fraction of the range from black to white, a luma sample may be and still count as
    /// black. Default 0.10, as blackdetect's pixel threshold.
    /// </summary>
    public double PixelThreshold { get; init; } = 0.10;

    /// <summary>Gets the share of black samples, from 0 to 1, that makes a frame black. Default 0.98, as blackdetect's picture threshold.</summary>
    public double PictureRatio { get; init; } = 0.98;

    /// <summary>Gets the shortest black stretch reported. Default 2 s, as blackdetect.</summary>
    public MediaTime MinDuration { get; init; } = MediaTime.FromSeconds(2);

    /// <summary>Gets whether <see cref="BlackDetector.Series"/> keeps every frame's ratio. Off by default to keep memory flat.</summary>
    public bool KeepSeries { get; init; }

    internal void Validate()
    {
        if (PixelThreshold is < 0 or > 1 || double.IsNaN(PixelThreshold))
        {
            throw new ArgumentOutOfRangeException(nameof(PixelThreshold), PixelThreshold, "The pixel threshold must be from 0 to 1.");
        }

        if (PictureRatio is < 0 or > 1 || double.IsNaN(PictureRatio))
        {
            throw new ArgumentOutOfRangeException(nameof(PictureRatio), PictureRatio, "The picture ratio must be from 0 to 1.");
        }

        if (MinDuration.Ticks < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MinDuration), MinDuration, "The minimum duration cannot be negative.");
        }
    }
}
