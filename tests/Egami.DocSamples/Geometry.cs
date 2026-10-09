using Egami.Formats;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.DocSamples;

[Collection("Samples")]
public class Geometry
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Resizing()
    {
        using SampleFiles files = new();
        using Raster photo = Raster.Load("egret.png");

        #region resize
        // Exactly 400 × 300, stretching if the aspect ratio differs.
        using Raster stretched = photo.Derive(ops => ops.Resize(400, 300), Ct);

        // 400 wide; 0 for one side keeps the aspect ratio.
        using Raster wide = photo.Derive(ops => ops.Resize(400, 0), Ct);

        // Fit inside 400 × 400 with the aspect ratio kept; enlarge: false never scales up.
        using Raster fitted = photo.Derive(ops => ops.Fit(400, 400, enlarge: false), Ct);

        // Cover 400 × 400 and crop what sticks out, keeping the top of the image.
        using Raster covered = photo.Derive(ops => ops.Cover(400, 400, Anchor.Top), Ct);
        #endregion

        Assert.Equal((400, 400), (covered.Width, covered.Height));
    }

    [Fact]
    public void FiltersAndLinearLight()
    {
        using SampleFiles files = new();
        using Raster photo = Raster.Load("egret.png");

        #region filters
        // Lanczos3 is sharper than the default Bicubic; Nearest keeps hard pixel edges (pixel art, masks).
        using Raster sharp = photo.Derive(ops => ops.Resize(200, 0, ResampleFilter.Lanczos3), Ct);

        // Blending in linear light keeps fine bright detail on dark backgrounds from going dim when reduced.
        using Raster linear = photo.Derive(ops => ops.Resize(200, 0, ResampleFilter.Mitchell, ColorSpace.Linear), Ct);
        #endregion

        Assert.Equal(200, linear.Width);
    }

    [Fact]
    public void CropPadRotate()
    {
        using SampleFiles files = new();
        using Raster<Rgba8> photo = Raster.Load<Rgba8>("duck.png");

        #region crop-pad-rotate
        photo.Apply(ops => ops
            .Crop(10, 10, 120, 90)                                  // keep a rectangle
            .Pad(140, 110, Rgba8.White, Anchor.Center)              // add a white border
            .Rotate(90)                                             // exact for multiples of 90°
            .Rotate(-3.5f, ResampleFilter.Bicubic, Rgba8.Transparent) // resampled for other angles
            .FlipHorizontal(), Ct);
        #endregion

        Assert.True(photo.Width > 110);
    }

    [Fact]
    public void Orientation()
    {
        using SampleFiles files = new();

        #region auto-orient
        // Load returns pixels as stored. A phone photo stored sideways says so in its EXIF orientation.
        RasterInfo info = Raster.Probe("sideways.jpg");
        Console.WriteLine(info.Orientation);   // Rotate90

        // AutoOrient turns the pixels upright and resets the tag. Followed by a resize, the resize runs first, on the stored image,
        // so only the small result is turned.
        using Raster photo = Raster.Load("sideways.jpg");
        photo.Apply(ops => ops.AutoOrient().Fit(300, 300), Ct);
        #endregion

        Assert.Equal(Egami.Metadata.ImageOrientation.Normal, photo.Metadata.Exif?.Orientation ?? Egami.Metadata.ImageOrientation.Normal);
    }

    [Fact]
    public void Planning()
    {
        using SampleFiles files = new();
        using Raster photo = Raster.Load("egret.png");

        #region planning
        // The whole chain is planned before any pixel changes: a bad argument throws first, a Crop before a resize reads only the
        // crop, and adjacent color steps run as one pass. Each step runs in parallel bands of rows.
        try
        {
            photo.Apply(ops => ops.Fit(100, 100).Crop(0, 0, 500, 500), Ct);
        }
        catch (ArgumentOutOfRangeException)
        {
            Console.WriteLine("The crop does not fit the 100-pixel result; the photo is unchanged.");
        }
        #endregion

        Assert.True(photo.Width > 100);
    }
}
