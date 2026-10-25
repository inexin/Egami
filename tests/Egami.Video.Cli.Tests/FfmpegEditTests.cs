namespace Egami.Video.Cli.Tests;

/// <summary>
/// ffmpeg mode's editing filters (V9 design Q14, Build Plan task 22): every line of ffmpeg-edit-lines.txt runs for real, or stops
/// with exit code 2 and the reason.
/// </summary>
public sealed class FfmpegEditTests
{
    public static TheoryData<string, string> Lines()
    {
        TheoryData<string, string> lines = [];
        foreach (string line in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "ffmpeg-edit-lines.txt")))
        {
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            int bar = line.IndexOf(" | ", StringComparison.Ordinal);
            lines.Add(line[..bar].Trim(), line[(bar + 3)..].Trim());
        }

        return lines;
    }

    [Fact]
    public void TheSetHasSixtyLines() => Assert.True(Lines().Count >= 60);

    [Theory]
    [MemberData(nameof(Lines))]
    public void LineRunsOrIsRefused(string expected, string command)
    {
        using Tool tool = new();
        tool.Copy("avi-mjpeg-420-pcm.avi", "a.avi");
        tool.Copy("avi-mjpeg-420-pcm.avi", "b.avi");
        tool.Copy("avi-mjpeg-444-noaudio.avi", "c.avi");
        tool.CopyImage("ops/shapes.png", "logo.png");
        tool.CopyImage("ops/duck.png", "still.png");
        ToolRun run = null!;
        foreach (string part in command.Split(" && "))
        {
            run = tool.Run(FfmpegModeTests.Split(part));
            if (run.Code != 0)
            {
                break;
            }
        }

        if (expected.StartsWith("ok", StringComparison.Ordinal))
        {
            Assert.True(run.Code == 0, $"{command}\n{run.Error}");
            if (expected.Length > 3)
            {
                double seconds = double.Parse(expected[3..], System.Globalization.CultureInfo.InvariantCulture);
                Assert.Equal(seconds, MediaFile.Probe(tool.PathOf("out.mkv")).Duration!.Value.Seconds, 0.15);
            }
        }
        else
        {
            Assert.True(run.Code == 2, $"{command}: exit {run.Code}\n{run.Error}");
            Assert.Contains(expected[3..], run.Error, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("-af", "volume=0.5")]
    [InlineData("-af", "atempo=1")]
    [InlineData("-vf", "hflip=enable='between(t,0.5,1)'")]
    [InlineData("-vf", "fps=10,fade=t=in:d=0.5")]
    public void SoundComesThroughFiltersThatNeedNoProbing(string option, string graph)
    {
        using Tool tool = new();
        tool.Copy("avi-mjpeg-420-pcm.avi", "a.avi");
        ToolRun run = tool.Run("ffmpeg", "-i", "a.avi", option, graph, "out.mkv");
        Assert.True(run.Code == 0, run.Error);
        AudioTrackInfo sound = Assert.Single(MediaFile.Probe(tool.PathOf("out.mkv")).Tracks.OfType<AudioTrackInfo>());
        Assert.Equal(2, sound.Duration?.Seconds ?? MediaFile.Probe(tool.PathOf("out.mkv")).Duration!.Value.Seconds, 0.15);
    }

    [Fact]
    public void ExplainPrintsTheEditFile()
    {
        using Tool tool = new();
        tool.Copy("avi-mjpeg-420-pcm.avi", "a.avi");
        ToolRun run = tool.Run("ffmpeg", "-i", "a.avi", "-vf", "fade=t=in:d=0.5", "out.mkv", "--explain");
        Assert.True(run.Code == 0, run.Error);
        Assert.StartsWith("egami-video edit edit.json -o out.mkv", run.Text, StringComparison.Ordinal);
        Assert.Contains("\"fadeIn\"", run.Text, StringComparison.Ordinal);
        Assert.False(File.Exists(tool.PathOf("out.mkv")));
    }
}
