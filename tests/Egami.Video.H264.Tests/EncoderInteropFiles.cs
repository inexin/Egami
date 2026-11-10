using Egami.Video.Codecs;

namespace Egami.Video.H264.Tests;

/// <summary>
/// Writes Annex B streams from the H.264 encoder, each with the frames Egami's decoder makes of it, for
/// tools/video/check-h264-encoder.py to decode with FFmpeg and the JM reference decoder: into EGAMI_INTEROP_DIR/h264video, each
/// stream as &lt;name&gt;.h264, the shown frames as &lt;name&gt;.yuv (planar 4:2:0) and its size as &lt;name&gt;.txt. Each stream is
/// also encoded at one thread and must give the same bytes. Runs only when EGAMI_INTEROP_DIR is set.
/// </summary>
public class EncoderInteropFiles
{
    /// <summary>The streams: a size, a frame count, the picture source and the encoder's options.</summary>
    internal static IEnumerable<(string Name, int Width, int Height, int Frames, Func<int, int, int, YuvRaster<byte>> Make, VideoEncoderOptions Options)> Streams()
    {
        Func<int, int, int, YuvRaster<byte>> moving = EncoderInterTests.Moving;
        foreach (int effort in new[] { 0, 1, 2, 4, 6, 8, 10 })
        {
            yield return ($"effort{effort}", 96, 64, 20, moving, Options(96, 64, effort, new H264EncoderOptions()));
        }

        yield return ("baseline", 96, 64, 16, moving, Options(96, 64, 4, new H264EncoderOptions { Profile = H264Profile.ConstrainedBaseline }));
        yield return ("main-cabac", 96, 64, 16, moving, Options(96, 64, 4, new H264EncoderOptions { Profile = H264Profile.Main }));
        yield return ("main-cavlc", 96, 64, 16, moving, Options(96, 64, 4, new H264EncoderOptions { Profile = H264Profile.Main, Entropy = H264EntropyCoder.Cavlc }));
        yield return ("high-cavlc", 96, 64, 16, moving, Options(96, 64, 6, new H264EncoderOptions { Entropy = H264EntropyCoder.Cavlc }));
        yield return ("p-only", 96, 64, 16, moving, Options(96, 64, 4, new H264EncoderOptions { BFrames = 0 }));
        yield return ("b1", 96, 64, 16, moving, Options(96, 64, 4, new H264EncoderOptions { BFrames = 1 }));
        yield return ("pyramid", 128, 72, 33, moving, Options(128, 72, 6, new H264EncoderOptions { BFrames = 7 }));
        yield return ("b16", 96, 64, 40, moving, Options(96, 64, 4, new H264EncoderOptions { BFrames = 16 }));
        yield return ("slices4", 128, 96, 12, moving, Options(128, 96, 4, new H264EncoderOptions { Slices = 4 }));
        yield return ("slices4-cavlc", 128, 96, 12, moving, Options(128, 96, 4, new H264EncoderOptions { Slices = 4, Entropy = H264EntropyCoder.Cavlc }));
        yield return ("slices32", 64, 48, 8, moving, Options(64, 48, 2, new H264EncoderOptions { Slices = 32 }));
        yield return ("fade", 96, 64, 12, EncoderInterTests.Fade, Options(96, 64, 4, new H264EncoderOptions { Profile = H264Profile.Main }));
        yield return ("fade-b", 96, 64, 12, EncoderInterTests.Fade, Options(96, 64, 6, new H264EncoderOptions { BFrames = 3 }));
        yield return ("no-aq", 96, 64, 12, moving, Options(96, 64, 4, new H264EncoderOptions { AdaptiveQuantization = false }));
        yield return ("keyint", 64, 48, 30, moving, Options(64, 48, 2, new H264EncoderOptions()) with { KeyframeInterval = 9 });
        yield return ("cbr", 96, 64, 24, moving, Options(96, 64, 2, new H264EncoderOptions()) with { RateControl = RateControlMode.ConstantBitrate, Bitrate = 150_000 });
        yield return ("vbr", 96, 64, 24, moving, Options(96, 64, 2, new H264EncoderOptions()) with { RateControl = RateControlMode.Bitrate, Bitrate = 200_000 });
        yield return ("realtime", 96, 64, 16, moving, Options(96, 64, 2, new H264EncoderOptions()) with { LatencyMode = LatencyMode.Realtime });
        yield return ("qp0", 48, 32, 6, moving, Options(48, 32, 2, new H264EncoderOptions()) with { RateControl = RateControlMode.Quantizer, Quantizer = 0 });
        yield return ("qp51", 64, 48, 12, moving, Options(64, 48, 2, new H264EncoderOptions()) with { RateControl = RateControlMode.Quantizer, Quantizer = 51 });
        yield return ("crop-98x62", 98, 62, 12, EncoderIntraTests.Picture, Options(98, 62, 5, new H264EncoderOptions()));
        yield return ("crop-18x10", 18, 10, 10, EncoderIntraTests.Picture, Options(18, 10, 7, new H264EncoderOptions()));
        yield return ("crop-2x2", 2, 2, 4, EncoderIntraTests.Picture, Options(2, 2, 4, new H264EncoderOptions()));
        yield return ("wide", 1280, 48, 6, moving, Options(1280, 48, 1, new H264EncoderOptions()));
    }

    [Fact]
    public void WriteFiles()
    {
        string? directory = Environment.GetEnvironmentVariable("EGAMI_INTEROP_DIR");
        Assert.SkipUnless(directory is not null, "EGAMI_INTEROP_DIR is not set.");
        directory = Path.Combine(directory!, "h264video");
        Directory.CreateDirectory(directory);
        string? only = Environment.GetEnvironmentVariable("EGAMI_INTEROP_ONLY");
        foreach ((string name, int w, int h, int count, Func<int, int, int, YuvRaster<byte>> make, VideoEncoderOptions options) in Streams())
        {
            if (only is not null && !only.Split(',').Contains(name))
            {
                continue;
            }

            (byte[] file, List<EncodedPacket> packets, VideoTrackInfo track) = Write(count, make, options, MediaContext.Default);
            (byte[] single, _, _) = Write(count, make, options, new MediaContext { Egami = EgamiContext.Default with { MaxParallelism = 1 } });
            Assert.True(file.AsSpan().SequenceEqual(single), $"{name}: the stream differs at one thread.");
            string basePath = Path.Combine(directory, name);
            File.WriteAllBytes(basePath + ".h264", file);
            List<YuvRaster<byte>> decoded = H264EncoderTests.Decode(track, packets);
            Assert.Equal(count, decoded.Count);
            using (FileStream yuv = File.Create(basePath + ".yuv"))
            {
                foreach (YuvRaster<byte> frame in decoded)
                {
                    yuv.Write(H264EncoderTests.Planes(frame));
                    frame.Dispose();
                }
            }

            foreach (EncodedPacket packet in packets)
            {
                packet.Dispose();
            }

            File.WriteAllText(basePath + ".txt", $"{w} {h} {count}");
        }
    }

    private static VideoEncoderOptions Options(int width, int height, int effort, H264EncoderOptions h264) => new()
    {
        Width = width,
        Height = height,
        Effort = effort,
        H264 = h264 with { AnnexB = true },
    };

    private static (byte[] File, List<EncodedPacket> Packets, VideoTrackInfo Track) Write(int count, Func<int, int, int, YuvRaster<byte>> make, VideoEncoderOptions options, MediaContext context)
    {
        using IVideoEncoder encoder = H264Codec.Instance.CreateEncoder(options, context);
        List<EncodedPacket> packets = [];
        Rational timeBase = options.FrameRate.Reciprocal();
        for (int i = 0; i < count; i++)
        {
            using YuvRaster<byte> picture = make(options.Width, options.Height, i);
            encoder.Send(new YuvVideoFrame(picture, new MediaTime(i, timeBase)));
            while (encoder.TryReceive(out EncodedPacket? packet))
            {
                packets.Add(packet);
            }
        }

        encoder.Flush();
        while (encoder.TryReceive(out EncodedPacket? packet))
        {
            packets.Add(packet);
        }

        Assert.Equal(count, packets.Count);
        using MemoryStream file = new();
        foreach (EncodedPacket packet in packets)
        {
            file.Write(packet.Data.Span);
        }

        return (file.ToArray(), packets, ((H264VideoEncoder)encoder).Track);
    }
}
