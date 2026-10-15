using Egami.Formats;
using Egami.Formats.Jxl;

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
}
