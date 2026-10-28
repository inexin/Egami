using Egami.Pixels;
using Egami.Video.Codecs;
using Egami.Video.Containers;

namespace Egami.Video.Tests.Codecs;

/// <summary>The FFV1 encoder: every corpus clip's frames coded and decoded back unchanged, in every coder (V6 tasks 11 to 13).</summary>
public class Ffv1EncoderTests
{
    public static TheoryData<string, Ffv1Coder> ClipsAndCoders
    {
        get
        {
            TheoryData<string, Ffv1Coder> data = [];
            foreach (string clip in Ffv1Tests.ClipNames)
            {
                (_, int depth, _) = Ffv1Tests.ReadReference(clip);
                data.Add(clip, Ffv1Coder.RangeCustomTable);
                data.Add(clip, Ffv1Coder.RangeDefaultTable);
                if (depth == 8)
                {
                    data.Add(clip, Ffv1Coder.GolombRice);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ClipsAndCoders))]
    public void CorpusFramesRoundTrip(string clip, Ffv1Coder coder)
    {
        (string file, int depth, List<string> expected) = Ffv1Tests.ReadReference(clip);
        byte[] encoded = Reencode(Ffv1Tests.PathOf(file), new Ffv1EncoderOptions { Coder = coder, BitsPerSample = depth });
        Assert.Equal(expected, Decode(encoded, depth));
    }

    [Theory]
    [InlineData(1, Ffv1ContextModel.Small, 1)]
    [InlineData(3, Ffv1ContextModel.Large, 4)]
    [InlineData(24, Ffv1ContextModel.Large, 2)]
    public void SettingsRoundTrip(int slices, Ffv1ContextModel model, int keyframeInterval)
    {
        foreach (string clip in (string[])["v3-odd-97x61", "v3-gop", "v3-bgra", "v3-422p10", "v3-411", "v3-ya8"])
        {
            (string file, int depth, List<string> expected) = Ffv1Tests.ReadReference(clip);
            byte[] encoded = Reencode(Ffv1Tests.PathOf(file), new Ffv1EncoderOptions { Slices = slices, ContextModel = model, BitsPerSample = depth }, keyframeInterval);
            Assert.Equal(expected, Decode(encoded, depth));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void OutputIsTheSameOnAnyNumberOfThreads(int threads)
    {
        string path = Ffv1Tests.PathOf("v3-slices-24.mkv");
        byte[] one = Reencode(path, new Ffv1EncoderOptions { Slices = 12 }, threads: 1);
        Assert.Equal(one, Reencode(path, new Ffv1EncoderOptions { Slices = 12 }, threads: threads));
    }

    [Fact]
    public void RgbRastersAreCodedAsRgbAndComeBackExactly()
    {
        using Raster<Rgba8> image = Raster.Create<Rgba8>(37, 21);
        for (int y = 0; y < image.Height; y++)
        {
            Span<Rgba8> row = image.Row(y);
            for (int x = 0; x < image.Width; x++)
            {
                row[x] = new Rgba8((byte)(x * 7), (byte)(y * 11), (byte)(x ^ y), (byte)(255 - x));
            }
        }

        MemoryStream output = new();
        using (VideoWriter writer = VideoWriter.Create(output, "mkv", new VideoWriterOptions { Codec = VideoCodecs.Ffv1 }, leaveOpen: true))
        {
            writer.WriteFrame(image);
            writer.Finish();
        }

        using VideoReader reader = VideoReader.Open(new MemoryStream(output.ToArray()));
        Assert.Equal(CodecIds.Ffv1, reader.Track.CodecId);
        using YuvVideoFrame frame = reader.ReadFrame()!;
        Assert.Equal(YuvMatrix.Identity, frame.Raster.Color.Matrix);
        Assert.True(frame.Raster.HasAlpha);
        using Raster<Rgba8> back = frame.ToRaster<Rgba8>();
        for (int y = 0; y < image.Height; y++)
        {
            Assert.True(image.Row(y).SequenceEqual(back.Row(y)), $"row {y}");
        }
    }

    [Theory]
    [InlineData("v3-422p10")]
    [InlineData("v3-bgra")]
    [InlineData("v3-gop-rice")]
    public void AviFilesRoundTrip(string clip)
    {
        (string file, int depth, List<string> expected) = Ffv1Tests.ReadReference(clip);
        byte[] avi = Reencode(Ffv1Tests.PathOf(file), new Ffv1EncoderOptions { BitsPerSample = depth }, container: "avi");
        Assert.Equal(expected, Decode(avi, depth));
        using VideoReader reader = VideoReader.Open(new MemoryStream(avi));
        Assert.Equal(CodecIds.Ffv1, reader.Track.CodecId);
        Assert.Equal(depth, reader.Track.BitDepth);
    }

    [Fact]
    public void QualityIsReportedAsIgnored()
    {
        List<EgamiDiagnostic> reported = [];
        MediaContext context = MediaContext.Default with { Egami = EgamiContext.Default with { Diagnostics = reported.Add } };
        using VideoReader reader = VideoReader.Open(Ffv1Tests.PathOf("v3-420.mkv"));
        using YuvVideoFrame frame = reader.ReadFrame()!;
        using IVideoEncoder encoder = VideoCodecs.Ffv1.CreateEncoder(new VideoEncoderOptions { Width = frame.Width, Height = frame.Height, Quality = 50 }, context);
        encoder.Send(frame);
        encoder.Send(frame);
        Assert.Equal("Ffv1.QualityIgnored", Assert.Single(reported).Id);
    }

    [Fact]
    public void DefaultsAreTheArchivalProfile()
    {
        using VideoReader reader = VideoReader.Open(Ffv1Tests.PathOf("v3-slices-24.mkv"));
        using YuvVideoFrame frame = reader.ReadFrame()!;
        using IVideoEncoder encoder = VideoCodecs.Ffv1.CreateEncoder(new VideoEncoderOptions { Width = frame.Width, Height = frame.Height }, MediaContext.Default);
        encoder.Send(frame);
        Assert.True(encoder.TryReceive(out EncodedPacket? packet));
        using (packet)
        {
            Assert.True(packet.IsKeyframe);
            Dictionary<string, long> fields = [];
            foreach (CodecHeaderField field in CodecHeaders.Describe(encoder.Track, packet))
            {
                fields.TryAdd(field.Name, field.Value);
            }

            Assert.Equal(3, fields["version"]);
            Assert.Equal(4, fields["micro_version"]);
            Assert.Equal(2, fields["coder_type"]);
            Assert.Equal(1, fields["ec"]);
            Assert.Equal(1, fields["intra"]);
            Assert.Equal(4, CodecHeaders.Describe(encoder.Track, packet).Count(f => f.Name == "slice"));
        }

        Assert.Equal(10, encoder.Track.BitDepth);
        Assert.Equal(ChromaFormat.Yuv420, encoder.Track.ChromaFormat);
    }

    [Fact]
    public void ALayoutChangeIsRefused()
    {
        using VideoReader a = VideoReader.Open(Ffv1Tests.PathOf("v3-slices-1.mkv"));
        using YuvVideoFrame first = a.ReadFrame()!;
        using YuvRaster other = YuvRaster.Create(first.Width, first.Height, ChromaFormat.Yuv420, YuvColor.Bt601Limited);
        using YuvVideoFrame second = new(other, new MediaTime(1, new Rational(1, 25)));
        using IVideoEncoder encoder = VideoCodecs.Ffv1.CreateEncoder(new VideoEncoderOptions { Width = first.Width, Height = first.Height }, MediaContext.Default);
        encoder.Send(first);
        Assert.Throws<ArgumentException>(() => encoder.Send(second));
    }

    [Theory]
    [InlineData(1, 512)]
    [InlineData(4, 8 * 1024)]
    public void SteadyStateEncodingAllocatesLittleBeyondThePacket(int threads, long budget)
    {
        MediaContext context = MediaContext.Default with { Egami = EgamiContext.Default with { MaxParallelism = threads } };
        using VideoReader reader = VideoReader.Open(Ffv1Tests.PathOf("v3-slices-24.mkv"));
        using YuvVideoFrame frame = reader.ReadFrame()!;
        using IVideoEncoder encoder = VideoCodecs.Ffv1.CreateEncoder(
            new VideoEncoderOptions { Width = frame.Width, Height = frame.Height, Ffv1 = new Ffv1EncoderOptions { Slices = 24 } }, context);
        long least = long.MaxValue;
        for (int i = 0; i < 12; i++)
        {
            long start = GC.GetAllocatedBytesForCurrentThread();
            encoder.Send(frame);
            Assert.True(encoder.TryReceive(out EncodedPacket? packet));
            long used = GC.GetAllocatedBytesForCurrentThread() - start;
            packet.Dispose();
            if (i >= 4)
            {
                least = Math.Min(least, used);
            }
        }

        Assert.True(least < budget, $"{least} bytes a frame");
    }

    internal static byte[] Reencode(string path, Ffv1EncoderOptions ffv1, int keyframeInterval = 1, int threads = 4, string container = "mkv")
    {
        MediaContext context = MediaContext.Default with { Egami = EgamiContext.Default with { MaxParallelism = threads } };
        using VideoReader reader = VideoReader.Open(path, context: context);
        MemoryStream output = new();
        IVideoEncoder? encoder = null;
        IMuxer? muxer = null;
        List<EncodedPacket> held = [];
        try
        {
            foreach (YuvVideoFrame frame in reader.ReadFrames())
            {
                using (frame)
                {
                    encoder ??= VideoCodecs.Ffv1.CreateEncoder(
                        new VideoEncoderOptions { Width = frame.Width, Height = frame.Height, FrameRate = reader.Track.FrameRate ?? new Rational(25, 1), KeyframeInterval = keyframeInterval, Ffv1 = ffv1 }, context);
                    encoder.Send(frame);
                    muxer ??= MediaFile.CreateMuxer(output, container, [encoder.Track], leaveOpen: true);
                    while (encoder.TryReceive(out EncodedPacket? packet))
                    {
                        using (packet)
                        {
                            muxer.WritePacket(packet);
                        }
                    }
                }
            }
        }
        finally
        {
            muxer?.Dispose();
            encoder?.Dispose();
            foreach (EncodedPacket packet in held)
            {
                packet.Dispose();
            }
        }

        return output.ToArray();
    }

    internal static List<string> Decode(byte[] file, int depth)
    {
        using VideoReader reader = VideoReader.Open(new MemoryStream(file), new VideoReaderOptions { Errors = DecodeErrorMode.Throw });
        List<string> md5 = [];
        foreach (YuvVideoFrame frame in reader.ReadFrames())
        {
            using (frame)
            {
                md5.Add(Ffv1Tests.Md5(frame.Raster, depth));
            }
        }

        return md5;
    }
}
