using System.Runtime.CompilerServices;

using Egami.Pixels;

namespace Egami.Tests.Memory;

public class PixelMemoryUsageTests
{
    [Fact]
    public void CountsBlocksUntilTheyAreDisposed()
    {
        PooledPixelMemorySource source = new() { PoolThresholdBytes = 1000 };
        EgamiContext context = new() { Memory = source };
        PixelMemoryUsage usage = source.Usage;
        Assert.Equal((0, 0), (usage.LiveBlocks, usage.LiveBytes));

        Raster<Rgba8> small = Raster.Create<Rgba8>(10, 10, context);
        Raster<Rgba8> large = Raster.Create<Rgba8>(100, 100, context);
        Assert.Equal(2, usage.LiveBlocks);
        Assert.Equal((10 * 10 * 4) + (100 * 100 * 4), usage.LiveBytes);

        small.Dispose();
        large.Dispose();
        large.Dispose();
        Assert.Equal((0, 0), (usage.LiveBlocks, usage.LiveBytes));
    }

    [Fact]
    public void ReportsBlocksCollectedWithoutBeingDisposed()
    {
        PooledPixelMemorySource source = new() { PoolThresholdBytes = 1000 };
        PixelMemoryUsage usage = source.Usage;
        List<PixelMemoryLeakEventArgs> leaks = [];
        usage.LeakDetected += (_, leak) =>
        {
            lock (leaks)
            {
                leaks.Add(leak);
            }
        };
        usage.CaptureStackTraces = true;
        usage.TrackLeaks = true;

        Forget(source, 20, 20);
        Raster<Rgba8> kept = Raster.Create<Rgba8>(4, 4, new EgamiContext { Memory = source });
        for (int i = 0; i < 10 && leaks.Count == 0; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.Single(leaks);
        Assert.Equal(20 * 20 * 4, leaks[0].Bytes);
        Assert.Contains(nameof(Forget), leaks[0].StackTrace, StringComparison.Ordinal);
        Assert.Equal(1, usage.LiveBlocks);
        kept.Dispose();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Assert.Single(leaks);
        Assert.Equal(0, usage.LiveBlocks);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Forget(PooledPixelMemorySource source, int width, int height)
    {
        _ = Raster.Create<Rgba8>(width, height, new EgamiContext { Memory = source });
    }
}
