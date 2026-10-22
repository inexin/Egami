using Egami.Video.Audio;
using Egami.Video.Audio.Codecs;
using Egami.Video.Containers;

namespace Egami.Video.Tests.Audio;

/// <summary>Ogg reading and writing (V5.1), with clips from opusenc and FFmpeg (tests/video/corpus/audio/opus).</summary>
public class OggTests
{
    internal static string PathOf(string name) => Path.Combine(AudioCorpus.Directory, "opus", name);

    [Theory]
    [InlineData("opus-stereo.opus", 2)]
    [InlineData("opus-mono-speech.opus", 1)]
    [InlineData("opus-51.opus", 6)]
    public void ReadsOggOpusTracks(string name, int channels)
    {
        MediaInfo info = MediaFile.Probe(PathOf(name));
        Assert.Equal("Ogg", info.FormatName);
        AudioTrackInfo track = Assert.IsType<AudioTrackInfo>(Assert.Single(info.Tracks));
        Assert.Equal(CodecIds.Opus, track.CodecId);
        Assert.Equal(48000, track.SampleRate);
        Assert.Equal(channels, track.Channels);
        Assert.NotNull(track.CodecDelay);
        Assert.True(track.CodecDelay!.Value.Ticks > 0);

        // Half a second of source, end trimmed by the last granule position.
        Assert.Equal(0.5, info.Duration!.Value.Seconds, 3);
    }

    [Fact]
    public void PacketTimesFollowTheGranules()
    {
        using IDemuxer demuxer = MediaFile.OpenDemuxer(PathOf("opus-stereo.opus"));
        AudioTrackInfo track = (AudioTrackInfo)demuxer.Tracks[0];
        long expected = -track.CodecDelay!.Value.Ticks;
        long total = 0;
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                Assert.Equal(expected, packet.Timestamp.Rescale(new Rational(1, 48000)).Ticks);
                long duration = packet.Duration!.Value.Rescale(new Rational(1, 48000)).Ticks;
                expected += duration;
                total += duration;
            }
        }

        // Every packet but the trimmed last one is whole; together they cover pre-skip plus the half second.
        Assert.Equal(24000 + track.CodecDelay.Value.Ticks, total);
    }

    [Fact]
    public void ChainedLinksJoinWithChapters()
    {
        MediaInfo info = MediaFile.Probe(PathOf("opus-chained.opus"));
        Assert.Single(info.Tracks);
        Assert.Equal(["One", "Two"], info.Chapters.Select(c => c.Title));
        Assert.Equal(1.0, info.Duration!.Value.Seconds, 2);
        Assert.Equal(0.5, info.Chapters[1].Start.Seconds, 2);

        using IDemuxer demuxer = MediaFile.OpenDemuxer(PathOf("opus-chained.opus"));
        // The first packet starts at minus the pre-skip.
        MediaTime last = MediaTime.FromSeconds(-1);
        int packets = 0;
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                Assert.True(packet.Timestamp >= last);
                last = packet.Timestamp;
                packets++;
            }
        }

        Assert.True(last.Seconds > 0.9, $"The second link's packets end at {last.Seconds} s.");
        Assert.True(packets > 40);
    }

    [Fact]
    public void ReadsOggFlacLikeTheMd5Says()
    {
        List<string> diagnostics = [];
        MediaContext context = MediaContext.Default with { Egami = EgamiContext.Default with { Diagnostics = d => diagnostics.Add(d.Id) } };
        using AudioReader reader = AudioReader.Open(PathOf("ogg-flac.oga"), new AudioReaderOptions { SampleFormat = null, Errors = DecodeErrorMode.Throw }, context);
        Assert.Equal(CodecIds.Flac, reader.Track.CodecId);
        long samples = 0;
        foreach (AudioFrame frame in reader.ReadFrames())
        {
            using (frame)
            {
                samples += frame.SampleCount;
            }
        }

        Assert.Equal(24000, samples);
        Assert.DoesNotContain(diagnostics, d => d.StartsWith("Flac.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("webm-opus.webm")]
    [InlineData("opus-stereo.opus")]
    [InlineData("ogg-flac.oga")]
    [InlineData("ogg-vorbis.ogg")]
    public void RemuxesToOggAndBack(string name)
    {
        string path = AudioRoundTripTests.OutputPath($"remux-{Path.GetFileNameWithoutExtension(name)}.ogg");
        MediaFile.Remux(PathOf(name), path, cancellationToken: TestContext.Current.CancellationToken);
        static List<(long Ticks, byte[] Data)> Packets(string file)
        {
            using IDemuxer demuxer = MediaFile.OpenDemuxer(file);
            TrackInfo audio = demuxer.Tracks.First(t => t.Kind == TrackKind.Audio);
            List<(long, byte[])> list = [];
            while (demuxer.ReadPacket() is { } packet)
            {
                using (packet)
                {
                    if (packet.TrackNumber == audio.Number)
                    {
                        list.Add((packet.Timestamp.Rescale(new Rational(1, 1000)).Ticks, packet.Data.ToArray()));
                    }
                }
            }

            return list;
        }

        List<(long Ticks, byte[] Data)> before = Packets(PathOf(name));
        List<(long Ticks, byte[] Data)> after = Packets(path);
        Assert.Equal(before.Count, after.Count);
        for (int i = 0; i < before.Count; i++)
        {
            Assert.True(before[i].Data.AsSpan().SequenceEqual(after[i].Data), $"Packet {i} differs.");
            if (!name.Contains("vorbis", StringComparison.Ordinal))
            {
                Assert.InRange(after[i].Ticks - before[i].Ticks, -1, 1);
            }
        }

        Assert.Equal(MediaFile.Probe(PathOf(name)).Tracks.First(t => t.Kind == TrackKind.Audio).CodecId, MediaFile.Probe(path).Tracks[0].CodecId);
    }

    [Fact]
    public void WritesFlacIntoOgg()
    {
        using AudioFrame input = AudioRoundTripTests.Signal(2, 16);
        string path = AudioRoundTripTests.OutputPath("flac.oga");
        using (AudioWriter writer = AudioWriter.Create(path, new AudioWriterOptions { Codec = FlacCodec.Instance, Metadata = new MediaMetadata { Tags = new Dictionary<string, string> { ["TITLE"] = "Ogg FLAC" } } }))
        {
            writer.Write(input);
        }

        Assert.Equal("Ogg FLAC", MediaFile.Probe(path).Tracks[0].Tags["TITLE"]);
        AudioRoundTripTests.AssertSameSamples(input, path);
    }

    [Fact]
    public void SkipsDamagedPages()
    {
        // The first link's audio page is damaged; the second link's still reads.
        byte[] data = File.ReadAllBytes(PathOf("opus-chained.opus"));
        data[data.Length / 4] ^= 0x55;
        List<string> warnings = [];
        MediaContext context = MediaContext.Default with { Warnings = w => warnings.Add(w.Code) };
        using IDemuxer demuxer = MediaFile.OpenDemuxer(new MemoryStream(data), context);
        int packets = 0;
        while (demuxer.ReadPacket() is { } packet)
        {
            packet.Dispose();
            packets++;
        }

        Assert.Contains("ogg-crc", warnings);
        Assert.True(packets >= 1);
    }
}
