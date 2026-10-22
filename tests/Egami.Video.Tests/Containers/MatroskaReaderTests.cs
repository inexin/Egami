using System.IO.Compression;

using Egami.Video.Containers;

using static Egami.Video.Tests.Containers.EbmlBuilder;

namespace Egami.Video.Tests.Containers;

public class MatroskaReaderTests
{
    public static TheoryData<string> Clips => [.. Corpus.Clips(".mkv", ".webm")];

    [Theory]
    [MemberData(nameof(Clips))]
    public void PacketsMatchFfprobe(string clip)
    {
        string path = Corpus.PathOf(clip);
        Probe probe = Corpus.ReadProbe(path);
        using IDemuxer demuxer = MediaFile.OpenDemuxer(path);
        Assert.Equal(clip.EndsWith(".webm", StringComparison.Ordinal) ? "WebM" : "Matroska", demuxer.Info.FormatName);
        Assert.Equal(probe.Streams.Count, demuxer.Tracks.Count);
        for (int i = 0; i < probe.Streams.Count; i++)
        {
            ProbeStream expected = probe.Streams[i];
            TrackInfo track = demuxer.Tracks[i];
            if (track is VideoTrackInfo video)
            {
                Assert.Equal("video", expected.Type);
                Assert.Equal((expected.Width, expected.Height), (video.Width, video.Height));
            }
            else if (track is AudioTrackInfo audio)
            {
                Assert.Equal("audio", expected.Type);

                // FFmpeg reports Opus at its 48 kHz decode rate, whatever the track says.
                Assert.Equal(expected.Channels, audio.Channels);
                Assert.True(audio.CodecId == CodecIds.Opus || expected.SampleRate == audio.SampleRate);
            }
        }

        // Vorbis laces carry no DefaultDuration; FFmpeg times the later frames by parsing Vorbis.
        Corpus.AssertMatchesProbe(probe, Corpus.ReadAll(demuxer), checkLacedTimestamps: !clip.Contains("vorbis", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Clips))]
    public void NonSeekableStreamsGiveTheSamePackets(string clip)
    {
        byte[] file = File.ReadAllBytes(Corpus.PathOf(clip));
        using IDemuxer seekable = MediaFile.OpenDemuxer(new MemoryStream(file));
        using IDemuxer streaming = MediaFile.OpenDemuxer(new NonSeekableStream(file));
        Assert.Equal(Corpus.ReadAll(seekable), Corpus.ReadAll(streaming));
    }

    [Theory]
    [InlineData("mkv-mjpeg-pcm.mkv")]
    [InlineData("mkv-mp3-nocues.mkv")]
    [InlineData("mkv-mjpeg-live.mkv")]
    [InlineData("webm-vp8-opus.webm")]
    public void SeekLandsOnTheKeyframeAtOrBeforeTheTime(string clip)
    {
        string path = Corpus.PathOf(clip);
        using IDemuxer reader = MediaFile.OpenDemuxer(path);
        int video = reader.Tracks[0].Number;
        List<(MediaTime Time, bool Key)> frames = [];
        while (reader.ReadPacket() is { } packet)
        {
            using (packet)
            {
                if (packet.TrackNumber == video)
                {
                    frames.Add((packet.Timestamp, packet.IsKeyframe));
                }
            }
        }

        using IDemuxer demuxer = MediaFile.OpenDemuxer(path);
        foreach (double seconds in (double[])[0, 0.03, 0.5, 0.52, 1.0, 1.37, 1.99, 5])
        {
            MediaTime time = MediaTime.FromSeconds(seconds);
            MediaTime expected = frames.Where(f => f.Key && f.Time <= time).Select(f => f.Time).DefaultIfEmpty(frames[0].Time).Max();
            demuxer.Seek(time);
            EncodedPacket first = FirstOf(demuxer, video);
            Assert.True(first.IsKeyframe);
            Assert.Equal(expected, first.Timestamp);
        }
    }

    [Fact]
    public void SeekThenReadGivesTheRestOfTheFile()
    {
        string path = Corpus.PathOf("mkv-mjpeg-pcm.mkv");
        using IDemuxer full = MediaFile.OpenDemuxer(path);
        List<ReadPacket> all = Corpus.ReadAll(full);
        using IDemuxer demuxer = MediaFile.OpenDemuxer(path);
        demuxer.Seek(MediaTime.FromSeconds(1.2));
        List<ReadPacket> tail = Corpus.ReadAll(demuxer);
        Assert.Equal(all[^tail.Count..], tail);
        Assert.Equal(0, tail[0].Track);
    }

    [Fact]
    public void XiphEbmlAndFixedLacesSplitIntoPackets()
    {
        byte[] a = [1, 2, 3], b = [4, 5, 6, 7, 8], c = [9, 10];

        // Xiph: count-1, then the first two sizes as plain bytes.
        byte[] xiph = [2, 3, 5, .. a, .. b, .. c];

        // EBML: count-1, first size as a VINT (0x83 = 3), then the difference 5 - 3 = +2 as a signed one-byte VINT (bias 63).
        byte[] ebml = [2, 0x83, 0x80 | (63 + 2), .. a, .. b, .. c];

        // Fixed: three frames of two bytes.
        byte[] fixedLace = [2, 1, 1, 2, 2, 3, 3];
        byte[] file = MatroskaFile("matroska", [AudioTrack(1, "A_PCM/INT/LIT", defaultDuration: 10_000_000)],
            Cluster(1000,
                SimpleBlock(1, 0, 0x80 | 0x02, xiph),
                SimpleBlock(1, 100, 0x80 | 0x06, ebml),
                SimpleBlock(1, 200, 0x80 | 0x04, fixedLace)));
        using IDemuxer demuxer = MediaFile.OpenDemuxer(new MemoryStream(file));
        List<(long Time, byte[] Data)> packets = Read(demuxer);
        Assert.Equal(
            [(1000L, a), (1010, b), (1020, c), (1100, a), (1110, b), (1120, c), (1200, [1, 1]), (1210, [2, 2]), (1220, [3, 3])],
            packets.Select(p => (p.Time, p.Data)), new LaceComparer());
    }

    [Fact]
    public void UnknownSizeSegmentsAndClustersAreRead()
    {
        byte[] file =
        [
            .. Element(0x1A45DFA3, Text(0x4282, "webm")),
            .. UnknownSize(0x18538067,
                Element(0x1654AE6B, AudioTrack(1, "A_OPUS")),
                UnknownSize(0x1F43B675, Uint(0xE7, 0), SimpleBlock(1, 0, 0x80, [1]), SimpleBlock(1, 20, 0x80, [2])),
                UnknownSize(0x1F43B675, Uint(0xE7, 40), SimpleBlock(1, 0, 0x80, [3]))),
        ];
        using IDemuxer demuxer = MediaFile.OpenDemuxer(new MemoryStream(file));
        Assert.Equal("WebM", demuxer.Info.FormatName);
        Assert.Equal([0L, 20, 40], Read(demuxer).Select(p => p.Time));
    }

    [Fact]
    public void HeaderStrippingAndZlibAreUndone()
    {
        static byte[] Track(int number, byte[] compression) => Element(0xAE,
            Uint(0xD7, (ulong)number), Uint(0x83, 2), Text(0x86, "A_AC3"),
            Element(0x6D80, Element(0x6240, Element(0x5034, compression))));

        MemoryStream deflated = new();
        using (ZLibStream z = new(deflated, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write([10, 20, 30, 40]);
        }

        byte[] file = MatroskaFile("matroska",
            [Track(1, [.. Uint(0x4254, 3), .. Element(0x4255, [0x0B, 0x77])]), Track(2, Uint(0x4254, 0))],
            Cluster(0, SimpleBlock(1, 0, 0x80, [1, 2]), SimpleBlock(2, 0, 0x80, deflated.ToArray())));
        using IDemuxer demuxer = MediaFile.OpenDemuxer(new MemoryStream(file));
        List<(long Time, byte[] Data)> packets = Read(demuxer);
        Assert.Equal([0x0B, 0x77, 1, 2], packets[0].Data);
        Assert.Equal([10, 20, 30, 40], packets[1].Data);
    }

    [Fact]
    public void BlockGroupsUseReferenceBlockForKeyframesAndDuration()
    {
        static byte[] Group(short time, bool reference) => Element(0xA0,
            [Element(0xA1, [0x81, (byte)(time >> 8), (byte)time, 0, 7]), Uint(0x9B, 33), .. reference ? (byte[][])[Element(0xFB, [0xFF])] : []]);
        byte[] file = MatroskaFile("matroska", [AudioTrack(1, "A_AAC")], Cluster(0, Group(0, false), Group(33, true)));
        using IDemuxer demuxer = MediaFile.OpenDemuxer(new MemoryStream(file));
        using EncodedPacket first = demuxer.ReadPacket()!;
        using EncodedPacket second = demuxer.ReadPacket()!;
        Assert.True(first.IsKeyframe);
        Assert.False(second.IsKeyframe);
        Assert.Equal(new MediaTime(33, new Rational(1, 1000)), first.Duration);
    }

    [Fact]
    public void GarbageBetweenClustersIsSkipped()
    {
        byte[] file = MatroskaFile("matroska", [AudioTrack(1, "A_AAC")],
            Cluster(0, SimpleBlock(1, 0, 0x80, [1])),
            [0x00, 0x00, 0x13, 0x37, 0x00],
            Cluster(10, SimpleBlock(1, 0, 0x80, [2])));
        using IDemuxer demuxer = MediaFile.OpenDemuxer(new MemoryStream(file));
        Assert.Equal([0L, 10], Read(demuxer).Select(p => p.Time));
        using IDemuxer streaming = MediaFile.OpenDemuxer(new NonSeekableStream(file));
        Assert.Equal([0L, 10], Read(streaming).Select(p => p.Time));
    }

    [Fact]
    public void OtherDocTypesAreRejected()
    {
        byte[] file = MatroskaFile("notmatroska", [AudioTrack(1, "A_AAC")]);
        Assert.Throws<UnsupportedFormatException>(() => MediaFile.OpenDemuxer(new MemoryStream(file)));
    }

    private static EncodedPacket FirstOf(IDemuxer demuxer, int track)
    {
        while (demuxer.ReadPacket() is { } packet)
        {
            if (packet.TrackNumber == track)
            {
                return packet;
            }

            packet.Dispose();
        }

        throw new InvalidOperationException("No packet.");
    }

    private static List<(long Time, byte[] Data)> Read(IDemuxer demuxer)
    {
        List<(long, byte[])> packets = [];
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                packets.Add((packet.Timestamp.Rescale(new Rational(1, 1000)).Ticks, packet.Data.ToArray()));
            }
        }

        return packets;
    }

    private sealed class LaceComparer : IEqualityComparer<(long, byte[])>
    {
        public bool Equals((long, byte[]) x, (long, byte[]) y) => x.Item1 == y.Item1 && x.Item2.AsSpan().SequenceEqual(y.Item2);

        public int GetHashCode((long, byte[]) obj) => obj.Item1.GetHashCode();
    }
}
