using System.Diagnostics;
using System.Globalization;

using Egami.Video.Codecs;

namespace Egami.Video.Benchmarks;

/// <summary>
/// The V5.2 gates' Egami side: encodes a YUV4MPEG2 clip to VP8 IVF with the options given and prints the encode time (frames
/// read into memory first, best of --runs), the output size and the bitrate, for egami-compare's gate script to compare with
/// vpxenc. Options: --out=file.ivf --quality=Q --quantizer=Q --effort=E --threads=N --bitrate=BITS --cbr --two-pass
/// --realtime --no-altref --frames=N --runs=N --decoded=file.yuv (Egami's decode of the output, packed I420).
/// </summary>
internal static class Vp8EncodeTiming
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

        if (Flag("trace"))
        {
            Egami.Video.Codecs.Encoding.EncoderCore.Trace = Console.Error.WriteLine;
        }

        int threads = Number("threads") ?? Environment.ProcessorCount;
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
            RateControl = bitrate is null ? Number("quantizer") is null ? RateControlMode.Quality : RateControlMode.Quantizer
                : Flag("cbr") ? RateControlMode.ConstantBitrate : RateControlMode.Bitrate,
            LatencyMode = Flag("realtime") ? LatencyMode.Realtime : LatencyMode.Quality,
            Vp8 = new Vp8EncoderOptions { AlternateReference = !Flag("no-altref") },
        };
        MediaContext context = new() { Egami = EgamiContext.Default with { MaxParallelism = threads } };
        int runs = Number("runs") ?? 1;
        double best = double.MaxValue;
        long bytes = 0;
        for (int run = 0; run < runs; run++)
        {
            long start = Stopwatch.GetTimestamp();
            if (Flag("two-pass"))
            {
                options = options with { FirstPassStats = VideoWriter.AnalyzeFirstPass(frames) };
            }

            using MemoryStream output = new();
            bytes = Encode(output, options, context, frames);

            best = Math.Min(best, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            if (run == 0 && Value("out") is { } path)
            {
                File.WriteAllBytes(path, output.ToArray());
            }

            if (run == 0 && Value("decoded") is { } decodedPath)
            {
                WriteDecoded(output.ToArray(), decodedPath);
            }
        }

        double seconds = frames.Count * (double)rate.Denominator / rate.Numerator;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{{\"frames\": {frames.Count}, \"bytes\": {bytes}, \"kbps\": {bytes * 8 / seconds / 1000:F1}, \"ms\": {best:F1}, \"msPerFrame\": {best / frames.Count:F2}, \"threads\": {threads}}}"));
        frames.ForEach(f => f.Dispose());
    }

    /// <summary>Decodes the IVF with Egami's decoder and writes the shown frames as packed I420, as vpxdec --i420 does.</summary>
    private static void WriteDecoded(byte[] ivf, string path)
    {
        using VideoReader reader = VideoReader.Open(new MemoryStream(ivf));
        using FileStream file = File.Create(path);
        foreach (YuvVideoFrame frame in reader.ReadFrames())
        {
            using (frame)
            {
                YuvRaster<byte> raster = (YuvRaster<byte>)frame.Raster;
                foreach (YuvPlane<byte> plane in (YuvPlane<byte>[])[raster.Y, raster.U!, raster.V!])
                {
                    for (int y = 0; y < plane.Height; y++)
                    {
                        file.Write(plane.Row(y));
                    }
                }
            }
        }
    }

    /// <summary>Encodes every frame into IVF in memory and returns its size.</summary>
    private static long Encode(MemoryStream output, VideoEncoderOptions options, MediaContext context, List<YuvVideoFrame> frames)
    {
        using IVideoEncoder encoder = VideoCodecs.Vp8.CreateEncoder(options, context);
        using Containers.IMuxer muxer = MediaFile.CreateMuxer(output, "ivf", [encoder.Track], context, leaveOpen: true);
        foreach (YuvVideoFrame frame in frames)
        {
            encoder.Send(frame);
            Drain(encoder, muxer);
        }

        encoder.Flush();
        Drain(encoder, muxer);
        muxer.Finish();
        return output.Length;
    }

    private static void Drain(IVideoEncoder encoder, Containers.IMuxer muxer)
    {
        while (encoder.TryReceive(out EncodedPacket? packet))
        {
            using (packet)
            {
                muxer.WritePacket(packet);
            }
        }
    }
}
