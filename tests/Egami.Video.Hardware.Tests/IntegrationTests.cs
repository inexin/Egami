using Egami.Pixels;
using Egami.Video.Codecs;
using Egami.Video.Containers;
using Egami.Video.Hardware.Encoding;
using Egami.Video.Hardware.Integration;

namespace Egami.Video.Hardware.Tests;

/// <summary>
/// <see cref="HardwareMediaContextExtensions.WithHardwareCodecs"/>: the GPU decodes what it can, the software codecs the
/// rest, with the same frames either way; encoding moves to the GPU only when asked.
/// </summary>
[Trait("Category", "Gpu")]
public sealed class IntegrationTests
{
    private static string Corpus => Path.Combine(AppContext.BaseDirectory, "corpus", "hevc");

    [Fact]
    public void NothingChangesWithoutUse()
    {
        MediaContext context = MediaContext.Default.WithHevc();
        Assert.Same(context, context.WithHardwareCodecs(HardwareUse.None));
        Assert.Throws<ArgumentNullException>(() => HardwareMediaContextExtensions.WithHardwareCodecs(null!));
    }

    [Fact]
    public void HardwareWrapsTheSoftwareCodecOnce()
    {
        HevcDecoderTests.RequireDecoder();
        MediaContext context = MediaContext.Default.WithH264().WithHevc().WithHardwareCodecs().WithHardwareCodecs(HardwareUse.All);
        HardwareFallbackCodec hevc = Assert.IsType<HardwareFallbackCodec>(context.Codecs.Single(c => c.CodecId == CodecIds.Hevc));
        Assert.Same(HevcCodec.Instance, hevc.Software);
        Assert.Same(HardwareVideoCodecs.Hevc, hevc.Hardware);
        Assert.Equal(context.Codecs.Count, context.Codecs.DistinctBy(c => c.CodecId).Count());
        Assert.Contains(context.Codecs, c => c is HardwareFallbackCodec { Software: H264Codec });
    }

    [Theory]
    [InlineData("b.h265", false)]
    [InlineData("main10.mp4", false)]
    [InlineData("422-10.h265", true)]
    [InlineData("main12.h265", true)]
    public void ReaderGivesTheSoftwareFrames(string clip, bool software)
    {
        HevcDecoderTests.RequireDecoder();
        string path = Path.Combine(Corpus, clip);
        MediaContext plain = MediaContext.Default.WithHevc();
        List<YuvVideoFrame> expected = Read(path, plain);
        List<YuvVideoFrame> actual = Read(path, plain.WithHardwareCodecs());
        try
        {
            Assert.Equal(expected.Count, actual.Count);
            for (int i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i].Timestamp, actual[i].Timestamp);
                Assert.True(SameY(expected[i].Planes!, actual[i].Planes!), $"{clip} frame {i}");
            }
        }
        finally
        {
            expected.ForEach(f => f.Dispose());
            actual.ForEach(f => f.Dispose());
        }

        using FileStream input = File.OpenRead(path);
        using IDemuxer demuxer = MediaFile.OpenDemuxer(input, plain.WithHardwareCodecs(), leaveOpen: true);
        Assert.Equal(software, !HardwareVideoCodecs.Hevc.CanDecode(demuxer.Info.VideoTrack!));
    }

    [Fact]
    public void StreamsTheGpuRefusesAtTheFirstPictureMoveToSoftware()
    {
        HevcDecoderTests.RequireDecoder();
        byte[] stream = File.ReadAllBytes(Path.Combine(Corpus, "444.h265"));
        List<YuvVideoFrame> expected = HevcDecoderTests.Decode(stream, "444.h265", HevcCodec.Instance);
        using MemoryStream input = new(stream);
        MediaContext context = MediaContext.Default.WithHevc().WithHardwareCodecs();
        using IDemuxer demuxer = HevcFormat.Instance.OpenDemuxer(input, context, leaveOpen: true);

        // A track that says nothing of its format, so the hardware decoder only finds out at the first picture.
        VideoTrackInfo track = demuxer.Info.VideoTrack! with { BitDepth = 0, ChromaFormat = null };
        VideoCodec codec = context.FindCodec(track)!;
        using IVideoDecoder decoder = codec.CreateDecoder(track, null, context);
        FallbackVideoDecoder fallback = Assert.IsType<FallbackVideoDecoder>(decoder);
        List<YuvVideoFrame> actual = [];
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                decoder.Send(packet);
            }

            while (decoder.TryReceive(out YuvVideoFrame? frame))
            {
                actual.Add(frame);
            }
        }

        decoder.Flush();
        while (decoder.TryReceive(out YuvVideoFrame? frame))
        {
            actual.Add(frame);
        }

        try
        {
            Assert.True(fallback.UsesSoftware);
            Assert.Equal(expected.Count, actual.Count);
            for (int i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i].Timestamp, actual[i].Timestamp);
                Assert.True(SameY(expected[i].Planes!, actual[i].Planes!), $"frame {i}");
            }
        }
        finally
        {
            expected.ForEach(f => f.Dispose());
            actual.ForEach(f => f.Dispose());
        }
    }

    [Fact]
    public void EncodingStaysInSoftwareUnlessAsked()
    {
        HevcEncoderTests.RequireEncoder();
        VideoEncoderOptions options = new() { Width = 320, Height = 240 };
        MediaContext decode = MediaContext.Default.WithHevc().WithHardwareCodecs();
        MediaContext all = MediaContext.Default.WithHevc().WithHardwareCodecs(HardwareUse.All);
        using (IVideoEncoder encoder = Hevc(decode).CreateEncoder(options, decode))
        {
            Assert.IsNotType<HevcHardwareEncoder>(encoder);
        }

        using (IVideoEncoder encoder = Hevc(all).CreateEncoder(options, all))
        {
            Assert.IsType<HevcHardwareEncoder>(encoder);
        }

        // Sizes the GPU cannot code go to the software encoder.
        using (IVideoEncoder encoder = Hevc(all).CreateEncoder(options with { Width = 8192, Height = 64 }, all))
        {
            Assert.IsNotType<HevcHardwareEncoder>(encoder);
        }

        // Without a software encoder the hardware one is used, and its refusals reach the caller.
        MediaContext hardwareOnly = MediaContext.Default.WithHardwareCodecs(HardwareUse.All);
        Assert.Throws<NotSupportedException>(() => Hevc(hardwareOnly).CreateEncoder(options with { Width = 8192, Height = 64 }, hardwareOnly));

        static VideoCodec Hevc(MediaContext context) => context.Codecs.Single(c => c.CodecId == CodecIds.Hevc);
    }

    private static List<YuvVideoFrame> Read(string path, MediaContext context)
    {
        List<YuvVideoFrame> frames = [];
        using VideoReader reader = VideoReader.Open(path, context: context);
        foreach (YuvVideoFrame frame in reader.ReadFrames())
        {
            frames.Add(frame);
        }

        return frames;
    }

    private static bool SameY(YuvRaster a, YuvRaster b)
    {
        if (a is YuvRaster<byte> x && b is YuvRaster<byte> y)
        {
            return Enumerable.Range(0, x.Height).All(r => x.Y.Row(r).SequenceEqual(y.Y.Row(r)));
        }

        if (a is YuvRaster<ushort> p && b is YuvRaster<ushort> q)
        {
            return Enumerable.Range(0, p.Height).All(r => p.Y.Row(r).SequenceEqual(q.Y.Row(r)));
        }

        return false;
    }
}
