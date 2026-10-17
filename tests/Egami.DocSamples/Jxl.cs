using Egami.Formats;
using Egami.Formats.Jxl;
using Egami.Pixels;

namespace Egami.DocSamples;

[Collection("Samples")]
public class Jxl
{
    [Fact]
    public void Probe()
    {
        using SampleFiles files = new();

        #region probe
        // The headers alone: size, natural pixel type, orientation and frame count, without decoding any pixels.
        RasterInfo info = Raster.Probe("duck.jxl");
        bool isJxl = info.Format == JxlFormat.Info;
        long bytes = info.EstimatedBytes;                       // what a full load would allocate
        #endregion

        Assert.True(isJxl);
        Assert.Equal((640, 480, 1), (info.Width, info.Height, info.FrameCount));
        Assert.Equal(640L * 480 * 3, bytes);
    }

    [Fact]
    public void Layers()
    {
        using SampleFiles files = new();

        #region layers
        // An animation loads as its frames, blended onto the canvas, each with its delay.
        using Raster animation = Raster.Load("sprite.jxl");
        int frames = animation.FrameCount;

        // The layers before blending, for an editor: each at its own size and place.
        IReadOnlyList<JxlLayer> layers = JxlImage.ReadLayers("sprite.jxl");
        foreach (JxlLayer layer in layers)
        {
            Console.WriteLine($"{layer.Raster.Width}x{layer.Raster.Height} at {layer.X},{layer.Y}: {layer.Blend}, {layer.Duration.TotalMilliseconds} ms");
            layer.Dispose();
        }

        // The preview some files carry for quick display, or null.
        using Raster? preview = JxlImage.ReadPreview("duck.jxl");

        // A lossy still at an eighth of its size, from its LF image alone.
        using Raster thumbnail = Raster.Load("duck.jxl", options: new JxlReaderOptions { MaxDownscale = 8 });
        #endregion

        Assert.Equal(6, frames);
        Assert.Equal(6, layers.Count);
        Assert.Null(preview);
        Assert.Equal((80, 60), (thumbnail.Width, thumbnail.Height));
    }

    [Fact]
    public void ExtraChannels()
    {
        using SampleFiles files = new();

        #region extra-channels
        // Alpha joins the pixels (straight, even when the file premultiplies); other extra channels are listed in the details.
        using Raster image = Raster.Load("sprite.jxl");
        JxlDetails details = (JxlDetails)image.Metadata.FormatDetails!;
        foreach (JxlExtraChannel channel in details.ExtraChannels)
        {
            Console.WriteLine($"{channel.Kind} '{channel.Name}', {channel.BitsPerSample} bits");
        }

        // Any one of them alone, such as depth or a selection mask, as gray floats.
        using Raster<GrayF32> alpha = JxlImage.ReadExtraChannel("sprite.jxl", 0);

        // Spot colors are mixed into the color as djxl does, unless turned off.
        using Raster plain = Raster.Load("sprite.jxl", options: new JxlReaderOptions { RenderSpotColors = false });
        #endregion

        Assert.Equal(JxlExtraChannelKind.Alpha, details.ExtraChannels[0].Kind);
        Assert.Equal((image.Width, image.Height), (alpha.Width, alpha.Height));
    }

    [Fact]
    public void Recompress()
    {
        using SampleFiles files = new();

        #region recompress
        // A JPEG becomes a JPEG XL file about a fifth smaller with the same pixels, and comes back byte for byte.
        JxlImage.RecompressJpeg("duck.jpg", "duck.jxl");
        JxlImage.ReconstructJpeg("duck.jxl", "duck-again.jpg");
        #endregion

        Assert.Equal(File.ReadAllBytes("duck.jpg"), File.ReadAllBytes("duck-again.jpg"));
        Assert.True(new FileInfo("duck.jxl").Length < new FileInfo("duck.jpg").Length);
    }

    [Fact]
    public void Reconstruct()
    {
        using SampleFiles files = new();

        #region reconstruct
        // A JPEG recompressed into JPEG XL loads as pixels like any other file, and gives its JPEG back byte for byte.
        using Raster image = Raster.Load("recompressed.jxl");
        if (((JxlDetails)image.Metadata.FormatDetails!).HasJpegReconstruction)
        {
            JxlImage.ReconstructJpeg("recompressed.jxl", "original.jpg");
        }
        #endregion

        Assert.True(File.Exists("original.jpg"));
        using Raster jpeg = Raster.Load("original.jpg");
        Assert.Equal((image.Width, image.Height), (jpeg.Width, jpeg.Height));
    }

    [Fact]
    public void Cookbook()
    {
        using SampleFiles files = new();

        #region cookbook-load
        // JPEG XL loads like any format: lossless, lossy, animated or recompressed from a JPEG, in its natural pixel type.
        using Raster duck = Raster.Load("duck.jxl");
        duck.Save("duck.png");
        #endregion

        #region cookbook-sdr
        // An HDR (PQ or HLG) file, tone mapped into sRGB as it loads; the peak comes from the file's intensity_target.
        using Raster<Rgb8> sdr = Raster.Load<Rgb8>("hdr.jxl", options: new ReaderOptions { ConvertColorTo = Egami.Colors.ColorProfile.Srgb });
        sdr.Save("hdr-as-sdr.jpg");
        #endregion

        #region save
        // Lossless JPEG XL, about the size cjxl -d 0 makes at the same effort (1 fastest to 7 smallest, 5 by default).
        using Raster photo = Raster.Load("duck.png");
        photo.Save("duck-lossless.jxl", new JxlWriterOptions { Lossless = true, Effort = 7 });
        #endregion

        Assert.True(File.Exists("duck.png"));
        Assert.True(File.Exists("hdr-as-sdr.jpg"));
        Assert.True(File.Exists("duck-lossless.jxl"));
    }

    [Fact]
    public void SaveLossy()
    {
        using SampleFiles files = new();

        #region save-lossy
        // Lossy JPEG XL at cjxl's default: quality 90, which is distance 1.0 (visually lossless).
        using Raster photo = Raster.Load("duck.png");
        photo.Save("duck-lossy.jxl", new JxlWriterOptions { Lossless = false });

        // A distance as cjxl -d takes it instead of a quality, and more effort for a smaller file.
        photo.Save("duck-small.jxl", new JxlWriterOptions { Lossless = false, Distance = 2.5f, Effort = 7 });
        #endregion

        #region save-lossy-alpha
        // Alpha stays exact in lossy files by default, and fully transparent pixels keep their color.
        using Raster<Rgba8> logo = Raster.Load<Rgba8>("duck.png");
        logo.Save("logo.jxl", new JxlWriterOptions { Lossless = false, Quality = 85 });

        // Smaller: alpha coded lossy too, and the color under transparent pixels left to the encoder (as cjxl does).
        logo.Save("logo-small.jxl", new JxlWriterOptions { Lossless = false, AlphaDistance = 1, KeepTransparentColors = false });
        #endregion

        long png = new FileInfo("duck.png").Length;
        Assert.True(new FileInfo("duck-lossy.jxl").Length < png);
        Assert.True(new FileInfo("duck-small.jxl").Length < new FileInfo("duck-lossy.jxl").Length);
        using Raster back = Raster.Load("logo.jxl");
        Assert.Equal((photo.Width, photo.Height), (back.Width, back.Height));
        Assert.True(File.Exists("logo-small.jxl"));
    }
}
