using Egami.Operations;
using Egami.Pixels;

namespace Egami.DocSamples;

[Collection("Samples")]
public class EdgesAndMorphology
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void FindEdges()
    {
        using SampleFiles files = new();
        using Raster photo = Raster.Load("egret.png");

        #region edges
        using Raster edges = photo.Derive(o => o.Edges(), Ct);                                  // Sobel, gray, 0 to 1
        using Raster crisp = photo.Derive(o => o.Edges(EdgeOperator.Scharr), Ct);              // rotation-steady
        using Raster colored = photo.Derive(o => o.Edges(EdgeOperator.Sobel, grayscale: false), Ct);  // per channel
        using Raster lines = photo.Derive(o => o.Blur(1).Edges(EdgeOperator.Laplacian3x3), Ct); // second derivative
        #endregion

        Assert.Equal(photo.Width, edges.Width);
    }
}
