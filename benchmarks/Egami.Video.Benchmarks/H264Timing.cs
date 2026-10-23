using System.Diagnostics;
using System.Security.Cryptography;

using Egami.Video.Codecs;
using Egami.Video.Codecs.H264;
using Egami.Video.Containers;

namespace Egami.Video.Benchmarks;

/// <summary>
/// The V7 speed gate: decode time per access unit of raw H.264 files, the best of five runs, at one thread and at all cores, for
/// comparison with <c>ffmpeg -threads 1 -i clip.h264 -f null -</c>. Frames are decoded and not copied out. --one-thread times one
/// thread only, for profiling; --threads=N times N threads only; --once runs once. --md5 instead prints the MD5 of every frame's
/// 8-bit planes one after the other, as <c>ffmpeg -i clip.h264 -f md5 -</c> gives it, decoded on --threads=N threads (default 1).
/// </summary>
internal static class H264Timing
{
    internal static void Run(string[] files)
    {
        if (files.Contains("--md5"))
        {
            Md5(files);
            return;
        }

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
                    H264Decoder decoder = new(EgamiContext.Default.Limits, DecodeErrorMode.Throw) { MaxParallelism = threads };
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    long start = Stopwatch.GetTimestamp();
                    foreach (byte[] packet in packets)
                    {
                        decoder.Decode(packet, null);
                    }

                    decoder.Flush();
                    best = Math.Min(best, Stopwatch.GetElapsedTime(start).TotalMilliseconds / packets.Count);
                    allocated = (GC.GetAllocatedBytesForCurrentThread() - before) / packets.Count;
                }

                Console.WriteLine($"{Path.GetFileName(file)}: {best:F2} ms a frame on {threads} thread{(threads == 1 ? "" : "s")}, {allocated} bytes allocated a frame");
            }
        }
    }

    private static void Md5(string[] files)
    {
        int threads = files.FirstOrDefault(f => f.StartsWith("--threads=", StringComparison.Ordinal)) is { } t
            ? int.Parse(t["--threads=".Length..], System.Globalization.CultureInfo.InvariantCulture) : 1;
        MediaContext context = MediaContext.Default.WithH264() with { Egami = EgamiContext.Default with { MaxParallelism = threads } };
        foreach (string file in files.Where(f => !f.StartsWith("--", StringComparison.Ordinal)))
        {
            using FileStream stream = File.OpenRead(file);
            using IDemuxer demuxer = H264Format.Instance.OpenDemuxer(stream, context, leaveOpen: true);
            using IVideoDecoder decoder = H264Codec.Instance.CreateDecoder(demuxer.Info.VideoTrack!, null, context);
            using IncrementalHash md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            int frames = 0;
            while (demuxer.ReadPacket() is { } packet)
            {
                using (packet)
                {
                    decoder.Send(packet);
                }

                frames += Append(decoder, md5);
            }

            decoder.Flush();
            frames += Append(decoder, md5);
            Console.WriteLine($"{Path.GetFileName(file)}: {frames} frames, MD5={Convert.ToHexStringLower(md5.GetHashAndReset())}");
        }
    }

    private static int Append(IVideoDecoder decoder, IncrementalHash md5)
    {
        int frames = 0;
        while (decoder.TryReceive(out YuvVideoFrame? frame))
        {
            using (frame)
            {
                YuvRaster<byte> raster = (YuvRaster<byte>)frame.Raster;
                foreach (YuvPlane<byte>? plane in new[] { raster.Y, raster.U, raster.V })
                {
                    for (int y = 0; plane is not null && y < plane.Height; y++)
                    {
                        md5.AppendData(plane.Row(y));
                    }
                }
            }

            frames++;
        }

        return frames;
    }

    private static List<byte[]> ReadPackets(string file)
    {
        List<byte[]> packets = [];
        using FileStream stream = File.OpenRead(file);
        using IDemuxer demuxer = H264Format.Instance.OpenDemuxer(stream, MediaContext.Default, leaveOpen: true);
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
