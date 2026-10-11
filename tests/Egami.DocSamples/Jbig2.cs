using Egami.Formats;
using Egami.Formats.Jbig2;
using Egami.Pixels;

namespace Egami.DocSamples;

[Collection("Samples")]
public class Jbig2
{
    [Fact]
    public void Load()
    {
        using SampleFiles files = new();

        #region jbig2-load
        // A standalone .jb2 file is detected like any other format; each page loads as Gray8, black 0 and white 255.
        using Raster first = Raster.Load("book.jb2");
        using RasterList pages = Raster.LoadAll("book.jb2");
        using Raster third = Raster.Load("book.jb2", options: new Jbig2ReaderOptions { Page = 2 });
        #endregion

        Assert.Equal(3, pages.Count);
        Assert.Equal(Gray8.Layout, first.Layout);
    }

    [Fact]
    public void Embedded()
    {
        using SampleFiles files = new();
        byte[] globalsStream = File.ReadAllBytes("book-globals.bin");
        byte[] imageStream = File.ReadAllBytes("book-page1.bin");

        #region jbig2-embedded
        // A PDF's JBIG2Decode image has no file header, so name the format. Parse the JBIG2Globals stream once and share it:
        // every image of the document that refers to it reuses its decoded symbols.
        Jbig2Globals globals = Jbig2Globals.Parse(globalsStream);
        using Raster image = Raster.Load(imageStream, Jbig2Format.Info, options: new Jbig2ReaderOptions { Globals = globals });
        #endregion

        Assert.True(((Jbig2Details)image.Metadata.FormatDetails!).UsesGlobals);
    }

    [Fact]
    public void Details()
    {
        using SampleFiles files = new();

        #region jbig2-details
        using Raster page = Raster.Load("book.jb2");
        var details = (Jbig2Details)page.Metadata.FormatDetails!;
        Console.WriteLine($"Page {details.PageIndex}, {details.Organisation}, regions {details.Regions}");
        Console.WriteLine($"Default pixel {details.DefaultPixel}, combined by {details.CombinationOperator}, lossless {details.Lossless}");
        #endregion

        Assert.Equal(Jbig2Organisation.Sequential, details.Organisation);
        Assert.True(details.Regions.HasFlag(Jbig2RegionKinds.Text));
    }
}
