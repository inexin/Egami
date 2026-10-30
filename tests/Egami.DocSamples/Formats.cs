using Egami.Formats;
using Egami.Formats.Bmp;
using Egami.Formats.Png;
using Egami.Formats.Pnm;
using Egami.Formats.Qoi;
using Egami.Formats.Tga;
using Egami.Pixels;

namespace Egami.DocSamples;

[Collection("Samples")]
public class Formats
{
    [Fact]
    public void Png()
    {
        using SampleFiles files = new();
        using Raster image = Raster.Load("duck.png");

        #region png
        using FileStream output = File.Create("duck-small.png");
        image.Save(output, PngFormat.Info, new PngWriterOptions
        {
            Reduce = true,          // the smallest exact form: no alpha if all opaque, gray or a palette when nothing is lost
            CompressionLevel = 9,   // 0 (fastest) to 9 (smallest); 5 by default
        });
        #endregion

        Assert.True(output.Length > 0);
    }

    [Fact]
    public void PngLarge()
    {
        using SampleFiles files = new();
        using Raster image = Raster.Load("duck.png");

        #region png-large
        image.Save("duck-large.png", new PngWriterOptions
        {
            ParallelCompression = true, // big images compress on several cores; the same file at any thread count
            Interlace = true,           // Adam7: a coarse image shows before the whole file has arrived
        });
        #endregion

        Assert.True(((PngDetails)Raster.Load("duck-large.png").Metadata.FormatDetails!).Interlaced);
    }

    [Fact]
    public void Bmp()
    {
        using SampleFiles files = new();
        using Raster image = Raster.Load("duck.png");

        #region bmp
        // Auto (the default): 24-bit when every pixel is opaque, 32-bit with alpha otherwise.
        image.Save("duck.bmp");

        // A 16-color palette, built from the image and dithered.
        image.Save("duck-16.bmp", new BmpWriterOptions { PixelFormat = BmpPixelFormat.Indexed4 });

        // The same palette, run-length encoded (RLE4): smaller where the image has flat areas.
        image.Save("duck-16-rle.bmp", new BmpWriterOptions { PixelFormat = BmpPixelFormat.Indexed4, RunLength = true });
        #endregion

        Assert.Equal(4, ((BmpDetails)Raster.Load("duck-16.bmp").Metadata.FormatDetails!).BitsPerPixel);
    }

    [Fact]
    public void Tga()
    {
        using SampleFiles files = new();
        using Raster image = Raster.Load("duck.png");

        #region tga
        // Run-length encoded by default, bottom-left origin, with a TGA 2.0 footer that states the alpha type.
        image.Save("duck.tga");
        image.Save("duck-raw.tga", new TgaWriterOptions { Rle = false });
        #endregion

        Assert.True(new FileInfo("duck.tga").Length < new FileInfo("duck-raw.tga").Length + 1000);
    }

    [Fact]
    public void Qoi()
    {
        using SampleFiles files = new();
        using Raster image = Raster.Load("duck.png");

        #region qoi
        // 3 channels when every pixel is opaque, 4 otherwise. The bytes match the reference encoder's.
        image.Save("duck.qoi");
        image.Save("duck-rgba.qoi", new QoiWriterOptions { IncludeAlpha = true });
        #endregion

        Assert.Equal(4, ((QoiDetails)Raster.Load("duck-rgba.qoi").Metadata.FormatDetails!).Channels);
    }

    [Fact]
    public void Pnm()
    {
        using SampleFiles files = new();
        using Raster image = Raster.Load("duck.png");

        #region pnm
        // Auto: P5 (PGM) for gray, P6 (PPM) for color, P7 (PAM) when the pixel type has alpha.
        image.Save("duck.ppm");

        // The plain-text forms, for tools that read them; lines are at most 70 characters.
        image.Save("duck-plain.ppm", new PnmWriterOptions { PlainText = true });

        // A 1-bit bitmap, thresholded at 50% gray.
        image.Save("duck.pbm", new PnmWriterOptions { Kind = PnmKind.Bitmap });
        #endregion

        Assert.Equal(PnmKind.Bitmap, ((PnmDetails)Raster.Load("duck.pbm").Metadata.FormatDetails!).Kind);
    }

    [Fact]
    public void NaturalTypes()
    {
        using SampleFiles files = new();

        #region natural-types
        using Raster image = Raster.Load("duck.png");
        switch (image)
        {
            case Raster<Rgb8> rgb:
                Console.WriteLine($"8-bit color, {rgb.Width} × {rgb.Height}");
                break;
            case Raster<Rgba8> rgba:
                Console.WriteLine($"8-bit color with alpha, {rgba.Width} × {rgba.Height}");
                break;
            default:
                Console.WriteLine($"{image.Layout.ChannelOrder}, {image.Layout.ChannelType}");
                break;
        }
        #endregion

        Assert.IsType<Raster<Rgba8>>(image);
    }

    [Fact]
    public void OneFormatOnly()
    {
        using SampleFiles files = new();
        Raster.Load("duck.png").Save("duck.tga");

        #region one-format
        // TGA has no signature, so it is detected last. When you know a file is TGA, register only that format.
        EgamiContext tgaOnly = EgamiContext.Default with { Formats = [TgaFormat.Info] };
        using Raster image = Raster.Load("duck.tga", tgaOnly);
        #endregion

        Assert.Equal("TGA", image.Metadata.FormatName);
    }

    [Fact]
    public void NamedFormat()
    {
        using SampleFiles files = new();
        Raster.Load("duck.png").Save("duck.tga");
        byte[] data = File.ReadAllBytes("duck.tga");

        #region named-format
        // Data without a signature of its own (a TGA here; a JBIG2 stream from a PDF) loads with its format named,
        // which skips detection. Probe, LoadAll and RasterDecoder.Open take the format the same way.
        using Raster image = Raster.Load(data, TgaFormat.Info);
        RasterInfo info = Raster.Probe(data, TgaFormat.Info);
        #endregion

        Assert.Equal(info.Width, image.Width);
    }
}
