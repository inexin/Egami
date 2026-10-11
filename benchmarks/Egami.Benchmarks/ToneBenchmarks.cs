using System.Numerics;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Egami.Operations;
using Egami.Pixels;

namespace Egami.Benchmarks;

/// <summary>
/// The Phase 2.5.3 speed scenarios, without the competitors (egami-compare times those): 6000 × 4000 photos through the tone steps,
/// filters, 3D tables and measured steps, in place, at one thread and all cores.
/// </summary>
[MemoryDiagnoser]
public class ToneBenchmarks
{
    private Raster<Rgba8> _rgba8 = null!;
    private Raster<Rgba16> _rgba16 = null!;
    private Raster<RgbaF32> _float = null!;
    private Raster<Gray16> _gray16 = null!;
    private Raster<Gray8> _page = null!;
    private Lut3D _cube33 = null!;
    private Lut3D _cube65 = null!;

    [Params(1, 0)]
    public int Threads { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        EgamiContext context = EgamiContext.Default with { MaxParallelism = Threads == 0 ? Environment.ProcessorCount : Threads };
        _rgba8 = Raster.Create<Rgba8>(6000, 4000, context);
        Random random = new(1);
        for (int y = 0; y < _rgba8.Height; y++)
        {
            random.NextBytes(MemoryMarshal.AsBytes(_rgba8.Row(y)));
        }

        _rgba16 = _rgba8.As<Rgba16>();
        _float = _rgba8.As<RgbaF32>();
        _gray16 = _rgba8.As<Gray16>();
        _page = Raster.Create<Gray8>(2480, 3508, context);
        for (int y = 0; y < _page.Height; y++)
        {
            random.NextBytes(MemoryMarshal.AsBytes(_page.Row(y)));
        }

        static Vector3 Grade(Vector3 c) => new(MathF.Pow(c.X, 0.9f), (c.Y * 0.95f) + (0.05f * c.Z), MathF.Sqrt(c.Z));
        _cube33 = Lut3D.FromFunction(Grade, 33);
        _cube65 = Lut3D.FromFunction(Grade, 65);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _rgba8.Dispose();
        _rgba16.Dispose();
        _float.Dispose();
        _gray16.Dispose();
        _page.Dispose();
    }

    /// <summary>Level, Gamma and SigmoidalContrast fused: one lookup table per channel.</summary>
    [Benchmark]
    public void ToneFused8() => _rgba8.Apply(o => o.Level(0.05f, 0.95f, 1.1f).Gamma(1.2f).SigmoidalContrast(4));

    [Benchmark]
    public void ToneFused16() => _rgba16.Apply(o => o.Level(0.05f, 0.95f, 1.1f).Gamma(1.2f).SigmoidalContrast(4));

    [Benchmark]
    public void Posterize() => _rgba8.Apply(o => o.Posterize(5));

    [Benchmark]
    public void Threshold() => _rgba8.Apply(o => o.Threshold(0.5f));

    [Benchmark]
    public void Tint() => _rgba8.Apply(o => o.Tint(new Rgba8(255, 200, 150, 255), 0.4f));

    [Benchmark]
    public void HighContrast() => _rgba8.Apply(o => o.HighContrast(true, ContrastInvert.Lightness, 0.3f));

    [Benchmark]
    public void LerpOfTwoFilters() => _rgba8.Apply(o => o.ApplyFilter(ColorFilter.Lerp(ColorFilter.Sepia(), ColorFilter.Saturation(1.5f), 0.4f)));

    [Benchmark]
    public void ColorBlindness() => _rgba8.Apply(o => o.SimulateColorBlindness(ColorVisionDeficiency.Deuteranopia));

    [Benchmark]
    public void Cube33Tetrahedral8() => _rgba8.Apply(o => o.ApplyFilter(ColorFilter.FromLut(_cube33)));

    [Benchmark]
    public void Cube65Trilinear8() => _rgba8.Apply(o => o.ApplyFilter(ColorFilter.FromLut(_cube65, LutInterpolation.Trilinear)));

    [Benchmark]
    public void Cube33TetrahedralFloat() => _float.Apply(o => o.ApplyFilter(ColorFilter.FromLut(_cube33)));

    [Benchmark]
    public void AutoLevel() => _rgba8.Apply(o => o.AutoLevel());

    [Benchmark]
    public void Normalize() => _rgba8.Apply(o => o.Normalize());

    [Benchmark]
    public void AutoWhiteBalance() => _rgba8.Apply(o => o.AutoWhiteBalance());

    [Benchmark]
    public void Equalize() => _rgba8.Apply(o => o.Equalize(EqualizeMode.Channels));

    [Benchmark]
    public void Clahe8() => _rgba8.Apply(o => o.Clahe());

    [Benchmark]
    public void Clahe16() => _gray16.Apply(o => o.Clahe());

    [Benchmark]
    public void LocalContrast() => _rgba8.Apply(o => o.LocalContrast());

    [Benchmark]
    public void AutoThreshold() => _page.Apply(o => o.AutoThreshold());
}
