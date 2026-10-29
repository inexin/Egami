namespace Egami.Video.Audio.Filters;

/// <summary>
/// The loudness a <see cref="LoudnessNormalizer"/> aims for: an integrated loudness in LUFS, a true-peak ceiling in dBTP and,
/// for the dynamic mode, a loudness range in LU. The presets are the common delivery specs.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/AudioFilters.cs" region="audio-loudness"/>
/// </example>
public sealed record LoudnessTarget
{
    /// <summary>Creates a target.</summary>
    /// <param name="integrated">The integrated loudness in LUFS, -70 to -5.</param>
    /// <param name="truePeak">The true-peak ceiling in dBTP, -9 to 0.</param>
    /// <param name="range">The loudness range the dynamic mode keeps within, in LU, 1 to 50; 7 by default.</param>
    /// <exception cref="ArgumentOutOfRangeException">A value is outside its range.</exception>
    public LoudnessTarget(double integrated, double truePeak, double range = 7)
    {
        Integrated = DynamicsCore.CheckDecibels(integrated, -70, -5, nameof(integrated));
        TruePeak = DynamicsCore.CheckDecibels(truePeak, -9, 0, nameof(truePeak));
        Range = DynamicsCore.CheckDecibels(range, 1, 50, nameof(range));
    }

    /// <summary>Gets EBU R 128 broadcast: -23 LUFS, -1 dBTP. The default.</summary>
    public static LoudnessTarget EbuR128 { get; } = new(-23, -1);

    /// <summary>Gets ATSC A/85 broadcast in the US: -24 LUFS, -2 dBTP.</summary>
    public static LoudnessTarget AtscA85 { get; } = new(-24, -2);

    /// <summary>Gets music streaming services: -14 LUFS, -1 dBTP.</summary>
    public static LoudnessTarget Streaming { get; } = new(-14, -1);

    /// <summary>Gets podcasts and spoken word: -16 LUFS, -1 dBTP.</summary>
    public static LoudnessTarget Podcast { get; } = new(-16, -1);

    /// <summary>Gets the integrated loudness in LUFS.</summary>
    public double Integrated { get; }

    /// <summary>Gets the true-peak ceiling in dBTP.</summary>
    public double TruePeak { get; }

    /// <summary>Gets the loudness range in LU the dynamic mode keeps the short-term loudness within.</summary>
    public double Range { get; }
}

/// <summary>
/// Brings audio to a <see cref="LoudnessTarget"/>, as EBU R 128 and ffmpeg's <c>loudnorm</c> do. By default it is linear: given
/// the input's <see cref="LoudnessReport"/> from a first pass with <see cref="LoudnessMeter"/>, it applies one gain, so the
/// dynamics stay as they are, and runs a true-peak <see cref="Limiter"/> only when that gain would take the peaks over the
/// ceiling. <see cref="AudioFilters.NormalizeLoudnessAsync"/> runs both passes over a file. For input that cannot be read
/// twice, <see cref="Dynamic"/> makes a one-pass normalizer that follows the loudness with 1.5 s of lookahead and changes the
/// dynamics to keep within the target's range.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/AudioFilters.cs" region="audio-loudness"/>
/// </example>
public sealed class LoudnessNormalizer : AudioFilter
{
    // Gains are kept within these, so silence or near-silence is not turned up into noise.
    private const double MaxBoost = 24;
    private const double MaxCut = -40;

    private readonly double _measuredPeak;
    private Limiter? _limiter;
    private float _factor;
    private DynamicLoudness? _dynamic;

    /// <summary>Creates a linear normalizer from a first pass's measurement.</summary>
    /// <param name="target">The target.</param>
    /// <param name="measured">The input's loudness, from <see cref="LoudnessMeter.Report"/> over the whole input.</param>
    public LoudnessNormalizer(LoudnessTarget target, LoudnessReport measured)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(measured);
        Target = target;
        Measured = measured;
        Gain = double.IsFinite(measured.Integrated) ? Math.Clamp(target.Integrated - measured.Integrated, MaxCut, MaxBoost) : 0;
        _measuredPeak = double.IsFinite(measured.TruePeak) ? measured.TruePeak : double.NegativeInfinity;
        Limits = _measuredPeak + Gain > target.TruePeak;
    }

    private LoudnessNormalizer(LoudnessTarget target)
    {
        Target = target;
        IsDynamic = true;
        Limits = true;
    }

    /// <summary>Gets the target.</summary>
    public LoudnessTarget Target { get; }

    /// <summary>Gets the first pass's measurement, or <see langword="null"/> for a dynamic normalizer.</summary>
    public LoudnessReport? Measured { get; }

    /// <summary>Gets whether the normalizer is dynamic (one pass) rather than linear.</summary>
    public bool IsDynamic { get; }

    /// <summary>Gets the linear gain in decibels: the target minus the measured loudness, within -40 to +24 dB. 0 for a dynamic normalizer.</summary>
    public double Gain { get; }

    /// <summary>Gets whether the true-peak limiter runs: for a linear normalizer, when the gain would take the peaks over the ceiling.</summary>
    public bool Limits { get; }

    private protected override long LatencySamples =>
        (_dynamic?.LatencySamples ?? 0) + (_limiter is null ? 0 : _limiter.Latency.Rescale(new Rational(1, SampleRate)).Ticks);

    /// <summary>Creates a one-pass dynamic normalizer, for input that cannot be measured first.</summary>
    /// <param name="target">The target; its <see cref="LoudnessTarget.Range"/> bounds how far the short-term loudness strays.</param>
    /// <returns>The normalizer.</returns>
    public static LoudnessNormalizer Dynamic(LoudnessTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new LoudnessNormalizer(target);
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _limiter?.Dispose();
        }

        base.Dispose(disposing);
    }

    private protected override void OnStart()
    {
        _factor = (float)SampleMath.FromDecibels(Gain);
        if (Limits)
        {
            _limiter = new Limiter { Ceiling = Target.TruePeak, TruePeak = true };
            _limiter.Context = Context;
        }

        if (IsDynamic)
        {
            _dynamic = new DynamicLoudness(Target, SampleRate, Layout, MaxCut, MaxBoost);
        }
    }

    private protected override void OnReset()
    {
        _limiter?.Reset();
        _dynamic?.Reset();
    }

    private protected override void Process(AudioFrame frame)
    {
        if (_dynamic is not null)
        {
            _dynamic.Add(frame);
            frame.Dispose();
            Pull(false);
            return;
        }

        if (_factor != 1)
        {
            for (int c = 0; c < frame.ChannelCount; c++)
            {
                SampleMath.Scale(frame.GetSpan<float>(c), _factor);
            }
        }

        Pass(frame);
    }

    private protected override void OnFlush()
    {
        if (_dynamic is not null)
        {
            Pull(true);
        }

        if (_limiter is not null)
        {
            _limiter.Flush();
            Drain();
        }
    }

    private void Pull(bool end)
    {
        while (_dynamic!.TryTake(end, Context, out AudioFrame? frame))
        {
            Pass(frame);
        }
    }

    private void Pass(AudioFrame frame)
    {
        if (_limiter is null)
        {
            Emit(frame);
            return;
        }

        _limiter.SendOwned(frame);
        Drain();
    }

    private void Drain()
    {
        while (_limiter!.TryReceive(out AudioFrame? output))
        {
            Emit(output);
        }
    }
}

/// <summary>
/// The one-pass dynamic loudness gain: BS.1770 K-weighted power in 100 ms parts, the loudness over 3 s centred on each part
/// (1.5 s of lookahead), and the integrated loudness of everything seen so far, gated as BS.1770 gates it. Each part's gain
/// brings the 3 s loudness to the target, give or take its distance from the integrated loudness, kept within half the
/// target range; a slow correction from the output's own integrated loudness so far pulls that to the target. Gains move
/// linearly from part to part and are smoothed so they never step.
/// </summary>
internal sealed class DynamicLoudness
{
    private const int Lookahead = 15;
    private const double AbsoluteGate = -70;
    private const double BinWidth = 0.1;
    private const int Bins = 800; // -70 to +10 LUFS

    private readonly LoudnessTarget _target;
    private readonly double _maxCut, _maxBoost;
    private readonly int _part;
    private readonly LoudnessMeter.KWeighting[] _filters;
    private readonly double[] _weights;
    private readonly SampleBuffer _held;
    private readonly List<double> _powers = [];
    private readonly List<double> _gains = [];
    private readonly Histogram _input = new();
    private readonly Histogram _output = new();
    private readonly List<double> _outPowers = [];
    private readonly ChannelLayout _layout;
    private readonly int _channels;
    private readonly int _rate;
    private float[] _scratch = [];
    private double _partPower;
    private int _partFill;
    private long _origin;
    private long _out;
    private bool _started;
    private double _smoothed;
    private bool _haveGain;

    public DynamicLoudness(LoudnessTarget target, int rate, ChannelLayout layout, double maxCut, double maxBoost)
    {
        _target = target;
        _maxCut = maxCut;
        _maxBoost = maxBoost;
        _rate = rate;
        _part = Math.Max(1, rate / 10);
        _layout = layout;
        _channels = layout.ChannelCount;
        _filters = new LoudnessMeter.KWeighting[_channels];
        _weights = new double[_channels];
        for (int c = 0; c < _channels; c++)
        {
            _filters[c] = new LoudnessMeter.KWeighting(rate);
            _weights[c] = LoudnessMeter.Weight(layout.GetSpeaker(c));
        }

        _held = new SampleBuffer(_channels, rate * 2);
    }

    /// <summary>Gets the most input held back: the lookahead and the part being filled.</summary>
    public long LatencySamples => (long)(Lookahead + 2) * _part;

    public void Reset()
    {
        for (int c = 0; c < _channels; c++)
        {
            _filters[c] = new LoudnessMeter.KWeighting(_rate);
        }

        _held.Clear();
        _powers.Clear();
        _gains.Clear();
        _input.Clear();
        _output.Clear();
        _outPowers.Clear();
        _partPower = 0;
        _partFill = 0;
        _started = false;
        _haveGain = false;
    }

    public void Add(AudioFrame frame)
    {
        if (!_started)
        {
            _started = true;
            _origin = frame.Timestamp.Rescale(new Rational(1, _rate)).Ticks;
            _out = 0;
        }

        _held.Append(frame);
        int at = 0, n = frame.SampleCount;
        while (at < n)
        {
            int take = Math.Min(n - at, _part - _partFill);
            for (int c = 0; c < _channels; c++)
            {
                _partPower += _weights[c] * _filters[c].Process(frame.AsSpan<float>(c).Slice(at, take));
            }

            _partFill += take;
            at += take;
            if (_partFill == _part)
            {
                AddPart(_partPower / _part);
                _partPower = 0;
                _partFill = 0;
            }
        }
    }

    /// <summary>Takes the next run of samples whose gains are known, scaled; at the end, everything held.</summary>
    public bool TryTake(bool end, MediaContext context, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out AudioFrame? frame)
    {
        frame = null;
        if (end && _partFill > 0)
        {
            AddPart(_partPower / _partFill);
            _partPower = 0;
            _partFill = 0;
        }

        // A part's gain needs the next part's gain to move towards; at the end the last gain holds.
        while (end && _gains.Count < _powers.Count)
        {
            _gains.Add(GainAt(_gains.Count));
        }

        long known = end ? _out + _held.Count : Math.Min(_out + _held.Count, (long)(_gains.Count - 1) * _part + (_part / 2));
        int count = (int)Math.Min(known - _out, 8192);
        if (count <= 0 || _held.Count == 0)
        {
            return false;
        }

        frame = new AudioFrame(SampleFormat.F32Planar, _layout, _rate, count, new MediaTime(_origin + _out, new Rational(1, _rate)), context);
        _held.CopyTo(frame);
        if (_scratch.Length < count)
        {
            _scratch = new float[count];
        }

        Span<float> gains = _scratch.AsSpan(0, count);
        for (int i = 0; i < count; i++)
        {
            gains[i] = (float)SampleMath.FromDecibels(Interpolate(_out + i));
        }

        for (int c = 0; c < _channels; c++)
        {
            SampleMath.Multiply(frame.GetSpan<float>(c), gains);
        }

        _held.Drop(count);
        _out += count;
        return true;
    }

    private void AddPart(double power)
    {
        _powers.Add(power);

        // A 400 ms block ends with every part from the fourth on; it counts toward the integrated loudness.
        _input.AddBlock(_powers);

        while (_gains.Count + Lookahead < _powers.Count)
        {
            _gains.Add(GainAt(_gains.Count));
        }
    }

    private double GainAt(int part)
    {
        double sum = 0;
        int count = 0;
        for (int i = Math.Max(0, part - Lookahead); i <= Math.Min(_powers.Count - 1, part + Lookahead - 1); i++)
        {
            sum += _powers[i];
            count++;
        }

        double window = count > 0 ? Loudness(sum / count) : double.NegativeInfinity;
        double integrated = _input.Integrated();
        double half = _target.Range / 2;
        double gain;
        if (!double.IsFinite(integrated) || window <= AbsoluteGate)
        {
            // Nothing loud enough yet, or a quiet stretch: hold the gain rather than turn silence up.
            gain = _haveGain ? _smoothed : double.IsFinite(integrated) ? _target.Integrated - integrated : 0;
        }
        else
        {
            double output = _output.Integrated();
            double correction = double.IsFinite(output) ? Math.Clamp(_target.Integrated - output, -half, half) : 0;
            gain = _target.Integrated - window + Math.Clamp(window - integrated, -half, half) + correction;
        }

        gain = Math.Clamp(gain, _maxCut, _maxBoost);

        // Smoothed over about half a second of parts, so the gain glides.
        _smoothed = _haveGain ? _smoothed + ((gain - _smoothed) * 0.2) : gain;
        _haveGain = true;
        _outPowers.Add(_powers[part] * Math.Pow(10, _smoothed / 10));
        _output.AddBlock(_outPowers);
        return _smoothed;
    }

    /// <summary>Returns the gain at a sample, moving linearly between the parts' centres.</summary>
    private double Interpolate(long index)
    {
        double position = ((double)index - (_part / 2.0)) / _part;
        int part = (int)Math.Floor(position);
        if (part < 0)
        {
            return _gains.Count > 0 ? _gains[0] : 0;
        }

        if (part >= _gains.Count - 1)
        {
            return _gains.Count > 0 ? _gains[^1] : 0;
        }

        double t = position - part;
        return _gains[part] + ((_gains[part + 1] - _gains[part]) * t);
    }

    private static double Loudness(double power) => power > 0 ? LoudnessMeter.Offset + (10 * Math.Log10(power)) : double.NegativeInfinity;

    /// <summary>The 400 ms block loudnesses in 0.1 LU bins, for the BS.1770 gated integrated loudness.</summary>
    private sealed class Histogram
    {
        private readonly long[] _counts = new long[Bins];
        private readonly double[] _powers = new double[Bins];

        public void Clear()
        {
            Array.Clear(_counts);
            Array.Clear(_powers);
        }

        /// <summary>Adds the block that ends with the newest of the parts, once there are four.</summary>
        public void AddBlock(List<double> parts)
        {
            if (parts.Count < 4)
            {
                return;
            }

            double block = (parts[^1] + parts[^2] + parts[^3] + parts[^4]) / 4;
            double loudness = Loudness(block);
            if (loudness > AbsoluteGate)
            {
                int bin = Math.Clamp((int)((loudness - AbsoluteGate) / BinWidth), 0, Bins - 1);
                _counts[bin]++;
                _powers[bin] += block;
            }
        }

        public double Integrated()
        {
            double power = 0;
            long count = 0;
            for (int b = 0; b < Bins; b++)
            {
                power += _powers[b];
                count += _counts[b];
            }

            if (count == 0)
            {
                return double.NegativeInfinity;
            }

            double relative = Loudness(power / count) - 10;
            int first = Math.Clamp((int)Math.Ceiling((relative - AbsoluteGate) / BinWidth), 0, Bins);
            power = 0;
            count = 0;
            for (int b = first; b < Bins; b++)
            {
                power += _powers[b];
                count += _counts[b];
            }

            return count == 0 ? double.NegativeInfinity : Loudness(power / count);
        }
    }
}
