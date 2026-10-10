using BenchmarkDotNet.Attributes;

using Egami.Colors;
using Egami.Pixels;

namespace Egami.Benchmarks;

/// <summary>Phase 2.1.2 color value rows: one 4000-pixel Rgba8 row to and from Lab, Oklab, HSL, HSV and YCbCr, single-threaded.</summary>
[MemoryDiagnoser]
public class ColorValueBenchmarks
{
    private const int Width = 4000;
    private readonly Rgba8[] _pixels = new Rgba8[Width];
    private readonly Rgba8[] _back = new Rgba8[Width];
    private readonly LabColor[] _lab = new LabColor[Width];
    private readonly OklabColor[] _oklab = new OklabColor[Width];
    private readonly HslColor[] _hsl = new HslColor[Width];
    private readonly HsvColor[] _hsv = new HsvColor[Width];
    private readonly YCbCrColor[] _ycbcr = new YCbCrColor[Width];

    [GlobalSetup]
    public void Setup()
    {
        new Random(1).NextBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(_pixels.AsSpan()));
        ColorConvert.FromPixels<Rgba8, LabColor>(_pixels, _lab);
        ColorConvert.FromPixels<Rgba8, OklabColor>(_pixels, _oklab);
    }

    [Benchmark]
    public void ToLab() => ColorConvert.FromPixels<Rgba8, LabColor>(_pixels, _lab);

    [Benchmark]
    public void ToOklab() => ColorConvert.FromPixels<Rgba8, OklabColor>(_pixels, _oklab);

    [Benchmark]
    public void ToHsl() => ColorConvert.FromPixels<Rgba8, HslColor>(_pixels, _hsl);

    [Benchmark]
    public void ToHsv() => ColorConvert.FromPixels<Rgba8, HsvColor>(_pixels, _hsv);

    [Benchmark]
    public void ToYCbCr() => ColorConvert.FromPixels<Rgba8, YCbCrColor>(_pixels, _ycbcr);

    [Benchmark]
    public void FromLab() => ColorConvert.ToPixels<LabColor, Rgba8>(_lab, _back);

    [Benchmark]
    public void FromOklab() => ColorConvert.ToPixels<OklabColor, Rgba8>(_oklab, _back);
}
