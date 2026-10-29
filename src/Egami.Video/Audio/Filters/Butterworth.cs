namespace Egami.Video.Audio.Filters;

/// <summary>
/// A Butterworth low or high pass of order 2 to 8: maximally flat in the pass band, -3 dB at the corner and 6 dB more cut per
/// octave for each order. It runs as a cascade of second-order sections (plus one first-order section for an odd order), each
/// designed by the bilinear transform warped to the corner, so the corner is exact at any sample rate. Order 2 is the same as
/// a <see cref="Biquad"/> with <see cref="Biquad.ButterworthQ"/>.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/AudioFilters.cs" region="audio-equalizer"/>
/// </example>
public sealed class Butterworth : AudioFilter
{
    private BiquadSection[] _sections = [];

    /// <summary>Creates the filter.</summary>
    /// <param name="kind"><see cref="BiquadKind.LowPass"/> or <see cref="BiquadKind.HighPass"/>.</param>
    /// <param name="frequency">The -3 dB corner in hertz, below half the sample rate.</param>
    /// <param name="order">The order, 2 to 8: 12 to 48 dB per octave.</param>
    /// <exception cref="ArgumentOutOfRangeException">The kind is not low or high pass, the frequency is not positive and finite, or the order is outside 2 to 8.</exception>
    public Butterworth(BiquadKind kind, double frequency, int order = 4)
    {
        if (kind is not (BiquadKind.LowPass or BiquadKind.HighPass))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "A Butterworth filter is a low or a high pass.");
        }

        Biquad.Check(kind, frequency, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(order, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(order, 8);
        Kind = kind;
        Frequency = frequency;
        Order = order;
    }

    /// <summary>Gets whether the filter is a low or a high pass.</summary>
    public BiquadKind Kind { get; }

    /// <summary>Gets the -3 dB corner in hertz.</summary>
    public double Frequency { get; }

    /// <summary>Gets the order.</summary>
    public int Order { get; }

    /// <summary>Returns the Q of each second-order section, the poles' angles on the analog prototype's unit circle.</summary>
    internal static double[] SectionQs(int order)
    {
        double[] qs = new double[order / 2];
        for (int k = 0; k < qs.Length; k++)
        {
            // Even orders have pole pairs at (2k + 1) pi / 2n from the negative real axis; odd orders at (k + 1) pi / n.
            double angle = order % 2 == 0 ? (2 * k + 1) * Math.PI / (2 * order) : (k + 1) * Math.PI / order;
            qs[k] = 1 / (2 * Math.Cos(angle));
        }

        return qs;
    }

    private protected override void OnStart()
    {
        if (Biquad.AboveNyquist(Frequency, SampleRate) is string message)
        {
            throw new ArgumentException(message, "frame");
        }

        double frequency = Frequency;
        double[] qs = SectionQs(Order);
        List<BiquadCoefficients> stages = [.. qs.Select(q => BiquadCoefficients.Design(Kind, frequency, q, 0, null, SampleRate))];
        if (Order % 2 == 1)
        {
            stages.Add(BiquadCoefficients.FirstOrder(Kind == BiquadKind.HighPass, frequency, SampleRate));
        }

        _sections = BiquadSection.ForChannels([.. stages], Layout.ChannelCount);
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
}
