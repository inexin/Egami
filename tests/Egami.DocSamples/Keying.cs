using Egami.Operations;
using Egami.Pixels;

namespace Egami.DocSamples;

[Collection("Samples")]
public class Keying
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void GreenScreen()
    {
        using SampleFiles files = new();
        using Raster photo = Raster.Load("duck.png");

        #region green-screen
        // Remove a green screen: within 0.15 of pure green becomes transparent, and alpha ramps back over the next 0.1 for soft edges.
        photo.Apply(ops => ops.MakeTransparent(new Rgba8(0, 177, 64, 255), tolerance: 0.15f, softness: 0.1f), Ct);
        #endregion

        Assert.True(photo.Width > 0);
    }

    [Fact]
    public void Replace()
    {
        using SampleFiles files = new();
        using Raster photo = Raster.Load("duck.png");

        #region replace
        photo.Apply(ops => ops.ReplaceColor(Rgba8.White, new Rgba8(250, 245, 230, 255), tolerance: 0.05f), Ct);
        #endregion

        Assert.True(photo.Width > 0);
    }

    [Fact]
    public void Flood()
    {
        using SampleFiles files = new();
        using Raster photo = Raster.Load("duck.png");

        #region flood-fill
        // Fill the region around (10, 10) whose colors are close to its own, as a paint bucket does.
        photo.Apply(ops => ops.FloodFill(new Point(10, 10), Rgba8.White, tolerance: 0.1f), Ct);
        #endregion

        Assert.True(photo.Width > 0);
    }

    [Fact]
    public void Select()
    {
        using SampleFiles files = new();
        using Raster photo = Raster.Load("duck.png");

        #region select-by-color
        // Select by color, then edit only the selection: a magic wand from a point, and every pixel close to a color.
        using Raster<Gray8> wand = photo.FloodMask(new Point(30, 30), tolerance: 0.12f);
        using Raster<Gray8> reds = photo.ColorMask(new Rgba8(200, 40, 30, 255), tolerance: 0.2f);
        photo.Apply(ops => ops
            .Within(new Mask(wand), o => o.Blur(4))
            .Within(new Mask(reds), o => o.Saturation(1.4f)), Ct);
        #endregion

        Assert.True(photo.Width > 0);
    }
}
