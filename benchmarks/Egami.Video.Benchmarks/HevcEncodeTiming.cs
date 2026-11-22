using System.Diagnostics;
using System.Globalization;

using Egami.Video.Codecs;
using Egami.Video.Codecs.Hevc.Encoder;

namespace Egami.Video.Benchmarks;

/// <summary>
/// The HEVC encoder gates' Egami side (tools/video/time-hevc-encoder.py): encodes a YUV4MPEG2 clip to a raw Annex B stream with
/// the options given and prints the encode time (frames read into memory first, best of --runs; wall clock and the process's
/// CPU time), the output size and the bitrate, for comparing with x265.
/// Options: --out=file.h265 --quality=Q --quantizer=Q --effort=E --threads=N (--one-thread for 1) --bitrate=BITS --cbr --two-pass --realtime
/// --bframes=N --profile=main|main10 --no-aq --keyint=N --frames=N --runs=N.
/// In a build that counts (<see cref="HevcEncodeStats.Enabled"/>), the JSON also carries the first run's work counters by
/// picture type ("stats") and, by picture kind (I, P, referenced B, B), the pictures' bits and the shares of coding unit kinds
/// in 8x8 blocks ("pictures").
/// </summary>
internal static class HevcEncodeTiming
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

        if (Value("horizon") is { } horizon)
        {
            HevcVideoEncoder.HorizonSeconds = double.Parse(horizon, CultureInfo.InvariantCulture);
        }

        if (Flag("trace"))
        {
            Egami.Video.Codecs.Encoding.EncoderCore.Trace = Console.Error.WriteLine;
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
            Hevc = new HevcEncoderOptions
            {
                AnnexB = true,
                BFrames = Number("bframes"),
                AdaptiveQuantization = !Flag("no-aq"),
                Profile = Value("profile") switch { "main" => HevcProfile.Main, "main10" => HevcProfile.Main10, _ => null },
            },
        };
        if (Value("inter") is { } inter)
        {
            // --inter=Name:value,... changes the inter profile of every effort, for measuring one setting (HevcInterProfile).
            HevcInterProfile.Adjust = profile =>
            {
                HevcInterProfile changed = profile with { };
                foreach (string pair in inter.Split(','))
                {
                    string[] parts = pair.Split(':');
                    System.Reflection.PropertyInfo property = typeof(HevcInterProfile).GetProperty(parts[0],
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                    property.SetValue(changed, Convert.ChangeType(parts[1] switch { "true" or "on" => "True", "false" or "off" => "False", _ => parts[1] },
                        property.PropertyType, CultureInfo.InvariantCulture));
                }

                return changed;
            };
        }

        MediaContext context = new() { Egami = EgamiContext.Default with { MaxParallelism = threads } };
        int runs = Number("runs") ?? 1;
        double best = double.MaxValue, bestCpu = double.MaxValue;
        long bytes = 0;
        string stats = "";
        HevcEncodeStats.Reset();
        using AllocationSampler? sampler = Flag("alloc-types") ? new AllocationSampler() : null;
        long allocated = 0;
        int collections = 0;
        for (int run = 0; run < runs; run++)
        {
            long start = Stopwatch.GetTimestamp();
            TimeSpan cpuStart = Process.GetCurrentProcess().TotalProcessorTime;
            long allocatedStart = GC.GetTotalAllocatedBytes(true);
            int collectionsStart = GC.CollectionCount(0);
            if (Flag("two-pass"))
            {
                options = options with { FirstPassStats = VideoWriter.AnalyzeFirstPass(frames) };
            }

            using MemoryStream output = new();
            using (IVideoEncoder encoder = HevcCodec.Instance.CreateEncoder(options, context))
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
            allocated = GC.GetTotalAllocatedBytes(true) - allocatedStart;
            collections = GC.CollectionCount(0) - collectionsStart;
            best = Math.Min(best, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            bestCpu = Math.Min(bestCpu, (Process.GetCurrentProcess().TotalProcessorTime - cpuStart).TotalMilliseconds);
            if (run == 0 && Value("out") is { } path)
            {
                File.WriteAllBytes(path, output.ToArray());
            }

            if (run == 0 && HevcEncodeStats.Enabled)
            {
                stats = StatsJson(HevcEncodeStats.Snapshot()) + PicturesJson(HevcEncodeStats.PictureSnapshot());
            }
        }

        double seconds = frames.Count * (double)rate.Denominator / rate.Numerator;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{{\"frames\": {frames.Count}, \"bytes\": {bytes}, \"kbps\": {bytes * 8 / seconds / 1000:F1}, \"ms\": {best:F1}, \"msPerFrame\": {best / frames.Count:F2}, \"cpuMsPerFrame\": {bestCpu / frames.Count:F2}, \"allocMBPerFrame\": {allocated / 1e6 / frames.Count:F2}, \"gcPerFrame\": {collections / (double)frames.Count:F2}, \"threads\": {threads}{stats}}}"));
        sampler?.Report(frames.Count * runs);
        frames.ForEach(f => f.Dispose());
    }

    private static string StatsJson(long[,] counts)
    {
        string[] names = Enum.GetNames<HevcEncodeStat>();
        IEnumerable<string> types = HevcEncodeStats.Types.Select((type, t) =>
            $"\"{type}\": {{{string.Join(", ", names.Select((name, s) => string.Create(CultureInfo.InvariantCulture, $"\"{name}\": {counts[t, s]}")))}}}");
        return $", \"stats\": {{{string.Join(", ", types)}}}";
    }

    private static string PicturesJson(long[,] counts)
    {
        IEnumerable<string> kinds = HevcEncodeStats.PictureKinds.Select((kind, k) =>
            $"\"{kind}\": {{{string.Join(", ", HevcEncodeStats.PictureColumns.Select((name, c) => string.Create(CultureInfo.InvariantCulture, $"\"{name}\": {counts[k, c]}")))}}}");
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
