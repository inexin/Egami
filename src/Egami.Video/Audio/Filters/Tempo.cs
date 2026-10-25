namespace Egami.Video.Audio.Filters;

/// <summary>What a <see cref="Tempo"/> is tuned for: the length of its windows and how far it searches.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/AudioFilters.cs" region="audio-tempo"/>
/// </example>
public enum TempoPreset
{
    /// <summary>46 ms windows, searched 12 ms either way: keeps the rhythm of music and low notes steady. The default.</summary>
    Music,

    /// <summary>24 ms windows, searched 8 ms either way: follows the fast changes of speech with fewer echoes.</summary>
    Speech,
}

/// <summary>
/// Changes the tempo and keeps the pitch, with WSOLA (waveform similarity overlap-add, after Verhelst and Roelands, 1993): the
/// output is built of overlapping Hann windows of the input, each taken at the place near its ideal position whose waveform
/// best continues the window before it. The output keeps the sample rate, starts at the input's first timestamp and lasts the
/// input's length divided by <see cref="Factor"/>, rounded to the nearest sample. A factor of 1 passes samples through. For a
/// speed change where the pitch follows, <see cref="Speed"/> is cheaper.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/AudioFilters.cs" region="audio-tempo"/>
/// </example>
public sealed class Tempo : AudioFilter
{
    private int _window;
    private int _hop;
    private int _search;
    private int _step;
    private float[] _hann = [];
    private SampleBuffer? _input;
    private SampleBuffer? _mono;
    private SampleBuffer? _output;
    private float[][] _overlap = [];
    private long _base;
    private long _received;
    private long _produced;
    private long _frames;
    private long _previous;
    private long _origin;
    private bool _started;

    /// <summary>Creates a tempo change.</summary>
    /// <param name="factor">How much faster: 1.25 plays a quarter faster, 0.8 a fifth slower; 0.25 to 4.</param>
    /// <param name="preset">What the input is. Default <see cref="TempoPreset.Music"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">The factor is outside 0.25 to 4.</exception>
    public Tempo(double factor, TempoPreset preset = TempoPreset.Music)
    {
        if (!(factor >= 0.25 && factor <= 4))
        {
            throw new ArgumentOutOfRangeException(nameof(factor), factor, "The tempo factor must be from 0.25 to 4.");
        }

        Factor = factor;
        Preset = preset;
    }

    /// <summary>Gets how much faster the stream plays.</summary>
    public double Factor { get; }

    /// <summary>Gets what the filter is tuned for.</summary>
    public TempoPreset Preset { get; }

    private protected override long LatencySamples => Factor == 1 ? 0 : _window + _search;

    private protected override void OnStart()
    {
        (double window, double search) = Preset == TempoPreset.Speech ? (0.024, 0.008) : (0.046, 0.012);
        _hop = Math.Max(8, (int)Math.Round(window * SampleRate / 2));
        _window = 2 * _hop;
        _search = Math.Max(1, (int)Math.Round(search * SampleRate));
        _step = Math.Max(1, SampleRate / 8000);

        // Periodic Hann: windows half a window apart add up to exactly 1.
        _hann = new float[_window];
        for (int i = 0; i < _window; i++)
        {
            _hann[i] = (float)(0.5 - (0.5 * Math.Cos(2 * Math.PI * i / _window)));
        }

        int channels = Layout.ChannelCount;
        _input = new SampleBuffer(channels, 4 * _window);
        _mono = new SampleBuffer(1, 4 * _window);
        _output = new SampleBuffer(channels, 4 * _window);
        _overlap = new float[channels][];
        for (int c = 0; c < channels; c++)
        {
            _overlap[c] = new float[_window];
        }

        OnReset();
    }

    private protected override void OnReset()
    {
        _started = false;
        _input?.Clear();
        _mono?.Clear();
        _output?.Clear();
        _base = _received = _produced = _frames = _previous = 0;
    }

    private protected override void Process(AudioFrame frame)
    {
        if (Factor == 1)
        {
            Emit(frame);
            return;
        }

        if (!_started)
        {
            _started = true;
            _origin = IndexOf(frame.Timestamp);
        }

        using (frame)
        {
            Append(frame);
        }

        Run(final: false);
    }

    private protected override void OnFlush()
    {
        if (Factor == 1 || !_started)
        {
            return;
        }

        // Zeros after the end so the last windows can be built, then stop at the exact output length.
        int pad = _window + _search + (int)Math.Ceiling(_hop * Factor) + _hop;
        _input!.AppendSilence(pad);
        _mono!.AppendSilence(pad);
        Run(final: true);
        OnReset();
    }

    private void Append(AudioFrame frame)
    {
        int n = frame.SampleCount, channels = frame.ChannelCount;
        _input!.Append(frame);
        _mono!.AppendSilence(n);
        Span<float> mono = _mono[0][^n..];
        float scale = 1f / channels;
        for (int c = 0; c < channels; c++)
        {
            SampleMath.AddScaled(mono, frame.AsSpan<float>(c), scale);
        }

        _received += n;
    }

    private void Run(bool final)
    {
        long target = (long)Math.Round(_received / Factor);
        while (!final || _produced + _output!.Count < target)
        {
            long ideal = (long)Math.Round(_frames * _hop * Factor);
            long needed = ideal + _search + _window;
            if (_base + _input!.Count < needed)
            {
                break;
            }

            long position = _frames == 0 ? 0 : Seek(ideal);
            AddWindow(position);
            _previous = position;
            _frames++;

            // Keep what the next window and its search can still reach.
            long next = (long)Math.Round(_frames * _hop * Factor) - _search;
            Drop(Math.Min(next, _previous + _hop));
        }

        // Never past the length the input so far maps to; windows past it wait for more input or are cut at the end.
        int count = (int)Math.Max(0, Math.Min(_output!.Count, target - _produced));

        if (count > 0)
        {
            AudioFrame frame = NewFrame(Layout, count, _origin + _produced);
            _output.CopyTo(frame);
            _output.Drop(count);
            _produced += count;
            Emit(frame);
        }
    }

    /// <summary>Adds the Hann window of the input at a position and hands out the half that is now final.</summary>
    private void AddWindow(long position)
    {
        int at = (int)(position - _base);
        for (int c = 0; c < _overlap.Length; c++)
        {
            float[] overlap = _overlap[c];
            ReadOnlySpan<float> input = _input![c].Slice(at, _window);
            if (_frames == 0)
            {
                // Nothing before the first window: give its rising half the falling half's weight too, so it starts at full level.
                for (int i = 0; i < _hop; i++)
                {
                    overlap[i] = input[i] * _hann[i + _hop];
                }
            }

            SampleMath.AddMultiplied(overlap, input, _hann);
            _output!.Append(c, overlap.AsSpan(0, _hop), c == _overlap.Length - 1);
            overlap.AsSpan(_hop).CopyTo(overlap);
            overlap.AsSpan(_hop).Clear();
        }
    }

    /// <summary>Finds the input position near <paramref name="ideal"/> whose start best continues the previous window.</summary>
    private long Seek(long ideal)
    {
        ReadOnlySpan<float> mono = _mono![0];
        long lo = Math.Max(0, ideal - _search), hi = ideal + _search;
        ReadOnlySpan<float> wanted = mono.Slice((int)(_previous + _hop - _base), _hop);

        // Coarse: every step-th candidate, with every step-th sample; then every candidate near the best one.
        long best = Math.Clamp(ideal, lo, hi);
        double bestScore = double.NegativeInfinity;
        for (long p = lo; p <= hi; p += _step)
        {
            double score = Score(wanted, mono.Slice((int)(p - _base), _hop), _step);
            if (score > bestScore)
            {
                bestScore = score;
                best = p;
            }
        }

        long center = best;
        bestScore = double.NegativeInfinity;
        for (long p = Math.Max(lo, center - _step + 1); p <= Math.Min(hi, center + _step - 1); p++)
        {
            double score = Score(wanted, mono.Slice((int)(p - _base), _hop), 1);
            if (score > bestScore)
            {
                bestScore = score;
                best = p;
            }
        }

        return best;
    }

    /// <summary>The cross-correlation of a candidate with the wanted waveform, over the candidate's energy (normalized).</summary>
    private static double Score(ReadOnlySpan<float> wanted, ReadOnlySpan<float> candidate, int stride)
    {
        double dot = 0, energy = 1e-12;
        for (int i = 0; i < wanted.Length; i += stride)
        {
            float x = candidate[i];
            dot += wanted[i] * x;
            energy += x * x;
        }

        return dot / Math.Sqrt(energy);
    }

    private void Drop(long upTo)
    {
        int count = (int)Math.Clamp(upTo - _base, 0, _input!.Count);
        if (count < 4096)
        {
            return;
        }

        _input.Drop(count);
        _mono!.Drop(count);
        _base += count;
    }
}
