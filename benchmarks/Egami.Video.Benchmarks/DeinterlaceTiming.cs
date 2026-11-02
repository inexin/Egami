using System.Diagnostics;
using System.Globalization;

using Egami.Pixels;
using Egami.Video.Filters;

namespace Egami.Video.Benchmarks;

/// <summary>
/// The Video 1.2.0 deinterlace speed gate: time per frame of <see cref="Deinterlacer"/> on one thread over the frames of a
/// file held in memory, the best of five runs, for comparison with FFmpeg's yadif -threads 1 (tools/video/check-deinterlace.py).
/// Every frame is deinterlaced. --bob times Bob, --field the field rate, --runs=N runs N times. <see cref="Look"/> writes frames
/// of a real interlaced file as PNGs for a review by eye.
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

    /// <summary>
    /// --look-deinterlace: --count=N frames (default 6) of a file from --at=seconds (default 10) as PNGs in --out=folder: woven as
    /// decoded, adaptive and bob at the frame rate, and adaptive at the field rate. H.264 and HEVC decode too.
    /// </summary>
    internal static void Look(string[] args)
    {
        string Value(string name, string fallback) => args.FirstOrDefault(a => a.StartsWith(name, StringComparison.Ordinal)) is { } v ? v[name.Length..] : fallback;
        double at = double.Parse(Value("--at=", "10"), CultureInfo.InvariantCulture);
        int count = int.Parse(Value("--count=", "6"), CultureInfo.InvariantCulture);
        string folder = Directory.CreateDirectory(Value("--out=", "look")).FullName;
        MediaContext context = MediaContext.Default.WithH264().WithHevc();
        foreach (string file in args.Where(f => !f.StartsWith("--", StringComparison.Ordinal)))
        {
            string stem = Path.GetFileNameWithoutExtension(file);
            (string Label, DeinterlacerOptions? Options)[] looks =
            [
                ("woven", null),
                ("adaptive", new DeinterlacerOptions { Frames = DeinterlaceFrames.All }),
                ("bob", new DeinterlacerOptions { Mode = DeinterlaceMode.Bob, Frames = DeinterlaceFrames.All }),
                ("adaptive-field", new DeinterlacerOptions { Rate = DeinterlaceRate.Field, Frames = DeinterlaceFrames.All }),
            ];
            foreach ((string label, DeinterlacerOptions? options) in looks)
            {
                using VideoReader reader = VideoReader.Open(file, context: context);
                reader.Seek(MediaTime.FromSeconds(at));
                using Deinterlacer? deinterlacer = options is null ? null : new Deinterlacer(reader.Track, options, context);
                int limit = options?.Rate == DeinterlaceRate.Field ? 2 * count : count, written = 0;
                Console.WriteLine($"{stem}: {reader.Track.Width}x{reader.Track.Height}, track {reader.Track.FieldOrder}");
                void Save(YuvVideoFrame frame)
                {
                    using (frame)
                    using (Raster<Rgb8> image = frame.ToRaster<Rgb8>())
                    {
                        image.Save(Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"{stem}-{label}-{written++:00}.png")));
                    }
                }

                while (written < limit && reader.ReadFrame() is { } frame)
                {
                    if (deinterlacer is null)
                    {
                        Console.WriteLine($"  frame at {frame.Timestamp.Seconds:F3}: {frame.FieldOrder}");
                        Save(frame);
                        continue;
                    }

                    using (frame)
                    {
                        deinterlacer.Send(frame);
                    }

                    while (written < limit && deinterlacer.TryReceive(out YuvVideoFrame? output))
                    {
                        Save(output);
                    }
                }
            }
        }

        Console.WriteLine($"wrote {folder}");
    }
}
