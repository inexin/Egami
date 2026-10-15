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
}
