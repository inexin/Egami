using Egami.Formats.Png;
using Egami.Pixels;

namespace Egami.Tests.Memory;

public class PeakMemoryTests
{
    [Fact]
    public void PeakIsTheMostLiveAtOnce()
    {
        MemoryPeak peak = PeakMemory.Measure(context =>
        {
            using (Raster.Create<Rgba8>(100, 100, context))
            using (Raster.Create<Rgba8>(50, 50, context))
            {
            }

            using Raster<Rgba8> after = Raster.Create<Rgba8>(10, 10, context);
        });
        Assert.Equal((100 * 100 * 4) + (50 * 50 * 4), peak.PeakPixelBytes);
        Assert.Equal(0, peak.LeftBytes);
    }

    [Fact]
    public void LoadPeaksAtTheRaster()
    {
        // The 1.0 baseline the 2.2 memory gates compare with: Load holds the whole raster.
        byte[] png;
        using (Raster<Rgb8> image = Raster.Create<Rgb8>(300, 200))
        {
            using MemoryStream stream = new();
            image.Save(stream, PngFormat.Info);
            png = stream.ToArray();
        }

        MemoryPeak peak = PeakMemory.Measure(context =>
        {
            using Raster<Rgb8> loaded = Raster.Load<Rgb8>(png, context);
        });
        Assert.InRange(peak.PeakPixelBytes, 300 * 200 * 3, (300 * 200 * 3) + 65_536);
        Assert.Equal(0, peak.LeftBytes);
    }
}
