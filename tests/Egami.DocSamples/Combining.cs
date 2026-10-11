using System.Text.Json;

using Egami.Pixels;

namespace Egami.DocSamples;

[Collection("Samples")]
public class Combining
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void AppendImages()
    {
        using SampleFiles files = new();
        using Raster duck = Raster.Load("duck.png");
        using Raster egret = Raster.Load("egret.png");

        #region append
        // Side by side with a 10-pixel white gap, the shorter image centered vertically (ImageMagick's +append).
        using Raster strip = Raster.Append([duck, egret], Axis.Horizontal, spacing: 10, background: Rgba8.White, alignment: Alignment.Center);

        // Stacked, as -append does.
        using Raster column = Raster.Append([duck, egret], Axis.Vertical);
        #endregion

        Assert.Equal(640 + 10 + 480, strip.Width);
        Assert.Equal(480 + 640, column.Height);
    }

    [Fact]
    public void ContactSheet()
    {
        using SampleFiles files = new();
        using Raster duck = Raster.Load("duck.png");
        using Raster egret = Raster.Load("egret.png");
        Raster[] photos = [duck, egret, duck, egret, duck];

        #region grid
        // A contact sheet three cells wide; each photo shrinks to fit a 200 × 200 cell and sits in its middle.
        using Raster sheet = Raster.Grid(photos, columns: 3, cell: new Size(200, 200), spacing: 8, background: Rgba8.White);
        #endregion

        Assert.Equal((3 * 200) + (2 * 8), sheet.Width);
    }

    [Fact]
    public void PackSprites()
    {
        using SampleFiles files = new();
        using Raster duck = Raster.Load("duck.png");
        using Raster small = duck.Derive(ops => ops.Resize(64, 48), Ct);
        using Raster tiny = duck.Derive(ops => ops.Resize(32, 24), Ct);

        #region sprite-sheet
        // Pack sprites with a 2-pixel gap and 1 pixel of extruded edge, so GPU filtering never bleeds between them.
        using SpriteSheet sheet = SpriteSheet.Pack([small, tiny, small, tiny], new SpritePackOptions { Padding = 2, Extrude = 1, PowerOfTwo = true });
        sheet.Image.Save("sprites.png");

        // The rectangles are plain data: writing them for a game engine is one line of System.Text.Json.
        File.WriteAllText("sprites.json", JsonSerializer.Serialize(sheet.Frames.Select(f => new { f.X, f.Y, w = f.Width, h = f.Height })));
        #endregion

        Assert.Equal(4, sheet.Frames.Count);
    }
}
