using System.Diagnostics;

using Egami.Operations;
using Egami.Pixels;

namespace Egami.Benchmarks;

/// <summary>
/// The Phase 2.5.4 speed scenarios on the 4000 × 3000 benchmark photo, timed with a stopwatch at one thread and at all cores:
/// the fastest of several runs, in milliseconds. egami-compare times the same scenarios against the other libraries; this is
/// the quick check while a kernel changes; the lighting scenarios run on a generated 2000 × 2000 alpha logo. Run:
/// <c>-- --time-filters [name...]</c>.
/// </summary>
internal static class FilterTiming
{
    internal static readonly Dictionary<string, Action<RasterOps>> Scenarios = new()
    {
        ["Blur-Exact-20"] = o => o.Blur(20),
        ["Blur-Fast-20"] = o => o.Blur(20, ColorSpace.Encoded, BlurMethod.Fast),
        ["Blur-Fast-60"] = o => o.Blur(60, ColorSpace.Encoded, BlurMethod.Fast),
        ["Median-2"] = o => o.Median(2),
        ["Median-10"] = o => o.Median(10),
        ["LensBlur-32"] = o => o.LensBlur(32),
        ["Bilateral-3"] = o => o.BilateralBlur(3, 0.1f),
        ["Bilateral-10"] = o => o.BilateralBlur(10, 0.1f),
        ["MotionBlur-30"] = o => o.MotionBlur(15, 5, 30),
        ["RotationalBlur-10"] = o => o.RotationalBlur(10),
        ["AddNoise-Gaussian"] = o => o.AddNoise(NoiseKind.Gaussian),
        ["Despeckle"] = o => o.Despeckle(),
        ["Emboss"] = o => o.Emboss(),
        ["Shade"] = o => o.Shade(),
    };

    /// <summary>The scenarios that light an alpha shape: 2000 × 2000 with alpha (<see cref="AlphaLogo"/>), as the design names them.</summary>
    internal static readonly Dictionary<string, Action<RasterOps>> AlphaScenarios = new()
    {
        ["Lighting-Distant"] = o => o.Lighting(new DistantLight(225, 40), LightingModel.Diffuse(), new LightingOptions { SurfaceScale = 4 }),
        ["Lighting-Spot"] = o => o.Lighting(new SpotLight(new System.Numerics.Vector3(400, 300, 600), new System.Numerics.Vector3(1200, 1400, 0), 4, 35), LightingModel.Specular(1, 20), new LightingOptions { SurfaceScale = 4 }),
    };

    internal static void Run(string[] names)
    {
        using Raster<Rgba8> photo = BenchImages.Photo(4000, 3000);
        RunAll(photo, Scenarios, names);
        using Raster<Rgba8> logo = AlphaLogo(2000, 2000);
        RunAll(logo, AlphaScenarios, names);
    }

    /// <summary>Soft-edged discs and a rounded square on transparent black, so alpha has edges and slopes to light.</summary>
    internal static Raster<Rgba8> AlphaLogo(int width, int height)
    {
        Raster<Rgba8> logo = Raster.Create<Rgba8>(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<Rgba8> row = logo.Row(y);
            for (int x = 0; x < width; x++)
            {
                double u = (x + 0.5) / width, v = (y + 0.5) / height;
                double square = Math.Max(Math.Abs(u - 0.35), Math.Abs(v - 0.4));
                double disc = Math.Sqrt(((u - 0.7) * (u - 0.7)) + ((v - 0.65) * (v - 0.65)));
                double a = Math.Max(Math.Clamp((0.25 - square) * 80, 0, 1), Math.Clamp((0.22 - disc) * 12, 0, 1));
                row[x] = new Rgba8((byte)(40 + (150 * u)), 110, (byte)(220 - (120 * v)), (byte)Math.Round(a * 255));
            }
        }

        return logo;
    }

    private static void RunAll(Raster<Rgba8> image, Dictionary<string, Action<RasterOps>> scenarios, string[] names)
    {
        foreach ((string name, Action<RasterOps> chain) in scenarios)
        {
            if (names.Length > 0 && !names.Any(n => name.StartsWith(n, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            double one = Time(image, chain, 1);
            double all = Time(image, chain, Environment.ProcessorCount);
            Console.WriteLine($"{name,-24} 1 thread {one,8:F1} ms   {Environment.ProcessorCount} threads {all,8:F1} ms");
        }
    }

    private static double Time(Raster<Rgba8> photo, Action<RasterOps> chain, int threads)
    {
        using Raster<Rgba8> copy = Raster.Create<Rgba8>(photo.Width, photo.Height, EgamiContext.Default with { MaxParallelism = threads });
        for (int y = 0; y < photo.Height; y++)
        {
            photo.Row(y).CopyTo(copy.Row(y));
        }

        double best = double.MaxValue;
        for (int i = 0; i < 4; i++)
        {
            long start = Stopwatch.GetTimestamp();
            copy.Derive(chain).Dispose();
            best = Math.Min(best, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        return best;
    }
}
