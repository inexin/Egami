using Egami.Pixels;
using Egami.Video.Codecs;
using Egami.Video.Containers;
using Egami.Video.Hardware.Encoding;
using Egami.Video.Hardware.Vulkan.Native;

namespace Egami.Video.Hardware.Tests;

/// <summary>
/// The hardware HEVC encoder: what it writes decodes in Egami's software decoder, close to the source, and the hardware
/// decoder decodes it to the same samples. The GPU tests skip without a GPU that encodes HEVC.
/// </summary>
[Trait("Category", "Gpu")]
public sealed class HevcEncoderTests
{
    public static TheoryData<int, int, bool, int?> Streams => new()
    {
        { 320, 180, false, null },
        { 320, 180, true, null },
        { 200, 118, false, null },
        { 320, 180, false, 2 },
        { 320, 180, true, 1 },
    };

    [Theory]
    [MemberData(nameof(Streams))]
    public void StreamsDecodeCloseToTheSource(int width, int height, bool annexB, int? bFrames)
    {
        RequireEncoder();
        VideoEncoderOptions options = new()
        {
            Width = width,
            Height = height,
            FrameRate = new Rational(25, 1),
            KeyframeInterval = 12,
            Hevc = new HevcEncoderOptions { AnnexB = annexB, BFrames = bFrames },
        };
        const int count = 30;
        (List<EncodedPacket> packets, VideoTrackInfo track) = Encode(options, count);
        try
        {
            Assert.Equal(count, packets.Count);
            Assert.Equal(annexB, track.CodecPrivate.IsEmpty);
            Assert.Equal(3, packets.Count(p => p.IsKeyframe));
            Assert.True(packets[0].IsKeyframe);
            for (int i = 0; i < packets.Count; i++)
            {
                MediaTime decodeTime = packets[i].DecodeTimestamp ?? packets[i].Timestamp;
                Assert.True(decodeTime.Ticks <= packets[i].Timestamp.Ticks, $"Packet {i} is decoded after it is shown.");
                Assert.True(i == 0 || decodeTime.Ticks > (packets[i - 1].DecodeTimestamp ?? packets[i - 1].Timestamp).Ticks, $"Packet {i}'s decode time does not rise.");
            }

            if (!annexB)
            {
                // HEVCDecoderConfigurationRecord: version 1, Main profile, three arrays (VPS, SPS, PPS).
                ReadOnlySpan<byte> hvcC = track.CodecPrivate.Span;
                Assert.Equal(1, hvcC[0]);
                Assert.Equal(1, hvcC[1] & 31);
                Assert.Equal(0, hvcC[17] & 7);
                Assert.Equal(0, hvcC[18] & 7);
                Assert.Equal(3, hvcC[21] & 3);
                Assert.Equal(3, hvcC[22]);
                Assert.Equal(32, hvcC[23] & 63);
            }

            List<YuvVideoFrame> decoded = Decode(HevcCodec.Instance, track, packets);
            List<YuvVideoFrame> gpu = Decode(HardwareVideoCodecs.Hevc, track, packets);
            try
            {
                Assert.Equal(count, decoded.Count);
                Assert.Equal(count, gpu.Count);
                for (int i = 0; i < count; i++)
                {
                    Assert.Equal(i, decoded[i].Timestamp.Ticks);
                    YuvRaster<byte> planes = (YuvRaster<byte>)decoded[i].Planes!;
                    Assert.Equal(width, planes.Width);
                    Assert.Equal(height, planes.Height);
                    using YuvRaster<byte> source = H264EncoderTests.Picture(width, height, i);
                    double psnr = H264EncoderTests.Psnr(source.Y, planes.Y);
                    Assert.True(psnr > 34, $"Frame {i}: PSNR-Y {psnr:F2}");
                    YuvRaster<byte> other = (YuvRaster<byte>)gpu[i].Planes!;
                    Assert.Equal(99, H264EncoderTests.Psnr(planes.Y, other.Y));
                    Assert.Equal(99, H264EncoderTests.Psnr(planes.U!, other.U!));
                    Assert.Equal(99, H264EncoderTests.Psnr(planes.V!, other.V!));
                }
            }
            finally
            {
                decoded.ForEach(f => f.Dispose());
                gpu.ForEach(f => f.Dispose());
            }
        }
        finally
        {
            packets.ForEach(p => p.Dispose());
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MainTenKeepsTenBits(bool askForProfile)
    {
        RequireEncoder();
        Assert.SkipUnless(
            HardwareVideoCodecs.GetDevices().Any(d => d.Profiles.Any(p => p.CodecId == CodecIds.Hevc && p.Direction == HardwareDirection.Encode && p.BitDepth == 10)),
            "No GPU encodes HEVC Main 10.");
        VideoEncoderOptions options = new()
        {
            Width = 192,
            Height = 128,
            Hevc = askForProfile ? new HevcEncoderOptions { Profile = HevcProfile.Main10 } : null,
        };
        using IVideoEncoder encoder = HardwareVideoCodecs.Hevc.CreateEncoder(options, MediaContext.Default);
        List<EncodedPacket> packets = [];
        for (int i = 0; i < 8; i++)
        {
            using YuvVideoFrame frame = new(Ramp(i), new MediaTime(i, options.FrameRate.Reciprocal()));
            encoder.Send(frame);
            Drain(encoder, packets);
        }

        encoder.Flush();
        Drain(encoder, packets);
        Assert.Equal(10, ((HevcHardwareEncoder)encoder).BitDepth);
        Assert.Equal(10, encoder.Track.BitDepth);
        List<YuvVideoFrame> decoded = Decode(HevcCodec.Instance, encoder.Track, packets);
        List<YuvVideoFrame> gpu = Decode(HardwareVideoCodecs.Hevc, encoder.Track, packets);
        try
        {
            Assert.Equal(8, decoded.Count);
            for (int i = 0; i < 8; i++)
            {
                YuvRaster<ushort> planes = Assert.IsType<YuvRaster<ushort>>(decoded[i].Planes);
                Assert.Equal(10, planes.BitDepth);
                double sum = 0;
                using YuvRaster<ushort> source = Ramp(i);
                for (int y = 0; y < 128; y++)
                {
                    ReadOnlySpan<ushort> a = source.Y.Row(y), b = planes.Y.Row(y);
                    for (int x = 0; x < 192; x++)
                    {
                        sum += Math.Abs(a[x] - b[x]);
                    }
                }

                Assert.True(sum / (192 * 128) < 12, $"Frame {i}: mean error {sum / (192 * 128):F2} of 1023");
                YuvRaster<ushort> other = Assert.IsType<YuvRaster<ushort>>(gpu[i].Planes);
                for (int y = 0; y < 128; y++)
                {
                    Assert.True(planes.Y.Row(y).SequenceEqual(other.Y.Row(y)), $"Frame {i} row {y} differs between the decoders.");
                }
            }
        }
        finally
        {
            decoded.ForEach(f => f.Dispose());
            gpu.ForEach(f => f.Dispose());
            packets.ForEach(p => p.Dispose());
        }

        // A 10-bit ramp: the two low bits carry detail an 8-bit stream would lose.
        static YuvRaster<ushort> Ramp(int index)
        {
            YuvRaster<ushort> picture = YuvRaster.Create(192, 128, ChromaFormat.Yuv420, 10, YuvColor.Bt601Limited);
            for (int y = 0; y < 128; y++)
            {
                Span<ushort> row = picture.Y.Row(y);
                for (int x = 0; x < 192; x++)
                {
                    row[x] = (ushort)(64 + ((x + y + (3 * index)) * 3 % 876));
                }
            }

            for (int y = 0; y < 64; y++)
            {
                picture.U!.Row(y).Fill(512);
                picture.V!.Row(y).Fill(512);
            }

            return picture;
        }
    }

    [Fact]
    public void MainTenMp4SaysTenBitsInItsConfiguration()
    {
        RequireEncoder();
        Assert.SkipUnless(
            HardwareVideoCodecs.GetDevices().Any(d => d.Profiles.Any(p => p.CodecId == CodecIds.Hevc && p.Direction == HardwareDirection.Encode && p.BitDepth == 10)),
            "No GPU encodes HEVC Main 10.");
        string path = Path.Combine(Path.GetTempPath(), $"egami-hw-main10-{Guid.NewGuid():N}.mp4");
        try
        {
            VideoWriterOptions options = new() { Codec = HardwareVideoCodecs.Hevc, FrameRate = new Rational(25, 1), Hevc = new HevcEncoderOptions { Profile = HevcProfile.Main10 } };
            using (VideoWriter writer = VideoWriter.Create(path, options))
            {
                for (int i = 0; i < 3; i++)
                {
                    using YuvRaster<byte> picture = H264EncoderTests.Picture(192, 128, i);
                    using YuvVideoFrame frame = new(picture, new MediaTime(i, new Rational(1, 25)));
                    writer.WriteFrame(frame);
                }
            }

            // HEVCDecoderConfigurationRecord: general_profile_idc 2 (Main 10), bitDepthLumaMinus8 and bitDepthChromaMinus8 2.
            using IDemuxer demuxer = MediaFile.OpenDemuxer(path, MediaContext.Default.WithHevc());
            ReadOnlySpan<byte> hvcC = demuxer.Info.VideoTrack!.CodecPrivate.Span;
            Assert.Equal(2, hvcC[1] & 31);
            Assert.Equal(2, hvcC[17] & 7);
            Assert.Equal(2, hvcC[18] & 7);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void KeyframesComeWhereAsked()
    {
        RequireEncoder();
        VideoEncoderOptions options = new() { Width = 128, Height = 96, KeyframeInterval = 100 };
        using IVideoEncoder encoder = HardwareVideoCodecs.Hevc.CreateEncoder(options, MediaContext.Default);
        List<EncodedPacket> packets = [];
        for (int i = 0; i < 12; i++)
        {
            using YuvRaster<byte> picture = H264EncoderTests.Picture(128, 96, i);
            using YuvVideoFrame frame = new(picture, new MediaTime(i, options.FrameRate.Reciprocal()));
            encoder.Send(frame, new EncodeFrameOptions { ForceKeyframe = i == 7 });
            Drain(encoder, packets);
        }

        encoder.Flush();
        Drain(encoder, packets);
        int[] keys = [.. packets.Where(p => p.IsKeyframe).Select(p => (int)p.Timestamp.Ticks)];
        Assert.Equal([0, 7], keys);
        List<YuvVideoFrame> decoded = Decode(HevcCodec.Instance, encoder.Track, packets);
        Assert.Equal(12, decoded.Count);
        decoded.ForEach(f => f.Dispose());
        packets.ForEach(p => p.Dispose());
    }

    [Fact]
    public void QualityOrdersSize()
    {
        RequireEncoder();
        static long Size(int quality)
        {
            (List<EncodedPacket> packets, _) = Encode(new VideoEncoderOptions { Width = 192, Height = 128, Quality = quality }, 6);
            long size = packets.Sum(p => (long)p.Data.Length);
            packets.ForEach(p => p.Dispose());
            return size;
        }

        Assert.True(Size(20) < Size(50));
        Assert.True(Size(50) < Size(80));
    }

    [Theory]
    [InlineData(RateControlMode.Bitrate)]
    [InlineData(RateControlMode.ConstantBitrate)]
    public void BitrateModesLandNearTheTarget(RateControlMode mode)
    {
        RequireEncoder();
        const long target = 1_000_000;
        VideoEncoderOptions options = new() { Width = 320, Height = 240, FrameRate = new Rational(25, 1), RateControl = mode, Bitrate = target };
        (List<EncodedPacket> packets, VideoTrackInfo track) = Encode(options, 100, noise: 24);
        long bits = packets.Sum(p => (long)p.Data.Length) * 8;
        double rate = bits / (100 / 25.0);
        Assert.InRange(rate, target * 0.6, target * 1.4);
        List<YuvVideoFrame> decoded = Decode(HevcCodec.Instance, track, packets);
        Assert.Equal(100, decoded.Count);
        decoded.ForEach(f => f.Dispose());
        packets.ForEach(p => p.Dispose());
    }

    [Fact]
    public void RgbFramesAreConverted()
    {
        RequireEncoder();
        VideoEncoderOptions options = new() { Width = 64, Height = 48 };
        using IVideoEncoder encoder = HardwareVideoCodecs.Hevc.CreateEncoder(options, MediaContext.Default);
        using Raster<Rgb8> raster = Raster.Create<Rgb8>(64, 48);
        for (int y = 0; y < 48; y++)
        {
            raster.Row(y).Fill(new Rgb8(200, 40, 20));
        }

        using VideoFrame<Rgb8> frame = new(raster, new MediaTime(0, options.FrameRate.Reciprocal()));
        encoder.Send(frame);
        encoder.Flush();
        Assert.True(encoder.TryReceive(out EncodedPacket? packet));
        using (packet)
        {
            List<YuvVideoFrame> decoded = Decode(HevcCodec.Instance, encoder.Track, [packet]);
            YuvRaster<byte> planes = (YuvRaster<byte>)decoded[0].Planes!;

            // BT.601 limited range: Y 89.5, Cr 199.7.
            Assert.InRange(planes.Y.Row(20)[30], 84, 95);
            Assert.InRange(planes.V!.Row(10)[15], 192, 207);
            decoded.ForEach(f => f.Dispose());
        }
    }

    [Fact]
    public void BadOptionsAreRefusedBeforeTheGpuIsTouched()
    {
        VideoCodec codec = HardwareVideoCodecs.Hevc;
        Assert.Throws<ArgumentException>(() => codec.CreateEncoder(new VideoEncoderOptions { Width = 33, Height = 32 }, MediaContext.Default));
        Assert.Throws<ArgumentOutOfRangeException>(() => codec.CreateEncoder(new VideoEncoderOptions { Width = 32, Height = 32, Quality = 101 }, MediaContext.Default));
        Assert.Throws<ArgumentException>(() => codec.CreateEncoder(new VideoEncoderOptions { Width = 32, Height = 32, RateControl = RateControlMode.Bitrate }, MediaContext.Default));
        Assert.Equal(CodecIds.Hevc, codec.CodecId);
        Assert.Equal(HardwareVideoCodecs.IsAvailable(CodecIds.Hevc, HardwareDirection.Encode), codec.CanEncode);
    }

    [Fact]
    public void BFramesAreOnlyWhereTheGpuCodesThem()
    {
        RequireEncoder();
        VideoEncoderOptions options = new() { Width = 64, Height = 64, FrameRate = new Rational(25, 1) };
        using (IVideoEncoder encoder = HardwareVideoCodecs.Hevc.CreateEncoder(options, MediaContext.Default))
        {
            Assert.Equal(0, ((HevcHardwareEncoder)encoder).BFrames);
        }

        options = options with { Hevc = new HevcEncoderOptions { BFrames = 2 } };
        using (IVideoEncoder encoder = HardwareVideoCodecs.Hevc.CreateEncoder(options, MediaContext.Default))
        {
            Assert.InRange(((HevcHardwareEncoder)encoder).BFrames, 0, 2);
        }
    }

    [Theory]
    [InlineData(0, 51)]
    [InlineData(60, 17)]
    [InlineData(100, 0)]
    [InlineData(50, 23)]
    public void QualityFollowsTheSoftwareScale(int quality, int qp) => Assert.Equal(qp, HevcHardwareEncoder.QualityToQp(quality));

    [Theory]
    [InlineData(10, 0)]
    [InlineData(31, 4)]
    [InlineData(41, 6)]
    [InlineData(51, 8)]
    [InlineData(62, 12)]
    public void LevelsMapToTheStdEnumerators(int level, int index) => Assert.Equal(index, HevcHardwareEncoder.LevelIndex(level));

    [Theory]
    [InlineData(25, 28, 31, -3, 3)]
    [InlineData(0, 0, 0, -2, 2)]
    [InlineData(28, 26, 31, -2, 2)]
    public void QpOffsetsFollowTheDriver(int i, int p, int b, int expectedI, int expectedB) =>
        Assert.Equal((expectedI, expectedB), HevcHardwareEncoder.QpOffsets(new VkVideoEncodeH265QpKHR { qpI = i, qpP = p, qpB = b }));

    internal static void RequireEncoder() =>
        Assert.SkipUnless(HardwareVideoCodecs.IsAvailable(CodecIds.Hevc, HardwareDirection.Encode), "No GPU encodes HEVC: " + HardwareVideoCodecs.UnavailableReason);

    private static (List<EncodedPacket> Packets, VideoTrackInfo Track) Encode(VideoEncoderOptions options, int frames, int noise = 0)
    {
        using IVideoEncoder encoder = HardwareVideoCodecs.Hevc.CreateEncoder(options, MediaContext.Default);
        List<EncodedPacket> packets = [];
        for (int i = 0; i < frames; i++)
        {
            using YuvRaster<byte> picture = H264EncoderTests.Picture(options.Width, options.Height, i, noise);
            using YuvVideoFrame frame = new(picture, new MediaTime(i, options.FrameRate.Reciprocal()));
            encoder.Send(frame);
            Drain(encoder, packets);
        }

        encoder.Flush();
        Drain(encoder, packets);
        return (packets, encoder.Track);
    }

    private static List<YuvVideoFrame> Decode(VideoCodec codec, VideoTrackInfo track, List<EncodedPacket> packets)
    {
        MediaContext context = MediaContext.Default.WithHevc();
        using IVideoDecoder decoder = codec.CreateDecoder(track, null, context);
        List<YuvVideoFrame> frames = [];
        foreach (EncodedPacket packet in packets)
        {
            decoder.Send(packet);
            while (decoder.TryReceive(out YuvVideoFrame? frame))
            {
                frames.Add(frame);
            }
        }

        decoder.Flush();
        while (decoder.TryReceive(out YuvVideoFrame? frame))
        {
            frames.Add(frame);
        }

        return frames;
    }

    private static void Drain(IVideoEncoder encoder, List<EncodedPacket> packets)
    {
        while (encoder.TryReceive(out EncodedPacket? packet))
        {
            packets.Add(packet);
        }
    }
}
