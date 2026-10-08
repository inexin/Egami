using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Egami.Pixels;

namespace Egami.Benchmarks;

/// <summary>The blur and sharpen scenarios from the operations design: a 1920 × 1080 Rgba8 photo.</summary>
[MemoryDiagnoser]
public class FilterBenchmarks
{
    private Raster<Rgba8> _raster = null!;

    [Params(1, 0)]
    public int Threads { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        EgamiContext context = EgamiContext.Default with { MaxParallelism = Threads == 0 ? Environment.ProcessorCount : Threads };
        _raster = Raster.Create<Rgba8>(1920, 1080, context);
        Random random = new(1);
        for (int y = 0; y < _raster.Height; y++)
        {
            Span<Rgba8> row = _raster.Row(y);
            random.NextBytes(MemoryMarshal.AsBytes(row));
            foreach (ref Rgba8 p in row)
            {
                p.A = 255;
            }
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _raster.Dispose();
    }

    [Benchmark]
    public void Blur3()
    {
        _raster.Derive(ops => ops.Blur(3)).Dispose();
    }

    [Benchmark]
    public void Blur25()
    {
        _raster.Derive(ops => ops.Blur(25)).Dispose();
    }

    [Benchmark]
    public void Sharpen()
    {
        _raster.Derive(ops => ops.Sharpen(1, 1)).Dispose();
    }
}
