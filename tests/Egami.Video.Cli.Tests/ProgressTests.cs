using System.Text.Json;

using Egami.Video.Cli.Host;

namespace Egami.Video.Cli.Tests;

/// <summary>Progress and cancellation (Build Plan task 25, V1.3 Progress design Q8 and Q9).</summary>
public sealed class ProgressTests
{
    private static readonly string[] FfmpegKeys = ["frame", "fps", "stream_0_0_q", "bitrate", "total_size", "out_time_us", "out_time_ms", "out_time", "dup_frames", "drop_frames", "speed", "progress"];

    [Fact]
    public void ProgressFileHasFfmpegKeys()
    {
        using Tool tool = new();
        ToolRun run = tool.Run("remux", Path.Combine(Tool.Corpus, "avi-mjpeg-420-pcm.avi"), "-o", "out.mkv", "--progress", "progress.txt");
        Assert.True(run.Code == 0, run.Error);
        string[] lines = File.ReadAllLines(tool.PathOf("progress.txt"));
        Assert.Equal("progress=end", lines[^1]);
        List<string> lastBlock = [.. lines.Reverse().TakeWhile((l, i) => i == 0 || !l.StartsWith("progress=", StringComparison.Ordinal)).Reverse()];
        Assert.Equal(FfmpegKeys, lastBlock.Select(l => l[..l.IndexOf('=', StringComparison.Ordinal)]));
        Assert.Contains("out_time=00:00:0", string.Join('\n', lastBlock), StringComparison.Ordinal);
        Assert.Contains("frame=50", lastBlock);
    }

    [Fact]
    public void ProgressJsonOnStandardOutput()
    {
        using Tool tool = new();
        ToolRun run = tool.Run("remux", Path.Combine(Tool.Corpus, "avi-mjpeg-420-pcm.avi"), "-o", "out.mkv", "--progress", "-", "--progress-format", "json");
        Assert.True(run.Code == 0, run.Error);
        string[] lines = run.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines.Length >= 2);
        using JsonDocument last = JsonDocument.Parse(lines[^1]);
        Assert.Equal("end", last.RootElement.GetProperty("progress").GetString());
        Assert.Equal(1.0, last.RootElement.GetProperty("fraction").GetDouble());
        Assert.Equal(50, last.RootElement.GetProperty("frames").GetInt64());
    }

    [Fact]
    public void BatchReportsCarryTheFile()
    {
        using Tool tool = new();
        tool.Copy("avi-mjpeg-420-pcm.avi", "a.avi");
        tool.Copy("avi-tags.avi", "b.avi");
        ToolRun run = tool.Run("remux", "*.avi", "-o", "out/{name}.mkv", "--progress", "-", "-j", "1");
        Assert.True(run.Code == 0, run.Error);
        Assert.Contains("file=a.avi\nfile_index=0\nfiles=2\n", run.Text, StringComparison.Ordinal);
        Assert.Contains("file=b.avi\nfile_index=1\nfiles=2\n", run.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ProgressFormatNeedsProgress()
    {
        using Tool tool = new();
        ToolRun run = tool.Run("remux", Path.Combine(Tool.Corpus, "avi-mjpeg-420-pcm.avi"), "-o", "out.mkv", "--progress-format", "json");
        Assert.Equal(2, run.Code);
        Assert.Equal(2, tool.Run("remux", Path.Combine(Tool.Corpus, "avi-mjpeg-420-pcm.avi"), "-o", "out.mkv", "--progress", "-", "--progress-format", "xml").Code);
    }

    [Fact]
    public void LiveLine()
    {
        InputFile input = new("x.mkv", "x.mkv", string.Empty, 1);
        MediaProgress progress = new()
        {
            Position = MediaTime.FromSeconds(12.5),
            Duration = MediaTime.FromSeconds(25),
            Fraction = 0.5,
            Frames = 300,
            BytesWritten = 3 << 20,
            Elapsed = TimeSpan.FromSeconds(2),
            Speed = 6.25,
        };
        Assert.Equal("[2/5] x.mkv time=0:00:12.500 50% frame=300 fps=150 size=3.0 MiB speed=6.25x eta=0:00:02", ProgressSink.LiveLine(input, 1, 5, progress));
        Assert.Equal("time=0:00:12.500 50% frame=300 fps=150 size=3.0 MiB speed=6.25x eta=0:00:02", ProgressSink.LiveLine(input, 0, 1, progress));
    }
}
