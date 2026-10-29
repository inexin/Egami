using System.Diagnostics.Tracing;

namespace Egami.Tests.Memory;

/// <summary>
/// Measures what a call allocates on the calling thread, for the allocation-budget tests. The shared array pool hands out a fresh
/// buffer when it has none left: after a full collection trims it, under memory pressure, or when other threads took what it
/// held. Tests running alongside make that common, so a call during which an array pool had to allocate is not counted, as the
/// runtime's array pool events report it. A call that misses the pool every time (renting more than the pool keeps, or renting
/// without returning) is still counted, because then no call is free of misses.
/// </summary>
/// <remarks>
/// The allocation-budget tests still go a few bytes over now and then when other tests run alongside, so they run only on
/// demand: they skip unless <c>EGAMI_ALLOCATION_TESTS=1</c> is set, and carry the trait <c>Category=Allocation</c> so a run can
/// pick them alone.
/// </remarks>
internal static class AllocationMeter
{
    /// <summary>The trait value of the allocation-budget tests: <c>[Trait("Category", AllocationMeter.Category)]</c>.</summary>
    public const string Category = "Allocation";

    private static readonly PoolMissListener Listener = new();

    /// <summary>Gets whether the allocation-budget tests run: <c>EGAMI_ALLOCATION_TESTS</c> is set to <c>1</c>.</summary>
    public static bool OnDemand => Environment.GetEnvironmentVariable("EGAMI_ALLOCATION_TESTS") == "1";

    private const string SkipReason = "Allocation-budget test: set EGAMI_ALLOCATION_TESTS=1 to run it.";

    /// <summary>Skips the calling allocation-budget test unless <see cref="OnDemand"/>.</summary>
    public static void SkipUnlessOnDemand() => Assert.SkipUnless(OnDemand, SkipReason);

    /// <summary>
    /// Gets a budget theory's rows when <see cref="OnDemand"/>, and otherwise one skipped row, so a normal run reports one skip for
    /// the theory instead of one for each of its rows.
    /// </summary>
    public static TheoryData<T> Rows<T>(TheoryData<T> rows)
    {
        if (OnDemand)
        {
            return rows;
        }

        TheoryData<T> skipped = [];
        skipped.Add(new TheoryDataRow<T>(default!) { Skip = SkipReason });
        return skipped;
    }

    /// <summary>
    /// Gets a budget theory's rows when <see cref="OnDemand"/>, and otherwise one skipped row, so a normal run reports one skip for
    /// the theory instead of one for each of its rows.
    /// </summary>
    public static TheoryData<T1, T2> Rows<T1, T2>(TheoryData<T1, T2> rows)
    {
        if (OnDemand)
        {
            return rows;
        }

        TheoryData<T1, T2> skipped = [];
        skipped.Add(new TheoryDataRow<T1, T2>(default!, default!) { Skip = SkipReason });
        return skipped;
    }

    /// <summary>Gets how many buffers the array pools allocated on the calling thread so far.</summary>
    public static int PoolMisses => PoolMissListener.Misses;

    /// <summary>
    /// Returns the fewest bytes one call of <paramref name="action"/> allocated over <paramref name="tries"/> calls during which
    /// no array pool allocated, after <paramref name="warmUps"/> calls that fill the pools and let the JIT finish. Stops early
    /// once a call allocates nothing. If the pools allocate in nearly every call, the fewest of all calls is returned.
    /// </summary>
    public static long Fewest(Action action, int warmUps = 3, int tries = 6)
    {
        SkipUnlessOnDemand();
        GC.KeepAlive(Listener);
        for (int i = 0; i < warmUps; i++)
        {
            action();
        }

        long fewest = long.MaxValue;
        long fewestOfAll = long.MaxValue;
        int counted = 0;
        for (int attempt = 0; counted < tries && attempt < tries * 20 && fewest > 0; attempt++)
        {
            int misses = PoolMisses;
            long before = GC.GetAllocatedBytesForCurrentThread();
            action();
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            fewestOfAll = Math.Min(fewestOfAll, bytes);
            if (PoolMisses == misses)
            {
                fewest = Math.Min(fewest, bytes);
                counted++;
            }
        }

        return counted > 0 ? fewest : fewestOfAll;
    }

    /// <summary>Counts the array pools' buffer allocations per thread; a pool raises its events on the renting thread.</summary>
    private sealed class PoolMissListener : EventListener
    {
        [ThreadStatic]
        private static int t_misses;

        public static int Misses => t_misses;

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            // BufferAllocated is informational; the per-rent and per-return events are verbose, so they stay off.
            if (eventSource.Name == "System.Buffers.ArrayPoolEventSource")
            {
                EnableEvents(eventSource, EventLevel.Informational);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (eventData.EventName == "BufferAllocated")
            {
                t_misses++;
            }
        }
    }
}
