namespace Egami.Video.Audio.Filters;

/// <summary>How a dynamics filter measures the level it reacts to.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/AudioFilters.cs" region="audio-dynamics"/>
/// </example>
public enum LevelDetection
{
    /// <summary>The loudest channel's sample peak: reacts to every transient.</summary>
    Peak,

    /// <summary>The loudest channel's RMS over 10 ms: follows the level the ear hears, the default.</summary>
    Rms,
}

/// <summary>
/// The level detector, gain computer and gain smoothing <see cref="Compressor"/> and <see cref="Gate"/> share. Every channel
/// gets the same gain, taken from the loudest channel, so the stereo image stays put. The gain is smoothed in decibels with
/// one-pole attack and release.
/// </summary>
internal sealed class DynamicsCore
{
    // Levels below this (-200 dBFS) count as silence.
    private const double Floor = 1e-10;

    private readonly bool _expander;
    private readonly double _threshold, _ratio, _knee, _range, _makeup;
    private readonly bool _rms;
    private double _attack, _release, _average;
    private double _mean;
    private double _gain;

    public DynamicsCore(bool expander, double threshold, double ratio, double knee, double range, double makeup, LevelDetection detection)
    {
        _expander = expander;
        _threshold = threshold;
        _ratio = ratio;
        _knee = knee;
        _range = range;
        _makeup = makeup;
        _rms = detection == LevelDetection.Rms;
    }

    /// <summary>Sets the time constants for a sample rate.</summary>
    public void Start(int sampleRate, MediaTime attack, MediaTime release)
    {
        _attack = Coefficient(attack.Seconds, sampleRate);
        _release = Coefficient(release.Seconds, sampleRate);
        _average = Coefficient(0.010, sampleRate);
        Reset();
    }

    public void Reset()
    {
        _mean = 0;
        _gain = 0;
    }

    /// <summary>Returns the gain change in decibels the static curve asks for at a level in dBFS: 0 or less.</summary>
    public double Curve(double level)
    {
        double over = level - _threshold;
        if (!_expander)
        {
            if (2 * over <= -_knee)
            {
                return 0;
            }

            if (2 * over < _knee)
            {
                double x = over + (_knee / 2);
                return (1 / _ratio - 1) * x * x / (2 * _knee);
            }

            return over * (1 / _ratio - 1);
        }

        double reduction;
        if (2 * over >= _knee)
        {
            return 0;
        }

        if (2 * over > -_knee)
        {
            double x = over - (_knee / 2);
            reduction = -(_ratio - 1) * x * x / (2 * _knee);
        }
        else
        {
            reduction = over * (_ratio - 1);
        }

        return Math.Max(reduction, _range);
    }

    /// <summary>Fills the linear gain for each sample from the detector's input: the loudest channel's absolute sample.</summary>
    public void Run(ReadOnlySpan<float> detector, Span<float> gains)
    {
        double makeup = SampleMath.FromDecibels(_makeup);

        // A compressor starts acting a knee's half below the threshold; an expander already acts there and below.
        double quiet = SampleMath.FromDecibels(_threshold - (_knee / 2));
        double loud = SampleMath.FromDecibels(_threshold + (_knee / 2));
        double mean = _mean, gain = _gain;
        for (int i = 0; i < detector.Length; i++)
        {
            double level = detector[i];
            if (_rms)
            {
                mean += ((level * level) - mean) * _average;
                level = Math.Sqrt(mean);
            }

            double target = !_expander
                ? (level <= quiet ? 0 : Curve(20 * Math.Log10(level)))
                : (level >= loud ? 0 : Curve(20 * Math.Log10(Math.Max(level, Floor))));

            // A compressor attacks when its reduction grows; a gate attacks when it opens.
            bool attack = _expander ? target > gain : target < gain;
            gain += (target - gain) * (attack ? _attack : _release);
            gains[i] = (float)(gain == 0 ? makeup : SampleMath.FromDecibels(gain + _makeup));
        }

        _mean = mean < 1e-30 ? 0 : mean;
        _gain = Math.Abs(gain) < 1e-9 ? 0 : gain;
    }

    /// <summary>Takes the loudest channel's absolute value of each sample.</summary>
    public static void Peaks(AudioFrame frame, Span<float> detector)
    {
        detector.Clear();
        for (int c = 0; c < frame.ChannelCount; c++)
        {
            ReadOnlySpan<float> samples = frame.GetSpan<float>(c);
            for (int i = 0; i < detector.Length; i++)
            {
                detector[i] = Math.Max(detector[i], Math.Abs(samples[i]));
            }
        }
    }

    /// <summary>Returns a one-pole coefficient that covers about 63% of a step in the time given; 1 for no time.</summary>
    private static double Coefficient(double seconds, int sampleRate) => seconds <= 0 ? 1 : 1 - Math.Exp(-1 / (seconds * sampleRate));

    /// <summary>Checks a time constant: 0 to 10 s.</summary>
    internal static MediaTime CheckTime(MediaTime value, string name)
    {
        if (value < MediaTime.Zero || value > MediaTime.FromSeconds(10))
        {
            throw new ArgumentOutOfRangeException(name, value, "The time must be from 0 to 10 seconds.");
        }

        return value;
    }

    /// <summary>Checks a level or gain in decibels is finite and within a range.</summary>
    internal static double CheckDecibels(double value, double min, double max, string name)
    {
        if (!(value >= min && value <= max))
        {
            throw new ArgumentOutOfRangeException(name, value, $"The value must be from {min} to {max} dB.");
        }

        return value;
    }
}
