using Egami.Video.Codecs;
using Egami.Video.Codecs.H264;

namespace Egami.Video.H264.Tests;

/// <summary>Slices: pictures cut into bands of macroblock rows decode to the reconstruction, the same for any number of threads.</summary>
public sealed class EncoderSliceTests
{
    [Theory]
    [InlineData(2, null, 4, null)]
    [InlineData(3, H264EntropyCoder.Cavlc, 4, null)]
    [InlineData(6, null, 2, 0)]
    [InlineData(4, H264EntropyCoder.Cavlc, 7, 2)]
    [InlineData(3, null, 8, 3)]
    public void SlicesDecodeToTheReconstruction(int slices, H264EntropyCoder? entropy, int effort, int? bFrames)
    {
        List<EncodedPacket> packets = EncoderInterTests.Check(Options(slices, entropy, effort, bFrames), 9, EncoderInterTests.Moving);
        Assert.Equal(slices, Nals(packets[0]).Count(t => t is 1 or 5));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(H264EntropyCoder.Cavlc)]
    public void ThreadsDoNotChangeTheStream(H264EntropyCoder? entropy)
    {
        VideoEncoderOptions options = Options(4, entropy, 4, null);
        byte[] one = Encode(options, 1), four = Encode(options, 4);
        Assert.Equal(one, four);
    }

    [Theory]
    [InlineData(640, 360, 1)]
    [InlineData(1280, 720, 2)]
    [InlineData(1920, 1080, 4)]
    [InlineData(3840, 2160, 8)]
    public void SlicesFollowTheFrameSize(int width, int height, int slices) =>
        Assert.Equal(slices, H264VideoEncoder.AutoSlices((width + 15) >> 4, (height + 15) >> 4));

    [Fact]
    public void SlicesOutsideTheRangeThrow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new H264EncoderOptions { Slices = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new H264EncoderOptions { Slices = 33 });
    }

    [Fact]
    public void MoreSlicesThanRowsAreRows()
    {
        // 80x32 has two macroblock rows.
        VideoEncoderOptions options = Options(8, null, 2, null) with { Height = 32 };
        List<EncodedPacket> packets = EncoderInterTests.Check(options, 3, EncoderInterTests.Moving);
        Assert.Equal(2, Nals(packets[0]).Count(t => t is 1 or 5));
    }

    private static VideoEncoderOptions Options(int slices, H264EntropyCoder? entropy, int effort, int? bFrames) => new()
    {
        Width = 160,
        Height = 96,
        Effort = effort,
        KeyframeInterval = 100,
        H264 = new H264EncoderOptions { Slices = slices, Entropy = entropy, BFrames = bFrames },
    };

    private static byte[] Encode(VideoEncoderOptions options, int threads)
    {
        MediaContext context = new() { Egami = EgamiContext.Default with { MaxParallelism = threads } };
        using IVideoEncoder encoder = H264Codec.Instance.CreateEncoder(options, context);
        using MemoryStream output = new();
        Rational timeBase = options.FrameRate.Reciprocal();
        for (int i = 0; i < 6; i++)
        {
            using YuvRaster<byte> picture = EncoderInterTests.Moving(options.Width, options.Height, i);
            encoder.Send(new YuvVideoFrame(picture, new MediaTime(i, timeBase)));
            while (encoder.TryReceive(out EncodedPacket? packet))
            {
                output.Write(packet.Data.Span);
            }
        }

        encoder.Flush();
        while (encoder.TryReceive(out EncodedPacket? packet))
        {
            output.Write(packet.Data.Span);
        }

        return output.ToArray();
    }

    /// <summary>The NAL unit types of a packet of 4-byte length-prefixed NAL units.</summary>
    private static List<int> Nals(EncodedPacket packet)
    {
        List<int> types = [];
        ReadOnlySpan<byte> data = packet.Data.Span;
        for (int at = 0; at + 4 < data.Length;)
        {
            int length = (data[at] << 24) | (data[at + 1] << 16) | (data[at + 2] << 8) | data[at + 3];
            types.Add(data[at + 4] & 31);
            at += 4 + length;
        }

        return types;
    }
}
