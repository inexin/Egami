namespace Egami.Video.Audio.Filters;

/// <summary>
/// Plays a stream faster or slower with the pitch following, like a tape or ffmpeg's <c>asetrate</c> followed by
/// <c>aresample</c>: the samples are resampled with <see cref="Resampler"/> as if they had been recorded at the rate times
/// <see cref="Factor"/>. The output keeps the sample rate and lasts the input's length divided by the factor; it starts at
/// the input's first timestamp. Cheaper than <see cref="Tempo"/>, which keeps the pitch. The factor is used as a fraction
/// with a denominator up to 1000.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/AudioFilters.cs" region="audio-tempo"/>
/// </example>
public sealed class Speed : AudioFilter
{
    private Resampler? _resampler;
    private int _virtualRate;
    private long _origin;
    private long _produced;
    private bool _started;

    /// <summary>Creates a speed change.</summary>
    /// <param name="factor">How much faster: 2 plays twice as fast and an octave up, 0.5 half as fast and an octave down; 0.25 to 4.</param>
    /// <param name="quality">The resampler's quality. Default <see cref="ResamplerQuality.Good"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">The factor is outside 0.25 to 4.</exception>
    public Speed(double factor, ResamplerQuality quality = ResamplerQuality.Good)
    {
        if (!(factor >= 0.25 && factor <= 4))
        {
            throw new ArgumentOutOfRangeException(nameof(factor), factor, "The speed factor must be from 0.25 to 4.");
        }

        Factor = factor;
        Quality = quality;
    }

    /// <summary>Gets how much faster the stream plays.</summary>
    public double Factor { get; }

    /// <summary>Gets the resampler's quality.</summary>
    public ResamplerQuality Quality { get; }

    private protected override long LatencySamples => _resampler is null ? 0 : _resampler.FilterLength / 2;

    private protected override void OnStart()
    {
        int numerator = (int)Math.Round(Factor * 1000), denominator = 1000;
        int g = Gcd(numerator, denominator);
        numerator /= g;
        denominator /= g;
        if (numerator == denominator)
        {
            return;
        }

        // Read the input as if it had been recorded at rate * factor, and resample that to the rate.
        int rg = Gcd(SampleRate * numerator, SampleRate * denominator);
        _virtualRate = SampleRate * numerator / rg;
        _resampler = new Resampler(_virtualRate, SampleRate * denominator / rg, Layout, Quality, null, SampleFormat.F32Planar, Context);
        OnReset();
    }

    private protected override void OnReset()
    {
        _resampler?.Reset();
        _started = false;
        _produced = 0;
    }

    private protected override void Process(AudioFrame frame)
    {
        if (_resampler is null)
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
            using AudioFrame relabelled = new(SampleFormat.F32Planar, Layout, _virtualRate, frame.SampleCount, MediaTime.Zero, Context);
            for (int c = 0; c < Layout.ChannelCount; c++)
            {
                frame.AsSpan<float>(c).CopyTo(relabelled.GetSpan<float>(c));
            }

            _resampler.Send(relabelled);
        }

        Take();
    }

    private protected override void OnFlush()
    {
        if (_resampler is null || !_started)
        {
            return;
        }

        _resampler.Flush();
        Take();
        _resampler.Reset();
        _started = false;
        _produced = 0;
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a;
    }

    private void Take()
    {
        while (_resampler!.TryReceive(out AudioFrame? output))
        {
            if (output.SampleRate == SampleRate)
            {
                output.Timestamp = TimeAt(_origin + _produced);
                _produced += output.SampleCount;
                Emit(output);
                continue;
            }

            // The resampler's output rate is the stream's rate times a whole number: relabel it to the stream's rate.
            using (output)
            {
                AudioFrame frame = NewFrame(Layout, output.SampleCount, _origin + _produced);
                for (int c = 0; c < Layout.ChannelCount; c++)
                {
                    output.AsSpan<float>(c).CopyTo(frame.GetSpan<float>(c));
                }

                _produced += frame.SampleCount;
                Emit(frame);
            }
        }
    }
}
