using Egami.Formats.Jxl;
using Egami.Pixels;
using Egami.Tests.Memory;
using Egami.Tests.Pixels;

namespace Egami.Tests.Formats.Jxl;

/// <summary>
/// Lossless tree learning splits large nodes and subtrees over worker threads, each with working arrays of its own; these tests
/// check that an encode on eight threads allocates at most a quarter more than the same encode on one (roadmap 2.4.11: effort
/// 10 on many threads once allocated 1.7 times as much). They count every thread's allocations, so they run alone.
/// </summary>
[Trait("Category", AllocationMeter.Category)]
[Collection(ExhaustiveGroup.Name)]
public class JxlEncodeThreadAllocationTests
{
    /// <summary>The lossless efforts that learn a tree, and how much more eight threads may allocate for their subtrees' arrays.</summary>
    public static TheoryData<int, double> Efforts => AllocationMeter.Rows(new TheoryData<int, double>
    {
        { 5, 1.25 },
        { 7, 1.25 },
        { 10, 1.25 },
    });

    [Theory]
    [MemberData(nameof(Efforts))]
    [Trait("Category", "Slow")]
    public void EncodingOnThreadsAllocatesNoMoreThanOnOne(int effort, double slack)
    {
        AllocationMeter.SkipUnlessOnDemand();
        JxlWriterOptions options = new() { Lossless = true, Effort = effort };
        using Raster<Rgba8> one = Photo(512, 512, 1);
        using Raster<Rgba8> eight = Photo(512, 512, 8);
        long oneBytes = AllocationMeter.FewestOnAllThreads(() => one.Save(Stream.Null, JxlFormat.Info, options));
        long eightBytes = AllocationMeter.FewestOnAllThreads(() => eight.Save(Stream.Null, JxlFormat.Info, options));
        Assert.True(eightBytes <= oneBytes * slack, $"e{effort}: {eightBytes:N0} bytes on eight threads, {oneBytes:N0} on one.");
    }

    /// <summary>A smooth gradient with a fixed pseudo-random texture, its context set to <paramref name="threads"/> threads.</summary>
    private static Raster<Rgba8> Photo(int width, int height, int threads)
    {
        Raster<Rgba8> raster = Raster.Create<Rgba8>(width, height, EgamiContext.Default with { MaxParallelism = threads });
        uint seed = 12345;
        for (int y = 0; y < height; y++)
        {
            Span<Rgba8> row = raster.Row(y);
            for (int x = 0; x < width; x++)
            {
                seed = (seed * 1664525) + 1013904223;
                int noise = (int)(seed >> 27) - 16;
                row[x] = new Rgba8((byte)Math.Clamp((x * 255 / width) + noise, 0, 255), (byte)Math.Clamp((y * 255 / height) + noise, 0, 255), (byte)Math.Clamp(128 + noise, 0, 255), 255);
            }
        }

        return raster;
    }
}
