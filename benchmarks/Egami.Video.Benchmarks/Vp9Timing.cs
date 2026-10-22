using System.Diagnostics;

using Egami.Video.Codecs.Vp9;
using Egami.Video.Containers;

namespace Egami.Video.Benchmarks;

/// <summary>
/// The V3 speed gate: decode time per packet of VP9 IVF files (tools/video/make-vp9-corpus.py --large), the best of five runs, at one
/// thread and at all cores, for comparison with vpxdec's times in timing.json. Like vpxdec --noblit, frames are decoded and not
/// copied out. --one-thread times one thread only, for profiling; --threads=N times N threads only.
/// </summary>
internal static class Vp9Timing
{
    internal static void Run(string[] files)
    {
        int runs = files.Contains("--once") ? 1 : 5;
        int[] counts = files.Contains("--one-thread") ? [1] : files.FirstOrDefault(f => f.StartsWith("--threads=", StringComparison.Ordinal)) is { } t
            ? [int.Parse(t["--threads=".Length..], System.Globalization.CultureInfo.InvariantCulture)]
            : [1, Environment.ProcessorCount];
        foreach (string file in files.Where(f => !f.StartsWith("--", StringComparison.Ordinal)))
        {
            List<byte[]> packets = ReadPackets(file);
            foreach (int threads in counts)
            {
                double best = double.MaxValue;
                long allocated = 0;
                for (int run = 0; run < runs; run++)
                {
                    using Vp9StreamDecoder decoder = new(EgamiContext.Default.Memory) { MaxParallelism = threads };
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    long start = Stopwatch.GetTimestamp();
                    foreach (byte[] packet in packets)
                    {
                        decoder.Decode(packet);
                    }

                    best = Math.Min(best, Stopwatch.GetElapsedTime(start).TotalMilliseconds / packets.Count);
                    allocated = (GC.GetAllocatedBytesForCurrentThread() - before) / packets.Count;
                }

                Console.WriteLine($"{Path.GetFileName(file)}: {best:F2} ms a packet on {threads} thread{(threads == 1 ? "" : "s")}, {allocated} bytes allocated a packet");
            }
        }
    }

    private static List<byte[]> ReadPackets(string file)
    {
        List<byte[]> packets = [];
        using IDemuxer demuxer = MediaFile.OpenDemuxer(file);
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                packets.Add(packet.Data.ToArray());
            }
        }

        return packets;
    }
}
