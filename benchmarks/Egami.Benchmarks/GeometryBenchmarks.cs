using BenchmarkDotNet.Attributes;

using Egami.Operations;
using Egami.Pixels;

namespace Egami.Benchmarks;

/// <summary>The exact geometry scenarios from the operations design: a 4000 × 3000 Rgba8 photo, on one thread and on all cores.</summary>
[MemoryDiagnoser]
public class GeometryBenchmarks
{
    private Raster<Rgba8> _raster = null!;
    private Raster<Rgba8> _hd = null!;

    [Params(1, 0)]
    public int Threads { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        EgamiContext context = EgamiContext.Default with { MaxParallelism = Threads == 0 ? Environment.ProcessorCount : Threads };
        _raster = Raster.Create<Rgba8>(4000, 3000, context);
        Random random = new(1);
        for (int y = 0; y < _raster.Height; y++)
        {
            random.NextBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(_raster.Row(y)));
        }

        _hd = Raster.Create<Rgba8>(1920, 1080, Rgba8.White, context);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _raster.Dispose();
        _hd.Dispose();
    }

    [Benchmark]
    public void Rotate90()
    {
        _raster.Derive(ops => ops.Rotate(90)).Dispose();
    }

    [Benchmark]
    public void FlipHorizontal()
    {
        _raster.Apply(ops => ops.Flip(FlipMode.Horizontal));
    }

    [Benchmark]
    public void FlipVertical()
    {
        _raster.Apply(ops => ops.Flip(FlipMode.Vertical));
    }

    [Benchmark]
    public void Crop()
    {
        _raster.Derive(ops => ops.Crop(500, 500, 3000, 2000)).Dispose();
    }

    [Benchmark]
    public void Rotate15()
    {
        _hd.Derive(ops => ops.Rotate(15)).Dispose();
    }
}
