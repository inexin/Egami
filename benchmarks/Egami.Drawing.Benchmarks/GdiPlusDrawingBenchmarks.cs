using System.Numerics;

using BenchmarkDotNet.Attributes;

using Egami.Pixels;

namespace Egami.Drawing.Benchmarks;

/// <summary>The cost of GDI+'s antialiasing and thin pens (<see cref="Antialiasing.GdiPlus"/>) against Egami's exact coverage.</summary>
[MemoryDiagnoser]
public class GdiPlusDrawingBenchmarks
{
    private readonly Path _star = Path.Star(new Vector2(512, 512), 500, 200, 9, 7);
    private readonly Path[] _small = [.. Enumerable.Range(0, 1000).Select(i => Path.Circle(new Vector2(12 + (i * 37 % 1000), 12 + (i * 91 % 1000)), 3 + (i % 7)))];
    private readonly (Vector2 A, Vector2 B)[] _lines = [.. Enumerable.Range(0, 1000).Select(i => (new Vector2(10 + (i * 37 % 1000), 10 + (i * 91 % 1000)), new Vector2(10 + ((i * 53) % 1000), 10 + ((i * 17) % 1000))))];
    private readonly Rgba8 _ink = new(20, 60, 180, 220);
    private Raster<Rgba8> _raster = null!;
    private RasterCanvas<Rgba8> _canvas = null!;

    /// <summary>Gets or sets the antialiasing: Exact (Egami's) or GdiPlus.</summary>
    [Params(Antialiasing.Exact, Antialiasing.GdiPlus)]
    public Antialiasing Mode { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _raster = Raster.Create(1024, 1024, Rgba8.White);
        _canvas = _raster.CreateCanvas(new CanvasOptions { Antialiasing = Mode, PixelOffset = Mode == Antialiasing.GdiPlus ? PixelOffset.None : PixelOffset.Half });
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _canvas.Dispose();
        _raster.Dispose();
    }

    [Benchmark]
    public void FillStar() => _canvas.FillPath(_star, _ink);

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

    /// <summary>1,000 lines of a 0.5-pixel pen: GDI+'s thin lines in GdiPlus mode, faint hairlines in Exact.</summary>
    [Benchmark]
    public void ThinLines()
    {
        var stroke = new Stroke(0.5f);
        foreach ((Vector2 a, Vector2 b) in _lines)
        {
            _canvas.DrawLine(a, b, stroke, _ink);
        }
    }
}
