using Egami.Video.Codecs;
using Egami.Video.Codecs.H264;

namespace Egami.Video.H264.Tests;

/// <summary>Rate control: the quality scale, the bitrate modes, and the levels they choose.</summary>
public sealed class EncoderRateTests
{
    private static readonly Rational Rate = new(30, 1);

    [Theory]
    [InlineData(0, 51)]
    [InlineData(50, 23)]
    [InlineData(75, 9)]
    [InlineData(100, 0)]
    public void QualityMapsToTheQuantizer(int quality, int qp) => Assert.Equal(qp, H264VideoEncoder.QualityToQp(quality));

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, 0)]
    public void BitrateLandsNearTheTarget(bool twoPass, int? bFrames)
    {
        long bitrate = 250_000;
        FirstPassStats? stats = twoPass ? VideoWriter.AnalyzeFirstPass(Frames(120), cancellationToken: TestContext.Current.CancellationToken) : null;
        List<EncodedPacket> packets = Encode(120, Options(RateControlMode.Bitrate, bitrate, bFrames) with { FirstPassStats = stats });
        double actual = packets.Sum(p => p.Data.Length * 8L) / (120 / 30.0);
        packets.ForEach(p => p.Dispose());
        Assert.InRange(actual / bitrate, twoPass ? 0.95 : 0.9, twoPass ? 1.05 : 1.1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AStillSceneLandsNearTheTarget(bool twoPass)
    {
        // One second of a still 60 fps scene: the key frame's overspend must be paid back within the second, not over two (with
        // two, one and two passes ended 23 % over).
        Rational rate = new(60, 1);
        long bitrate = 200_000;
        List<VideoFrame> frames = [.. Enumerable.Range(0, 60).Select(i => (VideoFrame)new YuvVideoFrame(Still(i), new MediaTime(i, rate.Reciprocal())))];
        FirstPassStats? stats = twoPass ? VideoWriter.AnalyzeFirstPass(frames, cancellationToken: TestContext.Current.CancellationToken) : null;
        using IVideoEncoder encoder = H264Codec.Instance.CreateEncoder(Options(RateControlMode.Bitrate, bitrate, null) with { Width = 320, Height = 192, FrameRate = rate, FirstPassStats = stats }, MediaContext.Default);
        long bits = 0;
        foreach (VideoFrame frame in frames)
        {
            encoder.Send(frame);
            while (encoder.TryReceive(out EncodedPacket? packet))
            {
                bits += packet.Data.Length * 8L;
                packet.Dispose();
            }
        }

        encoder.Flush();
        while (encoder.TryReceive(out EncodedPacket? packet))
        {
            bits += packet.Data.Length * 8L;
            packet.Dispose();
        }

        frames.ForEach(f => f.Dispose());
        Assert.InRange(bits / (double)bitrate, 0.9, 1.1);
    }

    [Theory]
    [InlineData(LatencyMode.Realtime)]
    [InlineData(LatencyMode.Quality)]
    public void ConstantBitrateNeverRunsTheBufferDry(LatencyMode latency)
    {
        long bitrate = 200_000, buffer = 200_000;
        List<EncodedPacket> packets = Encode(90, Options(RateControlMode.ConstantBitrate, bitrate, null) with { BufferSize = buffer, LatencyMode = latency });

        // Packets come in decode order, each taken from the buffer one frame interval after the one before.
        double level = buffer * 0.75, perFrame = bitrate / 30.0;
        foreach (EncodedPacket packet in packets)
        {
            level = Math.Min(buffer, level + perFrame) - (packet.Data.Length * 8);
            Assert.True(level >= 0, $"buffer at {level:F0} bits");
            packet.Dispose();
        }
    }

    [Theory]
    [InlineData(20_000)]
    [InlineData(40_000)]
    [InlineData(80_000)]
    public void StarvedConstantBitrateHoldsTheBufferAndTheRate(long bitrate)
    {
        // Real time at rates that need coarse steps: the first inter frame must not be planned from an unlearned model, and the
        // stream must not drift far below the target either.
        const int Frames = 90;
        List<EncodedPacket> packets = Encode(Frames, Options(RateControlMode.ConstantBitrate, bitrate, null) with { LatencyMode = LatencyMode.Realtime });
        double level = bitrate * 0.75, perFrame = bitrate / 30.0, bits = 0;
        foreach (EncodedPacket packet in packets)
        {
            level = Math.Min(bitrate, level + perFrame) - (packet.Data.Length * 8);
            bits += packet.Data.Length * 8;
            Assert.True(level >= 0, $"buffer at {level:F0} bits");
            packet.Dispose();
        }

        Assert.InRange(bits / (Frames / 30.0) / bitrate, 0.95, 1.05);
    }

    [Theory]
    [InlineData(RateControlMode.Quality, null, 13)]
    [InlineData(RateControlMode.ConstantBitrate, 1_500_000L, 20)]
    [InlineData(RateControlMode.ConstantBitrate, 3_000_000L, 21)]
    [InlineData(RateControlMode.Bitrate, 3_000_000L, 30)]
    public void LevelsFollowTheBitrate(RateControlMode mode, long? bitrate, int level)
    {
        // 352x288 at 30 frames a second fits level 1.3 by size and rate; the bitrate (High allows 1.25 times MaxBR) and an
        // average bitrate's peak of twice the target can lift it.
        VideoEncoderOptions options = new() { Width = 352, Height = 288, RateControl = mode, Bitrate = bitrate, H264 = new H264EncoderOptions { BFrames = 0 } };
        using IVideoEncoder encoder = H264Codec.Instance.CreateEncoder(options, MediaContext.Default);
        Assert.Equal(level, ((H264VideoEncoder)encoder).Config.LevelIdc);
    }

    [Fact]
    public void ALevelTooLowForTheBitrateThrows()
    {
        VideoEncoderOptions options = new()
        {
            Width = 352,
            Height = 288,
            FrameRate = new Rational(15, 1),
            RateControl = RateControlMode.ConstantBitrate,
            Bitrate = 1_000_000,
            H264 = new H264EncoderOptions { Level = 12 },
        };
        ArgumentException e = Assert.Throws<ArgumentException>(() => H264Codec.Instance.CreateEncoder(options, MediaContext.Default));
        Assert.Contains("bits a second", e.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => H264Codec.Instance.CreateEncoder(options with { Bitrate = 400_000, BufferSize = 2_000_000 }, MediaContext.Default));
    }

    private static VideoEncoderOptions Options(RateControlMode mode, long bitrate, int? bFrames) => new()
    {
        Width = 160,
        Height = 96,
        RateControl = mode,
        Bitrate = bitrate,
        Effort = 2,
        H264 = new H264EncoderOptions { BFrames = bFrames },
    };

    /// <summary>A still scene of 320x192 with a camera's noise, new in every frame.</summary>
    private static YuvRaster<byte> Still(int frame)
    {
        YuvRaster<byte> picture = EncoderInterTests.Moving(320, 192, 0);
        Random random = new(frame);
        for (int y = 0; y < 192; y++)
        {
            Span<byte> row = picture.Y.Row(y);
            for (int x = 0; x < 320; x++)
            {
                row[x] = (byte)Math.Clamp(row[x] + random.Next(-2, 3), 0, 255);
            }
        }

        return picture;
    }

    /// <summary>The moving texture, cut to a different picture at frame 60.</summary>
    private static YuvRaster<byte> Picture(int width, int height, int frame) =>
        frame < 60 ? EncoderInterTests.Moving(width, height, frame) : EncoderInterTests.Moving(width, height, 500 - frame);

    private static IEnumerable<VideoFrame> Frames(int count)
    {
        for (int i = 0; i < count; i++)
        {
            yield return new YuvVideoFrame(Picture(160, 96, i), new MediaTime(i, Rate.Reciprocal()));
        }
    }

    private static List<EncodedPacket> Encode(int frames, VideoEncoderOptions options)
    {
        using IVideoEncoder encoder = H264Codec.Instance.CreateEncoder(options, MediaContext.Default);
        List<EncodedPacket> packets = [];
        for (int i = 0; i < frames; i++)
        {
            using YuvRaster<byte> picture = Picture(options.Width, options.Height, i);
            encoder.Send(new YuvVideoFrame(picture, new MediaTime(i, Rate.Reciprocal())));
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

        return packets;
    }
}
