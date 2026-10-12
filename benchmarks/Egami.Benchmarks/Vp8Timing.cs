using System.Diagnostics;

using Egami.Formats.Webp;

namespace Egami.Benchmarks;

/// <summary>
/// The Phase 2.3.5 speed gate: decode time per frame of VP8 IVF files (tools/make-vp8-references.py --large), the best of five
/// runs, at one thread and at all cores, for comparison with vpxdec's times in timing.json.
/// </summary>
internal static class Vp8Timing
{
    internal static void Run(string[] files)
    {
        // --one-thread times one thread only, for profiling.
        int[] counts = files.Contains("--one-thread") ? [1] : [1, Environment.ProcessorCount];
        foreach (string file in files.Where(f => !f.StartsWith("--", StringComparison.Ordinal)))
        {
            List<byte[]> packets = ReadIvf(file);
            foreach (int threads in counts)
            {
                double best = double.MaxValue;
                for (int run = 0; run < 5; run++)
                {
                    using Vp8VideoDecoder video = new(threads);
                    long start = Stopwatch.GetTimestamp();
                    foreach (byte[] packet in packets)
                    {
                        video.Decode(packet, 0, packet.Length);
                    }

                    best = Math.Min(best, Stopwatch.GetElapsedTime(start).TotalMilliseconds / packets.Count);
                }

                Console.WriteLine($"{Path.GetFileName(file)}: {best:F2} ms a packet on {threads} thread{(threads == 1 ? "" : "s")}");
            }
        }
    }

    private static List<byte[]> ReadIvf(string path)
    {
        byte[] file = File.ReadAllBytes(path);
        int at = BitConverter.ToUInt16(file, 6);
        List<byte[]> packets = [];
        while (at + 12 <= file.Length)
        {
            int size = BitConverter.ToInt32(file, at);
            packets.Add(file.AsSpan(at + 12, size).ToArray());
            at += 12 + size;
        }

        return packets;
    }
}
