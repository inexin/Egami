using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

using Egami.Metadata;
using Egami.Video.Containers;

namespace Egami.Video.Tests.Containers;

public class Mp4ReaderTests
{
    public static TheoryData<string> Clips => [.. System.IO.Directory.GetFiles(Path.Combine(Corpus.Directory, "mp4"))
        .Where(f => !f.EndsWith(".json", StringComparison.Ordinal) && File.Exists(f + ".json")).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal)];

    private static string PathOf(string clip) => Path.Combine(Corpus.Directory, "mp4", clip);

    /// <summary>
    /// ffprobe's streams that are tracks: the chapter text track (a data stream of text samples) and covr cover art (an
    /// attached picture) are not, since they come back as chapters and attachments.
    /// </summary>
    private static List<JsonElement> TrackStreams(JsonElement root) =>
        [.. root.GetProperty("streams").EnumerateArray().Where(s =>
            !(s.TryGetProperty("disposition", out JsonElement d) && d.TryGetProperty("attached_pic", out _))
            && !(s.GetProperty("codec_type").GetString() == "data" && s.GetProperty("codec_tag_string").GetString() == "text"))];

    /// <summary>
    /// Where we read the spec and FFmpeg differs: FFmpeg adds a fragment's most negative composition offset to its presentation
    /// times so none comes before its decode time (CMAF writes no edit list, so the first frame shows at 0 by the spec).
    /// </summary>
    private static readonly Dictionary<string, long> PtsOffsets = new() { ["mp4-cmaf.mp4"] = 512 };

    /// <summary>
    /// Packets (clip, stream, packet) whose duration FFmpeg reports otherwise: it cuts a delayed track at the media edit's own
    /// length, not counting the empty edit before it (we cut at the edit's end in the movie); it gives MP4Box's last audio
    /// fragment (and its own fragmenter's) a full frame's length instead of the shorter one the file gives; and for a sample FFmpeg's fragmenter stretched to 1127 ticks it reports
    /// the frame rate's 512. We give what the file says.
    /// </summary>
    private static readonly HashSet<string> DurationsDiffer = ["mp4-editlist-delay.mp4:1:8", "mp4box-frag-mfra.mp4:1:8", "mp4-frag.mp4:0:0", "mp4-frag.mp4:1:8"];

    [Theory]
    [MemberData(nameof(Clips))]
    public void PacketsMatchFfprobe(string clip)
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(PathOf(clip) + ".json"));
        JsonElement root = json.RootElement;
        List<JsonElement> streams = TrackStreams(root);
        using IDemuxer demuxer = MediaFile.OpenDemuxer(PathOf(clip));
        Assert.Equal(clip.EndsWith(".mov", StringComparison.Ordinal) ? "QuickTime" : "MP4", demuxer.Info.FormatName);
        Assert.Equal(streams.Count, demuxer.Tracks.Count);

        Dictionary<int, int> streamOf = [];
        for (int i = 0; i < streams.Count; i++)
        {
            JsonElement s = streams[i];
            TrackInfo track = demuxer.Tracks[i];
            streamOf[track.Number] = s.GetProperty("index").GetInt32();
            Assert.Equal(Rational.Parse(s.GetProperty("time_base").GetString()!), track.TimeBase);
            string type = s.GetProperty("codec_type").GetString()!;
            Assert.Equal(type switch { "video" => TrackKind.Video, "audio" => TrackKind.Audio, "subtitle" => TrackKind.Subtitle, _ => TrackKind.Other }, track.Kind);
            string? codec = s.TryGetProperty("codec_name", out JsonElement c) ? c.GetString() : null;
            Assert.Equal(codec switch
            {
                "mjpeg" => CodecIds.MotionJpeg,
                "vp8" => CodecIds.Vp8,
                "vp9" => CodecIds.Vp9,
                "av1" => CodecIds.Av1,
                "h264" => CodecIds.Avc,
                "hevc" => CodecIds.Hevc,
                "aac" => CodecIds.Aac,
                "opus" => CodecIds.Opus,
                "flac" => CodecIds.Flac,
                "pcm_s16le" or "pcm_s24le" => CodecIds.PcmLittleEndian,
                "pcm_s16be" => CodecIds.PcmBigEndian,
                "mov_text" => CodecIds.MovText,
                _ => CodecIds.QuickTimeOther,
            }, track.CodecId);
            if (track is VideoTrackInfo video)
            {
                Assert.Equal((s.GetProperty("width").GetInt32(), s.GetProperty("height").GetInt32()), (video.Width, video.Height));
                Assert.Equal(s.GetProperty("r_frame_rate").GetString(), $"{video.FrameRate?.Numerator}/{video.FrameRate?.Denominator}");
            }
            else if (track is AudioTrackInfo audio)
            {
                Assert.Equal(int.Parse(s.GetProperty("sample_rate").GetString()!, CultureInfo.InvariantCulture), audio.SampleRate);
                Assert.Equal(s.GetProperty("channels").GetInt32(), audio.Channels);
            }
        }

        // Every packet of each stream: times, duration, size, flags and bytes, in ffprobe's order within the stream.
        Dictionary<int, List<JsonElement>> expected = root.GetProperty("packets").EnumerateArray()
            .GroupBy(p => p.GetProperty("stream_index").GetInt32()).ToDictionary(g => g.Key, g => g.ToList());
        Dictionary<int, int> seen = [];
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                int stream = streamOf[packet.TrackNumber];
                int n = seen[stream] = seen.GetValueOrDefault(stream) + 1;
                Assert.True(expected[stream].Count >= n, $"stream {stream}: more than ffprobe's {expected[stream].Count} packets");
                JsonElement want = expected[stream][n - 1];
                string where = $"{clip} stream {stream} packet {n - 1}";
                Assert.True(want.GetProperty("size").GetString() == packet.Data.Length.ToString(CultureInfo.InvariantCulture), $"{where}: size {packet.Data.Length}, ffprobe {want.GetProperty("size")}");
                long ptsOffset = PtsOffsets.GetValueOrDefault(clip);
                Assert.True(want.GetProperty("pts").GetInt64() == packet.Timestamp.Ticks + ptsOffset, $"{where}: pts {packet.Timestamp.Ticks}, ffprobe {want.GetProperty("pts")}");
                Assert.True(want.GetProperty("dts").GetInt64() == (packet.DecodeTimestamp ?? packet.Timestamp).Ticks, $"{where}: dts {(packet.DecodeTimestamp ?? packet.Timestamp).Ticks}, ffprobe {want.GetProperty("dts")}");
                bool differs = DurationsDiffer.Contains($"{clip}:{stream}:{n - 1}");
                Assert.True(differs || want.GetProperty("duration").GetInt64() == (packet.Duration?.Ticks ?? 0), $"{where}: duration {packet.Duration?.Ticks}, ffprobe {want.GetProperty("duration")}");
                string flags = want.GetProperty("flags").GetString()!;
                Assert.True(flags.Contains('K', StringComparison.Ordinal) == packet.IsKeyframe, $"{where}: keyframe {packet.IsKeyframe}, ffprobe {flags}");
                Assert.True(flags.Contains('D', StringComparison.Ordinal) == packet.IsDiscardable, $"{where}: discardable {packet.IsDiscardable}, ffprobe {flags}");
#pragma warning disable CA5351 // ffprobe's packet hashes are MD5
                Assert.Equal(want.GetProperty("md5").GetString(), Convert.ToHexStringLower(MD5.HashData(packet.Data.Span)));
#pragma warning restore CA5351
            }
        }

        foreach ((int stream, List<JsonElement> packets) in expected.Where(e => streamOf.ContainsValue(e.Key)))
        {
            Assert.True(packets.Count == seen.GetValueOrDefault(stream), $"stream {stream}: {seen.GetValueOrDefault(stream)} packets, ffprobe {packets.Count}");
        }
    }

    [Theory]
    [MemberData(nameof(Clips))]
    public void NonSeekableStreamsGiveTheSamePackets(string clip)
    {
        byte[] file = File.ReadAllBytes(PathOf(clip));
        using IDemuxer seekable = MediaFile.OpenDemuxer(new MemoryStream(file));
        bool moovFirst;
        try
        {
            using IDemuxer streaming = MediaFile.OpenDemuxer(new NonSeekableStream(file));
            moovFirst = true;
            Assert.Equal(Corpus.ReadAll(seekable), Corpus.ReadAll(streaming));
        }
        catch (NotSupportedException)
        {
            // moov after mdat, or samples out of file order, cannot be read from the start without seeking.
            moovFirst = false;
        }

        Assert.True(moovFirst || file.AsSpan().IndexOf("mdat"u8) < file.AsSpan().IndexOf("moov"u8) || seekable.Tracks.Count > 0);
    }

    [Theory]
    [InlineData("mov-rotate90.mov", ImageOrientation.Rotate270)]
    [InlineData("mp4-rotate270-hflip.mp4", ImageOrientation.Transpose)]
    [InlineData("mp4-mjpeg.mp4", ImageOrientation.Normal)]
    public void DisplayMatrixIsTheOrientation(string clip, ImageOrientation orientation)
    {
        // ffprobe's rotation is counter-clockwise: 90 needs a quarter turn clockwise undone, EXIF 8 (Rotate270).
        MediaInfo info = MediaFile.Probe(PathOf(clip));
        Assert.Equal(orientation, info.VideoTrack!.Orientation);
    }

    [Fact]
    public void TimecodeTrackGivesTheStartTimecode()
    {
        MediaInfo info = MediaFile.Probe(PathOf("mov-tmcd.mov"));
        Assert.Equal("01:00:00:00", info.Timecode);
        Assert.Equal(TrackKind.Other, info.Tracks[1].Kind);
    }

    [Theory]
    [InlineData(107892, 30, true, "01:00:00;00")]
    [InlineData(1800, 30, true, "00:01:00;02")]
    [InlineData(17982, 30, true, "00:10:00;00")]
    [InlineData(90000, 25, false, "01:00:00:00")]
    public void TimecodesCountDropFrames(uint frame, int fps, bool drop, string text) =>
        Assert.Equal(text, Video.Containers.Mp4.Mp4Demuxer.Timecode(frame, fps, drop));

    [Theory]
    [InlineData("mp4-metadata.mp4")]
    [InlineData("mov-metadata.mov")]
    [InlineData("mp4box-chapqt.mp4")]
    public void TagsAndChaptersMatchFfprobe(string clip)
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(PathOf(clip) + ".json"));
        JsonElement root = json.RootElement;
        MediaInfo info = MediaFile.Probe(PathOf(clip));
        Dictionary<string, string> names = new() { ["title"] = "TITLE", ["artist"] = "ARTIST", ["date"] = "DATE", ["comment"] = "COMMENT", ["encoder"] = "ENCODER" };
        foreach (JsonProperty tag in root.GetProperty("tags").EnumerateObject().Where(t => names.ContainsKey(t.Name)))
        {
            Assert.Equal(tag.Value.GetString(), info.Tags.GetValueOrDefault(names[tag.Name]));
        }

        List<JsonElement> chapters = [.. root.GetProperty("chapters").EnumerateArray()];
        Assert.Equal(chapters.Count, info.Chapters.Count);
        for (int i = 0; i < chapters.Count; i++)
        {
            Rational timeBase = Rational.Parse(chapters[i].GetProperty("time_base").GetString()!);
            Assert.Equal(new MediaTime(chapters[i].GetProperty("start").GetInt64(), timeBase), info.Chapters[i].Start);
            Assert.Equal(chapters[i].GetProperty("title").GetString(), info.Chapters[i].Title);
        }
    }

    [Fact]
    public void CoverArtIsAnAttachment()
    {
        MediaInfo info = MediaFile.Probe(PathOf("mp4-metadata.mp4"));
        AttachmentInfo cover = Assert.Single(info.Attachments);
        Assert.True(cover.IsCoverArt);
        Assert.Equal("image/jpeg", cover.MediaType);
        Assert.True(cover.Data.Span.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8]));
    }

    [Fact]
    public void MovTextSamplesKeepTextAndStyles()
    {
        using IDemuxer demuxer = MediaFile.OpenDemuxer(PathOf("mp4-movtext.mp4"));
        TrackInfo track = demuxer.Tracks.Single(t => t.CodecId == CodecIds.MovText);
        Assert.Equal("eng", track.Language);
        Assert.Equal(TrackKind.Subtitle, track.Kind);
        List<MovTextSample> samples = [];
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                if (packet.TrackNumber == track.Number)
                {
                    MovTextSample sample = MovTextSample.Parse(packet.Data.Span);
                    Assert.Equal(packet.Data.ToArray(), sample.ToBytes());
                    samples.Add(sample);
                }
            }
        }

        Assert.Equal(["", "Plain line", "Bold and italic", "Two\nlines"], samples.Select(s => s.Text));
        Assert.Equal([new MovTextStyle(0, 4, MovTextFace.Bold, 16, new Egami.Pixels.Rgba8(255, 255, 255, 255)), new MovTextStyle(9, 15, MovTextFace.Italic, 16, new Egami.Pixels.Rgba8(255, 255, 255, 255))],
            samples[2].Styles);
    }

    [Fact]
    public void BrokenMovTextSamplesThrow()
    {
        Assert.Throws<CorruptDataException>(() => MovTextSample.Parse([0]));
        Assert.Throws<CorruptDataException>(() => MovTextSample.Parse([0, 5, (byte)'a']));
        Assert.Throws<CorruptDataException>(() => MovTextSample.Parse([0, 1, (byte)'a', 0, 0, 0, 20, (byte)'s', (byte)'t', (byte)'y', (byte)'l']));
    }

    [Theory]
    [InlineData("mp4-frag.mp4")]
    [InlineData("mp4-cmaf.mp4")]
    [InlineData("mp4box-frag-mfra.mp4")]
    [InlineData("mp4box-dash.mp4")]
    [InlineData("mp4-h264-aac.mp4")]
    [InlineData("mp4-editlist-trim.mp4")]
    [InlineData("mp4box-co64.mp4")]
    public void SeekLandsOnTheLastKeyframeAtOrBeforeTheTime(string clip)
    {
        using IDemuxer demuxer = MediaFile.OpenDemuxer(PathOf(clip));
        TrackInfo video = demuxer.Tracks.First(t => t.Kind == TrackKind.Video);
        List<(long Pts, bool Key)> all = [];
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                if (packet.TrackNumber == video.Number)
                {
                    all.Add((packet.Timestamp.Ticks, packet.IsKeyframe));
                }
            }
        }

        List<long> keys = [.. all.Where(p => p.Key).Select(p => p.Pts)];
        foreach (double seconds in new[] { -1, 0, 0.1, 0.45, 0.5, 0.52, 0.9, 5 })
        {
            MediaTime time = MediaTime.FromSeconds(seconds);
            demuxer.Seek(time);
            long ticks = time.Rescale(video.TimeBase).Ticks;
            long expected = keys.Where(k => k <= ticks).DefaultIfEmpty(keys.Min()).Max();
            EncodedPacket? first = null;
            while (demuxer.ReadPacket() is { } packet)
            {
                if (packet.TrackNumber == video.Number)
                {
                    first = packet;
                    break;
                }

                packet.Dispose();
            }

            using (first)
            {
                Assert.True(first is { IsKeyframe: true }, $"{clip} seek to {seconds}: the first packet is not a keyframe");
                Assert.True(expected == first.Timestamp.Ticks, $"{clip} seek to {seconds}: keyframe at {first.Timestamp.Ticks}, expected {expected}");
            }
        }
    }
}
