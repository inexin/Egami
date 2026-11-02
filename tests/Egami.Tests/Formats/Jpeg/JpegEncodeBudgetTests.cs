using Egami.Formats.Jpeg;
using Egami.Pixels;
using Egami.Tests.Memory;
using Egami.Tests.Operations;

namespace Egami.Tests.Formats.Jpeg;

/// <summary>
/// Step 2.4.10's allocation budget for JPEG encoding: a 12-megapixel q75 4:2:0 encode of an Rgb8 raster on one thread to a null
/// stream allocates no more than main did before the speed work (measured at 7c50480f), plus 1 KB. Rows, planes and block
/// buffers come from the pools, so what is left is the encoder's objects and the headers.
/// </summary>
[Trait("Category", AllocationMeter.Category)]
public class JpegEncodeBudgetTests
{
    /// <summary>What main allocated for the encode, in bytes, at 7c50480f.</summary>
    private const long MainBytes = 2224;

    [Fact]
    public void TwelveMegapixelEncodeStaysInMainsBudget()
    {
        AllocationMeter.SkipUnlessOnDemand();
        EgamiContext context = EgamiContext.Default with { MaxParallelism = 1 };
        using Raster<Rgba8> noise = OperationCoverageTests.Noise(4000, 3000, context);
        using Raster<Rgb8> image = noise.As<Rgb8>();
        JpegWriterOptions options = new() { Quality = 75 };
        long bytes = AllocationMeter.Fewest(() => image.Save(Stream.Null, JpegFormat.Info, options), warmUps: 2, tries: 4);
        Assert.True(bytes <= MainBytes + 1024, $"A 12 MP q75 4:2:0 encode allocated {bytes:N0} bytes; main's budget is {MainBytes:N0} + 1,024.");
    }
}
