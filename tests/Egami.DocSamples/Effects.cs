using Egami.Operations;
using Egami.Pixels;

namespace Egami.DocSamples;

[Collection("Samples")]
public class Effects
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Color()
    {
        using SampleFiles files = new();
        using Raster photo = Raster.Load("duck.png");

        #region color
        photo.Apply(ops => ops
            .Brightness(1.05f)     // 1 changes nothing
            .Contrast(1.1f)
            .Saturation(1.2f)
            .HueRotate(10), Ct);    // these four run as one pass

        using Raster bw = photo.Derive(ops => ops.Grayscale(LumaWeights.Rec709), Ct);
        using Raster old = photo.Derive(ops => ops.Sepia(0.8f).Opacity(0.9f), Ct);
        #endregion

        Assert.Equal(photo.Width, bw.Width);
    }

    [Fact]
    public void Matrix()
    {
        using SampleFiles files = new();
        using Raster photo = Raster.Load("duck.png");

        #region color-matrix
        // A ColorMatrix is 4 rows of 5 values: each output channel is a weighted sum of R, G, B, A plus an offset.
        // Combine the ready-made ones with Then; no clamping happens in between.
        ColorMatrix warm = ColorMatrix.Saturation(1.1f).Then(new ColorMatrix(
        [
            1.05f, 0, 0, 0, 0,
            0, 1, 0, 0, 0,
            0, 0, 0.92f, 0, 0,
            0, 0, 0, 1, 0,
        ]));
        photo.Apply(ops => ops.ColorMatrix(warm), Ct);
        #endregion

        Assert.Equal(1.05f * 1.1f, warm[0, 0], 0.2f);
    }

    [Fact]
    public void Filters()
    {
        using SampleFiles files = new();
        using Raster photo = Raster.Load("egret.png");

        #region filters
        using Raster soft = photo.Derive(ops => ops.Blur(3), Ct);           // Gaussian, sigma in pixels
        using Raster boxed = photo.Derive(ops => ops.BoxBlur(4), Ct);       // fast, radius in pixels
        using Raster crisp = photo.Derive(ops => ops.Sharpen(0.8f, 1.5f), Ct); // unsharp mask: amount, sigma

        // Any kernel: here a 3 × 3 emboss, with edges clamped and alpha left alone.
        ConvolutionKernel emboss = new(3, 3, [-2, -1, 0, -1, 1, 1, 0, 1, 2]);
        using Raster embossed = photo.Derive(ops => ops.Convolve(emboss, EdgeMode.Clamp, preserveAlpha: true), Ct);
        #endregion

        Assert.Equal(photo.Width, embossed.Width);
    }

    [Fact]
    public void Compositing()
    {
        using SampleFiles files = new();
        using Raster<Rgba8> photo = Raster.Load<Rgba8>("egret.png");
        using Raster<Rgba8> logo = Raster.Load<Rgba8>("duck.png");
        logo.Apply(ops => ops.Fit(64, 64).Opacity(0.8f), Ct);

        #region draw-image
        // Source-over blending, clipped to the overlap; the logo may be any pixel type.
        photo.Apply(ops => ops.DrawImage(logo, new Point(photo.Width - logo.Width - 10, 10), opacity: 0.9f), Ct);
        #endregion

        #region draw-image-area
        // Draw part of an image, such as one tile of a sprite sheet: no copy is made.
        Rect tile = new(0, 0, 32, 32);
        photo.Apply(ops => ops.DrawImage(logo, tile, new Point(10, 10)), Ct);
        #endregion

        Assert.True(photo.Width > 64);
    }
}
