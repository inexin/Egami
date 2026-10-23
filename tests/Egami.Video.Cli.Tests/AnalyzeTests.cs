using System.Text.Json;

using Egami.Pixels;
using Egami.Video.Analysis;
using Egami.Video.Audio;

namespace Egami.Video.Cli.Tests;

/// <summary>
/// The <c>analyze</c> command (V1.4 Build Plan task 17): each detector on a generated clip with known black, frozen and silent
/// stretches, the three output forms, the series file and the checks on its options.
/// </summary>
public sealed class AnalyzeTests
{
    private static readonly Rational Rate = new(25, 1);

    /// <summary>
    /// Writes 1 s of color bars with a tone, 2.4 s of black with silence, 2.4 s of a still blue picture with a tone, then 1 s of
    /// the moving pattern with a tone, at 25 fps and 48 kHz stereo.
    /// </summary>
    internal static void WriteClip(string path, Egami.Video.Codecs.VideoCodec? codec = null, int? keyframeInterval = null)
    {
        TestVideoFormat format = new() { Width = 192, Height = 108, FrameRate = Rate };
        using VideoWriter writer = VideoWriter.Create(path, new VideoWriterOptions
        {
            FrameRate = Rate,
            Codec = codec ?? Egami.Video.Codecs.MotionJpegCodec.Instance,
            KeyframeInterval = keyframeInterval,
            SceneCutKeyframes = keyframeInterval is null,
            AudioCodec = Egami.Video.Audio.Codecs.FlacCodec.Instance,
            AudioSampleRate = 48000,
            AudioLayout = ChannelLayout.Stereo,
        });
        IEnumerable<YuvVideoFrame> video = TestSources.ColorBars(format, MediaTime.FromSeconds(1))
            .Concat(TestSources.Solid(format, MediaTime.FromSeconds(2.4), new Rgba8(0, 0, 0, 255)))
            .Concat(TestSources.Solid(format, MediaTime.FromSeconds(2.4), new Rgba8(30, 40, 220, 255)))
            .Concat(TestSources.Pattern(format, MediaTime.FromSeconds(1)));
        int index = 0;
        foreach (YuvVideoFrame frame in video)
        {
            using (frame)
            {
                writer.WriteFrame(frame.Raster, new MediaTime(index++, Rate.Reciprocal()));
            }
        }

        IEnumerable<AudioFrame> audio = TestSources.Sine(null, MediaTime.FromSeconds(1))
            .Concat(TestSources.Silence(null, MediaTime.FromSeconds(2.4)))
            .Concat(TestSources.Sine(null, MediaTime.FromSeconds(3.4)));
        foreach (AudioFrame block in audio)
        {
            using (block)
            {
                writer.WriteAudio(block);
            }
        }
    }

    [Fact]
    public void FindsEachStretch()
    {
        using Tool tool = new();
        WriteClip(tool.PathOf("clip.mkv"));
        ToolRun run = tool.Run("analyze", "clip.mkv", "--detect", "scenes,black,freeze,silence,crop", "--json");
        Assert.True(run.Code == 0, run.Error);
        JsonElement file = JsonDocument.Parse(run.Text).RootElement.GetProperty("files")[0];

        JsonElement black = Assert.Single(file.GetProperty("black").EnumerateArray());
        Assert.Equal(1.0, black.GetProperty("start").GetDouble(), 0.041);
        Assert.Equal(3.4, black.GetProperty("end").GetDouble(), 0.041);

        // The black stretch and the still blue picture are both frozen.
        JsonElement[] freeze = [.. file.GetProperty("freeze").EnumerateArray()];
        Assert.Contains(freeze, f => Math.Abs(f.GetProperty("start").GetDouble() - 3.4) < 0.081);

        JsonElement silence = Assert.Single(file.GetProperty("silence").EnumerateArray());
        Assert.Equal(1.0, silence.GetProperty("start").GetDouble(), 0.01);
        Assert.Equal(3.4, silence.GetProperty("end").GetDouble(), 0.01);

        // The black stretch between bars and blue is a fade out and in, so its scene starts in the middle of it; then a cut into
        // the pattern.
        JsonElement[] scenes = [.. file.GetProperty("scenes").EnumerateArray()];
        Assert.Contains(scenes, s => s.GetProperty("kind").GetString() == "fade" && s.GetProperty("start").GetDouble() is > 1 and < 3.4);
        Assert.Contains(scenes, s => s.GetProperty("kind").GetString() == "cut" && Math.Abs(s.GetProperty("start").GetDouble() - 5.8) < 0.041);

        JsonElement crop = file.GetProperty("crop");
        Assert.Equal(192, crop.GetProperty("frameWidth").GetInt32());
    }

    [Fact]
    public void AllRunsEveryDetector()
    {
        using Tool tool = new();
        WriteClip(tool.PathOf("clip.mkv"));
        ToolRun run = tool.Run("analyze", "clip.mkv", "--all", "--json");
        Assert.True(run.Code == 0, run.Error);
        JsonElement file = JsonDocument.Parse(run.Text).RootElement.GetProperty("files")[0];
        foreach (string name in new[] { "scenes", "black", "freeze", "crop", "interlace", "duplicates", "signal", "silence", "levels", "loudness" })
        {
            Assert.True(file.TryGetProperty(name, out _), name);
        }

        Assert.DoesNotContain("interlaced", file.GetProperty("interlace").GetProperty("verdict").GetString(), StringComparison.Ordinal);
        Assert.True(file.GetProperty("signal").TryGetProperty("blur", out _));

        // The -18 dBFS tone plays for 4.4 of 6.8 s: peak -18 dBFS; the integrated loudness gates out the silence, all but the
        // blocks that straddle its edges.
        Assert.Equal(-18, file.GetProperty("levels").GetProperty("overall").GetProperty("peak").GetDouble(), 0.1);
        Assert.Equal(-18, file.GetProperty("loudness").GetProperty("integrated").GetDouble(), 0.5);
    }

    [Fact]
    public void TextAndCsv()
    {
        using Tool tool = new();
        WriteClip(tool.PathOf("clip.mkv"));
        ToolRun text = tool.Run("analyze", "clip.mkv", "-d", "black", "-d", "silence", "--silence-noise", "-50dB", "--black-min", "1s");
        Assert.True(text.Code == 0, text.Error);
        Assert.Contains("black: 1 range", text.Text);
        Assert.Contains("silence: 1 range", text.Text);
        Assert.Contains("0:00:01.000 to 0:00:03.400", text.Text);

        ToolRun csv = tool.Run("analyze", "clip.mkv", "--detect", "black,loudness", "--csv");
        Assert.True(csv.Code == 0, csv.Error);
        string[] lines = csv.Text.TrimEnd('\n').Split('\n');
        Assert.Equal("file,detector,start,end,duration,name,value", lines[0]);
        Assert.Contains(lines, l => l.StartsWith("clip.mkv,black,1,3.4,2.4,ratio,", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("clip.mkv,loudness,,,,integrated,", StringComparison.Ordinal));
    }

    [Fact]
    public void WindowAndSeries()
    {
        using Tool tool = new();
        WriteClip(tool.PathOf("clip.mkv"));

        // From 2 s for 2 s: the black stretch is cut to 2 to 3.4 s.
        ToolRun run = tool.Run("analyze", "clip.mkv", "-d", "black,loudness,signal,scenes", "--black-min", "1s", "--start", "2", "--duration", "2s", "--json", "--series", "out/series.csv");
        Assert.True(run.Code == 0, run.Error);
        JsonElement file = JsonDocument.Parse(run.Text).RootElement.GetProperty("files")[0];
        JsonElement black = Assert.Single(file.GetProperty("black").EnumerateArray());
        Assert.Equal(2.0, black.GetProperty("start").GetDouble(), 0.041);
        Assert.Equal(3.4, black.GetProperty("end").GetDouble(), 0.041);

        string[] series = File.ReadAllLines(tool.PathOf("out/series.csv"));
        Assert.Equal("file,detector,time,seconds,name,value", series[0]);
        Assert.Contains(series, l => l.Contains(",black,", StringComparison.Ordinal) && l.Contains(",ratio,", StringComparison.Ordinal));
        Assert.Contains(series, l => l.Contains(",loudness,", StringComparison.Ordinal) && l.Contains(",momentary,", StringComparison.Ordinal));
        Assert.Contains(series, l => l.Contains(",signal,", StringComparison.Ordinal) && l.Contains(",y.mean,", StringComparison.Ordinal));
        Assert.Contains(series, l => l.Contains(",scenes,", StringComparison.Ordinal) && l.Contains(",score,", StringComparison.Ordinal));
    }

    [Fact]
    public void AllSkipsMissingTracks()
    {
        using Tool tool = new();
        WriteClip(tool.PathOf("clip.mkv"));
        ToolRun extract = tool.Run("extract", "clip.mkv", "-t", "a", "-o", "tone.mka");
        Assert.True(extract.Code == 0, extract.Error);

        ToolRun all = tool.Run("analyze", "tone.mka", "--all", "--json");
        Assert.True(all.Code == 0, all.Error);
        JsonElement file = JsonDocument.Parse(all.Text).RootElement.GetProperty("files")[0];
        Assert.True(file.TryGetProperty("silence", out _));
        Assert.False(file.TryGetProperty("black", out _));

        // Asked for by name, a detector without its track is an error.
        ToolRun black = tool.Run("analyze", "tone.mka", "-d", "black");
        Assert.Equal(2, black.Code);
        Assert.Contains("no video track", black.Error);
    }

    [Theory]
    [InlineData("nothing", "Nothing to look for", "analyze", "clip.mkv")]
    [InlineData("unknown", "is not a detector", "analyze", "clip.mkv", "-d", "flash")]
    [InlineData("both", "not both", "analyze", "clip.mkv", "--all", "--json", "--csv")]
    [InlineData("noise", "is not a level", "analyze", "clip.mkv", "-d", "silence", "--silence-noise", "loud")]
    [InlineData("ratio", "--black-ratio must be from 0 to 1", "analyze", "clip.mkv", "-d", "black", "--black-ratio", "2")]
    [InlineData("method", "is not adaptive, content or histogram", "analyze", "clip.mkv", "-d", "scenes", "--scene-method", "fast")]
    [InlineData("duration", "--duration", "analyze", "clip.mkv", "-d", "black", "--duration", "0")]
    [InlineData("stdin", "not standard input", "analyze", "-", "-d", "black")]
    public void ChecksItsOptions(string label, string message, params string[] args)
    {
        _ = label;
        using Tool tool = new();
        WriteClip(tool.PathOf("clip.mkv"));
        ToolRun run = tool.Run(args);
        Assert.Equal(2, run.Code);
        Assert.Contains(message, run.Error);
    }
}
