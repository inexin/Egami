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

        Assert.True(File.Exists("duck.png"));
        Assert.True(File.Exists("hdr-as-sdr.jpg"));
    }
}
