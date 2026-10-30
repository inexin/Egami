using System.Diagnostics;

using Egami.Formats.Jpeg;
using Egami.Formats.Png;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Benchmarks;

/// <summary>
/// Step 2.4.8's image scenarios, timed with a stopwatch: the fastest of --runs=N runs (default 5) in milliseconds, at one thread
/// and at all cores, or at --one-thread or --threads=N only. tools/video/count-instructions.py runs one scenario with two run
/// counts under perf to count instructions per operation. JPEG and PNG decode and encode, resize, compose, YUV conversion and
/// the byte blur, on the benchmark photo (4000 × 3000) unless the name says otherwise. Run: <c>-- --time-image [name...]</c>,
/// where a name selects every scenario it starts; --list prints the names.
/// </summary>
internal static class ImageTiming
{
    private static readonly Lazy<Raster<Rgba8>> PhotoRgba = new(() => BenchImages.Photo(4000, 3000));
    private static readonly Lazy<Raster<Rgb8>> PhotoRgb = new(() => PhotoRgba.Value.As<Rgb8>());
    private static readonly Lazy<Raster<Rgb8>> ScreenshotRgb = new(() => BenchImages.Screenshot(1920, 1080));
    private static readonly Lazy<Raster<Rgba8>> Logo = new(() => FilterTiming.AlphaLogo(1000, 1000));
    private static readonly Lazy<Raster<Rgba8>> Hd = new(() => BenchImages.Photo(1920, 1080));

    private static readonly Dictionary<string, Func<EgamiContext, Action>> Scenarios = Create();

    internal static void Run(string[] args)
    {
        if (args.Contains("--list"))
        {
            Console.WriteLine(string.Join(Environment.NewLine, Scenarios.Keys));
            return;
        }

        int runs = args.FirstOrDefault(a => a.StartsWith("--runs=", StringComparison.Ordinal)) is { } r
            ? int.Parse(r["--runs=".Length..], System.Globalization.CultureInfo.InvariantCulture) : 5;
        int[] counts = args.Contains("--one-thread") ? [1] : args.FirstOrDefault(a => a.StartsWith("--threads=", StringComparison.Ordinal)) is { } t
            ? [int.Parse(t["--threads=".Length..], System.Globalization.CultureInfo.InvariantCulture)]
            : [1, Environment.ProcessorCount];
        string[] names = [.. args.Where(a => !a.StartsWith("--", StringComparison.Ordinal))];
        foreach ((string name, Func<EgamiContext, Action> make) in Scenarios)
        {
            if (names.Length > 0 && !names.Any(n => name.StartsWith(n, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            foreach (int threads in counts)
            {
                Action operation = make(EgamiContext.Default with { MaxParallelism = threads });
                double best = double.MaxValue;
                long allocated = 0;
                for (int run = 0; run < runs; run++)
                {
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    long start = Stopwatch.GetTimestamp();
                    operation();
                    best = Math.Min(best, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                    allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                }

                Console.WriteLine($"{name,-28} {threads,2} thread{(threads == 1 ? " " : "s")} {best,9:F2} ms {allocated,12} bytes");
            }
        }
    }

    private static Dictionary<string, Func<EgamiContext, Action>> Create()
    {
        Dictionary<string, Func<EgamiContext, Action>> scenarios = [];
        foreach (string kind in (string[])["420", "444", "progressive", "gray"])
        {
            scenarios[$"jpeg-decode-{kind}"] = context =>
            {
                byte[] file = BenchFiles.Jpeg($"photo-{kind}.jpg");
                return () => Raster.Load(file, context).Dispose();
            };
        }

        AddEncode(scenarios, "jpeg-encode-q75-420", PhotoRgb, JpegFormat.Info, new JpegWriterOptions { Quality = 75 });
        AddEncode(scenarios, "jpeg-encode-q90-444", PhotoRgb, JpegFormat.Info, new JpegWriterOptions { Quality = 90, Subsampling = ChromaSubsampling.Subsample444 });
        AddEncode(scenarios, "jpeg-encode-q90-444-optimized", PhotoRgb, JpegFormat.Info,
            new JpegWriterOptions { Quality = 90, Subsampling = ChromaSubsampling.Subsample444, OptimizeHuffman = true });
        AddEncode(scenarios, "jpeg-encode-progressive", PhotoRgb, JpegFormat.Info, new JpegWriterOptions { Quality = 75, Progressive = true });
        foreach (string scenario in BenchImages.DecodeScenarios)
        {
            scenarios[$"png-decode-{scenario}"] = context =>
            {
                byte[] file = BenchImages.Png(scenario);
                return () => Raster.Load(file, context).Dispose();
            };
        }

        AddEncode(scenarios, "png-encode-photo-6", PhotoRgba, PngFormat.Info, new PngWriterOptions { CompressionLevel = 6 });
        AddEncode(scenarios, "png-encode-screenshot-6", ScreenshotRgb, PngFormat.Info, new PngWriterOptions { CompressionLevel = 6 });
        scenarios["resize-bicubic-800"] = context => Derive(PhotoRgba, context, ops => ops.Resize(800, 600));
        scenarios["resize-linear-800"] = context => Derive(PhotoRgba, context, ops => ops.Resize(800, 600, ResampleFilter.Bicubic, ColorSpace.Linear));
        scenarios["compose-logo-half"] = context => Derive(PhotoRgba, context, ops => ops.DrawImage(Logo.Value, new Point(1700, 1200), 0.5f));
        scenarios["rotate-15"] = context => Derive(PhotoRgba, context, ops => ops.Rotate(15));
        scenarios["median-2"] = context => Derive(PhotoRgba, context, ops => ops.Median(2));
        scenarios["blur-fast-20"] = context => Derive(PhotoRgba, context, ops => ops.Blur(20, ColorSpace.Encoded, BlurMethod.Fast));
        scenarios["yuv420-to-rgba8-1080p"] = context =>
        {
            Raster<Rgba8> rgba = Raster.Create<Rgba8>(1920, 1080, context);
            YuvRaster<byte> yuv = YuvRaster.FromRaster(Hd.Value, ChromaFormat.Yuv420, YuvColor.Bt709Limited);
            return () => yuv.CopyTo(rgba);
        };
        scenarios["rgba8-to-yuv420-1080p"] = context =>
        {
            Raster<Rgba8> rgba = Copy(Hd.Value, context);
            YuvRaster<byte> yuv = YuvRaster.FromRaster(Hd.Value, ChromaFormat.Yuv420, YuvColor.Bt709Limited);
            return () => yuv.CopyFrom(rgba);
        };
        return scenarios;
    }

    private static void AddEncode<TPixel>(Dictionary<string, Func<EgamiContext, Action>> scenarios, string name, Lazy<Raster<TPixel>> image,
        Formats.FormatInfo format, Formats.WriterOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        scenarios[name] = context =>
        {
            Raster<TPixel> copy = Copy(image.Value, context);
            return () => copy.Save(Stream.Null, format, options);
        };
    }

    private static Action Derive(Lazy<Raster<Rgba8>> image, EgamiContext context, Action<RasterOps> chain)
    {
        Raster<Rgba8> copy = Copy(image.Value, context);
        return () => copy.Derive(chain).Dispose();
    }

    private static Raster<TPixel> Copy<TPixel>(Raster<TPixel> image, EgamiContext context)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Raster<TPixel> copy = Raster.Create<TPixel>(image.Width, image.Height, context);
        for (int y = 0; y < image.Height; y++)
        {
            image.Row(y).CopyTo(copy.Row(y));
        }

        return copy;
    }
}
