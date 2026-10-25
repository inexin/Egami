namespace Egami.Video.Audio.Filters;

/// <summary>
/// Changes the level, by a fixed gain and, optionally, an <see cref="Envelope"/> that changes over time. A change to
/// <see cref="Decibels"/> or <see cref="Factor"/> between frames moves to the new gain over 5 ms, so it does not click. Unity
/// gain with no envelope passes samples through unchanged. Works in place; nothing is clipped, so a gain above 0 dB can take
/// samples past full scale until an encoder or a limiter clamps them.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/AudioFilters.cs" region="audio-filter-chain"/>
/// </example>
public sealed class Gain : AudioFilter
{
    private ParameterRamp _ramp;
    private float[] _gains = [];

    /// <summary>Creates a gain in decibels.</summary>
    /// <param name="decibels">The gain in decibels; 0 leaves the level alone, -6 about halves it.</param>
    /// <exception cref="ArgumentOutOfRangeException">The gain is NaN or infinite.</exception>
    public Gain(double decibels = 0)
    {
        _ramp = new ParameterRamp(ToFactor(decibels));
    }

    /// <summary>Gets or sets the fixed gain in decibels. A change moves to the new value over 5 ms.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is NaN or infinite.</exception>
    public double Decibels
    {
        get => SampleMath.ToDecibels(_ramp.Target);
        set => SetFactor(ToFactor(value));
    }

    /// <summary>Gets or sets the fixed gain as a linear factor: 1 leaves the level alone, 0.5 halves it, -1 inverts it.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is NaN or infinite.</exception>
    public double Factor
    {
        get => _ramp.Target;
        set
        {
            if (!double.IsFinite(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The factor must be a finite number.");
            }

            SetFactor(value);
        }
    }

    /// <summary>Gets the gain over time that multiplies the fixed gain, or <see langword="null"/> for none.</summary>
    public GainEnvelope? Envelope { get; init; }

    /// <summary>Creates a gain from a linear factor.</summary>
    /// <param name="factor">The factor: 1 leaves the level alone, 0.5 halves it.</param>
    /// <returns>The filter.</returns>
    public static Gain FromFactor(double factor) => new() { Factor = factor };

    private protected override void Process(AudioFrame frame)
    {
        int n = frame.SampleCount;
        if (Envelope is null && _ramp.IsSteady)
        {
            float gain = (float)_ramp.Value;
            if (gain != 1)
            {
                for (int c = 0; c < frame.ChannelCount; c++)
                {
                    SampleMath.Scale(frame.GetSpan<float>(c), gain);
                }
            }

            Emit(frame);
            return;
        }

        if (_gains.Length < n)
        {
            _gains = new float[n];
        }

        Span<float> gains = _gains.AsSpan(0, n);
        _ramp.Fill(gains);
        if (Envelope is not null)
        {
            long first = IndexOf(frame.Timestamp);
            for (int i = 0; i < n; i++)
            {
                gains[i] = (float)(gains[i] * Envelope.FactorAt((double)(first + i) / SampleRate));
            }
        }

        for (int c = 0; c < frame.ChannelCount; c++)
        {
            SampleMath.Multiply(frame.GetSpan<float>(c), gains);
        }

        Emit(frame);
    }

    private static double ToFactor(double decibels)
    {
        if (!double.IsFinite(decibels))
        {
            throw new ArgumentOutOfRangeException(nameof(decibels), decibels, "The gain must be a finite number of decibels.");
        }

        return SampleMath.FromDecibels(decibels);
    }

    private void SetFactor(double factor) => _ramp.Set(factor, SampleRate == 0 ? 0 : ParameterRamp.Length(SampleRate));
}
