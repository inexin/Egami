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

    /// <summary>Encodes each input as lossless JPEG XL at <paramref name="effort"/>, printing the encode time (best of five runs after at least 20 warm-up runs and a second, at all cores or one thread).</summary>
    internal static void Encode(int effort, string[] pairs)
    {
        int threads = pairs.Contains("--one-thread") ? 1 : Environment.ProcessorCount;
        pairs = [.. pairs.Where(p => p != "--one-thread")];
        for (int i = 0; i + 1 < pairs.Length; i += 2)
        {
            using Raster raster = Raster.Load(pairs[i]);
            Egami.Formats.Jxl.JxlEncodeInput input = Egami.Formats.Jxl.JxlWriter.ToInput(raster, effort) with { Parallelism = threads };
            byte[] data = [];
            // Warm up until the JIT has optimized the loops: 20 runs, and at least a second.
            long warmStart = Stopwatch.GetTimestamp();
            for (int warm = 0; warm < 20 || Stopwatch.GetElapsedTime(warmStart).TotalSeconds < 1; warm++)
            {
                data = Egami.Formats.Jxl.JxlLosslessEncoder.Encode(input);
            }

            double best = double.MaxValue;
            for (int run = 0; run < 5; run++)
            {
                long start = Stopwatch.GetTimestamp();
                data = Egami.Formats.Jxl.JxlLosslessEncoder.Encode(input);
                best = Math.Min(best, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }

            File.WriteAllBytes(pairs[i + 1], data);
            using Raster back = Raster.Load(data);
            Egami.Formats.Jxl.JxlEncodeInput again = Egami.Formats.Jxl.JxlWriter.ToInput(back, effort);
            bool exact = input.Color.Count == again.Color.Count && input.Color.Zip(again.Color).All(p => p.First.AsSpan().SequenceEqual(p.Second))
                && (input.Alpha is null ? again.Alpha is null : again.Alpha is not null && input.Alpha.AsSpan().SequenceEqual(again.Alpha));
            Console.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{pairs[i]}: {data.Length} bytes in {best:F1} ms, {(exact ? "exact in Egami" : "DIFFERS IN EGAMI")}"));
        }
    }
}
