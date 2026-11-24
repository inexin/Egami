namespace Egami.Video.Cli.Tests;

/// <summary><c>encode</c> with an animated SVG: drawn frame by frame at the clip's rate, for --duration or one loop.</summary>
public sealed class SvgEncodeTests
{
    // x is 0 for the first second of every two, 50 for the second: a loop of 2 s.
    private const string Loop = "<svg xmlns='http://www.w3.org/2000/svg' width='100' height='20'><rect width='100' height='20' fill='white'/><rect width='10' height='10' fill='blue'><animate attributeName='x' values='0;50' dur='2s' repeatCount='indefinite' calcMode='discrete'/></rect></svg>";

    [Fact]
    public void OneLoopAtTheRate()
    {
        using Tool tool = new();
        File.WriteAllText(tool.PathOf("loop.svg"), Loop);
        ToolRun run = tool.Run("encode", "loop.svg", "-o", "clip.y4m", "--rate", "10");
        Assert.True(run.Code == 0, run.Error);
        List<string> hashes = EncodeTests.Hashes(tool.PathOf("clip.y4m"));
        Assert.Equal(20, hashes.Count);

        // Still frames are kept, for a constant rate: two pictures, ten frames each.
        Assert.Equal(2, hashes.Distinct().Count());
        Assert.Equal(10, hashes.TakeWhile(h => h == hashes[0]).Count());
        VideoTrackInfo track = EncodeTests.Video(tool.PathOf("clip.y4m"));
        Assert.Equal((100, 20), (track.Width, track.Height));
        Assert.Equal(new Rational(10, 1), track.FrameRate);
    }

    [Fact]
    public void DurationAndSize()
    {
        using Tool tool = new();
        File.WriteAllText(tool.PathOf("loop.svg"), Loop);
        ToolRun run = tool.Run("encode", "loop.svg", "-o", "clip.y4m", "--duration", "3", "--size", "200x40");
        Assert.True(run.Code == 0, run.Error);
        Assert.Equal(75, EncodeTests.Hashes(tool.PathOf("clip.y4m")).Count);
        VideoTrackInfo track = EncodeTests.Video(tool.PathOf("clip.y4m"));
        Assert.Equal((200, 40), (track.Width, track.Height));
        Assert.Equal(new Rational(25, 1), track.FrameRate);
    }

    [Fact]
    public void DurationIsForSvgOnly()
    {
        using Tool tool = new();
        File.WriteAllText(tool.PathOf("loop.svg"), Loop);
        using (Raster<Pixels.Rgba8> png = Raster.Create(8, 8, Pixels.Rgba8.White))
        {
            png.Save(tool.PathOf("a.png"));
        }

        Assert.Equal(2, tool.Run("encode", "a.png", "-o", "x.y4m", "--duration", "3").Code);
        Assert.Equal(2, tool.Run("encode", "loop.svg", "-o", "x.y4m", "--duration", "0").Code);
        File.WriteAllText(tool.PathOf("bad.svg"), "<svg xmlns='http://www.w3.org/2000/svg'><rect");
        Assert.NotEqual(0, tool.Run("encode", "bad.svg", "-o", "y.y4m").Code);
    }
}
