using System.Diagnostics;
using System.Runtime.InteropServices;

using Egami.Formats.Webp;
using Egami.Pixels;

namespace Egami.Benchmarks;

/// <summary>
/// The Phase 2.3.5 speed gate: decode time per frame of VP8 IVF files (tools/make-vp8-references.py --large), the best of five
/// runs, at one thread and at all cores, for comparison with vpxdec's times in timing.json. And the Phase 2.4.5 profile
/// (<see cref="RunStages"/>): where one thread's time goes, stage by stage.
/// </summary>
internal static partial class Vp8Timing
{
    internal static void Run(string[] files)
    {
        // --one-thread times one thread only, for profiling.
        int[] counts = files.Contains("--one-thread") ? [1] : [1, Environment.ProcessorCount];
        foreach (string file in files.Where(f => !f.StartsWith("--", StringComparison.Ordinal)))
        {
            List<byte[]> packets = ReadIvf(file);
            foreach (int threads in counts)
            {
                double best = double.MaxValue;
                for (int run = 0; run < 5; run++)
                {
                    using Vp8VideoDecoder video = new(threads);
                    long start = Stopwatch.GetTimestamp();
                    foreach (byte[] packet in packets)
                    {
                        video.Decode(packet, 0, packet.Length);
                    }

                    best = Math.Min(best, Stopwatch.GetElapsedTime(start).TotalMilliseconds / packets.Count);
                }

                Console.WriteLine($"{Path.GetFileName(file)}: {best:F2} ms a packet on {threads} thread{(threads == 1 ? "" : "s")}");
            }
        }
    }

    /// <summary>
    /// Times one thread's decode of VP8 IVF files and lossy WebP stills stage by stage (<see cref="Vp8StageTimes"/>), the best of five
    /// runs, with the bytes allocated per frame after the first. The stages need a build with the timers:
    /// dotnet build -c Release -p:Vp8StageTimers=true; without them only the totals print.
    /// </summary>
    internal static void RunStages(string[] files)
    {
#if !VP8_STAGE_TIMERS
        Console.WriteLine("Built without -p:Vp8StageTimers=true: totals only.");
#endif
        foreach (string file in files)
        {
            bool still = file.EndsWith(".webp", StringComparison.OrdinalIgnoreCase);
            List<byte[]> packets = still ? [File.ReadAllBytes(file)] : ReadIvf(file);

            // A still is decoded 20 times a run, so a run lasts about as long as a clip's.
            int count = still ? 20 : packets.Count;
            EgamiContext context = EgamiContext.Default with { MaxParallelism = 1 };
            double best = double.MaxValue;
            double bestCycles = double.MaxValue;
            long[] stages = new long[(int)Vp8Stage.Count];
            long allocated = 0;
            for (int run = 0; run < 5; run++)
            {
                using Vp8VideoDecoder video = new(1);
                Vp8StageTimes.Start();
                long bytes = 0;
                long cycles = ThreadCycles();
                long start = Stopwatch.GetTimestamp();
                for (int i = 0; i < count; i++)
                {
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    if (still)
                    {
                        using Raster<Rgba8> raster = Raster.Load<Rgba8>(packets[0], context);
                    }
                    else
                    {
                        video.Decode(packets[i], 0, packets[i].Length);
                    }

                    if (i > 0 || still)
                    {
                        bytes += GC.GetAllocatedBytesForCurrentThread() - before;
                    }
                }

                double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds / count;
                bestCycles = Math.Min(bestCycles, (ThreadCycles() - cycles) / (double)count);
                if (ms < best)
                {
                    best = ms;
                    Vp8StageTimes.Ticks.CopyTo(stages, 0);
                    allocated = bytes / Math.Max(1, still ? count : count - 1);
                }
            }

            Console.WriteLine($"{Path.GetFileName(file)}: {best:F3} ms a {(still ? "decode" : "packet")} on 1 thread, {allocated:N0} bytes allocated a {(still ? "decode" : "frame after the first")}, {bestCycles / 1e6:F3} Mcycles");
            double attributed = 0;
            for (int stage = 0; stage < stages.Length; stage++)
            {
                double ms = stages[stage] * 1000.0 / Stopwatch.Frequency / count;
                attributed += ms;
                if (stages[stage] != 0)
                {
                    Console.WriteLine($"  {(Vp8Stage)stage,-12} {ms,8:F3} ms {100 * ms / best,5:F1}%");
                }
            }

            if (attributed > 0)
            {
                Console.WriteLine($"  {"(between)",-12} {best - attributed,8:F3} ms {100 * (best - attributed) / best,5:F1}%");
            }
        }
    }

    /// <summary>
    /// Returns the cycles this thread has run (Windows' QueryThreadCycleTime), which time spent waiting for the CPU leaves out:
    /// steadier than wall time on a busy machine. 0 elsewhere.
    /// </summary>
    private static long ThreadCycles() => OperatingSystem.IsWindows() && QueryThreadCycleTime(GetCurrentThread(), out ulong cycles) ? (long)cycles : 0;

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryThreadCycleTime(nint thread, out ulong cycles);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentThread();

    /// <summary>Writes the 2.3.1 WebP benchmark pictures as PNG, for cwebp to make the lossy stills the profile decodes.</summary>
    internal static void WritePictures(string directory)
    {
        Directory.CreateDirectory(directory);
        using Raster<Rgba8> photo = BenchImages.Photo(2048, 1536);
        photo.Save(Path.Combine(directory, "photo.png"));
        using Raster<Rgb8> screenshot = BenchImages.Screenshot(1920, 1080);
        screenshot.Save(Path.Combine(directory, "screenshot.png"));
    }

    private static List<byte[]> ReadIvf(string path)
    {
        byte[] file = File.ReadAllBytes(path);
        int at = BitConverter.ToUInt16(file, 6);
        List<byte[]> packets = [];
        while (at + 12 <= file.Length)
        {
            int size = BitConverter.ToInt32(file, at);
            packets.Add(file.AsSpan(at + 12, size).ToArray());
            at += 12 + size;
        }

        return packets;
    }
}
