namespace Egami.Video.Audio.Filters;

/// <summary>
/// Turns the level down above a threshold, by <see cref="Ratio"/>, like ffmpeg's <c>acompressor</c>: a level 12 dB over the
/// threshold comes out 3 dB over it at a ratio of 4. A soft <see cref="Knee"/> eases into the ratio, <see cref="Attack"/> and
/// <see cref="Release"/> set how fast the gain moves, and <see cref="Makeup"/> turns the result back up. Every channel gets
/// the same gain, from the loudest one.
/// </summary>
/// <remarks>
/// With <see cref="Sidechain"/> on, the level comes from another signal given to <see cref="SendSidechain"/> instead, like
/// ffmpeg's <c>sidechaincompress</c>: music compressed by a voice's level ducks under it. The sidechain is lined up with the
/// input by timestamps and must have the same sample rate; it may have other channels. Input waits until the sidechain
/// covers it or <see cref="CompleteSidechain"/> says no more comes; where there is no sidechain, the level counts as silence.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/AudioFilters.cs" region="audio-ducking"/>
/// </example>
public sealed class Compressor : AudioFilter
{
    private readonly Queue<AudioFrame> _pending = new();
    private DynamicsCore? _core;
    private float[] _detector = [];
    private float[] _gains = [];
    private float[] _side = [];
    private long _sideStart;
    private int _sideCount;
    private int _sideRate;
    private bool _sideStarted;
    private bool _sideComplete;

    /// <summary>Gets the threshold in dBFS above which the level is turned down; -18 by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside -120 to 0.</exception>
    public double Threshold { get; init => field = DynamicsCore.CheckDecibels(value, -120, 0, nameof(Threshold)); } = -18;

    /// <summary>Gets the ratio of level over the threshold in to level over it out, 1 to 100; 4 by default. 100 is close to a limiter.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 1 to 100.</exception>
    public double Ratio
    {
        get;
        init
        {
            if (!(value is >= 1 and <= 100))
            {
                throw new ArgumentOutOfRangeException(nameof(Ratio), value, "The ratio must be from 1 to 100.");
            }

            field = value;
        }
    } = 4;

    /// <summary>Gets the width in decibels of the soft knee around the threshold, 0 (hard) to 24; 6 by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 24.</exception>
    public double Knee { get; init => field = DynamicsCore.CheckDecibels(value, 0, 24, nameof(Knee)); } = 6;

    /// <summary>Gets how fast the gain comes down when the level rises, 0 to 10 s; 20 ms by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 10 seconds.</exception>
    public MediaTime Attack { get; init => field = DynamicsCore.CheckTime(value, nameof(Attack)); } = MediaTime.FromSeconds(0.020);

    /// <summary>Gets how fast the gain goes back up when the level falls, 0 to 10 s; 250 ms by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 10 seconds.</exception>
    public MediaTime Release { get; init => field = DynamicsCore.CheckTime(value, nameof(Release)); } = MediaTime.FromSeconds(0.250);

    /// <summary>Gets the gain in decibels after compression, 0 to 48; 0 by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 48.</exception>
    public double Makeup { get; init => field = DynamicsCore.CheckDecibels(value, 0, 48, nameof(Makeup)); }

    /// <summary>Gets how the level is measured; <see cref="LevelDetection.Rms"/> by default.</summary>
    public LevelDetection Detection { get; init; } = LevelDetection.Rms;

    /// <summary>Gets whether the level comes from <see cref="SendSidechain"/> rather than from the input itself.</summary>
    public bool Sidechain { get; init; }

    /// <summary>Gives the sidechain a frame, of any sample format; the caller keeps the frame.</summary>
    /// <param name="frame">The frame, at the input's sample rate.</param>
    /// <exception cref="InvalidOperationException"><see cref="Sidechain"/> is off, or <see cref="CompleteSidechain"/> was called.</exception>
    /// <exception cref="ArgumentException">The frame's sample rate differs from the input's or the sidechain's first frame.</exception>
    public void SendSidechain(AudioFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!Sidechain)
        {
            throw new InvalidOperationException("Set Sidechain to give the compressor a sidechain.");
        }

        if (_sideComplete)
        {
            throw new InvalidOperationException("The sidechain was completed; Reset starts it again.");
        }

        int rate = SampleRate != 0 ? SampleRate : _sideRate != 0 ? _sideRate : frame.SampleRate;
        if (frame.SampleRate != rate)
        {
            throw new ArgumentException($"The sidechain must run at the input's {rate} Hz; the frame has {frame.SampleRate} Hz.", nameof(frame));
        }

        _sideRate = rate;
        if (frame.SampleCount == 0)
        {
            return;
        }

        long first = frame.Timestamp.Rescale(new Rational(1, rate)).Ticks;
        if (!_sideStarted)
        {
            _sideStarted = true;
            _sideStart = first;
            _sideCount = 0;
        }

        long end = _sideStart + _sideCount;
        long last = first + frame.SampleCount;
        if (last > end)
        {
            int grow = checked((int)(last - end));
            if (_side.Length < _sideCount + grow)
            {
                Array.Resize(ref _side, Math.Max(_sideCount + grow, _side.Length * 2));
            }

            Span<float> added = _side.AsSpan(_sideCount, grow);
            added.Clear();
            using AudioFrame floats = frame.Format == SampleFormat.F32Planar ? frame.Slice(0, frame.SampleCount) : frame.Convert(SampleFormat.F32Planar);
            int skip = (int)Math.Max(0, end - first);
            int gap = (int)Math.Max(0, first - end);
            for (int c = 0; c < floats.ChannelCount; c++)
            {
                ReadOnlySpan<float> samples = floats.GetSpan<float>(c)[skip..];
                Span<float> into = added[gap..];
                for (int i = 0; i < samples.Length; i++)
                {
                    into[i] = Math.Max(into[i], Math.Abs(samples[i]));
                }
            }

            _sideCount += grow;
        }

        Pump(false);
    }

    /// <summary>Says no more sidechain comes, so input past its end is no longer held back.</summary>
    public void CompleteSidechain()
    {
        _sideComplete = true;
        Pump(false);
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DropPending();
        }

        base.Dispose(disposing);
    }

    private protected override void OnStart()
    {
        if (_sideRate != 0 && _sideRate != SampleRate)
        {
            throw new ArgumentException($"The input runs at {SampleRate} Hz; the sidechain has {_sideRate} Hz.", "frame");
        }

        _core = new DynamicsCore(false, Threshold, Ratio, Knee, 0, Makeup, Detection);
        _core.Start(SampleRate, Attack, Release);
    }

    private protected override void OnReset()
    {
        _core?.Reset();
        DropPending();
        _sideStarted = false;
        _sideComplete = false;
        _sideCount = 0;
        _sideRate = 0;
    }

    private protected override void Process(AudioFrame frame)
    {
        if (!Sidechain)
        {
            Grow(frame.SampleCount);
            Span<float> detector = _detector.AsSpan(0, frame.SampleCount);
            DynamicsCore.Peaks(frame, detector);
            Apply(frame, detector);
            return;
        }

        _pending.Enqueue(frame);
        Pump(false);
    }

    private protected override void OnFlush() => Pump(true);

    /// <summary>Compresses the waiting input the sidechain covers, or all of it at the end of the input.</summary>
    private void Pump(bool end)
    {
        while (_pending.TryPeek(out AudioFrame? frame))
        {
            long first = IndexOf(frame.Timestamp);
            int n = frame.SampleCount;
            long sideEnd = _sideStart + _sideCount;
            if (!end && !_sideComplete && (!_sideStarted || sideEnd < first + n))
            {
                return;
            }

            _pending.Dequeue();
            Grow(n);
            Span<float> detector = _detector.AsSpan(0, n);
            detector.Clear();
            if (_sideStarted)
            {
                long from = Math.Max(first, _sideStart), to = Math.Min(first + n, sideEnd);
                if (to > from)
                {
                    _side.AsSpan((int)(from - _sideStart), (int)(to - from)).CopyTo(detector[(int)(from - first)..]);
                }

                // The sidechain before the end of this frame is spent.
                int spent = (int)Math.Clamp(first + n - _sideStart, 0, _sideCount);
                _side.AsSpan(spent, _sideCount - spent).CopyTo(_side);
                _sideStart += spent;
                _sideCount -= spent;
            }

            Apply(frame, detector);
        }
    }

    private void Apply(AudioFrame frame, ReadOnlySpan<float> detector)
    {
        Span<float> gains = _gains.AsSpan(0, frame.SampleCount);
        _core!.Run(detector, gains);
        for (int c = 0; c < frame.ChannelCount; c++)
        {
            SampleMath.Multiply(frame.GetSpan<float>(c), gains);
        }

        Emit(frame);
    }

    private void Grow(int n)
    {
        if (_detector.Length < n)
        {
            _detector = new float[n];
            _gains = new float[n];
        }
    }

    private void DropPending()
    {
        while (_pending.TryDequeue(out AudioFrame? frame))
        {
            frame.Dispose();
        }
    }
}
