using Egami.Cli.Host;

namespace Egami.Video.Cli.Tests;

/// <summary>V9 stage A's commands: join, fade, overlay, pip, stack and edit (Build Plan task 11).</summary>
public sealed class EditCommandTests : IDisposable
{
    private readonly Tool _tool = new();

    public void Dispose() => _tool.Dispose();

    [Fact]
    public void JoinFitsClipsAndCrossfades()
    {
        _tool.Copy("avi-mjpeg-420-pcm.avi", "a.avi");
        _tool.Copy("webm-vp8-opus.webm", "b.webm");
        ToolRun run = _tool.Run("join", "a.avi", "b.webm", "--transition", "wipeleft:0.5", "--size", "160x120", "--rate", "10", "-o", "out.mkv");
        Assert.True(run.Code == ExitCodes.Success, run.Error);
        MediaInfo info = MediaFile.Probe(_tool.PathOf("out.mkv"));
        Assert.Equal(160, info.VideoTrack!.Width);
        Assert.Single(info.Tracks.OfType<AudioTrackInfo>());
        MediaInfo a = MediaFile.Probe(_tool.PathOf("a.avi")), b = MediaFile.Probe(_tool.PathOf("b.webm"));
        Assert.Equal(a.Duration!.Value.Seconds + b.Duration!.Value.Seconds - 0.5, info.Duration!.Value.Seconds, 0.15);
    }

    [Fact]
    public void SaveEditWritesAFileThatEditRenders()
    {
        _tool.Copy("avi-mjpeg-420-pcm.avi", "clips/a.avi");
        ToolRun save = _tool.Run("fade", "clips/a.avi", "--in", "0.2", "--out", "0.2", "--color", "white", "--save-edit", "clips/fade.json");
        Assert.True(save.Code == ExitCodes.Success, save.Error);
        Assert.False(File.Exists(_tool.PathOf("out.mkv")));
        string json = File.ReadAllText(_tool.PathOf("clips/fade.json"));
        Assert.Contains("\"fadeColor\": \"#FFFFFFFF\"", json, StringComparison.OrdinalIgnoreCase);

        // Paths in an edit file are taken from its own folder; effects are egami run steps.
        File.WriteAllText(_tool.PathOf("clips/fx.json"), "{\"track\":[{\"clip\":{\"open\":\"a.avi\",\"effects\":[{\"name\":\"grayscale\",\"start\":0.1,\"end\":0.3}]}}],\"effects\":[{\"name\":\"blur 2\"}]}");
        ToolRun run = _tool.Run("edit", "clips/fx.json", "--no-audio", "-o", "fx.mkv");
        Assert.True(run.Code == ExitCodes.Success, run.Error);
        Assert.DoesNotContain(MediaFile.Probe(_tool.PathOf("fx.mkv")).Tracks, t => t is AudioTrackInfo);
    }

    [Fact]
    public void OverlayPipAndStackRender()
    {
        _tool.Copy("avi-mjpeg-420-pcm.avi", "a.avi");
        _tool.Copy("mkv-mjpeg-pcm.mkv", "b.mkv");
        Assert.Equal(ExitCodes.Success, _tool.Run("overlay", "a.avi", "b.mkv", "--at", "10,20", "--scale", "0.5", "--key", "#00ff00", "-o", "o.mkv").Code);
        Assert.Equal(ExitCodes.Success, _tool.Run("pip", "a.avi", "b.mkv", "--corner", "top-right", "--from", "0.2", "--mute", "-o", "p.mkv").Code);
        ToolRun stack = _tool.Run("stack", "a.avi", "b.mkv", "a.avi", "--grid", "2x2", "--shortest", "-o", "s.mkv");
        Assert.True(stack.Code == ExitCodes.Success, stack.Error);
        VideoTrackInfo a = MediaFile.Probe(_tool.PathOf("a.avi")).VideoTrack!;
        Assert.Equal(a.Width * 2, MediaFile.Probe(_tool.PathOf("s.mkv")).VideoTrack!.Width);
    }

    [Fact]
    public void KeyAutoReadsTheScreenColour()
    {
        // A pale green screen with a gray square in front of it.
        Directory.CreateDirectory(_tool.PathOf("in"));
        for (int i = 0; i < 3; i++)
        {
            using Raster<Egami.Pixels.Rgb8> frame = Raster.Create<Egami.Pixels.Rgb8>(64, 48);
            for (int y = 0; y < 48; y++)
            {
                Span<Egami.Pixels.Rgb8> row = frame.Row(y);
                for (int x = 0; x < 64; x++)
                {
                    row[x] = x is >= 24 and < 40 && y is >= 16 and < 32 ? new Egami.Pixels.Rgb8(128, 128, 128) : new Egami.Pixels.Rgb8(0x34, 0xC9, 0x5F);
                }
            }

            frame.Save(_tool.PathOf($"in/f{i}.png"));
        }

        Assert.Equal(ExitCodes.Success, _tool.Run("encode", "in/*.png", "--codec", "ffv1", "-o", "screen.mkv").Code);
        _tool.Copy("mkv-mjpeg-pcm.mkv", "b.mkv");
        ToolRun run = _tool.Run("overlay", "b.mkv", "screen.mkv", "--key", "auto", "--save-edit", "keyed.json");
        Assert.True(run.Code == ExitCodes.Success, run.Error);
        string json = File.ReadAllText(_tool.PathOf("keyed.json"));
        Assert.Contains("\"color\": \"#34C95FFF\"", json, StringComparison.OrdinalIgnoreCase);

        // The soft edge ends before gray: the square keeps its alpha.
        double blend = double.Parse(System.Text.RegularExpressions.Regex.Match(json, "\"blend\": ([0-9.]+)").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(blend, 0.05, 0.3);

        Directory.CreateDirectory(_tool.PathOf("gray"));
        using (Raster<Egami.Pixels.Rgb8> gray = Raster.Create(64, 48, new Egami.Pixels.Rgb8(100, 110, 105)))
        {
            gray.Save(_tool.PathOf("gray/f0.png"));
        }

        Assert.Equal(ExitCodes.Success, _tool.Run("encode", "gray/*.png", "--codec", "ffv1", "-o", "gray.mkv").Code);
        ToolRun none = _tool.Run("overlay", "b.mkv", "gray.mkv", "--key", "auto", "-o", "x.mkv");
        Assert.Contains("no green or blue screen", none.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void SpeedReverseLoopAndFreeze()
    {
        _tool.Copy("avi-mjpeg-420-pcm.avi", "a.avi");
        double length = MediaFile.Probe(_tool.PathOf("a.avi")).Duration!.Value.Seconds;
        Assert.Equal(ExitCodes.Success, _tool.Run("speed", "a.avi", "2", "-o", "fast.mkv").Code);
        Assert.Equal(length / 2, MediaFile.Probe(_tool.PathOf("fast.mkv")).Duration!.Value.Seconds, 0.1);
        Assert.Equal(ExitCodes.Success, _tool.Run("speed", "a.avi", "0.5", "--pitch", "-o", "slow.mkv").Code);
        Assert.Equal(length * 2, MediaFile.Probe(_tool.PathOf("slow.mkv")).Duration!.Value.Seconds, 0.1);
        Assert.Equal(ExitCodes.Success, _tool.Run("reverse", "a.avi", "-o", "back.mkv").Code);
        Assert.Equal(ExitCodes.Success, _tool.Run("loop", "a.avi", "--count", "3", "-o", "three.mkv").Code);
        Assert.Equal(length * 3, MediaFile.Probe(_tool.PathOf("three.mkv")).Duration!.Value.Seconds, 0.1);
        Assert.Equal(ExitCodes.Success, _tool.Run("freeze", "a.avi", "--at", "0.2", "--for", "0.5", "-o", "held.mkv").Code);
        Assert.Equal(ExitCodes.Success, _tool.Run("freeze", "a.avi", "--at", "end", "--for", "1", "-o", "end.mkv").Code);
        Assert.Equal(length + 1, MediaFile.Probe(_tool.PathOf("end.mkv")).Duration!.Value.Seconds, 0.1);
        Assert.Equal(ExitCodes.Usage, _tool.Run("speed", "a.avi", "20", "-o", "x.mkv").Code);
    }

    [Fact]
    public void SlideshowOfImages()
    {
        _tool.CopyImage("ops/duck.png", "slides/1.png");
        _tool.CopyImage("ops/egret.png", "slides/2.png");
        ToolRun run = _tool.Run("slideshow", "slides/*.png", "--each", "1", "--transition", "dissolve:0.25", "--pan-zoom", "--size", "160x120", "--rate", "10", "-o", "show.mkv");
        Assert.True(run.Code == ExitCodes.Success, run.Error);
        Assert.Equal(1.75, MediaFile.Probe(_tool.PathOf("show.mkv")).Duration!.Value.Seconds, 0.1);
    }

    [Fact]
    public void StabilizeInTwoPasses()
    {
        _tool.Copy("avi-mjpeg-420-pcm.avi", "a.avi");
        ToolRun measure = _tool.Run("stabilize", "a.avi", "--save-motion", "m.json");
        Assert.True(measure.Code == ExitCodes.Success, measure.Error);
        Assert.False(File.Exists(_tool.PathOf("out.mkv")));
        Assert.Contains("\"frames\"", File.ReadAllText(_tool.PathOf("m.json")), StringComparison.Ordinal);
        ToolRun run = _tool.Run("stabilize", "a.avi", "--motion", "m.json", "--smoothing", "5", "--edges", "blur", "-o", "s.mkv");
        Assert.True(run.Code == ExitCodes.Success, run.Error);
        VideoTrackInfo a = MediaFile.Probe(_tool.PathOf("a.avi")).VideoTrack!;
        Assert.Equal(a.Width, MediaFile.Probe(_tool.PathOf("s.mkv")).VideoTrack!.Width);
        File.WriteAllText(_tool.PathOf("bad.json"), "{\"width\":1}");
        Assert.Equal(ExitCodes.Usage, _tool.Run("stabilize", "a.avi", "--motion", "bad.json", "-o", "x.mkv").Code);
    }

    [Fact]
    public void SmartRenderCopiesWhatItCan()
    {
        _tool.Copy("webm-vp8-opus.webm", "b.webm");
        ToolRun run = _tool.Run("join", "b.webm", "b.webm", "--transition", "fade:0.5", "--codec", "vp8", "--smart", "-o", "s.webm");
        Assert.True(run.Code == ExitCodes.Success, run.Error);
        MediaInfo b = MediaFile.Probe(_tool.PathOf("b.webm"));
        Assert.Equal((b.Duration!.Value.Seconds * 2) - 0.5, MediaFile.Probe(_tool.PathOf("s.webm")).Duration!.Value.Seconds, 0.15);
    }

    [Fact]
    public void RunStepsTakeTimeWindows()
    {
        _tool.Copy("avi-mjpeg-420-pcm.avi", "a.avi");
        ToolRun dry = _tool.Run("run", "a.avi", "grayscale", "@0.1-0.3", "+", "blur", "1", "@0.2-", "+", "save", "out.mkv", "--dry-run");
        Assert.True(dry.Code == ExitCodes.Success, dry.Error);
        Assert.Contains("@0.1-0.3", dry.Text, StringComparison.Ordinal);
        ToolRun run = _tool.Run("run", "a.avi", "grayscale", "@0.1-0.3", "+", "save", "out.mkv");
        Assert.True(run.Code == ExitCodes.Success, run.Error);
        Assert.Equal(ExitCodes.Usage, _tool.Run("run", "a.avi", "resize", "50%", "@0-1", "+", "save", "x.mkv").Code);
        Assert.Equal(ExitCodes.Usage, _tool.Run("run", "a.avi", "grayscale", "@0.3-0.1", "+", "save", "x.mkv").Code);
    }

    [Theory]
    [InlineData("join a.avi -o x.mkv", "two or more clips")]
    [InlineData("join a.avi a.avi --transition spin -o x.mkv", "is not a transition")]
    [InlineData("fade a.avi -o x.mkv", "needs --in, --out")]
    [InlineData("overlay a.avi a.avi --similarity 0.2 -o x.mkv", "go with --key")]
    [InlineData("overlay a.avi a.avi --at middle -o x.mkv", "is not x,y")]
    [InlineData("pip a.avi a.avi --corner centre -o x.mkv", "is not top-left")]
    [InlineData("join a.avi a.avi -o x.wav", "cannot hold both")]
    [InlineData("join a.avi a.avi", "No output")]
    [InlineData("stabilize a.avi --edges spin -o x.mkv", "is not zoom, black or blur")]
    [InlineData("stabilize a.avi --edges black --zoom none -o x.mkv", "not both")]
    public void UsageErrorsSayWhatIsWrong(string line, string message)
    {
        _tool.Copy("avi-mjpeg-420-pcm.avi", "a.avi");
        ToolRun run = _tool.Run(line.Split(' '));
        Assert.Equal(ExitCodes.Usage, run.Code);
        Assert.Contains(message, run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void EditFileProblemsAreUsageErrors()
    {
        File.WriteAllText(_tool.PathOf("bad.json"), "{\"track\":[{\"clip\":{\"open\":\"a.avi\"},\"transition\":{\"kind\":\"spin\",\"duration\":1}}]}");
        ToolRun run = _tool.Run("edit", "bad.json", "-o", "x.mkv");
        Assert.Equal(ExitCodes.Usage, run.Code);
        Assert.Contains("track[0].transition.kind", run.Error, StringComparison.Ordinal);
    }
}
