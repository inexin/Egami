using Egami.Video.Analysis;
using Egami.Video.Audio;

namespace Egami.Video.Tests.Analysis;

/// <summary>
/// The stage C audio detectors (V1.4 Build Plan tasks 12 and 13): silence ranges to the sample, sample levels, and the loudness
/// meter against the EBU Tech 3341 and 3342 cases, generated here from the specs' descriptions (1 kHz sines at stated levels).
/// </summary>
public sealed class AudioDetectorTests
{
    private const int Rate = 48000;

    [Fact]
    public void FiltersMatchTheBs1770TableAt48kHz()
    {
        double[] k = LoudnessMeter.FilterCoefficients(48000);
        double[] table = [1.53512485958697, -2.69169618940638, 1.19839281085285, -1.69065929318241, 0.73248077421585, -1.99004745483398, 0.99007225036621];
        for (int i = 0; i < table.Length; i++)
        {
            Assert.Equal(table[i], k[i], 1e-7);
        }
    }

    [Theory]
    [InlineData(-23.0, 20.0)]
    [InlineData(-33.0, 20.0)]
    public void Tech3341SteadyTones(double level, double seconds)
    {
        // Cases 1 and 2: a stereo 1 kHz sine, each channel at the level, reads that loudness.
        LoudnessMeter meter = new();
        Feed(meter, Tone(Rate, 2, (level, seconds)));
        Assert.Equal(level, meter.Integrated, 0.1);
        Assert.Equal(level, meter.Momentary, 0.1);
        Assert.Equal(level, meter.ShortTerm, 0.1);
    }

    [Fact]
    public void Tech3341GatedCases()
    {
        // Case 3: -36, -23, -36 dBFS for 10, 60 and 10 s.
        LoudnessMeter three = new();
        Feed(three, Tone(Rate, 2, (-36, 10), (-23, 60), (-36, 10)));
        Assert.Equal(-23, three.Integrated, 0.1);

        // Case 4: -72, -36, -23, -36, -72 dBFS for 10, 10, 60, 10 and 10 s.
        LoudnessMeter four = new();
        Feed(four, Tone(Rate, 2, (-72, 10), (-36, 10), (-23, 60), (-36, 10), (-72, 10)));
        Assert.Equal(-23, four.Integrated, 0.1);

        // Case 5: -26, -20, -26 dBFS for 20, 20.1 and 20 s.
        LoudnessMeter five = new();
        Feed(five, Tone(Rate, 2, (-26, 20), (-20, 20.1), (-26, 20)));
        Assert.Equal(-23, five.Integrated, 0.1);
    }

    [Theory]
    [InlineData(44100)]
    [InlineData(32000)]
    [InlineData(96000)]
    [InlineData(8000)]
    public void OtherRatesReadTheSame(int rate)
    {
        LoudnessMeter meter = new();
        Feed(meter, Tone(rate, 2, (-23, 10)));
        Assert.Equal(-23, meter.Integrated, 0.1);
    }

    [Theory]
    [InlineData(10, -20.0, -30.0)]
    [InlineData(5, -20.0, -15.0)]
    [InlineData(20, -40.0, -20.0)]
    public void Tech3342RangeCases(double range, double first, double second)
    {
        // Cases 1 to 3: two 20 s tones.
        LoudnessMeter meter = new();
        Feed(meter, Tone(Rate, 2, (first, 20), (second, 20)));
        Assert.Equal(range, meter.Range, 1.0);
        LoudnessReport report = meter.Report();
        Assert.Equal(report.RangeHigh - report.RangeLow, report.Range, 9);
    }

    [Fact]
    public void Tech3342FiveTones()
    {
        // Case 4: -50, -35, -20, -35, -50 dBFS, 20 s each, reads 15 LU.
        LoudnessMeter meter = new();
        Feed(meter, Tone(Rate, 2, (-50, 20), (-35, 20), (-20, 20), (-35, 20), (-50, 20)));
        Assert.Equal(15, meter.Range, 1.0);
    }

    [Theory]
    [InlineData(45.0)]
    [InlineData(60.0)]
    [InlineData(30.0)]
    public void TruePeakFindsThePeakBetweenSamples(double phase)
    {
        // A quarter-rate sine at -6 dBFS: its samples miss the peak (by 3 dB at a 45 degree phase), the true peak finds it.
        LoudnessMeter meter = new();
        Feed(meter, Frames(Rate, 1, Rate, i => 0.5 * Math.Sin((Math.PI / 2 * i) + (phase * Math.PI / 180))));
        double sine = 20 * Math.Log10(0.5);
        Assert.InRange(meter.TruePeak, sine - 0.4, sine + 0.2);
        Assert.True(meter.SamplePeak < meter.TruePeak);
        Assert.Equal(meter.TruePeak, Assert.Single(meter.Report().ChannelTruePeaks), 9);

        LoudnessMeter samples = new(new LoudnessOptions { TruePeak = false });
        Feed(samples, Frames(Rate, 1, Rate, i => 0.5 * Math.Sin((Math.PI / 2 * i) + (phase * Math.PI / 180))));
        Assert.Equal(samples.SamplePeak, samples.TruePeak, 9);
    }

    [Fact]
    public void SurroundChannelsWeighMoreAndLfeNotAtAll()
    {
        Assert.Equal(0, LoudnessMeter.Weight(Speakers.LowFrequency));
        Assert.Equal(1.41, LoudnessMeter.Weight(Speakers.BackLeft));
        Assert.Equal(1.41, LoudnessMeter.Weight(Speakers.SideRight));
        Assert.Equal(1, LoudnessMeter.Weight(Speakers.FrontCenter));

        // 5.1 with a loud LFE: only the -23 dBFS front pair counts.
        LoudnessMeter meter = new();
        Feed(meter, Frames(Rate, 6, Rate * 10, i => Math.Pow(10, -23 / 20.0) * Math.Sin(2 * Math.PI * 1000 * i / Rate), ChannelLayout.Surround51, c => c is 0 or 1 ? 1 : c == 3 ? 20 : 0));
        Assert.Equal(-23, meter.Integrated, 0.1);

        // The surround pair alone reads 1.5 dB louder than the same tone in the front pair.
        LoudnessMeter back = new();
        Feed(back, Frames(Rate, 6, Rate * 10, i => Math.Pow(10, -23 / 20.0) * Math.Sin(2 * Math.PI * 1000 * i / Rate), ChannelLayout.Surround51, c => c is 4 or 5 ? 1 : 0));
        Assert.Equal(-23 + (10 * Math.Log10(1.41)), back.Integrated, 0.1);
    }

    [Fact]
    public void LoudnessSeriesStepEvery100Milliseconds()
    {
        LoudnessMeter meter = new(new LoudnessOptions { KeepSeries = true });
        Feed(meter, Tone(Rate, 2, (-23, 5)));
        Assert.Equal(47, meter.MomentarySeries.Count);
        Assert.Equal(21, meter.ShortTermSeries.Count);
        Assert.Equal(0.4, meter.MomentarySeries[0].Time.Seconds, 9);
        Assert.Equal(3.0, meter.ShortTermSeries[0].Time.Seconds, 9);
        Assert.All(meter.ShortTermSeries, v => Assert.Equal(-23, v.Loudness, 0.1));
        Assert.Equal(5.0, meter.Duration.Seconds, 9);
        Assert.Equal(-23, meter.MaxShortTerm, 0.1);
    }

    [Fact]
    public void SilenceReadsNegativeInfinity()
    {
        LoudnessMeter meter = new();
        Feed(meter, Tone(Rate, 2, (double.NegativeInfinity, 5)));
        LoudnessReport report = meter.Report();
        Assert.True(double.IsNegativeInfinity(report.Integrated));
        Assert.True(double.IsNegativeInfinity(report.Range));
        Assert.True(double.IsNegativeInfinity(report.TruePeak));
    }

    [Fact]
    public void MetersRefuseAChangeOfFormat()
    {
        LoudnessMeter meter = new();
        Feed(meter, Tone(Rate, 2, (-23, 1)));
        using AudioFrame mono = new(SampleFormat.F32Planar, ChannelLayout.Mono, Rate, 100, MediaTime.FromSeconds(1));
        Assert.Throws<InvalidOperationException>(() => meter.Add(mono));
        using AudioFrame low = new(SampleFormat.F32Planar, ChannelLayout.Stereo, 4000, 100, MediaTime.Zero);
        Assert.Throws<NotSupportedException>(() => new LoudnessMeter().Add(low));
    }

    [Fact]
    public void SilenceRangesAreExactToTheSample()
    {
        // 1 s of tone, 3 s of silence, 1 s of tone, then 2.5 s of silence to the end.
        SilenceDetector detector = new();
        Feed(detector, Tone(Rate, 2, (-20, 1), (double.NegativeInfinity, 3), (-20, 1), (double.NegativeInfinity, 2.5)));
        Assert.Equal(2, detector.Ranges.Count);
        Assert.Equal(1.0, detector.Ranges[0].Start.Seconds, 9);

        // The tone starts at a zero crossing, so its first sample is still silent: the range ends one sample after 4 s.
        Assert.Equal(4.0 + (1.0 / Rate), detector.Ranges[0].End.Seconds, 9);
        Assert.True(detector.Ranges[0].Value < -200, "only digital silence and the tone's first, near-zero sample");
        Assert.Equal(5.0, detector.Ranges[1].Start.Seconds, 9);
        Assert.Equal(7.5, detector.Ranges[1].End.Seconds, 9);
        Assert.Empty(detector.ChannelRanges);
    }

    [Fact]
    public void QuietNoiseCountsAsSilence()
    {
        // A -70 dBFS tone is under the -60 dBFS default; the range's value is its peak. One second is under the 2 s minimum.
        SilenceDetector detector = new();
        Feed(detector, Tone(Rate, 1, (-20, 1), (-70, 3), (-20, 1), (-70, 1), (-20, 1)));
        DetectedRange range = Assert.Single(detector.Ranges);
        Assert.Equal(-70, range.Value, 0.01);
        Assert.Equal(3.0, range.Duration.Seconds, 3);

        SilenceDetector strict = new(new SilenceOptions { Noise = -80 });
        Feed(strict, Tone(Rate, 1, (-20, 1), (-70, 3), (-20, 1)));
        Assert.Empty(strict.Ranges);
    }

    [Fact]
    public void SilenceCanBePerChannel()
    {
        // The left channel goes quiet for 3 s while the right keeps playing.
        SilenceDetector detector = new(new SilenceOptions { PerChannel = true });
        Feed(detector, Frames(Rate, 2, Rate * 5, i => 0.1 * Math.Sin(2 * Math.PI * 1000 * i / Rate), ChannelLayout.Stereo, (c, i) => c == 0 && i >= Rate && i < Rate * 4 ? 0 : 1));
        Assert.Empty(detector.Ranges);
        Assert.Equal(2, detector.ChannelRanges.Count);
        DetectedRange left = Assert.Single(detector.ChannelRanges[0]);
        Assert.Equal(1.0, left.Start.Seconds, 9);
        Assert.Equal(4.0 + (1.0 / Rate), left.End.Seconds, 9);
        Assert.Empty(detector.ChannelRanges[1]);
    }

    [Fact]
    public void LevelsOfASine()
    {
        AudioLevelMeter meter = new();
        Feed(meter, Tone(Rate, 2, (-6, 2)));
        AudioLevelReport report = meter.Report();
        Assert.Equal(Rate * 2, report.Samples);
        Assert.Equal(Rate, report.SampleRate);
        Assert.Equal(2, report.Channels.Count);
        ChannelLevels all = report.Overall;
        Assert.Equal(-6, all.Peak, 0.01);
        Assert.Equal(-6 - (10 * Math.Log10(2)), all.Rms, 0.01);
        Assert.Equal(10 * Math.Log10(2), all.CrestFactor, 0.01);
        Assert.Equal(0, all.DcOffset, 1e-4);
        Assert.Equal(0, all.Clipped);
        Assert.Equal(-6, all.NoiseFloor, 0.01);
        Assert.Equal(-Math.Pow(10, -6 / 20.0), all.Min, 1e-4);

        // 1 kHz crosses zero 2000 times a second on each channel.
        Assert.InRange(report.Channels[0].ZeroCrossings, 3998, 4000);
        Assert.Equal(Rate * 2 * 2, all.Histogram.Sum());
        Assert.Equal(AudioLevelMeter.HistogramBins, all.Histogram.Count);
        Assert.Equal(0, all.Histogram[0]);
        Assert.True(all.Histogram[6] > 0);
        Assert.Equal(report.Channels[0].PeakCount + report.Channels[1].PeakCount, all.PeakCount);
    }

    [Fact]
    public void LevelsFindTheNoiseFloorAndClipping()
    {
        AudioLevelMeter meter = new();
        Feed(meter, Tone(Rate, 1, (-6, 1), (-60, 1), (double.NegativeInfinity, 1)));
        Assert.Equal(-60, meter.Report().Overall.NoiseFloor, 0.1);

        // 16-bit samples clip at 32767 and -32768.
        AudioLevelMeter clips = new();
        using AudioFrame frame = new(SampleFormat.S16, ChannelLayout.Mono, Rate, 4, MediaTime.Zero);
        frame.GetSpan<short>()[0] = short.MaxValue;
        frame.GetSpan<short>()[1] = short.MinValue;
        frame.GetSpan<short>()[2] = 1000;
        frame.GetSpan<short>()[3] = -1000;
        clips.Add(frame);
        ChannelLevels levels = clips.Report().Overall;
        Assert.Equal(2, levels.Clipped);
        Assert.Equal(0, levels.Peak, 1e-9);
        Assert.Equal(1, levels.PeakCount);
        Assert.Equal(3, levels.ZeroCrossings);
    }

    [Fact]
    public void NoAllocationPerFrame()
    {
        List<AudioFrame> frames = [.. Tone(Rate, 2, (-20, 2), (double.NegativeInfinity, 3), (-20, 2))];
        try
        {
            IAudioDetector[] detectors = [new SilenceDetector(new SilenceOptions { PerChannel = true }), new AudioLevelMeter(), new LoudnessMeter()];
            foreach (IAudioDetector detector in detectors)
            {
                for (int i = 0; i < 20; i++)
                {
                    detector.Add(frames[i]);
                }

                FrameAllocations.AssertNone(detector.GetType().Name, frames.Count - 20, i => detector.Add(frames[20 + i]));
            }
        }
        finally
        {
            frames.ForEach(f => f.Dispose());
        }
    }

    private static void Feed(IAudioDetector detector, IEnumerable<AudioFrame> frames)
    {
        MediaTime end = MediaTime.Zero;
        foreach (AudioFrame frame in frames)
        {
            using (frame)
            {
                detector.Add(frame);
                end = frame.Timestamp + frame.Duration;
            }
        }

        detector.Complete(end);
    }

    /// <summary>A 1 kHz sine on every channel through stretches of (peak level in dBFS, seconds).</summary>
    private static IEnumerable<AudioFrame> Tone(int rate, int channels, params (double Level, double Seconds)[] parts)
    {
        long[] ends = new long[parts.Length];
        long total = 0;
        for (int p = 0; p < parts.Length; p++)
        {
            total += (long)Math.Round(parts[p].Seconds * rate);
            ends[p] = total;
        }

        return Frames(rate, channels, total, i =>
        {
            int part = 0;
            while (i >= ends[part])
            {
                part++;
            }

            return Math.Pow(10, parts[part].Level / 20) * Math.Sin(2 * Math.PI * 1000 * i / rate);
        });
    }

    private static IEnumerable<AudioFrame> Frames(int rate, int channels, long samples, Func<long, double> wave, ChannelLayout? layout = null, Func<int, double>? gain = null) =>
        Frames(rate, channels, samples, wave, layout ?? ChannelLayout.ForChannelCount(channels), (c, _) => gain?.Invoke(c) ?? 1);

    private static IEnumerable<AudioFrame> Frames(int rate, int channels, long samples, Func<long, double> wave, ChannelLayout layout, Func<int, long, double> gain)
    {
        const int Size = 1024;
        for (long start = 0; start < samples; start += Size)
        {
            int count = (int)Math.Min(Size, samples - start);
            AudioFrame frame = new(SampleFormat.F32Planar, layout, rate, count, new MediaTime(start, new Rational(1, rate)));
            for (int c = 0; c < channels; c++)
            {
                Span<float> span = frame.GetSpan<float>(c);
                for (int i = 0; i < count; i++)
                {
                    span[i] = (float)(gain(c, start + i) * wave(start + i));
                }
            }

            yield return frame;
        }
    }
}
