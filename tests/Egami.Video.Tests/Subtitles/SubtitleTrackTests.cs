using Egami.Pixels;
using Egami.Video.Containers;
using Egami.Video.Subtitles;

namespace Egami.Video.Tests.Subtitles;

public class SubtitleTrackTests
{
    private static readonly Rational Ms = new(1, 1000);

    private static MediaTime At(long ms) => new(ms, Ms);

    private static string PathOf(string name) => Corpus.PathOf(Path.Combine("subtitles", name));

    /// <summary>Writes a track alone into a container and reads it back.</summary>
    private static SubtitleDocument RoundTrip(SubtitleDocument document, string container, string? codecId = null)
    {
        codecId ??= SubtitleTrackWriter.CodecIdFor(container, document.Format);
        SubtitleTrackWriter writer = new(document, 1, codecId);
        using MemoryStream file = new();
        using (IMuxer muxer = MediaFile.CreateMuxer(file, container, [writer.Track with { Language = "eng" }], leaveOpen: true))
        {
            foreach (EncodedPacket packet in writer.Packets)
            {
                muxer.WritePacket(packet);
            }
        }

        file.Position = 0;
        using IDemuxer demuxer = MediaFile.OpenDemuxer(file, leaveOpen: true);
        TrackInfo track = Assert.Single(demuxer.Tracks);
        Assert.Equal(TrackKind.Subtitle, track.Kind);
        Assert.Equal(codecId, track.CodecId);
        Assert.Equal("eng", track.Language);
        return SubtitleDocument.ReadTrack(demuxer, track);
    }

    private static void AssertSameCues(SubtitleDocument expected, SubtitleDocument actual)
    {
        Assert.Equal(
            expected.Cues.Where(c => !c.IsComment).OrderBy(c => c.Start).Select(c => (c.Start.Rescale(Ms).Ticks, c.End.Rescale(Ms).Ticks, c.PlainText)),
            actual.Cues.Select(c => (c.Start.Rescale(Ms).Ticks, c.End.Rescale(Ms).Ticks, c.PlainText)));
    }

    [Theory]
    [InlineData("clean.srt", "mkv")]
    [InlineData("features.vtt", "mkv")]
    [InlineData("features.vtt", "webm")]
    [InlineData("features.ass", "mkv")]
    [InlineData("clean.srt", "mp4")]
    [InlineData("features.vtt", "mp4")]
    public void TracksKeepEveryCue(string name, string container)
    {
        SubtitleDocument source = SubtitleDocument.Load(PathOf(name));
        SubtitleDocument read = RoundTrip(source, container);
        AssertSameCues(source, read);
    }

    [Theory]
    [InlineData("mkv", null)]
    [InlineData("webm", null)]
    [InlineData("mp4", null)]
    public void WebVttTracksKeepSettingsIdsAndStyles(string container, string? codecId)
    {
        SubtitleDocument source = SubtitleDocument.Load(PathOf("features.vtt"));
        SubtitleDocument read = RoundTrip(source, container, codecId);
        Assert.Equal(source.Cues.Select(c => c.Id), read.Cues.Select(c => c.Id));
        Assert.Equal(source.Cues.Select(c => c.WebVtt), read.Cues.Select(c => c.WebVtt));
        Assert.Equal(source.Cues.Select(c => c.Spans.Count), read.Cues.Select(c => c.Spans.Count));
        Assert.Equal(source.Regions, read.Regions);
        Assert.Equal(source.StyleSheets, read.StyleSheets);
    }

    [Fact]
    public void AssTracksKeepStylesLayersAndTags()
    {
        SubtitleDocument source = SubtitleDocument.Load(PathOf("features.ass"));
        SubtitleDocument read = RoundTrip(source, "mkv");
        SubtitleCue[] playable = [.. source.Cues.Where(c => !c.IsComment).OrderBy(c => c.Start)];
        Assert.Equal(source.Styles, read.Styles);
        Assert.Equal(playable.Select(c => (c.Layer, c.Style, c.Actor, c.Position, c.Alignment)), read.Cues.Select(c => (c.Layer, c.Style, c.Actor, c.Position, c.Alignment)));
        Assert.Equal(playable.Select(c => c.RawText), read.Cues.Select(c => c.RawText));
    }

    [Theory]
    [InlineData("ffmpeg-srt.mkv", "clean.srt")]
    [InlineData("ffmpeg-ass.mkv", "features.ass")]
    [InlineData("ffmpeg-vtt.mkv", "tracks/cues.vtt")]
    [InlineData("ffmpeg-vtt.webm", "tracks/cues.vtt")]
    [InlineData("ffmpeg-movtext.mp4", "clean.srt")]
    [InlineData("mp4box-movtext.mp4", "clean.srt")]
    [InlineData("mp4box-wvtt.mp4", "clean.srt")]
    [InlineData("mkvmerge-srt.mkv", "clean.srt")]
    [InlineData("mkvmerge-vtt.mkv", "clean.srt")]
    [InlineData("mkvmerge-ass.mkv", "features.ass")]
    public void TracksOtherToolsWroteGiveTheSourceCues(string name, string source)
    {
        // Made by tools/video/make-subtitle-tracks.py with FFmpeg, MKVToolNix and MP4Box. FFmpeg gets cues.vtt, features.vtt
        // without its REGION and STYLE blocks, since FFmpeg 6.1 reads no cues from a file that has them.
        using IDemuxer demuxer = MediaFile.OpenDemuxer(PathOf(Path.Combine("tracks", name)));
        TrackInfo track = demuxer.Tracks.Single(t => t.Kind == TrackKind.Subtitle);
        SubtitleDocument read = SubtitleDocument.ReadTrack(demuxer, track);
        AssertSameCues(SubtitleDocument.Load(PathOf(source)), read);
    }

    [Fact]
    public void MatroskaTracksGiveTheSamePacketsAgain()
    {
        // Read a track and write it again: the blocks are the same bytes.
        foreach ((string name, string container) in new[] { ("clean.srt", "mkv"), ("features.vtt", "mkv"), ("features.vtt", "webm"), ("features.ass", "mkv") })
        {
            SubtitleDocument source = SubtitleDocument.Load(PathOf(name));
            string codec = SubtitleTrackWriter.CodecIdFor(container, source.Format);
            SubtitleTrackWriter first = new(source, 1, codec);
            SubtitleDocument read = SubtitleDocument.FromPackets(first.Track, first.Packets);
            SubtitleTrackWriter second = new(read, 1, codec);
            Assert.Equal(first.Track.CodecPrivate.ToArray(), second.Track.CodecPrivate.ToArray());
            Assert.Equal(first.Packets.Select(p => (p.Timestamp, p.Duration, Convert.ToHexString(p.Data.Span), p.Additions.Count)),
                second.Packets.Select(p => (p.Timestamp, p.Duration, Convert.ToHexString(p.Data.Span), p.Additions.Count)));
        }
    }

    [Fact]
    public void WritesFilesForTheToolCheck()
    {
        // tools/video/check-subtitle-tracks.py sets EGAMI_SUBTITLE_OUT, then FFmpeg, MKVToolNix and MP4Box read what this writes.
        string? folder = Environment.GetEnvironmentVariable("EGAMI_SUBTITLE_OUT");
        Assert.SkipWhen(folder is null, "Run by tools/video/check-subtitle-tracks.py.");
        foreach ((string name, string container, string? codec) in new (string, string, string?)[]
        {
            ("clean.srt", "mkv", null), ("features.vtt", "mkv", null), ("features.vtt", "webm", null), ("features.ass", "mkv", null),
            ("clean.srt", "mp4", null), ("features.vtt", "mp4", null), ("features.vtt", "mkv", CodecIds.WebMWebVtt),
        })
        {
            SubtitleDocument source = SubtitleDocument.Load(PathOf(name));
            SubtitleTrackWriter writer = new(source, 1, codec ?? SubtitleTrackWriter.CodecIdFor(container, source.Format));
            string file = Path.Combine(folder!, $"{Path.GetFileNameWithoutExtension(name)}-{writer.Track.CodecId.Replace('/', '_')}.{container}");
            using (IMuxer muxer = MediaFile.CreateMuxer(file, [writer.Track]))
            {
                foreach (EncodedPacket packet in writer.Packets)
                {
                    muxer.WritePacket(packet);
                }
            }

            // What the tools must find: each cue's start and end in milliseconds, in time order.
            File.WriteAllLines(file + ".cues", source.Cues.Where(c => !c.IsComment).OrderBy(c => c.Start)
                .Select(c => $"{c.Start.Rescale(Ms).Ticks} {c.End.Rescale(Ms).Ticks}"));
        }
    }

    [Fact]
    public void MatroskaWebVttKaraokeTimesCountFromTheCue()
    {
        // As mkvmerge stores them; WebM and MP4 keep the file's times.
        SubtitleDocument source = SubtitleDocument.Load(PathOf("features.vtt"));
        Assert.Equal("<00:00:00.000>Kar<00:00:00.500>a<00:00:01.000>oke",
            System.Text.Encoding.UTF8.GetString(new SubtitleTrackWriter(source, 1, CodecIds.WebVtt).Packets[^1].Data.Span));
        Assert.EndsWith("\n<00:00:07.000>Kar<00:00:07.500>a<00:00:08.000>oke",
            System.Text.Encoding.UTF8.GetString(new SubtitleTrackWriter(source, 1, CodecIds.WebMWebVtt).Packets[^1].Data.Span), StringComparison.Ordinal);
        SubtitleDocument read = RoundTrip(source, "mkv");
        Assert.Equal(source.Cues[^1].Spans.Select(s => s.Timestamp), read.Cues[^1].Spans.Select(s => s.Timestamp));
    }

    [Fact]
    public void MovTextKeepsStylesAndSplitsOverlaps()
    {
        SubtitleDocument document = new();
        document.Cues.Add(new SubtitleCue(At(1000), At(4000),
        [
            new SubtitleSpan("Big "),
            new SubtitleSpan("red") { Bold = true, Color = new Rgba8(255, 0, 0, 255) },
            new SubtitleSpan(" été \U0001F600 "),
            new SubtitleSpan("end") { Italic = true, FontSize = 24 },
        ]));
        document.Cues.Add(new SubtitleCue(At(2000), At(3000), "Over"));
        SubtitleTrackWriter writer = new(document, 1, CodecIds.MovText);

        // A gap, the first cue alone, both, the first alone again.
        Assert.Equal([0L, 1000, 2000, 3000], writer.Packets.Select(p => p.Timestamp.Rescale(Ms).Ticks));
        Assert.Equal(["", "Big red été \U0001F600 end", "Big red été \U0001F600 end\nOver", "Big red été \U0001F600 end"],
            writer.Packets.Select(p => MovTextSample.Parse(p.Data.Span).Text));
        MovTextSample sample = MovTextSample.Parse(writer.Packets[1].Data.Span);
        Assert.Equal([new MovTextStyle(4, 7, MovTextFace.Bold, 18, new Rgba8(255, 0, 0, 255)), new MovTextStyle(14, 17, MovTextFace.Italic, 24, new Rgba8(255, 255, 255, 255))], sample.Styles);

        SubtitleDocument read = SubtitleDocument.FromPackets(writer.Track, writer.Packets);
        Assert.Equal(3, read.Cues.Count);
        Assert.True(read.Cues[0].Spans[1].Bold);
        Assert.Equal(new Rgba8(255, 0, 0, 255), read.Cues[0].Spans[1].Color);
        Assert.Equal(24, read.Cues[0].Spans[^1].FontSize);
    }

    [Fact]
    public void Mp4WebVttMergesACueShownOverSeveralSamples()
    {
        SubtitleDocument document = SubtitleDocument.Parse("WEBVTT\n\na\n00:01.000 --> 00:04.000 align:left\nLong\n\n00:02.000 --> 00:03.000\nShort\n");
        SubtitleTrackWriter writer = new(document, 1, CodecIds.Mp4WebVtt);
        Assert.Equal(4, writer.Packets.Count);
        SubtitleDocument read = SubtitleDocument.FromPackets(writer.Track, writer.Packets);
        Assert.Equal([(1000L, 4000L, "Long", "a"), (2000L, 3000L, "Short", null)],
            read.Cues.Select(c => (c.Start.Rescale(Ms).Ticks, c.End.Rescale(Ms).Ticks, c.PlainText, c.Id)));
        Assert.Equal(WebVttAlign.Left, read.Cues[0].WebVtt!.Align);
    }

    [Fact]
    public void CommentsAndUnsupportedCodecsAreReported()
    {
        List<EgamiDiagnostic> reported = [];
        MediaContext context = MediaContext.Default with { Egami = EgamiContext.Default with { Diagnostics = reported.Add } };
        SubtitleDocument document = SubtitleDocument.Load(PathOf("features.ass"));
        _ = new SubtitleTrackWriter(document, 1, CodecIds.MovText, context);
        Assert.Contains(reported, d => d.Id == "Subtitle.Dropped" && d.Message.Contains("ASS comment lines", StringComparison.Ordinal));
        Assert.Throws<NotSupportedException>(() => new SubtitleTrackWriter(document, 1, CodecIds.Ssa));
        Assert.Throws<NotSupportedException>(() => new SubtitleTrackWriter(document, 1, CodecIds.Opus));
        Assert.Throws<NotSupportedException>(() => SubtitleTrackWriter.CodecIdFor("avi", SubtitleFormat.Srt));
        Assert.False(SubtitleDocument.IsTextCodec(CodecIds.Opus));
        Assert.True(SubtitleDocument.IsTextCodec("D_WEBVTT/CAPTIONS"));
    }

    [Fact]
    public void MatroskaWritesSubtitleDurations()
    {
        SubtitleDocument source = SubtitleDocument.Load(PathOf("clean.srt"));
        SubtitleTrackWriter writer = new(source, 1);
        using MemoryStream file = new();
        using (IMuxer muxer = MediaFile.CreateMuxer(file, "mkv", [writer.Track], leaveOpen: true))
        {
            foreach (EncodedPacket packet in writer.Packets)
            {
                muxer.WritePacket(packet);
            }
        }

        file.Position = 0;
        using IDemuxer demuxer = MediaFile.OpenDemuxer(file, leaveOpen: true);
        List<EncodedPacket> packets = [];
        while (demuxer.ReadPacket() is { } packet)
        {
            packets.Add(packet);
        }

        Assert.Equal(writer.Packets.Select(p => p.Duration), packets.Select(p => p.Duration));
        Assert.All(packets, p => Assert.True(p.IsKeyframe));
        packets.ForEach(p => p.Dispose());
    }
}
