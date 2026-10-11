using Egami.Operations;
using Egami.Pixels;

namespace Egami.DocSamples;

[Collection("Samples")]
public class Arithmetic
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Constant()
    {
        using SampleFiles files = new();
        using Raster photo = Raster.Load("duck.png");

        #region arithmetic
        // Lift the shadows with a log curve, then cap the highlights; values are on a 0 to 1 scale, as ImageMagick's -evaluate.
        photo.Apply(ops => ops
            .Arithmetic(PixelMath.Log, 3)
            .Arithmetic(PixelMath.Min, 0.95f), Ct);
        #endregion

        Assert.True(photo.Width > 0);
    }

    [Fact]
    public void Burst()
    {
        using SampleFiles files = new();
        using Raster first = Raster.Load("duck.png");
        using Raster second = first.Derive(ops => ops.Brightness(1.02f), Ct);
        using Raster third = first.Derive(ops => ops.Brightness(0.98f), Ct);

        #region stack
        // Average a burst of photos to smooth noise; the median removes things that appear in only a few frames.
        using Raster mean = Raster.Stack([first, second, third], StackMode.Mean);
        using Raster clean = Raster.Stack([first, second, third], StackMode.Median);
        #endregion

        Assert.Equal(first.Width, mean.Width);
    }
}
