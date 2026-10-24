using Egami.Colors;
using Egami.Formats.Av1;
using Egami.Pixels;
using Egami.Video.Codecs;
using Egami.Video.Codecs.Av1;

using Clip = Egami.Video.Tests.Codecs.Vp8EncoderTests.Clip;

namespace Egami.Video.Tests.Codecs;

public class Av1EncoderTests
{
    private static readonly Rational Rate = new(30, 1);

    [Fact]
    public void FramesDecodeCloseToTheSourceAndHiddenFramesAreShownLater()
    {
        using Clip clip = Clip.Moving(96, 64, 34, speed: 0);
        (VideoTrackInfo track, List<EncodedPacket> packets) = Encode(clip, new VideoEncoderOptions { Width = 96, Height = 64, Quality = 80, Effort = 2 });
        try
        {
            Assert.Equal(CodecIds.Av1, track.CodecId);
            Assert.True(packets[0].IsKeyframe);
            Assert.False(track.CodecPrivate.IsEmpty);
            Assert.Equal(0x81, track.CodecPrivate.Span[0]);

            // One packet per frame: hidden frames lead the next shown frame's unit, and are shown later with show_existing_frame.
            Assert.Equal(clip.Frames.Count, packets.Count);
            Assert.Contains(packets, p => Av1Packets.SplitFrames(p.Data).Count >= 2);
            Assert.Contains(packets, p => ShowsExisting(p.Data.Span));
            List<YuvRaster<byte>> decoded = Decode(track, packets);
            Assert.Equal(clip.Frames.Count, decoded.Count);
            for (int i = 0; i < decoded.Count; i++)
            {
                double psnr = Vp8EncoderTests.Psnr(clip.Frames[i].Y, decoded[i].Y);
                Assert.True(psnr > 33, $"frame {i}: {psnr:F1} dB");
                decoded[i].Dispose();
            }

            for (int i = 1; i < packets.Count; i++)
            {
                Assert.True(packets[i].Timestamp > packets[i - 1].Timestamp);
            }
        }
        finally
        {
            packets.ForEach(p => p.Dispose());
        }
    }

    [Fact]
    public void PyramidHidesFramesHalfwayDownToPairs()
    {
        // A group of 16: the alternate reference, then hidden frames at 8, 4, 2, 6, 12, 10 and 14, each shown again later.
        using Clip clip = Clip.Moving(64, 48, 20, speed: 0);
        VideoEncoderOptions options = new() { Width = 64, Height = 48, Effort = 1, Av1 = new Av1EncoderOptions { GoldenInterval = 16 } };
        (_, List<EncodedPacket> pyramid) = Encode(clip, options);
        (_, List<EncodedPacket> flat) = Encode(clip, options with { Av1 = new Av1EncoderOptions { GoldenInterval = 16, Pyramid = false } });
        int existing = pyramid.Count(p => ShowsExisting(p.Data.Span)), flatExisting = flat.Count(p => ShowsExisting(p.Data.Span));
        pyramid.ForEach(p => p.Dispose());
        flat.ForEach(p => p.Dispose());
        Assert.True(existing >= 6 && flatExisting <= 2, $"{existing} frames shown again with the pyramid, {flatExisting} without");
    }

    [Fact]
    public void RealtimeGivesEachPacketAtOnce()
    {
        using Clip clip = Clip.Moving(64, 48, 8);
        using IVideoEncoder encoder = VideoCodecs.Av1.CreateEncoder(new VideoEncoderOptions { Width = 64, Height = 48, LatencyMode = LatencyMode.Realtime, Effort = 1 }, MediaContext.Default);
        for (int i = 0; i < clip.Frames.Count; i++)
        {
            using YuvVideoFrame frame = clip.Frame(i);
            encoder.Send(frame);
            Assert.True(encoder.TryReceive(out EncodedPacket? packet));
            using (packet)
            {
                Assert.Equal(i == 0, packet.IsKeyframe);
                Assert.Equal(new MediaTime(i, Rate.Reciprocal()), packet.Timestamp);
                Assert.Single(Av1Packets.SplitFrames(packet.Data));
            }

            Assert.False(encoder.TryReceive(out _));
        }
    }

    [Fact]
    public void OutputIsTheSameAtAnyThreadCount()
    {
        using Clip clip = Clip.Moving(160, 96, 12);
        VideoEncoderOptions options = new() { Width = 160, Height = 96, Effort = 3 };
        byte[] one = Bytes(clip, options, new MediaContext { Egami = EgamiContext.Default with { MaxParallelism = 1 } });
        byte[] many = Bytes(clip, options, new MediaContext { Egami = EgamiContext.Default with { MaxParallelism = 8 } });
        Assert.Equal(one, many);
    }

    [Fact]
    public void KeyframesComeAtSceneCutsTheIntervalAndOnRequest()
    {
        using Clip clip = Clip.Moving(64, 48, 60, cutAt: 25);
        using IVideoEncoder encoder = VideoCodecs.Av1.CreateEncoder(new VideoEncoderOptions { Width = 64, Height = 48, KeyframeInterval = 50, Effort = 0 }, MediaContext.Default);
        List<long> keys = [];
        for (int i = 0; i < clip.Frames.Count; i++)
        {
            using YuvVideoFrame frame = clip.Frame(i);
            encoder.Send(frame, new EncodeFrameOptions { ForceKeyframe = i == 40 });
            Drain(encoder, keys);
        }

        encoder.Flush();
        Drain(encoder, keys);
        Assert.Equal([0L, 25, 40], keys);
    }

    [Fact]
    public void BitrateLandsWithinFifteenPercent()
    {
        using Clip clip = Clip.Moving(160, 96, 90);
        long bitrate = 300_000;
        (_, List<EncodedPacket> packets) = Encode(clip, new VideoEncoderOptions { Width = 160, Height = 96, RateControl = RateControlMode.Bitrate, Bitrate = bitrate, Effort = 1 });
        long bits = packets.Sum(p => p.Data.Length * 8L);
        packets.ForEach(p => p.Dispose());
        double actual = bits / (clip.Frames.Count / 30.0);
        Assert.True(actual / bitrate is >= 0.85 and <= 1.15, $"{actual / bitrate:F3}");
    }

    [Fact]
    public void ConstantBitrateNeverRunsTheBufferDry()
    {
        using Clip clip = Clip.Moving(160, 96, 40, cutAt: 20);
        long bitrate = 200_000, buffer = 200_000;
        (_, List<EncodedPacket> packets) = Encode(clip, new VideoEncoderOptions
        {
            Width = 160,
            Height = 96,
            RateControl = RateControlMode.ConstantBitrate,
            Bitrate = bitrate,
            BufferSize = buffer,
            LatencyMode = LatencyMode.Realtime,
            Effort = 1,
        });

        double level = buffer * 0.75, perFrame = bitrate / 30.0;
        foreach (EncodedPacket packet in packets)
        {
            level = Math.Min(buffer, level + perFrame) - (packet.Data.Length * 8);
            Assert.True(level >= 0, $"buffer at {level:F0} bits");
            packet.Dispose();
        }
    }

    [Theory]
    [InlineData("webm", 8, ChromaFormat.Yuv420, 0)]
    [InlineData("mp4", 10, ChromaFormat.Yuv420, 0)]
    [InlineData("mkv", 10, ChromaFormat.Yuv444, 1)]
    [InlineData("mp4", 12, ChromaFormat.Yuv420, 2)]
    [InlineData("mkv", 8, ChromaFormat.Yuv422, 2)]
    [InlineData("ivf", 10, ChromaFormat.Yuv420, 0)]
    [InlineData("AV1 OBU", 8, ChromaFormat.Yuv444, 1)]
    [InlineData("AV1 Annex B", 10, ChromaFormat.Yuv420, 0)]
    public void ProfilesContainersColorAndHdrMetadataRoundTrip(string format, int depth, ChromaFormat chroma, int profile)
    {
        const int W = 72, H = 40;
        ColorCodePoints pq = new(9, 16, 9, false);
        HdrMetadata hdr = new()
        {
            MasteringPrimaries = new ColorPrimaries(new Chromaticity(0.708, 0.292), new Chromaticity(0.17, 0.797), new Chromaticity(0.131, 0.046), new Chromaticity(0.3127, 0.329)),
            MasteringMaxLuminance = 1000,
            MasteringMinLuminance = 0.005,
            MaxContentLightLevel = 800,
            MaxFrameAverageLightLevel = 300,
        };
        using MemoryStream file = new();
        List<YuvRaster> sources = [];
        VideoTrackInfo written;
        using (VideoWriter writer = VideoWriter.Create(file, format, new VideoWriterOptions { Codec = VideoCodecs.Av1, Quality = 85, Effort = 1 }, leaveOpen: true))
        {
            for (int i = 0; i < 8; i++)
            {
                YuvRaster picture = Synthetic(W, H, depth, chroma, i);
                sources.Add(picture);
                using YuvVideoFrame frame = new(picture.Crop(new Rect(0, 0, W, H)), new MediaTime(i, Rate.Reciprocal()), color: pq) { Hdr = hdr };
                writer.WriteFrame(frame);
            }

            written = writer.Track!;
        }

        Av1Config config = Av1Config.Parse(written.CodecPrivate.Span)!.Value;
        Assert.Equal(profile, config.Profile);
        file.Position = 0;
        using VideoReader reader = VideoReader.Open(file);
        VideoTrackInfo track = reader.Track;
        Assert.Equal(depth, track.BitDepth);
        Assert.Equal(chroma, track.ChromaFormat);
        int n = 0;
        foreach (YuvVideoFrame frame in reader.ReadFrames())
        {
            using (frame)
            {
                Assert.Equal(depth, frame.Raster.BitDepth);
                Assert.Equal(chroma, frame.Raster.ChromaFormat);
                Assert.Equal(16, frame.Color!.Value.TransferCharacteristics);
                Assert.Equal(9, frame.Color!.Value.Primaries);
                Assert.Equal(800, frame.Hdr!.MaxContentLightLevel);
                Assert.Equal(1000, frame.Hdr!.MasteringMaxLuminance!.Value, 2);
                Assert.Equal(0.708, frame.Hdr!.MasteringPrimaries!.Red.X, 3);
                Assert.True(LumaPsnr(sources[n], frame.Raster) > 30, $"frame {n}");
                n++;
            }
        }

        Assert.Equal(8, n);
        sources.ForEach(s => s.Dispose());
    }

    [Fact]
    public void Hdr10PlusGoesIntoTheFramesThatHaveIt()
    {
        byte[] t35 = [0xB5, 0x00, 0x3C, 0x00, 0x01, 0x04, 0x01, 0x40, 0x00, 0x0C, 0x80, 0x4E, 0x20, 0x27, 0x10, 0x00, 0x0A, 0x00, 0x00, 0x03, 0x20, 0x10, 0x00, 0x00, 0x00];
        Hdr10PlusMetadata meta = new() { Raw = t35 };
        using Clip clip = Clip.Moving(64, 48, 6);
        using IVideoEncoder encoder = VideoCodecs.Av1.CreateEncoder(new VideoEncoderOptions { Width = 64, Height = 48, Effort = 0, LatencyMode = LatencyMode.Realtime }, MediaContext.Default);
        List<EncodedPacket> packets = [];
        for (int i = 0; i < clip.Frames.Count; i++)
        {
            using YuvVideoFrame frame = new(clip.Frames[i].Crop(new Rect(0, 0, 64, 48)), new MediaTime(i, Rate.Reciprocal())) { Hdr10Plus = i == 2 ? meta : null };
            encoder.Send(frame);
            while (encoder.TryReceive(out EncodedPacket? packet))
            {
                packets.Add(packet);
            }
        }

        for (int i = 0; i < packets.Count; i++)
        {
            Assert.Equal(i == 2, packets[i].Data.Span.IndexOf(t35.AsSpan(0, 8)) >= 0);
            packets[i].Dispose();
        }
    }

    [Fact]
    public void BadOptionsAreRefused()
    {
        MediaContext c = MediaContext.Default;
        Assert.Throws<ArgumentException>(() => VideoCodecs.Av1.CreateEncoder(new VideoEncoderOptions { Width = 16, Height = 16, RateControl = RateControlMode.Bitrate }, c));
        Assert.Throws<ArgumentOutOfRangeException>(() => VideoCodecs.Av1.CreateEncoder(new VideoEncoderOptions { Width = 16, Height = 16, Quality = 101 }, c));
        Assert.Throws<ArgumentOutOfRangeException>(() => VideoCodecs.Av1.CreateEncoder(new VideoEncoderOptions { Width = 16, Height = 16, Quantizer = 256 }, c));
        Assert.Throws<ArgumentOutOfRangeException>(() => VideoCodecs.Av1.CreateEncoder(new VideoEncoderOptions { Width = 16, Height = 16, Effort = 11 }, c));
        Assert.Throws<ArgumentException>(() => VideoCodecs.Av1.CreateEncoder(new VideoEncoderOptions { Width = 16, Height = 16, Av1 = new Av1EncoderOptions { MinQuantizer = 50, MaxQuantizer = 40 } }, c));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Av1EncoderOptions { GoldenInterval = 3 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new Av1EncoderOptions { BitDepth = 9 });
        Assert.True(VideoCodecs.Av1.CanEncode);
        using IVideoEncoder encoder = VideoCodecs.Av1.CreateEncoder(new VideoEncoderOptions { Width = 16, Height = 16 }, c);
        using VideoFrame<Rgb8> frame = new(Raster.Create<Rgb8>(8, 8), MediaTime.Zero);
        Assert.Throws<ArgumentException>(() => encoder.Send(frame));
    }

    [Theory]
    [InlineData(176, 144, 30, 0)]
    [InlineData(1280, 720, 30, 5)]
    [InlineData(1280, 720, 60, 8)]
    [InlineData(1920, 1080, 30, 8)]
    [InlineData(1920, 1080, 50, 9)]
    [InlineData(3840, 2160, 60, 13)]
    public void LevelHoldsThePictureSizeAndRate(int width, int height, int fps, int level) => Assert.Equal(level, Av1VideoEncoder.Level(width, height, fps));

    [Fact]
    public void RgbFramesAreCodedAsBt601()
    {
        using Raster<Rgb8> rgb = Raster.Create<Rgb8>(32, 32);
        using IVideoEncoder encoder = VideoCodecs.Av1.CreateEncoder(new VideoEncoderOptions { Width = 32, Height = 32, Effort = 0 }, MediaContext.Default);
        encoder.Send(new VideoFrame<Rgb8>(rgb, MediaTime.Zero));
        encoder.Flush();
        Assert.True(encoder.TryReceive(out EncodedPacket? packet));
        packet.Dispose();
        Assert.Equal(6, encoder.Track.Color!.Value.MatrixCoefficients);
        Assert.Equal(8, encoder.Track.BitDepth);
    }

    /// <summary>Whether a temporal unit holds a frame header OBU with show_existing_frame set.</summary>
    internal static bool ShowsExisting(ReadOnlySpan<byte> unit)
    {
        int at = 0;
        while (at < unit.Length)
        {
            int type = (unit[at] >> 3) & 15;
            bool extension = (unit[at] & 4) != 0;
            int header = at + 1 + (extension ? 1 : 0);
            ulong size = 0;
            int shift = 0, i = header;
            while (true)
            {
                byte b = unit[i++];
                size |= (ulong)(b & 0x7F) << shift;
                shift += 7;
                if ((b & 0x80) == 0)
                {
                    break;
                }
            }

            if (type == 3 && (unit[i] & 0x80) != 0)
            {
                return true;
            }

            at = i + (int)size;
        }

        return false;
    }

    internal static YuvRaster Synthetic(int w, int h, int depth, ChromaFormat chroma, int frame)
    {
        YuvColor color = new(YuvMatrix.Bt2020Ncl, YuvRange.Limited, ChromaSiting.Center);
        if (depth == 8)
        {
            YuvRaster<byte> p8 = YuvRaster.Create(w, h, chroma, color);
            Fill(p8.Y, p8.U!, p8.V!, 255, frame);
            return p8;
        }

        YuvRaster<ushort> p16 = YuvRaster.Create(w, h, chroma, depth, color);
        Fill(p16.Y, p16.U!, p16.V!, (1 << depth) - 1, frame);
        return p16;
    }

    private static void Fill<T>(YuvPlane<T> y, YuvPlane<T> u, YuvPlane<T> v, int max, int frame)
        where T : unmanaged, System.Numerics.IBinaryInteger<T>
    {
        foreach ((YuvPlane<T> plane, int p) in new[] { (y, 0), (u, 1), (v, 2) })
        {
            for (int r = 0; r < plane.Height; r++)
            {
                Span<T> row = plane.Row(r);
                for (int x = 0; x < row.Length; x++)
                {
                    double value = p == 0 ? 0.5 + (0.3 * Math.Sin((x + frame) * 0.2) * Math.Cos(r * 0.15)) : 0.5 + (0.1 * Math.Sin((x * (p + 1)) * 0.1));
                    row[x] = T.CreateTruncating((int)(value * max));
                }
            }
        }
    }

    private static double LumaPsnr(YuvRaster a, YuvRaster b)
    {
        double max = (1 << a.BitDepth) - 1, sum = 0;
        int w = a.Width, h = a.Height;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double d = Sample(a, x, y) - Sample(b, x, y);
                sum += d * d;
            }
        }

        return sum == 0 ? 99 : 10 * Math.Log10(max * max * w * h / sum);

        static int Sample(YuvRaster r, int x, int y) => r is YuvRaster<byte> b ? b.Y.Row(y)[x] : ((YuvRaster<ushort>)r).Y.Row(y)[x];
    }

    private static void Drain(IVideoEncoder encoder, List<long> keys)
    {
        while (encoder.TryReceive(out EncodedPacket? packet))
        {
            using (packet)
            {
                if (packet.IsKeyframe)
                {
                    keys.Add(packet.Timestamp.Ticks);
                }
            }
        }
    }

    private static byte[] Bytes(Clip clip, VideoEncoderOptions options, MediaContext context)
    {
        (_, List<EncodedPacket> packets) = Encode(clip, options, context);
        byte[] all = [.. packets.SelectMany(p => p.Data.ToArray())];
        packets.ForEach(p => p.Dispose());
        return all;
    }

    internal static (VideoTrackInfo Track, List<EncodedPacket> Packets) Encode(Clip clip, VideoEncoderOptions options, MediaContext? context = null)
    {
        using IVideoEncoder encoder = VideoCodecs.Av1.CreateEncoder(options, context ?? MediaContext.Default);
        List<EncodedPacket> packets = [];
        for (int i = 0; i < clip.Frames.Count; i++)
        {
            using YuvVideoFrame frame = clip.Frame(i);
            encoder.Send(frame);
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

        return (encoder.Track, packets);
    }

    private static List<YuvRaster<byte>> Decode(VideoTrackInfo track, List<EncodedPacket> packets)
    {
        using IVideoDecoder decoder = VideoCodecs.Av1.CreateDecoder(track, null, MediaContext.Default);
        List<YuvRaster<byte>> frames = [];
        foreach (EncodedPacket packet in packets)
        {
            decoder.Send(packet);
            while (decoder.TryReceive(out YuvVideoFrame? frame))
            {
                frames.Add(Clip.Copy((YuvRaster<byte>)frame.Raster));
                frame.Dispose();
            }
        }

        decoder.Flush();
        while (decoder.TryReceive(out YuvVideoFrame? frame))
        {
            frames.Add(Clip.Copy((YuvRaster<byte>)frame.Raster));
            frame.Dispose();
        }

        return frames;
    }
}
