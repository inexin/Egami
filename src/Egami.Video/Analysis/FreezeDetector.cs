namespace Egami.Video.Analysis;

/// <summary>
/// Finds frozen stretches: runs of frames that stay within <see cref="FreezeOptions.Noise"/> of the frame that starts them, lasting
/// at least <see cref="FreezeOptions.MinDuration"/>. The measure is ffmpeg freezedetect's: the mean absolute difference over the Y,
/// Cb and Cr planes as a fraction of the largest code value, against the same -60 dB noise floor.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-video-detectors"/>
/// </example>
public sealed class FreezeDetector : IVideoDetector, IDisposable
{
    private readonly FreezeOptions _options;
    private readonly RangeBuilder _ranges;
    private YuvRaster? _reference;
    private YuvVideoFrame? _owned;
    private YuvVideoFrame? _offered;
    private MediaTime _referenceTime;
    private long _count;
    private MediaTime _lastEnd;
    private bool _complete;

    /// <summary>Creates a detector.</summary>
    /// <param name="options">The thresholds, or <see langword="null"/> for <see cref="FreezeOptions.Default"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-video-detectors"/>
    /// </example>
    public FreezeDetector(FreezeOptions? options = null)
    {
        _options = options ?? FreezeOptions.Default;
        _options.Validate();
        _ranges = new RangeBuilder(_options.MinDuration);
    }

    /// <summary>Gets the options the detector runs with.</summary>
    public FreezeOptions Options => _options;

    /// <summary>
    /// Gets the frozen ranges found so far. Each starts at the frame that froze and ends at the first frame that moved on; its value
    /// is the largest difference seen in it.
    /// </summary>
    public IReadOnlyList<DetectedRange> Ranges => _ranges.Ranges;

    /// <summary>
    /// Gets the last frame's difference from the frame the current run started with, from 0 to 1. Measuring stops once a frame has
    /// clearly moved on, so for such a frame this is a lower bound still over <see cref="FreezeOptions.Noise"/>.
    /// </summary>
    public double LastDifference { get; private set; }

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

        YuvRaster raster = frame.Raster;
        _count++;
        _lastEnd = frame.Duration is { } duration ? frame.Timestamp + duration : frame.Timestamp;
        if (_reference is null || !SameShape(_reference, raster))
        {
            _ranges.Close(frame.Timestamp);
            Keep(raster, frame.Timestamp);
            LastDifference = 0;
            return;
        }

        double difference = Difference(_reference, raster, _options.Noise);
        LastDifference = difference;
        if (difference <= _options.Noise)
        {
            if (!_ranges.Open)
            {
                _ranges.Extend(_referenceTime, difference, Math.Max);
            }
            else
            {
                _ranges.Extend(frame.Timestamp, difference, Math.Max);
            }
        }
        else
        {
            _ranges.Close(frame.Timestamp);
            Keep(raster, frame.Timestamp);
        }
    }

    /// <summary>Ends a frozen range that runs to the end of the video.</summary>
    /// <param name="endTime">When the video ends.</param>
    public void Complete(MediaTime endTime)
    {
        if (_complete)
        {
            return;
        }

        _complete = true;
        _ranges.Close(endTime > _lastEnd ? endTime : _lastEnd);
        Dispose();
    }

    /// <summary>Releases the copy of the frame the current run is compared with.</summary>
    public void Dispose()
    {
        ReleaseReference();
    }

    /// <summary>
    /// Takes the next frame and, when it becomes the frame later ones are compared with, keeps the frame itself instead of a
    /// copy. Returns whether it kept it; the detector then owns the frame and disposes it.
    /// </summary>
    internal bool AddAndKeep(YuvVideoFrame frame)
    {
        _offered = frame;
        try
        {
            Add(frame);
        }
        finally
        {
            _offered = null;
        }

        return ReferenceEquals(_owned, frame);
    }

    private void ReleaseReference()
    {
        if (_owned is not null)
        {
            _owned.Dispose();
            _owned = null;
        }
        else
        {
            _reference?.Dispose();
        }

        _reference = null;
    }

    private static bool SameShape(YuvRaster a, YuvRaster b)
        => a.Width == b.Width && a.Height == b.Height && a.ChromaFormat == b.ChromaFormat && a.BitDepth == b.BitDepth && a.GetType() == b.GetType();

    private static double Difference(YuvRaster a, YuvRaster b, double noise)
    {
        double max = (1 << a.BitDepth) - 1;
        return (a, b) switch
        {
            (YuvRaster<byte> x, YuvRaster<byte> y) => Mean(x.Y, y.Y, x.U, y.U, x.V, y.V, max, noise),
            (YuvRaster<ushort> x, YuvRaster<ushort> y) => Mean(x.Y, y.Y, x.U, y.U, x.V, y.V, max, noise),
            _ => throw new NotSupportedException($"Planes of {a.GetType().Name} are not supported."),
        };
    }

    /// <summary>
    /// The mean absolute difference over the planes as a fraction of <paramref name="max"/>. The planes' shares only add up, so
    /// once the part measured passes <paramref name="noise"/> the frame has moved on and the rest is not read.
    /// </summary>
    private static double Mean<T>(YuvPlane<T> y1, YuvPlane<T> y2, YuvPlane<T>? u1, YuvPlane<T>? u2, YuvPlane<T>? v1, YuvPlane<T>? v2, double max, double noise)
        where T : unmanaged, System.Numerics.IBinaryInteger<T>
    {
        int planes = u1 is not null && u2 is not null && v1 is not null && v2 is not null ? 3 : 1;
        double limit = noise * planes * max;
        double total = Share(y1, y2, 0, limit);
        if (planes == 3 && total <= limit)
        {
            total = Share(u1!, u2!, total, limit);
            if (total <= limit)
            {
                total = Share(v1!, v2!, total, limit);
            }
        }

        return total / planes / max;
    }

    /// <summary>Adds a plane's mean absolute difference to <paramref name="total"/>, stopping past <paramref name="limit"/>.</summary>
    private static double Share<T>(YuvPlane<T> a, YuvPlane<T> b, double total, double limit)
        where T : unmanaged, System.Numerics.IBinaryInteger<T>
    {
        double scale = 1 / ((double)a.Width * a.Height);
        long sum = 0;
        for (int y = 0; y < a.Height; y++)
        {
            sum += PlaneMath.AbsoluteDifference<T>(a.Row(y)[..a.Width], b.Row(y)[..a.Width]);
            if ((y & 15) == 15 && total + (sum * scale) > limit)
            {
                break;
            }
        }

        return total + (sum * scale);
    }

    private void Keep(YuvRaster raster, MediaTime time)
    {
        if (_offered is { } offered)
        {
            // MediaAnalysis hands over the frame itself, so the reference needs no copy.
            ReleaseReference();
            _owned = offered;
            _reference = offered.Raster;
            _referenceTime = time;
            return;
        }

        if (_owned is not null)
        {
            ReleaseReference();
        }

        if (_reference is null || !SameShape(_reference, raster))
        {
            _reference?.Dispose();
            _reference = raster.BitDepth == 8
                ? YuvRaster.Create(raster.Width, raster.Height, raster.ChromaFormat, raster.Color)
                : YuvRaster.Create(raster.Width, raster.Height, raster.ChromaFormat, raster.BitDepth, raster.Color);
        }

        YuvPainter.Copy(raster, _reference);
        _referenceTime = time;
    }
}

/// <summary>What <see cref="FreezeDetector"/> counts as frozen. The defaults are ffmpeg freezedetect's.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-video-detectors"/>
/// </example>
public sealed record FreezeOptions
{
    /// <summary>Gets the default options.</summary>
    public static FreezeOptions Default { get; } = new();

    /// <summary>
    /// Gets the largest mean difference, as a fraction of the largest code value, that still counts as the same picture. Default
    /// 0.001 (-60 dB), as freezedetect's noise.
    /// </summary>
    public double Noise { get; init; } = 0.001;

    /// <summary>Gets the shortest frozen stretch reported. Default 2 s, as freezedetect.</summary>
    public MediaTime MinDuration { get; init; } = MediaTime.FromSeconds(2);

    internal void Validate()
    {
        if (Noise is < 0 or > 1 || double.IsNaN(Noise))
        {
            throw new ArgumentOutOfRangeException(nameof(Noise), Noise, "The noise floor must be from 0 to 1.");
        }

        if (MinDuration.Ticks < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MinDuration), MinDuration, "The minimum duration cannot be negative.");
        }
    }
}
