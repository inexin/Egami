using Egami.Formats.Png;
using Egami.Pixels;

namespace Egami.Tests;

/// <summary>
/// Managed allocations must not grow with image size: pixel memory comes from the memory source and row buffers from pools,
/// so only a fixed handful of small objects per call reach the GC.
/// </summary>
public class AllocationTests
{
    [Theory]
    [InlineData(PngColorType.Rgba, 8)]
    [InlineData(PngColorType.Gray, 2)]
    [InlineData(PngColorType.Palette, 4)]
    [InlineData(PngColorType.Rgb, 16)]
    public void PngSaveAndLoadAllocateTheSameForTenRowsAndThousands(PngColorType colorType, int depth)
    {
        PngWriterOptions options = new() { ColorType = colorType, BitDepth = depth };
        using Raster<Rgba8> small = Raster.Create(300, 10, Rgba8.Red);
        using Raster<Rgba8> large = Raster.Create(300, 3000, Rgba8.Red);
        byte[] smallFile = Save(small, options);
        byte[] largeFile = Save(large, options);

        long saveSmall = Allocated(() => Save(small, options, Stream.Null));
        long saveLarge = Allocated(() => Save(large, options, Stream.Null));
        Assert.True(saveLarge - saveSmall < 1024, $"Saving 3,000 rows allocated {saveLarge:N0} bytes against {saveSmall:N0} for 10 rows.");

        long loadSmall = Allocated(() => Raster.Load(smallFile).Dispose());
        long loadLarge = Allocated(() => Raster.Load(largeFile).Dispose());
        Assert.True(loadLarge - loadSmall < 1024, $"Loading 3,000 rows allocated {loadLarge:N0} bytes against {loadSmall:N0} for 10 rows.");
    }

    /// <summary>
    /// Returns the fewest bytes one call allocated over several tries. Other tests run in parallel and can empty the shared array
    /// pool, so an occasional call allocates a fresh buffer; the minimum shows what the code itself allocates.
    /// </summary>
    private static long Allocated(Action action)
    {
        for (int i = 0; i < 3; i++)
        {
            action();
        }

        long fewest = long.MaxValue;
        for (int i = 0; i < 10; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            action();
            fewest = Math.Min(fewest, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        return fewest;
    }

    private static byte[] Save(Raster raster, PngWriterOptions options, Stream? output = null)
    {
        if (output is not null)
        {
            raster.Save(output, PngFormat.Info, options);
            return [];
        }

        using MemoryStream stream = new();
        raster.Save(stream, PngFormat.Info, options);
        return stream.ToArray();
    }
}
