using Egami.Formats;
using Egami.Formats.Extended;
using Egami.Formats.Extended.Pcx;
using Egami.Formats.Extended.Sgi;
using Egami.Formats.Extended.SunRaster;
using Egami.Formats.Extended.Xbm;
using Egami.Pixels;

namespace Egami.DocSamples;

[Collection("Samples")]
public class FormatsExtended
{
    [Fact]
    public void Register()
    {
        using SampleFiles files = new();

        #region register
        // Every stable format of Egami.Formats.Extended, once at startup before any other Egami call:
        // EgamiContext.SetDefault(new EgamiContext().WithExtendedFormats());

        // ...or in a context of its own.
        EgamiContext context = EgamiContext.Default.WithExtendedFormats();
        using Raster image = Raster.Load("duck.png", context);

        // A raster saves to a path through the context it was loaded with, so the extension picks PCX.
        image.Save("duck.pcx");
        using Raster back = Raster.Load("duck.pcx", context);
        string format = back.Metadata.FormatName!;               // "PCX"
        #endregion

        Assert.Equal("PCX", format);
        Assert.Equal((image.Width, image.Height), (back.Width, back.Height));
    }

    [Fact]
    public void RegisterOne()
    {
        using SampleFiles files = new();
        using Raster image = Raster.Load("duck.png");

        #region register-one
        // One format at a time: each has its own method and its own FormatInfo.
        EgamiContext context = EgamiContext.Default.WithSgi().WithSunRaster();
        using (FileStream output = File.Create("duck.sgi"))
        {
            image.Save(output, SgiFormat.Info);
        }

        RasterInfo info = Raster.Probe("duck.sgi", context);
        Console.WriteLine($"{info.Format.Name} {info.Width}×{info.Height} {info.Layout.ChannelOrder}");
        #endregion

        Assert.Equal("SGI", info.Format.Name);
        Assert.Equal(SunRasterFormat.Info, context.Formats[^1]);
    }

    [Fact]
    public void Details()
    {
        using SampleFiles files = new();
        EgamiContext context = EgamiContext.Default.WithExtendedFormats();
        using (Raster<Gray8> cursor = Raster.Create<Gray8>(16, 16, context))
        {
            cursor.Metadata.FormatDetails = new XbmDetails { Name = "arrow", Hotspot = new Point(1, 1) };
            cursor.Save("arrow.xbm");
        }

        #region details
        // What the header said that the pixels do not: PcxDetails, SgiDetails, SunRasterDetails, XbmDetails and XpmDetails.
        using Raster image = Raster.Load("arrow.xbm", context);
        if (image.Metadata.FormatDetails is XbmDetails xbm)
        {
            Console.WriteLine($"{xbm.Name}, hotspot {xbm.Hotspot}");
        }

        // Writers take the name and hotspot back from the details.
        image.Metadata.FormatDetails = new XbmDetails { Name = "pointer", Hotspot = new Point(0, 0) };
        image.Save("pointer.xbm");
        #endregion

        Assert.StartsWith("#define pointer_width 16", File.ReadAllText("pointer.xbm"), StringComparison.Ordinal);
        Assert.IsType<PcxDetails>(Raster.Load(SavePcx(image), context).Metadata.FormatDetails);
    }

    [Fact]
    public void Options()
    {
        using SampleFiles files = new();
        using Raster image = Raster.Load("duck.png");

        #region options
        // SGI codes its rows by default; Sun raster writes them plain unless asked.
        using (FileStream sgi = File.Create("duck.rgb"))
        {
            image.Save(sgi, SgiFormat.Info, new SgiWriterOptions { Rle = false });
        }

        using (FileStream sun = File.Create("duck.ras"))
        {
            image.Save(sun, SunRasterFormat.Info, new SunRasterWriterOptions { Rle = true });
        }
        #endregion

        EgamiContext context = EgamiContext.Default.WithExtendedFormats();
        Assert.False(((SgiDetails)Raster.Load("duck.rgb", context).Metadata.FormatDetails!).Rle);
        Assert.True(((SunRasterDetails)Raster.Load("duck.ras", context).Metadata.FormatDetails!).Rle);
    }

    private static byte[] SavePcx(Raster image)
    {
        using MemoryStream output = new();
        image.Save(output, PcxFormat.Info);
        return output.ToArray();
    }
}
