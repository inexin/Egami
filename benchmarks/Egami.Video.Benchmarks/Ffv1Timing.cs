using System.Diagnostics;

using Egami.Pixels;
using Egami.Video.Codecs;
using Egami.Video.Codecs.Ffv1;
using Egami.Video.Containers;

namespace Egami.Video.Benchmarks;

/// <summary>
/// The V6 speed gate: decode time per frame of FFV1 files, the best of five runs, at one thread and at all cores, for comparison
/// with FFmpeg's (tools/video/time-ffv1.py). Frames are decoded into planes and dropped. --one-thread times one thread only, for
/// profiling; --threads=N times N threads only; --runs=N runs N times. --verify times Ffv1Codec.Verify instead.
/// </summary>
internal static class Ffv1Timing
{
    internal static void Run(string[] args)
    {
        int runs = args.FirstOrDefault(f => f.StartsWith("--runs=", StringComparison.Ordinal)) is { } r
            ? int.Parse(r["--runs=".Length..], System.Globalization.CultureInfo.InvariantCulture) : 5;
        int[] counts = args.Contains("--one-thread") ? [1] : args.FirstOrDefault(f => f.StartsWith("--threads=", StringComparison.Ordinal)) is { } t
            ? [int.Parse(t["--threads=".Length..], System.Globalization.CultureInfo.InvariantCulture)]
            : [1, Environment.ProcessorCount];
        foreach (string file in args.Where(f => !f.StartsWith("--", StringComparison.Ordinal)))
        {
            if (args.Contains("--verify"))
            {
                double fastest = double.MaxValue;
                Ffv1VerifyResult? result = null;
                for (int run = 0; run < runs; run++)
                {
                    long start = Stopwatch.GetTimestamp();
                    result = Ffv1Codec.Verify(file);
                    fastest = Math.Min(fastest, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                }

                Console.WriteLine($"{Path.GetFileName(file)}: verified {result!.Frames} frames, {result.Slices} slices in {fastest:F1} ms ({fastest / result.Frames:F2} ms a frame), intact: {result.IsIntact}");
                continue;
            }

            (VideoTrackInfo track, List<byte[]> packets) = ReadPackets(file);
            foreach (int threads in counts)
            {
                EgamiContext context = EgamiContext.Default with { MaxParallelism = threads };
                double best = double.MaxValue;
                long allocated = 0;
                List<Ffv1SliceError> errors = [];
                var color = new YuvColor(YuvMatrix.Bt601, YuvRange.Limited, ChromaSiting.Left);
                for (int run = 0; run < runs; run++)
                {
                    var decoder = new Ffv1Decoder(track.Width, track.Height, Ffv1Codec.ConfigurationRecord(track).Span, out _);
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    long start = Stopwatch.GetTimestamp();
                    foreach (byte[] packet in packets)
                    {
                        using YuvRaster frame = decoder.Decode(packet, packet.Length, color, context, errors, out _);
                    }

                    best = Math.Min(best, Stopwatch.GetElapsedTime(start).TotalMilliseconds / packets.Count);
                    allocated = (GC.GetAllocatedBytesForCurrentThread() - before) / packets.Count;
                }

                Console.WriteLine($"{Path.GetFileName(file)}: {best:F2} ms a frame on {threads} thread{(threads == 1 ? "" : "s")}, {allocated} bytes allocated a frame, {errors.Count} errors");
            }
        }
    }

    private static (VideoTrackInfo, List<byte[]>) ReadPackets(string file)
    {
        List<byte[]> packets = [];
        using IDemuxer demuxer = MediaFile.OpenDemuxer(file);
        VideoTrackInfo track = demuxer.Info.VideoTrack!;
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                if (packet.TrackNumber == track.Number)
                {
                    packets.Add(packet.Data.ToArray());
                }
            }
        }

        return (track, packets);
    }
}
