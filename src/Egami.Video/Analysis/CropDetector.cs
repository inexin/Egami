using System.Numerics;

using Egami.Pixels;

namespace Egami.Video.Analysis;

/// <summary>
/// Finds black borders: the rectangle inside letterbox, pillarbox or windowbox bars, as ffmpeg's cropdetect does in its black mode.
/// Rows and columns whose mean luma is at or under <see cref="CropOptions.Limit"/> count as border. Each frame's rectangle is
/// rounded to <see cref="CropOptions.Round"/>; <see cref="Report"/> gives the rectangle that keeps every frame's picture, the most
/// common one, and the runs of frames that share one.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-video-detectors"/>
/// </example>
public sealed class CropDetector : IVideoDetector
{
    private readonly CropOptions _options;
    private readonly List<CropRun> _runs = [];
    private readonly Dictionary<Rect, long> _frequency = [];
    private long[] _columns = [];
    private long _count;
    private long _measured;
    private Rect? _union;
    private Rect _current;
    private MediaTime _runStart;
    private long _runFrames;
    private int _width;
    private int _height;
    private MediaTime _lastEnd;
    private bool _complete;

    /// <summary>Creates a detector.</summary>
    /// <param name="options">The thresholds, or <see langword="null"/> for <see cref="CropOptions.Default"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-video-detectors"/>
    /// </example>
    public CropDetector(CropOptions? options = null)
    {
        _options = options ?? CropOptions.Default;
        _options.Validate();
    }

    /// <summary>Gets the options the detector runs with.</summary>
    public CropOptions Options => _options;

    /// <summary>Gets how many frames the detector has taken.</summary>
    public long FrameCount => _count;

    /// <summary>Gets the last measured frame's rectangle, or <see langword="null"/> before the first one (or when every frame so far was black).</summary>
    public Rect? LastRectangle { get; private set; }

    /// <summary>Returns what the detector found so far.</summary>
    /// <returns>The report.</returns>
    public CropReport Report()
    {
        List<CropRun> runs = [.. _runs];
        if (_runFrames > 0)
        {
            runs.Add(new CropRun(_runStart, _lastEnd, _current, _runFrames));
        }

        Rect full = new(0, 0, _width, _height);
        Rect common = _frequency.Count == 0 ? full : _frequency.MaxBy(pair => pair.Value).Key;
        return new CropReport(_union ?? full, common, new Size(_width, _height), _measured, runs);
    }

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

        long index = _count++;
        YuvRaster raster = frame.Raster;
        _width = raster.Width;
        _height = raster.Height;
        MediaTime end = frame.Duration is { } duration ? frame.Timestamp + duration : frame.Timestamp;
        if (index < _options.Skip)
        {
            _lastEnd = end;
            return;
        }

        Rect? found = raster switch
        {
            YuvRaster<byte> bytes => Measure(bytes.Y, raster),
            YuvRaster<ushort> words => Measure(words.Y, raster),
            _ => throw new NotSupportedException($"Planes of {raster.GetType().Name} are not supported."),
        };
        LastRectangle = found;
        if (found is { } rect)
        {
            _measured++;
            _union = _union is { } u ? Union(u, rect) : rect;
            _frequency[rect] = _frequency.GetValueOrDefault(rect) + 1;
            if (_runFrames == 0 || rect != _current)
            {
                if (_runFrames > 0)
                {
                    _runs.Add(new CropRun(_runStart, frame.Timestamp, _current, _runFrames));
                }

                _current = rect;
                _runStart = frame.Timestamp;
                _runFrames = 0;
            }

            _runFrames++;
        }

        _lastEnd = end;
    }

    /// <summary>Ends the last run.</summary>
    /// <param name="endTime">When the video ends.</param>
    public void Complete(MediaTime endTime)
    {
        if (_complete)
        {
            return;
        }

        _complete = true;
        if (endTime > _lastEnd)
        {
            _lastEnd = endTime;
        }
    }

    private static Rect Union(Rect a, Rect b)
    {
        int x0 = Math.Min(a.X, b.X), y0 = Math.Min(a.Y, b.Y);
        int x1 = Math.Max(a.X + a.Width, b.X + b.Width), y1 = Math.Max(a.Y + a.Height, b.Y + b.Height);
        return new Rect(x0, y0, x1 - x0, y1 - y0);
    }

    private Rect? Measure<T>(YuvPlane<T> plane, YuvRaster raster)
        where T : unmanaged, IBinaryInteger<T>
    {
        int width = plane.Width, height = plane.Height;
        double limit = _options.Limit * ((1 << raster.BitDepth) - 1);
        long rowLimit = (long)Math.Floor(limit * width);
        if (_columns.Length != width)
        {
            _columns = new long[width];
        }
        else
        {
            Array.Clear(_columns);
        }

        int top = -1, bottom = -1;
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<T> row = plane.Row(y)[..width];
            long sum = 0;
            for (int x = 0; x < width; x++)
            {
                int v = int.CreateTruncating(row[x]);
                sum += v;
                _columns[x] += v;
            }

            if (sum > rowLimit)
            {
                if (top < 0)
                {
                    top = y;
                }

                bottom = y;
            }
        }

        if (top < 0)
        {
            return null;
        }

        long columnLimit = (long)Math.Floor(limit * height);
        int left = 0, right = width - 1;
        while (left < width && _columns[left] <= columnLimit)
        {
            left++;
        }

        while (right > left && _columns[right] <= columnLimit)
        {
            right--;
        }

        if (left > right)
        {
            return null;
        }

        (int x0, int w) = Fit(left, right - left + 1, width, raster.ChromaFormat is ChromaFormat.Yuv420 or ChromaFormat.Yuv422);
        (int y0, int h) = Fit(top, bottom - top + 1, height, raster.ChromaFormat is ChromaFormat.Yuv420);
        return new Rect(x0, y0, w, h);
    }

    /// <summary>Rounds a span down to a multiple of the rounding, centered on what was found, on an even start when chroma is halved.</summary>
    private (int Start, int Length) Fit(int start, int length, int total, bool even)
    {
        int round = _options.Round;
        int rounded = length - (length % round);
        if (rounded == 0)
        {
            rounded = Math.Min(round, total);
        }

        int at = start + ((length - rounded) / 2);
        if (even && (at & 1) != 0)
        {
            at--;
        }

        at = Math.Clamp(at, 0, total - rounded);
        return (at, rounded);
    }
}

/// <summary>What <see cref="CropDetector"/> counts as a border. The defaults follow ffmpeg cropdetect.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-video-detectors"/>
/// </example>
public sealed record CropOptions
{
    /// <summary>Gets the default options.</summary>
    public static CropOptions Default { get; } = new();

    /// <summary>
    /// Gets the highest mean luma, as a fraction of the largest code value, of a row or column that counts as border. Default 24/255,
    /// as cropdetect's limit of 24.
    /// </summary>
    public double Limit { get; init; } = 24.0 / 255;

    /// <summary>Gets the multiple the width and height are rounded down to. Default 2, so sizes stay even for 4:2:0 encoders.</summary>
    public int Round { get; init; } = 2;

    /// <summary>Gets how many frames at the start are not measured, as cropdetect's skip. Default 2.</summary>
    public int Skip { get; init; } = 2;

    internal void Validate()
    {
        if (Limit is < 0 or > 1 || double.IsNaN(Limit))
        {
            throw new ArgumentOutOfRangeException(nameof(Limit), Limit, "The limit must be from 0 to 1.");
        }

        if (Round < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(Round), Round, "The rounding must be 1 or more.");
        }

        if (Skip < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Skip), Skip, "The frames to skip cannot be negative.");
        }
    }
}

/// <summary>What <see cref="CropDetector"/> found.</summary>
/// <param name="Crop">The smallest rectangle that holds every measured frame's picture: safe to crop the whole video to.</param>
/// <param name="MostCommon">The rectangle most frames had.</param>
/// <param name="FrameSize">The frames' size.</param>
/// <param name="Frames">How many frames were measured (frames that were black all over are not).</param>
/// <param name="Runs">The runs of consecutive measured frames that share a rectangle, in order.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-video-detectors"/>
/// </example>
public sealed record CropReport(Rect Crop, Rect MostCommon, Size FrameSize, long Frames, IReadOnlyList<CropRun> Runs)
{
    /// <summary>Gets whether <see cref="Crop"/> removes anything.</summary>
    public bool HasBorders => Crop.Width < FrameSize.Width || Crop.Height < FrameSize.Height;
}

/// <summary>A run of frames that share one crop rectangle.</summary>
/// <param name="Start">The run's first frame's timestamp.</param>
/// <param name="End">When the run ends: the next run's start, or the end of the video.</param>
/// <param name="Crop">The rectangle.</param>
/// <param name="Frames">How many frames the run holds.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-video-detectors"/>
/// </example>
public sealed record CropRun(MediaTime Start, MediaTime End, Rect Crop, long Frames);
