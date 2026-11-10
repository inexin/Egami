using Egami.Video.Codecs;
using Egami.Video.Codecs.H264;

namespace Egami.Video.H264.Tests;

/// <summary>B pictures: they decode in Egami's decoder to the encoder's reconstruction, out of order, and save bits.</summary>
public sealed class EncoderBTests
{
    public static TheoryData<H264Profile, H264EntropyCoder?, int, int?> Streams => new()
    {
        { H264Profile.High, null, 1, null },
        { H264Profile.High, H264EntropyCoder.Cavlc, 2, 1 },
        { H264Profile.High, null, 4, 2 },
        { H264Profile.High, null, 4, 3 },
        { H264Profile.Main, H264EntropyCoder.Cavlc, 4, 3 },
        { H264Profile.Main, null, 6, 5 },
        { H264Profile.High, H264EntropyCoder.Cavlc, 7, 2 },
        { H264Profile.High, null, 8, null },
        { H264Profile.High, null, 4, 16 },
        { H264Profile.High, null, 10, 3 },
    };

    [Theory]
    [MemberData(nameof(Streams))]
    public void BFramesDecodeToTheReconstruction(H264Profile profile, H264EntropyCoder? entropy, int effort, int? bFrames)
    {
        H264FrameCoder? coder = null;
        EncoderInterTests.Check(Options(profile, entropy, effort, bFrames), 13, EncoderInterTests.Moving, c => coder = c);
        Assert.True(coder!.BPictures > 0, "No B pictures were coded.");
    }

    [Theory]
    [InlineData(H264EntropyCoder.Cabac, 4)]
    [InlineData(H264EntropyCoder.Cavlc, 4)]
    [InlineData(H264EntropyCoder.Cabac, 1)]
    public void TemporalDirectDecodesToTheReconstruction(H264EntropyCoder entropy, int effort)
    {
        H264FrameCoder? coder = null;
        EncoderInterTests.Check(Options(H264Profile.High, entropy, effort, 3), 13, EncoderInterTests.Moving, c =>
        {
            coder = c;
            c.TemporalDirect = true;
        });
        Assert.True(coder!.TemporalPictures > 0, "No B pictures predicted temporally.");
    }

    [Fact]
    public void FadesWithBFramesDecodeToTheReconstruction()
    {
        H264FrameCoder? coder = null;
        EncoderInterTests.Check(Options(H264Profile.Main, null, 4, 3), 10, EncoderInterTests.Fade, c => coder = c);
        Assert.True(coder!.BPictures > 0, "No B pictures were coded.");
    }

    [Fact]
    public void BFramesMakeTheStreamSmaller()
    {
        static long Size(int bFrames) => EncoderInterTests.Check(Options(H264Profile.High, null, 4, bFrames), 13, EncoderInterTests.Moving)
            .Skip(1).Sum(p => (long)p.Data.Length);

        long withB = Size(3), withoutB = Size(0);
        Assert.True(withB < withoutB, $"With B-frames {withB} bytes, without {withoutB}.");
    }

    [Fact]
    public void PacketsCarryDecodeTimestamps()
    {
        List<EncodedPacket> packets = EncoderInterTests.Check(Options(H264Profile.High, null, 4, 3), 9, EncoderInterTests.Moving);
        Assert.Contains(packets, p => p.DecodeTimestamp is { } dts && dts != p.Timestamp);
        for (int i = 1; i < packets.Count; i++)
        {
            Assert.True(packets[i].DecodeTimestamp!.Value > packets[i - 1].DecodeTimestamp!.Value);
        }
    }

    [Fact]
    public void ConstrainedBaselineRejectsBFrames()
    {
        VideoEncoderOptions options = Options(H264Profile.ConstrainedBaseline, null, 4, 2);
        Assert.Throws<ArgumentException>(() => H264Codec.Instance.CreateEncoder(options, MediaContext.Default));
    }

    [Fact]
    public void BFramesOutsideTheRangeThrow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new H264EncoderOptions { BFrames = 17 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new H264EncoderOptions { BFrames = -1 });
    }

    private static VideoEncoderOptions Options(H264Profile profile, H264EntropyCoder? entropy, int effort, int? bFrames) => new()
    {
        Width = 80,
        Height = 64,
        Effort = effort,
        KeyframeInterval = 100,
        H264 = new H264EncoderOptions { Profile = profile, Entropy = entropy, BFrames = bFrames },
    };
}
