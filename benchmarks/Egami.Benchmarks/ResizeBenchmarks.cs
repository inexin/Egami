using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Egami.Operations;
using Egami.Pixels;

namespace Egami.Benchmarks;

/// <summary>The resize scenarios from the operations design on Rgba8, on one thread and on all cores.</summary>
[MemoryDiagnoser]
public class ResizeBenchmarks
{
    private Raster<Rgba8> _large = null!;
    private Raster<Rgba8> _small = null!;

    [Params(1, 0)]
    public int Threads { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        EgamiContext context = EgamiContext.Default with { MaxParallelism = Threads == 0 ? Environment.ProcessorCount : Threads };
        _large = Noise(4000, 3000, context);
        _small = Noise(800, 600, context);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _large.Dispose();
        _small.Dispose();
    }

    [Benchmark]
    public void DownscaleBicubic()
    {
        _large.Derive(ops => ops.Resize(800, 600)).Dispose();
    }

    [Benchmark]
    public void DownscaleLinear()
    {
        _large.Derive(ops => ops.Resize(800, 600, ResampleFilter.Bicubic, ColorSpace.Linear)).Dispose();
    }

    [Benchmark]
    public void UpscaleBicubic()
    {
        _small.Derive(ops => ops.Resize(2400, 1800)).Dispose();
    }

    [Benchmark]
    public void Fill256()
    {
        _large.Derive(ops => ops.Cover(256, 256)).Dispose();
    }

    private static Raster<Rgba8> Noise(int width, int height, EgamiContext context)
    {
        Raster<Rgba8> raster = Raster.Create<Rgba8>(width, height, context);
        Random random = new(1);
        for (int y = 0; y < height; y++)
        {
            Span<Rgba8> row = raster.Row(y);
            random.NextBytes(MemoryMarshal.AsBytes(row));
            foreach (ref Rgba8 p in row)
            {
                p.A = 255;
            }
        }

        return raster;
    }
}
