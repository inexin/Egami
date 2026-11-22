using Egami.Drawing.Tracing;
using Egami.Pixels;

namespace Egami.Drawing.Tests.Tracing;

/// <summary>
/// What tracing allocates per pixel once warm, in each mode (EGAMI_ALLOCATION_TESTS=1). Tracing runs on several threads, so the
/// tests count the whole process's allocations; run them alone, in Release.
/// </summary>
public class TraceAllocationTests
{
    [Theory]
    [Trait("Category", "Allocation")]
    [InlineData(TraceMode.BlackAndWhite, 20)]
    [InlineData(TraceMode.Grayscale, 34)]
    [InlineData(TraceMode.Color, 34)]
    [InlineData(TraceMode.Logo, 235)]
    [InlineData(TraceMode.Centerline, 70)]
    public void AllocationsPerPixelStayWithinBudget(TraceMode mode, double budget)
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("EGAMI_ALLOCATION_TESTS") == "1", "Allocation budgets run on demand: set EGAMI_ALLOCATION_TESTS=1.");
        TraceOptions options = mode == TraceMode.Logo ? TraceOptions.Logo : new TraceOptions { Mode = mode, Colors = 4 };
        double Measure(int scale)
        {
            using Raster<Rgba8> raster = mode == TraceMode.Centerline ? CenterlineTraceTests.Drawing(scale * 1.25f).Raster : ColorTraceTests.Logo(scale);
            raster.Trace(options);
            long fewest = long.MaxValue;
            for (int i = 0; i < 3; i++)
            {
                long before = GC.GetTotalAllocatedBytes(precise: true);
                raster.Trace(options);
                fewest = Math.Min(fewest, GC.GetTotalAllocatedBytes(precise: true) - before);
            }

            return fewest / (double)(raster.Width * raster.Height);
        }

        double small = Measure(4), large = Measure(8);
        TestContext.Current.TestOutputHelper?.WriteLine($"{mode}: {small:F1} and {large:F1} bytes a pixel");

        // The label maps, distance fields and region tables grow with the image; nothing else does, so a larger image allocates no
        // more per pixel.
        Assert.True(large < small * 1.15, $"{large:F1} bytes a pixel at 8×, {small:F1} at 4×.");
        Assert.True(large < budget, $"{large:F1} bytes a pixel, over the budget of {budget}.");
    }
}
