using Egami.Formats.Tiff;
using Egami.Pixels;

namespace Egami.DocSamples;

[Collection("Samples")]
public class Tiff
{
    [Fact]
    public void Load()
    {
        using SampleFiles files = new();

        #region tiff-load
        // Load reads the first full-resolution page in the pixel type that fits it: Rgb16 for 16-bit color, Cmyk8 for CMYK.
        using Raster page = Raster.Load("scan.tif");
        Console.WriteLine($"{page.Width} × {page.Height}, {page.Layout.ChannelOrder} {page.Layout.ChannelType}");

        // Probe says how many pages LoadAll would return, without decoding any.
        Console.WriteLine($"{Raster.Probe("scan.tif").ImageCount} pages");
        #endregion

        Assert.Equal(64, page.Width);
    }

    [Fact]
    public void Page()
    {
        using SampleFiles files = new();

        #region tiff-page
        // Pick another page by its index among the full-resolution pages.
        using Raster<Rgba8> third = Raster.Load<Rgba8>("scan.tif", options: new TiffReaderOptions { Page = 2 });
        #endregion

        Assert.Equal(160, third.Width);
    }

    [Fact]
    public void Pages()
    {
        using SampleFiles files = new();

        #region tiff-pages
        // Every page at its own size and pixel type. The list owns the rasters.
        using RasterList pages = Raster.LoadAll("scan.tif");
        foreach (Raster page in pages)
        {
            Console.WriteLine($"{page.Width} × {page.Height}");
        }

        // Keep one page after the list is gone.
        using Raster first = pages.Detach(0);
        #endregion

        Assert.Equal(3, pages.Count);
    }

    [Fact]
    public void SaveAll()
    {
        using SampleFiles files = new();

        #region tiff-save-all
        using RasterList pages = Raster.LoadAll("scan.tif");

        // One raster saves to any format; several need a format that holds several.
        Raster.SaveAll([pages[1]], "second.png");
        #endregion

        Assert.True(File.Exists("second.png"));
    }

    [Fact]
    public void Details()
    {
        using SampleFiles files = new();

        #region tiff-details
        using Raster page = Raster.Load("scan.tif");
        TiffDetails tiff = (TiffDetails)page.Metadata.FormatDetails!;
        Console.WriteLine($"page {tiff.PageIndex}: {tiff.Photometric}, {tiff.BitsPerSample}-bit, {tiff.Compression}, {tiff.ByteOrder}, {tiff.Layout}");
        Console.WriteLine(tiff.TileWidth is int tile ? $"tiles of {tile}" : $"{tiff.RowsPerStrip} rows a strip");

        // Tags Egami does not map are kept as they were read.
        foreach (TiffTag tag in tiff.OtherTags)
        {
            Console.WriteLine($"tag {tag.Id}: {tag.Count} × {tag.Type}");
        }
        #endregion

        Assert.Equal(TiffCompression.None, tiff.Compression);
    }
}
