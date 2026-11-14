using System.Diagnostics;
using System.Globalization;

using Egami.Video.Codecs;
using Egami.Video.Codecs.H264;

namespace Egami.Video.Benchmarks;

/// <summary>
/// The H.264 encoder gates' Egami side: encodes a YUV4MPEG2 clip to a raw Annex B stream with the options given and prints the
/// encode time (frames read into memory first, best of --runs; wall clock and the process's CPU time, which a busy machine
/// skews less with one thread), the output size and the bitrate, for comparing with x264.
/// Options: --out=file.h264 --quality=Q --quantizer=Q --effort=E --threads=N (--one-thread for 1) --bitrate=BITS --cbr --two-pass --realtime
/// --bframes=N --profile=baseline|main|high --cavlc --no-aq --frames=N --runs=N.
/// In a build that counts (<see cref="H264EncodeStats.Enabled"/>), the JSON also carries the first run's work counters by
/// picture type ("stats") and, by picture kind (I, P, referenced B, B), the pictures' bits split into parts and the shares of
/// macroblock kinds ("pictures").
/// </summary>
internal static class H264EncodeTiming
{
    internal static void Run(string[] args)
    {
        string input = args.First(a => !a.StartsWith("--", StringComparison.Ordinal));
        string? Value(string name) => args.FirstOrDefault(a => a.StartsWith($"--{name}=", StringComparison.Ordinal))?[(name.Length + 3)..];
        int? Number(string name) => Value(name) is { } v ? int.Parse(v, CultureInfo.InvariantCulture) : null;
        bool Flag(string name) => args.Contains($"--{name}");

        List<YuvVideoFrame> frames = [];
        Rational rate;
        using (VideoReader reader = VideoReader.Open(input))
        {
            rate = reader.Track.FrameRate ?? new Rational(30, 1);
            int limit = Number("frames") ?? int.MaxValue;
            foreach (YuvVideoFrame frame in reader.ReadFrames())
            {
                if (frames.Count >= limit)
                {
                    frame.Dispose();
                    break;
                }

                frames.Add(frame);
            }
        }

        int threads = Flag("one-thread") ? 1 : Number("threads") ?? Environment.ProcessorCount;
        long? bitrate = Value("bitrate") is { } b ? long.Parse(b, CultureInfo.InvariantCulture) : null;
        VideoEncoderOptions options = new()
        {
            Width = frames[0].Width,
            Height = frames[0].Height,
            FrameRate = rate,
            Quality = Number("quality"),
            Quantizer = Number("quantizer"),
            Effort = Number("effort"),
            Bitrate = bitrate,
            KeyframeInterval = Number("keyint"),
            RateControl = bitrate is null ? Number("quantizer") is null ? RateControlMode.Quality : RateControlMode.Quantizer
                : Flag("cbr") ? RateControlMode.ConstantBitrate : RateControlMode.Bitrate,
            LatencyMode = Flag("realtime") ? LatencyMode.Realtime : LatencyMode.Quality,
            H264 = new H264EncoderOptions
            {
                AnnexB = true,
                BFrames = Number("bframes"),
                Slices = Number("slices"),
                AdaptiveQuantization = !Flag("no-aq"),
                Entropy = Flag("cavlc") ? H264EntropyCoder.Cavlc : null,
                Profile = Value("profile") switch { "baseline" => H264Profile.ConstrainedBaseline, "main" => H264Profile.Main, _ => H264Profile.High },
            },
        };
        MediaContext context = new() { Egami = EgamiContext.Default with { MaxParallelism = threads } };
        int runs = Number("runs") ?? 1;
        double best = double.MaxValue, bestCpu = double.MaxValue;
        long bytes = 0;
        string stats = "";
        H264EncodeStats.Reset();
        for (int run = 0; run < runs; run++)
        {
            long start = Stopwatch.GetTimestamp();
            TimeSpan cpuStart = Process.GetCurrentProcess().TotalProcessorTime;
            if (Flag("two-pass"))
            {
                options = options with { FirstPassStats = VideoWriter.AnalyzeFirstPass(frames) };
            }

            using MemoryStream output = new();
            using (IVideoEncoder encoder = H264Codec.Instance.CreateEncoder(options, context))
            {
                foreach (YuvVideoFrame frame in frames)
                {
                    encoder.Send(frame);
                    Drain(encoder, output);
                }

                encoder.Flush();
                Drain(encoder, output);
            }

            bytes = output.Length;
            best = Math.Min(best, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            bestCpu = Math.Min(bestCpu, (Process.GetCurrentProcess().TotalProcessorTime - cpuStart).TotalMilliseconds);
            if (run == 0 && Value("out") is { } path)
            {
                File.WriteAllBytes(path, output.ToArray());
            }

            if (run == 0 && H264EncodeStats.Enabled)
            {
                stats = StatsJson(H264EncodeStats.Snapshot()) + PicturesJson(H264EncodeStats.PictureSnapshot());
            }
        }

        double seconds = frames.Count * (double)rate.Denominator / rate.Numerator;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{{\"frames\": {frames.Count}, \"bytes\": {bytes}, \"kbps\": {bytes * 8 / seconds / 1000:F1}, \"ms\": {best:F1}, \"msPerFrame\": {best / frames.Count:F2}, \"cpuMsPerFrame\": {bestCpu / frames.Count:F2}, \"threads\": {threads}{stats}}}"));
        frames.ForEach(f => f.Dispose());
    }

    private static string StatsJson(long[,] counts)
    {
        string[] names = Enum.GetNames<H264EncodeStat>();
        IEnumerable<string> types = H264EncodeStats.Types.Select((type, t) =>
            $"\"{type}\": {{{string.Join(", ", names.Select((name, s) => string.Create(CultureInfo.InvariantCulture, $"\"{name}\": {counts[t, s]}")))}}}");
        return $", \"stats\": {{{string.Join(", ", types)}}}";
    }

    private static string PicturesJson(long[,] counts)
    {
        IEnumerable<string> kinds = H264EncodeStats.PictureKinds.Select((kind, k) =>
            $"\"{kind}\": {{{string.Join(", ", H264EncodeStats.PictureColumns.Select((name, c) => string.Create(CultureInfo.InvariantCulture, $"\"{name}\": {counts[k, c]}")))}}}");
        return $", \"pictures\": {{{string.Join(", ", kinds)}}}";
    }

    private static void Drain(IVideoEncoder encoder, MemoryStream output)
    {
        while (encoder.TryReceive(out EncodedPacket? packet))
        {
            using (packet)
            {
                output.Write(packet.Data.Span);
            }
        }
    }
}
