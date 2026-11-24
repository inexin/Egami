using System.Runtime.InteropServices;

using Egami.Cli.Host;
using Egami.Pixels;
using Egami.Svg;

namespace Egami.Cli.Tests;

/// <summary><c>--animate [fps]</c> and <c>--at &lt;time&gt;</c>: animated SVG inputs as frames or one moment.</summary>
public sealed class SvgAnimationTests : IDisposable
{
    // x is 0 for the first second of every two, 50 for the second.
    private const string Loop = "<svg xmlns='http://www.w3.org/2000/svg' width='100' height='20'><rect width='10' height='10' fill='blue'><animate attributeName='x' values='0;50' dur='2s' repeatCount='indefinite' calcMode='discrete'/></rect></svg>";

    private readonly Tool _tool = new();

    public SvgAnimationTests() => File.WriteAllText(_tool.PathOf("loop.svg"), Loop);

    public void Dispose() => _tool.Dispose();

    private static void Expect(int code, ToolRun run) => Assert.True(run.Code == code, $"exit {run.Code}: {run.Error}");

    [Theory]
    [InlineData("convert", "loop.svg", "-o", "out.gif", "--animate", "10")]
    [InlineData("convert", "loop.svg", "-o", "out.gif", "--animate")]
    [InlineData("convert", "--animate", "loop.svg", "-o", "out.gif")]
    [InlineData("convert", "loop.svg", "--animate=12", "-o", "out.gif")]
    [InlineData("run", "loop.svg", "--animate", "save", "out.gif")]
    [InlineData("run", "loop.svg", "--animate", "10", "save", "out.gif")]
    [InlineData("run", "--animate=10", "loop.svg", "save", "out.gif")]
    public void AnimateSavesEveryFrame(params string[] args)
    {
        Expect(0, _tool.Run(args));
        using Raster<Rgba8> gif = Raster.Load<Rgba8>(_tool.PathOf("out.gif"));
        Assert.Equal(2, gif.FrameCount);
        Assert.Equal(TimeSpan.FromSeconds(1), gif.Frames[1].Metadata.Delay);
        Assert.Equal(0, gif.Metadata.LoopCount);
    }

    [Fact]
    public void WithoutAnimateAnSvgIsOneFrame()
    {
        Expect(0, _tool.Run("convert", "loop.svg", "-o", "still.gif"));
        using Raster gif = Raster.Load(_tool.PathOf("still.gif"));
        Assert.Equal(1, gif.FrameCount);
    }

    [Theory]
    [InlineData("1.5s")]
    [InlineData("1500ms")]
    [InlineData("1.5")]
    public void AtDrawsOneMoment(string at)
    {
        Expect(0, _tool.Run("convert", "loop.svg", "--at", at, "-o", "moment.png"));
        using Raster<Rgba8> moment = Raster.Load<Rgba8>(_tool.PathOf("moment.png"));
        using Raster<Rgba8> drawn = SvgDocument.Parse(Loop).Rasterize<Rgba8>(new SvgRenderOptions { Time = TimeSpan.FromSeconds(1.5) });
        for (int y = 0; y < drawn.Height; y++)
        {
            Assert.True(MemoryMarshal.AsBytes(drawn.Row(y)).SequenceEqual(MemoryMarshal.AsBytes(moment.Row(y))));
        }

        Assert.Equal(255, moment.Row(5)[55].A);
    }

    [Theory]
    [InlineData("--animate=fast")]
    [InlineData("--animate", "0")]
    [InlineData("--animate", "1e9")]
    [InlineData("--at", "soon")]
    [InlineData("--at", "-1s")]
    public void BadValuesAreUsageErrors(params string[] options)
    {
        Expect(ExitCodes.Usage, _tool.Run(["convert", "loop.svg", .. options, "-o", "x.gif"]));
        Expect(ExitCodes.Usage, _tool.Run(["run", "loop.svg", .. options, "save", "x.gif"]));
    }

    [Fact]
    public void OtherInputsIgnoreTheOptions()
    {
        using (Raster<Rgba8> png = Raster.Create(4, 4, Rgba8.White))
        {
            png.Save(_tool.PathOf("white.png"));
        }

        Expect(0, _tool.Run("convert", "white.png", "--animate", "--at", "1s", "-o", "white.gif"));
        using Raster gif = Raster.Load(_tool.PathOf("white.gif"));
        Assert.Equal(1, gif.FrameCount);
    }

    [Fact]
    public void ParsedTimes()
    {
        Assert.Equal(TimeSpan.FromSeconds(0.25), SvgOptions.ParseTime("250ms", "--at"));
        Assert.Equal(TimeSpan.FromSeconds(2), SvgOptions.ParseTime(" 2S ", "--at"));
        Assert.Null(SvgOptions.Build(false, null, null));
        Assert.Equal(SvgOptions.DefaultRate, SvgOptions.Build(true, null, null)!.AnimationFrameRate);
    }
}
