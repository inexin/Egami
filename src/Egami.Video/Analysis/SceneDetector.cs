namespace Egami.Video.Analysis;

/// <summary>
/// Finds the scenes of a video: where one shot cuts to the next, and where a fade or cut through black separates two. Each frame
/// is summarized from its planes (a 16 by 9 grid of mean Y, Cb and Cr and a luma histogram) and scored against the frame before
/// by <see cref="SceneOptions.Method"/>; a high enough score starts a scene, unless it comes within the minimum scene length or
/// turns out to be a flash. Feed it every frame with <see cref="Add"/> and call <see cref="Complete"/> at the end, or let
/// <see cref="MediaAnalysis.Run"/> do it. Memory stays flat over any length of video unless <see cref="SceneOptions.KeepSeries"/>
/// is set.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-detectors"/>
/// </example>
public sealed class SceneDetector : IVideoDetector
{
    /// <summary>How many earlier differences the <see cref="SceneMethod.Content"/> method's mean takes.</summary>
    private const int ContentWindow = 8;

    /// <summary>The scale from a content difference to a score: a change of a quarter of the range scores 1.</summary>
    private const double Scale = 4;

    /// <summary>How much the mean luma must move from one frame to the next to count as falling or rising in a fade.</summary>
    private const double FadeStep = 0.002;

    private readonly SceneOptions _options;
    private readonly double _threshold;
    private readonly double _minLength;
    private readonly int _window;
    private readonly FrameSignature[] _signatures;
    private readonly FrameSignature _flashBefore = new();
    private readonly Entry[] _entries;
    private readonly double[] _recent = new double[ContentWindow];
    private readonly List<SceneCut> _cuts = [];
    private readonly List<Fade> _fades = [];
    private readonly List<SceneScore> _scores = [];
    private readonly List<MediaTime> _darkTimes = [];
    private int _recentCount;
    private long _count;
    private MediaTime _lastTime;
    private MediaTime? _end;
    private double _lastCut;
    private SceneCut? _pending;
    private SceneCut? _newest;
    private long _flashEnd = -1;
    private double _previousLuma;
    private bool _previousDark;
    private long _darkStart = -1;
    private bool _falling;
    private long _fallStart;
    private MediaTime _fallStartTime;
    private bool _rising;
    private long _riseStart;
    private MediaTime _riseStartTime;

    /// <summary>Creates a detector.</summary>
    /// <param name="options">How to find scenes, or <see langword="null"/> for <see cref="SceneOptions.Default"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-detectors"/>
    /// </example>
    public SceneDetector(SceneOptions? options = null)
    {
        _options = options ?? SceneOptions.Default;
        _options.Validate();
        _threshold = _options.EffectiveThreshold;
        _minLength = _options.MinSceneLength.Seconds;
        _window = _options.Method == SceneMethod.Adaptive ? _options.Window : 0;
        _signatures = new FrameSignature[_window + 2];
        for (int i = 0; i < _signatures.Length; i++)
        {
            _signatures[i] = new FrameSignature();
        }

        _entries = new Entry[(2 * _window) + 1];
    }

    /// <summary>Gets the options the detector runs with.</summary>
    public SceneOptions Options => _options;

    /// <summary>Gets the cuts found so far, the first at the first frame. A cut is listed once it is settled.</summary>
    public IReadOnlyList<SceneCut> Cuts => _cuts;

    /// <summary>
    /// Gets the scenes found so far: one per cut, each running to the next cut. The last scene runs to the last frame seen, or to
    /// the end given to <see cref="Complete"/>.
    /// </summary>
    public IReadOnlyList<Scene> Scenes
    {
        get
        {
            Scene[] scenes = new Scene[_cuts.Count];
            for (int i = 0; i < _cuts.Count; i++)
            {
                SceneCut cut = _cuts[i];
                SceneCut? next = i + 1 < _cuts.Count ? _cuts[i + 1] : null;
                long endFrame = next is null ? _count - 1 : next.Frame - 1;
                MediaTime end = next?.Time ?? _end ?? _lastTime;
                scenes[i] = new Scene(i + 1, cut.Frame, endFrame, cut.Time, end, cut.Score, cut.Kind);
            }

            return scenes;
        }
    }

    /// <summary>Gets the fades found so far (with <see cref="SceneOptions.DetectFades"/>).</summary>
    public IReadOnlyList<Fade> Fades => _fades;

    /// <summary>Gets every frame's score and luma, when <see cref="SceneOptions.KeepSeries"/> is set; otherwise empty.</summary>
    public IReadOnlyList<SceneScore> Series => _scores;

    /// <summary>Gets how many frames the detector has taken.</summary>
    public long FrameCount => _count;

    /// <summary>
    /// Gets how many frames are settled: no cut can still be added before this frame. A caller that keeps recent frames, to save
    /// the first frame of each scene for instance, may let go of the frames before it. The adaptive window, a cut held for the
    /// flash test and a dark run that may end in a fade keep it behind <see cref="FrameCount"/>.
    /// </summary>
    public long SettledFrames
    {
        get
        {
            if (_end is not null)
            {
                return _count;
            }

            long settled = Math.Max(0, _count - _window);
            if (_pending is { } pending)
            {
                settled = Math.Min(settled, pending.Frame);
            }

            if (_darkStart >= 0)
            {
                settled = Math.Min(settled, _darkStart);
            }

            return settled;
        }
    }

    /// <summary>Takes the next frame.</summary>
    /// <param name="frame">The frame, in presentation order.</param>
    /// <returns>The newest cut this frame settled, or <see langword="null"/>; <see cref="Cuts"/> lists every one.</returns>
    /// <exception cref="InvalidOperationException"><see cref="Complete"/> was called.</exception>
    public SceneCut? Add(YuvVideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (_end is not null)
        {
            throw new InvalidOperationException("The detector is complete; create a new one for more frames.");
        }

        _newest = null;
        long index = _count++;
        MediaTime time = frame.Timestamp;
        FrameSignature current = Signature(index);
        current.Measure(frame.Raster, histogram: _options.Method == SceneMethod.Histogram);
        double luma = current.Luma;
        bool dark = IsDark(luma);
        if (index == 0)
        {
            _lastTime = time;
            _lastCut = time.Seconds;
            _previousLuma = luma;
            _previousDark = dark;
            if (dark && _options.DetectFades)
            {
                // A video that opens in black has its first scene at the first frame; the dark run ends no scene.
                _darkStart = 0;
            }

            if (_options.KeepSeries)
            {
                _scores.Add(new SceneScore(0, time, 0, luma));
            }

            Settle(new SceneCut(0, time, 0, SceneStart.First));
            return _newest;
        }

        FrameSignature previous = Signature(index - 1);
        double difference, score;
        switch (_options.Method)
        {
            case SceneMethod.Histogram:
                difference = current.HistogramDistance(previous);
                score = difference;
                break;
            case SceneMethod.Content:
                difference = current.ContentDifference(previous);
                score = Math.Clamp((difference - RecentMean()) * Scale, 0, 1);
                Remember(difference);
                break;
            default:
                difference = current.ContentDifference(previous);
                score = Math.Min(1, difference * Scale);
                break;
        }

        if (_options.KeepSeries)
        {
            _scores.Add(new SceneScore(index, time, score, luma));
        }

        if (_pending is not null)
        {
            if (_options.Flash == FlashMode.Merge && Similar(current, _flashBefore))
            {
                // The picture came back to what it was before the cut: a flash, not a new scene, and the cut back is no scene either.
                _pending = null;
                _flashEnd = index;
            }
            else if (time.Seconds - _pending.Time.Seconds >= _minLength - 1e-9)
            {
                Settle(_pending);
                _pending = null;
            }
        }

        SceneCut? fadeCut = _options.DetectFades ? TrackFades(index, time, luma, dark) : null;
        Entry entry = new(index, time, difference, score, dark, _previousDark);
        SceneCut? cut = null;
        if (_window > 0)
        {
            _entries[index % _entries.Length] = entry;
            if (index - _window >= 1)
            {
                cut = Adaptive(index - _window, index);
            }
        }
        else if (score >= _threshold && !EntersOrLeavesDark(entry))
        {
            cut = new SceneCut(index, time, score, SceneStart.Cut);
        }

        if (fadeCut is not null && cut is not null && cut.Frame > fadeCut.Frame)
        {
            Submit(fadeCut, index);
            Submit(cut, index);
        }
        else
        {
            if (cut is not null)
            {
                Submit(cut, index);
            }

            if (fadeCut is not null)
            {
                Submit(fadeCut, index);
            }
        }

        _previousLuma = luma;
        _previousDark = dark;
        _lastTime = time;
        return _newest;
    }

    /// <summary>Settles what the detector was still weighing: the last frames of the adaptive window, a held cut and an open fade.</summary>
    /// <param name="endTime">When the video ends; the last scene runs to it.</param>
    public void Complete(MediaTime endTime)
    {
        if (_end is not null)
        {
            return;
        }

        if (_window > 0)
        {
            for (long j = Math.Max(1, _count - _window); j < _count; j++)
            {
                if (Adaptive(j, _count - 1) is { } cut)
                {
                    Submit(cut, _count - 1);
                }
            }
        }

        if (_pending is not null)
        {
            Settle(_pending);
            _pending = null;
        }

        if (_rising && _count - 1 - _riseStart >= 2)
        {
            _fades.Add(new Fade(FadeKind.In, _riseStart, _count - 1, _riseStartTime, _lastTime));
        }

        _rising = false;
        _darkStart = -1;
        _end = endTime > _lastTime ? endTime : _lastTime;
    }

    void IVideoDetector.Add(YuvVideoFrame frame) => Add(frame);

    private FrameSignature Signature(long index) => _signatures[index % _signatures.Length];

    private bool IsDark(double luma) => luma <= _options.FadeLevel || (_options.FadeToWhite && luma >= 1 - _options.FadeLevel);

    private bool EntersOrLeavesDark(Entry entry) => _options.DetectFades && (entry.Dark || entry.PreviousDark);

    private bool Similar(FrameSignature a, FrameSignature b) => _options.Method == SceneMethod.Histogram
        ? a.HistogramDistance(b) < _threshold
        : Math.Min(1, a.ContentDifference(b) * Scale) < _threshold;

    /// <summary>Weighs frame <paramref name="j"/> against the frames around it; <paramref name="last"/> is the newest frame seen.</summary>
    private SceneCut? Adaptive(long j, long last)
    {
        Entry entry = _entries[j % _entries.Length];
        if (entry.Score < _threshold || EntersOrLeavesDark(entry))
        {
            return null;
        }

        double sum = 0;
        int count = 0;
        for (long k = j - _window; k <= Math.Min(j + _window, last); k++)
        {
            if (k >= 1 && k != j)
            {
                sum += _entries[k % _entries.Length].Difference;
                count++;
            }
        }

        double mean = count > 0 ? sum / count : 0;
        return entry.Difference >= _options.AdaptiveRatio * mean ? new SceneCut(j, entry.Time, entry.Score, SceneStart.Cut) : null;
    }

    /// <summary>Follows the mean luma through falls, dark runs and rises; returns the cut a dark run's end makes.</summary>
    private SceneCut? TrackFades(long index, MediaTime time, double luma, bool dark)
    {
        SceneCut? cut = null;
        bool falling = luma < _previousLuma - FadeStep;
        if (falling && !_falling)
        {
            _fallStart = index;
            _fallStartTime = time;
        }

        _falling = falling;
        if (_rising && !(luma > _previousLuma + FadeStep))
        {
            if (index - 1 - _riseStart >= 2)
            {
                _fades.Add(new Fade(FadeKind.In, _riseStart, index - 1, _riseStartTime, _lastTime));
            }

            _rising = false;
        }

        if (dark && !_previousDark)
        {
            _darkStart = index;
            _darkTimes.Clear();
            _darkTimes.Add(time);
            if (_falling && index - _fallStart >= 1)
            {
                _fades.Add(new Fade(FadeKind.Out, _fallStart, index, _fallStartTime, time));
            }
        }
        else if (dark)
        {
            if (_darkStart >= 0)
            {
                _darkTimes.Add(time);
            }
        }
        else if (_previousDark)
        {
            if (_darkStart > 0)
            {
                long length = index - _darkStart;
                long at = _darkStart + (long)Math.Round(length * (1 + _options.FadeBias) / 2, MidpointRounding.AwayFromZero);
                at = Math.Clamp(at, _darkStart, index);
                MediaTime atTime = at < index ? _darkTimes[(int)(at - _darkStart)] : time;
                cut = new SceneCut(at, atTime, 1, SceneStart.Fade);
            }

            _darkStart = -1;
            _rising = true;
            _riseStart = index - 1;
            _riseStartTime = _lastTime;
        }

        return cut;
    }

    /// <summary>Puts a found cut through the minimum length and, for <see cref="FlashMode.Merge"/>, holds it for the flash test.</summary>
    private void Submit(SceneCut cut, long newest)
    {
        if (_pending is { } pending)
        {
            if (cut.Time.Seconds - pending.Time.Seconds < _minLength - 1e-9)
            {
                return;
            }

            Settle(pending);
            _pending = null;
        }

        if (cut.Time.Seconds - _lastCut < _minLength - 1e-9 || cut.Frame <= _cuts[^1].Frame || cut.Frame <= _flashEnd)
        {
            return;
        }

        if (_options.Flash == FlashMode.Merge && cut.Kind == SceneStart.Cut && _minLength > 0)
        {
            // The frames after the cut that are already in hand get the flash test now; later frames get it in Add.
            _flashBefore.CopyFrom(Signature(cut.Frame - 1));
            for (long k = cut.Frame + 1; k <= newest; k++)
            {
                if (Similar(Signature(k), _flashBefore))
                {
                    _flashEnd = k;
                    return;
                }
            }

            _pending = cut;
            return;
        }

        Settle(cut);
    }

    private void Settle(SceneCut cut)
    {
        _cuts.Add(cut);
        _lastCut = cut.Time.Seconds;
        _newest = cut;
    }

    private double RecentMean()
    {
        if (_recentCount == 0)
        {
            return 0;
        }

        double sum = 0;
        int n = Math.Min(_recentCount, ContentWindow);
        for (int i = 0; i < n; i++)
        {
            sum += _recent[i];
        }

        return sum / n;
    }

    private void Remember(double difference) => _recent[_recentCount++ % ContentWindow] = difference;

    private readonly record struct Entry(long Frame, MediaTime Time, double Difference, double Score, bool Dark, bool PreviousDark);
}
