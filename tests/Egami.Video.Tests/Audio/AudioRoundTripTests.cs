using Egami.Video.Audio;
using Egami.Video.Audio.Codecs;
using Egami.Video.Containers;

namespace Egami.Video.Tests.Audio;

/// <summary>Writes audio with PCM and FLAC into every container that holds it and reads it back sample for sample.</summary>
public class AudioRoundTripTests
{
    /// <summary>A frame of noisy tones: deterministic, with enough detail that FLAC's predictors are exercised.</summary>
    internal static AudioFrame Signal(int channels, int bits, int rate = 44100, int samples = 10000, int seed = 1)
    {
        SampleFormat format = bits <= 16 ? SampleFormat.S16Planar : SampleFormat.S32Planar;
        AudioFrame frame = new(format, ChannelLayout.ForChannelCount(channels), rate, samples, MediaTime.Zero) { ValidBits = bits };
        Random random = new(seed);
        double full = Math.ScaleB(1.0, bits - 1) - 1;
        for (int c = 0; c < channels; c++)
        {
            for (int i = 0; i < samples; i++)
            {
                double v = (0.5 * Math.Sin(2 * Math.PI * (220 + (110 * c)) * i / rate)) + (0.2 * Math.Sin(2 * Math.PI * 3000 * i / rate)) + (0.05 * (random.NextDouble() - 0.5));
                long s = (long)Math.Round(v * full);
                if (format == SampleFormat.S16Planar)
                {
                    frame.GetSpan<short>(c)[i] = (short)s;
                }
                else
                {
                    frame.GetSpan<int>(c)[i] = (int)s;
                }
            }
        }

        return frame;
    }

    public static IEnumerable<object[]> FlacCases()
    {
        foreach (int level in new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 })
        {
            yield return [level, 2, 16];
        }

        yield return [5, 1, 8];
        yield return [5, 6, 24];
        yield return [5, 2, 24];
        yield return [5, 2, 32];
        yield return [8, 8, 20];
        yield return [0, 1, 4];
    }

    [Theory]
    [MemberData(nameof(FlacCases))]
    public void FlacIsLossless(int level, int channels, int bits)
    {
        using AudioFrame input = Signal(channels, bits);
        string path = OutputPath($"egami-l{level}-{channels}ch-{bits}.flac");
        using (AudioWriter writer = AudioWriter.Create(path, new AudioWriterOptions { Codec = FlacCodec.Instance, Level = level }))
        {
            // Odd frame sizes, so the encoder's re-chunking is exercised.
            for (int start = 0; start < input.SampleCount; start += 1777)
            {
                using AudioFrame part = input.Slice(start, Math.Min(1777, input.SampleCount - start));
                writer.Write(part);
            }
        }

        AssertSameSamples(input, path);
        AudioTrackInfo track = (AudioTrackInfo)MediaFile.Probe(path).Tracks[0];
        Assert.Equal(bits, track.BitsPerSample);
        Assert.Equal(input.SampleCount, track.Duration!.Value.Ticks);
    }

    [Theory]
    [InlineData(".wav", 16)]
    [InlineData(".wav", 24)]
    [InlineData(".wav", 8)]
    [InlineData(".w64", 32)]
    [InlineData(".rf64", 16)]
    [InlineData(".mka", 24)]
    [InlineData(".mp4", 16)]
    public void PcmIsExact(string extension, int bits)
    {
        using AudioFrame input = Signal(2, bits);
        string path = OutputPath($"pcm-{bits}{extension}");
        using (AudioWriter writer = AudioWriter.Create(path))
        {
            writer.Write(input);
        }

        AssertSameSamples(input, path);
    }

    [Theory]
    [InlineData(".mka")]
    [InlineData(".mp4")]
    [InlineData(".flac")]
    public void FlacInEveryContainer(string extension)
    {
        using AudioFrame input = Signal(2, 16);
        string path = OutputPath($"flac-in{extension}");
        using (AudioWriter writer = AudioWriter.Create(path, new AudioWriterOptions { Codec = FlacCodec.Instance }))
        {
            writer.Write(input);
        }

        Assert.Equal(CodecIds.Flac, MediaFile.Probe(path).Tracks[0].CodecId);
        AssertSameSamples(input, path);
    }

    [Fact]
    public void FloatWav()
    {
        using AudioFrame ints = Signal(2, 24);
        using AudioFrame input = ints.Convert(SampleFormat.F32Planar);
        string path = OutputPath("float.wav");
        using (AudioWriter writer = AudioWriter.Create(path, new AudioWriterOptions { Codec = PcmCodec.FloatingPoint }))
        {
            writer.Write(input);
        }

        Assert.Equal(CodecIds.PcmFloat, MediaFile.Probe(path).Tracks[0].CodecId);
        using AudioReader reader = AudioReader.Open(path);
        int offset = 0;
        foreach (AudioFrame frame in reader.ReadFrames())
        {
            using (frame)
            {
                Assert.True(frame.AsSpan<float>(1).SequenceEqual(input.AsSpan<float>(1).Slice(offset, frame.SampleCount)));
                offset += frame.SampleCount;
            }
        }

        Assert.Equal(input.SampleCount, offset);
    }

    [Fact]
    public void WavKeepsTagsChaptersAndLayout()
    {
        using AudioFrame input = Signal(6, 16, rate: 8000, samples: 16000);
        MediaMetadata metadata = new()
        {
            Tags = new Dictionary<string, string> { ["TITLE"] = "Round trip", ["ARTIST"] = "Egami" },
            Chapters = [new ChapterInfo(MediaTime.Zero, null, "One"), new ChapterInfo(MediaTime.FromSeconds(1), null, "Two")],
        };
        string path = OutputPath("tags.wav");
        using (AudioWriter writer = AudioWriter.Create(path, new AudioWriterOptions { Metadata = metadata, Layout = ChannelLayout.Surround51Side }))
        {
            writer.Write(input);
        }

        MediaInfo info = MediaFile.Probe(path);
        Assert.Equal("Round trip", info.Tags["TITLE"]);
        Assert.Equal("Egami", info.Tags["ARTIST"]);
        Assert.Equal(["One", "Two"], info.Chapters.Select(c => c.Title));
        Assert.Equal(MediaTime.FromSeconds(1), info.Chapters[1].Start);
        Assert.Equal(ChannelLayout.Surround51Side, ((AudioTrackInfo)info.Tracks[0]).Layout);
    }

    [Fact]
    public void FlacKeepsTagsAndCoverArt()
    {
        using AudioFrame input = Signal(2, 16);
        byte[] png = File.ReadAllBytes(AudioCorpus.PathOf("flac-tags-picture.flac"))[..64];
        MediaMetadata metadata = new()
        {
            Tags = new Dictionary<string, string> { ["TITLE"] = "Round trip" },
            Attachments = [new AttachmentInfo("cover.png", "image/png", png)],
        };
        string path = OutputPath("tags.flac");
        using (AudioWriter writer = AudioWriter.Create(path, new AudioWriterOptions { Metadata = metadata }))
        {
            writer.Write(input);
        }

        MediaInfo info = MediaFile.Probe(path);
        Assert.Equal("Round trip", info.Tags["TITLE"]);
        Assert.True(Assert.Single(info.Attachments).Data.Span.SequenceEqual(png));
    }

    [Fact]
    public void NonSeekableOutputsStream()
    {
        // WAV with the streaming sizes and FLAC without padding: both read back whole.
        using AudioFrame input = Signal(2, 16);
        foreach (string format in new[] { "wav", "flac" })
        {
            using MemoryStream buffer = new();
            using (AudioWriter writer = AudioWriter.Create(new WriteOnlyStream(buffer), format))
            {
                writer.Write(input);
            }

            using AudioReader reader = AudioReader.Open(new MemoryStream(buffer.ToArray()), new AudioReaderOptions { SampleFormat = null });
            Assert.Equal(input.SampleCount, reader.ReadFrames().Sum(f =>
            {
                using (f)
                {
                    return f.SampleCount;
                }
            }));
        }
    }

    [Fact]
    public void Rf64AfterFourGigabytes()
    {
        // The real switch needs 4 GB of data; the test lowers the threshold the writer switches at.
        using AudioFrame input = Signal(2, 16);
        string path = OutputPath("auto.wav");
        Egami.Video.Containers.Wav.WavMuxer.Rf64Threshold = 1000;
        try
        {
            using AudioWriter writer = AudioWriter.Create(path);
            writer.Write(input);
        }
        finally
        {
            Egami.Video.Containers.Wav.WavMuxer.Rf64Threshold = uint.MaxValue;
        }

        Assert.Equal("RF64"u8.ToArray(), File.ReadAllBytes(path)[..4]);
        AssertSameSamples(input, path);
    }

    [Fact]
    public void TranscodesAudioAndCopiesVideo()
    {
        string path = OutputPath("transcoded.mkv");
        MediaFile.Transcode(Corpus.PathOf("mkv-mjpeg-pcm.mkv"), path, audio: FlacCodec.Instance, cancellationToken: TestContext.Current.CancellationToken);
        MediaInfo info = MediaFile.Probe(path);
        Assert.Contains(info.Tracks, t => t.CodecId == CodecIds.MotionJpeg);
        Assert.Contains(info.Tracks, t => t.CodecId == CodecIds.Flac);

        using AudioReader original = AudioReader.Open(Corpus.PathOf("mkv-mjpeg-pcm.mkv"), new AudioReaderOptions { SampleFormat = null });
        using AudioReader flac = AudioReader.Open(path, new AudioReaderOptions { SampleFormat = null });
        Assert.Equal(AudioCorpus.Hash(original.ReadFrames(), 16, false), AudioCorpus.Hash(flac.ReadFrames(), 16, false));
    }

    [Fact]
    public void VideoWriterEncodesAnAudioTrack()
    {
        using AudioFrame input = Signal(2, 16, rate: 48000, samples: 48000);
        string path = OutputPath("with-audio.mkv");
        using (VideoWriter writer = VideoWriter.Create(path, new VideoWriterOptions { AudioCodec = FlacCodec.Instance, AudioSampleRate = 48000, AudioLayout = ChannelLayout.Stereo }))
        {
            writer.WriteAudio(input);
            using Raster<Egami.Pixels.Rgb8> picture = Raster.Create<Egami.Pixels.Rgb8>(32, 32);
            for (int i = 0; i < 30; i++)
            {
                writer.WriteFrame(picture);
            }
        }

        MediaInfo info = MediaFile.Probe(path);
        Assert.Equal(CodecIds.MotionJpeg, info.Tracks[0].CodecId);
        Assert.Equal(CodecIds.Flac, info.Tracks[1].CodecId);
        AssertSameSamples(input, path);
    }

    internal static string OutputPath(string name)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "audio-output");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, name);
    }

    internal static void AssertSameSamples(AudioFrame expected, string path)
    {
        using AudioReader reader = AudioReader.Open(path, new AudioReaderOptions { SampleFormat = null, Errors = DecodeErrorMode.Throw });
        using AudioFrame wide = expected.Convert(SampleFormat.S64Planar);
        int offset = 0;
        foreach (AudioFrame frame in reader.ReadFrames())
        {
            using (frame)
            using (AudioFrame got = frame.Convert(SampleFormat.S64Planar))
            {
                for (int c = 0; c < expected.ChannelCount; c++)
                {
                    Assert.True(
                        got.AsSpan<long>(c).SequenceEqual(wide.AsSpan<long>(c).Slice(offset, frame.SampleCount)),
                        $"Channel {c} differs in the {frame.SampleCount} samples from {offset}.");
                }

                offset += frame.SampleCount;
            }
        }

        Assert.Equal(expected.SampleCount, offset);
    }

    private sealed class WriteOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }
}
