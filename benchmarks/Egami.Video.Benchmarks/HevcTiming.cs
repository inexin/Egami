using System.Diagnostics;

using Egami.Heic;
using Egami.Video.Containers;

namespace Egami.Video.Benchmarks;

/// <summary>
/// The V8 speed gate: decode time per access unit of raw HEVC files through Egami.Heic's <see cref="HevcStreamDecoder"/>, the
/// best of five runs, at one thread and at all cores, for comparison with <c>ffmpeg -threads 1 -i clip.h265 -f null -</c>. Each
/// frame is copied out into one reused raster, as Egami.Video.Hevc does. --one-thread times one thread only, for profiling;
/// --threads=N times N threads only; --once runs once and --runs=N N times.
/// </summary>
internal static class HevcTiming
{
    internal static void Run(string[] files)
    {
        int runs = files.Contains("--once") ? 1 : files.FirstOrDefault(f => f.StartsWith("--runs=", StringComparison.Ordinal)) is { } r
            ? int.Parse(r["--runs=".Length..], System.Globalization.CultureInfo.InvariantCulture) : 5;
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
                YuvRaster? raster = null;
                for (int run = 0; run < runs; run++)
                {
                    using HevcStreamDecoder decoder = new(null, EgamiContext.Default with { MaxParallelism = threads });
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    long start = Stopwatch.GetTimestamp();
                    foreach (byte[] packet in packets)
                    {
                        decoder.Send(packet);
                        Drain(decoder, ref raster);
                    }

                    decoder.Flush();
                    Drain(decoder, ref raster);
                    best = Math.Min(best, Stopwatch.GetElapsedTime(start).TotalMilliseconds / packets.Count);
                    allocated = (GC.GetAllocatedBytesForCurrentThread() - before) / packets.Count;
                }

                raster?.Dispose();
                Console.WriteLine($"{Path.GetFileName(file)}: {best:F2} ms a frame on {threads} thread{(threads == 1 ? "" : "s")}, {allocated} bytes allocated a frame");
            }
        }
    }

    private static void Drain(HevcStreamDecoder decoder, ref YuvRaster? raster)
    {
        while (decoder.NextFrame is { } next)
        {
            if (raster is null || raster.Width != next.Width || raster.Height != next.Height)
            {
                raster?.Dispose();
                raster = next.BitDepth == 8
                    ? YuvRaster.Create(next.Width, next.Height, next.Chroma, Egami.Pixels.YuvColor.Bt709Limited)
                    : YuvRaster.Create(next.Width, next.Height, next.Chroma, next.BitDepth, Egami.Pixels.YuvColor.Bt709Limited);
            }

            decoder.TryReceiveFrame(raster, out _);
        }
    }

    private static List<byte[]> ReadPackets(string file)
    {
        List<byte[]> packets = [];
        using FileStream stream = File.OpenRead(file);
        using IDemuxer demuxer = HevcFormat.Instance.OpenDemuxer(stream, MediaContext.Default, leaveOpen: true);
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
