using System.Buffers.Binary;

using Egami.Video.Audio;
using Egami.Video.Audio.Codecs;
using Egami.Video.Audio.Codecs.Opus;

namespace Egami.Video.Tests.Audio;

/// <summary>
/// The Opus encoder: packet for packet the same as libopus 1.5.2 in every mode (tests/video/corpus/opus-encode, see
/// tools/video/opusref/make-opus-encoder-reference.c), and through <see cref="AudioWriter"/> files that decode to the input's
/// exact length in every container, CBR packets of the exact size and constrained VBR within 5 % of the bitrate.
/// </summary>
public class OpusEncoderTests(ITestOutputHelper log)
{
    private static string ReferencePath => Path.Combine(AppContext.BaseDirectory, "corpus", "opus-encode", "encoder-ref.bin");

    /// <summary>The settings of one reference stream, in the order make-opus-encoder-reference.c writes them.</summary>
    private sealed record Config(int Kind, int Fs, int Channels, int Family, int Application, int Frame, int Bitrate, int Vbr, int Cvbr, int Complexity, int Fec, int Loss, int Dtx, int Bandwidth, int Signal, int ForceMode);

    [Fact]
    public void MatchesLibopusPacketForPacket()
    {
        byte[] file = File.ReadAllBytes(ReferencePath);
        Assert.True(file.AsSpan(0, 4).SequenceEqual("OPEN"u8));
        int count = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(4));
        int p = 8;
        int failures = 0;
        for (int k = 0; k < count; k++)
        {
            int[] f = new int[16];
            for (int i = 0; i < 16; i++, p += 4)
            {
                f[i] = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(p));
            }

            Config c = new(f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8], f[9], f[10], f[11], f[12], f[13], f[14], f[15]);
            int n = c.Fs * 15 / 10;
            int packets = n / c.Frame;
            float[] pcm = Signal(n, c.Channels, c.Fs);
            Func<ReadOnlySpan<float>, byte[], int> encode = Create(c, out Func<uint> finalRange);
            byte[] buffer = new byte[8 * 7680];
            int mismatch = -1;
            for (int i = 0; i < packets; i++, p += 10)
            {
                int length = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(p));
                uint hash = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(p + 2));
                uint range = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(p + 6));
                int got = encode(pcm.AsSpan(i * c.Frame * c.Channels, c.Frame * c.Channels), buffer);
                if (mismatch < 0 && (got != length || Fnv(buffer.AsSpan(0, Math.Max(got, 0))) != hash || finalRange() != range))
                {
                    mismatch = i;
                }
            }

            if (mismatch >= 0)
            {
                failures++;
                log.WriteLine($"Config {k} {c}: first packet that differs from libopus is {mismatch} of {packets}.");
            }
        }

        Assert.Equal(0, failures);
    }

    public static TheoryData<string, int> Containers() => new()
    {
        { ".opus", 2 },
        { ".webm", 2 },
        { ".mka", 1 },
        { ".mp4", 2 },
        { ".opus", 6 },
        { ".mka", 8 },
    };

    [Theory]
    [MemberData(nameof(Containers))]
    public void WritesEveryContainerWithExactLength(string extension, int channels)
    {
        using AudioFrame input = Tones(channels, 48000, 30_011);
        string path = AudioRoundTripTests.OutputPath($"opus-{channels}ch{extension}");
        using (AudioWriter writer = AudioWriter.Create(path, new AudioWriterOptions { Codec = AudioCodecs.Opus }))
        {
            for (int start = 0; start < input.SampleCount; start += 1777)
            {
                using AudioFrame part = input.Slice(start, Math.Min(1777, input.SampleCount - start));
                writer.Write(part);
            }
        }

        AudioTrackInfo track = Assert.Single(MediaFile.Probe(path).Tracks.OfType<AudioTrackInfo>());
        Assert.Equal(CodecIds.Opus, track.CodecId);
        Assert.Equal(channels, track.Channels);
        double snr = DecodedSnr(input, path, out int length);
        log.WriteLine($"{extension} {channels} ch: {snr:F1} dB");
        Assert.Equal(input.SampleCount, length);
        Assert.True(snr > 10, $"The decoded audio is {snr:F1} dB from the input.");
    }

    [Fact]
    public void OpusExtensionDefaultsToOpus()
    {
        using AudioFrame input = Tones(1, 48000, 4800);
        string path = AudioRoundTripTests.OutputPath("default.opus");
        using (AudioWriter writer = AudioWriter.Create(path))
        {
            writer.Write(input);
        }

        Assert.Equal(CodecIds.Opus, MediaFile.Probe(path).Tracks[0].CodecId);
    }

    [Theory]
    [InlineData(44100)]
    [InlineData(16000)]
    public void OtherRatesAreResampledAndKeptInTheHeader(int rate)
    {
        using AudioFrame input = Tones(2, rate, rate);
        string path = AudioRoundTripTests.OutputPath($"rate-{rate}.opus");
        using (AudioWriter writer = AudioWriter.Create(path, new AudioWriterOptions { Opus = new OpusEncoderOptions { Application = OpusApplication.Voice } }))
        {
            writer.Write(input);
        }

        AudioTrackInfo track = (AudioTrackInfo)MediaFile.Probe(path).Tracks[0];
        OpusHead head = OpusHead.Read(track.CodecPrivate.Span)!;
        Assert.Equal(rate, head.InputSampleRate);
        Assert.Equal(312, head.PreSkip);
        using AudioReader reader = AudioReader.Open(path);
        long samples = reader.ReadFrames().Sum(f =>
        {
            using (f)
            {
                return (long)f.SampleCount;
            }
        });
        Assert.Equal(input.SampleCount * 48000L / rate, samples, tolerance: 1);
    }

    [Theory]
    [InlineData(64_000, 20)]
    [InlineData(32_000, 10)]
    [InlineData(96_000, 60)]
    public void CbrPacketsHaveTheExactSize(int bitrate, int milliseconds)
    {
        int[] sizes = PacketSizes(new OpusEncoderOptions { Bitrate = bitrate, RateMode = OpusRateMode.Cbr, FrameDuration = TimeSpan.FromMilliseconds(milliseconds) }, 2, 48000);
        int expected = bitrate * milliseconds / 8000;
        Assert.All(sizes, size => Assert.Equal(expected, size));
    }

    [Theory]
    [InlineData(64_000, 2)]
    [InlineData(24_000, 1)]
    [InlineData(128_000, 2)]
    public void ConstrainedVbrAveragesWithinFivePercent(int bitrate, int channels)
    {
        int[] sizes = PacketSizes(new OpusEncoderOptions { Bitrate = bitrate, RateMode = OpusRateMode.ConstrainedVbr }, channels, 48000 * 10);
        double average = sizes.Average() * 8 * 50;
        log.WriteLine($"{bitrate} bit/s asked, {average:F0} bit/s coded");
        Assert.InRange(average, bitrate * 0.95, bitrate * 1.05);
    }

    [Theory]
    [InlineData(64_000, 2)]
    [InlineData(24_000, 1)]
    public void VbrFollowsTheContent(int bitrate, int channels)
    {
        // Unconstrained VBR spends what the audio needs: libopus 1.5.2 codes this tonal clip at about 78 and 34.5 kbit/s (the
        // reference test checks the packets are the same), and pink noise at 15 % under the bitrate.
        int[] sizes = PacketSizes(new OpusEncoderOptions { Bitrate = bitrate }, channels, 48000 * 10);
        double average = sizes.Average() * 8 * 50;
        log.WriteLine($"{bitrate} bit/s asked, {average:F0} bit/s coded");
        Assert.InRange(average, bitrate * 0.7, bitrate * 1.5);
    }

    [Fact]
    public void TranscodeToWebMPicksOpus()
    {
        // FLAC cannot go in WebM, so with no codec given its audio is encoded in Opus.
        string flac = AudioRoundTripTests.OutputPath("source.mka");
        using (AudioFrame input = AudioRoundTripTests.Signal(2, 16, rate: 44100, samples: 44100))
        using (AudioWriter writer = AudioWriter.Create(flac, new AudioWriterOptions { Codec = FlacCodec.Instance }))
        {
            writer.Write(input);
        }

        string path = AudioRoundTripTests.OutputPath("transcoded.webm");
        MediaFile.Transcode(flac, path, cancellationToken: TestContext.Current.CancellationToken);
        AudioTrackInfo track = Assert.Single(MediaFile.Probe(path).Tracks.OfType<AudioTrackInfo>());
        Assert.Equal(CodecIds.Opus, track.CodecId);
        Assert.Equal(44100, OpusHead.Read(track.CodecPrivate.Span)!.InputSampleRate);
    }

    [Fact]
    public void TranscodeToWebMCopiesOpus()
    {
        string path = AudioRoundTripTests.OutputPath("copied.webm");
        MediaFile.Transcode(Corpus.PathOf("webm-vp8-opus.webm"), path, cancellationToken: TestContext.Current.CancellationToken);
        TrackInfo original = MediaFile.Probe(Corpus.PathOf("webm-vp8-opus.webm")).Tracks.OfType<AudioTrackInfo>().Single();
        TrackInfo copied = MediaFile.Probe(path).Tracks.OfType<AudioTrackInfo>().Single();
        Assert.True(original.CodecPrivate.Span.SequenceEqual(copied.CodecPrivate.Span));
    }

    [Fact]
    public void OptionsRejectValuesOpusCannotCode()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpusEncoderOptions { FrameDuration = TimeSpan.FromMilliseconds(30) });
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpusEncoderOptions { Effort = 11 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpusEncoderOptions { ExpectedLoss = 101 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new OpusEncoderOptions { Bitrate = 100 });
        Assert.Equal(TimeSpan.FromMilliseconds(2.5), new OpusEncoderOptions { FrameDuration = TimeSpan.FromMilliseconds(2.5) }.FrameDuration);
    }

    [Fact]
    public void EncoderTrackDescribesTheStream()
    {
        using IAudioEncoder encoder = AudioCodecs.Opus.CreateEncoder(new AudioEncoderOptions { SampleRate = 48000, Layout = ChannelLayout.ForChannelCount(6) }, MediaContext.Default);
        AudioTrackInfo track = encoder.Track;
        OpusHead head = OpusHead.Read(track.CodecPrivate.Span)!;
        Assert.Equal(1, head.MappingFamily);
        Assert.Equal(4, head.StreamCount);
        Assert.Equal(2, head.CoupledCount);
        Assert.Equal(312, head.PreSkip);
        Assert.Equal(new MediaTime(312, new Rational(1, 48000)), track.CodecDelay);
        Assert.Equal(960, encoder.FrameSize);
    }

    [Theory]
    [InlineData(2, 0, OpusEncoder.ApplicationAudio, 960)]
    [InlineData(1, 0, OpusEncoder.ApplicationVoip, 960)]
    [InlineData(6, 1, OpusEncoder.ApplicationAudio, 960)]
    [InlineData(2, 0, OpusEncoder.ApplicationAudio, 2880)]
    public void AllocatesNothingPerPacket(int channels, int family, int application, int frame)
    {
        // The test signal runs through speech, noise, silence and chords, so every mode and switch is warmed up first.
        float[] pcm = Signal(48000 * 3, channels, 48000);
        OpusMultistreamEncoder encoder = new(48000, channels, family, application) { Bitrate = 32_000 * channels };
        byte[] data = new byte[8 * 7680];
        int packets = pcm.Length / channels / frame;
        void Pass()
        {
            for (int i = 0; i < packets; i++)
            {
                Assert.True(encoder.Encode(pcm.AsSpan(i * frame * channels, frame * channels), frame, data, 0, data.Length) > 0);
            }
        }

        Pass();
        long before = GC.GetAllocatedBytesForCurrentThread();
        Pass();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        bool optimized = typeof(AudioFrame).Assembly.GetCustomAttributes(typeof(System.Diagnostics.DebuggableAttribute), false)
            .OfType<System.Diagnostics.DebuggableAttribute>().All(d => !d.IsJITOptimizerDisabled);
        if (optimized)
        {
            Assert.Equal(0, allocated);
        }
    }

    /// <summary>The reference signal of make-opus-encoder-reference.c, interleaved.</summary>
    internal static float[] Signal(int n, int channels, int fs)
    {
        float[] pcm = new float[n * channels];
        uint s = 12345;
        int segment = fs / 4;
        for (int i = 0; i < n; i++)
        {
            int seg = i / segment % 6;
            int p1 = fs / (100 + (20 * (i / (fs / 8) % 5)));
            int p2 = fs / 330;
            for (int c = 0; c < channels; c++)
            {
                s = (s * 1664525u) + 1013904223u;
                double noise = ((s >> 8) / 16777216.0 * 2.0) - 1.0;
                int k = i + (7 * c);
                double saw1 = (double)((2 * (k % p1)) - p1) / p1;
                double saw2 = (double)((2 * (k % p2)) - p2) / p2;
                double gain = 1.0 - (0.1 * c);
                double v = seg switch
                {
                    0 or 1 => (0.3 * saw1) + (0.02 * noise),
                    2 => 0.2 * noise,
                    3 => 0.0005 * noise,
                    4 => (0.15 * saw1) + (0.12 * saw2) + (0.01 * noise),
                    _ => 0.0,
                };
                pcm[(i * channels) + c] = (float)(gain * v);
            }
        }

        return pcm;
    }

    private static Func<ReadOnlySpan<float>, byte[], int> Create(Config c, out Func<uint> finalRange)
    {
        if (c.Kind == 0)
        {
            OpusEncoder e = new(c.Fs, c.Channels, c.Application);
            if (c.Bitrate != OpusEncoder.Auto)
            {
                e.Bitrate = c.Bitrate;
            }

            e.Vbr = c.Vbr != 0;
            e.VbrConstraint = c.Cvbr != 0;
            e.Complexity = c.Complexity;
            e.InbandFec = c.Fec;
            e.PacketLossPercentage = c.Loss;
            e.Dtx = c.Dtx != 0;
            e.Bandwidth = c.Bandwidth;
            e.Signal = c.Signal;
            e.ForceMode = c.ForceMode;
            finalRange = () => e.FinalRange;
            return (pcm, data) => e.Encode(pcm, c.Frame, data, 0, 1276);
        }

        OpusMultistreamEncoder m = new(c.Fs, c.Channels, c.Family, c.Application);
        if (c.Bitrate != OpusEncoder.Auto)
        {
            m.Bitrate = c.Bitrate;
        }

        foreach (OpusEncoder e in m.Encoders)
        {
            e.Vbr = c.Vbr != 0;
            e.VbrConstraint = c.Cvbr != 0;
            e.Complexity = c.Complexity;
            e.InbandFec = c.Fec;
            e.PacketLossPercentage = c.Loss;
            e.Dtx = c.Dtx != 0;
        }

        finalRange = () => m.FinalRange;
        return (pcm, data) => m.Encode(pcm, c.Frame, data, 0, data.Length);
    }

    private static uint Fnv(ReadOnlySpan<byte> data)
    {
        uint h = 2166136261u;
        foreach (byte b in data)
        {
            h = (h ^ b) * 16777619u;
        }

        return h;
    }

    /// <summary>A planar float frame of the reference signal.</summary>
    private static AudioFrame Tones(int channels, int rate, int samples)
    {
        float[] pcm = Signal(samples, channels, rate);
        AudioFrame frame = new(SampleFormat.F32Planar, ChannelLayout.ForChannelCount(channels), rate, samples, MediaTime.Zero);
        for (int c = 0; c < channels; c++)
        {
            Span<float> plane = frame.GetSpan<float>(c);
            for (int i = 0; i < samples; i++)
            {
                plane[i] = pcm[(i * channels) + c];
            }
        }

        return frame;
    }

    /// <summary>
    /// A melody of harmonic notes with a soft attack and decay, two a second, and a little noise: closer to music than the
    /// reference signal's buzz and white noise, which no encoder codes at its average bitrate.
    /// </summary>
    private static AudioFrame Music(int channels, int rate, int samples)
    {
        AudioFrame frame = new(SampleFormat.F32Planar, ChannelLayout.ForChannelCount(channels), rate, samples, MediaTime.Zero);
        double[] notes = [220, 247, 262, 294, 330, 349, 392, 440];
        Random random = new(7);
        for (int i = 0; i < samples; i++)
        {
            double t = (double)i / rate;
            int note = (int)(t * 2);
            double f = notes[note * 5 % notes.Length];
            double local = t - (note / 2.0);
            double envelope = Math.Min(1, local * 50) * Math.Exp(-local * 3);
            double v = 0;
            for (int h = 1; h <= 6; h++)
            {
                v += Math.Sin(2 * Math.PI * f * h * t) / (h * h);
            }

            double noise = random.NextDouble() - 0.5;
            for (int c = 0; c < channels; c++)
            {
                frame.GetSpan<float>(c)[i] = (float)((0.3 * envelope * v * (1 - (0.2 * c))) + (0.002 * noise));
            }
        }

        return frame;
    }

    /// <summary>The packet sizes of a stream of music, without the last packet, which may be shorter.</summary>
    private static int[] PacketSizes(OpusEncoderOptions options, int channels, int samples)
    {
        using AudioFrame input = Music(channels, 48000, samples);
        using IAudioEncoder encoder = AudioCodecs.Opus.CreateEncoder(new AudioEncoderOptions { SampleRate = 48000, Layout = input.Layout, Opus = options }, MediaContext.Default);
        List<int> sizes = [];
        encoder.Send(input);
        while (encoder.TryReceive(out EncodedPacket? packet))
        {
            using (packet)
            {
                sizes.Add(packet.Data.Length);
            }
        }

        return [.. sizes];
    }

    /// <summary>The signal-to-noise ratio of the decoded file against the input, in dB, and the decoded length.</summary>
    private static double DecodedSnr(AudioFrame input, string path, out int length)
    {
        using AudioReader reader = AudioReader.Open(path, new AudioReaderOptions { SampleFormat = SampleFormat.F32Planar, Errors = DecodeErrorMode.Throw });
        double signal = 0, noise = 0;
        int offset = 0;
        foreach (AudioFrame frame in reader.ReadFrames())
        {
            using (frame)
            {
                for (int c = 0; c < input.ChannelCount; c++)
                {
                    ReadOnlySpan<float> got = frame.AsSpan<float>(c);
                    ReadOnlySpan<float> want = input.AsSpan<float>(c);
                    for (int i = 0; i < frame.SampleCount && offset + i < want.Length; i++)
                    {
                        double d = got[i] - want[offset + i];
                        signal += want[offset + i] * (double)want[offset + i];
                        noise += d * d;
                    }
                }

                offset += frame.SampleCount;
            }
        }

        length = offset;
        return 10 * Math.Log10(signal / Math.Max(noise, 1e-30));
    }
}
