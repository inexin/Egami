using Egami.Formats;
using Egami.Formats.Avif;

namespace Egami.DocSamples;

[Collection("Samples")]
public class Avif
{
    [Fact]
    public void Probe()
    {
        using SampleFiles files = new();

        #region probe
        // The container alone: size, natural pixel type and orientation, without decoding any AV1.
        RasterInfo info = Raster.Probe("duck.avif");
        bool isAvif = info.Format == AvifFormat.Info;
        long bytes = info.EstimatedBytes;                       // what a full load would allocate
        #endregion

        Assert.True(isAvif);
        Assert.Equal((320, 240), (info.Width, info.Height));
        Assert.Equal(320L * 240 * 3, bytes);
    }
}
