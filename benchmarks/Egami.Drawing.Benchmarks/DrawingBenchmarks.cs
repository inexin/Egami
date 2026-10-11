using System.Numerics;

using BenchmarkDotNet.Attributes;

using Egami.Pixels;

namespace Egami.Drawing.Benchmarks;

/// <summary>Egami.Drawing's own fill, stroke and clip costs, for tracking changes; the comparisons live in egami-compare.</summary>
[MemoryDiagnoser]
public class DrawingBenchmarks
{
    private readonly Path _star = Path.Star(new Vector2(512, 512), 500, 200, 9, 7);
    private readonly Path _circle = Path.Circle(new Vector2(512, 512), 480);
    private readonly Path[] _small = [.. Enumerable.Range(0, 1000).Select(i => Path.Circle(new Vector2(12 + (i * 37 % 1000), 12 + (i * 91 % 1000)), 3 + (i % 7)))];
    private readonly Rgba8 _ink = new(20, 60, 180, 220);
    private Raster<Rgba8> _raster = null!;
    private RasterCanvas<Rgba8> _canvas = null!;
    private RasterCanvas<Rgba8> _clipped = null!;

    [GlobalSetup]
    public void Setup()
    {
        _raster = Raster.Create(1024, 1024, Rgba8.White);
        _canvas = _raster.CreateCanvas();
        _clipped = _raster.CreateCanvas();
        _clipped.ClipPath(_circle);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _canvas.Dispose();
        _clipped.Dispose();
        _raster.Dispose();
    }

    [Benchmark]
    public void FillStar() => _canvas.FillPath(_star, _ink);

    [Benchmark]
    public void FillStarClipped() => _clipped.FillPath(_star, _ink);

    [Benchmark]
    public void StrokeStar() => _canvas.StrokePath(_star, new Stroke(8) { Join = LineJoin.Round }, _ink);

    [Benchmark]
    public void FillSmallShapes()
    {
        foreach (Path path in _small)
        {
            _canvas.FillPath(path, _ink);
        }
    }
}
