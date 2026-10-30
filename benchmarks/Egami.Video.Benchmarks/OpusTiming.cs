using System.Diagnostics;

using Egami.Video.Audio;
using Egami.Video.Containers;

namespace Egami.Video.Benchmarks;

/// <summary>
/// Step 2.4.8's Opus decode count: decode time per packet of the first audio track of Ogg Opus files, the best of five runs,
/// for comparison with <c>ffmpeg -c:a libopus -i file.opus -f null -</c> through tools/video/count-instructions.py. The packets
/// are read into memory first, so only decoding is timed. --once runs once and --runs=N N times; --one-thread is accepted for the
/// counting script and changes nothing, as the decoder runs on one thread.
/// </summary>
internal static class OpusTiming
{
    internal static void Run(string[] files)
    {
        int runs = files.Contains("--once") ? 1 : files.FirstOrDefault(f => f.StartsWith("--runs=", StringComparison.Ordinal)) is { } r
            ? int.Parse(r["--runs=".Length..], System.Globalization.CultureInfo.InvariantCulture) : 5;
        foreach (string file in files.Where(f => !f.StartsWith("--", StringComparison.Ordinal)))
        {
            (AudioTrackInfo track, List<byte[]> packets) = ReadPackets(file);
            double best = double.MaxValue;
            long allocated = 0;
            long samples = 0;
            for (int run = 0; run < runs; run++)
            {
                using IAudioDecoder decoder = AudioCodecs.CreateDecoder(track);
                samples = 0;
                long before = GC.GetAllocatedBytesForCurrentThread();
                long start = Stopwatch.GetTimestamp();
                foreach (byte[] data in packets)
                {
                    using (EncodedPacket packet = new(track.Number, data, default))
                    {
                        decoder.Send(packet);
                    }

                    samples += Drain(decoder);
                }

                decoder.Flush();
                samples += Drain(decoder);
                best = Math.Min(best, Stopwatch.GetElapsedTime(start).TotalMilliseconds * 1000 / packets.Count);
                allocated = (GC.GetAllocatedBytesForCurrentThread() - before) / packets.Count;
            }

            Console.WriteLine($"{Path.GetFileName(file)}: {best:F2} us a packet ({packets.Count} packets, {samples} samples), {allocated} bytes allocated a packet");
        }
    }

    private static long Drain(IAudioDecoder decoder)
    {
        long samples = 0;
        while (decoder.TryReceive(out AudioFrame? frame))
        {
            using (frame)
            {
                samples += frame.SampleCount;
            }
        }

        return samples;
    }

    private static (AudioTrackInfo Track, List<byte[]> Packets) ReadPackets(string file)
    {
        List<byte[]> packets = [];
        using FileStream stream = File.OpenRead(file);
        using IDemuxer demuxer = OggFormat.Instance.OpenDemuxer(stream, MediaContext.Default, leaveOpen: true);
        AudioTrackInfo track = demuxer.Tracks.OfType<AudioTrackInfo>().First();
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
