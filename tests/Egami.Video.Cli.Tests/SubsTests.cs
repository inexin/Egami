using Egami.Video.Subtitles;

namespace Egami.Video.Cli.Tests;

/// <summary>
/// <c>egami-video subs</c> (V1.2 Build Plan task 13): convert, shift, sync, extract and embed, and subtitle files read as
/// inputs by <c>info</c> and <c>embed</c>.
/// </summary>
public sealed class SubsTests
{
    private const string Script = """
        1
        00:00:01,000 --> 00:00:02,000
        First

        2
        00:00:03,000 --> 00:00:04,500
        <i>Second</i>

        3
        00:01:00,000 --> 00:01:02,000
        Third

        """;

    [Fact]
    public void ConvertWritesTheFormatTheExtensionNames()
    {
        using Tool tool = new();
        File.WriteAllText(tool.PathOf("movie.srt"), Script);
        ToolRun run = tool.Run("subs", "convert", "movie.srt", "-o", "movie.vtt");
        Assert.True(run.Code == 0, run.Error);
        string vtt = File.ReadAllText(tool.PathOf("movie.vtt"));
        Assert.StartsWith("WEBVTT", vtt, StringComparison.Ordinal);
        Assert.Contains("00:00:03.000 --> 00:00:04.500", vtt, StringComparison.Ordinal);
        Assert.Contains("<i>Second</i>", vtt, StringComparison.Ordinal);
    }

    [Fact]
    public void ConvertUnchangedGivesBackTheSameBytes()
    {
        using Tool tool = new();
        File.WriteAllText(tool.PathOf("movie.srt"), Script);
        ToolRun run = tool.Run("subs", "convert", "movie.srt", "-o", "-");
        Assert.True(run.Code == 0, run.Error);
        Assert.Equal(File.ReadAllBytes(tool.PathOf("movie.srt")), run.Bytes);
    }

    [Fact]
    public void ConvertToStandardOutputTakesFormat()
    {
        using Tool tool = new();
        File.WriteAllText(tool.PathOf("movie.srt"), Script);
        ToolRun run = tool.Run("subs", "convert", "movie.srt", "-o", "-", "--format", "ass");
        Assert.True(run.Code == 0, run.Error);
        string ass = System.Text.Encoding.UTF8.GetString(run.Bytes);
        Assert.Contains("[Events]", ass, StringComparison.Ordinal);
        Assert.Contains("Dialogue: 0,0:00:01.00,0:00:02.00", ass, StringComparison.Ordinal);
    }

    [Fact]
    public void ConvertCutsToARangeAndRebases()
    {
        using Tool tool = new();
        File.WriteAllText(tool.PathOf("movie.srt"), Script);
        ToolRun run = tool.Run("subs", "convert", "movie.srt", "--from", "1.5", "--to", "4", "--rebase", "-o", "cut.srt");
        Assert.True(run.Code == 0, run.Error);
        Assert.Equal([("First", 0, 0.5), ("Second", 1.5, 2.5)], Cues(tool.PathOf("cut.srt")));
    }

    [Fact]
    public void ShiftTakesANegativeOffset()
    {
        using Tool tool = new();
        File.WriteAllText(tool.PathOf("movie.srt"), Script);
        ToolRun run = tool.Run("subs", "shift", "movie.srt", "--by", "-0.5", "-o", "out.srt");
        Assert.True(run.Code == 0, run.Error);
        Assert.Equal([("First", 0.5, 1.5), ("Second", 2.5, 4), ("Third", 59.5, 61.5)], Cues(tool.PathOf("out.srt")));
    }

    [Fact]
    public void ShiftRetimesForAFrameRateChange()
    {
        using Tool tool = new();
        File.WriteAllText(tool.PathOf("movie.srt"), Script);
        ToolRun run = tool.Run("subs", "shift", "movie.srt", "--fps", "23.976:25", "-o", "out.srt");
        Assert.True(run.Code == 0, run.Error);

        // 60 s at 23.976 fps is 60 * 24000 / 1001 / 25 = 57.542 s at 25.
        Assert.Equal(57.542, Cues(tool.PathOf("out.srt"))[2].Start, 3);
    }

    [Fact]
    public void ShiftInPlaceReplacesTheInput()
    {
        using Tool tool = new();
        File.WriteAllText(tool.PathOf("movie.srt"), Script);
        ToolRun run = tool.Run("subs", "shift", "movie.srt", "--by", "1", "--in-place");
        Assert.True(run.Code == 0, run.Error);
        Assert.Equal(2, Cues(tool.PathOf("movie.srt"))[0].Start);
    }

    [Fact]
    public void ShiftNeedsSomethingToDo()
    {
        using Tool tool = new();
        File.WriteAllText(tool.PathOf("movie.srt"), Script);
        ToolRun run = tool.Run("subs", "shift", "movie.srt", "-o", "out.srt");
        Assert.Equal(2, run.Code);
        Assert.Contains("--by, --scale or --fps", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void SyncByTwoPointsFixesOffsetAndSpeed()
    {
        using Tool tool = new();
        File.WriteAllText(tool.PathOf("movie.srt"), Script);
        ToolRun run = tool.Run("subs", "sync", "movie.srt", "--map", "1=2", "--map", "1:00=1:03", "-o", "out.srt");
        Assert.True(run.Code == 0, run.Error);
        List<(string Text, double Start, double End)> cues = Cues(tool.PathOf("out.srt"));
        Assert.Equal(2, cues[0].Start);
        Assert.Equal(63, cues[2].Start);
        // 3 s lies 2 / 59 of the way from 1 s to 60 s, so it moves to 2 + 2 * 61 / 59.
        Assert.Equal(4.068, cues[1].Start, 3);
    }

    [Fact]
    public void ExtractWritesATrackInItsOwnFormat()
    {
        using Tool tool = new();
        string clip = Subtitled(tool);
        ToolRun run = tool.Run("subs", "extract", clip, "-o", "out/{name}.{format}");
        Assert.True(run.Code == 0, run.Error);
        Assert.Equal(Cues(Script), Cues(tool.PathOf("out/subtitled.srt")));
    }

    [Fact]
    public void ExtractConvertsWithFormat()
    {
        using Tool tool = new();
        string clip = Subtitled(tool);
        ToolRun run = tool.Run("subs", "extract", clip, "-o", "-", "--format", "vtt");
        Assert.True(run.Code == 0, run.Error);
        Assert.StartsWith("WEBVTT", System.Text.Encoding.UTF8.GetString(run.Bytes), StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractWithoutSubtitlesFails()
    {
        using Tool tool = new();
        string clip = tool.Copy(Path.Combine("vp8", "v0-keyframes.webm"));
        ToolRun run = tool.Run("subs", "extract", clip, "-o", "out.srt");
        Assert.Equal(2, run.Code);
        Assert.Contains("no subtitle track", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void EmbedAddsTracksWithLanguageNameAndDefault()
    {
        using Tool tool = new();
        string clip = tool.Copy(Path.Combine("vp8", "v0-keyframes.webm"));
        File.WriteAllText(tool.PathOf("movie.srt"), Script);
        File.WriteAllText(tool.PathOf("movie.nor.srt"), Script.Replace("First", "Første", StringComparison.Ordinal));
        ToolRun run = tool.Run("subs", "embed", clip, "--add", "movie.srt=eng", "--add", "movie.nor.srt=nor", "--name", "2=Norsk", "--default", "2", "-o", "out.mkv");
        Assert.True(run.Code == 0, run.Error);

        using IDemuxer demuxer = MediaFile.OpenDemuxer(tool.PathOf("out.mkv"));
        TrackInfo[] subtitles = [.. demuxer.Tracks.Where(t => t.Kind == TrackKind.Subtitle)];
        Assert.Equal(2, subtitles.Length);
        Assert.Equal(CodecIds.SubRip, subtitles[0].CodecId);
        Assert.Equal("eng", subtitles[0].Language);
        Assert.False(subtitles[0].IsDefault);
        Assert.Equal("Norsk", subtitles[1].Name);
        Assert.True(subtitles[1].IsDefault);
        Assert.Equal("Første", SubtitleDocument.ReadTrack(demuxer, subtitles[1]).Cues[0].PlainText);
    }

    [Fact]
    public void EmbedConvertsToWebVttForWebM()
    {
        using Tool tool = new();
        string clip = tool.Copy(Path.Combine("vp8", "v0-keyframes.webm"));
        File.WriteAllText(tool.PathOf("movie.srt"), Script);
        ToolRun run = tool.Run("subs", "embed", clip, "--add", "movie.srt", "-o", "out.webm");
        Assert.True(run.Code == 0, run.Error);

        using IDemuxer demuxer = MediaFile.OpenDemuxer(tool.PathOf("out.webm"));
        TrackInfo track = demuxer.Tracks.Single(t => t.Kind == TrackKind.Subtitle);
        Assert.Equal(CodecIds.WebMWebVtt, track.CodecId);
        Assert.Equal(Cues(Script), Cues(SubtitleDocument.ReadTrack(demuxer, track)));
    }

    [Fact]
    public void EmbedReplaceLeavesOutTheOwnTracks()
    {
        using Tool tool = new();
        string clip = Subtitled(tool);
        File.WriteAllText(tool.PathOf("other.srt"), Script.Replace("First", "Other", StringComparison.Ordinal));
        ToolRun run = tool.Run("subs", "embed", clip, "--add", "other.srt", "--replace", "-o", "out.mkv");
        Assert.True(run.Code == 0, run.Error);

        using IDemuxer demuxer = MediaFile.OpenDemuxer(tool.PathOf("out.mkv"));
        TrackInfo track = demuxer.Tracks.Single(t => t.Kind == TrackKind.Subtitle);
        Assert.Equal("Other", SubtitleDocument.ReadTrack(demuxer, track).Cues[0].PlainText);
    }

    [Fact]
    public void InfoReadsASubtitleFile()
    {
        using Tool tool = new();
        File.WriteAllText(tool.PathOf("movie.srt"), Script);
        ToolRun run = tool.Run("info", "movie.srt", "--all");
        Assert.True(run.Code == 0, run.Error);
        Assert.Contains("SRT", run.Text, StringComparison.Ordinal);
        Assert.Contains("#1 subtitle (s:0): SubRip", run.Text, StringComparison.Ordinal);
        Assert.Contains("3 packets", run.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void GenericEmbedAddsASubtitleFile()
    {
        using Tool tool = new();
        string clip = tool.Copy(Path.Combine("vp8", "v0-keyframes.webm"));
        File.WriteAllText(tool.PathOf("movie.srt"), Script);
        ToolRun run = tool.Run("embed", clip, "--add", "movie.srt", "--language", "s:0=eng", "-o", "out.mkv");
        Assert.True(run.Code == 0, run.Error);

        using IDemuxer demuxer = MediaFile.OpenDemuxer(tool.PathOf("out.mkv"));
        TrackInfo track = demuxer.Tracks.Single(t => t.Kind == TrackKind.Subtitle);
        Assert.Equal("eng", track.Language);
        Assert.Equal(Cues(Script), Cues(SubtitleDocument.ReadTrack(demuxer, track)));
    }

    /// <summary>The VP8 test clip with the script as an SRT track, as Matroska.</summary>
    private static string Subtitled(Tool tool)
    {
        string clip = tool.Copy(Path.Combine("vp8", "v0-keyframes.webm"));
        File.WriteAllText(tool.PathOf("subtitled.srt"), Script);
        ToolRun run = tool.Run("subs", "embed", clip, "--add", "subtitled.srt", "-o", "subtitled.mkv");
        Assert.True(run.Code == 0, run.Error);
        return tool.PathOf("subtitled.mkv");
    }

    private static List<(string Text, double Start, double End)> Cues(string pathOrScript) =>
        Cues(File.Exists(pathOrScript) ? SubtitleDocument.Load(pathOrScript) : SubtitleDocument.Parse(pathOrScript));

    private static List<(string Text, double Start, double End)> Cues(SubtitleDocument document) =>
        [.. document.Cues.Select(c => (c.PlainText, Math.Round(c.Start.Seconds, 3), Math.Round(c.End.Seconds, 3)))];
}
