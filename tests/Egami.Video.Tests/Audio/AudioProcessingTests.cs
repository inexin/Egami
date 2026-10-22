using Egami.Video.Audio;
using Egami.Video.Containers;

namespace Egami.Video.Tests.Audio;

/// <summary>Sample format conversion, channel layouts and mixing, the resampler, and sample-exact seeking.</summary>
public class AudioProcessingTests
{
    [Theory]
    [InlineData(SampleFormat.S16, SampleFormat.F32Planar)]
    [InlineData(SampleFormat.S16Planar, SampleFormat.S32)]
    [InlineData(SampleFormat.S32Planar, SampleFormat.F64)]
    [InlineData(SampleFormat.S16, SampleFormat.U8Planar)]
    [InlineData(SampleFormat.S16Planar, SampleFormat.S64)]
    public void ConversionsRoundTrip(SampleFormat via, SampleFormat back)
    {
        using AudioFrame input = AudioRoundTripTests.Signal(3, 16, samples: 3000);
        using AudioFrame there = input.Convert(via);
        using AudioFrame other = there.Convert(back);
        using AudioFrame again = other.Convert(SampleFormat.S16Planar);
        for (int c = 0; c < 3; c++)
        {
            if (back.ToInterleaved() == SampleFormat.U8)
            {
                // Eight bits keep the top byte: equal within half a step of 256.
                for (int i = 0; i < input.SampleCount; i++)
                {
                    Assert.InRange(again.AsSpan<short>(c)[i] - input.AsSpan<short>(c)[i], -128, 128);
                }
            }
            else
            {
                Assert.True(again.AsSpan<short>(c).SequenceEqual(input.AsSpan<short>(c)));
            }
        }
    }

    [Fact]
    public void ScalesBy24ValidBits()
    {
        using AudioFrame frame = new(SampleFormat.S32Planar, ChannelLayout.Mono, 48000, 2, MediaTime.Zero) { ValidBits = 24 };
        frame.GetSpan<int>(0)[0] = 1 << 22;
        frame.GetSpan<int>(0)[1] = -(1 << 23);
        using AudioFrame f = frame.Convert(SampleFormat.F32Planar);
        Assert.Equal(0.5f, f.AsSpan<float>(0)[0]);
        Assert.Equal(-1f, f.AsSpan<float>(0)[1]);
        using AudioFrame s16 = frame.Convert(SampleFormat.S16Planar);
        Assert.Equal(1 << 14, s16.AsSpan<short>(0)[0]);
    }

    [Fact]
    public void DitherIsTriangularAndRepeatable()
    {
        using AudioFrame silence = new(SampleFormat.F32Planar, ChannelLayout.Mono, 48000, 20000, MediaTime.Zero);
        AudioConvertOptions dither = new() { Dither = true };
        using AudioFrame a = silence.Convert(SampleFormat.S16Planar, null, dither);
        using AudioFrame b = silence.Convert(SampleFormat.S16Planar, null, dither);
        Assert.True(a.AsSpan<short>(0).SequenceEqual(b.AsSpan<short>(0)));

        // TPDF of one step peak rounded: values -1, 0 and 1 only, an eighth, three quarters and an eighth.
        int[] counts = new int[3];
        foreach (short v in a.AsSpan<short>(0))
        {
            Assert.InRange(v, -1, 1);
            counts[v + 1]++;
        }

        Assert.InRange(counts[1] / 20000.0, 0.72, 0.78);
        Assert.InRange(counts[0] / 20000.0, 0.11, 0.14);
    }

    [Fact]
    public void LayoutsNameTheirSpeakers()
    {
        Assert.Equal(6, ChannelLayout.Surround51.ChannelCount);
        Assert.Equal(Speakers.LowFrequency, ChannelLayout.Surround51.GetSpeaker(3));
        Assert.Equal(4, ChannelLayout.Surround51.IndexOf(Speakers.BackLeft));
        Assert.Equal(-1, ChannelLayout.Stereo.IndexOf(Speakers.FrontCenter));
        Assert.Equal(Speakers.None, ChannelLayout.Unpositioned(4).GetSpeaker(2));
        Assert.Equal("5.1", ChannelLayout.Surround51.ToString());
        Assert.Equal("7.1.4", ChannelLayout.Surround714.ToString());
        Assert.Equal(ChannelLayout.Surround61, ChannelLayout.ForChannelCount(7));
    }

    [Fact]
    public void DownmixesFiveOneToStereo()
    {
        ChannelMixer mixer = new(ChannelLayout.Surround51, ChannelLayout.Stereo);
        const float h = ChannelMixer.MinusThreeDecibels;
        float[,] expected =
        {
            // FL FR  FC  LFE BL  BR
            { 1, 0, h, 0, h, 0 },
            { 0, 1, h, 0, 0, h },
        };
        Assert.Equal(expected, mixer.GetMatrix());

        using AudioFrame frame = new(SampleFormat.F32Planar, ChannelLayout.Surround51, 48000, 1, MediaTime.Zero);
        for (int c = 0; c < 6; c++)
        {
            frame.GetSpan<float>(c)[0] = 0.1f * (c + 1);
        }

        using AudioFrame stereo = mixer.Mix(frame);
        Assert.Equal(0.1f + (h * 0.3f) + (h * 0.5f), stereo.AsSpan<float>(0)[0], 6);
        Assert.Equal(0.2f + (h * 0.3f) + (h * 0.6f), stereo.AsSpan<float>(1)[0], 6);
    }

    [Fact]
    public void MixesOtherLayouts()
    {
        const float h = ChannelMixer.MinusThreeDecibels;
        Assert.Equal(new float[,] { { h, h } }, new ChannelMixer(ChannelLayout.Stereo, ChannelLayout.Mono).GetMatrix());
        Assert.Equal(new float[,] { { h }, { h } }, new ChannelMixer(ChannelLayout.Mono, ChannelLayout.Stereo).GetMatrix());

        // 7.1 to 5.1: the side channels fold into the back ones at -3 dB.
        float[,] m = new ChannelMixer(ChannelLayout.Surround71, ChannelLayout.Surround51).GetMatrix();
        Assert.Equal(h, m[4, 6]);
        Assert.Equal(h, m[5, 7]);
        Assert.Equal(1, m[4, 4]);

        // Upmixing routes only shared speakers: stereo into 5.1 leaves center, LFE and surrounds silent.
        float[,] up = new ChannelMixer(ChannelLayout.Stereo, ChannelLayout.Surround51).GetMatrix();
        Assert.Equal(1, up[0, 0]);
        Assert.Equal(0, up[2, 0] + up[2, 1] + up[3, 0] + up[4, 0] + up[5, 1]);

        // 5.1(side) to 5.1: side and back are the same pair under two names.
        float[,] side = new ChannelMixer(ChannelLayout.Surround51Side, ChannelLayout.Surround51).GetMatrix();
        Assert.Equal(1, side[4, 4]);
    }

    [Theory]
    [InlineData(44100, 48000, ResamplerQuality.Good)]
    [InlineData(48000, 44100, ResamplerQuality.Good)]
    [InlineData(48000, 16000, ResamplerQuality.Good)]
    [InlineData(8000, 44100, ResamplerQuality.Fast)]
    [InlineData(44100, 96000, ResamplerQuality.Best)]
    [InlineData(44100, 47999, ResamplerQuality.Good)]
    public void ResamplesExactlyInTime(int from, int to, ResamplerQuality quality)
    {
        // Output length is the input length at the new rate, and frames tile the time line with no gap.
        using Resampler resampler = new(from, to, ChannelLayout.Stereo, quality);
        int total = 0;
        long next = 0;
        List<AudioFrame> frames = [];
        for (int i = 0; i < 5; i++)
        {
            using AudioFrame input = AudioRoundTripTests.Signal(2, 16, rate: from, samples: 3001, seed: i);
            input.Timestamp = new MediaTime(i * 3001L, new Rational(1, from));
            resampler.Send(input);
            while (resampler.TryReceive(out AudioFrame? frame))
            {
                frames.Add(frame);
            }
        }

        resampler.Flush();
        while (resampler.TryReceive(out AudioFrame? frame))
        {
            frames.Add(frame);
        }

        foreach (AudioFrame frame in frames)
        {
            using (frame)
            {
                Assert.Equal(new MediaTime(next, new Rational(1, to)), frame.Timestamp);
                next += frame.SampleCount;
                total += frame.SampleCount;
            }
        }

        Assert.Equal((long)Math.Ceiling(5 * 3001.0 * to / from), total);
    }

    [Theory]
    [InlineData(44100, 48000)]
    [InlineData(48000, 44100)]
    [InlineData(96000, 44100)]
    public void GoodQualityGate(int from, int to)
    {
        // Passband: tones up to 0.9 of the lower Nyquist come through within 0.1 dB.
        double nyquist = Math.Min(from, to) / 2.0;
        foreach (double fraction in new[] { 0.05, 0.3, 0.6, 0.9 })
        {
            double gain = ToneGain(from, to, fraction * nyquist);
            Assert.InRange(20 * Math.Log10(gain), -0.1, 0.1);
        }

        // Stopband: a tone above the output's Nyquist (aliasing) is at least 100 dB down.
        if (to < from)
        {
            foreach (double fraction in new[] { 1.02, 1.2, 1.6 })
            {
                double f = fraction * nyquist;
                if (f < from / 2.0)
                {
                    Assert.True(20 * Math.Log10(ToneGain(from, to, f)) < -100, $"{f} Hz leaks.");
                }
            }
        }
    }

    [Theory]
    [InlineData("flac-l5-stereo.flac")]
    [InlineData("flac-odd-block.flac")]
    [InlineData("wav-s24-51.wav")]
    [InlineData("w64-s16-stereo.w64")]
    [InlineData("mka-flac-stereo.mka")]
    [InlineData("mp4-flac-stereo.mp4")]
    public void SeeksToTheSample(string name)
    {
        string path = AudioCorpus.PathOf(name);
        AudioReaderOptions options = new() { SampleFormat = SampleFormat.S32Planar, Errors = DecodeErrorMode.Throw };
        using AudioReader whole = AudioReader.Open(path, options);
        List<int> all = [];
        foreach (AudioFrame frame in whole.ReadFrames())
        {
            using (frame)
            {
                all.AddRange(frame.AsSpan<int>(0).ToArray());
            }
        }

        using AudioReader reader = AudioReader.Open(path, options);
        foreach (long target in new long[] { 3333, 0, all.Count - 100, 1234 })
        {
            reader.Seek(new MediaTime(target, new Rational(1, reader.SampleRate)));
            Assert.True(reader.TryRead(out AudioFrame? frame));
            using (frame)
            {
                Assert.Equal(new MediaTime(target, new Rational(1, reader.SampleRate)), frame.Timestamp);
                int n = Math.Min(frame.SampleCount, 50);
                Assert.Equal(all.GetRange((int)target, n), frame.AsSpan<int>(0)[..n].ToArray());
            }
        }
    }

    [Fact]
    public void ReaderConvertsRateAndLayout()
    {
        using AudioReader reader = AudioReader.Open(AudioCorpus.PathOf("wav-s24-51.wav"), new AudioReaderOptions { SampleRate = 48000, Layout = ChannelLayout.Stereo, SampleFormat = SampleFormat.S16 });
        long samples = 0;
        foreach (AudioFrame frame in reader.ReadFrames())
        {
            using (frame)
            {
                Assert.Equal(SampleFormat.S16, frame.Format);
                Assert.Equal(ChannelLayout.Stereo, frame.Layout);
                Assert.Equal(48000, frame.SampleRate);
                samples += frame.SampleCount;
            }
        }

        Assert.Equal(8000 * 3, samples);
    }

    /// <summary>The steady-state amplitude of a resampled sine relative to the input's.</summary>
    private static double ToneGain(int from, int to, double frequency)
    {
        int n = from;
        using AudioFrame tone = new(SampleFormat.F32Planar, ChannelLayout.Mono, from, n, MediaTime.Zero);
        Span<float> s = tone.GetSpan<float>(0);
        for (int i = 0; i < n; i++)
        {
            s[i] = (float)(0.5 * Math.Sin(2 * Math.PI * frequency * i / from));
        }

        using Resampler resampler = new(from, to, ChannelLayout.Mono, ResamplerQuality.Good);
        resampler.Send(tone);
        resampler.Flush();
        List<float> output = [];
        while (resampler.TryReceive(out AudioFrame? frame))
        {
            using (frame)
            {
                output.AddRange(frame.AsSpan<float>(0).ToArray());
            }
        }

        // Fit a sine of the tone's frequency over the middle, away from the edges: amplitude from the I and Q sums.
        // A tone above the output's Nyquist frequency would show up mirrored below it.
        double measured = frequency > to / 2.0 ? to - frequency : frequency;
        int start = output.Count / 4, end = output.Count * 3 / 4;
        double i0 = 0, q0 = 0;
        for (int k = start; k < end; k++)
        {
            double phase = 2 * Math.PI * measured * k / to;
            i0 += output[k] * Math.Sin(phase);
            q0 += output[k] * Math.Cos(phase);
        }

        double amplitude = 2 * Math.Sqrt((i0 * i0) + (q0 * q0)) / (end - start);
        return Math.Max(amplitude / 0.5, 1e-12);
    }
}

/// <summary>The steady-state decode loop allocates only each frame's small object, not sample memory (V5 allocation gate).</summary>
public class AudioAllocationTests
{
    [Theory]
    [InlineData("flac-l5-stereo.flac")]
    [InlineData("flac-24-51.flac")]
    [InlineData("wav-s16-stereo.wav")]
    public void DecodeLoopAllocatesNoSampleMemory(string name)
    {
        List<EncodedPacket> packets = [];
        AudioTrackInfo track;
        using (IDemuxer demuxer = MediaFile.OpenDemuxer(AudioCorpus.PathOf(name)))
        {
            track = (AudioTrackInfo)demuxer.Tracks[0];
            while (demuxer.ReadPacket() is { } packet)
            {
                packets.Add(packet);
            }
        }

        using IAudioDecoder decoder = AudioCodecs.CreateDecoder(track, new AudioDecoderOptions { VerifyChecksums = false });
        void Pass()
        {
            foreach (EncodedPacket packet in packets)
            {
                decoder.Send(packet);
                while (decoder.TryReceive(out AudioFrame? frame))
                {
                    frame.Dispose();
                }
            }
        }

        Pass();
        Pass();
        long before = GC.GetAllocatedBytesForCurrentThread();
        Pass();
        long perPacket = (GC.GetAllocatedBytesForCurrentThread() - before) / packets.Count;
        packets.ForEach(p => p.Dispose());
        Assert.True(perPacket < 400, $"{perPacket} bytes allocated per packet");
    }
}
