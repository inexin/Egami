using Egami.Video.Subtitles;

namespace Egami.Video.Cli.Tests;

/// <summary>
/// Subtitle tracks through <c>trim</c>, <c>concat</c> and <c>timestamps</c> (V1.2 Build Plan task 11): cues move with the
/// video, a cue shown across a cut is clipped to it, and cues stay in start order.
/// </summary>
public sealed class SubtitleCutTests
{
    private const string Script = """
        1
        00:00:00,000 --> 00:00:00,400
        First

        2
        00:00:00,250 --> 00:00:00,900
        <i>Across the cut</i>

        3
        00:00:00,800 --> 00:00:00,950
        Last
        """;

    [Fact]
    public void TrimClipsCuesShownAcrossTheCut()
    {
        using Tool tool = new();
        string clip = Subtitled(tool);
        ToolRun run = tool.Run("trim", clip, "--from", "0.3", "--to", "0.7", "-o", "cut.mkv");
        Assert.True(run.Code == 0, run.Error);

        // The cut starts at the keyframe at 0.233 and stops at 0.7: the first two cues are clipped to it, the last one is after it.
        Assert.Equal(
            [("First", 0, 0.167), ("Across the cut", 0.017, 0.467)],
            Cues(tool.PathOf("cut.mkv")));
    }

    [Fact]
    public void TrimFromStandardInputClipsTheSameWay()
    {
        using Tool tool = new();
        string clip = Subtitled(tool);
        ToolRun piped = tool.Run(["trim", "-", "--from", "0.3", "--to", "0.7", "-o", "-", "--format", "mkv"], File.ReadAllBytes(clip));
        Assert.True(piped.Code == 0, piped.Error);
        File.WriteAllBytes(tool.PathOf("piped.mkv"), piped.Bytes);
        Assert.Equal(2, Cues(tool.PathOf("piped.mkv")).Count);
        Assert.Equal(0.017, Cues(tool.PathOf("piped.mkv"))[1].Start, 3);
    }

    [Fact]
    public void ConcatMovesTheCuesOfEachClip()
    {
        using Tool tool = new();
        string clip = Subtitled(tool);
        File.Copy(clip, tool.PathOf("again.mkv"));
        ToolRun run = tool.Run("concat", clip, "again.mkv", "-o", "joined.mkv");
        Assert.True(run.Code == 0, run.Error);

        List<(string Text, double Start, double End)> cues = Cues(tool.PathOf("joined.mkv"));
        Assert.Equal(6, cues.Count);
        double length = cues[3].Start;
        Assert.True(length >= 0.95, $"the second clip starts at {length}");
        Assert.Equal(Cues(clip), cues[..3]);
        Assert.Equal(Cues(clip).Select(c => (c.Text, Math.Round(c.Start + length, 3), Math.Round(c.End + length, 3))), cues[3..]);
    }

    [Fact]
    public void TimestampsShiftMovesCuesAndRepairKeepsCuesThatStartTogether()
    {
        using Tool tool = new();
        string clip = Subtitled(tool, Script + "\n\n4\n00:00:00,800 --> 00:00:00,900\nWith the last\n");
        ToolRun run = tool.Run("timestamps", clip, "--shift", "0.5", "--fix", "-o", "out.mkv");
        Assert.True(run.Code == 0, run.Error);
        List<(string Text, double Start, double End)> cues = Cues(tool.PathOf("out.mkv"));
        Assert.Equal(Cues(clip).Select(c => (c.Text, Math.Round(c.Start + 0.5, 3), Math.Round(c.End + 0.5, 3))), cues);
        Assert.Equal(cues[2].Start, cues[3].Start);
    }

    /// <summary>The VP8 test clip with an SRT track added, as Matroska.</summary>
    private static string Subtitled(Tool tool, string cues = Script)
    {
        string path = tool.PathOf("subtitled.mkv");
        SubtitleTrackWriter writer = new(SubtitleDocument.Parse(cues, SubtitleFormat.Srt), 2, CodecIds.SubRip);
        using IDemuxer demuxer = MediaFile.OpenDemuxer(Path.Combine(Tool.Corpus, "vp8", "v0-keyframes.webm"));
        List<EncodedPacket> packets = [];
        while (demuxer.ReadPacket() is { } packet)
        {
            packets.Add(packet);
        }

        using (FileStream file = File.Create(path))
        using (IMuxer muxer = MediaFile.CreateMuxer(file, "mkv", [demuxer.Tracks[0] with { Number = 1 }, writer.Track], leaveOpen: true))
        {
            foreach (EncodedPacket packet in packets.Concat(writer.Packets).OrderBy(p => p.Timestamp.Seconds))
            {
                using (packet)
                {
                    muxer.WritePacket(packet);
                }
            }

            muxer.Finish();
        }

        return path;
    }

    /// <summary>A file's subtitle cues: text, start and end in seconds rounded to milliseconds.</summary>
    private static List<(string Text, double Start, double End)> Cues(string path)
    {
        using IDemuxer demuxer = MediaFile.OpenDemuxer(path);
        SubtitleDocument document = SubtitleDocument.ReadTrack(demuxer, demuxer.Tracks.First(t => t.Kind == TrackKind.Subtitle));
        return [.. document.Cues.Select(c => (c.PlainText, Math.Round(c.Start.Seconds, 3), Math.Round(c.End.Seconds, 3)))];
    }
}
