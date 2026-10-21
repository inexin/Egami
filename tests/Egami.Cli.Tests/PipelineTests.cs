using Egami.Cli.Commands;
using Egami.Cli.Host;
using Egami.Cli.Pipeline;
using Egami.Formats;
using Egami.Formats.Png;
using Egami.Formats.Webp;
using Egami.Operations;

namespace Egami.Cli.Tests;

/// <summary>Stage B: run, workflow files, batches and external commands.</summary>
public sealed class PipelineTests : IDisposable
{
    private const string Png = "pngsuite/basn6a08.png";

    private readonly Tool _tool = new();

    public void Dispose() => _tool.Dispose();

    [Fact]
    public void RunIsTheSameChainAsInCSharp()
    {
        string png = _tool.Copy(Png);
        ToolRun run = _tool.Run("run", png, "resize", "24x", "+", "sharpen", "1.5", "--sigma", "0.8", "+", "sepia", "+", "save", "out.png");
        Assert.Equal(0, run.Code);
        Assert.Equal(Chain(png, ops => ops.Resize(24, 0).Sharpen(1.5f, 0.8f).Sepia()), File.ReadAllBytes(_tool.PathOf("out.png")));
    }

    [Fact]
    public void RunEqualsTheSingleCommand()
    {
        string png = _tool.Copy(Png);
        Assert.Equal(0, _tool.Run("resize", png, "--size", "20x20", "--fit", "cover", "-o", "single.png").Code);
        Assert.Equal(0, _tool.Run("run", png, "resize", "20x20", "--fit", "cover", "-o", "run.png").Code);
        Assert.Equal(File.ReadAllBytes(_tool.PathOf("single.png")), File.ReadAllBytes(_tool.PathOf("run.png")));
    }

    [Fact]
    public void SaveCanComeMoreThanOnce()
    {
        string png = _tool.Copy(Png);
        ToolRun run = _tool.Run("run", png, "save", "big.png", "+", "resize", "8x8!", "+", "save", "small.webp", "--lossless");
        Assert.Equal(0, run.Code);
        Assert.Equal(32, Raster.Probe(_tool.PathOf("big.png")).Width);
        RasterInfo small = Raster.Probe(_tool.PathOf("small.webp"));
        Assert.Equal((8, 8), (small.Width, small.Height));
        Assert.Equal(WebpFormat.Info, small.Format);
    }

    [Fact]
    public void WriterOptionsOutsideSaveApplyToTheSaves()
    {
        string png = _tool.Copy(Png);
        Assert.Equal(0, _tool.Run("run", png, "invert", "-o", "a.png", "--set", "png.compression-level=0").Code);
        Assert.Equal(Chain(png, ops => ops.Invert(), new PngWriterOptions { CompressionLevel = 0 }), File.ReadAllBytes(_tool.PathOf("a.png")));
    }

    [Fact]
    public void WorkflowsRoundTrip()
    {
        string png = _tool.Copy(Png);
        ToolRun saved = _tool.Run("run", png, "blur", "1.2", "--color-space", "linear", "+", "flip", "vertical", "+", "save", "--format", "png", "--set", "png.filter=paeth", "--save-workflow", "w.json", "-o", "direct.png");
        Assert.Equal(0, saved.Code);
        string json = File.ReadAllText(_tool.PathOf("w.json"));
        Assert.Contains("\"op\": \"blur\"", json, StringComparison.Ordinal);
        Assert.Contains("\"sigma\": 1.2", json, StringComparison.Ordinal);
        Assert.Contains("\"png.filter\": \"paeth\"", json, StringComparison.Ordinal);

        Assert.Equal(0, _tool.Run("run", png, "--workflow", "w.json", "-o", "again.png").Code);
        Assert.Equal(File.ReadAllBytes(_tool.PathOf("direct.png")), File.ReadAllBytes(_tool.PathOf("again.png")));
    }

    [Fact]
    public void WorkflowFilesFromTheDesignLoad()
    {
        List<PipelineItem> items = Workflow.Parse(
            """
            {
              "egami": 1,
              "steps": [
                { "op": "autoorient" },
                { "op": "resize", "size": "1200x", "filter": "lanczos3" },
                { "op": "sharpen", "sigma": 0.6 },
                { "op": "save", "format": "webp", "quality": 82 }
              ]
            }
            """u8.ToArray(),
            "web.json");
        Assert.Equal(4, items.Count);
        SaveItem save = Assert.IsType<SaveItem>(items[3]);
        Assert.Equal("webp", save.Save.Format);
        Assert.Equal("82", save.Save.Common["quality"]);
    }

    [Theory]
    [InlineData("[]", "must be a JSON object")]
    [InlineData("{ \"egami\": 2, \"steps\": [] }", "version 2")]
    [InlineData("{ \"egami\": 1, \"steps\": [ { \"op\": \"melt\" } ] }", "step 1: 'melt' is not a step")]
    [InlineData("{ \"egami\": 1, \"steps\": [ { \"op\": \"blur\", \"radius\": 3 } ] }", "step 1: blur")]
    [InlineData("{ \"egami\": 1, \"steps\": [ { \"op\": \"blur\", \"sigma\": {} } ] }", "must be a string, number")]
    [InlineData("{ \"egami\": 1, \"stepz\": [] }", "unknown property \"stepz\"")]
    [InlineData("{ \"egami\": 1, \"steps\": [ ] }", "\"steps\" is empty")]
    [InlineData("{ nope", "not valid JSON")]
    public void BadWorkflowsSayWhatIsWrong(string json, string message)
    {
        UsageException error = Assert.Throws<UsageException>(() => Workflow.Parse(System.Text.Encoding.UTF8.GetBytes(json), "w.json"));
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DryRunPrintsStepsAndOutputs()
    {
        _tool.Copy(Png, "a/one.png");
        _tool.Copy(Png, "b/c/two.png");
        ToolRun run = _tool.Run("run", "**/*.png", "resize", "50%", "+", "grayscale", "-o", "out/{dir}/{name}.webp", "--dry-run");
        Assert.Equal(0, run.Code);
        Assert.Equal(
            "resize --size 50%\ngrayscale\nsave\na/one.png -> out/a/one.webp\nb/c/two.png -> out/b/c/two.webp\n",
            run.Text.Lines());
        Assert.False(Directory.Exists(_tool.PathOf("out")));
    }

    [Fact]
    public void BatchesGiveTheSameBytesAtAnyJobCount()
    {
        for (int i = 0; i < 6; i++)
        {
            _tool.Copy(Png, $"in/{i}.png");
        }

        Assert.Equal(0, _tool.Run("run", "in/*.png", "blur", "1", "+", "resize", "50%", "-o", "one/{name}.png", "--jobs", "1").Code);
        Assert.Equal(0, _tool.Run("run", "in/*.png", "blur", "1", "+", "resize", "50%", "-o", "four/", "--jobs", "4").Code);
        for (int i = 0; i < 6; i++)
        {
            Assert.Equal(File.ReadAllBytes(_tool.PathOf($"one/{i}.png")), File.ReadAllBytes(_tool.PathOf($"four/{i}.png")));
        }
    }

    [Fact]
    public void BatchesCarryOnAfterAFailureUnlessFailFast()
    {
        _tool.Copy(Png, "in/a.png");
        File.WriteAllText(_tool.PathOf("in/b.png"), "broken");
        _tool.Copy(Png, "in/c.png");
        ToolRun run = _tool.Run("convert", "in/*.png", "-o", "out/{name}.qoi", "--jobs", "1");
        Assert.Equal(ExitCodes.Failed, run.Code);
        Assert.True(File.Exists(_tool.PathOf("out/c.qoi")));
        Assert.Contains("in/b.png", run.Error, StringComparison.Ordinal);
        Assert.Contains("3 files: 2 done, 1 failed", run.Error, StringComparison.Ordinal);

        ToolRun fast = _tool.Run("convert", "in/*.png", "-o", "fast/{name}.qoi", "--jobs", "1", "--fail-fast");
        Assert.Equal(ExitCodes.Failed, fast.Code);
        Assert.False(File.Exists(_tool.PathOf("fast/c.qoi")));
    }

    [Fact]
    public void ManyInputsNeedAPerInputOutput()
    {
        _tool.Copy(Png, "a.png");
        _tool.Copy(Png, "b.png");
        Assert.Equal(ExitCodes.Usage, _tool.Run("convert", "*.png", "-o", "one.qoi").Code);
        Assert.Equal(ExitCodes.Usage, _tool.Run("convert", "a.png", "b.png", "-o", "-").Code);
        Assert.Equal(0, _tool.Run("convert", "*.png", "-o", "{name}-{index:00}.qoi").Code);
        Assert.True(File.Exists(_tool.PathOf("a-00.qoi")));
        Assert.True(File.Exists(_tool.PathOf("b-01.qoi")));
    }

    [Fact]
    public void FoldersNeedRecursive()
    {
        _tool.Copy(Png, "pics/x/a.png");
        File.WriteAllText(_tool.PathOf("pics/notes.txt"), "not an image");
        Assert.Equal(ExitCodes.Usage, _tool.Run("info", "pics").Code);
        ToolRun run = _tool.Run("info", "pics", "--recursive");
        Assert.Equal(0, run.Code);
        Assert.Contains("pics/x/a.png", run.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("notes", run.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownCommandsRunEgamiPrograms()
    {
        string bin = _tool.PathOf("bin");
        Directory.CreateDirectory(bin);
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(Path.Combine(bin, "egami-test.cmd"), "@echo external %1 %2\r\n@exit /b 7\r\n");
        }
        else
        {
            string script = Path.Combine(bin, "egami-test");
            File.WriteAllText(script, "#!/bin/sh\necho external \"$1\" \"$2\"\nexit 7\n");
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        _tool.Environment["PATH"] = bin;
        ToolRun run = _tool.Run("test", "one", "two");
        Assert.Equal(7, run.Code);
        Assert.Contains("external one two", run.Text, StringComparison.Ordinal);

        ToolRun missing = _tool.Run("nothing", "here");
        Assert.Equal(ExitCodes.Usage, missing.Code);
        Assert.Contains("no egami-nothing program", missing.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void RunArgumentMistakesAreUsageErrors()
    {
        string png = _tool.Copy(Png);
        Assert.Contains("is not a step", _tool.Run("run", png, "blur", "1", "+", "melt", "-o", "x.png").Error, StringComparison.Ordinal);
        Assert.Contains("A + must come between", _tool.Run("run", png, "+").Error, StringComparison.Ordinal);
        Assert.Contains("end with a +", _tool.Run("run", png, "blur", "1", "+").Error, StringComparison.Ordinal);
        Assert.Contains("blur has no option --radius", _tool.Run("run", png, "blur", "1", "--radius", "2").Error, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Usage, _tool.Run("run", png).Code);
    }

    private byte[] Chain(string input, Action<RasterOps> ops, WriterOptions? options = null)
    {
        using Raster image = Raster.Load(_tool.PathOf(input));
        image.Apply(ops);
        using MemoryStream output = new();
        image.Save(output, PngFormat.Info, options);
        return output.ToArray();
    }
}
