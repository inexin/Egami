namespace Egami.Video.Audio.Filters;

/// <summary>
/// Keeps the level under a <see cref="Ceiling"/>, like ffmpeg's <c>alimiter</c>. It looks 5 ms ahead, so the gain is already
/// down when a peak arrives and no sample goes over; the gain then comes back up over <see cref="Release"/>. With
/// <see cref="TruePeak"/> on, it also limits the peaks between samples, found by 4x oversampling as ITU-R BS.1770 measures
/// them, so a decoder or a sample rate converter downstream stays under the ceiling too. The output is held back by
/// <see cref="AudioFilter.Latency"/> and keeps the input's timestamps; <see cref="AudioFilter.Flush"/> hands out the rest.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/AudioFilters.cs" region="audio-dynamics"/>
/// </example>
public sealed class Limiter : AudioFilter
{
    private const int Chunk = 4096;

    private int _lookahead;
    private int _peakDelay;
    private int _delay;
    private double _release;
    private float _ceiling;
    private float[][] _lines = [];
    private int _linePosition;
    private float[] _gains = [];
    private float[] _required = [];
    private TruePeakEstimator[] _truePeaks = [];
    private float _lastTruePeak;

    // The sample peaks, held 6 samples so they line up with the true peaks.
    private float[] _peakLine = [];
    private int _peakPosition;

    // The sliding minimum of the required gain over lookahead + 1 samples, as a monotonic queue of (index, gain).
    private (long Index, float Gain)[] _minimum = [];
    private int _minimumHead, _minimumCount;

    // The box average of the released gain over lookahead + 1 samples.
    private float[] _box = [];
    private int _boxPosition;
    private double _boxSum;
    private double _held;

    private long _step;
    private long _origin;
    private long _received;
    private long _sent;
    private bool _started;

    /// <summary>Gets the most the output may reach in dBFS, -60 to 0; -1 by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside -60 to 0.</exception>
    public double Ceiling { get; init => field = DynamicsCore.CheckDecibels(value, -60, 0, nameof(Ceiling)); } = -1;

    /// <summary>Gets how fast the gain goes back up after a peak, 1 ms to 10 s; 50 ms by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 1 ms to 10 seconds.</exception>
    public MediaTime Release
    {
        get;
        init
        {
            if (value < MediaTime.FromSeconds(0.001))
            {
                throw new ArgumentOutOfRangeException(nameof(Release), value, "The release must be from 1 ms to 10 seconds.");
            }

            field = DynamicsCore.CheckTime(value, nameof(Release));
        }
    } = MediaTime.FromSeconds(0.050);

    /// <summary>Gets whether peaks between samples count too, found by oversampling 4x (2x from 96 kHz; off from 192 kHz).</summary>
    public bool TruePeak { get; init; }

    private protected override long LatencySamples => _delay;

    private protected override void OnStart()
    {
        _lookahead = Math.Max(1, (int)Math.Round(SampleRate * 0.005));
        int factor = TruePeak ? (SampleRate < 96000 ? 4 : SampleRate < 192000 ? 2 : 1) : 1;
        _peakDelay = factor > 1 ? TruePeakEstimator.Delay : 0;
        _delay = _lookahead + _peakDelay;
        _ceiling = (float)SampleMath.FromDecibels(Ceiling);
        _release = 1 - Math.Exp(-1 / (Release.Seconds * SampleRate));
        int channels = Layout.ChannelCount;
        _lines = new float[channels][];
        for (int c = 0; c < channels; c++)
        {
            _lines[c] = new float[_delay];
        }

        if (factor > 1)
        {
            float[][] phases = LoudnessMeter.PeakEstimator.Design(factor);
            _truePeaks = new TruePeakEstimator[channels];
            for (int c = 0; c < channels; c++)
            {
                _truePeaks[c] = new TruePeakEstimator(phases);
            }
        }

        _minimum = new (long, float)[_lookahead + 1];
        _box = new float[_lookahead + 1];
        OnReset();
    }

    private protected override void OnReset()
    {
        foreach (float[] line in _lines)
        {
            Array.Clear(line);
        }

        foreach (TruePeakEstimator estimator in _truePeaks)
        {
            estimator.Clear();
        }

        _linePosition = 0;
        _lastTruePeak = 0;
        _minimumHead = 0;
        _minimumCount = 0;
        Array.Fill(_box, 1f);
        _boxPosition = 0;
        _boxSum = _box.Length;
        _held = 1;
        _step = 0;
        _received = 0;
        _sent = 0;
        _started = false;
    }

    private protected override void Process(AudioFrame frame)
    {
        if (!_started)
        {
            _started = true;
            _origin = IndexOf(frame.Timestamp);
        }

        int n = frame.SampleCount;
        _received += n;
        Run(frame);

        // The first samples out are the delay line's start, not input.
        long first = _step - n - _delay;
        int skip = (int)Math.Clamp(-first, 0, n);
        if (skip == 0)
        {
            frame.Timestamp = TimeAt(_origin + first);
            _sent += n;
            Emit(frame);
            return;
        }

        if (skip < n)
        {
            EmitPart(frame, skip, n - skip);
        }

        frame.Dispose();
    }

    private protected override void OnFlush()
    {
        if (!_started)
        {
            return;
        }

        while (_sent < _received)
        {
            int n = (int)Math.Min(Chunk, Math.Max(_delay, 1));
            AudioFrame frame = NewFrame(Layout, n, 0);
            for (int c = 0; c < frame.ChannelCount; c++)
            {
                frame.GetSpan<float>(c).Clear();
            }

            Run(frame);
            long first = _step - n - _delay;
            int skip = (int)Math.Clamp(-first, 0, n);
            int count = (int)Math.Min(n - skip, _received - _sent);
            if (count > 0)
            {
                EmitPart(frame, skip, count);
            }

            frame.Dispose();
        }

        _started = false;
        OnReset();
    }

    private void EmitPart(AudioFrame frame, int start, int count)
    {
        AudioFrame part = NewFrame(Layout, count, _origin + _sent);
        for (int c = 0; c < frame.ChannelCount; c++)
        {
            frame.GetSpan<float>(c).Slice(start, count).CopyTo(part.GetSpan<float>(c));
        }

        _sent += count;
        Emit(part);
    }

    /// <summary>Limits a block in place: each sample comes out as the sample <see cref="_delay"/> steps earlier times its gain.</summary>
    private void Run(AudioFrame frame)
    {
        int n = frame.SampleCount;
        if (_gains.Length < n)
        {
            _gains = new float[n];
            _required = new float[n];
        }

        Span<float> required = _required.AsSpan(0, n);
        DynamicsCore.Peaks(frame, required);
        if (_truePeaks.Length > 0)
        {
            // The oversampled points at step m lie between input samples m - 6 and m - 5, so sample m - 6 takes the larger of
            // step m's and step m - 1's; the peak sequence below runs 6 samples behind the input.
            Span<float> peaks = _gains.AsSpan(0, n);
            peaks.Clear();
            for (int c = 0; c < _truePeaks.Length; c++)
            {
                _truePeaks[c].Run(frame.GetSpan<float>(c), peaks);
            }

            for (int i = 0; i < n; i++)
            {
                float peak = peaks[i];
                peaks[i] = Math.Max(peak, _lastTruePeak);
                _lastTruePeak = peak;
            }

            DelayPeaks(required, peaks);
        }

        Span<float> gains = _gains.AsSpan(0, n);
        for (int i = 0; i < n; i++)
        {
            float peak = required[i];
            gains[i] = Gain(peak > _ceiling ? _ceiling / peak : 1f);
            _step++;
        }

        for (int c = 0; c < frame.ChannelCount; c++)
        {
            Span<float> samples = frame.GetSpan<float>(c);
            float[] line = _lines[c];
            int position = _linePosition;
            for (int i = 0; i < n; i++)
            {
                float x = samples[i];
                float y = line[position] * gains[i];
                samples[i] = Math.Clamp(y, -_ceiling, _ceiling);
                line[position] = x;
                position = position + 1 == line.Length ? 0 : position + 1;
            }
        }

        _linePosition = (int)((_linePosition + (long)n) % _delay);
    }

    private void DelayPeaks(Span<float> required, ReadOnlySpan<float> truePeaks)
    {
        if (_peakLine.Length != _peakDelay)
        {
            _peakLine = new float[_peakDelay];
            _peakPosition = 0;
        }

        for (int i = 0; i < required.Length; i++)
        {
            float sample = _peakLine[_peakPosition];
            _peakLine[_peakPosition] = required[i];
            _peakPosition = _peakPosition + 1 == _peakLine.Length ? 0 : _peakPosition + 1;
            required[i] = Math.Max(sample, truePeaks[i]);
        }
    }

    /// <summary>Takes the required gain of the newest step and returns the gain for the sample leaving the delay line.</summary>
    private float Gain(float required)
    {
        // Sliding minimum over the last lookahead + 1 steps.
        int size = _minimum.Length;
        while (_minimumCount > 0 && _minimum[(_minimumHead + _minimumCount - 1) % size].Gain >= required)
        {
            _minimumCount--;
        }

        _minimum[(_minimumHead + _minimumCount) % size] = (_step, required);
        _minimumCount++;
        if (_minimum[_minimumHead].Index <= _step - size)
        {
            _minimumHead = (_minimumHead + 1) % size;
            _minimumCount--;
        }

        float minimum = _minimum[_minimumHead].Gain;

        // Falls at once, rises over the release; never above the minimum, so every peak in the window is covered.
        _held = Math.Min(minimum, _held + ((1 - _held) * _release));

        // The box average of lookahead + 1 held gains reaches each peak's gain by the time the peak leaves the delay line.
        _boxSum += _held - _box[_boxPosition];
        _box[_boxPosition] = (float)_held;
        _boxPosition++;
        if (_boxPosition == _box.Length)
        {
            _boxPosition = 0;
            _boxSum = 0;
            foreach (float g in _box)
            {
                _boxSum += g;
            }
        }

        return (float)(_boxSum / _box.Length);
    }

    /// <summary>One channel's oversampling interpolator, giving the largest point between samples for each step.</summary>
    private sealed class TruePeakEstimator(float[][] phases)
    {
        public const int Delay = 6;

        private const int Taps = LoudnessMeter.PeakTaps;
        private readonly float[] _history = new float[Taps * 2];
        private int _next;

        public void Clear()
        {
            Array.Clear(_history);
            _next = 0;
        }

        /// <summary>Raises each step's peak to the largest absolute oversampled point of this channel.</summary>
        public void Run(ReadOnlySpan<float> samples, Span<float> peaks)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                _history[_next] = samples[i];
                _history[_next + Taps] = samples[i];
                _next = _next == Taps - 1 ? 0 : _next + 1;
                ReadOnlySpan<float> window = _history.AsSpan(_next, Taps);
                float peak = peaks[i];
                foreach (float[] phase in phases)
                {
                    float y = 0;
                    for (int j = 0; j < Taps; j++)
                    {
                        y += phase[j] * window[j];
                    }

                    peak = Math.Max(peak, Math.Abs(y));
                }

                peaks[i] = peak;
            }
        }
    }
}
