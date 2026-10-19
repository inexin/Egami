namespace Egami.Tests.Memory;

/// <summary>
/// Measures what a call allocates on the calling thread, for the allocation-budget tests. After a full (generation 2) collection
/// the runtime trims the shared array pool, and under memory pressure it empties it, so the next calls rent fresh buffers. Tests
/// running alongside make such collections common, so a call that a collection overlapped is not counted: the pool is filled
/// again and the call measured again.
/// </summary>
internal static class AllocationMeter
{
    /// <summary>
    /// Returns the fewest bytes one call of <paramref name="action"/> allocated over <paramref name="tries"/> calls that no full
    /// collection overlapped, after <paramref name="warmUps"/> calls that fill the pools and let the JIT finish. Stops early once
    /// a call allocates nothing. If collections overlap nearly every call, the fewest of all calls is returned.
    /// </summary>
    public static long Fewest(Action action, int warmUps = 3, int tries = 6)
    {
        for (int i = 0; i < warmUps; i++)
        {
            action();
        }

        int settled = Settle(action);
        long fewest = long.MaxValue;
        long fewestOfAll = long.MaxValue;
        int counted = 0;
        for (int attempt = 0; counted < tries && attempt < tries * 20 && fewest > 0; attempt++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            action();
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            fewestOfAll = Math.Min(fewestOfAll, bytes);
            if (GC.CollectionCount(2) == settled)
            {
                fewest = Math.Min(fewest, bytes);
                counted++;
            }
            else
            {
                settled = Settle(action);
            }
        }

        return counted > 0 ? fewest : fewestOfAll;
    }

    /// <summary>
    /// Lets the pool trimming that follows a full collection finish (it runs on the finalizer thread), then fills the pools again
    /// with one call; repeated while a full collection overlaps it. Returns the full-collection count the next calls must keep.
    /// </summary>
    private static int Settle(Action action)
    {
        for (int i = 0; ; i++)
        {
            int count = GC.CollectionCount(2);
            GC.WaitForPendingFinalizers();
            action();
            if (GC.CollectionCount(2) == count || i == 10)
            {
                return GC.CollectionCount(2);
            }
        }
    }
}
