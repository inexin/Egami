using System.Diagnostics;

using Egami.Formats.Av1;

namespace Egami.Benchmarks;

/// <summary>
/// The Phase 2.9.5 speed gate: decode time per temporal unit of AV1 IVF files (the 720p and 1080p clips in .cache/video/av1/large),
/// the best of five runs, at one thread and at all cores, for comparison with dav1d's.
/// </summary>
internal static class Av1Timing
{
    internal static void Run(string[] files)
    {
        // --one-thread times one thread only, for profiling.
        int[] counts = files.Contains("--one-thread") ? [1] : [1, Environment.ProcessorCount];
        foreach (string file in files.Where(f => !f.StartsWith("--", StringComparison.Ordinal)))
        {
            List<byte[]> units = Vp8Timing.ReadIvf(file);
            foreach (int threads in counts)
            {
                double best = double.MaxValue, bestCycles = double.MaxValue;
                for (int run = 0; run < 5; run++)
                {
                    long cycles = Vp8Timing.ThreadCycles();
                    Av1Stream stream = new("AV1") { Parallelism = threads };
                    long start = Stopwatch.GetTimestamp();
                    foreach (byte[] unit in units)
                    {
                        foreach (Av1Frame frame in stream.Decode(unit, 0, unit.Length))
                        {
                            Av1FilmGrainSynthesis.Apply(frame);
                        }
                    }

                    best = Math.Min(best, Stopwatch.GetElapsedTime(start).TotalMilliseconds / units.Count);
                    bestCycles = Math.Min(bestCycles, (Vp8Timing.ThreadCycles() - cycles) / 1e6 / units.Count);
                }

                Console.WriteLine($"{Path.GetFileName(file)}: {best:F2} ms a unit on {threads} thread{(threads == 1 ? "" : "s")}{(threads == 1 ? $", {bestCycles:F1} M cycles" : "")}");
            }
        }
    }
}
