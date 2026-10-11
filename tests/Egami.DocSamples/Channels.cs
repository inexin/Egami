using Egami.Operations;
using Egami.Pixels;

namespace Egami.DocSamples;

[Collection("Samples")]
public class Channels
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void SplitAndJoin()
    {
        using SampleFiles files = new();
        using Raster photo = Raster.Load("duck.png");

        #region extract-combine
        // Pull channels out as gray rasters (Gray8 from 8-bit images), edit one, and build the image again.
        using Raster red = photo.ExtractChannel(ColorChannel.Red);
        using Raster green = photo.ExtractChannel(ColorChannel.Green);
        using Raster blue = photo.ExtractChannel(ColorChannel.Blue);
        blue.Apply(ops => ops.Blur(2), Ct);
        using Raster<Rgba8> rebuilt = Raster.CombineChannels<Rgba8>(red, green, blue);
        #endregion

        Assert.Equal(photo.Width, rebuilt.Width);
    }

    [Fact]
    public void Shuffle()
    {
        using SampleFiles files = new();
        using Raster photo = Raster.Load("duck.png");

        #region shuffle
        // Swap red and blue: one color matrix, fused with the other color steps into one pass.
        photo.Apply(ops => ops
            .ShuffleChannels(ChannelSource.Blue, ChannelSource.Green, ChannelSource.Red, ChannelSource.Alpha)
            .Brightness(1.05f), Ct);
        #endregion

        Assert.True(photo.Width > 0);
    }

    [Fact]
    public void OnSomeChannels()
    {
        using SampleFiles files = new();
        using Raster photo = Raster.Load("duck.png");

        #region on-channels
        // Soften only the alpha; invert only the red.
        photo.Apply(ops => ops
            .OnChannels(ColorChannels.Alpha, o => o.Blur(2))
            .OnChannels(ColorChannels.Red, o => o.Invert()), Ct);
        #endregion

        Assert.True(photo.Width > 0);
    }

    [Fact]
    public void Alpha()
    {
        using SampleFiles files = new();
        using Raster photo = Raster.Load("duck.png");
        using Raster<Gray8> matte = Raster.Create(photo.Width, photo.Height, new Gray8(200));

        #region alpha
        using Raster alpha = photo.Derive(ops => ops.AlphaToGray(), Ct);                     // see the transparency as gray
        using Raster cutout = photo.Derive(ops => ops.CopyAlpha(new Mask(matte)), Ct);       // a gray image becomes the alpha
        using Raster onWhite = cutout.Derive(ops => ops.Flatten(Rgba8.White), Ct);           // composite onto white, opaque
        using Raster luma = photo.Derive(ops => ops.LuminanceToAlpha(), Ct);                 // brightness becomes alpha
        #endregion

        Assert.Equal(photo.Width, onWhite.Width);
    }
}
