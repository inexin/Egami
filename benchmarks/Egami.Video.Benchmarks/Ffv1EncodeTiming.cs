using System.Diagnostics;
using System.Globalization;

using Egami.Video.Codecs;

namespace Egami.Video.Benchmarks;

/// <summary>
/// The V6 encode gates: decodes a file's frames into memory, then encodes them with the FFV1 encoder, the best of three runs,
/// and gives the time per frame and the coded size (packets only), for comparison with FFmpeg's (tools/video/time-ffv1.py
/// --encode). --slices=N, --context=small|large, --coder=tab|def|rice and --threads=N set what is matched; --runs=N runs N times.
/// </summary>
internal static class Ffv1EncodeTiming
{
    internal static void Run(string[] args)
    {
        string? Arg(string name) => args.FirstOrDefault(a => a.StartsWith($"--{name}=", StringComparison.Ordinal))?[(name.Length + 3)..];
        int runs = Arg("runs") is { } r ? int.Parse(r, CultureInfo.InvariantCulture) : 3;
        int threads = Arg("threads") is { } t ? int.Parse(t, CultureInfo.InvariantCulture) : 1;
        var options = new Ffv1EncoderOptions
        {
            Slices = Arg("slices") is { } s ? int.Parse(s, CultureInfo.InvariantCulture) : null,
            ContextModel = Arg("context") switch { "small" => Ffv1ContextModel.Small, "large" => Ffv1ContextModel.Large, _ => null },
            Coder = Arg("coder") switch { "def" => Ffv1Coder.RangeDefaultTable, "rice" => Ffv1Coder.GolombRice, _ => Ffv1Coder.RangeCustomTable },
        };
        MediaContext context = MediaContext.Default with { Egami = EgamiContext.Default with { MaxParallelism = threads } };
        foreach (string file in args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)))
        {
            List<YuvVideoFrame> frames = [];
            using (VideoReader reader = VideoReader.Open(file))
            {
                frames.AddRange(reader.ReadFrames());
            }

            double best = double.MaxValue;
            long bytes = 0;
            for (int run = 0; run < runs; run++)
            {
                bytes = 0;
                using IVideoEncoder encoder = VideoCodecs.Ffv1.CreateEncoder(new VideoEncoderOptions { Width = frames[0].Width, Height = frames[0].Height, Ffv1 = options }, context);
                long start = Stopwatch.GetTimestamp();
                foreach (YuvVideoFrame frame in frames)
                {
                    encoder.Send(frame);
                    while (encoder.TryReceive(out EncodedPacket? packet))
                    {
                        bytes += packet.Data.Length;
                        packet.Dispose();
                    }
                }

                best = Math.Min(best, Stopwatch.GetElapsedTime(start).TotalMilliseconds / frames.Count);
            }

            Console.WriteLine($"{Path.GetFileName(file)}: {best:F2} ms a frame on {threads} thread{(threads == 1 ? "" : "s")}, {bytes} bytes");
            foreach (YuvVideoFrame frame in frames)
            {
                frame.Dispose();
            }
        }
    }
}
