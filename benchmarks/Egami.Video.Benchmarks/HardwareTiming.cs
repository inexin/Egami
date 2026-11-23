using System.Diagnostics;
using System.Globalization;

using Egami.Pixels;
using Egami.Video.Codecs;
using Egami.Video.Hardware;

namespace Egami.Video.Benchmarks;

/// <summary>
/// Egami.Video.Hardware against the software codecs (Vulkan Video plan, PR E): for H.264 and HEVC, the frames of a Y4M clip
/// encoded at constant QP by the software encoder at effort 0 and by the GPU at efforts 4 and 0, with frames a second, bit
/// rate and PSNR-Y; then the software encoder's stream decoded by the software decoder on one thread and on all, and by the
/// GPU, with frames a second and whether the GPU's frames match. The best of three runs. --frames=N reads N frames (60);
/// --qp=N sets the QP (27).
/// </summary>
internal static class HardwareTiming
{
    internal static void Run(string[] args)
    {
        int count = Option(args, "--frames=", 60);
        int qp = Option(args, "--qp=", 27);
        foreach (string file in args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)))
        {
            List<YuvVideoFrame> frames = Read(file, count, out Rational rate);
            try
            {
                Time(Path.GetFileName(file), frames, rate, qp, "H.264", H264Codec.Instance, HardwareVideoCodecs.H264, CodecIds.Avc);
                Time(Path.GetFileName(file), frames, rate, qp, "HEVC", HevcCodec.Instance, HardwareVideoCodecs.Hevc, CodecIds.Hevc);
            }
            finally
            {
                frames.ForEach(f => f.Dispose());
            }
        }
    }

    private static void Time(string name, List<YuvVideoFrame> frames, Rational rate, int qp, string label, VideoCodec software, VideoCodec hardware, string codecId)
    {
        MediaContext context = MediaContext.Default.WithH264().WithHevc();
        double seconds = frames.Count * (double)rate.Denominator / rate.Numerator;
        List<EncodedPacket>? reference = null;
        VideoTrackInfo? track = null;
        foreach ((string encoder, VideoCodec codec, int effort) in (ReadOnlySpan<(string, VideoCodec, int)>)[("software e0", software, 0), ("GPU e4", hardware, 4), ("GPU e0", hardware, 0)])
        {
            if (codec == hardware && !HardwareVideoCodecs.IsAvailable(codecId, HardwareDirection.Encode))
            {
                Console.WriteLine($"{name} {label} {encoder}: no GPU encoder ({HardwareVideoCodecs.UnavailableReason ?? "not this codec"})");
                continue;
            }

            VideoEncoderOptions options = new()
            {
                Width = frames[0].Width,
                Height = frames[0].Height,
                FrameRate = rate,
                RateControl = RateControlMode.Quantizer,
                Quantizer = qp,
                Effort = effort,
                H264 = new H264EncoderOptions { AnnexB = true },
                Hevc = new HevcEncoderOptions { AnnexB = true },
            };
            double best = double.MaxValue;
            List<EncodedPacket> packets = [];
            VideoTrackInfo? encoded = null;
            for (int run = 0; run < 3; run++)
            {
                packets.ForEach(p => p.Dispose());
                packets = [];
                long start = Stopwatch.GetTimestamp();
                using IVideoEncoder e = codec.CreateEncoder(options, context);
                foreach (YuvVideoFrame frame in frames)
                {
                    e.Send(frame);
                    while (e.TryReceive(out EncodedPacket? packet))
                    {
                        packets.Add(packet);
                    }
                }

                e.Flush();
                while (e.TryReceive(out EncodedPacket? packet))
                {
                    packets.Add(packet);
                }

                best = Math.Min(best, Stopwatch.GetElapsedTime(start).TotalSeconds);
                encoded = e.Track;
            }

            List<YuvVideoFrame> decoded = Decode(software, encoded!, packets, context, Environment.ProcessorCount);
            double psnr = decoded.Select((f, i) => Psnr(((YuvRaster<byte>)frames[i].Planes!).Y, ((YuvRaster<byte>)f.Planes!).Y)).Average();
            decoded.ForEach(f => f.Dispose());
            long bytes = packets.Sum(p => (long)p.Data.Length);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{name} {label} encode {encoder}: {frames.Count / best:F1} fps, {bytes * 8 / seconds / 1000:F0} kbit/s, PSNR-Y {psnr:F2} dB"));
            if (reference is null)
            {
                reference = packets;
                track = encoded;
            }
            else
            {
                packets.ForEach(p => p.Dispose());
            }
        }

        List<YuvVideoFrame>? expected = null;
        foreach ((string decoder, VideoCodec codec, int threads) in (ReadOnlySpan<(string, VideoCodec, int)>)[("software 1 thread", software, 1), ("software all threads", software, Environment.ProcessorCount), ("GPU", hardware, 1)])
        {
            if (codec == hardware && !HardwareVideoCodecs.IsAvailable(codecId, HardwareDirection.Decode))
            {
                Console.WriteLine($"{name} {label} decode {decoder}: no GPU decoder");
                continue;
            }

            double best = double.MaxValue;
            List<YuvVideoFrame> output = [];
            for (int run = 0; run < 3; run++)
            {
                output.ForEach(f => f.Dispose());
                long start = Stopwatch.GetTimestamp();
                output = Decode(codec, track!, reference!, context, threads);
                best = Math.Min(best, Stopwatch.GetElapsedTime(start).TotalSeconds);
            }

            string same = "";
            if (expected is null)
            {
                expected = output;
            }
            else
            {
                int equal = output.Zip(expected).Count(p => Same((YuvRaster<byte>)p.First.Planes!, (YuvRaster<byte>)p.Second.Planes!));
                same = $", {equal}/{expected.Count} frames the same";
                output.ForEach(f => f.Dispose());
            }

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{name} {label} decode {decoder}: {reference!.Count / best:F1} fps{same}"));
        }

        expected?.ForEach(f => f.Dispose());
        reference?.ForEach(p => p.Dispose());
    }

    private static List<YuvVideoFrame> Decode(VideoCodec codec, VideoTrackInfo track, List<EncodedPacket> packets, MediaContext context, int threads)
    {
        MediaContext threaded = context with { Egami = context.Egami with { MaxParallelism = threads } };
        using IVideoDecoder decoder = codec.CreateDecoder(track, null, threaded);
        List<YuvVideoFrame> frames = [];
        foreach (EncodedPacket packet in packets)
        {
            decoder.Send(packet);
            while (decoder.TryReceive(out YuvVideoFrame? frame))
            {
                frames.Add(frame);
            }
        }

        decoder.Flush();
        while (decoder.TryReceive(out YuvVideoFrame? frame))
        {
            frames.Add(frame);
        }

        return frames;
    }

    private static List<YuvVideoFrame> Read(string file, int count, out Rational rate)
    {
        List<YuvVideoFrame> frames = [];
        using VideoReader reader = VideoReader.Open(file);
        rate = reader.Track.FrameRate ?? new Rational(25, 1);
        while (frames.Count < count && reader.ReadFrame() is { } frame)
        {
            frames.Add(frame);
        }

        return frames;
    }

    private static bool Same(YuvRaster<byte> a, YuvRaster<byte> b)
    {
        foreach ((YuvPlane<byte> x, YuvPlane<byte> y) in (ReadOnlySpan<(YuvPlane<byte>, YuvPlane<byte>)>)[(a.Y, b.Y), (a.U!, b.U!), (a.V!, b.V!)])
        {
            for (int row = 0; row < x.Height; row++)
            {
                if (!x.Row(row).SequenceEqual(y.Row(row)))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static double Psnr(YuvPlane<byte> a, YuvPlane<byte> b)
    {
        double sum = 0;
        for (int y = 0; y < a.Height; y++)
        {
            ReadOnlySpan<byte> ra = a.Row(y), rb = b.Row(y);
            for (int x = 0; x < ra.Length; x++)
            {
                int d = ra[x] - rb[x];
                sum += d * d;
            }
        }

        return sum == 0 ? 99 : 10 * Math.Log10(255.0 * 255 * a.Width * a.Height / sum);
    }

    private static int Option(string[] args, string name, int fallback) =>
        args.FirstOrDefault(a => a.StartsWith(name, StringComparison.Ordinal)) is { } value ? int.Parse(value[name.Length..], CultureInfo.InvariantCulture) : fallback;
}
