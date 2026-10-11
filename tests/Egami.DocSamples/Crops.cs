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

    [Fact]
    public void ManySizesFromOneMipChain()
    {
        using SampleFiles files = new();
        using Raster photo = Raster.Load("egret.png");

        #region mip-chain
        // A responsive image set: one chain, then each width from the smallest level at least twice its size.
        using MipChain chain = MipChain.Build(photo);
        foreach (int width in (int[])[320, 240, 160, 120, 80])
        {
            using Raster thumbnail = chain.Fit(width, width, ResampleFilter.Lanczos2);
            thumbnail.Save($"egret-{width}.png");
        }
        #endregion

        Assert.True(File.Exists("egret-80.png"));
    }

    [Fact]
    public void StraightenAPage()
    {
        using SampleFiles files = new();
        using Raster photo = Raster.Load("duck.png");

        #region deskew
        // Measure the tilt first, if you want to show it or decide yourself...
        float tilt = photo.DetectSkew(maxAngle: 10);

        // ...or straighten in one step, keeping the page size with white corners.
        using Raster straight = photo.Derive(ops => ops.Deskew(background: Rgba8.White), Ct);
        #endregion

        Assert.Equal(0, tilt);
        Assert.Equal(photo.Width, straight.Width);
    }
}
