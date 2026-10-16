using System.Diagnostics;

namespace Egami.Benchmarks;

/// <summary>
/// The Phase 2.9.3 speed gate: decode time of JPEG XL files read into memory first, the best of ten runs after ten warm-up runs
/// (so the JIT has optimized the hot loops, as a native decoder starts optimized), at one thread and at all cores, for comparison
/// with djxl's (tools/time-jxl-gate.py).
/// </summary>
internal static class JxlTiming
{
    internal static void Run(string[] files)
    {
        int[] counts = files.Contains("--one-thread") ? [1] : [1, Environment.ProcessorCount];
        foreach (string file in files.Where(f => !f.StartsWith("--", StringComparison.Ordinal)))
        {
            byte[] data = File.ReadAllBytes(file);
            foreach (int threads in counts)
            {
                EgamiContext context = EgamiContext.Default with { MaxParallelism = threads };
                for (int warm = 0; warm < 10; warm++)
                {
                    using (Raster.Load(data, context))
                    {
                    }
                }

                double best = double.MaxValue;
                for (int run = 0; run < 10; run++)
                {
                    long start = Stopwatch.GetTimestamp();
                    using (Raster.Load(data, context))
                    {
                    }

                    best = Math.Min(best, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                }

                Console.WriteLine($"{file}: {best:F2} ms an image on {threads} thread{(threads == 1 ? "" : "s")}");
            }
        }
    }
}
