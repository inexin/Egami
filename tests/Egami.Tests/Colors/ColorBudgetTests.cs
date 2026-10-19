using Egami.Colors;
using Egami.Metadata;
using Egami.Operations;
using Egami.Pixels;
using Egami.Tests.Memory;
using Egami.Tests.Operations;

namespace Egami.Tests.Colors;

/// <summary>
/// Phase 2.1.2 task 12: the color operations keep the allocation budget of every operation: what they allocate does not grow with
/// the image (under 1 KB more for 16 times the pixels) and stays under 8 KB a call, once their transform is cached, besides the
/// copy of an embedded ICC profile that Derive makes with the metadata.
/// </summary>
public class ColorBudgetTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, string> Cases()
    {
        TheoryData<string, string> data = [];
        foreach (string chain in new[] { "ConvertColor-Matrix", "ConvertColor-Table", "ConvertColor-Chain", "ToneMap", "Resize-Linear-Gamma", "Blur-Linear-Pq" })
        {
            foreach (string type in new[] { nameof(Rgba8), nameof(Bgra8), nameof(Rgb8), nameof(Gray8), nameof(Rgba16), nameof(RgbaF32), nameof(RgbaF16) })
            {
                data.Add(chain, type);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ColorOperationsStayInTheBudget(string chain, string type)
    {
        switch (type)
        {
            case nameof(Rgba8): Budget<Rgba8>(chain); break;
            case nameof(Bgra8): Budget<Bgra8>(chain); break;
            case nameof(Rgb8): Budget<Rgb8>(chain); break;
            case nameof(Gray8): Budget<Gray8>(chain); break;
            case nameof(Rgba16): Budget<Rgba16>(chain); break;
            case nameof(RgbaF32): Budget<RgbaF32>(chain); break;
            default: Budget<RgbaF16>(chain); break;
        }
    }

    private static void Budget<T>(string chain)
        where T : unmanaged, IPixelType<T>
    {
        EgamiContext context = EgamiContext.Default with { MaxParallelism = 1 };
        using Raster<Rgba8> smallSource = OperationCoverageTests.Noise(120, 90, context);
        using Raster<Rgba8> largeSource = OperationCoverageTests.Noise(480, 360, context);
        using Raster<T> small = Tagged(smallSource.As<T>(), chain);
        using Raster<T> large = Tagged(largeSource.As<T>(), chain);
        long smallBytes = Allocated(small, Chain(chain));
        long largeBytes = Allocated(large, Chain(chain));
        Assert.True(largeBytes - smallBytes < 1024, $"{chain} on {typeof(T).Name}: {largeBytes:N0} bytes for 480×360 against {smallBytes:N0} for 120×90.");
        Assert.True(largeBytes < (8 * 1024) + large.Metadata.IccProfile.Length + 512, $"{chain} on {typeof(T).Name}: {largeBytes:N0} bytes per call.");
    }

    private static Raster<T> Tagged<T>(Raster<T> raster, string chain)
        where T : unmanaged, IPixelType<T>
    {
        switch (chain)
        {
            case "ConvertColor-Table":
                raster.Metadata.IccProfile = IccReferences.Bytes("synthetic/rgb-lut16-v2.icc");
                break;
            case "Resize-Linear-Gamma":
                raster.Metadata.ColorSpace = ColorSpaceHint.Gamma;
                raster.Metadata.Gamma = 1.8;
                break;
            case "Blur-Linear-Pq":
            case "ToneMap":
                raster.Metadata.CodePoints = ColorCodePoints.Rec2100Pq;
                raster.Metadata.Hdr = new HdrMetadata { MaxContentLightLevel = 4000 };
                break;
        }

        return raster;
    }

    private static Action<RasterOps> Chain(string chain) => chain switch
    {
        "ConvertColor-Matrix" => ops => ops.ConvertColor(ColorProfile.DisplayP3),
        "ConvertColor-Table" => ops => ops.ConvertColor(ColorProfile.Srgb),
        "ConvertColor-Chain" => ops => ops.ConvertColor(ColorProfile.AdobeRgb).ConvertColor(ColorProfile.Srgb, new ColorTransformOptions { BlackPointCompensation = true }),
        "ToneMap" => ops => ops.ToneMap(),
        "Resize-Linear-Gamma" => ops => ops.Resize(77, 41, ResampleFilter.Bicubic, ColorSpace.Linear),
        _ => ops => ops.Blur(1.5f, ColorSpace.Linear),
    };

    private static long Allocated<T>(Raster<T> raster, Action<RasterOps> chain)
        where T : unmanaged, IPixelType<T> =>
        AllocationMeter.Fewest(() => raster.Derive(chain, Ct).Dispose(), warmUps: 2, tries: 4);
}
