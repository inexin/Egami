using System.Numerics;

using BenchmarkDotNet.Running;

using Egami;
using Egami.Drawing;
using Egami.Pixels;
using Egami.Tests.Memory;

// Run all:      dotnet run -c Release --project benchmarks/Egami.Drawing.Benchmarks -- --filter "*"
// Run a class:  dotnet run -c Release --project benchmarks/Egami.Drawing.Benchmarks -- --filter "*Fill*"
// Peak memory of cells and clip masks, with the Phase 2.2 harness: -- --peak
// Quick tracing timings, the median of five runs per mode and size, the bytes the last run allocated (all threads) and a
// hash of each trace's output: -- --time-trace
if (args is ["--time-trace", ..])
{
    TimeTrace(args.Length > 1 ? args[1] : null);
    return;
}

if (args is ["--peak"])
{
    Peak("4000x4000 star fill", 4000, 4000, canvas => canvas.FillPath(Egami.Drawing.Path.Star(new Vector2(2000, 2000), 1990, 800, 9, 7), Rgba8.Black));
    Peak("4000x4000 round clip and fill", 4000, 4000, canvas =>
    {
        canvas.ClipPath(Egami.Drawing.Path.Circle(new Vector2(2000, 2000), 1900));
        canvas.FillRect(new RectF(0, 0, 4000, 4000), Rgba8.Black);
    });
    Peak("20000x2000 hairline diagonal", 20000, 2000, canvas => canvas.DrawLine(new Vector2(0, 0), new Vector2(20000, 2000), Stroke.Hairline, Rgba8.Black));
    Peak("20000x2000 thick diagonal", 20000, 2000, canvas => canvas.DrawLine(new Vector2(0, 0), new Vector2(20000, 2000), new Stroke(9), Rgba8.Black));
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

static void TimeTrace(string? only)
{
    foreach (Egami.Drawing.Tracing.TraceMode mode in Enum.GetValues<Egami.Drawing.Tracing.TraceMode>())
    {
        if (only is not null && !mode.ToString().Equals(only, StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        foreach (int width in (int[])[1000, 3000])
        {
            using Raster<Rgba8> scene = Egami.Benchmarks.TraceScenes.For(mode, width);
            Egami.Drawing.Tracing.TraceOptions options = Egami.Benchmarks.TraceScenes.Options(mode);
            Egami.Drawing.Tracing.TraceResult result = Egami.Drawing.Tracing.RasterTraceExtensions.Trace(scene, options);
            double[] times = new double[5];
            for (int i = 0; i < times.Length; i++)
            {
                long allocated = GC.GetTotalAllocatedBytes(precise: true);
                System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
                result = Egami.Drawing.Tracing.RasterTraceExtensions.Trace(scene, options);
                times[i] = clock.Elapsed.TotalMilliseconds;
                if (i == times.Length - 1)
                {
                    Array.Sort(times);
                    Console.WriteLine($"{mode,-13} {scene.Width}x{scene.Height}: {times[2],8:F1} ms  {(GC.GetTotalAllocatedBytes(precise: true) - allocated) / 1048576.0,7:F1} MB  {result.Shapes.Count,4} shapes  hash {Egami.Benchmarks.TraceScenes.Hash(result):x16}");
                }
            }
        }
    }
}

// The peak includes the raster's own pixels; what drawing adds is the peak less the raster.
static void Peak(string name, int width, int height, Action<Canvas> draw)
{
    SafetyLimits limits = SafetyLimits.Default with { MaxPixels = 1L << 32, MaxTotalBytes = 1L << 36 };
    MemoryPeak peak = PeakMemory.Measure(
        context =>
        {
            using Raster<Rgba8> raster = Raster.Create<Rgba8>(width, height, context);
            using RasterCanvas<Rgba8> canvas = raster.CreateCanvas();
            draw(canvas);
        },
        limits);
    long pixels = (long)width * height * 4;
    Console.WriteLine($"{name}: {peak}; drawing added {peak.PeakPixelBytes - pixels:N0} bytes over the {pixels:N0}-byte raster");
}
