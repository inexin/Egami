using Egami.Pixels;
using Egami.Video.Codecs;
using Egami.Video.Codecs.Vp8;

namespace Egami.Video.Tests.Codecs;

public class Vp8EncoderTests
{
    private static readonly Rational Rate = new(30, 1);

    [Fact]
    public void FramesDecodeCloseToTheSourceAndHiddenFramesShowNothing()
    {
        // A still picture with a little noise: the frames reach an alternate reference easily.
        using Clip clip = Clip.Moving(96, 64, 40, speed: 0);
        (VideoTrackInfo track, List<EncodedPacket> packets) = Encode(clip, new VideoEncoderOptions { Width = 96, Height = 64, Quality = 80 });
        try
        {
            Assert.Equal(CodecIds.Vp8, track.CodecId);
            Assert.True(packets[0].IsKeyframe);

            // Quality latency codes hidden alternate references: more packets than frames.
            Assert.True(packets.Count > clip.Frames.Count, $"{packets.Count} packets for {clip.Frames.Count} frames");
            List<YuvRaster<byte>> decoded = Decode(track, packets);
            Assert.Equal(clip.Frames.Count, decoded.Count);
            for (int i = 0; i < decoded.Count; i++)
            {
                Assert.True(Psnr(clip.Frames[i].Y, decoded[i].Y) > 33, $"frame {i}: {Psnr(clip.Frames[i].Y, decoded[i].Y):F1} dB");
                decoded[i].Dispose();
            }

            // Packets come in decode order; shown timestamps rise, a hidden frame shares the next shown frame's.
            List<MediaTime> times = [.. packets.Select(p => p.Timestamp)];
            for (int i = 1; i < times.Count; i++)
            {
                Assert.True(times[i] >= times[i - 1]);
            }
        }
        finally
        {
            packets.ForEach(p => p.Dispose());
        }
    }

    [Fact]
    public void RealtimeGivesEachPacketAtOnceAndNoHiddenFrames()
    {
        using Clip clip = Clip.Moving(64, 48, 20);
        using IVideoEncoder encoder = VideoCodecs.Vp8.CreateEncoder(new VideoEncoderOptions { Width = 64, Height = 48, LatencyMode = LatencyMode.Realtime }, MediaContext.Default);
        for (int i = 0; i < clip.Frames.Count; i++)
        {
            using YuvVideoFrame frame = clip.Frame(i);
            encoder.Send(frame);
            Assert.True(encoder.TryReceive(out EncodedPacket? packet));
            using (packet)
            {
                Assert.Equal(i == 0, packet.IsKeyframe);
                Assert.Equal(new MediaTime(i, Rate.Reciprocal()), packet.Timestamp);
                Assert.Equal(1, (packet.Data.Span[0] >> 4) & 1); // show_frame
            }

            Assert.False(encoder.TryReceive(out _));
        }
    }

    [Fact]
    public void OutputIsTheSameAtAnyThreadCount()
    {
        using Clip clip = Clip.Moving(80, 48, 30);
        VideoEncoderOptions options = new() { Width = 80, Height = 48 };
        byte[] one = Bytes(clip, options, new MediaContext { Egami = EgamiContext.Default with { MaxParallelism = 1 } });
        byte[] many = Bytes(clip, options, new MediaContext { Egami = EgamiContext.Default with { MaxParallelism = 8 } });
        Assert.Equal(one, many);
    }

    [Fact]
    public void KeyframesComeAtSceneCutsTheIntervalAndOnRequest()
    {
        using Clip clip = Clip.Moving(64, 48, 60, cutAt: 25);
        using IVideoEncoder encoder = VideoCodecs.Vp8.CreateEncoder(new VideoEncoderOptions { Width = 64, Height = 48, KeyframeInterval = 50 }, MediaContext.Default);
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
    public void FixedSpacingWithoutSceneCuts()
    {
        using Clip clip = Clip.Moving(64, 48, 40, cutAt: 25);
        using IVideoEncoder encoder = VideoCodecs.Vp8.CreateEncoder(new VideoEncoderOptions { Width = 64, Height = 48, KeyframeInterval = 15, SceneCutKeyframes = false }, MediaContext.Default);
        List<long> keys = [];
        for (int i = 0; i < clip.Frames.Count; i++)
        {
            using YuvVideoFrame frame = clip.Frame(i);
            encoder.Send(frame);
        }

        encoder.Flush();
        Drain(encoder, keys);
        Assert.Equal([0L, 15, 30], keys);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BitrateLandsWithinFivePercent(bool twoPass)
    {
        using Clip clip = Clip.Moving(160, 96, 120);
        long bitrate = 200_000;
        FirstPassStats? stats = twoPass ? VideoWriter.AnalyzeFirstPass(clip.Frames.Select((p, i) => (VideoFrame)new YuvVideoFrame(Clip.Copy(p), new MediaTime(i, Rate.Reciprocal()))), cancellationToken: TestContext.Current.CancellationToken) : null;
        (_, List<EncodedPacket> packets) = Encode(clip, new VideoEncoderOptions { Width = 160, Height = 96, RateControl = RateControlMode.Bitrate, Bitrate = bitrate, FirstPassStats = stats });
        long bits = packets.Sum(p => p.Data.Length * 8L);
        packets.ForEach(p => p.Dispose());
        double actual = bits / (clip.Frames.Count / 30.0);
        Assert.InRange(actual / bitrate, 0.95, 1.05);
    }

    [Fact]
    public void ConstantBitrateNeverRunsTheBufferDry()
    {
        using Clip clip = Clip.Moving(160, 96, 90, cutAt: 45);
        long bitrate = 150_000, buffer = 150_000;
        (_, List<EncodedPacket> packets) = Encode(clip, new VideoEncoderOptions
        {
            Width = 160,
            Height = 96,
            RateControl = RateControlMode.ConstantBitrate,
            Bitrate = bitrate,
            BufferSize = buffer,
            LatencyMode = LatencyMode.Realtime,
        });

        // The decoder's buffer starts three quarters full and fills by the bitrate each frame time.
        double level = buffer * 0.75, perFrame = bitrate / 30.0;
        foreach (EncodedPacket packet in packets)
        {
            level = Math.Min(buffer, level + perFrame) - (packet.Data.Length * 8);
            Assert.True(level >= 0, $"buffer at {level:F0} bits");
            packet.Dispose();
        }
    }

    [Fact]
    public void FirstPassStatsRoundTrip()
    {
        using Clip clip = Clip.Moving(64, 48, 10);
        FirstPassStats stats = VideoWriter.AnalyzeFirstPass(clip.Frames.Select((p, i) => (VideoFrame)new YuvVideoFrame(Clip.Copy(p), new MediaTime(i, Rate.Reciprocal()))), cancellationToken: TestContext.Current.CancellationToken);
        using MemoryStream stream = new();
        stats.Save(stream);
        stream.Position = 0;
        FirstPassStats loaded = FirstPassStats.Load(stream);
        Assert.Equal(10, loaded.FrameCount);
        Assert.Equal(64, loaded.Width);
        Assert.Equal(stats.Frames.ToArray(), loaded.Frames.ToArray());
        Assert.Throws<InvalidDataException>(() => FirstPassStats.Load(new MemoryStream(new byte[24])));
    }

    [Fact]
    public void AlphaRoundTripsThroughWebM()
    {
        using Clip clip = Clip.Moving(64, 48, 12, alpha: true);
        using MemoryStream file = new();
        using (VideoWriter writer = VideoWriter.Create(file, "webm", new VideoWriterOptions { Codec = VideoCodecs.Vp8, Quality = 90 }, leaveOpen: true))
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
                Assert.True(Psnr(clip.Frames[n].A!, raster.A!) > 35);
                Assert.True(Psnr(clip.Frames[n].Y, raster.Y) > 33);
                n++;
            }
        }

        Assert.Equal(clip.Frames.Count, n);
    }

    [Theory]
    [InlineData("ivf")]
    [InlineData("mkv")]
    public void RgbFramesWriteToIvfAndMatroska(string format)
    {
        using MemoryStream file = new();
        using (VideoWriter writer = VideoWriter.Create(file, format, new VideoWriterOptions { Codec = VideoCodecs.Vp8, Effort = 1 }, leaveOpen: true))
        {
            for (int i = 0; i < 8; i++)
            {
                using Raster<Rgb8> image = Raster.Create(50, 30, new Rgb8((byte)(i * 20), 100, 200));
                writer.WriteFrame(image);
            }
        }

        file.Position = 0;
        using VideoReader reader = VideoReader.Open(file);
        int n = 0;
        foreach (YuvVideoFrame frame in reader.ReadFrames())
        {
            using (frame)
            {
                Assert.Equal(50, frame.Width);
                n++;
            }
        }

        Assert.Equal(8, n);
    }

    [Fact]
    public void BadOptionsAreRefused()
    {
        MediaContext c = MediaContext.Default;
        Assert.Throws<ArgumentException>(() => VideoCodecs.Vp8.CreateEncoder(new VideoEncoderOptions { Width = 16, Height = 16, RateControl = RateControlMode.Bitrate }, c));
        Assert.Throws<ArgumentOutOfRangeException>(() => VideoCodecs.Vp8.CreateEncoder(new VideoEncoderOptions { Width = 16, Height = 16, Quality = 101 }, c));
        Assert.Throws<ArgumentOutOfRangeException>(() => VideoCodecs.Vp8.CreateEncoder(new VideoEncoderOptions { Width = 16, Height = 16, Effort = 11 }, c));
        Assert.Throws<ArgumentOutOfRangeException>(() => VideoCodecs.Vp8.CreateEncoder(new VideoEncoderOptions { Width = 16384, Height = 16 }, c));
        Assert.Throws<ArgumentException>(() => VideoCodecs.Vp8.CreateEncoder(new VideoEncoderOptions { Width = 16, Height = 16, Vp8 = new Vp8EncoderOptions { MinQuantizer = 50, MaxQuantizer = 40 } }, c));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Vp8EncoderOptions { Partitions = 3 });
        using IVideoEncoder encoder = VideoCodecs.Vp8.CreateEncoder(new VideoEncoderOptions { Width = 16, Height = 16 }, c);
        using VideoFrame<Rgb8> frame = new(Raster.Create<Rgb8>(8, 8), MediaTime.Zero);
        Assert.Throws<ArgumentException>(() => encoder.Send(frame));
    }

    [Theory]
    [InlineData(0, 127)]
    [InlineData(60, 33)]
    [InlineData(100, 0)]
    public void QualityMapsAsLossyWebP(int quality, int quantizer) => Assert.Equal(quantizer, Vp8VideoEncoder.QualityToQuantizer(quality));

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
        using IVideoEncoder encoder = VideoCodecs.Vp8.CreateEncoder(options, context ?? MediaContext.Default);
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
        using IVideoDecoder decoder = VideoCodecs.Vp8.CreateDecoder(track, null, MediaContext.Default);
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

    internal static double Psnr(YuvPlane<byte> a, YuvPlane<byte> b)
    {
        double sum = 0;
        for (int y = 0; y < a.Height; y++)
        {
            ReadOnlySpan<byte> ra = a.Row(y), rb = b.Row(y);
            for (int x = 0; x < a.Width; x++)
            {
                double d = ra[x] - rb[x];
                sum += d * d;
            }
        }

        return sum == 0 ? 99 : 10 * Math.Log10(255.0 * 255 * a.Width * a.Height / sum);
    }

    /// <summary>A synthetic clip: a textured picture panning, with a little noise, and optionally a cut to another picture.</summary>
    internal sealed class Clip : IDisposable
    {
        public List<YuvRaster<byte>> Frames { get; } = [];

        public static Clip Moving(int width, int height, int count, int cutAt = -1, bool alpha = false, int speed = 1)
        {
            Clip clip = new();
            uint seed = 12345;
            for (int f = 0; f < count; f++)
            {
                bool second = cutAt >= 0 && f >= cutAt;
                YuvRaster<byte> picture = YuvRaster.Create(width, height, ChromaFormat.Yuv420, YuvColor.Bt601Limited, alpha);
                for (int y = 0; y < height; y++)
                {
                    Span<byte> row = picture.Y.Row(y);
                    for (int x = 0; x < width; x++)
                    {
                        seed = (seed * 1664525) + 1013904223;
                        int sx = x + (f * speed), sy = y + (f * speed / 2);
                        int v = second
                            ? 128 + (int)(60 * Math.Sin(sx * 0.31) * Math.Cos(sy * 0.17))
                            : 40 + ((sx / 8 + sy / 8) % 2 * 120) + ((sx * 3 + sy * 5) % 23);
                        row[x] = (byte)Math.Clamp(v + (int)((seed >> 29) & 3) - 1, 16, 235);
                    }

                    if (alpha)
                    {
                        Span<byte> a = picture.A!.Row(y);
                        for (int x = 0; x < width; x++)
                        {
                            a[x] = (byte)Math.Clamp(((x + f) * 255 / width) % 256, 0, 255);
                        }
                    }
                }

                for (int y = 0; y < picture.U!.Height; y++)
                {
                    Span<byte> u = picture.U.Row(y), v = picture.V!.Row(y);
                    for (int x = 0; x < u.Length; x++)
                    {
                        u[x] = (byte)(second ? 90 : 128 + ((x + (f / 2)) % 16) - 8);
                        v[x] = (byte)(second ? 160 : 128 + ((y + (f / 2)) % 12) - 6);
                    }
                }

                clip.Frames.Add(picture);
            }

            return clip;
        }

        public static YuvRaster<byte> Copy(YuvRaster<byte> source)
        {
            YuvRaster<byte> copy = YuvRaster.Create(source.Width, source.Height, source.ChromaFormat, source.Color, source.HasAlpha);
            Copy(source.Y, copy.Y);
            Copy(source.U!, copy.U!);
            Copy(source.V!, copy.V!);
            if (source.A is { } a)
            {
                Copy(a, copy.A!);
            }

            return copy;
        }

        public YuvVideoFrame Frame(int i) => new(Copy(Frames[i]), new MediaTime(i, Rate.Reciprocal()));

        public void Dispose() => Frames.ForEach(f => f.Dispose());

        private static void Copy(YuvPlane<byte> from, YuvPlane<byte> to)
        {
            for (int y = 0; y < from.Height; y++)
            {
                from.Row(y).CopyTo(to.Row(y));
            }
        }
    }
}
