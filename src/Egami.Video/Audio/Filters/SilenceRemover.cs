namespace Egami.Video.Audio.Filters;

/// <summary>What a <see cref="SilenceRemover"/> takes out.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/AudioFilters.cs" region="audio-silence"/>
/// </example>
public enum SilenceRemoval
{
    /// <summary>The silence before the first sound and after the last, the default.</summary>
    Trim,

    /// <summary>That, and every pause longer than <see cref="SilenceRemover.MaxGap"/> cut to <see cref="SilenceRemover.KeepGap"/>.</summary>
    Shorten,
}

/// <summary>
/// Cuts silence out of audio, like ffmpeg's <c>silenceremove</c>: a sample is silent when the loudest channel's RMS over the
/// 20 ms around it is under <see cref="Threshold"/>. <see cref="SilenceRemoval.Trim"/> cuts the silence at the start and the
/// end; <see cref="SilenceRemoval.Shorten"/> also cuts long pauses down, as for a voice recording. Each cut fades or crossfades
/// over 10 ms, so it does not click. The output runs on from the input's first timestamp without gaps, so it is shorter than
/// the input: use it on audio alone, never on a track that has to stay in sync with video.
/// </summary>
/// <remarks>
/// A silent stretch is held until sound comes back (or the input ends), since only then is it known to be a pause rather than
/// the end, so a long silence takes memory for its length. <see cref="AudioFilter.Latency"/> gives the 10 ms of lookahead
/// it always holds.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/AudioFilters.cs" region="audio-silence"/>
/// </example>
public sealed class SilenceRemover : AudioFilter
{
    private SampleBuffer? _held;
    private long _heldStart; // the stream index of the first held sample

    // The RMS detector: squares of the loudest channel over the last 2 * _half samples.
    private float[] _squares = [];
    private float[] _loudest = [];
    private int _squarePosition;
    private double _squareSum;
    private int _half;
    private int _fade;
    private int _keep;
    private long _maxGap;
    private double _threshold;

    private long _received;
    private long _pushed;
    private long _classified;
    private long _approved; // samples before this stream index are cleared to go out
    private long _origin;
    private long _out;
    private bool _started;
    private bool _heard;
    private bool _inRun;
    private long _runStart;
    private int _fadeIn; // samples at the front of the approved region still to fade in

    /// <summary>Creates the filter.</summary>
    /// <param name="mode">What to take out.</param>
    /// <exception cref="ArgumentOutOfRangeException">The mode is not defined.</exception>
    public SilenceRemover(SilenceRemoval mode = SilenceRemoval.Trim)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown silence removal mode.");
        }

        Mode = mode;
    }

    /// <summary>Gets what the filter takes out.</summary>
    public SilenceRemoval Mode { get; }

    /// <summary>Gets the RMS level in dBFS under which audio counts as silent, -120 to 0; -50 by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside -120 to 0.</exception>
    public double Threshold { get; init => field = DynamicsCore.CheckDecibels(value, -120, 0, nameof(Threshold)); } = -50;

    /// <summary>Gets the longest pause <see cref="SilenceRemoval.Shorten"/> leaves alone, 0.1 to 60 s; 1 s by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0.1 to 60 seconds.</exception>
    public MediaTime MaxGap { get; init => field = CheckGap(value, nameof(MaxGap)); } = MediaTime.FromSeconds(1);

    /// <summary>Gets the length <see cref="SilenceRemoval.Shorten"/> cuts a longer pause to, 0.1 s up to <see cref="MaxGap"/>; 0.3 s by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0.1 to 60 seconds.</exception>
    public MediaTime KeepGap { get; init => field = CheckGap(value, nameof(KeepGap)); } = MediaTime.FromSeconds(0.3);

    private protected override long LatencySamples => _half;

    private protected override void OnStart()
    {
        if (KeepGap > MaxGap)
        {
            throw new ArgumentException($"KeepGap ({KeepGap}) must not be longer than MaxGap ({MaxGap}).", "frame");
        }

        _half = Math.Max(1, (int)Math.Round(SampleRate * 0.010));
        _fade = _half;
        _keep = (int)IndexOf(KeepGap);
        _maxGap = IndexOf(MaxGap);
        _threshold = Math.Pow(10, Threshold / 10); // mean square
        _squares = new float[2 * _half];
        _held = new SampleBuffer(Layout.ChannelCount, SampleRate);
        OnReset();
    }

    private protected override void OnReset()
    {
        _held?.Clear();
        Array.Clear(_squares);
        _squarePosition = 0;
        _squareSum = 0;
        _heldStart = 0;
        _received = 0;
        _pushed = 0;
        _classified = 0;
        _approved = 0;
        _out = 0;
        _started = false;
        _heard = false;
        _inRun = false;
        _fadeIn = 0;
    }

    private protected override void Process(AudioFrame frame)
    {
        if (!_started)
        {
            _started = true;
            _origin = IndexOf(frame.Timestamp);
        }

        _held!.Append(frame);
        int n = frame.SampleCount;
        if (_loudest.Length < n)
        {
            _loudest = new float[n];
        }

        Span<float> loudest = _loudest.AsSpan(0, n);
        DynamicsCore.Peaks(frame, loudest);
        foreach (float peak in loudest)
        {
            Push(peak * peak);
        }

        frame.Dispose();
        Release();
    }

    private protected override void OnFlush()
    {
        if (!_started)
        {
            return;
        }

        // Classify the last samples with silence after the end.
        while (_classified < _received)
        {
            Push(0, real: false);
        }

        if (_inRun)
        {
            if (_heard)
            {
                // Trailing silence: keep a fade out's worth, drop the rest.
                ApproveTo(_runStart);
                Release();
                int tail = (int)Math.Min(_fade, _received - _runStart);
                FadeOut(tail);
                _approved = _runStart + tail;
                Release();
            }
        }
        else
        {
            _approved = _received;
            Release();
        }

        OnReset();
    }

    private static MediaTime CheckGap(MediaTime value, string name)
    {
        if (value < MediaTime.FromSeconds(0.1) || value > MediaTime.FromSeconds(60))
        {
            throw new ArgumentOutOfRangeException(name, value, "The time must be from 0.1 to 60 seconds.");
        }

        return value;
    }

    /// <summary>Adds the next sample's square to the detector; that classifies the sample _half before it.</summary>
    private void Push(float square, bool real = true)
    {
        if (real)
        {
            _received++;
        }

        _pushed++;
        _squareSum += square - _squares[_squarePosition];
        _squares[_squarePosition] = square;
        _squarePosition = _squarePosition + 1 == _squares.Length ? 0 : _squarePosition + 1;
        long index = _pushed - 1 - _half;
        if (index < 0 || index >= _received)
        {
            return;
        }

        Classify(index, Math.Max(_squareSum, 0) / _squares.Length < _threshold);
        _classified = index + 1;
    }

    private void Classify(long index, bool silent)
    {
        if (silent)
        {
            if (!_inRun)
            {
                _inRun = true;
                _runStart = index;
            }

            return;
        }

        if (_inRun)
        {
            _inRun = false;
            long length = index - _runStart;
            if (!_heard)
            {
                // Leading silence: start a fade in's worth before the sound.
                long start = Math.Max(_runStart, index - _fade);
                Skip(start);
                _fadeIn = (int)(index - start);
            }
            else if (Mode == SilenceRemoval.Shorten && length > _maxGap)
            {
                Shorten(_runStart, index);
            }
        }

        _heard = true;
        _approved = index + 1;
    }

    /// <summary>Keeps the gap's head and tail, crossfaded over 10 ms, so the pause lasts <see cref="KeepGap"/>.</summary>
    private void Shorten(long start, long end)
    {
        int side = (_keep - _fade) / 2;
        ApproveTo(start + side);
        Release();

        // Mix the tail's crossfade part into the head's, then drop everything between.
        SampleBuffer held = _held!;
        int head = (int)(start + side - _heldStart), tail = (int)(end - side - _fade - _heldStart);
        for (int c = 0; c < held.ChannelCount; c++)
        {
            Span<float> samples = held[c];
            for (int k = 0; k < _fade; k++)
            {
                float t = (k + 0.5f) / _fade;
                samples[head + k] = (samples[head + k] * (1 - t)) + (samples[tail + k] * t);
            }
        }

        _approved = start + side + _fade;
        Release();
        Skip(end - side);
    }

    private void ApproveTo(long index) => _approved = Math.Max(_approved, index);

    /// <summary>Drops held samples up to a stream index.</summary>
    private void Skip(long index)
    {
        int count = (int)Math.Clamp(index - _heldStart, 0, _held!.Count);
        _held.Drop(count);
        _heldStart += count;
        _approved = Math.Max(_approved, _heldStart);
    }

    private void FadeOut(int count)
    {
        for (int c = 0; c < _held!.ChannelCount; c++)
        {
            Span<float> samples = _held[c];
            for (int k = 0; k < count && k < samples.Length; k++)
            {
                samples[k] *= 1 - ((k + 0.5f) / count);
            }
        }
    }

    /// <summary>Hands out the approved samples.</summary>
    private void Release()
    {
        int count = (int)Math.Clamp(_approved - _heldStart, 0, _held!.Count);
        if (count == 0)
        {
            return;
        }

        if (_fadeIn > 0)
        {
            int fade = _fadeIn;
            for (int c = 0; c < _held.ChannelCount; c++)
            {
                Span<float> samples = _held[c];
                for (int k = 0; k < Math.Min(fade, count); k++)
                {
                    samples[k] *= (k + 0.5f) / fade;
                }
            }

            // The fade in ends before the first sound, which is approved with it, so it always goes out whole.
            _fadeIn = 0;
        }

        AudioFrame frame = NewFrame(Layout, count, _origin + _out);
        _held.CopyTo(frame);
        _held.Drop(count);
        _heldStart += count;
        _out += count;
        Emit(frame);
    }
}
