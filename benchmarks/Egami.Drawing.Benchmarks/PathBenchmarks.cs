using System.Numerics;

using BenchmarkDotNet.Attributes;

namespace Egami.Drawing.Benchmarks;

/// <summary>Path geometry costs: an affine transform, and warps through an affine and a curved function.</summary>
[MemoryDiagnoser]
public class PathBenchmarks
{
    private static readonly Matrix3x2 Matrix = Matrix3x2.CreateRotation(0.3f) * Matrix3x2.CreateTranslation(10, 20);
    private readonly Path _shapes = new PathBuilder()
        .AddPath(Path.Star(new Vector2(512, 512), 500, 200, 9, 7))
        .AddPath(Path.Circle(new Vector2(512, 512), 300))
        .AddPath(Path.RoundRect(new RoundRect(new RectF(100, 100, 300, 200), 40)))
        .ToPath();

    [Benchmark(Baseline = true)]
    public Path Transform() => _shapes.Transform(Matrix);

    [Benchmark]
    public Path WarpAffine() => _shapes.Warp(p => Vector2.Transform(p, Matrix));

    [Benchmark]
    public Path WarpArch() => _shapes.Warp(p => new Vector2(p.X, p.Y - (p.Y / 1024 * 120 * MathF.Sin(MathF.PI * p.X / 1024))));
}
