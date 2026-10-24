using System.Text.Json;

using Egami.Pixels;

namespace Egami.Video.Cli.Tests;

/// <summary>
/// V1.4 Build Plan task 18: the new <c>scenes</c> options, the <c>waveform</c> and <c>spectrum</c> pictures with and without a
/// legend, and <c>generate</c>.
/// </summary>
public sealed class AnalysisCommandTests
{
    [Fact]
    public void ScenesFadesFramesAndSeries()
    {
        using Tool tool = new();
        AnalyzeTests.WriteClip(tool.PathOf("clip.mkv"));
        ToolRun run = tool.Run("scenes", "clip.mkv", "--fades", "--json", "--min-length", "0.5s", "--series", "scores.csv", "--frame", "middle", "-o", "shots/{scene}.png");
        Assert.True(run.Code == 0, run.Error);
        JsonElement file = JsonDocument.Parse(run.Text).RootElement.GetProperty("files")[0];
        JsonElement[] scenes = [.. file.GetProperty("scenes").EnumerateArray()];
        Assert.Contains(scenes, s => s.GetProperty("kind").GetString() == "fade");
        Assert.True(file.TryGetProperty("fades", out _));
        Assert.Equal(scenes.Length, Directory.GetFiles(tool.PathOf("shots"), "*.png").Length);

        // One score per frame: 1 s, 2.4 s, 2.4 s and 1 s at 25 fps.
        string[] series = File.ReadAllLines(tool.PathOf("scores.csv"));
        Assert.Equal("file,frame,time,seconds,score,luma", series[0]);
        Assert.Equal(170, series.Length - 1);

        // --min-gap still works, and text output shows the kind with --fades only.
        ToolRun text = tool.Run("scenes", "clip.mkv", "--min-gap", "0.5s", "--flash", "suppress");
        Assert.True(text.Code == 0, text.Error);
        Assert.DoesNotContain("kind", text.Text);
        Assert.Contains("kind", tool.Run("scenes", "clip.mkv", "--fades").Text);
    }

    [Theory]
    [InlineData("flash", "is not merge or suppress", "scenes", "clip.mkv", "--flash", "drop")]
    [InlineData("frame", "is not first, middle or last", "scenes", "clip.mkv", "--frame", "best")]
    [InlineData("size", "is not a size", "waveform", "clip.mkv", "-o", "w.png", "--size", "big")]
    [InlineData("channels", "is not mixed or stacked", "waveform", "clip.mkv", "-o", "w.png", "--channels", "left")]
    [InlineData("color", "is not a color", "waveform", "clip.mkv", "-o", "w.png", "--color", "blue")]
    [InlineData("window", "power of two", "spectrum", "clip.mkv", "-o", "s.png", "--window", "1000")]
    [InlineData("floor", "--floor must be below --ceiling", "spectrum", "clip.mkv", "-o", "s.png", "--floor", "0")]
    [InlineData("no output", "No output", "spectrum", "clip.mkv")]
    [InlineData("source", "is not a test source", "generate", "fireworks", "f.mkv")]
    [InlineData("tone on sound", "is a sound already", "generate", "sine", "t.wav", "--tone")]
    [InlineData("y4m sound", "holds video only", "generate", "bars", "b.y4m", "--tone")]
    [InlineData("flac in wav", "holds PCM, not FLAC", "generate", "sine", "t.wav", "--audio-codec", "flac")]
    public void ChecksOptions(string label, string message, params string[] args)
    {
        _ = label;
        using Tool tool = new();
        AnalyzeTests.WriteClip(tool.PathOf("clip.mkv"));
        ToolRun run = tool.Run(args);
        Assert.Equal(2, run.Code);
        Assert.Contains(message, run.Error);
    }

    [Fact]
    public void WaveformAndSpectrumPictures()
    {
        using Tool tool = new();
        AnalyzeTests.WriteClip(tool.PathOf("clip.mkv"));
        ToolRun wave = tool.Run("waveform", "clip.mkv", "-o", "wave.png", "--size", "680x120", "--channels", "stacked");
        Assert.True(wave.Code == 0, wave.Error);
        using (Raster<Rgba8> image = Raster.Load<Rgba8>(tool.PathOf("wave.png")))
        {
            Assert.Equal(680, image.Width);
            Assert.Equal(120, image.Height);

            // Silence from 1 to 3.4 s of 6.8: the middle of that stretch draws only the centre line of each strip.
            Assert.Equal(0, image.Row(10)[220].A);
            Assert.NotEqual(0, image.Row(30)[50].A);
        }

        ToolRun spectrum = tool.Run("spectrum", "clip.mkv", "-o", "spec.png", "--size", "400x200", "--legend", "--scale", "log", "--colors", "gray");
        Assert.True(spectrum.Code == 0, spectrum.Error);
        using (Raster<Rgba8> image = Raster.Load<Rgba8>(tool.PathOf("spec.png")))
        {
            Assert.Equal(400, image.Width);
            Assert.Equal(200, image.Height);
        }

        ToolRun legend = tool.Run("waveform", "clip.mkv", "-o", "legend.png", "--legend", "--scale", "db", "--rms-color", "none", "--color", "#ff8800");
        Assert.True(legend.Code == 0, legend.Error);
        using Raster<Rgba8> framed = Raster.Load<Rgba8>(tool.PathOf("legend.png"));
        Assert.Equal(1800, framed.Width);
        Assert.Equal(255, framed.Row(0)[0].A);
    }

    [Fact]
    public void SegmentsAtScenes()
    {
        using Tool tool = new();

        // Every MJPEG frame is a keyframe: each part starts on its scene's first frame and holds it to the next.
        AnalyzeTests.WriteClip(tool.PathOf("clip.mkv"));
        ToolRun scenes = tool.Run("analyze", "clip.mkv", "-d", "scenes", "--json");
        int count = JsonDocument.Parse(scenes.Text).RootElement.GetProperty("files")[0].GetProperty("scenes").GetArrayLength();
        ToolRun run = tool.Run("segment", "clip.mkv", "--scenes", "-o", "parts/{part}.mkv", "-v");
        Assert.True(run.Code == 0, run.Error);
        string[] parts = [.. Directory.GetFiles(tool.PathOf("parts"), "*.mkv").Order(StringComparer.Ordinal)];
        Assert.True(count == parts.Length, run.Error + scenes.Text);
        Assert.Contains(", 0.000 s before", run.Error, StringComparison.Ordinal);
        Assert.Equal(170, parts.Sum(p => CutTests.Read(p).Count(k => k.Kind == TrackKind.Video)));

        // VP8 with a keyframe a second: parts start at the keyframe before each scene, and say how far before.
        AnalyzeTests.WriteClip(tool.PathOf("vp8.mkv"), Egami.Video.Codecs.Vp8Codec.Instance, keyframeInterval: 25);
        ToolRun vp8 = tool.Run("segment", "vp8.mkv", "--scenes", "-o", "vp8/{part}.mkv", "-v");
        Assert.True(vp8.Code == 0, vp8.Error);
        Assert.Contains("scene 2 starts at 0:00:02.200; its part starts at the keyframe at 0:00:02.000, 0.200 s before", vp8.Error, StringComparison.Ordinal);
        Assert.Contains("part 2 starts at 0:00:02.000", vp8.Error, StringComparison.Ordinal);

        // --exact: every VP8 part starts on its scene's first frame, by smart cut; the parts hold every frame once.
        ToolRun exact = tool.Run("segment", "vp8.mkv", "--scenes", "--exact", "-o", "x/{part}.mkv", "-v");
        Assert.True(exact.Code == 0, exact.Error);
        Assert.Contains("part 2 starts at 0:00:02.200 (scene 2)", exact.Error, StringComparison.Ordinal);
        string[] exactParts = [.. Directory.GetFiles(tool.PathOf("x"), "*.mkv").Order(StringComparer.Ordinal)];
        Assert.Equal(parts.Length, exactParts.Length);
        Assert.Equal(170, exactParts.Sum(p =>
        {
            using VideoReader reader = VideoReader.Open(p);
            return reader.ReadFrames().Count(f =>
            {
                f.Dispose();
                return true;
            });
        }));
        Assert.All(exactParts, p => Assert.True(CutTests.Read(p).First(k => k.Kind == TrackKind.Video).Keyframe));
        Assert.Equal(2, tool.Run("segment", "vp8.mkv", "--scenes", "--exact", "--keep-timestamps", "-o", "w/{part}.mkv").Code);
        Assert.Equal(2, tool.Run("segment", "clip.mkv", "--every", "2", "--threshold", "0.3", "-o", "y/{part}.mkv").Code);
        Assert.Equal(2, tool.Run("segment", "clip.mkv", "--every", "2", "--scenes", "-o", "z/{part}.mkv").Code);
    }

    [Fact]
    public void RunCropsBarsAway()
    {
        using Tool tool = new();
        AnalyzeTests.WriteClip(tool.PathOf("clip.mkv"));
        ToolRun boxed = tool.Run("run", "clip.mkv", "pad", "192", "148", "--background", "#000000", "+", "save", "boxed.mkv");
        Assert.True(boxed.Code == 0, boxed.Error);
        ToolRun run = tool.Run("run", "boxed.mkv", "--crop", "auto", "save", "cropped.mkv", "-v");
        Assert.True(run.Code == 0, run.Error);
        VideoTrackInfo track = MediaFile.Probe(tool.PathOf("cropped.mkv")).Tracks.OfType<VideoTrackInfo>().Single();
        Assert.Equal((192, 108), (track.Width, track.Height));
        Assert.Equal(2, tool.Run("run", "boxed.mkv", "--crop", "16:9", "save", "x.mkv").Code);
    }

    [Theory]
    [InlineData("bars", "bars.mkv", "--tone", "-t", "2", "--size", "320x180")]
    [InlineData("pattern", "pattern.webm", "--codec", "vp8", "--audio", "noise", "-t", "1", "--size", "160x90", "--rate", "25")]
    [InlineData("gradient", "-o", "g.avi", "--color", "#102030", "--to-color", "#f0e0d0", "--vertical", "-t", "1", "--size", "64x64")]
    [InlineData("solid", "s.y4m", "-t", "0.5", "--size", "64x48")]
    public void GeneratesClips(string source, params string[] rest)
    {
        using Tool tool = new();
        ToolRun run = tool.Run(["generate", source, .. rest]);
        Assert.True(run.Code == 0, run.Error);
        string file = rest[0] == "-o" ? rest[1] : rest[0];
        ToolRun info = tool.Run("info", file, "--json");
        Assert.True(info.Code == 0, info.Error);
        JsonElement tracks = JsonDocument.Parse(info.Text).RootElement.GetProperty("files")[0].GetProperty("tracks");
        bool sound = rest.Contains("--tone") || rest.Contains("--audio");
        Assert.Equal(sound ? 2 : 1, tracks.GetArrayLength());
    }

    [Fact]
    public void GeneratesSounds()
    {
        using Tool tool = new();
        Assert.Equal(0, tool.Run("generate", "sine", "tone.wav", "-t", "1", "--frequency", "440", "--level", "-6", "--channels", "mono").Code);
        Assert.Equal(0, tool.Run("generate", "sweep", "sweep.flac", "-t", "1").Code);
        Assert.Equal(0, tool.Run("generate", "noise", "pink.opus", "-t", "1", "--noise", "pink").Code);

        ToolRun levels = tool.Run("analyze", "tone.wav", "-d", "levels", "--json");
        Assert.True(levels.Code == 0, levels.Error);
        JsonElement overall = JsonDocument.Parse(levels.Text).RootElement.GetProperty("files")[0].GetProperty("levels").GetProperty("overall");
        Assert.Equal(-6, overall.GetProperty("peak").GetDouble(), 0.05);
        Assert.Equal(48000, JsonDocument.Parse(levels.Text).RootElement.GetProperty("files")[0].GetProperty("levels").GetProperty("samples").GetInt64());
    }
}
