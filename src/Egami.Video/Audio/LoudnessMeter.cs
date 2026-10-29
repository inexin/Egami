using Egami.Video.Analysis;

namespace Egami.Video.Audio;

/// <summary>
/// Measures loudness as ITU-R BS.1770-5 and EBU R 128 define it: momentary (400 ms) and short-term (3 s) loudness while audio
/// comes in, and at any point the gated integrated loudness, the loudness range (EBU Tech 3342), the true peak and the sample
/// peak. It takes any sample format, any rate from 8 to 384 kHz and any channel layout, weighting surround channels by +1.5 dB
/// and leaving the LFE channel out. The audio filters' loudness normalization measures with it too.
/// </summary>
/// <remarks>
/// Values update every 100 ms of audio. Integrated loudness and loudness range read negative infinity until a gated block passed,
/// as does a meter fed only silence.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-loudness"/>
/// </example>
public sealed class LoudnessMeter : IAudioDetector
{
    private const double Offset = -0.691;
    private const int ShortTermSteps = 30;
    private const int MomentarySteps = 4;

    /// <summary>
    /// How many 10 ms parts a 100 ms step has. The highest momentary and short-term loudness look at every window starting on a
    /// 10 ms boundary, as a file-based meter must for EBU Tech 3341 cases 10 and 13 (bursts that start between steps).
    /// </summary>
    private const int Parts = 10;
    internal const int PeakTaps = 12;

    private static readonly double AbsoluteGate = Power(-70);

    private readonly LoudnessOptions _options;
    private readonly AudioChannels _input = new();
    private readonly double[] _stepPower = new double[ShortTermSteps];
    private readonly long[] _stepCount = new long[ShortTermSteps];
    private readonly List<double> _blocks = [];
    private readonly List<double> _shortBlocks = [];
    private readonly List<LoudnessValue> _momentarySeries = [];
    private readonly List<LoudnessValue> _shortTermSeries = [];
    private KWeighting[] _filters = [];
    private double[] _weights = [];
    private double[] _squares = [];
    private double[] _partSquares = [];
    private readonly double[] _partPower = new double[ShortTermSteps * Parts];
    private readonly long[] _partCount = new long[ShortTermSteps * Parts];
    private long _part;
    private long _partStart;
    private long _partEnd;
    private int _partsFilled;
    private float[] _samplePeaks = [];
    private PeakEstimator[] _truePeaks = [];
    private long _origin;
    private long _samples;
    private long _step;
    private long _stepStart;
    private long _stepEnd;
    private int _filled;
    private bool _complete;

    /// <summary>Creates a meter.</summary>
    /// <param name="options">What to measure and keep, or <see langword="null"/> for <see cref="LoudnessOptions.Default"/>.</param>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-loudness"/>
    /// </example>
    public LoudnessMeter(LoudnessOptions? options = null)
    {
        _options = options ?? LoudnessOptions.Default;
    }

    /// <summary>Gets the options the meter runs with.</summary>
    public LoudnessOptions Options => _options;

    /// <summary>Gets the loudness of the last 400 ms in LUFS, or of what came so far when that is shorter.</summary>
    public double Momentary { get; private set; } = double.NegativeInfinity;

    /// <summary>Gets the loudness of the last 3 s in LUFS, or of what came so far when that is shorter.</summary>
    public double ShortTerm { get; private set; } = double.NegativeInfinity;

    /// <summary>Gets the highest momentary loudness so far, in LUFS, over every 400 ms window that starts on a 10 ms boundary.</summary>
    public double MaxMomentary { get; private set; } = double.NegativeInfinity;

    /// <summary>Gets the highest short-term loudness so far, in LUFS, over every 3 s window that starts on a 10 ms boundary.</summary>
    public double MaxShortTerm { get; private set; } = double.NegativeInfinity;

    /// <summary>Gets the gated integrated loudness of everything so far in LUFS (BS.1770: -70 LUFS absolute and -10 LU relative gates).</summary>
    public double Integrated => Gate(_blocks, -10, out _);

    /// <summary>Gets the loudness range so far in LU: the spread from the 10th to the 95th percentile of the gated short-term loudness (EBU Tech 3342).</summary>
    public double Range => LoudnessRange(out _, out _, out _);

    /// <summary>Gets the highest true peak so far in dBTP, over all channels; the sample peak when <see cref="LoudnessOptions.TruePeak"/> is off.</summary>
    public double TruePeak => AudioChannels.Decibels(_truePeaks.Length > 0 ? _truePeaks.Max(p => p.Peak) : SamplePeakLinear);

    /// <summary>Gets the highest sample level so far in dBFS, over all channels.</summary>
    public double SamplePeak => AudioChannels.Decibels(SamplePeakLinear);

    /// <summary>Gets how much audio the meter has taken.</summary>
    public MediaTime Duration => _input.SampleRate > 0 ? new MediaTime(_samples, new Rational(1, _input.SampleRate)) : MediaTime.Zero;

    /// <summary>Gets the momentary loudness every 100 ms, when <see cref="LoudnessOptions.KeepSeries"/> is set; otherwise empty.</summary>
    public IReadOnlyList<LoudnessValue> MomentarySeries => _momentarySeries;

    /// <summary>Gets the short-term loudness every 100 ms from 3 s on, when <see cref="LoudnessOptions.KeepSeries"/> is set; otherwise empty.</summary>
    public IReadOnlyList<LoudnessValue> ShortTermSeries => _shortTermSeries;

    private float SamplePeakLinear => _samplePeaks.Length > 0 ? _samplePeaks.Max() : 0;

    /// <summary>Takes the next frame of samples.</summary>
    /// <param name="frame">The frame, in time order, in any sample format.</param>
    /// <exception cref="InvalidOperationException"><see cref="Complete"/> was called, or the rate or layout changed.</exception>
    /// <exception cref="NotSupportedException">The sample rate is below 8 kHz or above 384 kHz.</exception>
    public void Add(AudioFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (_complete)
        {
            throw new InvalidOperationException("The meter is complete; create a new one for more audio.");
        }

        if (_input.SampleRate == 0)
        {
            Start(frame);
        }

        _input.Read(frame, "loudness meter");
        int count = frame.SampleCount;
        int position = 0;
        while (position < count)
        {
            int take = (int)Math.Min(count - position, _partEnd - _samples);
            for (int c = 0; c < _filters.Length; c++)
            {
                ReadOnlySpan<float> samples = _input.Channel(c, count).Slice(position, take);
                if (_weights[c] != 0)
                {
                    _partSquares[c] += _filters[c].Process(samples);
                }

                _samplePeaks[c] = Math.Max(_samplePeaks[c], PeakOf(samples));
                if (_truePeaks.Length > 0)
                {
                    _truePeaks[c].Process(samples);
                }
            }

            position += take;
            _samples += take;
            if (_samples == _partEnd)
            {
                EndPart();
            }

            if (_samples == _stepEnd)
            {
                EndStep();
            }
        }
    }

    /// <summary>Marks the end of the audio. A last step shorter than 100 ms is left out, as BS.1770 measures whole blocks.</summary>
    /// <param name="endTime">When the audio ends.</param>
    public void Complete(MediaTime endTime) => _complete = true;

    /// <summary>Returns everything measured so far.</summary>
    /// <returns>The report.</returns>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-loudness"/>
    /// </example>
    public LoudnessReport Report()
    {
        double integrated = Gate(_blocks, -10, out double threshold);
        double range = LoudnessRange(out double low, out double high, out double rangeThreshold);
        double[] peaks = _truePeaks.Length > 0
            ? [.. _truePeaks.Select(p => AudioChannels.Decibels(p.Peak))]
            : [.. _samplePeaks.Select(p => AudioChannels.Decibels(p))];
        return new LoudnessReport(
            integrated,
            range,
            low,
            high,
            threshold,
            rangeThreshold,
            TruePeak,
            SamplePeak,
            MaxMomentary,
            MaxShortTerm,
            Duration,
            peaks);
    }

    /// <summary>Returns the weight BS.1770-5 gives a channel: 0 for LFE, 1.41 (+1.5 dB) for the surround pair, 1 for the rest.</summary>
    internal static double Weight(Speakers speaker) => speaker switch
    {
        Speakers.LowFrequency => 0,
        Speakers.BackLeft or Speakers.BackRight or Speakers.SideLeft or Speakers.SideRight => 1.41,
        _ => 1,
    };

    /// <summary>Returns the K-weighting coefficients for a rate: the shelf's b0, b1, b2, a1, a2 and the high-pass's a1, a2.</summary>
    internal static double[] FilterCoefficients(int rate)
    {
        var (b0, b1, b2, a1, a2, c1, c2) = new KWeighting(rate).Coefficients;
        return [b0, b1, b2, a1, a2, c1, c2];
    }

    private static double Power(double loudness) => Math.Pow(10, (loudness - Offset) / 10);

    private static double Loudness(double power) => power > 0 ? Offset + (10 * Math.Log10(power)) : double.NegativeInfinity;

    /// <summary>Applies the absolute gate and a relative gate <paramref name="relative"/> LU below the absolutely gated mean.</summary>
    private static double Gate(List<double> blocks, double relative, out double threshold)
    {
        double sum = 0;
        int count = 0;
        foreach (double block in blocks)
        {
            if (block > AbsoluteGate)
            {
                sum += block;
                count++;
            }
        }

        if (count == 0)
        {
            threshold = double.NegativeInfinity;
            return double.NegativeInfinity;
        }

        threshold = Loudness(sum / count) + relative;
        double gate = Math.Max(AbsoluteGate, Power(threshold));
        sum = 0;
        count = 0;
        foreach (double block in blocks)
        {
            if (block > gate)
            {
                sum += block;
                count++;
            }
        }

        return count > 0 ? Loudness(sum / count) : double.NegativeInfinity;
    }

    private static float PeakOf(ReadOnlySpan<float> samples)
    {
        float peak = 0;
        foreach (float x in samples)
        {
            peak = Math.Max(peak, Math.Abs(x));
        }

        return peak;
    }

    private double LoudnessRange(out double low, out double high, out double threshold)
    {
        low = high = double.NegativeInfinity;
        Gate(_shortBlocks, -20, out threshold);
        if (double.IsNegativeInfinity(threshold))
        {
            return double.NegativeInfinity;
        }

        double gate = Math.Max(AbsoluteGate, Power(threshold));
        List<double> kept = [.. _shortBlocks.Where(b => b > gate).Select(Loudness)];
        if (kept.Count == 0)
        {
            return double.NegativeInfinity;
        }

        kept.Sort();
        low = Percentile(kept, 0.10);
        high = Percentile(kept, 0.95);
        return high - low;
    }

    private static double Percentile(List<double> sorted, double fraction)
    {
        double position = fraction * (sorted.Count - 1);
        int below = (int)position;
        int above = Math.Min(below + 1, sorted.Count - 1);
        return sorted[below] + ((position - below) * (sorted[above] - sorted[below]));
    }

    private void Start(AudioFrame frame)
    {
        if (frame.SampleRate is < 8000 or > 384000)
        {
            throw new NotSupportedException($"The loudness meter takes rates from 8 to 384 kHz, not {frame.SampleRate} Hz.");
        }

        int channels = frame.ChannelCount;
        _filters = new KWeighting[channels];
        _weights = new double[channels];
        _squares = new double[channels];
        _partSquares = new double[channels];
        _samplePeaks = new float[channels];
        for (int c = 0; c < channels; c++)
        {
            _filters[c] = new KWeighting(frame.SampleRate);
            _weights[c] = Weight(frame.Layout.GetSpeaker(c));
        }

        if (_options.TruePeak)
        {
            int factor = frame.SampleRate < 96000 ? 4 : frame.SampleRate < 192000 ? 2 : 1;
            if (factor > 1)
            {
                float[][] phases = PeakEstimator.Design(factor);
                _truePeaks = new PeakEstimator[channels];
                for (int c = 0; c < channels; c++)
                {
                    _truePeaks[c] = new PeakEstimator(phases);
                }
            }
        }

        _origin = frame.Timestamp.Rescale(new Rational(1, frame.SampleRate)).Ticks;
        _stepEnd = Boundary(1, frame.SampleRate);
        _partEnd = PartBoundary(1, frame.SampleRate);
    }

    /// <summary>Returns where 100 ms step <paramref name="step"/> starts, in samples, so steps stay exact at any rate.</summary>
    private static long Boundary(long step, int rate) => ((step * rate) + 5) / 10;

    /// <summary>Returns where 10 ms part <paramref name="part"/> starts; every tenth part starts where a step does.</summary>
    private static long PartBoundary(long part, int rate) => ((part * rate) + 50) / 100;

    /// <summary>Ends a 10 ms part: adds it to its step and takes the highest momentary and short-term loudness ending here.</summary>
    private void EndPart()
    {
        double weighted = 0;
        for (int c = 0; c < _partSquares.Length; c++)
        {
            weighted += _weights[c] * _partSquares[c];
            _squares[c] += _partSquares[c];
            _partSquares[c] = 0;
        }

        int slot = (int)(_part % _partPower.Length);
        _partPower[slot] = weighted;
        _partCount[slot] = _partEnd - _partStart;
        _partsFilled = Math.Min(_partsFilled + 1, _partPower.Length);
        _part++;
        _partStart = _partEnd;
        _partEnd = PartBoundary(_part + 1, _input.SampleRate);
        if (_partsFilled >= MomentarySteps * Parts)
        {
            MaxMomentary = Math.Max(MaxMomentary, Loudness(RecentParts(MomentarySteps * Parts)));
        }

        if (_partsFilled >= ShortTermSteps * Parts)
        {
            MaxShortTerm = Math.Max(MaxShortTerm, Loudness(RecentParts(ShortTermSteps * Parts)));
        }
    }

    private double RecentParts(int parts)
    {
        double power = 0;
        long count = 0;
        for (int i = 1; i <= parts; i++)
        {
            int slot = (int)((_part - i) % _partPower.Length);
            power += _partPower[slot];
            count += _partCount[slot];
        }

        return count > 0 ? power / count : 0;
    }

    private void EndStep()
    {
        double weighted = 0;
        for (int c = 0; c < _squares.Length; c++)
        {
            weighted += _weights[c] * _squares[c];
            _squares[c] = 0;
        }

        int slot = (int)(_step % ShortTermSteps);
        _stepPower[slot] = weighted;
        _stepCount[slot] = _stepEnd - _stepStart;
        _filled = Math.Min(_filled + 1, ShortTermSteps);
        _step++;
        _stepStart = _stepEnd;
        _stepEnd = Boundary(_step + 1, _input.SampleRate);

        MediaTime time = new(_origin + _stepStart, new Rational(1, _input.SampleRate));
        double momentary = Recent(Math.Min(MomentarySteps, _filled));
        Momentary = Loudness(momentary);
        if (_filled >= MomentarySteps)
        {
            _blocks.Add(momentary);
            if (_options.KeepSeries)
            {
                _momentarySeries.Add(new LoudnessValue(time, Momentary));
            }
        }

        double shortTerm = Recent(_filled);
        ShortTerm = Loudness(shortTerm);
        if (_filled >= ShortTermSteps)
        {
            _shortBlocks.Add(shortTerm);
            if (_options.KeepSeries)
            {
                _shortTermSeries.Add(new LoudnessValue(time, ShortTerm));
            }
        }
    }

    /// <summary>Returns the mean weighted power over the last <paramref name="steps"/> 100 ms steps.</summary>
    private double Recent(int steps)
    {
        double power = 0;
        long count = 0;
        for (int i = 1; i <= steps; i++)
        {
            int slot = (int)((_step - i) % ShortTermSteps);
            power += _stepPower[slot];
            count += _stepCount[slot];
        }

        return count > 0 ? power / count : 0;
    }

    /// <summary>
    /// The BS.1770 K-weighting: a high shelf (+4 dB above about 1.5 kHz, the head's effect) followed by a high-pass (the RLB
    /// curve). BS.1770 gives the coefficients at 48 kHz; for other rates they come from the same analog filters through the
    /// bilinear transform, with the analog parameters that reproduce the 48 kHz table, as libebur128 does (THIRD-PARTY-NOTICES.md).
    /// </summary>
    private sealed class KWeighting
    {
        private readonly double _b0, _b1, _b2, _a1, _a2, _c1, _c2;
        private double _s1, _s2, _t1, _t2;

        public KWeighting(int rate)
        {
            // High shelf: centre 1681.97 Hz, gain 4.00 dB, Q 0.7072.
            double k = Math.Tan(Math.PI * 1681.974450955533 / rate);
            double q = 0.7071752369554196;
            double vh = Math.Pow(10, 3.999843853973347 / 20);
            double vb = Math.Pow(vh, 0.4996667741545416);
            double a0 = 1 + (k / q) + (k * k);
            _b0 = (vh + (vb * k / q) + (k * k)) / a0;
            _b1 = 2 * ((k * k) - vh) / a0;
            _b2 = (vh - (vb * k / q) + (k * k)) / a0;
            _a1 = 2 * ((k * k) - 1) / a0;
            _a2 = (1 - (k / q) + (k * k)) / a0;

            // High-pass: corner 38.14 Hz, Q 0.5003; its numerator is 1, -2, 1.
            k = Math.Tan(Math.PI * 38.13547087602444 / rate);
            q = 0.5003270373238773;
            a0 = 1 + (k / q) + (k * k);
            _c1 = 2 * ((k * k) - 1) / a0;
            _c2 = (1 - (k / q) + (k * k)) / a0;
        }

        /// <summary>Gets the shelf and high-pass coefficients: b0, b1, b2, a1, a2 and the high-pass's a1, a2.</summary>
        public (double B0, double B1, double B2, double A1, double A2, double C1, double C2) Coefficients => (_b0, _b1, _b2, _a1, _a2, _c1, _c2);

        /// <summary>Filters the samples and returns the sum of the squared output.</summary>
        public double Process(ReadOnlySpan<float> samples)
        {
            double s1 = _s1, s2 = _s2, t1 = _t1, t2 = _t2, sum = 0;
            foreach (float sample in samples)
            {
                // Transposed direct form II for both stages.
                double x = sample;
                double y = (_b0 * x) + s1;
                s1 = (_b1 * x) - (_a1 * y) + s2;
                s2 = (_b2 * x) - (_a2 * y);
                double z = y + t1;
                t1 = (-2 * y) - (_c1 * z) + t2;
                t2 = y - (_c2 * z);
                sum += z * z;
            }

            _s1 = s1;
            _s2 = s2;
            _t1 = t1;
            _t2 = t2;
            return sum;
        }
    }

    /// <summary>
    /// The true-peak estimate of BS.1770 Annex 2: the samples upsampled by 4 (2 from 96 kHz) with a polyphase interpolating
    /// low-pass of 12 taps per phase, and the largest magnitude kept.
    /// </summary>
    internal sealed class PeakEstimator(float[][] phases)
    {
        private readonly float[] _history = new float[PeakTaps * 2];
        private int _next;

        public float Peak { get; private set; }

        /// <summary>Designs the phases: a Kaiser-windowed sinc cut at the input's Nyquist frequency, each phase scaled to a gain of 1.</summary>
        public static float[][] Design(int factor)
        {
            int length = factor * PeakTaps;
            double middle = (length - 1) / 2.0;
            const double Beta = 5.0;
            double[] taps = new double[length];
            for (int n = 0; n < length; n++)
            {
                double t = (n - middle) / factor;
                double sinc = t == 0 ? 1 : Math.Sin(Math.PI * t) / (Math.PI * t);
                double r = (n - middle) / (middle + 1);
                taps[n] = sinc * Bessel0(Beta * Math.Sqrt(1 - (r * r))) / Bessel0(Beta);
            }

            float[][] phases = new float[factor][];
            for (int p = 0; p < factor; p++)
            {
                // Phase p weighs input sample m - j by tap p + j * factor; stored oldest first to match the history window.
                double gain = 0;
                for (int j = 0; j < PeakTaps; j++)
                {
                    gain += taps[p + (j * factor)];
                }

                phases[p] = new float[PeakTaps];
                for (int j = 0; j < PeakTaps; j++)
                {
                    phases[p][PeakTaps - 1 - j] = (float)(taps[p + (j * factor)] / gain);
                }
            }

            return phases;
        }

        public void Process(ReadOnlySpan<float> samples)
        {
            float peak = Peak;
            foreach (float sample in samples)
            {
                // The history is written twice, so the last 12 samples always sit together in one window.
                _history[_next] = sample;
                _history[_next + PeakTaps] = sample;
                _next = _next == PeakTaps - 1 ? 0 : _next + 1;
                ReadOnlySpan<float> window = _history.AsSpan(_next, PeakTaps);
                foreach (float[] phase in phases)
                {
                    float y = 0;
                    for (int j = 0; j < PeakTaps; j++)
                    {
                        y += phase[j] * window[j];
                    }

                    peak = Math.Max(peak, Math.Abs(y));
                }
            }

            Peak = peak;
        }

        private static double Bessel0(double x)
        {
            double sum = 1, term = 1;
            for (int k = 1; k < 30; k++)
            {
                term *= x / (2 * k);
                sum += term * term;
            }

            return sum;
        }
    }
}

/// <summary>What <see cref="LoudnessMeter"/> measures and keeps.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-loudness"/>
/// </example>
public sealed record LoudnessOptions
{
    /// <summary>Gets the default options.</summary>
    public static LoudnessOptions Default { get; } = new();

    /// <summary>Gets whether to measure the true peak by upsampling (BS.1770 Annex 2). On by default; off, the true peak is the sample peak.</summary>
    public bool TruePeak { get; init; } = true;

    /// <summary>Gets whether to keep the momentary and short-term loudness every 100 ms. Off by default to keep memory flat.</summary>
    public bool KeepSeries { get; init; }
}

/// <summary>One loudness reading in a <see cref="LoudnessMeter"/> series.</summary>
/// <param name="Time">When the 400 ms or 3 s window it measures ends.</param>
/// <param name="Loudness">The loudness in LUFS.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-loudness"/>
/// </example>
public readonly record struct LoudnessValue(MediaTime Time, double Loudness);

/// <summary>What <see cref="LoudnessMeter"/> measured. Loudness is in LUFS, ranges in LU, peaks in dBFS (dBTP for true peak).</summary>
/// <param name="Integrated">The gated integrated loudness, as ebur128's I.</param>
/// <param name="Range">The loudness range, as ebur128's LRA: <paramref name="RangeHigh"/> minus <paramref name="RangeLow"/>.</param>
/// <param name="RangeLow">The 10th percentile of the gated short-term loudness, as ebur128's LRA low.</param>
/// <param name="RangeHigh">The 95th percentile of the gated short-term loudness, as ebur128's LRA high.</param>
/// <param name="IntegratedThreshold">The relative gate the integrated loudness used, 10 LU below the absolutely gated loudness.</param>
/// <param name="RangeThreshold">The relative gate the loudness range used, 20 LU below the absolutely gated short-term loudness.</param>
/// <param name="TruePeak">The highest true peak over all channels in dBTP.</param>
/// <param name="SamplePeak">The highest sample level over all channels in dBFS.</param>
/// <param name="MaxMomentary">The highest momentary loudness.</param>
/// <param name="MaxShortTerm">The highest short-term loudness.</param>
/// <param name="Duration">How much audio was measured.</param>
/// <param name="ChannelTruePeaks">Each channel's true peak in dBTP, in channel order.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-loudness"/>
/// </example>
public sealed record LoudnessReport(
    double Integrated,
    double Range,
    double RangeLow,
    double RangeHigh,
    double IntegratedThreshold,
    double RangeThreshold,
    double TruePeak,
    double SamplePeak,
    double MaxMomentary,
    double MaxShortTerm,
    MediaTime Duration,
    IReadOnlyList<double> ChannelTruePeaks);
