using System.Numerics;
using System.Text.RegularExpressions;

using Egami.Cli.Host;
using Egami.Drawing;
using Egami.Pixels;

namespace Egami.Cli.Tests;

/// <summary><c>egami trace</c>: presets, options, outputs and the PNG preview.</summary>
public sealed partial class TraceCommandTests : IDisposable
{
    private readonly Tool _tool = new();

    public void Dispose() => _tool.Dispose();

    [Fact]
    public void TracesToSvgOnStandardOutputAndToFiles()
    {
        Scene("logo.png");
        ToolRun stdout = _tool.Run("trace", "logo.png", "--preset", "logo");
        Assert.True(stdout.Code == 0, stdout.Error);
        string svg = stdout.Bytes.Utf8();
        Assert.StartsWith("<?xml", svg, StringComparison.Ordinal);
        Assert.Equal(4, PathElement().Count(svg));     // white, red, blue and black

        Expect(0, _tool.Run("trace", "logo.png", "-p", "logo", "--ignore-white", "-o", "out/logo.svg", "--preview-png", "out/logo-preview.png"));
        Assert.Equal(3, PathElement().Count(File.ReadAllText(_tool.PathOf("out/logo.svg"))));

        // The preview is the trace drawn at the image's size, close to the image itself.
        using Raster source = Raster.Load(_tool.PathOf("logo.png"));
        using Raster preview = Raster.Load(_tool.PathOf("out/logo-preview.png"));
        Assert.Equal((source.Width, source.Height), (preview.Width, preview.Height));
        using Raster<Rgba8> flat = Raster.Create(preview.Width, preview.Height, Rgba8.White);
        flat.Apply(ops => ops.DrawImage(preview, default(Point)), TestContext.Current.CancellationToken);
        Assert.True(Raster.Compare(source, flat).Overall.Mae < 0.01);
    }

    [Fact]
    public void OptionsChangeThePreset()
    {
        Scene("logo.png");
        string Svg(params string[] args)
        {
            ToolRun run = _tool.Run(["trace", "logo.png", .. args]);
            Assert.True(run.Code == 0, run.Error);
            return run.Bytes.Utf8();
        }

        // The default preset is black and white: a white background, then the ink.
        string ink = Svg();
        Assert.Equal(3, PathElement().Count(ink));     // paper, then the shapes and the line
        Assert.Contains("fill=\"#ffffff\"", ink, StringComparison.Ordinal);
        Assert.Equal(2, PathElement().Count(Svg("--preset", "black-and-white")));

        // A fixed palette, abutting layers and no arcs.
        string fixedColors = Svg("--mode", "color", "--palette", "rgb(220 30 30), #1e3cc8, black, white", "--layering", "abutting", "--snap-arcs", "false", "--ignore-white");
        Assert.Equal(3, PathElement().Count(fixedColors));
        Assert.DoesNotContain(" A", fixedColors, StringComparison.Ordinal);

        // Centerline strokes with one pen, and fewer decimals.
        string strokes = Svg("-p", "line-art", "--stroke-width", "2.5", "--precision", "1");
        Assert.Contains("stroke-width=\"2.5\"", strokes, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"\d\.\d\d", strokes);
    }

    [Fact]
    public void ManyInputsNeedAFolderOrATemplate()
    {
        Scene("a.png");
        Scene("b.png");
        Expect(ExitCodes.Usage, _tool.Run("trace", "a.png", "b.png"));
        Expect(ExitCodes.Usage, _tool.Run("trace", "a.png", "b.png", "-o", "one.svg"));
        Expect(0, _tool.Run("trace", "a.png", "b.png", "-o", "svg/", "--preview-png", "png/{name}-trace.png"));
        Assert.True(File.Exists(_tool.PathOf("svg/a.svg")) && File.Exists(_tool.PathOf("svg/b.svg")));
        Assert.True(File.Exists(_tool.PathOf("png/a-trace.png")) && File.Exists(_tool.PathOf("png/b-trace.png")));
    }

    [Fact]
    public void BadValuesAreUsageErrors()
    {
        Scene("logo.png");
        foreach (string[] args in (string[][])[
            ["--preset", "comic"],
            ["--mode", "pixel"],
            ["--colors", "1"],
            ["--corners", "2"],
            ["--noise", "-1"],
            ["--stroke-width", "0"],
            ["--palette", "notacolor"],
            ["--layering", "sideways"],
            ["--precision", "-3"],
            ["-o", "-", "--preview-png", "-"]])
        {
            ToolRun run = _tool.Run(["trace", "logo.png", .. args]);
            Assert.True(run.Code == ExitCodes.Usage, $"{string.Join(' ', args)}: exit {run.Code}");
        }

        ToolRun colors = _tool.Run("trace", "logo.png", "--colors", "1");
        Assert.Contains("--colors", colors.Error, StringComparison.Ordinal);
    }

    private void Scene(string name)
    {
        using Raster<Rgba8> raster = Raster.Create(120, 90, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
        {
            canvas.FillCircle(new Vector2(40, 45), 30, new Rgba8(220, 30, 30, 255));
            canvas.FillRect(new RectF(60, 20, 45, 50), new Rgba8(30, 60, 200, 255));
            canvas.StrokePath(new PathBuilder().MoveTo(10, 85).LineTo(110, 85).ToPath(), new Stroke(3), Rgba8.Black);
        }

        raster.Save(_tool.PathOf(name));
    }

    private static void Expect(int code, ToolRun run) => Assert.True(run.Code == code, $"exit {run.Code}, expected {code}: {run.Error}");

    [GeneratedRegex("<path ")]
    private static partial Regex PathElement();
}
