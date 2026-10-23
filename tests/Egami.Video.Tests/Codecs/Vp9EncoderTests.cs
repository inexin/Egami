using Egami.Colors;
using Egami.Pixels;
using Egami.Video.Codecs;
using Egami.Video.Codecs.Vp9;

using Clip = Egami.Video.Tests.Codecs.Vp8EncoderTests.Clip;

namespace Egami.Video.Tests.Codecs;

public class Vp9EncoderTests
{
    private static readonly Rational Rate = new(30, 1);

    [Fact]
    public void FramesDecodeCloseToTheSourceAndHiddenFramesRideInSuperframes()
    {
        using Clip clip = Clip.Moving(96, 64, 40, speed: 0);
        (VideoTrackInfo track, List<EncodedPacket> packets) = Encode(clip, new VideoEncoderOptions { Width = 96, Height = 64, Quality = 80 });
        try
        {
            Assert.Equal(CodecIds.Vp9, track.CodecId);
            Assert.True(packets[0].IsKeyframe);

            // One packet per frame: hidden alternate references go out in a superframe with the next shown frame.
            Assert.Equal(clip.Frames.Count, packets.Count);
            Assert.Contains(packets, p => (p.Data.Span[^1] & 0xe0) == 0xc0 && Vp9Superframe.Split(p.Data).Count == 2);
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
    public void RealtimeGivesEachPacketAtOnce()
    {
        using Clip clip = Clip.Moving(64, 48, 12);
        using IVideoEncoder encoder = VideoCodecs.Vp9.CreateEncoder(new VideoEncoderOptions { Width = 64, Height = 48, LatencyMode = LatencyMode.Realtime }, MediaContext.Default);
        for (int i = 0; i < clip.Frames.Count; i++)
        {
            using YuvVideoFrame frame = clip.Frame(i);
            encoder.Send(frame);
            Assert.True(encoder.TryReceive(out EncodedPacket? packet));
            using (packet)
            {
                Assert.Equal(i == 0, packet.IsKeyframe);
                Assert.Equal(new MediaTime(i, Rate.Reciprocal()), packet.Timestamp);
                Assert.Equal(1, (packet.Data.Span[0] >> 1) & 1); // show_frame
            }

            Assert.False(encoder.TryReceive(out _));
        }
    }

    [Fact]
    public void OutputIsTheSameAtAnyThreadCount()
    {
        // 520 pixels wide: two tile columns, which the threads take in parallel.
        using Clip clip = Clip.Moving(520, 64, 8);
        VideoEncoderOptions options = new() { Width = 520, Height = 64, Effort = 2 };
        byte[] one = Bytes(clip, options, new MediaContext { Egami = EgamiContext.Default with { MaxParallelism = 1 } });
        byte[] many = Bytes(clip, options, new MediaContext { Egami = EgamiContext.Default with { MaxParallelism = 8 } });
        Assert.Equal(one, many);
    }

    [Fact]
    public void KeyframesComeAtSceneCutsTheIntervalAndOnRequest()
    {
        using Clip clip = Clip.Moving(64, 48, 60, cutAt: 25);
        using IVideoEncoder encoder = VideoCodecs.Vp9.CreateEncoder(new VideoEncoderOptions { Width = 64, Height = 48, KeyframeInterval = 50, Effort = 1 }, MediaContext.Default);
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
        (_, List<EncodedPacket> packets) = Encode(clip, new VideoEncoderOptions { Width = 160, Height = 96, RateControl = RateControlMode.Bitrate, Bitrate = bitrate, Effort = 2 });
        long bits = packets.Sum(p => p.Data.Length * 8L);
        packets.ForEach(p => p.Dispose());
        double actual = bits / (clip.Frames.Count / 30.0);
        Assert.InRange(actual / bitrate, 0.85, 1.15);
    }

    [Fact]
    public void ConstantBitrateNeverRunsTheBufferDry()
    {
        using Clip clip = Clip.Moving(160, 96, 60, cutAt: 30);
        long bitrate = 200_000, buffer = 200_000;
        (_, List<EncodedPacket> packets) = Encode(clip, new VideoEncoderOptions
        {
            Width = 160,
            Height = 96,
            RateControl = RateControlMode.ConstantBitrate,
            Bitrate = bitrate,
            BufferSize = buffer,
            LatencyMode = LatencyMode.Realtime,
            Effort = 2,
        });

        double level = buffer * 0.75, perFrame = bitrate / 30.0;
        foreach (EncodedPacket packet in packets)
        {
            level = Math.Min(buffer, level + perFrame) - (packet.Data.Length * 8);
            Assert.True(level >= 0, $"buffer at {level:F0} bits");
            packet.Dispose();
        }
    }

    [Fact]
    public void AlphaRoundTripsThroughWebM()
    {
        using Clip clip = Clip.Moving(64, 48, 12, alpha: true);
        using MemoryStream file = new();
        using (VideoWriter writer = VideoWriter.Create(file, "webm", new VideoWriterOptions { Codec = VideoCodecs.Vp9, Quality = 90 }, leaveOpen: true))
        {
            foreach (YuvRaster<byte> picture in clip.Frames)
            {
                writer.WriteFrame(picture);
            }

            Assert.True(writer.Track!.HasAlpha);
        }

        file.Position = 0;
        using VideoReader reader = VideoReader.Open(file);
        int n = 0;
        foreach (YuvVideoFrame frame in reader.ReadFrames())
        {
            using (frame)
            {
                YuvRaster<byte> raster = (YuvRaster<byte>)frame.Raster;
                Assert.NotNull(raster.A);
                Assert.True(Vp8EncoderTests.Psnr(clip.Frames[n].A!, raster.A!) > 35);
                Assert.True(Vp8EncoderTests.Psnr(clip.Frames[n].Y, raster.Y) > 33);
                n++;
            }
        }

        Assert.Equal(clip.Frames.Count, n);
    }

    [Theory]
    [InlineData("webm", 10, ChromaFormat.Yuv420)]
    [InlineData("mp4", 10, ChromaFormat.Yuv420)]
    [InlineData("mkv", 12, ChromaFormat.Yuv444)]
    [InlineData("webm", 8, ChromaFormat.Yuv422)]
    public void DeepAndFullChromaFramesKeepTheirFormatColorAndHdrMetadata(string format, int depth, ChromaFormat chroma)
    {
        const int W = 72, H = 40;
        ColorCodePoints pq = new(9, 16, 9, false);
        HdrMetadata hdr = new() { MasteringMaxLuminance = 1000, MasteringMinLuminance = 0.005, MaxContentLightLevel = 800, MaxFrameAverageLightLevel = 300 };
        using MemoryStream file = new();
        List<YuvRaster> sources = [];
        using (VideoWriter writer = VideoWriter.Create(file, format, new VideoWriterOptions { Codec = VideoCodecs.Vp9, Quality = 85, Effort = 2 }, leaveOpen: true))
        {
            for (int i = 0; i < 8; i++)
            {
                YuvRaster picture = Synthetic(W, H, depth, chroma, i);
                sources.Add(picture);
                using YuvVideoFrame frame = new(picture.Crop(new Rect(0, 0, W, H)), new MediaTime(i, Rate.Reciprocal()), color: pq) { Hdr = hdr };
                writer.WriteFrame(frame);
            }
        }

        file.Position = 0;
        using VideoReader reader = VideoReader.Open(file);
        VideoTrackInfo track = reader.Track;
        Assert.Equal(depth, track.BitDepth);
        Assert.Equal(chroma, track.ChromaFormat);
        Assert.Equal(16, track.Color!.Value.TransferCharacteristics);
        Assert.Equal(9, track.Color!.Value.Primaries);
        Assert.Equal(800, track.Hdr!.MaxContentLightLevel);
        int n = 0;
        foreach (YuvVideoFrame frame in reader.ReadFrames())
        {
            using (frame)
            {
                Assert.Equal(depth, frame.Raster.BitDepth);
                Assert.Equal(chroma, frame.Raster.ChromaFormat);
                Assert.True(LumaPsnr(sources[n], frame.Raster) > 30, $"frame {n}");
                n++;
            }
        }

        Assert.Equal(8, n);
        sources.ForEach(s => s.Dispose());
    }

    [Fact]
    public void BadOptionsAreRefused()
    {
        MediaContext c = MediaContext.Default;
        Assert.Throws<ArgumentException>(() => VideoCodecs.Vp9.CreateEncoder(new VideoEncoderOptions { Width = 16, Height = 16, RateControl = RateControlMode.Bitrate }, c));
        Assert.Throws<ArgumentOutOfRangeException>(() => VideoCodecs.Vp9.CreateEncoder(new VideoEncoderOptions { Width = 16, Height = 16, Quality = 101 }, c));
        Assert.Throws<ArgumentOutOfRangeException>(() => VideoCodecs.Vp9.CreateEncoder(new VideoEncoderOptions { Width = 16, Height = 16, Quantizer = 256 }, c));
        Assert.Throws<ArgumentOutOfRangeException>(() => VideoCodecs.Vp9.CreateEncoder(new VideoEncoderOptions { Width = 16, Height = 16, Effort = 11 }, c));
        Assert.Throws<ArgumentException>(() => VideoCodecs.Vp9.CreateEncoder(new VideoEncoderOptions { Width = 16, Height = 16, Vp9 = new Vp9EncoderOptions { MinQuantizer = 50, MaxQuantizer = 40 } }, c));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Vp9EncoderOptions { TileColumns = 3 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new Vp9EncoderOptions { BitDepth = 9 });
        Assert.True(VideoCodecs.Vp9.CanEncode);
        using IVideoEncoder encoder = VideoCodecs.Vp9.CreateEncoder(new VideoEncoderOptions { Width = 16, Height = 16 }, c);
        using VideoFrame<Rgb8> frame = new(Raster.Create<Rgb8>(8, 8), MediaTime.Zero);
        Assert.Throws<ArgumentException>(() => encoder.Send(frame));
    }

    [Theory]
    [InlineData(0, 254)]
    [InlineData(60, 66)]
    [InlineData(100, 0)]
    public void QualityMapsToTwiceVp8sIndex(int quality, int quantizer) => Assert.Equal(quantizer, Vp9VideoEncoder.QualityToQuantizer(quality));

    private static YuvRaster Synthetic(int w, int h, int depth, ChromaFormat chroma, int frame)
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

    private static (VideoTrackInfo Track, List<EncodedPacket> Packets) Encode(Clip clip, VideoEncoderOptions options, MediaContext? context = null)
    {
        using IVideoEncoder encoder = VideoCodecs.Vp9.CreateEncoder(options, context ?? MediaContext.Default);
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
        using IVideoDecoder decoder = VideoCodecs.Vp9.CreateDecoder(track, null, MediaContext.Default);
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
