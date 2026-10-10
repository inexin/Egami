using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Egami.Pixels;

namespace Egami.Benchmarks;

/// <summary>
/// Resize, blur and DrawImage on translucent images, straight Rgba8 against PremulRgba8, which skips the multiply and divide
/// (the 2.1.1 gate: premultiplied must be faster). One thread, so the comparison is per core.
/// </summary>
[MemoryDiagnoser]
public class PremultipliedBenchmarks
{
    private Raster<Rgba8> _straight = null!;
    private Raster<PremulRgba8> _premultiplied = null!;
    private Raster<Rgba8> _logo = null!;

    [GlobalSetup]
    public void Setup()
    {
        EgamiContext context = EgamiContext.Default with { MaxParallelism = 1 };
        _straight = Raster.Create<Rgba8>(2000, 1500, context);
        Random random = new(1);
        for (int y = 0; y < _straight.Height; y++)
        {
            random.NextBytes(MemoryMarshal.AsBytes(_straight.Row(y)));
        }

        _premultiplied = _straight.As<PremulRgba8>();
        _logo = Raster.Create<Rgba8>(600, 400, new Rgba8(200, 30, 60, 140), context);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _straight.Dispose();
        _premultiplied.Dispose();
        _logo.Dispose();
    }

    [Benchmark]
    public void ResizeStraight() => _straight.Derive(ops => ops.Resize(500, 375)).Dispose();

    [Benchmark]
    public void ResizePremultiplied() => _premultiplied.Derive(ops => ops.Resize(500, 375)).Dispose();

    [Benchmark]
    public void BlurStraight() => _straight.Derive(ops => ops.Blur(3)).Dispose();

    [Benchmark]
    public void BlurPremultiplied() => _premultiplied.Derive(ops => ops.Blur(3)).Dispose();

    [Benchmark]
    public void DrawStraight() => _straight.Apply(ops => ops.DrawImage(_logo, new Point(100, 100), 0.9f));

    [Benchmark]
    public void DrawPremultiplied() => _premultiplied.Apply(ops => ops.DrawImage(_logo, new Point(100, 100), 0.9f));
}
