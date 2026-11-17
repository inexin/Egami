using Egami.Pixels;
using Egami.Video.Codecs;

namespace Egami.Video.Hevc.Tests;

/// <summary>The HEVC encoder through <see cref="HevcCodec.CreateEncoder"/>: every frame decodes in Egami's decoder to the encoder's own reconstruction.</summary>
public sealed class EncoderTests
{
    private static readonly YuvColor Color = new(YuvMatrix.Bt709, YuvRange.Limited, ChromaSiting.Center);

    public static TheoryData<HevcProfile?, int, bool, int> Streams => new()
    {
        { null, 8, false, 4 },
        { null, 8, true, 4 },
        { null, 10, false, 4 },
        { HevcProfile.Main10, 8, false, 2 },
        { HevcProfile.Main, 10, false, 0 },
        { null, 8, false, 0 },
        { null, 8, false, 10 },
    };

    [Theory]
    [MemberData(nameof(Streams))]
    public void FramesDecodeToTheReconstruction(HevcProfile? profile, int depth, bool annexB, int effort)
    {
        VideoEncoderOptions options = new()
        {
            Width = 96,
            Height = 72,
            FrameRate = new Rational(25, 1),
            KeyframeInterval = 4,
            Effort = effort,
            Hevc = new HevcEncoderOptions { Profile = profile, AnnexB = annexB },
        };
        (List<EncodedPacket> packets, List<ushort[]> recon, VideoTrackInfo track) = Encode(options, 9, i => Picture(96, 72, i, depth));
        Assert.Equal(9, packets.Count);
        Assert.Equal(CodecIds.Hevc, track.CodecId);
        Assert.Equal(annexB, track.CodecPrivate.IsEmpty);
        Assert.Equal((profile ?? (depth == 8 ? HevcProfile.Main : HevcProfile.Main10)) == HevcProfile.Main10 ? 10 : 8, track.BitDepth);
        Assert.True(packets[0].IsKeyframe);
        Assert.True(packets[4].IsKeyframe);
        Assert.False(packets[1].IsKeyframe);
        List<ushort[]> decoded = Decode(track, packets);
        Assert.Equal(9, decoded.Count);
        for (int i = 0; i < decoded.Count; i++)
        {
            Assert.True(decoded[i].AsSpan().SequenceEqual(recon[i]), $"Frame {i} decodes differently from the encoder's reconstruction.");
        }

        Dispose(packets);
    }

    [Theory]
    [InlineData(66, 38)]
    [InlineData(2, 2)]
    [InlineData(200, 8)]
    public void SizesThatAreNotWholeBlocksAreCropped(int width, int height)
    {
        VideoEncoderOptions options = new() { Width = width, Height = height, KeyframeInterval = 3 };
        (List<EncodedPacket> packets, List<ushort[]> recon, VideoTrackInfo track) = Encode(options, 4, i => Picture(width, height, i, 8));
        List<ushort[]> decoded = Decode(track, packets);
        Assert.Equal(4, decoded.Count);
        for (int i = 0; i < decoded.Count; i++)
        {
            Assert.True(decoded[i].AsSpan().SequenceEqual(recon[i]), $"Frame {i} decodes differently from the encoder's reconstruction.");
        }

        Dispose(packets);
    }

    [Fact]
    public void TheStreamIsTheSameOnAnyNumberOfThreads()
    {
        VideoEncoderOptions options = new() { Width = 320, Height = 200, KeyframeInterval = 3, Effort = 3 };
        MediaContext one = new() { Egami = EgamiContext.Default with { MaxParallelism = 1 } };
        MediaContext many = new() { Egami = EgamiContext.Default with { MaxParallelism = 8 } };
        (List<EncodedPacket> a, _, VideoTrackInfo trackA) = Encode(options, 4, i => Picture(320, 200, i, 8), one);
        (List<EncodedPacket> b, _, VideoTrackInfo trackB) = Encode(options, 4, i => Picture(320, 200, i, 8), many);
        Assert.True(trackA.CodecPrivate.Span.SequenceEqual(trackB.CodecPrivate.Span));
        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
        {
            Assert.True(a[i].Data.Span.SequenceEqual(b[i].Data.Span), $"Packet {i} differs between one and eight threads.");
        }

        Dispose(a);
        Dispose(b);
    }

    [Theory]
    [InlineData(".mp4")]
    [InlineData(".mov")]
    [InlineData(".mkv")]
    public void ContainersKeepTheStream(string extension)
    {
        string path = Path.Combine(Path.GetTempPath(), $"egami-hevc-encode-{Guid.NewGuid():N}{extension}");
        try
        {
            VideoWriterOptions options = new() { Codec = HevcCodec.Instance, FrameRate = new Rational(30, 1), KeyframeInterval = 4 };
            List<YuvRaster<byte>> pictures = [.. Enumerable.Range(0, 6).Select(i => (YuvRaster<byte>)Picture(64, 48, i, 8))];
            using (VideoWriter writer = VideoWriter.Create(path, options))
            {
                foreach (YuvRaster<byte> picture in pictures)
                {
                    writer.WriteFrame(picture);
                }
            }

            (List<EncodedPacket> packets, List<ushort[]> recon, _) = Encode(
                new VideoEncoderOptions { Width = 64, Height = 48, FrameRate = new Rational(30, 1), KeyframeInterval = 4 }, 6, i => Picture(64, 48, i, 8));
            if (extension != ".mkv")
            {
                byte[] file = File.ReadAllBytes(path);
                Assert.True(file.AsSpan().IndexOf("hvc1hvcC"u8) < 0 && file.AsSpan().IndexOf("hvc1"u8) >= 0 && file.AsSpan().IndexOf("hvcC"u8) >= 0, "The sample entry is not hvc1 with an hvcC box.");
            }

            using VideoReader reader = VideoReader.Open(path, context: MediaContext.Default.WithHevc());
            Assert.Equal(CodecIds.Hevc, reader.Track.CodecId);
            Assert.Equal((64, 48), (reader.Track.Width, reader.Track.Height));
            int count = 0;
            foreach (YuvVideoFrame frame in reader.ReadFrames())
            {
                using (frame)
                {
                    Assert.True(Samples(frame.Planes).AsSpan().SequenceEqual(recon[count]), $"Frame {count} read back differently.");
                    count++;
                }
            }

            Assert.Equal(6, count);
            pictures.ForEach(p => p.Dispose());
            Dispose(packets);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ProfileAndLevelAreInTheHvcC()
    {
        VideoEncoderOptions options = new() { Width = 1280, Height = 720, FrameRate = new Rational(30, 1) };
        using IVideoEncoder encoder = HevcCodec.Instance.CreateEncoder(options, MediaContext.Default);
        using (YuvRaster<byte> picture = (YuvRaster<byte>)Picture(1280, 720, 0, 8))
        {
            encoder.Send(new YuvVideoFrame(picture, new MediaTime(0, new Rational(1, 30))));
        }

        ReadOnlySpan<byte> hvcC = encoder.Track.CodecPrivate.Span;
        Assert.Equal(1, hvcC[0]);
        Assert.Equal(1, hvcC[1] & 0x1F); // Main
        Assert.Equal(93, hvcC[12]); // Level 3.1
        Assert.Equal(new Rational(30, 1), encoder.Track.FrameRate);
    }

    [Fact]
    public void QualityOrdersSize()
    {
        static long Size(int quality)
        {
            VideoEncoderOptions options = new() { Width = 64, Height = 48, Quality = quality, KeyframeInterval = 1 };
            (List<EncodedPacket> packets, _, _) = Encode(options, 2, i => Picture(64, 48, i, 8));
            long size = packets.Sum(p => (long)p.Data.Length);
            Dispose(packets);
            return size;
        }

        Assert.True(Size(20) < Size(50));
        Assert.True(Size(50) < Size(80));
    }

    [Theory]
    [InlineData(33, 32)]
    [InlineData(32, 31)]
    public void OddSizesAreRefused(int width, int height) =>
        Assert.Throws<ArgumentException>(() => HevcCodec.Instance.CreateEncoder(new VideoEncoderOptions { Width = width, Height = height }, MediaContext.Default));

    [Fact]
    public void ALevelTooSmallIsRefused() =>
        Assert.Throws<ArgumentException>(() => HevcCodec.Instance.CreateEncoder(
            new VideoEncoderOptions { Width = 1920, Height = 1080, Hevc = new HevcEncoderOptions { Level = 30 } }, MediaContext.Default));

    [Fact]
    public void BadOptionValuesAreRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HevcEncoderOptions { Level = 35 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new HevcEncoderOptions { MinQuantizer = 52 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new HevcEncoderOptions { MaxQuantizer = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new HevcEncoderOptions { BFrames = 17 });
        Assert.Throws<ArgumentException>(() => HevcCodec.Instance.CreateEncoder(
            new VideoEncoderOptions { Width = 64, Height = 64, Hevc = new HevcEncoderOptions { MinQuantizer = 40, MaxQuantizer = 30 } }, MediaContext.Default));
        Assert.True(HevcCodec.Instance.CanEncode);
    }

    /// <summary>A test picture: gradients, a moving square and a little texture, 8-bit or 16-bit planes of a depth.</summary>
    internal static YuvRaster Picture(int width, int height, int frame, int depth)
    {
        int max = (1 << depth) - 1;
        int Sample(int x, int y, int c) => Math.Clamp(
            ((((x * 3) + (y * 2) + (c * 40) + (frame * 5)) % 220) + 16 + (((x * 7) ^ (y * 13) ^ frame) & 7)
            + ((x - (frame * 3) & 31) < 12 && (y - (frame * 2) & 31) < 12 ? 60 : 0)) << (depth - 8), 0, max);
        if (depth == 8)
        {
            YuvRaster<byte> bytes = YuvRaster.Create(width, height, ChromaFormat.Yuv420, Color);
            Fill(bytes.Y, 0);
            Fill(bytes.U!, 1);
            Fill(bytes.V!, 2);
            return bytes;

            void Fill(YuvPlane<byte> plane, int c)
            {
                for (int y = 0; y < plane.Height; y++)
                {
                    Span<byte> row = plane.Row(y);
                    for (int x = 0; x < row.Length; x++)
                    {
                        row[x] = (byte)Sample(x, y, c);
                    }
                }
            }
        }

        YuvRaster<ushort> words = YuvRaster.Create(width, height, ChromaFormat.Yuv420, depth, Color);
        FillWords(words.Y, 0);
        FillWords(words.U!, 1);
        FillWords(words.V!, 2);
        return words;

        void FillWords(YuvPlane<ushort> plane, int c)
        {
            for (int y = 0; y < plane.Height; y++)
            {
                Span<ushort> row = plane.Row(y);
                for (int x = 0; x < row.Length; x++)
                {
                    row[x] = (ushort)Sample(x, y, c);
                }
            }
        }
    }

    internal static (List<EncodedPacket> Packets, List<ushort[]> Recon, VideoTrackInfo Track) Encode(VideoEncoderOptions options, int frames,
        Func<int, YuvRaster> make, MediaContext? context = null)
    {
        using HevcVideoEncoder encoder = (HevcVideoEncoder)HevcCodec.Instance.CreateEncoder(options, context ?? MediaContext.Default);
        List<EncodedPacket> packets = [];
        List<ushort[]> recon = [.. Enumerable.Repeat(Array.Empty<ushort>(), frames)];
        Rational timeBase = options.FrameRate.Reciprocal();
        for (int i = 0; i < frames; i++)
        {
            using YuvRaster picture = make(i);
            encoder.Send(new YuvVideoFrame(picture, new MediaTime(i, timeBase)));
            encoder.Coder!.Coded ??= (pic, index) => recon[index] = Samples(pic.Planes, pic.Widths, options.Width, options.Height);
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

    internal static List<ushort[]> Decode(VideoTrackInfo track, List<EncodedPacket> packets)
    {
        using IVideoDecoder decoder = HevcCodec.Instance.CreateDecoder(track, null, MediaContext.Default);
        List<ushort[]> frames = [];
        foreach (EncodedPacket packet in packets)
        {
            decoder.Send(packet);
            Drain();
        }

        decoder.Flush();
        Drain();
        return frames;

        void Drain()
        {
            while (decoder.TryReceive(out YuvVideoFrame? frame))
            {
                using (frame)
                {
                    frames.Add(Samples(frame.Planes));
                }
            }
        }
    }

    /// <summary>A reconstruction's visible samples, Y then Cb then Cr.</summary>
    internal static ushort[] Samples(ushort[][] planes, int[] strides, int width, int height)
    {
        List<ushort> samples = [];
        for (int c = 0; c < 3; c++)
        {
            int w = c == 0 ? width : width / 2, h = c == 0 ? height : height / 2;
            for (int y = 0; y < h; y++)
            {
                samples.AddRange(planes[c].AsSpan(y * strides[c], w));
            }
        }

        return [.. samples];
    }

    /// <summary>A decoded frame's samples, Y then Cb then Cr, widened to 16 bits.</summary>
    internal static ushort[] Samples(YuvRaster raster)
    {
        List<ushort> samples = [];
        if (raster is YuvRaster<byte> bytes)
        {
            foreach (YuvPlane<byte> plane in (YuvPlane<byte>[])[bytes.Y, bytes.U!, bytes.V!])
            {
                for (int y = 0; y < plane.Height; y++)
                {
                    foreach (byte b in plane.Row(y))
                    {
                        samples.Add(b);
                    }
                }
            }
        }
        else
        {
            YuvRaster<ushort> words = (YuvRaster<ushort>)raster;
            foreach (YuvPlane<ushort> plane in (YuvPlane<ushort>[])[words.Y, words.U!, words.V!])
            {
                for (int y = 0; y < plane.Height; y++)
                {
                    samples.AddRange(plane.Row(y));
                }
            }
        }

        return [.. samples];
    }

    internal static void Dispose(List<EncodedPacket> packets)
    {
        foreach (EncodedPacket packet in packets)
        {
            packet.Dispose();
        }
    }
}
