using Egami.Cli.Commands;
using Egami.Cli.Host;
using Egami.Cli.Pipeline;
using Egami.Cli.Steps;
using Egami.Pixels;
using Egami.Video.Codecs;

namespace Egami.Video.Cli.Tests;

/// <summary>
/// <c>run</c> (Build Plan task 12): egami run's steps over every frame give the same pixels as egami run itself on each
/// frame; frames keep their times, audio is copied, and the steps' syntax and errors are egami run's.
/// </summary>
public sealed class RunTests
{
    [Theory]
    [InlineData("resize 75%")]
    [InlineData("crop 10 10 100 80 + blur 1.5 + contrast 1.3")]
    [InlineData("flip horizontal + sepia + resize 64x64 --fit pad --background red")]
    [InlineData("grayscale + resize 50% + sharpen")]
    public void SamePixelsAsEgamiRun(string chain)
    {
        using Tool tool = new();
        string clip = Path.Combine(Tool.Corpus, "mkv-mjpeg-pcm.mkv");
        string[] steps = chain.Split(' ');
        ToolRun run = tool.Run(["run", clip, .. steps, "+", "save", "out.mkv", "--codec", "raw"]);
        Assert.True(run.Code == 0, run.Error);

        // egami run itself, through its Runner, on each decoded frame saved losslessly as PNG.
        CliSession egami = new(new CliConsole(() => Stream.Null, () => Stream.Null, TextWriter.Null, TextWriter.Null), tool.Folder, _ => null);
        egami.Configure(Verbosity.Quiet, null, 0);
        List<PipelineItem> items = [.. RunCommand.Parse(steps).Steps.Select(RunCommand.Bind)];
        Plan plan = new([.. items, new SaveItem(SaveSpec.Default)], ReadSpec.Default);
        List<(MediaTime Time, Raster<Rgba8> Image)> source = Decode(clip);
        string expectedPath = tool.PathOf("expected.mkv");
        using (VideoWriter writer = VideoWriter.Create(expectedPath, new VideoWriterOptions { Codec = RawVideoCodec.Instance, FrameRate = new Rational(25, 1), ChromaFormat = ChromaFormat.Yuv444 }))
        {
            for (int i = 0; i < source.Count; i++)
            {
                string frame = tool.PathOf($"frames/{i:000}.png");
                Directory.CreateDirectory(Path.GetDirectoryName(frame)!);
                source[i].Image.Save(frame);
                Runner.Run(egami, new InputSource(new InputFile(frame, frame, string.Empty, i)), plan, OutputTemplate.Parse(tool.PathOf($"egami/{i:000}.png")));
                using Raster<Rgba8> expected = Raster.Load(tool.PathOf($"egami/{i:000}.png")).As<Rgba8>();
                writer.WriteFrame(expected, source[i].Time);
                source[i].Image.Dispose();
            }
        }

        List<(MediaTime Time, Raster<Rgba8> Image)> ours = Decode(tool.PathOf("out.mkv"));
        List<(MediaTime Time, Raster<Rgba8> Image)> theirs = Decode(expectedPath);
        Assert.Equal(theirs.Count, ours.Count);
        Assert.Equal(source.Select(s => s.Time), ours.Select(o => o.Time));
        for (int i = 0; i < ours.Count; i++)
        {
            Assert.Equal(theirs[i].Image.Width, ours[i].Image.Width);
            Assert.Equal(theirs[i].Image.Height, ours[i].Image.Height);
            for (int y = 0; y < ours[i].Image.Height; y++)
            {
                Assert.True(theirs[i].Image.Row(y).SequenceEqual(ours[i].Image.Row(y)), $"frame {i}, row {y} differs");
            }
        }
    }

    [Fact]
    public void KeepsTimesAndCopiesAudio()
    {
        using Tool tool = new();
        string clip = Path.Combine(Tool.Corpus, "avi-mjpeg-420-pcm.avi");
        ToolRun run = tool.Run("run", clip, "resize", "50%", "+", "save", "out.mkv");
        Assert.True(run.Code == 0, run.Error);
        List<string> source = RemuxTests.Packets(clip);
        List<string> copy = RemuxTests.Packets(tool.PathOf("out.mkv"));
        Assert.Equal(source.Where(p => p.StartsWith("Audio", StringComparison.Ordinal)), copy.Where(p => p.StartsWith("Audio", StringComparison.Ordinal)));
        Assert.Equal(VideoTimes(clip), VideoTimes(tool.PathOf("out.mkv")));
        VideoTrackInfo video = MediaFile.Probe(tool.PathOf("out.mkv")).Tracks.OfType<VideoTrackInfo>().Single();
        Assert.Equal((80, 60), (video.Width, video.Height));
        Assert.Equal(CodecIds.MotionJpeg, video.CodecId);
    }

    [Fact]
    public void LeavesOutAudioAndPicksTheTrack()
    {
        using Tool tool = new();
        string clip = Path.Combine(Tool.Corpus, "avi-mjpeg-420-pcm.avi");
        Assert.Equal(0, tool.Run("run", clip, "grayscale", "-o", "quiet.mkv", "-an").Code);
        Assert.Equal([TrackKind.Video], MediaFile.Probe(tool.PathOf("quiet.mkv")).Tracks.Select(t => t.Kind));
        Assert.Equal(0, tool.Run("run", clip, "grayscale", "-o", "picked.mkv", "--track", "v:0").Code);
        Assert.Equal(2, MediaFile.Probe(tool.PathOf("picked.mkv")).Tracks.Count);
        ToolRun audio = tool.Run("run", clip, "grayscale", "-o", "none.mkv", "--track", "a");
        Assert.Equal(2, audio.Code);
        Assert.Contains("no video track", audio.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("mkv-mjpeg-pcm.mkv", null, CodecIds.MotionJpeg)]
    [InlineData("mkv-raw-i420.mkv", null, CodecIds.Uncompressed)]
    [InlineData("webm-vp8-opus.webm", null, CodecIds.Vp8)]
    [InlineData("mkv-mjpeg-pcm.mkv", "raw", CodecIds.Uncompressed)]
    public void CodecIsTheSourcesWhenItCanBeEncoded(string clip, string? codec, string expected)
    {
        using Tool tool = new();
        string[] args = ["run", Path.Combine(Tool.Corpus, clip), "invert", "-o", "out.mkv"];
        ToolRun run = tool.Run(codec is null ? args : [.. args, "--codec", codec]);
        Assert.True(run.Code == 0, run.Error);
        Assert.Equal(expected, MediaFile.Probe(tool.PathOf("out.mkv")).Tracks.OfType<VideoTrackInfo>().Single().CodecId);
    }

    [Fact]
    public void RateDropsOrRepeatsFrames()
    {
        using Tool tool = new();
        string clip = Path.Combine(Tool.Corpus, "mkv-mjpeg-pcm.mkv");
        Assert.Equal(0, tool.Run("run", clip, "grayscale", "-o", "ten.mkv", "--rate", "10").Code);
        Assert.Equal(Enumerable.Range(0, 20).Select(i => Math.Round(i / 10.0, 3)), VideoTimes(tool.PathOf("ten.mkv")));
        Assert.Equal(0, tool.Run("run", clip, "grayscale", "-o", "fifty.mkv", "--rate", "50").Code);
        Assert.Equal(100, VideoTimes(tool.PathOf("fifty.mkv")).Count);
    }

    [Fact]
    public void WorkflowFiles()
    {
        using Tool tool = new();
        string clip = Path.Combine(Tool.Corpus, "mkv-mjpeg-pcm.mkv");
        ToolRun saved = tool.Run("run", "resize", "50%", "+", "sepia", "--save-workflow", "steps.json");
        Assert.True(saved.Code == 0, saved.Error);
        Assert.Equal(0, tool.Run("run", clip, "resize", "50%", "+", "sepia", "-o", "words.mkv", "--codec", "raw").Code);
        Assert.Equal(0, tool.Run("run", clip, "--workflow", "steps.json", "-o", "file.mkv", "--codec", "raw").Code);
        Assert.Equal(RemuxTests.Packets(tool.PathOf("words.mkv")), RemuxTests.Packets(tool.PathOf("file.mkv")));
    }

    [Fact]
    public void FramesOfAnotherSizeNameTheStep()
    {
        using Tool tool = new();
        string clip = tool.PathOf("border.mkv");
        using (VideoWriter writer = VideoWriter.Create(clip, new VideoWriterOptions { Codec = RawVideoCodec.Instance, ChromaFormat = ChromaFormat.Yuv444 }))
        {
            for (int i = 0; i < 4; i++)
            {
                using Raster<Rgba8> frame = Raster.Create(64, 48, Rgba8.White);
                int inset = i < 2 ? 8 : 4;
                frame.Apply(ops => ops.FillColor(new Rgba8(200, 30, 30, 255), new Rect(inset, inset, 64 - (2 * inset), 48 - (2 * inset))), TestContext.Current.CancellationToken);
                writer.WriteFrame(frame);
            }
        }

        ToolRun run = tool.Run("run", "border.mkv", "trim", "+", "save", "out.mkv");
        Assert.Equal(1, run.Code);
        Assert.Contains("frame #2", run.Error, StringComparison.Ordinal);
        Assert.Contains("'trim'", run.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(tool.PathOf("out.mkv")));
        Assert.Equal(0, tool.Run("run", "border.mkv", "trim", "+", "resize", "48x32!", "+", "save", "fixed.mkv").Code);
    }

    [Fact]
    public void PipesInAndOut()
    {
        using Tool tool = new();
        byte[] clip = File.ReadAllBytes(Path.Combine(Tool.Corpus, "mkv-mjpeg-pcm.mkv"));
        ToolRun run = tool.Run(["run", "-", "grayscale", "-o", "-", "--format", "mkv"], clip);
        Assert.True(run.Code == 0, run.Error);
        File.WriteAllBytes(tool.PathOf("piped.mkv"), run.Bytes);
        Assert.Equal(50, VideoTimes(tool.PathOf("piped.mkv")).Count);
    }

    [Fact]
    public void BatchesAndDryRun()
    {
        using Tool tool = new();
        tool.Copy("mkv-mjpeg-pcm.mkv", "in/a.mkv");
        tool.Copy("mkv-raw-i420.mkv", "in/b.mkv");
        ToolRun dry = tool.Run("run", "in/*.mkv", "invert", "+", "save", "out/{name}.mkv", "--dry-run");
        Assert.True(dry.Code == 0, dry.Error);
        Assert.Contains("invert", dry.Text, StringComparison.Ordinal);
        Assert.Contains("out/a.mkv", dry.Text, StringComparison.Ordinal);
        Assert.False(Directory.Exists(tool.PathOf("out")));
        Assert.Equal(0, tool.Run("run", "in/*.mkv", "invert", "+", "save", "out/{name}.mkv").Code);
        Assert.True(File.Exists(tool.PathOf("out/a.mkv")));
        Assert.True(File.Exists(tool.PathOf("out/b.mkv")));
    }

    [Fact]
    public void ListsTheSteps()
    {
        using Tool tool = new();
        ToolRun all = tool.Run("run", "--list");
        Assert.Equal(0, all.Code);
        Assert.Contains("egami-video run", all.Text, StringComparison.Ordinal);
        Assert.Contains("  resize ", all.Text, StringComparison.Ordinal);
        ToolRun one = tool.Run("run", "--list", "blur");
        Assert.Equal(0, one.Code);
        Assert.Contains("--sigma", one.Text, StringComparison.Ordinal);
        Assert.Contains("save", tool.Run("run", "--help").Text, StringComparison.Ordinal);
    }

    [Fact]
    public void OwnOptionsAreNoStepsOptions()
    {
        // egami-video run takes its own options out before egami's parser sees the steps, so no step may have one of them.
        string[] own = ["codec", "rate", "track", "no-audio", "no-subtitles", "progress", "progress-format"];
        Assert.DoesNotContain(StepCatalog.All.SelectMany(s => s.Overloads).SelectMany(o => o.Parameters), p => own.Contains(p.Name));
    }

    [Theory]
    [InlineData("No steps", "run", "{clip}", "-o", "out.mkv")]
    [InlineData("No output", "run", "{clip}", "invert")]
    [InlineData("No inputs", "run", "invert", "-o", "out.mkv")]
    [InlineData("one save", "run", "{clip}", "invert", "+", "save", "a.mkv", "+", "save", "b.mkv")]
    [InlineData("one save", "run", "{clip}", "save", "a.mkv", "+", "invert")]
    [InlineData("cannot encode AV1", "run", "{clip}", "invert", "-o", "out.mkv", "--codec", "av1")]
    [InlineData("not a video codec", "run", "{clip}", "invert", "-o", "out.mkv", "--codec", "h265")]
    [InlineData("not a frame rate", "run", "{clip}", "invert", "-o", "out.mkv", "--rate", "fast")]
    [InlineData("run takes no --to", "run", "{clip}", "invert", "-o", "out.mkv", "--to", "srgb")]
    [InlineData("'egami-video run --help'", "run", "{clip}", "--bogus", "invert", "-o", "out.mkv")]
    [InlineData("is not a step", "run", "{clip}", "invert", "+", "wobble", "-o", "out.mkv")]
    [InlineData("--codec needs a value", "run", "{clip}", "invert", "-o", "out.mkv", "--codec")]
    [InlineData("--quality '0' must be", "run", "{clip}", "invert", "-o", "out.mkv", "--quality", "0")]
    [InlineData("would replace the input", "run", "{clip}", "invert", "-o", "{clip}")]
    [InlineData("give --format too", "run", "{clip}", "invert", "-o", "-")]
    public void UsageErrors(string message, params string[] args)
    {
        using Tool tool = new();
        tool.Copy("mkv-mjpeg-pcm.mkv", "clip.mkv");
        ToolRun run = tool.Run([.. args.Select(a => a.Replace("{clip}", "clip.mkv", StringComparison.Ordinal))]);
        Assert.Equal(2, run.Code);
        Assert.Contains(message, run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ImageSettingsAreIgnoredWithAWarning()
    {
        using Tool tool = new();
        ToolRun run = tool.Run("run", Path.Combine(Tool.Corpus, "mkv-mjpeg-pcm.mkv"), "invert", "+", "save", "out.mkv", "--quality", "60", "--lossless");
        Assert.True(run.Code == 0, run.Error);
        Assert.Contains("--lossless is for image formats", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void CancelledJobLeavesNoFile()
    {
        using Tool tool = new() { Cancellation = new CancellationToken(canceled: true) };
        ToolRun run = tool.Run("run", Path.Combine(Tool.Corpus, "mkv-mjpeg-pcm.mkv"), "invert", "-o", "out.mkv");
        Assert.Equal(130, run.Code);
        Assert.Empty(Directory.GetFiles(tool.Folder, "*", SearchOption.AllDirectories));
    }

    /// <summary>The frames of a file as RGBA pixels with their times.</summary>
    internal static List<(MediaTime Time, Raster<Rgba8> Image)> Decode(string path)
    {
        using VideoReader reader = VideoReader.Open(path);
        List<(MediaTime, Raster<Rgba8>)> frames = [];
        foreach (VideoFrame<Rgba8> frame in reader.ReadFrames<Rgba8>())
        {
            frames.Add((frame.Timestamp, frame.Raster));
        }

        return frames;
    }

    /// <summary>The times of a file's video packets, in milliseconds' precision, in order.</summary>
    internal static List<double> VideoTimes(string path)
    {
        using IDemuxer demuxer = MediaFile.OpenDemuxer(path);
        int video = demuxer.Tracks.First(t => t.Kind == TrackKind.Video).Number;
        List<double> times = [];
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                if (packet.TrackNumber == video)
                {
                    times.Add(Math.Round(packet.Timestamp.Seconds, 3));
                }
            }
        }

        times.Sort();
        return times;
    }
}
