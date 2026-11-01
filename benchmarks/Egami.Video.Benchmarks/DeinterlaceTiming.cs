using System.Diagnostics;
using System.Globalization;

using Egami.Video.Filters;

namespace Egami.Video.Benchmarks;

/// <summary>
/// The Video 1.2.0 deinterlace speed gate: time per frame of <see cref="Deinterlacer"/> on one thread over the frames of a
/// file held in memory, the best of five runs, for comparison with FFmpeg's yadif -threads 1 (tools/video/check-deinterlace.py).
/// Every frame is deinterlaced. --bob times Bob, --field the field rate, --runs=N runs N times.
/// </summary>
internal static class DeinterlaceTiming
{
    internal static void Run(string[] args)
    {
        int runs = args.FirstOrDefault(f => f.StartsWith("--runs=", StringComparison.Ordinal)) is { } r
            ? int.Parse(r["--runs=".Length..], CultureInfo.InvariantCulture) : 5;
        DeinterlacerOptions options = new()
        {
            Mode = args.Contains("--bob") ? DeinterlaceMode.Bob : DeinterlaceMode.Adaptive,
            Rate = args.Contains("--field") ? DeinterlaceRate.Field : DeinterlaceRate.Frame,
            Frames = DeinterlaceFrames.All,
        };
        MediaContext context = MediaContext.Default with { Egami = EgamiContext.Default with { Memory = new PooledPixelMemorySource(), MaxParallelism = 1 } };
        foreach (string file in args.Where(f => !f.StartsWith("--", StringComparison.Ordinal)))
        {
            List<YuvVideoFrame> frames = [];
            using (VideoReader reader = VideoReader.Open(file))
            {
                frames.AddRange(reader.ReadFrames());
            }

            double best = double.MaxValue;
            long allocated = 0;
            int outputs = 0;
            for (int run = 0; run < runs; run++)
            {
                using Deinterlacer deinterlacer = new(options, context);
                outputs = 0;
                long before = GC.GetAllocatedBytesForCurrentThread();
                long start = Stopwatch.GetTimestamp();
                foreach (YuvVideoFrame frame in frames)
                {
                    deinterlacer.Send(frame);
                    while (deinterlacer.TryReceive(out YuvVideoFrame? output))
                    {
                        output.Dispose();
                        outputs++;
                    }
                }

                deinterlacer.Flush();
                while (deinterlacer.TryReceive(out YuvVideoFrame? output))
                {
                    output.Dispose();
                    outputs++;
                }

                best = Math.Min(best, Stopwatch.GetElapsedTime(start).TotalMilliseconds / frames.Count);
                allocated = (GC.GetAllocatedBytesForCurrentThread() - before) / frames.Count;
            }

            YuvRaster first = frames[0].Raster;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{Path.GetFileName(file)}: {first.Width}x{first.Height} {first.ChromaFormat} {first.BitDepth}-bit, {frames.Count} frames in, " +
                $"{outputs} out, {options.Mode} {best:F2} ms a frame, {allocated} bytes allocated a frame"));
            frames.ForEach(f => f.Dispose());
        }
    }
}
