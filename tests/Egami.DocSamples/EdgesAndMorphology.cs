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

    [Fact]
    public void SelectionFromCanny()
    {
        using SampleFiles files = new();
        using Raster photo = Raster.Load("duck.png");

        #region canny
        // Thin white edges on black. A gray raster works as a Mask as it is; an RGBA one needs MaskChannel.Luminance,
        // because a Mask of a raster with alpha reads its alpha.
        using Raster<Gray8> edges = Raster.Load<Gray8>("duck.png");
        edges.Apply(o => o.CannyEdges(sigma: 1.4f).Dilate(1), Ct);                 // thin edges, thickened

        photo.Apply(o => o.Within(new Mask(edges), s => s.FillColor(Rgba8.Red)), Ct);  // paint over them
        #endregion

        Assert.Equal(photo.Width, edges.Width);
    }

    [Fact]
    public void CleanAScan()
    {
        using SampleFiles files = new();

        #region morphology
        // Close removes dark specks smaller than the shape (dust and toner dots on the paper) and keeps strokes wider than it;
        // Open does the opposite, for light pinholes in dark areas.
        using Raster<Gray8> scan = Raster.Load<Gray8>("scan.tif");
        scan.Apply(o => o.Morphology(MorphologyOp.Close, StructuringElement.Disk(1)), Ct);

        // The outlines of shapes, and the light details smaller than a disk.
        using Raster outlines = scan.Derive(o => o.Morphology(MorphologyOp.Gradient, StructuringElement.Square(1)), Ct);
        using Raster details = scan.Derive(o => o.Morphology(MorphologyOp.TopHat, StructuringElement.Disk(5)), Ct);
        #endregion

        Assert.Equal(scan.Width, outlines.Width);
    }

    [Fact]
    public void DilateAndErode()
    {
        using SampleFiles files = new();
        using Raster photo = Raster.Load("duck.png");

        #region dilate-erode
        using Raster grown = photo.Derive(o => o.Dilate(3), Ct);       // light areas grow 3 px: a 7 × 7 square
        using Raster shrunk = photo.Derive(o => o.Erode(3), Ct);       // light areas shrink 3 px
        using Raster wide = photo.Derive(o => o.Dilate(6, 1), Ct);     // 13 × 3, as Skia's dilate radii
        #endregion

        Assert.Equal(photo.Height, wide.Height);
    }

    [Fact]
    public void Shapes()
    {
        using SampleFiles files = new();
        using Raster photo = Raster.Load("duck.png");

        #region shapes
        StructuringElement disk = StructuringElement.Disk(4);          // x² + y² ≤ 16, ImageMagick's Disk:4
        StructuringElement diamond = StructuringElement.Diamond(3);    // |x| + |y| ≤ 3
        StructuringElement plus = StructuringElement.Cross(2);         // ImageMagick's Plus:2
        StructuringElement corner = StructuringElement.FromMask(
            new[,]
            {
                { true, true, true },
                { true, false, false },
                { true, false, false },
            },
            new Point(0, 0));                                          // [y, x] cells; the origin sits on the pixel

        using Raster rounded = photo.Derive(o => o.Morphology(MorphologyOp.Open, disk, iterations: 2), Ct);
        #endregion

        Assert.Equal(3, corner.Width);
        Assert.Equal(9, disk.Height);
        Assert.Equal(7, diamond.Width);
        Assert.Equal(5, plus.Height);
    }
}
