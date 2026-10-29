namespace Egami.Video.Audio.Filters;

/// <summary>One band of a <see cref="ParametricEqualizer"/>: a <see cref="Biquad"/> shape, frequency, gain and Q.</summary>
/// <param name="Kind">The response shape, usually <see cref="BiquadKind.Peaking"/> or a shelf.</param>
/// <param name="Frequency">The centre or corner frequency in hertz.</param>
/// <param name="Gain">The boost or cut in decibels; low and high pass, band pass, notch and all pass ignore it.</param>
/// <param name="Q">The Q; 0.7071 by default.</param>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/AudioFilters.cs" region="audio-equalizer"/>
/// </example>
public readonly record struct EqualizerBand(BiquadKind Kind, double Frequency, double Gain, double Q = Biquad.ButterworthQ);

/// <summary>
/// Several <see cref="Biquad"/> bands run as one filter, in the order given, like a mixing desk's EQ or a chain of ffmpeg's
/// <c>equalizer</c>, <c>bass</c> and <c>treble</c>. Works in place.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/AudioFilters.cs" region="audio-equalizer"/>
/// </example>
public sealed class ParametricEqualizer : AudioFilter
{
    private readonly EqualizerBand[] _bands;
    private BiquadSection[] _sections = [];

    /// <summary>Creates the equalizer.</summary>
    /// <param name="bands">The bands, run in order; none passes the sound through.</param>
    /// <exception cref="ArgumentOutOfRangeException">A band's frequency or Q is not positive and finite, or its gain is not finite.</exception>
    public ParametricEqualizer(params IEnumerable<EqualizerBand> bands)
    {
        ArgumentNullException.ThrowIfNull(bands);
        _bands = [.. bands];
        foreach (EqualizerBand band in _bands)
        {
            Biquad.Check(band.Kind, band.Frequency, band.Gain);
            if (!(band.Q > 0 && double.IsFinite(band.Q)))
            {
                throw new ArgumentOutOfRangeException(nameof(bands), band.Q, "Each band's Q must be a positive finite number.");
            }
        }
    }

    /// <summary>Gets the bands, in the order they run.</summary>
    public IReadOnlyList<EqualizerBand> Bands => _bands;

    private protected override void OnStart()
    {
        foreach (EqualizerBand band in _bands)
        {
            if (Biquad.AboveNyquist(band.Frequency, SampleRate) is string message)
            {
                throw new ArgumentException(message, "frame");
            }
        }

        BiquadCoefficients[] stages = [.. _bands.Select(b => BiquadCoefficients.Design(b.Kind, b.Frequency, b.Q, b.Gain, null, SampleRate))];
        _sections = BiquadSection.ForChannels(stages, Layout.ChannelCount);
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
        if (_bands.Length > 0)
        {
            for (int c = 0; c < _sections.Length; c++)
            {
                _sections[c].Run(frame.GetSpan<float>(c));
            }
        }

        Emit(frame);
    }
}
