using System.IO.Compression;

using Egami.Formats;
using Egami.Pixels;

namespace Egami.Metafiles.Tests;

/// <summary>Task 3: MetafileFormat reads WMF, EMF, WMZ and EMZ through Raster.Load and Raster.Probe.</summary>
public class FormatTests
{
    private static readonly EgamiContext s_context = EgamiContext.Default.WithFormat(MetafileFormat.Info);

    public static TheoryData<string> Synthetic => [.. TestFiles.Synthetic()];

    [Theory]
    [MemberData(nameof(Synthetic))]
    public void ProbeAndLoadMatchTheMetafile(string name)
    {
        string path = TestFiles.SyntheticPath(name);
        Metafile metafile = Metafile.Load(path);
        int width = (int)Math.Ceiling((metafile.Size.X * 96 / 72) - 1e-3), height = (int)Math.Ceiling((metafile.Size.Y * 96 / 72) - 1e-3);

        RasterInfo info = Raster.Probe(path, s_context);
        Assert.Same(MetafileFormat.Info, info.Format);
        Assert.Equal((width, height), (info.Width, info.Height));

        using Raster raster = Raster.Load(path, s_context);
        Assert.IsType<Raster<Rgba8>>(raster);
        Assert.Equal((width, height), (raster.Width, raster.Height));
        Assert.Equal("Metafile", raster.Metadata.FormatName);

        using Raster<Rgba8> direct = metafile.Rasterize<Rgba8>();
        Raster<Rgba8> loaded = (Raster<Rgba8>)raster;
        for (int y = 0; y < height; y++)
        {
            Assert.True(direct.Row(y).SequenceEqual(loaded.Row(y)), $"Row {y} differs.");
        }
    }

    [Fact]
    public void GzipFilesAreDetected()
    {
        byte[] raw = File.ReadAllBytes(TestFiles.SyntheticPath("plus-shapes.dual.emf"));
        using MemoryStream gz = new();
        using (GZipStream zip = new(gz, CompressionLevel.Optimal, leaveOpen: true))
        {
            zip.Write(raw);
        }

        RasterInfo packed = Raster.Probe(gz.ToArray(), s_context), plain = Raster.Probe(raw, s_context);
        Assert.Equal((plain.Width, plain.Height), (packed.Width, packed.Height));
    }

    [Fact]
    public void DpiScalesTheSize()
    {
        string path = TestFiles.SyntheticPath("gdi-shapes-basic.wmf");
        RasterInfo info = Raster.Probe(path, s_context, new MetafileDecodeOptions { Dpi = 192 });
        Assert.Equal((400, 300), (info.Width, info.Height));
    }

    [Fact]
    public void OtherFilesAreNotMetafiles()
    {
        Assert.False(MetafileFormat.Matches("\x89PNG\r\n\x1a\n"u8));
        Assert.False(MetafileFormat.Matches("<svg xmlns='http://www.w3.org/2000/svg'/>"u8));
        Assert.False(MetafileFormat.Matches([]));
    }

    [Fact]
    public void WarningsReachTheRaster()
    {
        using Raster raster = Raster.Load(TestFiles.SyntheticPath("gdi-text-textout.plain.wmf"), s_context);
        Assert.Contains(raster.Metadata.Warnings, w => w.Id == "Metafile.NoPlaceableHeader");
    }
}
