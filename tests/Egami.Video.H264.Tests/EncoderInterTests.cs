using Egami.Pixels;
using Egami.Video.Codecs;
using Egami.Video.Codecs.H264;

namespace Egami.Video.H264.Tests;

/// <summary>P pictures: moving and fading frames decode in Egami's decoder to the encoder's reconstruction, and code smaller than intra.</summary>
public sealed class EncoderInterTests
{
    public static TheoryData<H264Profile, H264EntropyCoder?, int> Streams => new()
    {
        { H264Profile.ConstrainedBaseline, null, 4 },
        { H264Profile.Main, null, 4 },
        { H264Profile.Main, H264EntropyCoder.Cavlc, 4 },
        { H264Profile.High, null, 0 },
        { H264Profile.High, null, 1 },
        { H264Profile.High, null, 2 },
        { H264Profile.High, null, 4 },
        { H264Profile.High, H264EntropyCoder.Cavlc, 4 },
        { H264Profile.High, null, 6 },
        { H264Profile.High, H264EntropyCoder.Cavlc, 6 },
        { H264Profile.High, null, 7 },
        { H264Profile.Main, H264EntropyCoder.Cavlc, 7 },
        { H264Profile.High, null, 10 },
    };

    [Theory]
    [MemberData(nameof(Streams))]
    public void MovingFramesDecodeToTheReconstruction(H264Profile profile, H264EntropyCoder? entropy, int effort)
    {
        VideoEncoderOptions options = new()
        {
            Width = 80,
            Height = 64,
            Effort = effort,
            KeyframeInterval = 100,
            H264 = new H264EncoderOptions { Profile = profile, Entropy = entropy },
        };
        List<EncodedPacket> packets = Check(options, 8, Moving);
        long intra = packets[0].Data.Length, inter = packets.Skip(1).Sum(p => (long)p.Data.Length) / (packets.Count - 1);
        Assert.True(inter * 3 < intra, $"P frames average {inter} bytes against the keyframe's {intra}.");
    }

    [Theory]
    [InlineData(H264Profile.High, null)]
    [InlineData(H264Profile.Main, H264EntropyCoder.Cavlc)]
    [InlineData(H264Profile.ConstrainedBaseline, null)]
    public void FadesDecodeToTheReconstruction(H264Profile profile, H264EntropyCoder? entropy)
    {
        VideoEncoderOptions options = new()
        {
            Width = 64,
            Height = 48,
            KeyframeInterval = 100,
            H264 = new H264EncoderOptions { Profile = profile, Entropy = entropy },
        };
        Check(options, 8, Fade);
    }

    [Fact]
    public void WeightsMakeFadesSmaller()
    {
        static long Size(H264Profile profile)
        {
            VideoEncoderOptions options = new() { Width = 64, Height = 48, KeyframeInterval = 100, Quality = 70, H264 = new H264EncoderOptions { Profile = profile, Entropy = H264EntropyCoder.Cavlc } };
            return Check(options, 6, Fade).Skip(1).Sum(p => (long)p.Data.Length);
        }

        // Constrained Baseline has no weighted prediction; Main weights the fade.
        Assert.True(Size(H264Profile.Main) < Size(H264Profile.ConstrainedBaseline));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QuantizersPerMacroblockDecode(bool adaptive)
    {
        foreach (H264EntropyCoder entropy in (H264EntropyCoder[])[H264EntropyCoder.Cabac, H264EntropyCoder.Cavlc])
        {
            VideoEncoderOptions options = new()
            {
                Width = 160,
                Height = 96,
                KeyframeInterval = 5,
                H264 = new H264EncoderOptions { AdaptiveQuantization = adaptive, Entropy = entropy, MinQuantizer = 10, MaxQuantizer = 40 },
            };
            Check(options, 7, Moving);
        }
    }

    [Fact]
    public void ProbeCodesAKeyAndAPFrame()
    {
        H264FrameCoder coder = new(new H264StreamConfig
        {
            Width = 64,
            Height = 48,
            ProfileIdc = 100,
            LevelIdc = 30,
            Cabac = true,
            Transform8x8 = true,
            FrameRate = new Rational(25, 1),
            Color = H264VideoEncoder.H264Color,
        }, 4, H264EncoderOptions.Default, null);
        using YuvRaster<byte> key = Moving(64, 48, 0), next = Moving(64, 48, 1);
        (long keyBits, long nextBits) = coder.Probe(key, next, 26);
        Assert.True(keyBits > 0);
        Assert.True(nextBits > 0 && nextBits < keyBits);
    }

    /// <summary>Encodes frames, checks each decodes to the reconstruction, and returns the packets.</summary>
    private static List<EncodedPacket> Check(VideoEncoderOptions options, int frames, Func<int, int, int, YuvRaster<byte>> make)
    {
        using H264VideoEncoder encoder = (H264VideoEncoder)H264Codec.Instance.CreateEncoder(options, MediaContext.Default);
        List<EncodedPacket> packets = [];
        List<byte[]> recon = [];
        Rational timeBase = options.FrameRate.Reciprocal();
        for (int i = 0; i < frames; i++)
        {
            using YuvRaster<byte> picture = make(options.Width, options.Height, i);
            encoder.Send(new YuvVideoFrame(picture, new MediaTime(i, timeBase)));
            encoder.Coder!.Coded ??= pic => recon.Add(H264EncoderTests.Planes(pic, options.Width, options.Height));
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

        Assert.Equal(frames, packets.Count);
        List<YuvRaster<byte>> decoded = H264EncoderTests.Decode(encoder.Track, packets);
        Assert.Equal(frames, decoded.Count);
        for (int i = 0; i < frames; i++)
        {
            Assert.True(H264EncoderTests.Planes(decoded[i]).AsSpan().SequenceEqual(recon[i]), $"Frame {i} decodes differently from the encoder's reconstruction.");
            decoded[i].Dispose();
        }

        return packets;
    }

    /// <summary>
    /// A smooth texture panning a fractional distance each frame, with a square moving the other way across it and a little
    /// noise.
    /// </summary>
    internal static YuvRaster<byte> Moving(int width, int height, int frame)
    {
        YuvRaster<byte> picture = YuvRaster.Create(width, height, ChromaFormat.Yuv420, YuvColor.Bt601Limited);
        Random random = new(frame + 7);
        double dx = frame * 1.25, dy = frame * 0.75;
        int bx = 40 - (frame * 3), by = 8 + (frame * 2);
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = picture.Y.Row(y);
            for (int x = 0; x < width; x++)
            {
                double u = x + dx, v = y + dy;
                double value = 128 + (50 * Math.Sin(u * 0.31) * Math.Cos(v * 0.23)) + (30 * Math.Sin((u + v) * 0.11));
                if (x >= bx && x < bx + 16 && y >= by && y < by + 16)
                {
                    value = 40 + (((x - bx) ^ (y - by)) * 9 % 160);
                }

                row[x] = (byte)Math.Clamp((int)Math.Round(value) + random.Next(-2, 3), 0, 255);
            }
        }

        for (int y = 0; y < height / 2; y++)
        {
            Span<byte> cb = picture.U!.Row(y), cr = picture.V!.Row(y);
            for (int x = 0; x < width / 2; x++)
            {
                double u = (2 * x) + dx, v = (2 * y) + dy;
                cb[x] = (byte)Math.Clamp((int)Math.Round(128 + (30 * Math.Sin(u * 0.13))), 0, 255);
                cr[x] = (byte)Math.Clamp((int)Math.Round(128 + (30 * Math.Cos(v * 0.17))), 0, 255);
            }
        }

        return picture;
    }

    /// <summary>A still picture fading toward black.</summary>
    internal static YuvRaster<byte> Fade(int width, int height, int frame)
    {
        YuvRaster<byte> picture = Moving(width, height, 0);
        double gain = 1 - (frame * 0.1);
        foreach (YuvPlane<byte> plane in (YuvPlane<byte>[])[picture.Y, picture.U!, picture.V!])
        {
            int mid = plane == picture.Y ? 16 : 128;
            for (int y = 0; y < plane.Height; y++)
            {
                Span<byte> row = plane.Row(y);
                for (int x = 0; x < row.Length; x++)
                {
                    row[x] = (byte)Math.Clamp((int)Math.Round(mid + ((row[x] - mid) * gain)), 0, 255);
                }
            }
        }

        return picture;
    }
}
