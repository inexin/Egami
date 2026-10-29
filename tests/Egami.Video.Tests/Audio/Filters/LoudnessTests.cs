using Egami.Video.Audio;
using Egami.Video.Audio.Filters;

namespace Egami.Video.Tests.Audio.Filters;

/// <summary>
/// The V9.1 stage C gates (Design Q15): normalized output within 0.5 LU of the target and under its true-peak ceiling, on the
/// EBU Tech 3341 and 3342 cases (generated from the specs' descriptions, as the meter's own tests do), and the silence remover.
/// </summary>
public class LoudnessTests
{
    private const int Rate = 48000;

    public static TheoryData<string, double[], double[]> Cases => new()
    {
        { "3341 case 1", [-23], [20] },
        { "3341 case 2", [-33], [20] },
        { "3341 case 3", [-36, -23, -36], [10, 60, 10] },
        { "3341 case 4", [-72, -36, -23, -36, -72], [10, 10, 60, 10, 10] },
        { "3341 case 5", [-26, -20, -26], [20, 20.1, 20] },
        { "3342 case 1", [-20, -30], [20, 20] },
        { "3342 case 3", [-40, -20], [20, 20] },
        { "3342 case 4", [-50, -35, -20, -35, -50], [20, 20, 20, 20, 20] },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void LinearNormalizationHitsTheTarget(string name, double[] levels, double[] seconds)
    {
        foreach (LoudnessTarget target in new[] { LoudnessTarget.EbuR128, LoudnessTarget.Streaming, LoudnessTarget.AtscA85 })
        {
            float[][] signal = Tone(levels, seconds);
            LoudnessReport measured = Measure(signal);
            using LoudnessNormalizer normalizer = new(target, measured);
            LoudnessReport output = Measure(FilterHarness.Run(normalizer, signal, Rate));
            Assert.True(Math.Abs(output.Integrated - target.Integrated) < 0.5, $"{name} to {target.Integrated}: {output.Integrated:F2} LUFS");
            Assert.True(output.TruePeak <= target.TruePeak + 0.1, $"{name} to {target.Integrated}: {output.TruePeak:F2} dBTP");
            Assert.Equal(measured.Range, output.Range, 0.2);
        }
    }

    [Fact]
    public void LimiterRunsOnlyWhenTheGainWouldClip()
    {
        // A quiet tone with sparse loud clicks: lifting it to -14 LUFS takes the clicks far over -1 dBTP.
        float[][] signal = Tone([-30], [20]);
        for (int i = Rate; i < signal[0].Length; i += Rate)
        {
            for (int c = 0; c < 2; c++)
            {
                signal[c][i] = 0.5f;
                signal[c][i + 1] = -0.5f;
            }
        }

        LoudnessReport measured = Measure(signal);
        using LoudnessNormalizer quiet = new(LoudnessTarget.EbuR128, Measure(Tone([-30], [20])));
        Assert.False(quiet.Limits);
        Assert.Equal(7, quiet.Gain, 0.1);

        using LoudnessNormalizer loud = new(LoudnessTarget.Streaming, measured);
        Assert.True(loud.Limits);
        float[][] result = FilterHarness.Run(loud, signal, Rate);
        Assert.Equal(signal[0].Length, result[0].Length);
        LoudnessReport output = Measure(result);
        Assert.True(Math.Abs(output.Integrated - -14) < 0.5, $"{output.Integrated:F2} LUFS");
        Assert.True(output.TruePeak <= -0.9, $"{output.TruePeak:F2} dBTP");
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void DynamicNormalizationHitsTheTargetAndRange(string name, double[] levels, double[] seconds)
    {
        LoudnessTarget target = LoudnessTarget.EbuR128;
        float[][] signal = Tone(levels, seconds);
        using LoudnessNormalizer normalizer = LoudnessNormalizer.Dynamic(target);
        float[][] result = FilterHarness.Run(normalizer, signal, Rate);
        Assert.Equal(signal[0].Length, result[0].Length);
        LoudnessReport output = Measure(result);

        // One pass cannot know what comes later, so a quiet half after a loud one ends up under the target: the two-pass gate
        // of 0.5 LU holds for steady programmes, 1.5 LU for the 3342 steps, and the range comes within 1.5 LU of the target's.
        double tolerance = name.StartsWith("3342", StringComparison.Ordinal) ? 1.5 : 0.5;
        Assert.True(Math.Abs(output.Integrated - target.Integrated) < tolerance, $"{name}: {output.Integrated:F2} LUFS");
        Assert.True(output.TruePeak <= target.TruePeak + 0.1, $"{name}: {output.TruePeak:F2} dBTP");
        Assert.True(output.Range <= target.Range + 1.5, $"{name}: {output.Range:F1} LU");
    }

    [Fact]
    public void SilenceStaysSilent()
    {
        float[][] silence = [new float[Rate * 5], new float[Rate * 5]];
        using LoudnessNormalizer linear = new(LoudnessTarget.EbuR128, Measure(silence));
        Assert.Equal(0, linear.Gain);
        Assert.Equal(silence, FilterHarness.Run(linear, silence, Rate));
        using LoudnessNormalizer dynamic = LoudnessNormalizer.Dynamic(LoudnessTarget.EbuR128);
        Assert.All(FilterHarness.Run(dynamic, silence, Rate), c => Assert.All(c, s => Assert.Equal(0f, s)));
    }

    [Fact]
    public void TargetsHaveTheirPresets()
    {
        Assert.Equal((-23.0, -1.0, 7.0), (LoudnessTarget.EbuR128.Integrated, LoudnessTarget.EbuR128.TruePeak, LoudnessTarget.EbuR128.Range));
        Assert.Equal((-24.0, -2.0), (LoudnessTarget.AtscA85.Integrated, LoudnessTarget.AtscA85.TruePeak));
        Assert.Equal((-14.0, -1.0), (LoudnessTarget.Streaming.Integrated, LoudnessTarget.Streaming.TruePeak));
        Assert.Equal((-16.0, -1.0), (LoudnessTarget.Podcast.Integrated, LoudnessTarget.Podcast.TruePeak));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LoudnessTarget(0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LoudnessTarget(-23, 3));
    }

    [Fact]
    public async Task NormalizeLoudnessAsyncRunsBothPasses()
    {
        string input = AudioRoundTripTests.OutputPath("loudness-in.wav"), output = AudioRoundTripTests.OutputPath("loudness-out.flac");
        using (AudioWriter writer = AudioWriter.Create(input))
        {
            foreach (AudioFrame frame in FilterHarness.Frames(Tone([-36, -28, -36], [3, 6, 3]), Rate, 4800))
            {
                using (frame)
                {
                    writer.Write(frame);
                }
            }
        }

        IReadOnlyList<LoudnessNormalization> done = await AudioFilters.NormalizeLoudnessAsync(input, output, LoudnessTarget.Podcast, cancellationToken: TestContext.Current.CancellationToken);
        LoudnessNormalization track = Assert.Single(done);
        Assert.Equal(-16 - track.Measured.Integrated, track.Gain, 9);
        Assert.False(track.Limited);
        Assert.Equal(CodecIds.PcmLittleEndian, ((AudioTrackInfo)MediaFile.Probe(input).Tracks[0]).CodecId);
        using AudioReader reader = AudioReader.Open(output);
        LoudnessMeter meter = new();
        foreach (AudioFrame frame in reader.ReadFrames())
        {
            using (frame)
            {
                meter.Add(frame);
            }
        }

        Assert.Equal(-16, meter.Integrated, 0.5);
        Assert.Equal(12 * Rate, (long)Math.Round(meter.Duration.Seconds * Rate));
    }

    [Fact]
    public async Task ApplyAsyncFiltersEveryAudioTrack()
    {
        string input = Path.Combine(AudioCorpus.Directory, "mp4-flac-stereo.mp4");
        Assert.SkipUnless(File.Exists(input), "corpus");
        string output = AudioRoundTripTests.OutputPath("apply-gain.mkv");
        await AudioFilters.ApplyAsync(input, output, _ => new Gain(-6), cancellationToken: TestContext.Current.CancellationToken);
        AudioTrackInfo track = (AudioTrackInfo)MediaFile.Probe(output).Tracks[0];
        Assert.Equal(CodecIds.Flac, track.CodecId);
        float[][] before = Read(input), after = Read(output);
        Assert.Equal(before[0].Length, after[0].Length);
        Assert.True(FilterHarness.DifferenceDb([before[0].Select(s => s * 0.5011872f).ToArray()], [after[0]]) < -80);
    }

    [Fact]
    public void TrimCutsLeadingAndTrailingSilence()
    {
        float[][] signal = Gaps((0.5, false), (1, true), (0.2, false), (1, true), (0.7, false));
        using SilenceRemover remover = new();
        List<AudioFrame> frames = FilterHarness.Frames(signal, Rate, 1000, MediaTime.FromSeconds(2));
        float[][] output = FilterHarness.Run(remover, frames, out MediaTime start);
        Assert.Equal(MediaTime.FromSeconds(2), start);

        // Sound from 0.5 s to 2.7 s (the inner pause kept), plus up to 10 ms the 20 ms detector reaches ahead and 10 ms of fade
        // on each side.
        Assert.InRange(output[0].Length, (int)(2.22 * Rate), (int)(2.24 * Rate) + 20);
        Assert.True(Math.Abs(output[0][0]) < 0.01 && Math.Abs(output[0][^1]) < 0.01, "the cuts fade");
        Assert.True(MaxStep(output[0]) < 0.1, "no clicks");
    }

    [Fact]
    public void ShortenCutsLongPauses()
    {
        float[][] signal = Gaps((1, true), (3, false), (1, true), (0.5, false), (1, true));
        using SilenceRemover remover = new(SilenceRemoval.Shorten);
        float[][] output = FilterHarness.Run(remover, signal, Rate);

        // The 3 s pause becomes 0.3 s; the 0.5 s pause stays.
        Assert.InRange(output[0].Length, (int)(3.8 * Rate), (int)(3.82 * Rate) + 20);
        Assert.True(MaxStep(output[0]) < 0.1, "no clicks");
        using SilenceRemover trim = new() { Threshold = -40 };
        Assert.Equal(signal[0].Length, FilterHarness.Run(trim, signal, Rate)[0].Length);
    }

    [Fact]
    public void AllSilenceComesOutEmpty()
    {
        using SilenceRemover remover = new();
        float[][] output = FilterHarness.Run(remover, [new float[Rate], new float[Rate]], Rate);
        Assert.Empty(output[0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SilenceRemover { KeepGap = MediaTime.FromSeconds(0.01) });
        using SilenceRemover bad = new() { KeepGap = MediaTime.FromSeconds(2) };
        using AudioFrame frame = FilterHarness.Frames(FilterHarness.Tone(Rate, 0.01, 100), Rate)[0];
        Assert.Throws<ArgumentException>(() => bad.Send(frame));
    }

    /// <summary>Stretches of a 300 Hz tone at -12 dBFS (true) or noise at -70 dBFS (false), in seconds, stereo.</summary>
    private static float[][] Gaps(params (double Seconds, bool Sound)[] parts)
    {
        Random random = new(3);
        List<float> samples = [];
        foreach ((double s, bool sound) in parts)
        {
            int n = (int)Math.Round(s * Rate);
            for (int i = 0; i < n; i++)
            {
                samples.Add(sound ? (float)(0.25 * Math.Sin(2 * Math.PI * 300 * samples.Count / Rate)) : (float)(0.0003 * ((random.NextDouble() * 2) - 1)));
            }
        }

        return [[.. samples], [.. samples]];
    }

    private static float MaxStep(float[] samples)
    {
        float step = 0;
        for (int i = 1; i < samples.Length; i++)
        {
            step = Math.Max(step, Math.Abs(samples[i] - samples[i - 1]));
        }

        return step;
    }

    /// <summary>A stereo 1 kHz sine, each channel at the peak level, through stretches of (dBFS, seconds).</summary>
    private static float[][] Tone(double[] levels, double[] seconds)
    {
        int total = seconds.Sum(s => (int)Math.Round(s * Rate));
        float[] samples = new float[total];
        int at = 0;
        for (int p = 0; p < levels.Length; p++)
        {
            double a = Math.Pow(10, levels[p] / 20);
            int n = (int)Math.Round(seconds[p] * Rate);
            for (int i = 0; i < n; i++, at++)
            {
                samples[at] = (float)(a * Math.Sin(2 * Math.PI * 1000 * at / Rate));
            }
        }

        return [samples, (float[])samples.Clone()];
    }

    private static LoudnessReport Measure(float[][] signal)
    {
        LoudnessMeter meter = new();
        foreach (AudioFrame frame in FilterHarness.Frames(signal, Rate, 4800))
        {
            using (frame)
            {
                meter.Add(frame);
            }
        }

        meter.Complete(meter.Duration);
        return meter.Report();
    }

    private static float[][] Read(string path)
    {
        using AudioReader reader = AudioReader.Open(path);
        List<AudioFrame> frames = [.. reader.ReadFrames()];
        float[][] result = FilterHarness.Join(frames);
        frames.ForEach(f => f.Dispose());
        return result;
    }
}
