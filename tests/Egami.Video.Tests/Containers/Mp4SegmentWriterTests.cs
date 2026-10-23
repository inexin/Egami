using System.Text;
using System.Xml.Linq;

using Egami.Video.Containers;

using static Egami.Video.Tests.Containers.Mp4WriterTests;

namespace Egami.Video.Tests.Containers;

public class Mp4SegmentWriterTests
{
    public static TheoryData<string> Clips => Mp4ReaderTests.Clips;

    private static string PathOf(string clip) => Path.Combine(Corpus.Directory, "mp4", clip);

    /// <summary>Writes a clip's audio and video as segments into memory; returns the files by name.</summary>
    private static Dictionary<string, byte[]> Segment(string path, Mp4SegmentOptions? options = null)
    {
        using IDemuxer demuxer = MediaFile.OpenDemuxer(path);
        TrackInfo[] tracks = [.. demuxer.Tracks.Where(t => t.Kind is TrackKind.Audio or TrackKind.Video)];
        Dictionary<string, MemoryStream> files = [];
        using (Mp4SegmentWriter writer = new(name => files[name] = new MemoryStream(), tracks, demuxer.Info.Metadata, options))
        {
            while (demuxer.ReadPacket() is { } packet)
            {
                using (packet)
                {
                    if (tracks.Any(t => t.Number == packet.TrackNumber))
                    {
                        writer.WritePacket(packet);
                    }
                }
            }
        }

        // tools/video/check-mp4-segments.py sets this to have MP4Box, ffprobe and the browsers check what we wrote.
        if (Environment.GetEnvironmentVariable("EGAMI_SEGMENTS_OUT") is { Length: > 0 } folder)
        {
            string into = Path.Combine(folder, Path.GetFileNameWithoutExtension(path));
            Directory.CreateDirectory(into);
            foreach ((string name, MemoryStream stream) in files)
            {
                File.WriteAllBytes(Path.Combine(into, name), stream.ToArray());
            }
        }

        return files.ToDictionary(f => f.Key, f => f.Value.ToArray());
    }

    /// <summary>Joins a track's init segment and its media segments in order, as a player appends them.</summary>
    private static byte[] Joined(Dictionary<string, byte[]> files, string track) =>
        [.. files[$"{track}-init.mp4"], .. files.Where(f => f.Key.StartsWith(track + "-", StringComparison.Ordinal) && f.Key.EndsWith(".m4s", StringComparison.Ordinal)).OrderBy(f => f.Key, StringComparer.Ordinal).SelectMany(f => f.Value)];

    [Theory]
    [MemberData(nameof(Clips))]
    public void SegmentsAfterTheirInitReadAsOneFileWithEveryPacket(string clip)
    {
        using IDemuxer original = MediaFile.OpenDemuxer(PathOf(clip));
        List<int> kept = [.. original.Tracks.Select((t, i) => (t, i)).Where(p => p.t.Kind is TrackKind.Audio or TrackKind.Video).Select(p => p.i)];
        if (kept.Count == 0)
        {
            return;
        }

        List<Packet> before = ReadAll(original);
        Dictionary<string, byte[]> files = Segment(PathOf(clip), new Mp4SegmentOptions { SegmentDuration = TimeSpan.FromSeconds(0.25) });
        for (int n = 0; n < kept.Count; n++)
        {
            TrackInfo source = original.Tracks[kept[n]];
            string name = $"{(source.Kind == TrackKind.Video ? "video" : "audio")}{n + 1}";
            using IDemuxer again = MediaFile.OpenDemuxer(new MemoryStream(Joined(files, name)));
            Assert.Equal(source.CodecId, Assert.Single(again.Tracks).CodecId);
            List<Packet> x = [.. before.Where(p => p.Track == kept[n])], y = ReadAll(again);
            Assert.Equal(x.Count, y.Count);
            for (int i = 0; i < x.Count; i++)
            {
                Assert.True(x[i].Data.AsSpan().SequenceEqual(y[i].Data), $"{name} packet {i}: bytes differ");
                Assert.True((x[i].Pts, x[i].Dts, x[i].Key) == (y[i].Pts, y[i].Dts, y[i].Key), $"{name} packet {i}: {(y[i].Pts, y[i].Dts, y[i].Key)}, before {(x[i].Pts, x[i].Dts, x[i].Key)}");
            }

            // Every media segment is styp, sidx, moof and mdat, and a video segment starts with a keyframe.
            foreach ((string file, byte[] bytes) in files.Where(f => f.Key.StartsWith(name + "-", StringComparison.Ordinal) && f.Key.EndsWith(".m4s", StringComparison.Ordinal)))
            {
                Assert.Equal("styp", Encoding.Latin1.GetString(bytes, 4, 4));
                Assert.True(bytes.AsSpan().IndexOf("sidx"u8) > 0, file);
                using IDemuxer segment = MediaFile.OpenDemuxer(new MemoryStream([.. files[$"{name}-init.mp4"], .. bytes]));
                using EncodedPacket? first = segment.ReadPacket();
                Assert.True(first is { IsKeyframe: true }, $"{file} starts without a keyframe");
            }
        }
    }

    [Fact]
    public void PlaylistsListEverySegment()
    {
        Dictionary<string, byte[]> files = Segment(PathOf("mp4-h264-aac.mp4"), new Mp4SegmentOptions { SegmentDuration = TimeSpan.FromSeconds(0.2) });
        string master = Encoding.UTF8.GetString(files["master.m3u8"]);
        Assert.StartsWith("#EXTM3U\n", master, StringComparison.Ordinal);
        Assert.Contains("#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"audio\"", master, StringComparison.Ordinal);
        Assert.Contains("CODECS=\"avc1.4d400b,mp4a.40.2\"", master, StringComparison.Ordinal);
        Assert.Contains("RESOLUTION=160x120", master, StringComparison.Ordinal);
        Assert.Contains("\nvideo1.m3u8\n", master, StringComparison.Ordinal);
        foreach (string track in new[] { "video1", "audio2" })
        {
            string media = Encoding.UTF8.GetString(files[$"{track}.m3u8"]);
            List<string> segments = [.. files.Keys.Where(k => k.StartsWith(track + "-", StringComparison.Ordinal) && k.EndsWith(".m4s", StringComparison.Ordinal)).Order(StringComparer.Ordinal)];
            Assert.Contains($"#EXT-X-MAP:URI=\"{track}-init.mp4\"", media, StringComparison.Ordinal);
            Assert.Equal(segments, media.Split('\n').Where(l => l.EndsWith(".m4s", StringComparison.Ordinal)));
            Assert.EndsWith("#EXT-X-ENDLIST\n", media, StringComparison.Ordinal);
            double total = media.Split('\n').Where(l => l.StartsWith("#EXTINF:", StringComparison.Ordinal)).Sum(l => double.Parse(l[8..^1], System.Globalization.CultureInfo.InvariantCulture));
            Assert.InRange(total, 0.9, 1.2);
        }

        XNamespace dash = "urn:mpeg:dash:schema:mpd:2011";
        XDocument mpd = XDocument.Parse(Encoding.UTF8.GetString(files["manifest.mpd"]));
        List<XElement> representations = [.. mpd.Descendants(dash + "Representation")];
        Assert.Equal(["video1", "audio2"], representations.Select(r => (string)r.Attribute("id")!));
        foreach (XElement representation in representations)
        {
            string id = (string)representation.Attribute("id")!;
            int listed = representation.Descendants(dash + "S").Sum(s => 1 + ((int?)s.Attribute("r") ?? 0));
            Assert.Equal(files.Keys.Count(k => k.StartsWith(id + "-", StringComparison.Ordinal) && k.EndsWith(".m4s", StringComparison.Ordinal)), listed);
        }
    }

    [Fact]
    public void OnlyTheAskedPlaylistsAreWritten()
    {
        Dictionary<string, byte[]> files = Segment(PathOf("mp4-h264-aac.mp4"), new Mp4SegmentOptions { Playlists = SegmentPlaylists.Dash });
        Assert.Contains("manifest.mpd", files.Keys);
        Assert.DoesNotContain(files.Keys, k => k.EndsWith(".m3u8", StringComparison.Ordinal));
    }

    [Fact]
    public void AFolderGetsTheFiles()
    {
        string folder = Path.Combine(Path.GetTempPath(), "egami-segments-" + Guid.NewGuid().ToString("N"));
        try
        {
            using IDemuxer demuxer = MediaFile.OpenDemuxer(PathOf("mp4-vp9-opus.mp4"));
            using (Mp4SegmentWriter writer = new(folder, demuxer.Tracks))
            {
                while (demuxer.ReadPacket() is { } packet)
                {
                    using (packet)
                    {
                        writer.WritePacket(packet);
                    }
                }
            }

            Assert.True(File.Exists(Path.Combine(folder, "video1-init.mp4")));
            Assert.True(File.Exists(Path.Combine(folder, "audio2-00001.m4s")));
            Assert.Contains("CODECS=\"vp09.00.10.08,opus\"", File.ReadAllText(Path.Combine(folder, "master.m3u8")), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void TracksOtherThanAudioAndVideoAreRefused()
    {
        TrackInfo text = new TrackInfo { Number = 1, Kind = TrackKind.Subtitle, CodecId = CodecIds.MovText, TimeBase = new Rational(1, 1000) };
        Assert.Throws<NotSupportedException>(() => new Mp4SegmentWriter(_ => new MemoryStream(), [text]));
    }
}
