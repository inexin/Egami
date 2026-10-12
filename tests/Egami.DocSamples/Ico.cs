using Egami.Formats;
using Egami.Formats.Ico;
using Egami.Pixels;

namespace Egami.DocSamples;

[Collection("Samples")]
public class Ico
{
    [Fact]
    public void Load()
    {
        using SampleFiles files = new();

        #region ico-load
        // Load gives the best entry: the largest, then the deepest. LoadAll gives every entry at its own size.
        using Raster best = Raster.Load("app.ico");
        using RasterList entries = Raster.LoadAll("app.ico");

        // MinimumSize picks the smallest entry at least that big, so a 32 × 32 toolbar icon decodes only the 32 × 32 entry.
        using Raster small = Raster.Load("app.ico", options: new ReaderOptions { MinimumSize = new Size(32, 32) });
        #endregion

        Assert.Equal(256, best.Width);
        Assert.Equal(6, entries.Count);
        Assert.Equal(32, small.Width);
    }

    [Fact]
    public void Entry()
    {
        using SampleFiles files = new();

        #region ico-entry
        // Pick an entry by its place in the file's directory.
        using Raster second = Raster.Load("app.ico", options: new IcoReaderOptions { Entry = 1 });
        #endregion

        Assert.Equal(24, second.Width);
    }

    [Fact]
    public void Sizes()
    {
        using SampleFiles files = new();

        #region ico-sizes
        // Probe reads only the directory: the sizes an icon holds, without decoding a pixel.
        RasterInfo info = Raster.Probe("app.ico");
        Console.WriteLine($"{info.ImageCount} entries; Load gives {info.Width}×{info.Height}");
        using Raster best = Raster.Load("app.ico");
        foreach (IcoEntry entry in ((IcoDetails)best.Metadata.FormatDetails!).Entries)
        {
            Console.WriteLine($"{entry.Width}×{entry.Height}, {entry.BitsPerPixel} bits, {entry.Encoding}");
        }
        #endregion

        Assert.Equal(6, info.ImageCount);
    }

    [Fact]
    public void Save()
    {
        using SampleFiles files = new();
        using Raster<Rgba8> logo = Raster.Load<Rgba8>("duck.png");

        #region ico-save
        // A favicon: 16, 32 and 48 resized from one raster. A raster that is not square is centred on transparency.
        logo.Save("favicon.ico", new IcoWriterOptions { Sizes = IcoWriterOptions.FaviconSizes });

        // Windows' set: 16 to 256, the 256 entry as PNG and the rest as 32-bit BMP, as Microsoft's guidance asks.
        logo.Save("app.ico", new IcoWriterOptions { Sizes = IcoWriterOptions.WindowsSizes, Filter = Egami.Operations.ResampleFilter.Lanczos3 });

        // Hand-drawn small sizes: each raster is written at its own size; the 48 comes from the largest raster.
        using Raster<Rgba8> drawn16 = logo.Derive(ops => ops.Fit(16, 16));
        Raster.SaveAll([logo, drawn16], "drawn.ico", new IcoWriterOptions { Sizes = [16, 48] });
        #endregion

        Assert.Equal(3, Raster.Probe("favicon.ico").ImageCount);
        Assert.Equal(2, Raster.Probe("drawn.ico").ImageCount);
    }

    [Fact]
    public void Cursor()
    {
        using SampleFiles files = new();
        using Raster<Rgba8> photo = Raster.Load<Rgba8>("duck.png");
        using Raster<Rgba8> arrow = photo.Derive(ops => ops.Crop(0, 0, 480, 480));

        #region cur-save
        // A cursor is an icon with a hotspot, given at the raster's own size and scaled to each entry.
        arrow.Save("pointer.cur", new IcoWriterOptions { Sizes = [32, 48], Hotspot = new Point(0, 0) });
        using RasterList cursor = Raster.LoadAll("pointer.cur");
        Point? hotspot = ((IcoDetails)cursor[0].Metadata.FormatDetails!).Hotspot;
        #endregion

        Assert.Equal(new Point(0, 0), hotspot);
        Assert.Equal(CurFormat.Info, Raster.Probe("pointer.cur").Format);
    }
}
