namespace Egami.Video.Audio.Filters;

/// <summary>The response shapes of a <see cref="Biquad"/>, the eight of the Audio EQ Cookbook (Robert Bristow-Johnson).</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/AudioFilters.cs" region="audio-equalizer"/>
/// </example>
public enum BiquadKind
{
    /// <summary>Passes frequencies below the corner and cuts those above, 12 dB per octave; ffmpeg's <c>lowpass</c>.</summary>
    LowPass,

    /// <summary>Passes frequencies above the corner and cuts those below, 12 dB per octave; ffmpeg's <c>highpass</c>.</summary>
    HighPass,

    /// <summary>Passes a band around the centre with 0 dB at its peak; ffmpeg's <c>bandpass</c>.</summary>
    BandPass,

    /// <summary>Cuts a band around the centre; ffmpeg's <c>bandreject</c>.</summary>
    Notch,

    /// <summary>Leaves the level of every frequency alone and turns the phase around the centre; ffmpeg's <c>allpass</c>.</summary>
    AllPass,

    /// <summary>Boosts or cuts a band around the centre by <see cref="Biquad.Gain"/>; ffmpeg's <c>equalizer</c>.</summary>
    Peaking,

    /// <summary>Boosts or cuts everything below the corner by <see cref="Biquad.Gain"/>; ffmpeg's <c>bass</c> (<c>lowshelf</c>).</summary>
    LowShelf,

    /// <summary>Boosts or cuts everything above the corner by <see cref="Biquad.Gain"/>; ffmpeg's <c>treble</c> (<c>highshelf</c>).</summary>
    HighShelf,
}

/// <summary>
/// A second-order filter from the Audio EQ Cookbook: low and high pass, band pass, notch, all pass, peaking and shelves. The
/// width is a Q (0.7071, the default, is the flattest low and high pass) or, with <see cref="FromBandwidth"/>, a bandwidth in
/// octaves. The filter runs in transposed direct form II with its state in double, so it stays quiet at low frequencies. For
/// steeper low and high pass use <see cref="Butterworth"/>; for several bands in one filter, <see cref="ParametricEqualizer"/>.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/AudioFilters.cs" region="audio-equalizer"/>
/// </example>
public sealed class Biquad : AudioFilter
{
    /// <summary>The Q of the flattest low and high pass, 1 divided by the square root of 2.</summary>
    public const double ButterworthQ = 0.70710678118654752;

    private BiquadSection[] _sections = [];

    /// <summary>Creates a filter with its width as a Q.</summary>
    /// <param name="kind">The response shape.</param>
    /// <param name="frequency">The corner or centre frequency in hertz, below half the sample rate.</param>
    /// <param name="q">The Q: higher is narrower, or for low and high pass, a sharper corner with a peak.</param>
    /// <param name="gain">The boost or cut in decibels of <see cref="BiquadKind.Peaking"/> and the shelves; the other kinds ignore it.</param>
    /// <exception cref="ArgumentOutOfRangeException">The frequency or Q is not positive and finite, or the gain is not finite.</exception>
    public Biquad(BiquadKind kind, double frequency, double q = ButterworthQ, double gain = 0)
    {
        Check(kind, frequency, gain);
        if (!(q > 0 && double.IsFinite(q)))
        {
            throw new ArgumentOutOfRangeException(nameof(q), q, "The Q must be a positive finite number.");
        }

        Kind = kind;
        Frequency = frequency;
        Q = q;
        Gain = gain;
    }

    private Biquad(BiquadKind kind, double frequency, double q, double gain, double bandwidth)
        : this(kind, frequency, q, gain)
    {
        Bandwidth = bandwidth;
    }

    /// <summary>Gets the response shape.</summary>
    public BiquadKind Kind { get; }

    /// <summary>Gets the corner or centre frequency in hertz.</summary>
    public double Frequency { get; }

    /// <summary>Gets the Q; for a filter made from a bandwidth, the Q of that bandwidth before the cookbook's warping correction.</summary>
    public double Q { get; }

    /// <summary>Gets the bandwidth in octaves the filter was made with, or <see langword="null"/> when it was made with a Q.</summary>
    public double? Bandwidth { get; }

    /// <summary>Gets the boost or cut in decibels of <see cref="BiquadKind.Peaking"/> and the shelves.</summary>
    public double Gain { get; }

    /// <summary>
    /// Creates a filter with its width as a bandwidth in octaves, like ffmpeg's <c>width_type=o</c>. For band pass, notch,
    /// all pass and peaking it is the width between the -3 dB points (or the half-gain points), corrected for the digital
    /// filter's warping as the cookbook does; for the other kinds it is turned into a Q.
    /// </summary>
    /// <param name="kind">The response shape.</param>
    /// <param name="frequency">The corner or centre frequency in hertz, below half the sample rate.</param>
    /// <param name="octaves">The bandwidth in octaves, such as 1.</param>
    /// <param name="gain">The boost or cut in decibels of <see cref="BiquadKind.Peaking"/> and the shelves.</param>
    /// <returns>The filter.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The frequency or bandwidth is not positive and finite, or the gain is not finite.</exception>
    public static Biquad FromBandwidth(BiquadKind kind, double frequency, double octaves, double gain = 0)
    {
        if (!(octaves > 0 && double.IsFinite(octaves)))
        {
            throw new ArgumentOutOfRangeException(nameof(octaves), octaves, "The bandwidth must be a positive finite number of octaves.");
        }

        double q = 1 / (2 * Math.Sinh(Math.Log(2) / 2 * octaves));
        return new Biquad(kind, frequency, q, gain, octaves);
    }

    private protected override void OnStart()
    {
        if (AboveNyquist(Frequency, SampleRate) is string message)
        {
            throw new ArgumentException(message, "frame");
        }

        BiquadCoefficients coefficients = BiquadCoefficients.Design(Kind, Frequency, Q, Gain, Bandwidth, SampleRate);
        _sections = BiquadSection.ForChannels(coefficients, Layout.ChannelCount);
    }

    private protected override void OnReset()
    {
        foreach (BiquadSection section in _sections)
        {
            section.Clear();
        }
    }

    private protected override void Process(AudioFrame frame)
    {
        for (int c = 0; c < _sections.Length; c++)
        {
            _sections[c].Run(frame.GetSpan<float>(c));
        }

        Emit(frame);
    }

    /// <summary>Checks the parameters every equalizer band shares.</summary>
    internal static void Check(BiquadKind kind, double frequency, double gain)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown filter kind.");
        }

        if (!(frequency > 0 && double.IsFinite(frequency)))
        {
            throw new ArgumentOutOfRangeException(nameof(frequency), frequency, "The frequency must be a positive finite number of hertz.");
        }

        if (!double.IsFinite(gain))
        {
            throw new ArgumentOutOfRangeException(nameof(gain), gain, "The gain must be a finite number of decibels.");
        }
    }

    /// <summary>Returns the message for a frequency at or above half the stream's sample rate, or <see langword="null"/> when it is below.</summary>
    internal static string? AboveNyquist(double frequency, int sampleRate) => frequency < sampleRate / 2.0
        ? null
        : $"The filter's frequency of {frequency} Hz must be below half the sample rate of {sampleRate} Hz.";
}

/// <summary>The normalized coefficients of one second-order (or first-order) section, a0 divided out.</summary>
internal readonly record struct BiquadCoefficients(double B0, double B1, double B2, double A1, double A2)
{
    /// <summary>Designs a section with the Audio EQ Cookbook's formulas.</summary>
    public static BiquadCoefficients Design(BiquadKind kind, double frequency, double q, double gain, double? bandwidth, int sampleRate)
    {
        double w0 = 2 * Math.PI * frequency / sampleRate;
        double cos = Math.Cos(w0), sin = Math.Sin(w0);
        bool band = kind is BiquadKind.BandPass or BiquadKind.Notch or BiquadKind.AllPass or BiquadKind.Peaking;
        double alpha = bandwidth is double bw && band
            ? sin * Math.Sinh(Math.Log(2) / 2 * bw * w0 / sin)
            : sin / (2 * q);
        double a = Math.Pow(10, gain / 40);
        double b0, b1, b2, a0, a1, a2;
        switch (kind)
        {
            case BiquadKind.LowPass:
                b0 = (1 - cos) / 2;
                b1 = 1 - cos;
                b2 = b0;
                a0 = 1 + alpha;
                a1 = -2 * cos;
                a2 = 1 - alpha;
                break;
            case BiquadKind.HighPass:
                b0 = (1 + cos) / 2;
                b1 = -(1 + cos);
                b2 = b0;
                a0 = 1 + alpha;
                a1 = -2 * cos;
                a2 = 1 - alpha;
                break;
            case BiquadKind.BandPass:
                b0 = alpha;
                b1 = 0;
                b2 = -alpha;
                a0 = 1 + alpha;
                a1 = -2 * cos;
                a2 = 1 - alpha;
                break;
            case BiquadKind.Notch:
                b0 = 1;
                b1 = -2 * cos;
                b2 = 1;
                a0 = 1 + alpha;
                a1 = -2 * cos;
                a2 = 1 - alpha;
                break;
            case BiquadKind.AllPass:
                b0 = 1 - alpha;
                b1 = -2 * cos;
                b2 = 1 + alpha;
                a0 = 1 + alpha;
                a1 = -2 * cos;
                a2 = 1 - alpha;
                break;
            case BiquadKind.Peaking:
                b0 = 1 + (alpha * a);
                b1 = -2 * cos;
                b2 = 1 - (alpha * a);
                a0 = 1 + (alpha / a);
                a1 = -2 * cos;
                a2 = 1 - (alpha / a);
                break;
            case BiquadKind.LowShelf:
                {
                    double s = 2 * Math.Sqrt(a) * alpha;
                    b0 = a * ((a + 1) - ((a - 1) * cos) + s);
                    b1 = 2 * a * ((a - 1) - ((a + 1) * cos));
                    b2 = a * ((a + 1) - ((a - 1) * cos) - s);
                    a0 = (a + 1) + ((a - 1) * cos) + s;
                    a1 = -2 * ((a - 1) + ((a + 1) * cos));
                    a2 = (a + 1) + ((a - 1) * cos) - s;
                    break;
                }

            default:
                {
                    double s = 2 * Math.Sqrt(a) * alpha;
                    b0 = a * ((a + 1) + ((a - 1) * cos) + s);
                    b1 = -2 * a * ((a - 1) + ((a + 1) * cos));
                    b2 = a * ((a + 1) + ((a - 1) * cos) - s);
                    a0 = (a + 1) - ((a - 1) * cos) + s;
                    a1 = 2 * ((a - 1) - ((a + 1) * cos));
                    a2 = (a + 1) - ((a - 1) * cos) - s;
                    break;
                }
        }

        return new BiquadCoefficients(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
    }

    /// <summary>Designs a first-order low or high pass by the bilinear transform, warped to meet the corner exactly.</summary>
    public static BiquadCoefficients FirstOrder(bool highPass, double frequency, int sampleRate)
    {
        double k = Math.Tan(Math.PI * frequency / sampleRate);
        double a0 = k + 1;
        double a1 = (k - 1) / a0;
        return highPass
            ? new BiquadCoefficients(1 / a0, -1 / a0, 0, a1, 0)
            : new BiquadCoefficients(k / a0, k / a0, 0, a1, 0);
    }
}

/// <summary>One channel's cascade of sections in transposed direct form II, state in double.</summary>
internal sealed class BiquadSection(BiquadCoefficients[] stages)
{
    // Below this the state is denormal territory; it is flushed to zero after each block so silence stays cheap.
    private const double Tiny = 1e-30;

    private readonly double[] _z1 = new double[stages.Length];
    private readonly double[] _z2 = new double[stages.Length];

    /// <summary>Makes one section of a single stage for each channel.</summary>
    public static BiquadSection[] ForChannels(BiquadCoefficients coefficients, int channels) => ForChannels([coefficients], channels);

    /// <summary>Makes one cascade for each channel.</summary>
    public static BiquadSection[] ForChannels(BiquadCoefficients[] stages, int channels)
    {
        BiquadSection[] sections = new BiquadSection[channels];
        for (int c = 0; c < channels; c++)
        {
            sections[c] = new BiquadSection(stages);
        }

        return sections;
    }

    public void Clear()
    {
        Array.Clear(_z1);
        Array.Clear(_z2);
    }

    /// <summary>Filters samples in place through every stage.</summary>
    public void Run(Span<float> samples)
    {
        for (int s = 0; s < stages.Length; s++)
        {
            BiquadCoefficients k = stages[s];
            double b0 = k.B0, b1 = k.B1, b2 = k.B2, a1 = k.A1, a2 = k.A2;
            double z1 = _z1[s], z2 = _z2[s];
            for (int i = 0; i < samples.Length; i++)
            {
                double x = samples[i];
                double y = (b0 * x) + z1;
                z1 = (b1 * x) - (a1 * y) + z2;
                z2 = (b2 * x) - (a2 * y);
                samples[i] = (float)y;
            }

            _z1[s] = Math.Abs(z1) < Tiny ? 0 : z1;
            _z2[s] = Math.Abs(z2) < Tiny ? 0 : z2;
        }
    }
}
