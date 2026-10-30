using Egami.Formats;
using Egami.Formats.Icns;
using Egami.Pixels;

namespace Egami.DocSamples;

[Collection("Samples")]
public class Icns
{
    [Fact]
    public void Save()
    {
        using SampleFiles files = new();
        using Raster<Rgba8> logo = Raster.Load<Rgba8>("duck.png");

        #region icns-save
        // A macOS app icon: 16 to 1024, each resized from one raster. A raster that is not square is centred on transparency.
        logo.Save("app.icns", new IcnsWriterOptions { Sizes = IcnsWriterOptions.MacSizes, Filter = Egami.Operations.ResampleFilter.Lanczos3 });

        // Hand-drawn small sizes: each raster goes in the entry size it fills, centred on transparency.
        using Raster<Rgba8> drawn16 = logo.Derive(ops => ops.Fit(16, 16));
        using Raster<Rgba8> large = logo.Derive(ops => ops.Fit(512, 512));
        Raster.SaveAll([large, drawn16], "drawn.icns");
        #endregion

        Assert.Equal(7, Raster.Probe("app.icns").ImageCount);
        Assert.Equal(2, Raster.Probe("drawn.icns").ImageCount);
    }

    [Fact]
    public void Load()
    {
        using SampleFiles files = new();
        Write();

        #region icns-load
        // Load gives the best entry: the largest, then the deepest. LoadAll gives every entry at its own size.
        using Raster best = Raster.Load("app.icns");
        using RasterList entries = Raster.LoadAll("app.icns");

        // MinimumSize picks the smallest entry at least that big, so a 64 × 64 thumbnail decodes only the 64 × 64 entry.
        using Raster small = Raster.Load("app.icns", options: new ReaderOptions { MinimumSize = new Size(64, 64) });
        #endregion

        Assert.Equal(1024, best.Width);
        Assert.Equal(7, entries.Count);
        Assert.Equal(64, small.Width);
    }

    [Fact]
    public void Entry()
    {
        using SampleFiles files = new();
        Write();

        #region icns-entry
        // Pick an entry by its place among the file's image entries.
        using Raster second = Raster.Load("app.icns", options: new IcnsReaderOptions { Entry = 1 });
        #endregion

        Assert.Equal(32, second.Width);
    }

    [Fact]
    public void Sizes()
    {
        using SampleFiles files = new();
        Write();

        #region icns-sizes
        // Probe reads only the elements: how many images an icon family holds, without decoding a pixel.
        RasterInfo info = Raster.Probe("app.icns");
        Console.WriteLine($"{info.ImageCount} entries; Load gives {info.Width}×{info.Height}");
        using Raster best = Raster.Load("app.icns");
        foreach (IcnsEntry entry in ((IcnsDetails)best.Metadata.FormatDetails!).Entries)
        {
            Console.WriteLine($"{entry.Type}: {entry.Width}×{entry.Height}, {entry.BitsPerPixel} bits, {entry.Encoding}");
        }
        #endregion

        Assert.Equal(7, info.ImageCount);
    }

    private static void Write()
    {
        using Raster<Rgba8> logo = Raster.Load<Rgba8>("duck.png");
        logo.Save("app.icns", new IcnsWriterOptions { Sizes = IcnsWriterOptions.MacSizes });
    }
}
