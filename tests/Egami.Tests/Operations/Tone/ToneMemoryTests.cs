using Egami.Pixels;
using Egami.Tests.Memory;

namespace Egami.Tests.Operations.Tone;

/// <summary>
/// Phase 2.5.3 memory, with the 2.2 peak harness: tone steps and measured steps run in place on the raster's own plane;
/// CLAHE adds one lightness plane, LocalContrast two, AdaptiveThreshold one, all from the chain's memory source.
/// </summary>
public class ToneMemoryTests
{
    private const int Width = 1000, Height = 800, Image = Width * Height * 4, Plane = Width * Height * 4;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void FiltersAndMeasuredStepsRunInPlace()
    {
        MemoryPeak peak = PeakMemory.Measure(context =>
        {
            using Raster<Rgba8> raster = Raster.Create(Width, Height, new Rgba8(90, 120, 150, 255), context);
            raster.Apply(o => o.Level(0.1f, 0.9f).AutoLevel().Normalize().Equalize().AutoWhiteBalance().Tint(Rgba8.Red, 0.2f).AutoThreshold(), Ct);
        });
        Assert.True(peak.PeakPixelBytes <= Image + 4096, peak.ToString());
        Assert.Equal(0, peak.LeftBytes);
    }

    [Theory]
    [InlineData("Clahe", 1)]
    [InlineData("LocalContrast", 2)]
    [InlineData("AdaptiveThreshold", 1)]
    public void LocalStepsHoldTheirPlanes(string step, int planes)
    {
        MemoryPeak peak = PeakMemory.Measure(context =>
        {
            using Raster<Rgba8> raster = Raster.Create(Width, Height, new Rgba8(90, 120, 150, 255), context);
            raster.Apply(
                o =>
                {
                    _ = step switch
                    {
                        "Clahe" => o.Clahe(),
                        "LocalContrast" => o.LocalContrast(),
                        _ => o.AdaptiveThreshold(),
                    };
                },
                Ct);
        });
        Assert.True(peak.PeakPixelBytes <= Image + (planes * Plane) + 65536, $"{step}: {peak}");
        Assert.Equal(0, peak.LeftBytes);
    }

    [Fact]
    public void TheLightnessPlaneCountsAgainstTheBudget()
    {
        using Raster<Rgba8> raster = Raster.Create(Width, Height, new Rgba8(90, 120, 150, 255), EgamiContext.Default with { Limits = SafetyLimits.Default with { MaxTotalBytes = Plane / 2 } });
        Assert.Throws<LimitExceededException>(() => raster.Apply(o => o.Clahe(), Ct));
    }
}
