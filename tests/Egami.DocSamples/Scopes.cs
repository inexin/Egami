using Egami.Operations;
using Egami.Pixels;

namespace Egami.DocSamples;

[Collection("Samples")]
public class Scopes
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Rectangle()
    {
        using SampleFiles files = new();
        using Raster photo = Raster.Load("duck.png");

        #region within-rect
        // Blur a face: only pixels inside the rectangle change, and the blur reads the real pixels around it, so there is no seam.
        photo.Apply(ops => ops
            .Within(new Rect(200, 120, 160, 120), o => o.Blur(6))
            .Within(new Rect(0, 0, photo.Width, 60), o => o.Brightness(0.8f).Saturation(0.5f)), Ct);   // a darker band on top
        #endregion

        Assert.True(photo.Width > 0);
    }

    [Fact]
    public void Masked()
    {
        using SampleFiles files = new();
        using Raster photo = Raster.Load("duck.png");
        using Raster<Gray8> sky = Raster.Create<Gray8>(photo.Width, photo.Height);
        for (int y = 0; y < sky.Height; y++)
        {
            sky.Row(y).Fill(new Gray8((byte)Math.Max(0, 255 - (y * 2))));   // white at the top, fading out
        }

        using Raster<Rgba8> logo = Raster.Create(64, 64, new Rgba8(0, 0, 0, 255));

        #region within-mask
        // Edit only the sky: a gray mask, white where the edit applies fully, black where it does not, soft in between.
        photo.Apply(ops => ops
            .Within(new Mask(sky), o => o.Saturation(1.3f).Contrast(1.1f))

            // Or take coverage from a logo's alpha, placed on the photo, and invert the area under it.
            .Within(new Mask(logo, new Point(20, 20), MaskChannel.Alpha), o => o.Invert()), Ct);
        #endregion

        Assert.True(photo.Width > 0);
    }
}
