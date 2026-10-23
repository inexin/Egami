using System.Diagnostics;
using System.Security.Cryptography;

namespace Egami.Video.Cli.Tests;

/// <summary>
/// <c>trim</c>, <c>concat</c> and <c>segment</c> (Build Plan task 6): cuts start at keyframes and end at --to, segments joined
/// again give back the source's packets, cut points agree with ffprobe when it is on the PATH.
/// </summary>
public sealed class CutTests
{
    [Fact]
    public void TrimStartsAtTheKeyframeBeforeFrom()
    {
        using Tool tool = new();
        string clip = Path.Combine(Tool.Corpus, "vp8", "v0-keyframes.webm");
        ToolRun run = tool.Run("trim", clip, "--from", "0.3", "--to", "0.7", "-o", "cut.webm");
        Assert.True(run.Code == 0, run.Error);
        Assert.Contains("starts at 0:00:00.233, 0.067 s before --from (the keyframe before it)", run.Error, StringComparison.Ordinal);

        // The source's packets from the keyframe at 0.233 up to 0.7, moved to start at 0.
        List<Packet> source = Read(clip);
        List<Packet> expected = [.. source.Where(p => p.Time >= 0.233 - 1e-6 && p.Time < 0.7).Select(p => p with { Time = p.Time - 0.233 })];
        List<Packet> output = Read(tool.PathOf("cut.webm"));
        Assert.Equal(expected.Select(p => p.Hash), output.Select(p => p.Hash));
        Assert.Equal(expected.Select(p => Math.Round(p.Time, 3)), output.Select(p => Math.Round(p.Time, 3)));
        Assert.True(output[0].Keyframe);
        Assert.Equal(0, output[0].Time);

        if (Ffprobe.Packets(tool.PathOf("cut.webm")) is { } probed)
        {
            Assert.Equal(output.Count, probed.Count);
            Assert.Equal(0, probed[0].Time, 3);
            Assert.True(probed[0].Keyframe);
            Assert.All(probed, p => Assert.True(p.Time < 0.7 - 0.233));
        }
    }

    [Fact]
    public void TrimFromStandardInputGivesTheSameFile()
    {
        using Tool tool = new();
        string clip = Path.Combine(Tool.Corpus, "vp8", "v0-keyframes.webm");
        Assert.Equal(0, tool.Run("trim", clip, "--from", "0.5", "--duration", "0.3", "-o", "seek.webm").Code);
        ToolRun piped = tool.Run(["trim", "-", "--from", "0.5", "--duration", "0.3", "-o", "-", "--format", "webm"], File.ReadAllBytes(clip));
        Assert.True(piped.Code == 0, piped.Error);
        File.WriteAllBytes(tool.PathOf("piped.webm"), piped.Bytes);
        Assert.Equal(Read(tool.PathOf("seek.webm")), Read(tool.PathOf("piped.webm")));
    }

    [Fact]
    public void TrimCopiesVideoWithBFramesAndAudioWithoutDecoding()
    {
        // H.264 and AAC: Egami.Video decodes neither, so the cut copies packets only.
        using Tool tool = new();
        string clip = Path.Combine(Tool.Corpus, "mp4", "mp4-h264-aac.mp4");
        ToolRun run = tool.Run("trim", clip, "--from", "0.5", "--to", "0.9", "-o", "cut.mp4");
        Assert.True(run.Code == 0, run.Error);
        Assert.Contains("starts at 0:00:00.480, 0.020 s before --from", run.Error, StringComparison.Ordinal);
        List<Packet> output = Read(tool.PathOf("cut.mp4"));
        List<Packet> video = [.. output.Where(p => p.Kind == TrackKind.Video)];
        List<Packet> audio = [.. output.Where(p => p.Kind == TrackKind.Audio)];
        Assert.Equal(11, video.Count);
        Assert.True(video[0].Keyframe);
        Assert.Equal([0.0, 0.04, 0.08, 0.12, 0.16, 0.2, 0.24, 0.28, 0.32, 0.36, 0.4], video.Select(p => Math.Round(p.Time, 3)).Order());
        Assert.All(audio, p => Assert.InRange(p.Time, 0, 0.42 - 1e-6));
        List<Packet> source = Read(clip);
        Assert.Equal(source.Where(p => p.Kind == TrackKind.Video && p.Time >= 0.48 - 1e-6 && p.Time < 0.9).Select(p => p.Hash).Order(), video.Select(p => p.Hash).Order());

        if (Ffprobe.Packets(tool.PathOf("cut.mp4")) is { } probed)
        {
            Assert.Equal(output.Count, probed.Count);
            Assert.Empty(Ffprobe.Warnings(tool.PathOf("cut.mp4")));
        }
    }

    [Fact]
    public void TrimKeepsTagsAndClipsChapters()
    {
        using Tool tool = new();
        ToolRun run = tool.Run("trim", Path.Combine(Tool.Corpus, "mkv-metadata.mkv"), "--from", "0.68", "--to", "1.5", "-o", "cut.mkv");
        Assert.True(run.Code == 0, run.Error);
        Assert.DoesNotContain("warning", run.Error, StringComparison.Ordinal);
        MediaInfo info = MediaFile.Probe(tool.PathOf("cut.mkv"));
        Assert.Equal("Corpus clip", info.Tags["TITLE"]);
        Assert.Equal(["cover.jpg"], info.Attachments.Select(a => a.Name));
        Assert.Equal(["Opening", "Rest"], info.Chapters.Select(c => c.Title));
        Assert.Equal(0, info.Chapters[0].Start.Seconds, 3);
        Assert.Equal(0.12, info.Chapters[1].Start.Seconds, 3);
        Assert.Equal(0.82, info.Chapters[1].End!.Value.Seconds, 3);
    }

    [Fact]
    public void TrimByFrameNumbersAndTrackSelection()
    {
        using Tool tool = new();
        string clip = Path.Combine(Tool.Corpus, "avi-mjpeg-420-pcm.avi");
        ToolRun run = tool.Run("trim", clip, "--from", "#10", "--duration", "#5", "--no-audio", "-o", "cut.mkv");
        Assert.True(run.Code == 0, run.Error);
        List<Packet> output = Read(tool.PathOf("cut.mkv"));
        Assert.Equal(5, output.Count);
        Assert.All(output, p => Assert.Equal(TrackKind.Video, p.Kind));
        List<Packet> source = Read(clip).Where(p => p.Kind == TrackKind.Video).ToList();
        Assert.Equal(source.Skip(10).Take(5).Select(p => p.Hash), output.Select(p => p.Hash));
    }

    [Fact]
    public void TrimAudioOnlyCutsOnPackets()
    {
        using Tool tool = new();
        ToolRun run = tool.Run("trim", Path.Combine(Tool.Corpus, "audio", "mka-flac-stereo.mka"), "--from", "0.2", "--to", "0.4", "-o", "cut.mka");
        Assert.True(run.Code == 0, run.Error);
        List<Packet> output = Read(tool.PathOf("cut.mka"));
        Assert.NotEmpty(output);
        Assert.Equal(0, output[0].Time, 3);
        Assert.All(output, p => Assert.Equal(TrackKind.Audio, p.Kind));
    }

    [Fact]
    public void TrimUsageErrors()
    {
        using Tool tool = new();
        string clip = Path.Combine(Tool.Corpus, "mkv-mjpeg-pcm.mkv");
        Assert.Contains("trim needs --from", tool.Run("trim", clip, "-o", "a.mkv").Error, StringComparison.Ordinal);
        ToolRun both = tool.Run("trim", clip, "--to", "1", "--duration", "1", "-o", "a.mkv");
        Assert.Equal(2, both.Code);
        Assert.Contains("not both", both.Error, StringComparison.Ordinal);
        ToolRun backwards = tool.Run("trim", clip, "--from", "1", "--to", "0.5", "-o", "a.mkv");
        Assert.Equal(2, backwards.Code);
        Assert.Contains("must be after --from", backwards.Error, StringComparison.Ordinal);
        ToolRun wrong = tool.Run("trim", clip, "--from", "soon", "-o", "a.mkv");
        Assert.Equal(2, wrong.Code);
        Assert.Contains("is not a time", wrong.Error, StringComparison.Ordinal);
        ToolRun zero = tool.Run("trim", clip, "--duration", "0", "-o", "a.mkv");
        Assert.Equal(2, zero.Code);
        ToolRun frames = tool.Run(["trim", "-", "--from", "#3", "-o", "a.mkv"], File.ReadAllBytes(clip));
        Assert.Equal(2, frames.Code);
        Assert.Contains("need a file", frames.Error, StringComparison.Ordinal);
        ToolRun past = tool.Run("trim", clip, "--from", "10", "-o", "a.mkv");
        Assert.Equal(1, past.Code);
        Assert.Contains("past the end", past.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(tool.PathOf("a.mkv")));
    }

    [Theory]
    [InlineData("vp8/v0-keyframes.webm", "webm", 3)]
    [InlineData("mp4/mp4-h264-aac.mp4", "mp4", 3)]
    [InlineData("mkv-mjpeg-pcm.mkv", "mkv", 5)]
    [InlineData("webm-vp8-opus.webm", "webm", 1)]
    public void SegmentsJoinedGiveBackTheSource(string clip, string extension, int parts)
    {
        using Tool tool = new();
        string source = Path.Combine(Tool.Corpus, clip);
        ToolRun split = tool.Run("segment", source, "--every", "0.4", "-o", "parts/");
        Assert.True(split.Code == 0, split.Error);
        string[] files = [.. Directory.GetFiles(tool.PathOf("parts")).Order(StringComparer.Ordinal)];
        Assert.Equal(parts, files.Length);
        string name = Path.GetFileNameWithoutExtension(clip);
        Assert.Equal(Enumerable.Range(1, parts).Select(i => $"{name}-{i:000}.{extension}"), files.Select(Path.GetFileName));
        foreach (string part in files.Skip(1))
        {
            // Each part starts at a keyframe at 0.
            Packet first = Read(part).First(p => p.Kind == TrackKind.Video);
            Assert.True(first.Keyframe);
            Assert.Equal(0, first.Time, 3);
        }

        if (parts == 1)
        {
            Assert.Equal(RemuxTests.Packets(source), RemuxTests.Packets(files[0]));
            return;
        }

        ToolRun join = tool.Run("concat", $"parts/*.{extension}", "-o", $"joined.{extension}");
        Assert.True(join.Code == 0, join.Error);
        AssertSamePackets(source, tool.PathOf($"joined.{extension}"));
        if (Ffprobe.Packets(source) is { } want && Ffprobe.Packets(tool.PathOf($"joined.{extension}")) is { } got)
        {
            Assert.Equal(want.Count, got.Count);
            foreach (IGrouping<int, Ffprobe.Packet> stream in want.GroupBy(p => p.Stream))
            {
                Ffprobe.Packet[] a = [.. stream.OrderBy(p => p.Time)];
                Ffprobe.Packet[] b = [.. got.Where(p => p.Stream == stream.Key).OrderBy(p => p.Time)];
                Assert.Equal(a.Select(p => p.Size), b.Select(p => p.Size));
                Assert.All(a.Zip(b), p => Assert.True(Math.Abs(p.First.Time - p.Second.Time) <= Tick, $"ffprobe: stream {stream.Key} at {p.First.Time} came back at {p.Second.Time}"));
            }
        }
    }

    [Fact]
    public void SegmentAtTimesReportsWhereEachPartStarts()
    {
        using Tool tool = new();
        string clip = Path.Combine(Tool.Corpus, "vp8", "v0-keyframes.webm");
        ToolRun run = tool.Run("segment", clip, "--at", "0.3,0.7", "-o", "out-{part:00}.webm", "--keep-timestamps", "-v");
        Assert.True(run.Code == 0, run.Error);
        Assert.Contains("part 2 starts at 0:00:00.467, 0.167 s after the split point 0:00:00.300 (the next keyframe)", run.Error, StringComparison.Ordinal);
        Assert.Contains("part 3 starts at 0:00:00.700", run.Error, StringComparison.Ordinal);
        Assert.Equal(0.467, Read(tool.PathOf("out-02.webm"))[0].Time, 3);
        Assert.Equal(0.7, Read(tool.PathOf("out-03.webm"))[0].Time, 3);
        Assert.Equal(Read(clip).Count, Enumerable.Range(1, 3).Sum(i => Read(tool.PathOf($"out-{i:00}.webm")).Count));
    }

    [Fact]
    public void SegmentSplitsInterleavedAudioByTime()
    {
        using Tool tool = new();
        string clip = Path.Combine(Tool.Corpus, "mkv-mjpeg-pcm.mkv");
        ToolRun run = tool.Run("segment", clip, "--at", "1", "-o", "p{part}.mkv");
        Assert.True(run.Code == 0, run.Error);
        List<Packet> first = Read(tool.PathOf("p1.mkv"));
        List<Packet> second = Read(tool.PathOf("p2.mkv"));
        Assert.All(first, p => Assert.True(p.Time < 1));
        Assert.All(second, p => Assert.True(p.Time >= 0));
        Assert.Equal(Read(clip).Count, first.Count + second.Count);
    }

    [Fact]
    public void SegmentUsageErrors()
    {
        using Tool tool = new();
        string clip = Path.Combine(Tool.Corpus, "mkv-mjpeg-pcm.mkv");
        ToolRun neither = tool.Run("segment", clip, "-o", "out/");
        Assert.Equal(2, neither.Code);
        Assert.Contains("--every or --at", neither.Error, StringComparison.Ordinal);
        Assert.Equal(2, tool.Run("segment", clip, "--every", "1", "--at", "1", "-o", "out/").Code);
        ToolRun noPart = tool.Run("segment", clip, "--every", "1", "-o", "one.mkv");
        Assert.Equal(2, noPart.Code);
        Assert.Contains("one-{part:000}.mkv", noPart.Error, StringComparison.Ordinal);
        ToolRun stdout = tool.Run("segment", clip, "--every", "1", "-o", "-");
        Assert.Equal(2, stdout.Code);
        Assert.Contains("not standard output", stdout.Error, StringComparison.Ordinal);
        Assert.Equal(2, tool.Run("segment", clip, "--every", "#25", "-o", "out/").Code);
        Assert.Equal(2, tool.Run("segment", clip, "--at", ",", "-o", "out/").Code);
        Assert.Empty(Directory.GetFiles(tool.Folder, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void ConcatRefusesClipsThatDiffer()
    {
        using Tool tool = new();
        ToolRun run = tool.Run("concat", Path.Combine(Tool.Corpus, "mkv-mjpeg-pcm.mkv"), Path.Combine(Tool.Corpus, "webm-vp8-opus.webm"), "-o", "joined.mkv");
        Assert.Equal(1, run.Code);
        Assert.Contains("does not match", run.Error, StringComparison.Ordinal);
        Assert.Contains("VP8", run.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(tool.PathOf("joined.mkv")));

        ToolRun size = tool.Run("concat", Path.Combine(Tool.Corpus, "vp8", "v0-keyframes.webm"), Path.Combine(Tool.Corpus, "vp8", "v0-odd-97x61.webm"), "-o", "joined.webm");
        Assert.Equal(1, size.Code);
        Assert.Contains("97x61", size.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ConcatUsageErrors()
    {
        using Tool tool = new();
        string clip = Path.Combine(Tool.Corpus, "mkv-mjpeg-pcm.mkv");
        ToolRun one = tool.Run("concat", clip, "-o", "joined.mkv");
        Assert.Equal(2, one.Code);
        Assert.Contains("two or more", one.Error, StringComparison.Ordinal);
        Assert.Equal(2, tool.Run("concat", clip, clip).Code);
        tool.Copy("mkv-mjpeg-pcm.mkv", "a.mkv");
        ToolRun replace = tool.Run("concat", "a.mkv", clip, "-o", "a.mkv");
        Assert.Equal(2, replace.Code);
        Assert.Contains("would replace", replace.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ConcatWritesToStandardOutput()
    {
        using Tool tool = new();
        string clip = Path.Combine(Tool.Corpus, "vp8", "v0-keyframes.webm");
        ToolRun run = tool.Run(["concat", clip, "-", "-o", "-", "--format", "webm", "--progress", "progress.txt"], File.ReadAllBytes(clip));
        Assert.True(run.Code == 0, run.Error);
        File.WriteAllBytes(tool.PathOf("joined.webm"), run.Bytes);
        List<Packet> joined = Read(tool.PathOf("joined.webm"));
        List<Packet> source = Read(clip);
        Assert.Equal(source.Count * 2, joined.Count);
        Assert.Equal(source.Select(p => p.Hash).Concat(source.Select(p => p.Hash)), joined.Select(p => p.Hash));
        Assert.True(joined.Zip(joined.Skip(1)).All(p => p.Second.Time > p.First.Time));
        Assert.Contains("progress=end", File.ReadAllText(tool.PathOf("progress.txt")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("trim", "--from", "0.5")]
    [InlineData("segment", "--every", "0.5")]
    [InlineData("concat", null, null)]
    public void CancelledJobLeavesNoFile(string command, string? option, string? value)
    {
        using Tool tool = new() { Cancellation = new CancellationToken(canceled: true) };
        string clip = Path.Combine(Tool.Corpus, "mkv-mjpeg-pcm.mkv");
        string other = Path.Combine(Tool.Corpus, "mkv-metadata.mkv");
        string[] args = option is null ? [command, clip, other, "-t", "v", "-t", "a", "-o", "out.mkv"] : [command, clip, option, value!, "-o", command == "segment" ? "out/" : "out.mkv"];
        ToolRun run = tool.Run(args);
        Assert.Equal(130, run.Code);
        Assert.Empty(Directory.GetFiles(tool.Folder, "*", SearchOption.AllDirectories));
    }

    /// <summary>How far a timestamp may come back from a split and a join: one millisecond, Matroska's usual tick.</summary>
    private const double Tick = 0.0011;

    /// <summary>
    /// Whether two files hold the same packets per track, in order, with timestamps relative to the first file's start within
    /// <see cref="Tick"/>: a part that ends on a frame rate that is not a whole number of ticks may end one tick early or late.
    /// </summary>
    internal static void AssertSamePackets(string expected, string actual)
    {
        List<Packet> a = Read(expected);
        List<Packet> b = Read(actual);
        Assert.Equal(a.Count, b.Count);
        foreach (TrackKind kind in a.Select(p => p.Kind).Distinct())
        {
            Packet[] want = [.. a.Where(p => p.Kind == kind)];
            Packet[] got = [.. b.Where(p => p.Kind == kind)];
            Assert.Equal(want.Select(p => p.Hash), got.Select(p => p.Hash));
            Assert.All(want.Zip(got), p => Assert.True(Math.Abs(p.First.Time - p.Second.Time) <= Tick, $"{kind} packet at {p.First.Time} came back at {p.Second.Time}"));
        }
    }

    /// <summary>One packet as our demuxer reads it.</summary>
    internal sealed record Packet(TrackKind Kind, double Time, bool Keyframe, string Hash);

    /// <summary>A file's packets in file order.</summary>
    internal static List<Packet> Read(string path)
    {
        using IDemuxer demuxer = MediaFile.OpenDemuxer(path);
        Dictionary<int, TrackKind> kinds = demuxer.Tracks.ToDictionary(t => t.Number, t => t.Kind);
        List<Packet> packets = [];
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                packets.Add(new Packet(kinds[packet.TrackNumber], packet.Timestamp.Seconds, packet.IsKeyframe, Convert.ToHexString(SHA256.HashData(packet.Data.Span))[..16]));
            }
        }

        return packets;
    }
}

/// <summary>Runs ffprobe, when it is on the PATH, to check outputs against a reader that is not ours.</summary>
internal static class Ffprobe
{
    private static readonly Lazy<string?> Program = new(Find);

    /// <summary>One packet as ffprobe lists it.</summary>
    internal sealed record Packet(int Stream, double Time, int Size, bool Keyframe);

    /// <summary>The packets of a file in file order, or <see langword="null"/> without ffprobe.</summary>
    public static List<Packet>? Packets(string path)
    {
        if (Run(path, ["-v", "error", "-show_entries", "packet=stream_index,pts_time,size,flags", "-of", "csv=p=0"], stderr: false) is not { } output)
        {
            return null;
        }

        List<Packet> packets = [];
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] fields = line.Split(',');
            packets.Add(new Packet(
                int.Parse(fields[0], System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture),
                int.Parse(fields[2], System.Globalization.CultureInfo.InvariantCulture),
                fields[3].Contains('K', StringComparison.Ordinal)));
        }

        return packets;
    }

    /// <summary>What ffprobe warns about while reading every packet of a file; empty without ffprobe.</summary>
    public static string Warnings(string path)
    {
        return Run(path, ["-v", "warning", "-show_packets"], stderr: true) ?? string.Empty;
    }

    private static string? Run(string path, string[] args, bool stderr)
    {
        if (Program.Value is not { } program)
        {
            return null;
        }

        ProcessStartInfo start = new(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        start.ArgumentList.Add(path);
        using Process process = Process.Start(start)!;
        Task<string> error = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return stderr ? error.Result : output;
    }

    private static string? Find()
    {
        string name = OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";
        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(folder.Trim('"'), name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
