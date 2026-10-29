using System.Numerics;

using Egami.Video.Audio;
using Egami.Video.Audio.Filters;

namespace Egami.Video.Tests.Audio.Filters;

/// <summary>
/// The V9.1 stage B EQ gate (Design Q15): the measured response of every biquad kind, Butterworth order and the parametric
/// equalizer is within 0.1 dB of the analytic curve, the analog prototype mapped through the bilinear transform.
/// </summary>
public class EqualizerTests
{
    private const int Rate = 48000;

    // Multiples of 2 Hz, so half a second holds whole cycles.
    private static readonly double[] Frequencies = [40, 100, 250, 500, 800, 1000, 1250, 2000, 3150, 5000, 8000, 12500, 18000];

    public static TheoryData<BiquadKind, double, double, double> Biquads => new()
    {
        { BiquadKind.LowPass, 1000, Biquad.ButterworthQ, 0 },
        { BiquadKind.LowPass, 5000, 3, 0 },
        { BiquadKind.HighPass, 80, Biquad.ButterworthQ, 0 },
        { BiquadKind.HighPass, 2000, 0.5, 0 },
        { BiquadKind.BandPass, 1000, 2, 0 },
        { BiquadKind.Notch, 1000, 4, 0 },
        { BiquadKind.AllPass, 1500, 0.7, 0 },
        { BiquadKind.Peaking, 1000, 1.5, 6 },
        { BiquadKind.Peaking, 3150, 0.8, -12 },
        { BiquadKind.LowShelf, 200, Biquad.ButterworthQ, 6 },
        { BiquadKind.HighShelf, 4000, Biquad.ButterworthQ, -5 },
        { BiquadKind.HighShelf, 8000, 1.2, 9 },
    };

    [Theory]
    [MemberData(nameof(Biquads))]
    public void BiquadMatchesTheAnalyticCurve(BiquadKind kind, double frequency, double q, double gain)
    {
        foreach (double f in Frequencies)
        {
            double expected = Decibels(Analog(kind, Warp(f, frequency), q, gain));
            if (expected < -40)
            {
                continue; // Too deep in a stop band to measure with float samples.
            }

            using Biquad biquad = new(kind, frequency, q, gain);
            double measured = Measure(biquad, f);
            Assert.True(Math.Abs(measured - expected) < 0.1, $"{kind} {frequency} Hz at {f} Hz: {measured:F3} dB, expected {expected:F3} dB");
        }
    }

    [Theory]
    [InlineData(BiquadKind.LowPass, 1000, 2)]
    [InlineData(BiquadKind.LowPass, 1000, 3)]
    [InlineData(BiquadKind.LowPass, 2000, 4)]
    [InlineData(BiquadKind.LowPass, 4000, 5)]
    [InlineData(BiquadKind.LowPass, 3000, 8)]
    [InlineData(BiquadKind.HighPass, 500, 2)]
    [InlineData(BiquadKind.HighPass, 300, 6)]
    [InlineData(BiquadKind.HighPass, 200, 7)]
    [InlineData(BiquadKind.HighPass, 1000, 8)]
    public void ButterworthMatchesTheAnalyticCurve(BiquadKind kind, double frequency, int order)
    {
        foreach (double f in Frequencies)
        {
            double omega = Warp(f, frequency);
            if (kind == BiquadKind.HighPass)
            {
                omega = 1 / omega;
            }

            double expected = -10 * Math.Log10(1 + Math.Pow(omega, 2 * order));
            if (expected < -40)
            {
                continue;
            }

            using Butterworth filter = new(kind, frequency, order);
            double measured = Measure(filter, f);
            Assert.True(Math.Abs(measured - expected) < 0.1, $"order {order} {kind} at {f} Hz: {measured:F3} dB, expected {expected:F3} dB");
        }
    }

    [Fact]
    public void ButterworthIsMinus3DecibelsAtTheCorner()
    {
        for (int order = 2; order <= 8; order++)
        {
            using Butterworth filter = new(BiquadKind.LowPass, 1000, order);
            Assert.Equal(-3.01, Measure(filter, 1000), 1);
        }
    }

    [Fact]
    public void ParametricEqualizerAddsItsBands()
    {
        EqualizerBand[] bands =
        [
            new(BiquadKind.HighPass, 60, 0),
            new(BiquadKind.LowShelf, 150, 4),
            new(BiquadKind.Peaking, 1000, -6, 2),
            new(BiquadKind.HighShelf, 6000, 3),
        ];
        foreach (double f in Frequencies)
        {
            double expected = bands.Sum(b => Decibels(Analog(b.Kind, Warp(f, b.Frequency), b.Q, b.Gain)));
            using ParametricEqualizer equalizer = new(bands);
            Assert.True(Math.Abs(Measure(equalizer, f) - expected) < 0.1, $"at {f} Hz");
        }
    }

    [Fact]
    public void EmptyEqualizerAndZeroGainPeakPassThrough()
    {
        float[][] tone = FilterHarness.Tone(Rate, 0.1, 700);
        using ParametricEqualizer empty = new();
        Assert.Equal(tone, FilterHarness.Run(empty, tone, Rate));
        using Biquad flat = new(BiquadKind.Peaking, 1000, 1, 0);
        Assert.True(FilterHarness.DifferenceDb(tone, FilterHarness.Run(flat, tone, Rate)) < -130);
    }

    [Fact]
    public void BandwidthGivesTheCookbookQ()
    {
        Biquad biquad = Biquad.FromBandwidth(BiquadKind.Peaking, 1000, 1, 6);
        Assert.Equal(1.0, biquad.Bandwidth);
        Assert.Equal(1.4142, biquad.Q, 3);
        Assert.Null(new Biquad(BiquadKind.LowPass, 100).Bandwidth);
        Assert.Equal(6, Measure(biquad, 1000), 1);
    }

    [Fact]
    public void FiltersRefuseBadParameters()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Biquad(BiquadKind.LowPass, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Biquad(BiquadKind.LowPass, 100, q: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Biquad(BiquadKind.Peaking, 100, gain: double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => Biquad.FromBandwidth(BiquadKind.Peaking, 100, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Butterworth(BiquadKind.Peaking, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Butterworth(BiquadKind.LowPass, 100, 9));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ParametricEqualizer(new EqualizerBand(BiquadKind.Peaking, 100, 0, -1)));

        // A frequency at or above half the sample rate is refused on the first frame.
        using Biquad high = new(BiquadKind.LowPass, 30000);
        using AudioFrame frame = FilterHarness.Frames(FilterHarness.Tone(Rate, 0.01, 100), Rate)[0];
        Assert.Throws<ArgumentException>(() => high.Send(frame));
    }

    [Fact]
    public void SilenceAfterSignalDecaysToExactZero()
    {
        // The state is flushed below 1e-30 so a long silence doesn't run on denormals.
        using Butterworth filter = new(BiquadKind.HighPass, 40, 8);
        float[][] signal = FilterHarness.Tone(Rate, 0.2, 100);
        float[][] silence = [new float[Rate * 5], new float[Rate * 5]];
        float[][] output = FilterHarness.Run(filter, signal.Zip(silence, (s, z) => s.Concat(z).ToArray()).ToArray(), Rate);
        Assert.All(output, c => Assert.Equal(0f, c[^1]));
    }

    /// <summary>Returns the bilinear transform's analog frequency for a digital one, normalized to the filter's corner.</summary>
    private static double Warp(double f, double corner) => Math.Tan(Math.PI * f / Rate) / Math.Tan(Math.PI * corner / Rate);

    /// <summary>The cookbook's analog prototypes, H(s) at s = j omega.</summary>
    private static Complex Analog(BiquadKind kind, double omega, double q, double gain)
    {
        Complex s = new(0, omega);
        double a = Math.Pow(10, gain / 40);
        Complex s2 = s * s;
        return kind switch
        {
            BiquadKind.LowPass => 1 / (s2 + (s / q) + 1),
            BiquadKind.HighPass => s2 / (s2 + (s / q) + 1),
            BiquadKind.BandPass => (s / q) / (s2 + (s / q) + 1),
            BiquadKind.Notch => (s2 + 1) / (s2 + (s / q) + 1),
            BiquadKind.AllPass => (s2 - (s / q) + 1) / (s2 + (s / q) + 1),
            BiquadKind.Peaking => (s2 + (s * a / q) + 1) / (s2 + (s / (a * q)) + 1),
            BiquadKind.LowShelf => a * (s2 + (Math.Sqrt(a) / q * s) + a) / ((a * s2) + (Math.Sqrt(a) / q * s) + 1),
            _ => a * ((a * s2) + (Math.Sqrt(a) / q * s) + 1) / (s2 + (Math.Sqrt(a) / q * s) + a),
        };
    }

    private static double Decibels(Complex h) => 20 * Math.Log10(Math.Max(h.Magnitude, 1e-12));

    /// <summary>Runs a 1 s sine through a filter and returns the gain in dB over the last half second, once it has settled.</summary>
    private static double Measure(IAudioFilter filter, double frequency)
    {
        float[][] input = FilterHarness.Tone(Rate, 1, frequency, 0.25, channels: 1);
        float[][] output = FilterHarness.Run(filter, input, Rate);
        return 20 * Math.Log10(Rms(output[0].AsSpan(Rate / 2)) / Rms(input[0].AsSpan(Rate / 2)));
    }

    private static double Rms(ReadOnlySpan<float> samples)
    {
        double sum = 0;
        foreach (float s in samples)
        {
            sum += s * s;
        }

        return Math.Sqrt(sum / samples.Length);
    }
}
