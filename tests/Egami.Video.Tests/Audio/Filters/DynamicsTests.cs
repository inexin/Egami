using Egami.Video.Analysis;
using Egami.Video.Audio;
using Egami.Video.Audio.Filters;

namespace Egami.Video.Tests.Audio.Filters;

/// <summary>The V9.1 stage B dynamics: compressor and its sidechain, gate and limiter.</summary>
public class DynamicsTests
{
    private const int Rate = 48000;

    [Theory]
    [InlineData(-6, -18, 4, 0, -15)]
    [InlineData(-12, -24, 2, 0, -18)]
    [InlineData(-6, -18, 4, 6, -15)]
    [InlineData(-21, -18, 4, 6, -21)]
    [InlineData(-30, -18, 4, 0, -30)]
    public void CompressorFollowsItsStaticCurve(double input, double threshold, double ratio, double knee, double expected)
    {
        // A sine's RMS level in dBFS is the detector's; the output settles on the curve within the gain ripple.
        double amplitude = Math.Sqrt(2) * Math.Pow(10, input / 20);
        float[][] tone = FilterHarness.Tone(Rate, 1, 1000, amplitude, channels: 1);
        using Compressor compressor = new() { Threshold = threshold, Ratio = ratio, Knee = knee, Attack = MediaTime.FromSeconds(0.005), Release = MediaTime.FromSeconds(0.05) };
        float[][] output = FilterHarness.Run(compressor, tone, Rate);
        Assert.Equal(expected, RmsDb(output[0].AsSpan(Rate / 2)), 0.3);
    }

    [Fact]
    public void SoftKneeBlendsIntoTheRatio()
    {
        using Compressor compressor = new() { Threshold = -20, Ratio = 4, Knee = 10 };
        float[][] tone = FilterHarness.Tone(Rate, 1, 1000, Math.Sqrt(2) * Math.Pow(10, -20 / 20.0), channels: 1);
        float[][] output = FilterHarness.Run(compressor, tone, Rate);

        // At the threshold the knee is half through: (1/4 - 1) * 5^2 / 20 = -0.94 dB.
        Assert.Equal(-20.94, RmsDb(output[0].AsSpan(Rate / 2)), 0.2);
    }

    [Fact]
    public void CompressorBelowItsThresholdIsBitExactAndMakeupAdds()
    {
        float[][] quiet = FilterHarness.Tone(Rate, 0.5, 440, 0.01);
        using (Compressor compressor = new())
        {
            Assert.Equal(quiet, FilterHarness.Run(compressor, quiet, Rate));
        }

        using Compressor makeup = new() { Makeup = 6 };
        float[][] louder = FilterHarness.Run(makeup, quiet, Rate);
        Assert.Equal(20 * Math.Log10(2), RmsDb(louder[0]) - RmsDb(quiet[0]), 0.05);
    }

    [Fact]
    public void CompressorAttacksAndReleasesAtItsTimes()
    {
        // A step from -40 to -6 dBFS: the gain is 63% of the way down one attack in, and back 63% one release after the drop.
        float[][] step = [new float[Rate * 2]];
        for (int i = 0; i < step[0].Length; i++)
        {
            double a = i is >= Rate / 2 and < Rate ? 0.5 : 0.01;
            step[0][i] = (float)(a * Math.Sign(Math.Sin(2 * Math.PI * 100 * i / Rate) + 1e-9));
        }

        using Compressor compressor = new() { Threshold = -20, Ratio = 10, Knee = 0, Detection = LevelDetection.Peak, Attack = MediaTime.FromSeconds(0.01), Release = MediaTime.FromSeconds(0.1) };
        float[][] output = FilterHarness.Run(compressor, step, Rate);
        double Gain(int i) => 20 * Math.Log10(Math.Abs(output[0][i] / step[0][i]));
        double full = Gain((Rate / 2) + 4000);
        Assert.Equal((-6 + 20) * (0.1 - 1), full, 0.1);
        Assert.Equal(full * 0.632, Gain((Rate / 2) + 480), 0.4);
        Assert.Equal(full * 0.368, Gain(Rate + 4800), 0.4);
    }

    [Fact]
    public void SidechainDucksMusicUnderAVoice()
    {
        float[][] music = FilterHarness.Tone(Rate, 2, 220, 0.25, channels: 2);
        float[][] voice = [new float[Rate * 2]];
        for (int i = Rate / 2; i < Rate; i++)
        {
            voice[0][i] = (float)(0.5 * Math.Sin(2 * Math.PI * 300 * i / Rate));
        }

        using Compressor ducker = new() { Sidechain = true, Threshold = -30, Ratio = 10, Attack = MediaTime.FromSeconds(0.01), Release = MediaTime.FromSeconds(0.1) };
        List<AudioFrame> output = [];
        List<AudioFrame> main = FilterHarness.Frames(music, Rate, 1024), side = FilterHarness.Frames(voice, Rate, 700);

        // The music runs ahead; its frames wait for the sidechain.
        foreach (AudioFrame frame in main)
        {
            ducker.Send(frame);
            frame.Dispose();
        }

        Assert.False(ducker.TryReceive(out _));
        foreach (AudioFrame frame in side)
        {
            ducker.SendSidechain(frame);
            frame.Dispose();
            while (ducker.TryReceive(out AudioFrame? ready))
            {
                output.Add(ready);
            }
        }

        ducker.CompleteSidechain();
        ducker.Flush();
        while (ducker.TryReceive(out AudioFrame? ready))
        {
            output.Add(ready);
        }

        FilterHarness.CheckContiguous(output);
        float[][] result = FilterHarness.Join(output);
        output.ForEach(f => f.Dispose());
        Assert.Equal(music[0].Length, result[0].Length);
        Assert.Equal(music[0].AsSpan(0, Rate / 4).ToArray(), result[0].AsSpan(0, Rate / 4).ToArray());
        double ducked = RmsDb(result[0].AsSpan((Rate / 2) + 4800, Rate / 4)) - RmsDb(music[0].AsSpan((Rate / 2) + 4800, Rate / 4));
        Assert.True(ducked < -15, $"ducked by {ducked:F1} dB");
        double after = RmsDb(result[0].AsSpan((Rate * 3) / 2, Rate / 4)) - RmsDb(music[0].AsSpan((Rate * 3) / 2, Rate / 4));
        Assert.True(after > -0.1, $"recovered to {after:F2} dB");
    }

    [Fact]
    public void SidechainNeedsSidechainOnAndTheSameRate()
    {
        using AudioFrame frame = FilterHarness.Frames(FilterHarness.Tone(Rate, 0.01, 100), Rate)[0];
        using Compressor plain = new();
        Assert.Throws<InvalidOperationException>(() => plain.SendSidechain(frame));
        using Compressor ducker = new() { Sidechain = true };
        ducker.Send(frame);
        using AudioFrame other = FilterHarness.Frames(FilterHarness.Tone(44100, 0.01, 100), 44100)[0];
        Assert.Throws<ArgumentException>(() => ducker.SendSidechain(other));
    }

    [Fact]
    public void GateTurnsQuietPartsDown()
    {
        float[][] signal = [new float[Rate * 2]];
        for (int i = 0; i < signal[0].Length; i++)
        {
            double a = i < Rate ? 0.3 : 0.001; // -10 dBFS RMS, then -60.
            signal[0][i] = (float)(a * Math.Sin(2 * Math.PI * 500 * i / Rate));
        }

        using Gate gate = new() { Threshold = -40, Range = -30 };
        float[][] output = FilterHarness.Run(gate, signal, Rate);
        Assert.Equal(RmsDb(signal[0].AsSpan(Rate / 4, Rate / 2)), RmsDb(output[0].AsSpan(Rate / 4, Rate / 2)), 0.01);
        Assert.Equal(-30, RmsDb(output[0].AsSpan(Rate * 3 / 2)) - RmsDb(signal[0].AsSpan(Rate * 3 / 2)), 0.2);
    }

    [Fact]
    public void LimiterNeverGoesOverItsCeiling()
    {
        Random random = new(5);
        float[][] loud = [new float[Rate * 2], new float[Rate * 2]];
        for (int c = 0; c < 2; c++)
        {
            for (int i = 0; i < loud[c].Length; i++)
            {
                double burst = (i / 4800) % 3 == 0 ? 4 : 0.5;
                loud[c][i] = (float)(burst * ((random.NextDouble() * 2) - 1));
            }
        }

        using Limiter limiter = new() { Ceiling = -1 };
        List<AudioFrame> frames = FilterHarness.Frames(loud, Rate, 1000, MediaTime.FromSeconds(1.5));
        float[][] output = FilterHarness.Run(limiter, frames, out MediaTime start);
        Assert.Equal(MediaTime.FromSeconds(1.5), start);
        Assert.Equal(new MediaTime(240, new Rational(1, Rate)), limiter.Latency);
        Assert.Equal(loud[0].Length, output[0].Length);
        float ceiling = (float)Math.Pow(10, -1 / 20.0);
        Assert.All(output, c => Assert.True(c.Max(Math.Abs) <= ceiling));

        // Between bursts the gain comes back to unity.
        Assert.Equal(1, output[0][14000] / loud[0][14000], 0.03);
    }

    [Fact]
    public void LimiterPassesQuietAudioUnchangedAndDelaysOnlyInternally()
    {
        float[][] quiet = FilterHarness.Tone(Rate, 0.3, 440, 0.5);
        using Limiter limiter = new();
        Assert.Equal(quiet, FilterHarness.Run(limiter, quiet, Rate, size: 100));

        // Shorter than the lookahead, all of it comes out at the flush.
        float[][] tiny = FilterHarness.Tone(Rate, 0.001, 440, 0.5);
        Assert.Equal(tiny, FilterHarness.Run(limiter, tiny, Rate));
    }

    [Fact]
    public void TruePeakLimiterCatchesPeaksBetweenSamples()
    {
        // A quarter-rate sine at 45 degrees: every sample is 0.707 of the true peak of 1.0.
        float[][] signal = [new float[Rate]];
        for (int i = 0; i < Rate; i++)
        {
            signal[0][i] = (float)Math.Sin((Math.PI / 2 * i) + (Math.PI / 4));
        }

        using Limiter samplePeak = new() { Ceiling = -1 };
        Assert.Equal(signal, FilterHarness.Run(samplePeak, signal, Rate));

        using Limiter truePeak = new() { Ceiling = -1, TruePeak = true };
        float[][] output = FilterHarness.Run(truePeak, signal, Rate);
        Assert.Equal(new MediaTime(246, new Rational(1, Rate)), truePeak.Latency);
        Assert.Equal(signal[0].Length, output[0].Length);
        LoudnessMeter meter = new();
        foreach (AudioFrame frame in FilterHarness.Frames(output, Rate))
        {
            meter.Add(frame);
            frame.Dispose();
        }

        Assert.True(meter.TruePeak < -0.9, $"true peak {meter.TruePeak:F2} dBTP");
        Assert.True(meter.TruePeak > -1.5, $"true peak {meter.TruePeak:F2} dBTP");
    }

    [Fact]
    public void SteadyEqAndDynamicsChainAllocatesNoSampleBuffers()
    {
        // The best of three runs: tests running alongside can empty the shared sample pool for a moment.
        double perFrame = Enumerable.Range(0, 3).Min(_ => SteadyChainBytesPerFrame());
        Assert.True(perFrame < 256, $"{perFrame:F0} bytes per frame");
    }

    private static double SteadyChainBytesPerFrame()
    {
        using AudioFilterChain chain = new AudioFilterChain(Rate, ChannelLayout.Stereo)
            .Add(new Butterworth(BiquadKind.HighPass, 80, 4))
            .Add(new ParametricEqualizer(new EqualizerBand(BiquadKind.Peaking, 3000, 3, 1), new EqualizerBand(BiquadKind.HighShelf, 8000, -2)))
            .Add(new Compressor())
            .Add(new Gate())
            .Add(new Limiter { TruePeak = true });
        using AudioFrame input = new(SampleFormat.F32Planar, ChannelLayout.Stereo, Rate, 1024, MediaTime.Zero);
        long allocated = 0;
        int frames = 20 * Rate / 1024;
        for (int i = 0; i < frames; i++)
        {
            input.Timestamp = new MediaTime(i * 1024L, new Rational(1, Rate));
            long before = GC.GetAllocatedBytesForCurrentThread();
            chain.Send(input);
            while (chain.TryReceive(out AudioFrame? output))
            {
                output.Dispose();
            }

            if (i >= 100)
            {
                allocated += GC.GetAllocatedBytesForCurrentThread() - before;
            }
        }

        return (double)allocated / (frames - 100);
    }

    [Fact]
    public void DynamicsRefuseBadParameters()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Compressor { Ratio = 0.5 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new Compressor { Threshold = 3 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new Compressor { Attack = MediaTime.FromSeconds(-1) });
        Assert.Throws<ArgumentOutOfRangeException>(() => new Gate { Range = 6 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new Limiter { Ceiling = double.NaN });
        Assert.Throws<ArgumentOutOfRangeException>(() => new Limiter { Release = MediaTime.Zero });
    }

    [Fact]
    public void TestSourcesRunThroughTheDynamics()
    {
        foreach (IAudioFilter filter in new IAudioFilter[] { new Compressor { Makeup = 12 }, new Gate(), new Limiter { TruePeak = true } })
        {
            using (filter)
            {
                float[][] output = FilterHarness.Run(filter, TestSources.Noise(TestAudioFormat.Default, MediaTime.FromSeconds(1), NoiseColor.Pink, -10), out _);
                Assert.Equal(Rate, output[0].Length);
                Assert.All(output, c => Assert.All(c, s => Assert.True(float.IsFinite(s))));
            }
        }
    }

    private static double RmsDb(ReadOnlySpan<float> samples)
    {
        double sum = 0;
        foreach (float s in samples)
        {
            sum += s * s;
        }

        return 10 * Math.Log10(sum / samples.Length);
    }
}
