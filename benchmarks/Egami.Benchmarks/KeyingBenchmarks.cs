using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Egami.Operations;
using Egami.Pixels;

namespace Egami.Benchmarks;

/// <summary>
/// The 2.5.8 keying scenario: a 4000 × 3000 Rgba8 frame keyed by RGB distance (the existing MakeTransparent), by chroma, and
/// despilled, in place, at one thread and all cores.
/// </summary>
[MemoryDiagnoser]
public class KeyingBenchmarks
{
    private static readonly Rgba8 Screen = new(0, 177, 64, 255);
    private Raster<Rgba8> _frame = null!;

    [Params(1, 0)]
    public int Threads { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        EgamiContext context = EgamiContext.Default with { MaxParallelism = Threads == 0 ? Environment.ProcessorCount : Threads };
        _frame = Raster.Create<Rgba8>(4000, 3000, context);
        Random random = new(1);
        for (int y = 0; y < _frame.Height; y++)
        {
            Span<Rgba8> row = _frame.Row(y);
            random.NextBytes(MemoryMarshal.AsBytes(row));
            foreach (ref Rgba8 p in row)
            {
                p.A = 255;
            }
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _frame.Dispose();

    [Benchmark(Baseline = true)]
    public void MakeTransparent() => _frame.Apply(ops => ops.MakeTransparent(Screen, 0.15f, 0.1f));

    [Benchmark]
    public void ChromaKey() => _frame.Apply(ops => ops.ChromaKey(Screen, 0.12f, 0.06f));

    [Benchmark]
    public void Despill() => _frame.Apply(ops => ops.Despill(DespillColor.Green));
}
