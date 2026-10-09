using BenchmarkDotNet.Attributes;

namespace Egami.Benchmarks;

/// <summary>Allocating and disposing a pooled (64 KB) and an unmanaged (4 MB) block, the path the usage counters sit on.</summary>
[MemoryDiagnoser]
public class MemoryBenchmarks
{
    private readonly PooledPixelMemorySource _source = new();

    [Benchmark]
    public void PooledBlock()
    {
        _source.Allocate(64 * 1024, ClearMode.None).Dispose();
    }

    [Benchmark]
    public void UnmanagedBlock()
    {
        _source.Allocate(4 * 1024 * 1024, ClearMode.None).Dispose();
    }
}
