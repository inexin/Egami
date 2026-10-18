using Egami.Drawing;
using Egami.Formats;
using Egami.Metafiles;
using Egami.Pixels;

namespace Egami.DocSamples;

[Collection("Samples")]
public class Metafiles
{
    private static string Chart => System.IO.Path.Combine(AppContext.BaseDirectory, "images", "chart.emf");

    [Fact]
    public void RasterizeAnEmf()
    {
        string output = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"egami-doc-{Guid.NewGuid():N}.png");
        string path = Chart;
        try
        {
            #region metafile-to-png
            // WMF, EMF and EMF+ alike, and the gzip forms WMZ and EMZ.
            Metafile chart = Metafile.Load(path);
            Console.WriteLine($"{chart.Kind}, {chart.Size.X} by {chart.Size.Y} points");

            // At twice the screen's resolution, over white; text uses the installed fonts.
            using Raster<Rgba8> image = chart.Rasterize<Rgba8>(new MetafileRenderOptions
            {
                Dpi = 192,
                Background = new RgbaF32(1, 1, 1, 1),
            });
            image.Save(output);
            #endregion
            Assert.True(File.Exists(output));
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public void DrawOntoACanvas()
    {
        string path = Chart;
        #region metafile-draw
        Metafile picture = Metafile.Load(path);

        // The frame is stretched to the box, as Word stretches a picture to its frame. The same calls draw on a PDF page.
        using Raster<Rgba8> page = Raster.Create<Rgba8>(400, 300);
        using RasterCanvas<Rgba8> canvas = page.CreateCanvas();
        canvas.Clear(new RgbaF32(1, 1, 1, 1));
        picture.Draw(canvas, new RectF(50, 50, 300, 200));
        #endregion
        Assert.NotEqual(page.GetPixel(5, 5), page.GetPixel(80, 80));
    }

    [Fact]
    public void LoadThroughRasterLoad()
    {
        string path = Chart;
        #region metafile-format
        // Register the format once; Raster.Load then draws metafiles at their own size at 96 DPI.
        EgamiContext context = EgamiContext.Default.WithFormat(MetafileFormat.Info);
        using Raster raster = Raster.Load(path, context);
        RasterInfo info = Raster.Probe(path, context); // the size from the header, nothing drawn

        // MetafileDecodeOptions sets the resolution; Raster.Load then draws at that DPI.
        using Raster sharp = Raster.Load(path, context, new MetafileDecodeOptions { Dpi = 300 });
        #endregion
        Assert.Equal(raster.Width, info.Width);
    }

    [Fact]
    public void SubstituteFonts()
    {
        string path = Chart;
        #region metafile-fonts
        // Faces the machine lacks: the built-in table maps Calibri to Carlito, Arial to Liberation Sans and so on; add your own.
        Metafile picture = Metafile.Load(path);
        using Raster<Rgba8> image = picture.Rasterize<Rgba8>(new MetafileRenderOptions
        {
            FontSubstitutions = new Dictionary<string, string> { ["Segoe UI"] = "Noto Sans" },
            Antialias = false, // GDI's own aliased look, for comparing with Windows
        });
        #endregion
        Assert.True(image.Width > 0);
    }

    [Fact]
    public void LimitTheLoad()
    {
        byte[] bytes = File.ReadAllBytes(Chart);
        #region metafile-limits
        // Untrusted files: the context's limits bound the file size and what EMZ and WMZ inflate to; a record that runs past
        // the end is skipped with a warning, and what came before it plays.
        MetafileLoadOptions options = new()
        {
            Context = EgamiContext.Default with { Limits = SafetyLimits.Default with { MaxTotalBytes = 16 << 20 } },
        };
        Metafile picture = Metafile.Load(new MemoryStream(bytes), options);
        if (picture.Kind is MetafileKind.EmfPlusDual or MetafileKind.EmfPlusOnly)
        {
            Console.WriteLine("Drawn by its EMF+ records, as GDI+ draws it.");
        }
        #endregion
        Assert.Equal(MetafileKind.Emf, picture.Kind);
    }

    [Fact]
    public void LoadFromBytes()
    {
        byte[] bytes = File.ReadAllBytes(Chart);
        #region metafile-bytes
        // A picture taken out of a document, already in memory: no stream needed.
        Metafile picture = Metafile.Load(bytes);
        using Raster<Rgba8> page = Raster.Create<Rgba8>(200, 150);
        using RasterCanvas<Rgba8> canvas = page.CreateCanvas();
        picture.Draw(canvas, new RectF(0, 0, 200, 150));
        #endregion
        Assert.Equal(MetafileKind.Emf, picture.Kind);
    }

    [Fact]
    public void PlayTheGdiPart()
    {
        string path = Chart;
        #region metafile-gdi
        // A dual EMF+ file plays its EMF+ records by default, as Office and GDI+ do; Gdi plays only the GDI records, as
        // programs without GDI+ draw it.
        Metafile picture = Metafile.Load(path);
        using Raster<Rgba8> gdi = picture.Rasterize<Rgba8>(new MetafileRenderOptions { Playback = MetafilePlayback.Gdi });
        #endregion
        Assert.True(gdi.Width > 0);
    }
}
