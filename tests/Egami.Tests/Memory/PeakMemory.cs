using System.Buffers;

namespace Egami.Tests.Memory;

/// <summary>What a piece of work held at its peak, as <see cref="PeakMemory.Measure"/> reports it.</summary>
/// <param name="PeakPixelBytes">The most pixel memory (rasters and working planes) live at once.</param>
/// <param name="ManagedBytes">Bytes allocated on the managed heap by the measuring thread, pooled arrays rented for the first time included.</param>
/// <param name="LeftBytes">Pixel memory still live when the work returned, which should be 0.</param>
internal readonly record struct MemoryPeak(long PeakPixelBytes, long ManagedBytes, long LeftBytes)
{
    public override string ToString() => $"peak {PeakPixelBytes:N0} pixel bytes, {ManagedBytes:N0} managed bytes, {LeftBytes:N0} left";
}

/// <summary>
/// The Phase 2.2 memory harness: runs work with a context whose pixel memory comes from a fresh 1.5 pooled source, wrapped so the
/// peak of its usage counters is kept, not only the live count. Shared by the tests and the benchmark project's --peak mode.
/// </summary>
internal static class PeakMemory
{
    /// <summary>Runs <paramref name="work"/> with its own context (default limits raised by <paramref name="limits"/>) and reports its peak.</summary>
    public static MemoryPeak Measure(Action<EgamiContext> work, SafetyLimits? limits = null)
    {
        PooledPixelMemorySource pool = new();
        PeakSource source = new(pool);
        EgamiContext context = new() { Memory = source, Limits = limits ?? SafetyLimits.Default };
        long managed = GC.GetAllocatedBytesForCurrentThread();
        work(context);
        managed = GC.GetAllocatedBytesForCurrentThread() - managed;
        return new MemoryPeak(source.PeakBytes, managed, pool.Usage.LiveBytes);
    }

    /// <summary>Passes blocks through from the pool and keeps the highest total live at once.</summary>
    private sealed class PeakSource(PooledPixelMemorySource inner) : IPixelMemorySource
    {
        private long _live;
        private long _peak;

        public int MaxBlockBytes => inner.MaxBlockBytes;

        public long PeakBytes => Interlocked.Read(ref _peak);

        public IMemoryOwner<byte> Allocate(int byteLength, ClearMode clear)
        {
            IMemoryOwner<byte> block = inner.Allocate(byteLength, clear);
            long live = Interlocked.Add(ref _live, byteLength);
            long peak;
            while (live > (peak = Interlocked.Read(ref _peak)) && Interlocked.CompareExchange(ref _peak, live, peak) != peak)
            {
            }

            return new Block(this, block, byteLength);
        }

        private sealed class Block(PeakSource owner, IMemoryOwner<byte> inner, int length) : IMemoryOwner<byte>
        {
            private int _disposed;

            public Memory<byte> Memory => inner.Memory;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    inner.Dispose();
                    Interlocked.Add(ref owner._live, -length);
                }
            }
        }
    }
}
