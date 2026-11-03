using System.Diagnostics;
using System.Globalization;

using Egami.Formats.Jxl;

namespace Egami.Benchmarks;

/// <summary>
/// The Phase 2.9.3 and 2.9.2 speed gates: Raster.Load time of JPEG XL or AVIF files read into memory first, the best of ten runs after ten warm-up runs
/// (so the JIT has optimized the hot loops, as a native decoder starts optimized), at one thread and at all cores, for comparison
/// with djxl's (tools/time-jxl-gate.py) or libavif's (tools/time-avif-gate.py).
/// </summary>
internal static class JxlTiming
{
    internal static void Run(string[] files)
    {
        if (files.Contains("--md5"))
        {
            // The decoded pixels' MD5, to check a change decodes the same: -- --time-avif --md5 file.avif.
            foreach (string file in files.Where(f => !f.StartsWith("--", StringComparison.Ordinal)))
            {
                using Raster raster = Raster.Load(File.ReadAllBytes(file), EgamiContext.Default with { MaxParallelism = 1 });
                using Raster<Pixels.Rgba16> pixels = raster.As<Pixels.Rgba16>();
                using System.Security.Cryptography.IncrementalHash md5 = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.MD5);
                for (int y = 0; y < pixels.Height; y++)
                {
                    md5.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixels.Row(y)));
                }

                Console.WriteLine($"{file}: MD5={Convert.ToHexStringLower(md5.GetHashAndReset())}");
            }

            return;
        }

        // --one-thread times one thread only; --runs=N runs N times with no separate warm-up (tools/video/count-instructions.py
        // counts the difference of two run counts); EGAMI_JXL_STAGES=1 prints the bytes one decode allocates and, in a build with
        // -p:JxlStageTimers=true, where one thread's time goes.
        int[] counts = files.Contains("--one-thread") ? [1] : [1, Environment.ProcessorCount];
        int? runs = Runs(files);
        foreach (string file in files.Where(f => !f.StartsWith("--", StringComparison.Ordinal)))
        {
            byte[] data = File.ReadAllBytes(file);
            foreach (int threads in counts)
            {
                EgamiContext context = EgamiContext.Default with { MaxParallelism = threads };
                void Decode()
                {
                    using (Raster.Load(data, context))
                    {
                    }
                }

                double best = Timed(runs, Decode);
                Console.WriteLine($"{file}: {best:F2} ms an image on {threads} thread{(threads == 1 ? "" : "s")}");
                if (Stages && threads == 1)
                {
                    PrintStages(Decode);
                }
            }
        }
    }

    /// <summary>
    /// Times encoding each input image as JPEG XL with <c>--setting=</c>: d<i>distance</i>e<i>effort</i> for lossy (with a
    /// trailing p for progressive) or ll-e<i>effort</i> for lossless, as roadmap 2.4.11's profile runs them; the best of
    /// <c>--runs=N</c> runs (default ten after ten warm-up runs), at one thread (<c>--one-thread</c>) or at one and all cores.
    /// </summary>
    internal static void EncodeTimed(string[] args)
    {
        string setting = args.FirstOrDefault(a => a.StartsWith("--setting=", StringComparison.Ordinal))?["--setting=".Length..] ?? "d1e7";
        JxlWriterOptions options = Options(setting);
        int[] counts = args.Contains("--one-thread") ? [1] : [1, Environment.ProcessorCount];
        int? runs = Runs(args);
        foreach (string file in args.Where(f => !f.StartsWith("--", StringComparison.Ordinal)))
        {
            foreach (int threads in counts)
            {
                // The raster's context sets the encoder's threads.
                using Raster raster = Raster.Load(file, EgamiContext.Default with { MaxParallelism = threads });
                using MemoryStream output = new();
                void Encode()
                {
                    output.SetLength(0);
                    raster.Save(output, JxlFormat.Info, options);
                }

                double best = Timed(runs, Encode);
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{file} {setting}: {output.Length} bytes, {best:F2} ms on {threads} thread{(threads == 1 ? "" : "s")}"));
                if (Stages && threads == 1)
                {
                    PrintStages(Encode);
                }
            }
        }
    }

    /// <summary>Gets the writer options of an <see cref="EncodeTimed"/> setting.</summary>
    private static JxlWriterOptions Options(string setting)
    {
        if (setting.StartsWith("ll-e", StringComparison.Ordinal))
        {
            return new JxlWriterOptions { Lossless = true, Effort = int.Parse(setting[4..], CultureInfo.InvariantCulture) };
        }

        bool progressive = setting.EndsWith('p');
        string[] parts = setting.TrimEnd('p')[1..].Split('e');
        return new JxlWriterOptions
        {
            Lossless = false,
            Distance = float.Parse(parts[0], CultureInfo.InvariantCulture),
            Effort = int.Parse(parts[1], CultureInfo.InvariantCulture),
            Progressive = progressive,
        };
    }

    private static bool Stages => Environment.GetEnvironmentVariable("EGAMI_JXL_STAGES") == "1";

    private static int? Runs(string[] args) => args.FirstOrDefault(a => a.StartsWith("--runs=", StringComparison.Ordinal)) is { } r
        ? int.Parse(r["--runs=".Length..], CultureInfo.InvariantCulture) : null;

    /// <summary>Runs <paramref name="action"/> <paramref name="runs"/> times, or ten times after ten warm-up runs, and gives the fastest in milliseconds.</summary>
    private static double Timed(int? runs, Action action)
    {
        if (runs is null)
        {
            for (int warm = 0; warm < 10; warm++)
            {
                action();
            }
        }

        double best = double.MaxValue;
        for (int run = 0; run < (runs ?? 10); run++)
        {
            long start = Stopwatch.GetTimestamp();
            action();
            best = Math.Min(best, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        return best;
    }

    /// <summary>
    /// Prints the managed bytes one run allocates (on every thread) and, in a build with the stage timers, the milliseconds each
    /// stage takes, the fastest of five runs stage by stage. An encode's own draft decodes (the EPF and quantization searches)
    /// land in the decode stages, which an encode prints together as drafts.
    /// </summary>
    private static void PrintStages(Action action)
    {
        action();
        long before = GC.GetTotalAllocatedBytes(precise: true);
        action();
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  allocated {allocated / 1048576.0:F2} MiB a run"));
#if JXL_STAGE_TIMERS
        double[] best = new double[(int)JxlStage.Count];
        Array.Fill(best, double.MaxValue);
        for (int run = 0; run < 5; run++)
        {
            JxlStageTimes.Start();
            action();
            JxlStageTimes.Lap(JxlStage.Pixels);
            for (int s = 0; s < best.Length; s++)
            {
                best[s] = Math.Min(best[s], JxlStageTimes.Ticks[s] * 1000.0 / Stopwatch.Frequency);
            }
        }

        bool encode = best[(int)JxlStage.Input] > 0;
        double drafts = 0;
        for (int s = 0; s < best.Length; s++)
        {
            JxlStage stage = (JxlStage)s;
            if (encode && stage <= JxlStage.Pixels)
            {
                // The draft decodes, and the time after an encode's last lap.
                drafts += best[s];
                continue;
            }

            if (best[s] > 0.005)
            {
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {stage,-15} {best[s],9:F2} ms"));
            }
        }

        if (encode)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {"Drafts",-15} {drafts,9:F2} ms"));
        }
#else
        Console.WriteLine("  Built without -p:JxlStageTimers=true: no stages.");
#endif
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
