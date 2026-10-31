using System.Diagnostics;

using Egami.Formats.Av1;

namespace Egami.Benchmarks;

/// <summary>
/// The Phase 2.9.5 speed gate: decode time per temporal unit of AV1 IVF files (the 720p and 1080p clips in .cache/video/av1/large),
/// the best of five runs, at one thread and at all cores, for comparison with dav1d's.
/// </summary>
internal static class Av1Timing
{
    internal static void Run(string[] files)
    {
        if (files.Contains("--md5"))
        {
            Md5(files);
            return;
        }

        // --one-thread times one thread only, for profiling; --runs=N runs N times (tools/video/count-instructions.py).
        int[] counts = files.Contains("--one-thread") ? [1] : [1, Environment.ProcessorCount];
        int runs = files.FirstOrDefault(f => f.StartsWith("--runs=", StringComparison.Ordinal)) is { } r
            ? int.Parse(r["--runs=".Length..], System.Globalization.CultureInfo.InvariantCulture) : 5;
        foreach (string file in files.Where(f => !f.StartsWith("--", StringComparison.Ordinal)))
        {
            List<byte[]> units = Vp8Timing.ReadIvf(file);
            foreach (int threads in counts)
            {
                double best = double.MaxValue, bestCycles = double.MaxValue;
                for (int run = 0; run < runs; run++)
                {
                    long cycles = Vp8Timing.ThreadCycles();
                    Av1Stream stream = new("AV1") { Parallelism = threads };
                    long start = Stopwatch.GetTimestamp();
                    foreach (byte[] unit in units)
                    {
                        foreach (Av1Frame frame in stream.Decode(unit, 0, unit.Length))
                        {
                            Av1FilmGrainSynthesis.Apply(frame);
                        }
                    }

                    best = Math.Min(best, Stopwatch.GetElapsedTime(start).TotalMilliseconds / units.Count);
                    bestCycles = Math.Min(bestCycles, (Vp8Timing.ThreadCycles() - cycles) / 1e6 / units.Count);
                }

                Console.WriteLine($"{Path.GetFileName(file)}: {best:F2} ms a unit on {threads} thread{(threads == 1 ? "" : "s")}{(threads == 1 ? $", {bestCycles:F1} M cycles" : "")}");
            }
        }
    }

    /// <summary>
    /// Prints the MD5 of every decoded frame's planes, one after another, film grain applied, for checking that a change
    /// decodes the same as before: <c>-- --time-av1 --md5 file.ivf</c>. Only 8-bit frames are hashed.
    /// </summary>
    private static void Md5(string[] files)
    {
        foreach (string file in files.Where(f => !f.StartsWith("--", StringComparison.Ordinal)))
        {
            Av1Stream stream = new("AV1") { Parallelism = 1 };
            using System.Security.Cryptography.IncrementalHash md5 = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.MD5);
            int frames = 0;
            foreach (byte[] unit in Vp8Timing.ReadIvf(file))
            {
                foreach (Av1Frame frame in stream.Decode(unit, 0, unit.Length))
                {
                    Av1FilmGrainSynthesis.Apply(frame);
                    if (!frame.Compact)
                    {
                        continue;
                    }

                    for (int plane = 0; plane < frame.Header.NumPlanes; plane++)
                    {
                        int ssx = plane > 0 ? frame.Sequence.SubsamplingX : 0, ssy = plane > 0 ? frame.Sequence.SubsamplingY : 0;
                        int w = (frame.Header.UpscaledWidth + ssx) >> ssx, h = (frame.Header.FrameHeight + ssy) >> ssy;
                        byte[] samples = frame.Plane<byte>(plane);
                        for (int y = 0; y < h; y++)
                        {
                            md5.AppendData(samples, y * frame.Strides[plane], w);
                        }
                    }

                    frames++;
                }
            }

            Console.WriteLine($"{Path.GetFileName(file)}: {frames} frames, MD5={Convert.ToHexStringLower(md5.GetHashAndReset())}");
        }
    }

    /// <summary>
    /// Times the 2.9.9 inter encoder: decodes the first frames of an AV1 IVF clip, then encodes them as a key frame and inter frames
    /// at each effort, and prints the frames a second, the bits and the luma PSNR.
    /// </summary>
    internal static void Encode(int frames, int quality, int[] efforts, string file, int threads)
    {
        List<byte[]> units = Vp8Timing.ReadIvf(file);
        List<YuvRaster> pictures = [];
        using (Av1StreamDecoder decoder = new())
        {
            foreach (byte[] unit in units)
            {
                decoder.Send(unit);
                while (decoder.NextFrame is Av1FrameInfo info && pictures.Count < frames)
                {
                    YuvRaster picture = info.BitDepth == 8 ? YuvRaster.Create(info.Width, info.Height, info.Chroma, Pixels.YuvColor.Bt709Limited)
                        : YuvRaster.Create(info.Width, info.Height, info.Chroma, info.BitDepth, Pixels.YuvColor.Bt709Limited);
                    decoder.TryReceiveFrame(picture, out _);
                    pictures.Add(picture);
                }

                if (pictures.Count >= frames)
                {
                    break;
                }
            }
        }

        YuvRaster first = pictures[0];
        EgamiContext context = EgamiContext.Default with { MaxParallelism = threads };
        foreach (int effort in efforts)
        {
            using Av1StreamEncoder encoder = new(new Av1SequenceOptions { Width = first.Width, Height = first.Height, BitDepth = first.BitDepth, Chroma = first.ChromaFormat }, context);
            long bits = 0;
            double psnr = 0;
            long start = Stopwatch.GetTimestamp();
            foreach (YuvRaster picture in pictures)
            {
                Av1EncodedFrame encoded = encoder.Encode(picture, new Av1FramePlan { Effort = effort, Quality = quality });
                bits += encoded.Bits;
                psnr += encoded.LumaPsnr;
            }

            double seconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
            Console.WriteLine(System.FormattableString.Invariant(
                $"{Path.GetFileName(file)} E{effort} q{quality}: {pictures.Count / seconds:F2} fps on {threads} threads, {bits / pictures.Count / 1000.0:F1} kbit a frame, PSNR-Y {psnr / pictures.Count:F2} dB"));
        }

        foreach (YuvRaster picture in pictures)
        {
            picture.Dispose();
        }
    }
}
