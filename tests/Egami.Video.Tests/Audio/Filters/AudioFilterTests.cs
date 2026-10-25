using Egami.Video.Analysis;
using Egami.Video.Audio;
using Egami.Video.Audio.Filters;

using Fade = Egami.Video.Audio.Filters.Fade;

namespace Egami.Video.Tests.Audio.Filters;

/// <summary>V9.1 stage A: the filter model, gain, fades, crossfade, mixer, pan, timing filters, tempo and speed.</summary>
public class AudioFilterTests
{
    private const int Rate = 48000;

    [Fact]
    public void UnityGainIsBitExact()
    {
        float[][] signal = FilterHarness.Tone(Rate, 0.3, 440);
        using Gain gain = new();
        float[][] output = FilterHarness.Run(gain, signal, Rate, 777);
        Assert.True(signal[0].AsSpan().SequenceEqual(output[0]));
        Assert.True(signal[1].AsSpan().SequenceEqual(output[1]));
    }

    [Fact]
    public void GainScalesAndConvertsAnyFormat()
    {
        using AudioFrame s16 = new(SampleFormat.S16, ChannelLayout.Stereo, Rate, 100, MediaTime.FromSeconds(1));
        s16.GetSpan<short>()[0] = 16384;
        s16.GetSpan<short>()[1] = -16384;
        using Gain gain = Gain.FromFactor(0.5);
        gain.Send(s16);
        Assert.True(gain.TryReceive(out AudioFrame? output));
        using (output)
        {
            Assert.Equal(SampleFormat.F32Planar, output.Format);
            Assert.Equal(MediaTime.FromSeconds(1), output.Timestamp);
            Assert.Equal(0.25f, output.AsSpan<float>(0)[0]);
            Assert.Equal(-0.25f, output.AsSpan<float>(1)[0]);
        }

        Assert.Equal(-6.0206, Gain.FromFactor(0.5).Decibels, 3);
    }

    [Fact]
    public void GainChangesRampOver5Milliseconds()
    {
        float[][] ones = [Enumerable.Repeat(1f, 4800).ToArray()];
        using Gain gain = new();
        List<AudioFrame> frames = FilterHarness.Frames(ones, Rate, 1200);
        List<float> output = [];
        for (int i = 0; i < frames.Count; i++)
        {
            if (i == 1)
            {
                gain.Decibels = -6;
            }

            gain.Send(frames[i]);
            frames[i].Dispose();
            Assert.True(gain.TryReceive(out AudioFrame? frame));
            using (frame)
            {
                output.AddRange(frame.AsSpan<float>(0).ToArray());
            }
        }

        Assert.Equal(1f, output[1199]);
        Assert.InRange(output[1200], 0.99f, 1f);
        Assert.InRange(output[1200 + 120], 0.7f, 0.8f);
        Assert.Equal(0.501187f, output[1200 + 240], 5);
        for (int i = 1; i < output.Count; i++)
        {
            Assert.True(Math.Abs(output[i] - output[i - 1]) < 0.003, $"step of {output[i] - output[i - 1]} at {i}");
        }
    }

    [Fact]
    public void EnvelopeFollowsKeyframes()
    {
        float[][] ones = [Enumerable.Repeat(1f, Rate).ToArray()];
        using Gain gain = new()
        {
            Envelope = new GainEnvelope(
                new Keyframe(MediaTime.FromSeconds(0.25), 0),
                new Keyframe(MediaTime.FromSeconds(0.5), -20, Easing.Hold),
                new Keyframe(MediaTime.FromSeconds(0.75), -40)),
        };
        float[][] output = FilterHarness.Run(gain, ones, Rate);
        Assert.Equal(1f, output[0][0]);
        Assert.Equal(1f, output[0][12000]);
        Assert.Equal(Math.Pow(10, -0.5), output[0][18000], 4);
        Assert.Equal(0.1, output[0][24000], 5);
        Assert.Equal(0.1, output[0][35999], 5);
        Assert.Equal(0.01, output[0][36000], 5);
        Assert.Equal(0.01, output[0][47999], 5);
        Assert.Equal(-10, gain.Envelope.DecibelsAt(MediaTime.FromSeconds(0.375)), 6);
    }

    [Fact]
    public void EnvelopeToSilenceMovesInAmplitude()
    {
        GainEnvelope envelope = new(new Keyframe(MediaTime.Zero, 0), new Keyframe(MediaTime.FromSeconds(1), double.NegativeInfinity));
        Assert.Equal(SampleMath.ToDecibels(0.5), envelope.DecibelsAt(MediaTime.FromSeconds(0.5)), 9);
        Assert.Equal(double.NegativeInfinity, envelope.DecibelsAt(MediaTime.FromSeconds(2)));
        Assert.Throws<ArgumentException>(() => new GainEnvelope());
        Assert.Throws<ArgumentException>(() => new GainEnvelope(new Keyframe(MediaTime.Zero, double.NaN)));
    }

    [Theory]
    [InlineData(FadeCurve.Linear)]
    [InlineData(FadeCurve.EqualPower)]
    [InlineData(FadeCurve.Logarithmic)]
    [InlineData(FadeCurve.Exponential)]
    [InlineData(FadeCurve.SCurve)]
    public void FadesHitTheirEndLevelOnTheExactSample(FadeCurve curve)
    {
        float[][] ones = [Enumerable.Repeat(1f, Rate).ToArray()];
        using Fade fadeIn = new(FadeDirection.In, MediaTime.FromSeconds(0.25), MediaTime.FromSeconds(0.5), curve);
        float[][] a = FilterHarness.Run(fadeIn, ones, Rate, 1000);
        Assert.Equal(0f, a[0][11999]);
        Assert.True(a[0][12001] > 0f && a[0][35999] <= 1f);
        Assert.Equal(1f, a[0][36000]);
        for (int i = 12001; i < 36000; i++)
        {
            Assert.True(a[0][i] >= a[0][i - 1], $"{curve} falls at {i}");
        }

        using Fade fadeOut = new(FadeDirection.Out, MediaTime.FromSeconds(0.25), MediaTime.FromSeconds(0.5), curve);
        float[][] b = FilterHarness.Run(fadeOut, ones, Rate, 1000);
        Assert.Equal(1f, b[0][11999]);
        Assert.Equal(1f, b[0][12000]);
        Assert.Equal(0f, b[0][36000]);
        Assert.Equal(0f, b[0][47999]);
    }

    [Fact]
    public void EqualPowerCrossfadeKeepsPowerOfUnrelatedSignals()
    {
        // Two independent noises: the power through an equal-power overlap stays within 1 dB.
        float[][] first = Noise(Rate, 1, 1), second = Noise(Rate, 1, 2);
        using Crossfade crossfade = new(MediaTime.FromSeconds(0.5));
        float[][] output = RunCrossfade(crossfade, first, second);
        Assert.Equal(Rate * 3 / 2, output[0].Length);
        double before = Power(output[0].AsSpan(0, 24000)), middle = Power(output[0].AsSpan(30000, 12000));
        Assert.InRange(10 * Math.Log10(middle / before), -1, 1);
    }

    [Fact]
    public void CrossfadeShortensToTheShorterInput()
    {
        float[][] first = FilterHarness.Tone(Rate, 0.1, 300, channels: 1), second = FilterHarness.Tone(Rate, 0.05, 300, channels: 1);
        using Crossfade crossfade = new(MediaTime.FromSeconds(1));
        float[][] output = RunCrossfade(crossfade, first, second);
        Assert.Equal(first[0].Length, output[0].Length);
    }

    [Fact]
    public void MixerPlacesInputsByTimestamp()
    {
        using AudioMixer mixer = new(Rate, ChannelLayout.Mono, 2);
        List<AudioFrame> a = FilterHarness.Frames([Enumerable.Repeat(0.25f, 4800).ToArray()], Rate, 1000);
        List<AudioFrame> b = FilterHarness.Frames([Enumerable.Repeat(0.5f, 2400).ToArray()], Rate, 1000, MediaTime.FromSeconds(0.05));
        foreach (AudioFrame frame in a)
        {
            mixer.Send(0, frame);
            frame.Dispose();
        }

        foreach (AudioFrame frame in b)
        {
            mixer.Send(1, frame);
            frame.Dispose();
        }

        mixer.Flush();
        float[][] output = Collect(mixer.TryReceive);
        Assert.Equal(4800, output[0].Length);
        Assert.Equal(0.25f, output[0][2399]);
        Assert.Equal(0.75f, output[0][2400]);
        Assert.Equal(0.75f, output[0][4799]);
    }

    [Theory]
    [InlineData(MixDuration.Longest, 4800)]
    [InlineData(MixDuration.Shortest, 2400)]
    [InlineData(MixDuration.First, 2400)]
    public void MixerDurationModes(MixDuration duration, int length)
    {
        using AudioMixer mixer = new(Rate, ChannelLayout.Mono, 2) { Duration = duration, Normalize = true };
        using AudioFrame a = new(SampleFormat.F32Planar, ChannelLayout.Mono, Rate, 2400, MediaTime.Zero);
        using AudioFrame b = new(SampleFormat.F32Planar, ChannelLayout.Mono, Rate, 4800, MediaTime.Zero);
        a.GetSpan<float>().Fill(1);
        b.GetSpan<float>().Fill(1);
        mixer.Send(0, a);
        mixer.Complete(0);
        mixer.Send(1, b);
        mixer.Complete(1);
        float[][] output = Collect(mixer.TryReceive);
        Assert.Equal(length, output[0].Length);
        Assert.Equal(1f, output[0][0]);
    }

    [Fact]
    public void MixerResamplesAndRemixesOnEntry()
    {
        using AudioMixer mixer = new(Rate, ChannelLayout.Stereo, 2);
        float[][] mono = FilterHarness.Tone(44100, 0.5, 1000, channels: 1);
        foreach (AudioFrame frame in FilterHarness.Frames(mono, 44100))
        {
            mixer.Send(0, frame);
            frame.Dispose();
        }

        mixer.Complete(1);
        mixer.Flush();
        float[][] output = Collect(mixer.TryReceive);
        Assert.Equal(2, output.Length);
        Assert.InRange(output[0].Length, 23999, 24001);
        Assert.Equal(1000, FilterHarness.Frequency(output[0], Rate), 0);
    }

    [Fact]
    public void PanPlacesMonoAndSwapsAndInverts()
    {
        float[][] ones = [Enumerable.Repeat(1f, 1000).ToArray()];
        using Pan left = new(-1);
        float[][] a = FilterHarness.Run(left, ones, Rate);
        Assert.Equal(1f, a[0][0], 6);
        Assert.Equal(0f, a[1][0], 6);

        float[][] stereo = [Enumerable.Repeat(1f, 1000).ToArray(), Enumerable.Repeat(0.5f, 1000).ToArray()];
        using Pan pan = new() { Swap = true, InvertRight = true, Balance = -0.5 };
        float[][] b = FilterHarness.Run(pan, stereo, Rate);
        Assert.Equal(0.25f, b[0][0], 6);
        Assert.Equal(-1f, b[1][0], 6);

        using Pan centre = new();
        float[][] c = FilterHarness.Run(centre, stereo, Rate);
        Assert.Equal(1f, c[0][0]);
        Assert.Equal(0.5f, c[1][0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => new Pan(1.5));
    }

    [Fact]
    public void TrimCutsOnExactSamplesAndRetimeStartsAtZero()
    {
        float[][] ramp = [Enumerable.Range(0, Rate).Select(i => (float)i / Rate).ToArray()];
        using AudioFilterChain chain = new AudioFilterChain(Rate, ChannelLayout.Mono)
            .Add(new Trim(MediaTime.FromSeconds(0.25), MediaTime.FromSeconds(0.5)))
            .Add(new Retime());
        float[][] output = FilterHarness.Run(chain, FilterHarness.Frames(ramp, Rate, 1000, MediaTime.FromSeconds(10)).Select(f => Shift(f, -10)), out MediaTime start);
        Assert.Equal(MediaTime.Zero, start);
        Assert.Equal(12000, output[0].Length);
        Assert.Equal(ramp[0][12000], output[0][0]);
        Assert.Equal(ramp[0][23999], output[0][^1]);
    }

    [Fact]
    public void PadAddsSilence()
    {
        float[][] ones = [Enumerable.Repeat(1f, 1000).ToArray()];
        using Pad pad = new(MediaTime.FromSeconds(0.1));
        Assert.Equal(1000 + 4800, FilterHarness.Run(pad, ones, Rate)[0].Length);
        using Pad toLength = Pad.ToLength(MediaTime.FromSeconds(1));
        float[][] output = FilterHarness.Run(toLength, ones, Rate);
        Assert.Equal(Rate, output[0].Length);
        Assert.Equal(0f, output[0][^1]);
    }

    [Fact]
    public void DelayShiftsEachChannel()
    {
        float[][] ones = [Enumerable.Repeat(1f, 1000).ToArray(), Enumerable.Repeat(1f, 1000).ToArray()];
        using Delay delay = new([MediaTime.Zero, new MediaTime(10, new Rational(1, Rate))]);
        float[][] output = FilterHarness.Run(delay, ones, Rate, 300);
        Assert.Equal(1010, output[0].Length);
        Assert.Equal(1f, output[0][0]);
        Assert.Equal(0f, output[0][1000]);
        Assert.Equal(0f, output[1][9]);
        Assert.Equal(1f, output[1][10]);
        Assert.Equal(1f, output[1][1009]);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(1.25)]
    [InlineData(2)]
    [InlineData(0.8)]
    public void SpeedChangesLengthAndPitch(double factor)
    {
        float[][] tone = FilterHarness.Tone(Rate, 1, 500, channels: 1);
        using Speed speed = new(factor);
        float[][] output = FilterHarness.Run(speed, tone, Rate);
        Assert.InRange(output[0].Length, (Rate / factor) - 1, (Rate / factor) + 1);
        Assert.Equal(500 * factor, FilterHarness.Frequency(output[0].AsSpan(1000, output[0].Length - 2000), Rate), 500 * factor * 0.01);
    }

    [Theory]
    [InlineData(0.25, TempoPreset.Music)]
    [InlineData(0.5, TempoPreset.Music)]
    [InlineData(0.8, TempoPreset.Speech)]
    [InlineData(1.25, TempoPreset.Music)]
    [InlineData(1.5, TempoPreset.Speech)]
    [InlineData(2, TempoPreset.Music)]
    [InlineData(4, TempoPreset.Speech)]
    public void TempoKeepsPitchAndExactLength(double factor, TempoPreset preset)
    {
        float[][] tone = FilterHarness.Tone(Rate, 2, 440, 0.5, channels: 2);
        using Tempo tempo = new(factor, preset);
        float[][] output = FilterHarness.Run(tempo, FilterHarness.Frames(tone, Rate, 1024, MediaTime.FromSeconds(3)), out MediaTime start);
        Assert.Equal(MediaTime.FromSeconds(3), start);
        Assert.Equal((int)Math.Round(2 * Rate / factor), output[0].Length);
        Assert.Equal(440, FilterHarness.Frequency(output[0], Rate), 440 * 0.01);
        Assert.Equal(880, FilterHarness.Frequency(output[1], Rate), 880 * 0.01);

        // Level kept: the RMS of the middle within 1 dB of the input's.
        int quarter = output[0].Length / 4;
        double level = 10 * Math.Log10(Power(output[0].AsSpan(quarter, 2 * quarter)) / Power(tone[0]));
        Assert.InRange(level, -1, 1);
        Assert.True(tempo.Latency > MediaTime.Zero);
    }

    [Fact]
    public void TempoOfOnePassesThrough()
    {
        float[][] tone = FilterHarness.Tone(Rate, 0.2, 440);
        using Tempo tempo = new(1);
        float[][] output = FilterHarness.Run(tempo, tone, Rate);
        Assert.True(tone[0].AsSpan().SequenceEqual(output[0]));
    }

    [Fact]
    public void ChainRunsFiltersInOrderAndTakesCustomFilters()
    {
        float[][] ones = [Enumerable.Repeat(1f, 4800).ToArray()];
        using AudioFilterChain chain = new AudioFilterChain(Rate, ChannelLayout.Mono)
            .Add(new Gain(-6))
            .Add(new Negate())
            .Add(new Pan(1));
        float[][] output = FilterHarness.Run(chain, ones, Rate, 1000);
        Assert.Equal(2, output.Length);
        Assert.Equal(0f, output[0][100], 6);
        Assert.Equal(-0.501187f, output[1][100], 5);
        Assert.Equal(3, chain.Filters.Count);
        Assert.Throws<ArgumentException>(() => chain.Add(chain.Filters[0]));
    }

    [Fact]
    public void FiltersRefuseAnotherRateOrLayout()
    {
        using Gain gain = new();
        using AudioFrame a = new(SampleFormat.F32Planar, ChannelLayout.Stereo, Rate, 10, MediaTime.Zero);
        using AudioFrame b = new(SampleFormat.F32Planar, ChannelLayout.Mono, Rate, 10, MediaTime.Zero);
        gain.Send(a);
        Assert.Throws<ArgumentException>(() => gain.Send(b));
        using AudioFilterChain chain = new(44100, ChannelLayout.Stereo);
        Assert.Throws<ArgumentException>(() => chain.Send(a));
    }

    [Fact]
    public void ResetDropsHeldInput()
    {
        float[][] tone = FilterHarness.Tone(Rate, 0.5, 440);
        using Tempo tempo = new(1.5);
        foreach (AudioFrame frame in FilterHarness.Frames(tone, Rate))
        {
            tempo.Send(frame);
            frame.Dispose();
        }

        tempo.Reset();
        Assert.False(tempo.TryReceive(out _));
        float[][] output = FilterHarness.Run(tempo, tone, Rate);
        Assert.Equal((int)Math.Round(tone[0].Length / 1.5), output[0].Length);
    }

    [Fact]
    public void TestSourcesRunThroughAChain()
    {
        using AudioFilterChain chain = new AudioFilterChain(Rate, ChannelLayout.Stereo)
            .Add(new Fade(FadeDirection.In, MediaTime.Zero, MediaTime.FromSeconds(0.1)))
            .Add(new Gain(-3))
            .Add(new Tempo(1.1))
            .Add(new Pan { Balance = -0.2 })
            .Add(new Fade(FadeDirection.Out, MediaTime.FromSeconds(0.8), MediaTime.FromSeconds(0.1)));
        float[][] output = FilterHarness.Run(chain, TestSources.Noise(TestAudioFormat.Default, MediaTime.FromSeconds(1)), out _);
        Assert.Equal((int)Math.Round(Rate / 1.1), output[0].Length);
        Assert.All(output[0], s => Assert.True(float.IsFinite(s)));
    }

    [Fact]
    public void SteadyChainAllocatesNoSampleBuffers()
    {
        // The Q15 allocation budget: a five-filter chain over 60 s of 48 kHz stereo allocates only the small frame objects,
        // never sample memory (which comes from the pool), once it runs.
        using AudioFilterChain chain = new AudioFilterChain(Rate, ChannelLayout.Stereo)
            .Add(new Gain(-3))
            .Add(new Fade(FadeDirection.In, MediaTime.Zero, MediaTime.FromSeconds(1)))
            .Add(new Pan { Balance = 0.1 })
            .Add(new Delay(MediaTime.FromSeconds(0.01)))
            .Add(new Fade(FadeDirection.Out, MediaTime.FromSeconds(58), MediaTime.FromSeconds(2), FadeCurve.EqualPower));
        using AudioFrame input = new(SampleFormat.F32Planar, ChannelLayout.Stereo, Rate, 1024, MediaTime.Zero);
        long allocated = 0;
        int frames = 60 * Rate / 1024;
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

        double perFrame = (double)allocated / (frames - 100);
        Assert.True(perFrame < 256, $"{perFrame:F0} bytes per frame; a frame of samples is {1024 * 2 * 4}");
    }

    private static AudioFrame Shift(AudioFrame frame, double seconds)
    {
        frame.Timestamp += MediaTime.FromSeconds(seconds);
        return frame;
    }

    private static float[][] RunCrossfade(Crossfade crossfade, float[][] first, float[][] second)
    {
        foreach ((int input, float[][] signal) in new[] { (0, first), (1, second) })
        {
            foreach (AudioFrame frame in FilterHarness.Frames(signal, Rate, 1000))
            {
                crossfade.Send(input, frame);
                frame.Dispose();
            }

            crossfade.Complete(input);
        }

        return Collect(crossfade.TryReceive);
    }

    private delegate bool Receive([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out AudioFrame? frame);

    private static float[][] Collect(Receive receive)
    {
        List<AudioFrame> frames = [];
        while (receive(out AudioFrame? frame))
        {
            frames.Add(frame);
        }

        FilterHarness.CheckContiguous(frames);
        float[][] result = FilterHarness.Join(frames);
        frames.ForEach(f => f.Dispose());
        return result;
    }

    private static float[][] Noise(int rate, double seconds, int seed)
    {
        Random random = new(seed);
        return [Enumerable.Range(0, (int)(rate * seconds)).Select(_ => (float)((random.NextDouble() * 2) - 1) * 0.3f).ToArray()];
    }

    private static double Power(ReadOnlySpan<float> samples)
    {
        double sum = 0;
        foreach (float s in samples)
        {
            sum += s * s;
        }

        return sum / samples.Length;
    }

    /// <summary>A filter written against the interface only.</summary>
    private sealed class Negate : IAudioFilter
    {
        private readonly Queue<AudioFrame> _ready = new();

        public MediaTime Latency => MediaTime.Zero;

        public void Send(AudioFrame frame)
        {
            AudioFrame copy = frame.Convert(SampleFormat.F32Planar);
            for (int c = 0; c < copy.ChannelCount; c++)
            {
                Span<float> s = copy.GetSpan<float>(c);
                for (int i = 0; i < s.Length; i++)
                {
                    s[i] = -s[i];
                }
            }

            _ready.Enqueue(copy);
        }

        public bool TryReceive([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out AudioFrame? frame) => _ready.TryDequeue(out frame);

        public void Flush()
        {
        }

        public void Reset() => _ready.Clear();

        public void Dispose()
        {
        }
    }
}
