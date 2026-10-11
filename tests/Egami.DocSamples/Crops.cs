using Egami.Operations;
using Egami.Pixels;

namespace Egami.DocSamples;

[Collection("Samples")]
public class Crops
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void TrimBorders()
    {
        using SampleFiles files = new();
        using Raster<Rgba8> photo = Raster.Load<Rgba8>("duck.png");
        using Raster<Rgba8> scan = photo.Derive(ops => ops.Pad(800, 600, Rgba8.White), Ct);

        #region trim
        // Cut away the white margin, counting near-white (within 10% by ImageMagick's -fuzz distance) as margin too.
        Rect content = scan.TrimBounds(tolerance: 0.1f);
        scan.Apply(ops => ops.Trim(tolerance: 0.1f).Fit(400, 400), Ct);
        #endregion

        Assert.Equal(new Rect(80, 60, 640, 480), content);
        Assert.Equal(400, scan.Width);
    }

    [Fact]
    public void CropToTheBusyPart()
    {
        using SampleFiles files = new();
        using Raster<Rgba8> photo = Raster.Load<Rgba8>("duck.png");
        using Raster<Rgba8> padded = photo.Derive(ops => ops.Pad(900, 700, new Rgba8(40, 40, 40, 255)), Ct);

        #region entropy-crop
        // Keep the part with detail in it: flat tiles (a plain backdrop) fall away.
        Rect busy = padded.EntropyBounds();
        using Raster<Rgba8> cropped = padded.Derive(ops => ops.EntropyCrop(), Ct);
        #endregion

        Assert.Equal((busy.Width, busy.Height), (cropped.Width, cropped.Height));
        Assert.True(cropped.Width < 900);
    }
}
