using Egami.Colors;
using Egami.Pixels;
using Egami.Video.Codecs;
using Egami.Video.Codecs.H264;

namespace Egami.Video.H264.Tests;

/// <summary>The H.264 encoder through <see cref="H264Codec.CreateEncoder"/>: every frame decodes in Egami's decoder to the encoder's own reconstruction.</summary>
public sealed class H264EncoderTests
{
    public static TheoryData<H264Profile, H264EntropyCoder?, bool> Streams => new()
    {
        { H264Profile.ConstrainedBaseline, null, false },
        { H264Profile.Main, null, false },
        { H264Profile.Main, H264EntropyCoder.Cavlc, false },
        { H264Profile.High, null, false },
        { H264Profile.High, H264EntropyCoder.Cavlc, false },
        { H264Profile.High, null, true },
        { H264Profile.ConstrainedBaseline, null, true },
    };

    [Theory]
    [MemberData(nameof(Streams))]
    public void FramesDecodeToTheReconstruction(H264Profile profile, H264EntropyCoder? entropy, bool annexB)
    {
        VideoEncoderOptions options = new()
        {
            Width = 96,
            Height = 64,
            FrameRate = new Rational(25, 1),
            KeyframeInterval = 4,
            H264 = new H264EncoderOptions { Profile = profile, Entropy = entropy, AnnexB = annexB },
        };
        (List<EncodedPacket> packets, List<byte[]> recon, VideoTrackInfo track) = Encode(options, 10);
        Assert.Equal(10, packets.Count);
        Assert.Equal(CodecIds.Avc, track.CodecId);
        Assert.Equal(annexB, track.CodecPrivate.IsEmpty);
        Assert.True(packets[0].IsKeyframe);
        Assert.True(packets[4].IsKeyframe);
        Assert.False(packets[1].IsKeyframe);
        List<YuvRaster<byte>> decoded = Decode(track, packets);
        Assert.Equal(10, decoded.Count);
        for (int i = 0; i < decoded.Count; i++)
        {
            Assert.True(Planes(decoded[i]).AsSpan().SequenceEqual(recon[i]), $"Frame {i} decodes differently from the encoder's reconstruction.");
            decoded[i].Dispose();
        }
    }

    [Fact]
    public void ProfileAndLevelAreInTheAvcC()
    {
        VideoEncoderOptions options = new() { Width = 1280, Height = 720, FrameRate = new Rational(30, 1) };
        using IVideoEncoder encoder = H264Codec.Instance.CreateEncoder(options, MediaContext.Default);
        ReadOnlySpan<byte> avcC = encoder.Track.CodecPrivate.Span;
        Assert.Equal(1, avcC[0]);
        Assert.Equal(100, avcC[1]);
        Assert.Equal(31, avcC[3]);
        Assert.Equal(0xFF, avcC[4]);
    }

    [Fact]
    public void QualityOrdersSize()
    {
        static long Size(int quality)
        {
            VideoEncoderOptions options = new() { Width = 64, Height = 48, Quality = quality, KeyframeInterval = 1 };
            return Encode(options, 2).Packets.Sum(p => (long)p.Data.Length);
        }

        Assert.True(Size(20) < Size(50));
        Assert.True(Size(50) < Size(80));
    }

    [Theory]
    [InlineData(33, 32)]
    [InlineData(32, 31)]
    public void OddSizesAreRefused(int width, int height) =>
        Assert.Throws<ArgumentException>(() => H264Codec.Instance.CreateEncoder(new VideoEncoderOptions { Width = width, Height = height }, MediaContext.Default));

    [Fact]
    public void ALevelTooSmallIsRefused() =>
        Assert.Throws<ArgumentException>(() => H264Codec.Instance.CreateEncoder(
            new VideoEncoderOptions { Width = 1920, Height = 1080, H264 = new H264EncoderOptions { Level = 30 } }, MediaContext.Default));

    [Fact]
    public void CabacInConstrainedBaselineIsRefused() =>
        Assert.Throws<ArgumentException>(() => H264Codec.Instance.CreateEncoder(
            new VideoEncoderOptions { Width = 64, Height = 64, H264 = new H264EncoderOptions { Profile = H264Profile.ConstrainedBaseline, Entropy = H264EntropyCoder.Cabac } },
            MediaContext.Default));

    [Fact]
    public void BadOptionValuesAreRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new H264EncoderOptions { Level = 35 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new H264EncoderOptions { MinQuantizer = 52 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new H264EncoderOptions { MaxQuantizer = -1 });
    }

    [Fact]
    public void HdrMetadataGoesInTheStream()
    {
        HdrMetadata hdr = new()
        {
            MasteringPrimaries = new ColorPrimaries(new(0.708, 0.292), new(0.17, 0.797), new(0.131, 0.046), new(0.3127, 0.329)),
            MasteringMaxLuminance = 1000,
            MasteringMinLuminance = 0.005,
            MaxContentLightLevel = 800,
            MaxFrameAverageLightLevel = 400,
        };
        VideoEncoderOptions options = new() { Width = 32, Height = 32, KeyframeInterval = 1 };
        using IVideoEncoder encoder = H264Codec.Instance.CreateEncoder(options, MediaContext.Default);
        using YuvRaster<byte> picture = EncoderIntraTests.Picture(32, 32, 0);
        encoder.Send(new YuvVideoFrame(picture, new MediaTime(0, new Rational(1, 30))) { Hdr = hdr });
        encoder.Flush();
        Assert.True(encoder.TryReceive(out EncodedPacket? packet));
        using H264VideoDecoder decoder = new(encoder.Track, VideoDecoderOptions.Default, MediaContext.Default);
        decoder.Send(packet);
        decoder.Flush();
        Assert.True(decoder.TryReceive(out YuvVideoFrame? frame));
        using (frame)
        {
            Assert.Equal(1000, frame.Hdr!.MasteringMaxLuminance!.Value, 3);
            Assert.Equal(0.005, frame.Hdr.MasteringMinLuminance!.Value, 4);
            Assert.Equal(800, frame.Hdr.MaxContentLightLevel);
            Assert.Equal(0.708, frame.Hdr.MasteringPrimaries!.Red.X, 4);
        }
    }

    internal static (List<EncodedPacket> Packets, List<byte[]> Recon, VideoTrackInfo Track) Encode(VideoEncoderOptions options, int frames)
    {
        using H264VideoEncoder encoder = (H264VideoEncoder)H264Codec.Instance.CreateEncoder(options, MediaContext.Default);
        List<EncodedPacket> packets = [];
        List<byte[]> recon = [];
        Rational timeBase = options.FrameRate.Reciprocal();
        for (int i = 0; i < frames; i++)
        {
            using YuvRaster<byte> picture = EncoderIntraTests.Picture(options.Width, options.Height, i);
            encoder.Send(new YuvVideoFrame(picture, new MediaTime(i, timeBase)));
            encoder.Coder!.Coded ??= pic => recon.Add(Planes(pic, options.Width, options.Height));
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

        return (packets, recon, encoder.Track);
    }

    internal static List<YuvRaster<byte>> Decode(VideoTrackInfo track, List<EncodedPacket> packets)
    {
        using H264VideoDecoder decoder = new(track, VideoDecoderOptions.Default, MediaContext.Default);
        List<YuvRaster<byte>> frames = [];
        foreach (EncodedPacket packet in packets)
        {
            decoder.Send(packet);
            while (decoder.TryReceive(out YuvVideoFrame? frame))
            {
                frames.Add((YuvRaster<byte>)frame.Planes);
            }
        }

        decoder.Flush();
        while (decoder.TryReceive(out YuvVideoFrame? frame))
        {
            frames.Add((YuvRaster<byte>)frame.Planes);
        }

        return frames;
    }

    /// <summary>A reconstruction's visible samples, Y then Cb then Cr.</summary>
    private static byte[] Planes(H264Picture pic, int width, int height)
    {
        List<byte> bytes = [];
        for (int y = 0; y < height; y++)
        {
            bytes.AddRange(pic.Y<byte>().AsSpan(pic.Origin + (y * pic.Stride), width));
        }

        foreach (byte[] plane in (byte[][])[pic.Cb<byte>(), pic.Cr<byte>()])
        {
            for (int y = 0; y < height / 2; y++)
            {
                bytes.AddRange(plane.AsSpan(pic.ChromaOrigin + (y * pic.ChromaStride), width / 2));
            }
        }

        return [.. bytes];
    }

    private static byte[] Planes(YuvRaster<byte> raster)
    {
        List<byte> bytes = [];
        foreach (YuvPlane<byte> plane in (YuvPlane<byte>[])[raster.Y, raster.U!, raster.V!])
        {
            for (int y = 0; y < plane.Height; y++)
            {
                bytes.AddRange(plane.Row(y).ToArray());
            }
        }

        return [.. bytes];
    }
}
