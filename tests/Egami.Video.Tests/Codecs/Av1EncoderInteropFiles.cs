using Egami.Colors;
using Egami.Pixels;
using Egami.Video.Codecs;

namespace Egami.Video.Tests.Codecs;

/// <summary>
/// Writes IVF files from Egami.Video's AV1 encoder through <see cref="VideoWriter"/>, each with the frames Egami's decoder makes of
/// it, for tools/video/check-av1-encoder.py to decode with dav1d and libaom: into EGAMI_INTEROP_DIR/av1video, each stream as
/// &lt;name&gt;.ivf, the shown frames as &lt;name&gt;.yuv (planar, 16-bit little-endian above 8 bits) and its shape as &lt;name&gt;.txt.
/// Each stream is also encoded at one thread and must give the same bytes. Runs only when EGAMI_INTEROP_DIR is set.
/// </summary>
public class Av1EncoderInteropFiles
{
    /// <summary>The streams: a size, a frame count, the frames' bit depth and chroma format, and the writer's options.</summary>
    internal static IEnumerable<(string Name, int Width, int Height, int Frames, int Depth, ChromaFormat Chroma, VideoWriterOptions Options)> Streams()
    {
        VideoWriterOptions av1 = new() { Codec = VideoCodecs.Av1 };
        foreach (int effort in new[] { 0, 2, 4, 6, 8 })
        {
            yield return ($"effort{effort}", 96, 64, 20, 8, ChromaFormat.Yuv420, av1 with { Effort = effort, Quality = 55 });
        }

        yield return ("pyramid", 128, 72, 40, 8, ChromaFormat.Yuv420, av1 with { Effort = 3, Av1 = new Av1EncoderOptions { GoldenInterval = 16 } });
        yield return ("flat", 96, 64, 30, 8, ChromaFormat.Yuv420, av1 with { Effort = 3, Av1 = new Av1EncoderOptions { Pyramid = false } });
        yield return ("no-altref", 96, 64, 20, 8, ChromaFormat.Yuv420, av1 with { Effort = 3, Av1 = new Av1EncoderOptions { AlternateReference = false } });
        yield return ("realtime", 96, 64, 16, 8, ChromaFormat.Yuv420, av1 with { Effort = 2, LatencyMode = LatencyMode.Realtime });
        yield return ("cbr", 96, 64, 24, 8, ChromaFormat.Yuv420, av1 with { Effort = 2, RateControl = RateControlMode.ConstantBitrate, Bitrate = 150_000 });
        yield return ("vbr", 96, 64, 24, 8, ChromaFormat.Yuv420, av1 with { Effort = 2, RateControl = RateControlMode.Bitrate, Bitrate = 200_000 });
        yield return ("keyint", 64, 48, 30, 8, ChromaFormat.Yuv420, av1 with { Effort = 2, KeyframeInterval = 9 });
        yield return ("q0", 48, 32, 6, 8, ChromaFormat.Yuv420, av1 with { Effort = 2, RateControl = RateControlMode.Quantizer, Quantizer = 0 });
        yield return ("q255", 64, 48, 12, 8, ChromaFormat.Yuv420, av1 with { Effort = 2, RateControl = RateControlMode.Quantizer, Quantizer = 255 });
        yield return ("no-aq", 96, 64, 12, 8, ChromaFormat.Yuv420, av1 with { Effort = 4, Av1 = new Av1EncoderOptions { AdaptiveQuantization = false } });
        yield return ("resilient", 96, 64, 12, 8, ChromaFormat.Yuv420, av1 with { Effort = 3, Av1 = new Av1EncoderOptions { ErrorResilient = true } });
        yield return ("odd-97x61", 97, 61, 12, 8, ChromaFormat.Yuv420, av1 with { Effort = 5 });
        yield return ("odd-17x9", 17, 9, 10, 8, ChromaFormat.Yuv420, av1 with { Effort = 7 });
        yield return ("odd-1x1", 1, 1, 4, 8, ChromaFormat.Yuv420, av1 with { Effort = 4 });
        yield return ("main-10", 72, 40, 12, 10, ChromaFormat.Yuv420, av1 with { Effort = 3 });
        yield return ("high-444-8", 72, 40, 12, 8, ChromaFormat.Yuv444, av1 with { Effort = 3 });
        yield return ("high-444-10", 64, 48, 12, 10, ChromaFormat.Yuv444, av1 with { Effort = 5 });
        yield return ("pro-420-12", 72, 40, 12, 12, ChromaFormat.Yuv420, av1 with { Effort = 4 });
        yield return ("pro-422-8", 61, 35, 12, 8, ChromaFormat.Yuv422, av1 with { Effort = 6 });
        yield return ("pro-422-10", 72, 40, 12, 10, ChromaFormat.Yuv422, av1 with { Effort = 3 });
        yield return ("pro-444-12", 48, 32, 10, 12, ChromaFormat.Yuv444, av1 with { Effort = 3 });
        yield return ("hdr", 72, 40, 12, 10, ChromaFormat.Yuv420, av1 with { Effort = 2 });
        yield return ("wide-tiles", 1280, 64, 6, 8, ChromaFormat.Yuv420, av1 with { Effort = 1 });
    }

    [Fact]
    public void WriteFiles()
    {
        string? directory = Environment.GetEnvironmentVariable("EGAMI_INTEROP_DIR");
        Assert.SkipUnless(directory is not null, "EGAMI_INTEROP_DIR is not set.");
        directory = Path.Combine(directory!, "av1video");
        Directory.CreateDirectory(directory);
        string? only = Environment.GetEnvironmentVariable("EGAMI_INTEROP_ONLY");
        foreach ((string name, int w, int h, int count, int depth, ChromaFormat chroma, VideoWriterOptions options) in Streams())
        {
            if (only is not null && !only.Split(',').Contains(name))
            {
                continue;
            }

            byte[] file = Write(name, w, h, count, depth, chroma, options, MediaContext.Default);
            byte[] single = Write(name, w, h, count, depth, chroma, options, new MediaContext { Egami = EgamiContext.Default with { MaxParallelism = 1 } });
            Assert.True(file.AsSpan().SequenceEqual(single), $"{name}: the stream differs at one thread.");
            string basePath = Path.Combine(directory, name);
            File.WriteAllBytes(basePath + ".ivf", file);
            using FileStream yuv = File.Create(basePath + ".yuv");
            using VideoReader reader = VideoReader.Open(new MemoryStream(file));
            int frames = 0;
            foreach (YuvVideoFrame frame in reader.ReadFrames())
            {
                using (frame)
                {
                    WritePlanes(yuv, frame.Raster);
                    frames++;
                }
            }

            Assert.Equal(count, frames);
            File.WriteAllText(basePath + ".txt", $"{w} {h} {depth} {chroma}");
        }
    }

    private static byte[] Write(string name, int w, int h, int count, int depth, ChromaFormat chroma, VideoWriterOptions options, MediaContext context)
    {
        using MemoryStream file = new();
        HdrMetadata? hdr = name == "hdr" ? new HdrMetadata
        {
            MasteringPrimaries = new ColorPrimaries(new Chromaticity(0.708, 0.292), new Chromaticity(0.17, 0.797), new Chromaticity(0.131, 0.046), new Chromaticity(0.3127, 0.329)),
            MasteringMaxLuminance = 1000,
            MasteringMinLuminance = 0.0001,
            MaxContentLightLevel = 1000,
            MaxFrameAverageLightLevel = 400,
        } : null;
        ColorCodePoints? color = name == "hdr" ? new ColorCodePoints(9, 16, 9, false) : null;
        using (VideoWriter writer = VideoWriter.Create(file, "ivf", options, context, leaveOpen: true))
        {
            using Vp8EncoderTests.Clip clip = depth == 8 && chroma == ChromaFormat.Yuv420 ? Vp8EncoderTests.Clip.Moving(w, h, count, cutAt: name == "keyint" ? 20 : -1) : new();
            for (int i = 0; i < count; i++)
            {
                using YuvRaster picture = depth == 8 && chroma == ChromaFormat.Yuv420 ? Vp8EncoderTests.Clip.Copy(clip.Frames[i]) : Av1EncoderTests.Synthetic(w, h, depth, chroma, i);
                using YuvVideoFrame frame = new(picture.Crop(new Rect(0, 0, w, h)), new MediaTime(i, options.FrameRate.Reciprocal()), color: color) { Hdr = hdr };
                writer.WriteFrame(frame);
            }
        }

        return file.ToArray();
    }

    private static void WritePlanes(Stream output, YuvRaster frame)
    {
        if (frame is YuvRaster<byte> bytes)
        {
            foreach (YuvPlane<byte>? plane in new[] { bytes.Y, bytes.U, bytes.V })
            {
                for (int y = 0; plane is not null && y < plane.Height; y++)
                {
                    output.Write(plane.Row(y)[..plane.Width]);
                }
            }

            return;
        }

        YuvRaster<ushort> words = (YuvRaster<ushort>)frame;
        foreach (YuvPlane<ushort>? plane in new[] { words.Y, words.U, words.V })
        {
            for (int y = 0; plane is not null && y < plane.Height; y++)
            {
                foreach (ushort v in plane.Row(y)[..plane.Width])
                {
                    output.WriteByte((byte)v);
                    output.WriteByte((byte)(v >> 8));
                }
            }
        }
    }
}
