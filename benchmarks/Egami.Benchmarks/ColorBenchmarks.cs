using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Egami.Pixels;

namespace Egami.Benchmarks;

/// <summary>The color chain scenario from the operations design: a 4000 × 3000 Rgba8 photo, in place.</summary>
[MemoryDiagnoser]
public class ColorBenchmarks
{
    private Raster<Rgba8> _raster = null!;

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
            random.NextBytes(MemoryMarshal.AsBytes(_raster.Row(y)));
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _raster.Dispose();
    }

    /// <summary>Mixes channels, so it runs as one fused matrix.</summary>
    [Benchmark]
    public void BrightnessContrastSaturation()
    {
        _raster.Apply(ops => ops.Brightness(1.02f).Contrast(0.98f).Saturation(1.01f));
    }

    /// <summary>Per-channel, so it runs as lookup tables.</summary>
    [Benchmark]
    public void BrightnessContrast()
    {
        _raster.Apply(ops => ops.Brightness(1.02f).Contrast(0.98f));
    }
}
