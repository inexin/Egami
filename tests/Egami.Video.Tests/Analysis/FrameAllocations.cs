namespace Egami.Video.Tests.Analysis;

/// <summary>
/// Checks that a detector allocates nothing per frame. Each frame is measured on its own, and only frames that allocated are
/// counted: a detector that allocates per frame does so on every frame, while the runtime's own work on the test thread (tiered
/// compilation, a pool refill, a collection under load from tests running alongside) lands on one frame now and then.
/// </summary>
internal static class FrameAllocations
{
    /// <summary>Feeds <paramref name="count"/> frames through <paramref name="add"/> and asserts at most a quarter of them allocated.</summary>
    public static void AssertNone(string name, int count, Action<int> add)
    {
        int allocating = 0;
        long total = 0;
        for (int i = 0; i < count; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            add(i);
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            total += bytes;
            allocating += bytes > 0 ? 1 : 0;
        }

        Assert.True(allocating <= count / 4, $"{name}: {allocating} of {count} frames allocated, {total} bytes in all");
    }
}
