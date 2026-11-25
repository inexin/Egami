using System.Buffers.Binary;
using System.Text;

using Egami.Formats;
using Egami.Formats.Eps;
using Egami.Formats.Tiff;
using Egami.Metafiles;
using Egami.Pixels;

namespace Egami.DocSamples;

[Collection("Samples")]
public class Eps
{
    /// <summary>Writes logo.eps: a DOS binary EPS whose preview is a TIFF of a 40 × 20 raster, with a 30 × 15 point bounding box.</summary>
    private static void WriteSample()
    {
        using Raster<Rgb8> picture = Raster.Create(40, 20, new Rgb8(200, 30, 30));
        using MemoryStream tiffStream = new();
        picture.Save(tiffStream, TiffFormat.Info);
        byte[] tiff = tiffStream.ToArray();
        byte[] ps = Encoding.ASCII.GetBytes("%!PS-Adobe-3.0 EPSF-3.0\n%%BoundingBox: 0 0 30 15\n%%Creator: (Egami docs)\n%%Title: (Logo)\n%%EndComments\n0.8 0.1 0.1 setrgbcolor 0 0 30 15 rectfill\n%%EOF\n");
        byte[] file = new byte[30 + ps.Length + tiff.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(file, 0xC6D3D0C5);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), 30);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(8), (uint)ps.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(20), (uint)(30 + ps.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(24), (uint)tiff.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(28), 0xFFFF);
        ps.CopyTo(file, 30);
        tiff.CopyTo(file, 30 + ps.Length);
        File.WriteAllBytes("logo.eps", file);
    }

    /// <summary>Writes photo.eps the way Photoshop saves a CMYK image: an %ImageData comment, then 8 × 4 pixels of binary data.</summary>
    private static void WritePhotoshopSample()
    {
        byte[] head = Encoding.ASCII.GetBytes("%!PS-Adobe-3.0 EPSF-3.0\n%%Creator: Adobe Photoshop\n%%BoundingBox: 0 0 8 4\n%%EndComments\n%ImageData: 8 4 8 4 0 1 1 \"beginimage\"\nbeginimage\n");
        byte[] pixels = [.. Enumerable.Repeat<byte[]>([0, 160, 255, 0], 32).SelectMany(p => p)];
        File.WriteAllBytes("photo.eps", [.. head, .. pixels, .. "\n%%EOF\n"u8]);
    }

    [Fact]
    public void Load()
    {
        using SampleFiles files = new();
        WriteSample();

        #region eps-load
        // Load gives the preview the EPS file carries (here a TIFF); its PostScript is never run.
        using Raster preview = Raster.Load("logo.eps");
        EpsDetails details = (EpsDetails)preview.Metadata.FormatDetails!;
        Console.WriteLine($"{details.Preview} preview, {preview.Width} × {preview.Height} pixels, box {details.BoundingBox?.Width} × {details.BoundingBox?.Height} pt");
        #endregion

        Assert.Equal(EpsPreviewKind.Tiff, details.Preview);
        Assert.Equal(40, preview.Width);
        Assert.Equal(30, details.BoundingBox?.Width);
    }

    [Fact]
    public void Container()
    {
        using SampleFiles files = new();
        WriteSample();

        #region eps-file
        // EpsFile gives the parts: the PostScript, the TIFF or metafile preview, and the header comments.
        EpsFile eps = EpsFile.Load(File.ReadAllBytes("logo.eps"));
        if (eps.Preview == EpsPreviewKind.Tiff)
        {
            File.WriteAllBytes("logo-preview.tif", eps.TiffPreview.ToArray());
        }

        Console.WriteLine($"{eps.Title} by {eps.Creator}: {eps.BoundingBox}, {eps.PostScript.Length} bytes of PostScript");
        #endregion

        Assert.Equal("Logo", eps.Title);
        Assert.True(File.Exists("logo-preview.tif"));
        Assert.Equal(TiffFormat.Info, Raster.Probe("logo-preview.tif").Format);
    }

    [Fact]
    public void PhotoshopImage()
    {
        using SampleFiles files = new();
        WritePhotoshopSample();

        #region eps-photoshop
        // A Photoshop EPS loads at full size in its own colours: here CMYK, kept as Cmyk8.
        using Raster image = Raster.Load("photo.eps");
        if (image is Raster<Cmyk8> cmyk)
        {
            Cmyk8 first = cmyk.Row(0)[0];
            Console.WriteLine($"{cmyk.Width} × {cmyk.Height} CMYK, first pixel {first.C} {first.M} {first.Y} {first.K}");
        }
        #endregion

        Assert.IsType<Raster<Cmyk8>>(image);
        Assert.Equal(EpsPreviewKind.PhotoshopImage, ((EpsDetails)image.Metadata.FormatDetails!).Preview);
    }

    [Fact]
    public void Recipe()
    {
        using SampleFiles files = new();
        WriteSample();

        #region eps-recipe
        // Metafile previews draw only with a metafile format registered; files with only PostScript have no image to load.
        EgamiContext context = EgamiContext.Default.WithFormat(MetafileFormat.Info);
        try
        {
            using Raster<Rgba8> image = Raster.Load<Rgba8>("logo.eps", context);
            image.Save("logo.png");
        }
        catch (UnsupportedFormatException)
        {
            RasterInfo box = Raster.Probe("logo.eps", context);
            Console.WriteLine($"Only PostScript: {box.Width} × {box.Height} pt, render it with a PostScript interpreter");
        }
        #endregion

        Assert.True(File.Exists("logo.png"));
    }
}
