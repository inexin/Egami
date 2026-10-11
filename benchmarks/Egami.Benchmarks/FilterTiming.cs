using System.Diagnostics;

using Egami.Operations;
using Egami.Pixels;

namespace Egami.Benchmarks;

/// <summary>
/// The Phase 2.5.4 speed scenarios on the 4000 × 3000 benchmark photo, timed with a stopwatch at one thread and at all cores:
/// the fastest of several runs, in milliseconds. egami-compare times the same scenarios against the other libraries; this is
/// the quick check while a kernel changes. Run: <c>-- --time-filters [name...]</c>.
/// </summary>
internal static class FilterTiming
{
    internal static readonly Dictionary<string, Action<RasterOps>> Scenarios = new()
    {
        ["Blur-Exact-20"] = o => o.Blur(20),
        ["Blur-Fast-20"] = o => o.Blur(20, ColorSpace.Encoded, BlurMethod.Fast),
        ["Blur-Fast-60"] = o => o.Blur(60, ColorSpace.Encoded, BlurMethod.Fast),
        ["Median-2"] = o => o.Median(2),
        ["Median-10"] = o => o.Median(10),
    };

    internal static void Run(string[] names)
    {
        using Raster<Rgba8> photo = BenchImages.Photo(4000, 3000);
        foreach ((string name, Action<RasterOps> chain) in Scenarios)
        {
            if (names.Length > 0 && !names.Any(n => name.StartsWith(n, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            double one = Time(photo, chain, 1);
            double all = Time(photo, chain, Environment.ProcessorCount);
            Console.WriteLine($"{name,-24} 1 thread {one,8:F1} ms   {Environment.ProcessorCount} threads {all,8:F1} ms");
        }
    }

    private static double Time(Raster<Rgba8> photo, Action<RasterOps> chain, int threads)
    {
        using Raster<Rgba8> copy = Raster.Create<Rgba8>(photo.Width, photo.Height, EgamiContext.Default with { MaxParallelism = threads });
        for (int y = 0; y < photo.Height; y++)
        {
            photo.Row(y).CopyTo(copy.Row(y));
        }

        double best = double.MaxValue;
        for (int i = 0; i < 4; i++)
        {
            long start = Stopwatch.GetTimestamp();
            copy.Derive(chain).Dispose();
            best = Math.Min(best, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        return best;
    }
}
