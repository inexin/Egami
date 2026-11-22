using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Globalization;

namespace Egami.Video.Benchmarks;

/// <summary>
/// The runtime's allocation ticks (one about every 100 KB allocated) summed by type, for finding what an encoder allocates
/// (--alloc-types in the timing tools).
/// </summary>
internal sealed class AllocationSampler : EventListener
{
    private readonly ConcurrentDictionary<string, long> _bytes = new();

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == "Microsoft-Windows-DotNETRuntime")
        {
            EnableEvents(eventSource, EventLevel.Verbose, (EventKeywords)0x1);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (eventData.EventName?.StartsWith("GCAllocationTick", StringComparison.Ordinal) != true || eventData.PayloadNames is null)
        {
            return;
        }

        int type = eventData.PayloadNames.IndexOf("TypeName"), amount = eventData.PayloadNames.IndexOf("AllocationAmount64");
        if (type >= 0 && amount >= 0)
        {
            _bytes.AddOrUpdate((string)eventData.Payload![type]!, Convert.ToInt64(eventData.Payload[amount], CultureInfo.InvariantCulture), (_, v) => v + Convert.ToInt64(eventData.Payload[amount], CultureInfo.InvariantCulture));
        }
    }

    /// <summary>Writes the types that allocated most, in MB a frame, to the error output.</summary>
    internal void Report(int frames)
    {
        foreach ((string type, long bytes) in _bytes.OrderByDescending(p => p.Value).Take(20))
        {
            Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{bytes / 1e6 / frames,8:F2} MB/frame  {type}"));
        }
    }
}
