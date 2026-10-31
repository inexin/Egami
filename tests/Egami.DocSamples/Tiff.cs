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

        // Several rasters make a multi-page TIFF; one raster saves to any format.
        Raster.SaveAll(pages, "copy.tif");
        Raster.SaveAll([pages[1]], "second.png");
        #endregion

        Assert.Equal(3, Raster.Probe("copy.tif").ImageCount);
    }

    [Fact]
    public void Split()
    {
        using SampleFiles files = new();

        #region tiff-split
        using RasterList pages = Raster.LoadAll("scan.tif");
        for (int i = 0; i < pages.Count; i++)
        {
            pages[i].Save($"page-{i + 1}.png");
        }
        #endregion

        Assert.True(File.Exists("page-3.png"));
    }

    [Fact]
    public void Save()
    {
        using SampleFiles files = new();
        using Raster<Rgb16> photo = Raster.Load<Rgb16>("duck.png");

        #region tiff-save
        // Each pixel type is written at its own depth: 16-bit color stays 16-bit. Deflate with the predictor is the default.
        photo.Save("duck.tif");

        // Tiles, LZW, BigTIFF, or the smallest exact form the pixels allow.
        photo.Save("tiled.tif", new TiffWriterOptions { Compression = TiffCompression.Lzw, TileSize = 256, Layout = TiffLayout.Auto });
        photo.Save("small.tif", new TiffWriterOptions { Reduce = true });

        // JPEG inside TIFF: RGB is written as YCbCr 4:2:0 with the tables shared by every strip.
        photo.Save("photo.tif", new TiffWriterOptions { Compression = TiffCompression.Jpeg, Quality = 90 });
        #endregion

        Assert.Equal(16, ((TiffDetails)Raster.Load("duck.tif").Metadata.FormatDetails!).BitsPerSample);
    }

    [Fact]
    public void Fax()
    {
        using SampleFiles files = new();
        using Raster<Gray8> page = Raster.Load<Gray8>("duck.png");
        for (int y = 0; y < page.Height; y++)
        {
            foreach (ref Gray8 p in page.Row(y))
            {
                p = new Gray8(p.L < 128 ? (byte)0 : (byte)255);
            }
        }

        #region tiff-fax
        // A page where every pixel is black or white saves as a 1-bit CCITT Group 4 fax.
        page.Save("fax.tif", new TiffWriterOptions { Compression = TiffCompression.CcittGroup4 });
        #endregion

        Assert.Equal(TiffCompression.CcittGroup4, ((TiffDetails)Raster.Load("fax.tif").Metadata.FormatDetails!).Compression);
    }

    [Fact]
    public void Zstd()
    {
        using SampleFiles files = new();
        using Raster<Rgb16> photo = Raster.Load<Rgb16>("duck.png");

        #region tiff-zstd
        // Zstandard with the predictor: lossless, read by libtiff and GDAL. Level 1 writes fastest, 19 writes smallest.
        photo.Save("zstd.tif", new TiffWriterOptions { Compression = TiffCompression.Zstd });
        photo.Save("zstd-small.tif", new TiffWriterOptions { Compression = TiffCompression.Zstd, ZstdLevel = 19 });
        #endregion

        Assert.Equal(TiffCompression.Zstd, ((TiffDetails)Raster.Load("zstd.tif").Metadata.FormatDetails!).Compression);
        Assert.True(new FileInfo("zstd-small.tif").Length <= new FileInfo("zstd.tif").Length);
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
