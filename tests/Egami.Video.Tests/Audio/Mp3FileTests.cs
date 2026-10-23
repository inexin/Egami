using System.IO.Compression;
using System.Text.Json;

using Egami.Video.Audio;
using Egami.Video.Containers;

namespace Egami.Video.Tests.Audio;

/// <summary>
/// MP3 and MP2 files read through AudioReader against FFmpeg's gapless float decode (tools/video/make-audio-corpus.py --mp3):
/// the same number of samples (LAME-tagged files give the source's exact count) and every sample within 2^-14 of full scale.
/// Tags, cover art and chapters come from the ID3 tags; seeks land on the exact sample; the writer's files read back the same.
/// </summary>
public class Mp3FileTests(ITestOutputHelper log)
{
    private const float Tolerance = 1f / 16384;

    public static string Directory => Path.Combine(AppContext.BaseDirectory, "corpus", "audio", "mp3");

    public static TheoryData<string> Clips => [.. Manifest().Keys.Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(Clips))]
    public void DecodesLikeFfmpeg(string name)
    {
        JsonElement entry = Manifest()[name];
        int channels = entry.GetProperty("channels").GetInt32();
        float[] expected = ReadReference(Path.Combine(Directory, name + ".f32.gz"));
        float[][] got = Decode(Path.Combine(Directory, name));
        Assert.Equal(channels, got.Length);
        int samples = expected.Length / channels;
        Assert.Equal(entry.GetProperty("samples").GetInt32(), samples);
        if (name == "m4a-mp3.m4a")
        {
            // The edit list ends the track at 13847 samples (as ffprobe reports); FFmpeg's decode runs 24 samples past it.
            samples = 13847;
        }

        float worst = 0;
        for (int i = 0; i < Math.Min(samples, got[0].Length); i++)
        {
            for (int c = 0; c < channels; c++)
            {
                worst = Math.Max(worst, Math.Abs(got[c][i] - expected[(i * channels) + c]));
            }
        }

        log.WriteLine($"{name}: {got[0].Length} samples (FFmpeg {samples}), max difference {worst:E2}");
        Assert.Equal(samples, got[0].Length);
        Assert.True(worst < Tolerance, $"{name}: samples differ from FFmpeg by up to {worst:E2}.");
    }

    [Theory]
    [InlineData("mp3-cbr-stereo.mp3")]
    [InlineData("mp3-vbr-joint.mp3")]
    [InlineData("mp3-mpeg2-mono.mp3")]
    [InlineData("mp3-mpeg25-8k.mp3")]
    [InlineData("mp3-id3v1-noxing.mp3")]
    [InlineData("mp2-stereo.mp2")]
    [InlineData("mp2-crc-mono.mp2")]
    public void ReadsOnePacketPerFrame(string name)
    {
        JsonElement entry = Manifest()[name];
        using FileStream file = File.OpenRead(Path.Combine(Directory, name));
        using IDemuxer demuxer = Mp3Format.Instance.OpenDemuxer(file, MediaContext.Default, leaveOpen: false);
        int packets = 0;
        while (demuxer.ReadPacket() is { } packet)
        {
            packet.Dispose();
            packets++;
        }

        Assert.Equal(entry.GetProperty("packets").GetInt32(), packets);
    }

    [Theory]
    [InlineData("mp3-cbr-stereo.mp3", CodecIds.Mp3)]
    [InlineData("mp3-mpeg2-mono.mp3", CodecIds.Mp3)]
    [InlineData("mp2-stereo.mp2", CodecIds.Mp2)]
    public void DetectsTheFormat(string name, string codecId)
    {
        MediaInfo info = MediaFile.Probe(Path.Combine(Directory, name));
        Assert.Equal("MP3", info.FormatName);
        AudioTrackInfo track = Assert.IsType<AudioTrackInfo>(Assert.Single(info.Tracks));
        Assert.Equal(codecId, track.CodecId);
        Assert.Equal(Manifest()[name].GetProperty("rate").GetInt32(), track.SampleRate);
    }

    [Fact]
    public void LameTagGivesTheExactDuration()
    {
        MediaInfo info = MediaFile.Probe(Path.Combine(Directory, "mp3-vbr-joint.mp3"));
        AudioTrackInfo track = (AudioTrackInfo)info.Tracks[0];
        Assert.Equal(new MediaTime(13230, new Rational(1, 44100)), track.Duration);
        Assert.NotNull(track.CodecDelay);
        Assert.True(track.CodecDelay!.Value.Rescale(track.TimeBase).Ticks > 529);
    }

    [Fact]
    public void ReadsId3Tags()
    {
        MediaInfo cbr = MediaFile.Probe(Path.Combine(Directory, "mp3-cbr-stereo.mp3"));
        Assert.Equal("Corpus tone", cbr.Tags["TITLE"]);
        Assert.Equal("Egami", cbr.Tags["ARTIST"]);
        Assert.StartsWith("Lavf", cbr.Tags["ENCODER"], StringComparison.Ordinal);

        MediaInfo v1 = MediaFile.Probe(Path.Combine(Directory, "mp3-id3v1-noxing.mp3"));
        Assert.Equal("Corpus tone", v1.Tags["TITLE"]);
        Assert.Equal("Egami", v1.Tags["ARTIST"]);
        Assert.Equal("7", v1.Tags["PART_NUMBER"]);
        Assert.Equal("Pop", v1.Tags["GENRE"]);
        Assert.Equal("2026", v1.Tags["DATE"]);
    }

    [Fact]
    public void ReadsCoverArtAndChapters()
    {
        MediaInfo info = MediaFile.Probe(Path.Combine(Directory, "mp3-id3v23-cover-chapters.mp3"));
        Assert.Equal("Corpus tone", info.Tags["TITLE"]);
        AttachmentInfo cover = Assert.Single(info.Attachments);
        Assert.True(cover.IsCoverArt);
        Assert.Equal("image/png", cover.MediaType);
        Assert.Equal(0x89, cover.Data.Span[0]);
        Assert.Collection(
            info.Chapters,
            c =>
            {
                Assert.Equal("One", c.Title);
                Assert.Equal(TimeSpan.Zero, c.Start.ToTimeSpan());
                Assert.Equal(TimeSpan.FromMilliseconds(150), c.End!.Value.ToTimeSpan());
            },
            c =>
            {
                Assert.Equal("Two", c.Title);
                Assert.Equal(TimeSpan.FromMilliseconds(150), c.Start.ToTimeSpan());
            });
    }

    [Theory]
    [InlineData("mp3-vbr-joint.mp3")]
    [InlineData("mp3-cbr-stereo.mp3")]
    [InlineData("mp3-mpeg2-mono.mp3")]
    [InlineData("mp3-mpeg25-8k.mp3")]
    [InlineData("mp2-stereo.mp2")]
    public void SeeksToTheSample(string name)
    {
        string path = Path.Combine(Directory, name);
        float[][] all = Decode(path);
        using AudioReader reader = AudioReader.Open(path, new AudioReaderOptions { Errors = DecodeErrorMode.Throw });
        Rational sample = new(1, reader.SampleRate);
        int length = all[0].Length;
        foreach (long target in new long[] { length / 2, 1, length / 5, length - 100, 3000 % length })
        {
            reader.Seek(new MediaTime(target, sample));
            int at = (int)target;
            while (at < length && reader.TryRead(out AudioFrame? frame))
            {
                using (frame)
                {
                    if (at == target)
                    {
                        Assert.Equal(new MediaTime(target, sample), frame.Timestamp);
                    }

                    for (int c = 0; c < all.Length; c++)
                    {
                        ReadOnlySpan<float> got = frame.AsSpan<float>(c);
                        for (int i = 0; i < got.Length; i++)
                        {
                            // Bit-exact: the pre-roll fills the bit reservoir, the IMDCT overlap and the synthesis history.
                            Assert.True(got[i] == all[c][at + i], $"{name} seek to {target}: sample {at + i} channel {c} is {got[i]}, a straight decode gives {all[c][at + i]}");
                        }
                    }

                    at += frame.SampleCount;
                }
            }

            Assert.Equal(length, at);
        }
    }

    [Fact]
    public void FastSeekLandsNearTheTime()
    {
        string path = Path.Combine(Directory, "mp3-vbr-joint.mp3");
        MediaContext context = MediaContext.Default.WithContainer(new Mp3Format { FastSeek = true });
        using AudioReader reader = AudioReader.Open(path, null, context);
        reader.Seek(new MediaTime(6615, new Rational(1, 44100)));
        Assert.True(reader.TryRead(out AudioFrame? frame));
        using (frame)
        {
            Assert.InRange(frame.Timestamp.ToTimeSpan().TotalMilliseconds, 100, 200);
        }
    }

    [Fact]
    public void ReadsFromAStreamThatCannotSeek()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Directory, "mp3-vbr-joint.mp3"));
        using IDemuxer demuxer = Mp3Format.Instance.OpenDemuxer(new ForwardOnlyStream(data), MediaContext.Default, leaveOpen: false);
        Assert.False(demuxer.CanSeek);
        int packets = 0;
        while (demuxer.ReadPacket() is { } packet)
        {
            packet.Dispose();
            packets++;
        }

        Assert.Equal(13, packets);
    }

    [Fact]
    public void SkipsJunkBeforeAndBetweenFrames()
    {
        byte[] clean = File.ReadAllBytes(Path.Combine(Directory, "mp3-id3v1-noxing.mp3"));
        byte[] junk = [.. Enumerable.Range(0, 3000).Select(i => (byte)(i * 37)), .. clean[..1000], .. new byte[] { 0xFF, 0xFB, 0x12, 0x00, 0x55 }, .. clean[1000..]];
        using IDemuxer demuxer = Mp3Format.Instance.OpenDemuxer(new MemoryStream(junk), MediaContext.Default, leaveOpen: false);
        int packets = 0;
        while (demuxer.ReadPacket() is { } packet)
        {
            packet.Dispose();
            packets++;
        }

        Assert.Equal(13, packets);
    }

    [Theory]
    [InlineData("mp3-vbr-joint.mp3")]
    [InlineData("mp3-id3v23-cover-chapters.mp3")]
    [InlineData("mka-mp3.mka")]
    [InlineData("m4a-mp3.m4a")]
    public void RemuxesToMp3(string name)
    {
        string source = Path.Combine(Directory, name);
        string mp3 = AudioRoundTripTests.OutputPath("remux-" + Path.ChangeExtension(name, ".mp3"));
        MediaFile.Remux(source, mp3, cancellationToken: TestContext.Current.CancellationToken);
        MediaInfo before = MediaFile.Probe(source);
        MediaInfo after = MediaFile.Probe(mp3);
        Assert.Equal("MP3", after.FormatName);
        Assert.Equal(before.Tags.GetValueOrDefault("TITLE"), after.Tags.GetValueOrDefault("TITLE"));
        Assert.Equal(before.Chapters.Select(c => (c.Title, c.Start)), after.Chapters.Select(c => (c.Title, c.Start)));
        Assert.Equal(before.Attachments.Select(a => (a.Name, a.Data.Length)), after.Attachments.Select(a => (a.Name, a.Data.Length)));
        float[][] want = Decode(source);
        float[][] got = Decode(mp3);
        if (name == "mka-mp3.mka")
        {
            // FFmpeg's Matroska file keeps the encoder delay as audio (no CodecDelay) and cuts the end with DiscardPadding. A LAME
            // tag's delay is never below the decoder's 529 samples, so it cannot say that: the MP3 keeps the last frame's padding.
            Assert.Equal(14976, got[0].Length);
            got = [.. got.Select(c => c[..want[0].Length])];
        }
        else
        {
            Assert.Equal(before.Tracks[0].Duration, after.Tracks[0].Duration);
        }

        Assert.Equal(want[0].Length, got[0].Length);
        for (int c = 0; c < want.Length; c++)
        {
            Assert.Equal(want[c], got[c]);
        }
    }

    internal static float[][] Decode(string path, MediaContext? context = null)
    {
        using AudioReader reader = AudioReader.Open(path, new AudioReaderOptions { Errors = DecodeErrorMode.Throw }, context);
        List<float>[] channels = [.. Enumerable.Range(0, reader.Layout.ChannelCount).Select(_ => new List<float>())];
        foreach (AudioFrame frame in reader.ReadFrames())
        {
            using (frame)
            {
                for (int c = 0; c < channels.Length; c++)
                {
                    channels[c].AddRange(frame.AsSpan<float>(c).ToArray());
                }
            }
        }

        return [.. channels.Select(c => c.ToArray())];
    }

    private static Dictionary<string, JsonElement> Manifest() =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(Path.Combine(Directory, "manifest.json")))!;

    private static float[] ReadReference(string path)
    {
        using GZipStream gzip = new(File.OpenRead(path), CompressionMode.Decompress);
        using MemoryStream raw = new();
        gzip.CopyTo(raw);
        return [.. System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(raw.ToArray())];
    }

    private sealed class ForwardOnlyStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;

        public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();

        public override long Position { get => base.Position; set => throw new NotSupportedException(); }

        public override long Length => throw new NotSupportedException();
    }
}
