using System.Diagnostics;

namespace Egami.Samples.MemoryAndThreading;

/// <summary>Times one blur at 1 thread and all cores, and shows the pooled memory counters before and after.</summary>
public static class Sample
{
    /// <summary>Runs the sample.</summary>
    /// <param name="output">Where to write.</param>
    public static void Run(string output)
    {
        PixelMemoryUsage usage = PooledPixelMemorySource.Shared.Usage;
        Console.WriteLine($"Before: {usage.LiveBlocks} blocks, {usage.LiveBytes:N0} bytes");

        foreach (int threads in new[] { 1, Environment.ProcessorCount })
        {
            EgamiContext context = EgamiContext.Default with { MaxParallelism = threads };
            using Raster photo = Raster.Load(SampleRunner.Image("egret.png"), context);
            Console.WriteLine($"Loaded: {usage.LiveBlocks} blocks, {usage.LiveBytes:N0} bytes");
            Stopwatch clock = Stopwatch.StartNew();
            photo.Apply(ops => ops.Resize(1440, 0).Blur(4));
            Console.WriteLine($"{threads} thread(s): {clock.Elapsed.TotalMilliseconds:N1} ms");
            photo.Save(Path.Combine(output, $"blur-{threads}.png"));
        }

        Console.WriteLine($"After: {usage.LiveBlocks} blocks, {usage.LiveBytes:N0} bytes");
    }
}
