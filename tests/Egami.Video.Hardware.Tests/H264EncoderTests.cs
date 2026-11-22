using Egami.Colors;
using Egami.Pixels;
using Egami.Video.Codecs;
using Egami.Video.Hardware.Encoding;
using Egami.Video.Hardware.Vulkan.Native;

namespace Egami.Video.Hardware.Tests;

/// <summary>
/// The hardware H.264 encoder: what it writes decodes in Egami's software decoder, close to the source. The GPU tests skip
/// without a GPU that encodes H.264.
/// </summary>
[Trait("Category", "Gpu")]
public sealed class H264EncoderTests
{
    public static TheoryData<H264Profile, H264EntropyCoder?, bool, int?> Streams => new()
    {
        { H264Profile.High, null, false, null },
        { H264Profile.High, null, true, null },
        { H264Profile.High, null, false, 0 },
        { H264Profile.High, H264EntropyCoder.Cavlc, false, 1 },
        { H264Profile.Main, null, false, 3 },
        { H264Profile.High, null, true, 2 },
        { H264Profile.ConstrainedBaseline, null, false, null },
        { H264Profile.ConstrainedBaseline, null, true, null },
    };

    [Theory]
    [MemberData(nameof(Streams))]
    public void StreamsDecodeCloseToTheSource(H264Profile profile, H264EntropyCoder? entropy, bool annexB, int? bFrames)
    {
        RequireEncoder();
        VideoEncoderOptions options = new()
        {
            Width = 320,
            Height = 180,
            FrameRate = new Rational(25, 1),
            KeyframeInterval = 12,
            H264 = new H264EncoderOptions { Profile = profile, Entropy = entropy, AnnexB = annexB, BFrames = bFrames },
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
                ReadOnlySpan<byte> avcC = track.CodecPrivate.Span;
                Assert.Equal(1, avcC[0]);
                Assert.Equal(profile switch { H264Profile.ConstrainedBaseline => 66, H264Profile.Main => 77, _ => 100 }, avcC[1]);
            }

            List<YuvVideoFrame> decoded = Decode(track, packets);
            try
            {
                Assert.Equal(count, decoded.Count);
                for (int i = 0; i < count; i++)
                {
                    Assert.Equal(i, decoded[i].Timestamp.Ticks);
                    using YuvRaster<byte> source = Picture(options.Width, options.Height, i);
                    double psnr = Psnr(source.Y, ((YuvRaster<byte>)decoded[i].Planes!).Y);
                    Assert.True(psnr > 34, $"Frame {i}: PSNR-Y {psnr:F2}");
                }
            }
            finally
            {
                decoded.ForEach(f => f.Dispose());
            }
        }
        finally
        {
            packets.ForEach(p => p.Dispose());
        }
    }

    [Fact]
    public void KeyframesComeWhereAsked()
    {
        RequireEncoder();
        VideoEncoderOptions options = new() { Width = 128, Height = 96, KeyframeInterval = 100 };
        using IVideoEncoder encoder = HardwareVideoCodecs.H264.CreateEncoder(options, MediaContext.Default);
        List<EncodedPacket> packets = [];
        for (int i = 0; i < 12; i++)
        {
            using YuvRaster<byte> picture = Picture(128, 96, i);
            using YuvVideoFrame frame = new(picture, new MediaTime(i, options.FrameRate.Reciprocal()));
            encoder.Send(frame, new EncodeFrameOptions { ForceKeyframe = i == 7 });
            Drain(encoder, packets);
        }

        encoder.Flush();
        Drain(encoder, packets);
        int[] keys = [.. packets.Where(p => p.IsKeyframe).Select(p => (int)p.Timestamp.Ticks)];
        Assert.Equal([0, 7], keys);
        Assert.Equal(12, Decode(encoder.Track, packets).Select(f =>
        {
            f.Dispose();
            return 1;
        }).Sum());
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
        Assert.Equal(100, Decode(track, packets).Select(f =>
        {
            f.Dispose();
            return 1;
        }).Sum());
        packets.ForEach(p => p.Dispose());
    }

    [Fact]
    public void RgbFramesAreConverted()
    {
        RequireEncoder();
        VideoEncoderOptions options = new() { Width = 64, Height = 48 };
        using IVideoEncoder encoder = HardwareVideoCodecs.H264.CreateEncoder(options, MediaContext.Default);
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
            List<YuvVideoFrame> decoded = Decode(encoder.Track, [packet]);
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
        VideoCodec codec = HardwareVideoCodecs.H264;
        Assert.Throws<ArgumentException>(() => codec.CreateEncoder(new VideoEncoderOptions { Width = 33, Height = 32 }, MediaContext.Default));
        Assert.Throws<ArgumentOutOfRangeException>(() => codec.CreateEncoder(new VideoEncoderOptions { Width = 32, Height = 32, Quality = 101 }, MediaContext.Default));
        Assert.Throws<ArgumentException>(() => codec.CreateEncoder(new VideoEncoderOptions { Width = 32, Height = 32, RateControl = RateControlMode.Bitrate }, MediaContext.Default));
        Assert.Throws<ArgumentException>(() => codec.CreateEncoder(
            new VideoEncoderOptions { Width = 32, Height = 32, H264 = new H264EncoderOptions { Profile = H264Profile.ConstrainedBaseline, Entropy = H264EntropyCoder.Cabac } },
            MediaContext.Default));
        Assert.Equal(CodecIds.Avc, codec.CodecId);
        Assert.Equal(HardwareVideoCodecs.IsAvailable(CodecIds.Avc, HardwareDirection.Encode), codec.CanEncode);
    }

    [Theory]
    [InlineData(0, 5, 0u)]
    [InlineData(1, 5, 1u)]
    [InlineData(2, 5, 2u)]
    [InlineData(4, 5, 4u)]
    [InlineData(10, 5, 4u)]
    [InlineData(4, 1, 0u)]
    [InlineData(4, 3, 2u)]
    [InlineData(2, 3, 1u)]
    public void EffortPicksTheQualityLevel(int effort, uint levels, uint expected) =>
        Assert.Equal(expected, H264HardwareEncoder.QualityLevel(effort, levels));

    [Fact]
    public void BFramesAreOffUnlessAsked()
    {
        RequireEncoder();
        VideoEncoderOptions options = new() { Width = 64, Height = 64, FrameRate = new Rational(25, 1), Effort = 4 };
        using (IVideoEncoder encoder = HardwareVideoCodecs.H264.CreateEncoder(options, MediaContext.Default))
        {
            Assert.Equal(0, ((H264HardwareEncoder)encoder).BFrames);
        }

        options = options with { H264 = new H264EncoderOptions { BFrames = 2 } };
        using (IVideoEncoder encoder = HardwareVideoCodecs.H264.CreateEncoder(options, MediaContext.Default))
        {
            Assert.InRange(((H264HardwareEncoder)encoder).BFrames, 0, 2);
        }
    }

    [Theory]
    [InlineData(25, 28, 31, -3, 3)]
    [InlineData(26, 26, 26, 0, 0)]
    [InlineData(0, 0, 0, -2, 2)]
    [InlineData(10, 30, 31, -2, 2)]
    [InlineData(28, 26, 31, -2, 2)]
    public void QpOffsetsFollowTheDriver(int i, int p, int b, int expectedI, int expectedB) =>
        Assert.Equal((expectedI, expectedB), H264HardwareEncoder.QpOffsets(new VkVideoEncodeH264QpKHR { qpI = i, qpP = p, qpB = b }));

    [Fact]
    public void LevelsFitTheStream()
    {
        Assert.Equal(31, H264Levels.Choose(80, 45, 30, 3, 0, high: true).Idc);
        Assert.Equal(40, H264Levels.Choose(120, 68, 30, 3, 0, high: true).Idc);
        Assert.Equal(42, H264Levels.Choose(120, 68, 60, 3, 0, high: true).Idc);
        Assert.Equal(51, H264Levels.Choose(240, 135, 30, 3, 0, high: true).Idc);
        Assert.NotNull(H264Levels.Misfit(H264Levels.All[H264Levels.IndexOf(30)], 120, 68, 30, 1, 0, high: true));
    }

    internal static void RequireEncoder() =>
        Assert.SkipUnless(HardwareVideoCodecs.IsAvailable(CodecIds.Avc, HardwareDirection.Encode), "No GPU encodes H.264: " + HardwareVideoCodecs.UnavailableReason);

    internal static (List<EncodedPacket> Packets, VideoTrackInfo Track) Encode(VideoEncoderOptions options, int frames, int noise = 0)
    {
        using IVideoEncoder encoder = HardwareVideoCodecs.H264.CreateEncoder(options, MediaContext.Default);
        List<EncodedPacket> packets = [];
        for (int i = 0; i < frames; i++)
        {
            using YuvRaster<byte> picture = Picture(options.Width, options.Height, i, noise);
            using YuvVideoFrame frame = new(picture, new MediaTime(i, options.FrameRate.Reciprocal()));
            encoder.Send(frame);
            Drain(encoder, packets);
        }

        encoder.Flush();
        Drain(encoder, packets);
        return (packets, encoder.Track);
    }

    internal static List<YuvVideoFrame> Decode(VideoTrackInfo track, List<EncodedPacket> packets)
    {
        using IVideoDecoder decoder = H264Codec.Instance.CreateDecoder(track, null, MediaContext.Default);
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

    /// <summary>
    /// A textured picture that moves a little each frame, so P and B pictures have motion to find, with luma noise of up to
    /// <paramref name="noise"/> levels that changes every frame to make it costly to code.
    /// </summary>
    internal static YuvRaster<byte> Picture(int width, int height, int index, int noise = 0)
    {
        Random random = new(index);
        YuvRaster<byte> picture = YuvRaster.Create(width, height, ChromaFormat.Yuv420, YuvColor.Bt601Limited);
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = picture.Y.Row(y);
            for (int x = 0; x < width; x++)
            {
                int sx = x + (2 * index), sy = y + index;
                int v = 128 + (int)(60 * Math.Sin(sx * 0.11) * Math.Cos(sy * 0.07)) + ((((sx / 16) + (sy / 16)) & 1) * 30);
                row[x] = (byte)Math.Clamp(v + (noise > 0 ? random.Next(-noise, noise + 1) : 0), 16, 235);
            }
        }

        for (int y = 0; y < (height + 1) / 2; y++)
        {
            Span<byte> u = picture.U!.Row(y), v = picture.V!.Row(y);
            for (int x = 0; x < (width + 1) / 2; x++)
            {
                u[x] = (byte)(128 + (int)(30 * Math.Sin((x + index) * 0.2)));
                v[x] = (byte)(128 + (int)(30 * Math.Cos((y + index) * 0.15)));
            }
        }

        return picture;
    }

    internal static double Psnr(YuvPlane<byte> a, YuvPlane<byte> b)
    {
        double sum = 0;
        for (int y = 0; y < a.Height; y++)
        {
            ReadOnlySpan<byte> ra = a.Row(y)[..a.Width], rb = b.Row(y)[..a.Width];
            for (int x = 0; x < ra.Length; x++)
            {
                int d = ra[x] - rb[x];
                sum += d * d;
            }
        }

        double mse = sum / (a.Width * a.Height);
        return mse == 0 ? 99 : 10 * Math.Log10(255 * 255 / mse);
    }

    private static void Drain(IVideoEncoder encoder, List<EncodedPacket> packets)
    {
        while (encoder.TryReceive(out EncodedPacket? packet))
        {
            packets.Add(packet);
        }
    }
}
