using System.Numerics;

using BenchmarkDotNet.Running;

using Egami;
using Egami.Drawing;
using Egami.Pixels;
using Egami.Tests.Memory;

// Run all:      dotnet run -c Release --project benchmarks/Egami.Drawing.Benchmarks -- --filter "*"
// Run a class:  dotnet run -c Release --project benchmarks/Egami.Drawing.Benchmarks -- --filter "*Fill*"
// Peak memory of cells and clip masks, with the Phase 2.2 harness: -- --peak
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
