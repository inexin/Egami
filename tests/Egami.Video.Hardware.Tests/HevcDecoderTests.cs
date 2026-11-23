using Egami.Pixels;
using Egami.Video.Codecs;
using Egami.Video.Containers;

namespace Egami.Video.Hardware.Tests;

/// <summary>
/// The hardware HEVC decoder: every 4:2:0 Main and Main 10 clip of the HEVC test set decodes to the same frames, sample for
/// sample, as Egami's software decoder, with the same timestamps and color; the other clips are refused. The GPU tests skip
/// without a GPU that decodes HEVC.
/// </summary>
[Trait("Category", "Gpu")]
public sealed class HevcDecoderTests
{
    /// <summary>The clips the hardware decoder refuses: monochrome, 4:2:2, 4:4:4 and 12 bits.</summary>
    private static readonly string[] Refused = ["422-10", "444", "444-12", "gray", "main12"];

    public static TheoryData<string> Clips
    {
        get
        {
            TheoryData<string> clips = [];
            foreach (string path in Directory.EnumerateFiles(Corpus).Where(p => p.EndsWith(".h265", StringComparison.Ordinal) || p.EndsWith(".mp4", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
            {
                clips.Add(Path.GetFileName(path));
            }

            return clips;
        }
    }

    private static string Corpus => Path.Combine(AppContext.BaseDirectory, "corpus", "hevc");

    [Theory]
    [MemberData(nameof(Clips))]
    public void ClipsMatchTheSoftwareDecoder(string clip)
    {
        RequireDecoder();
        byte[] stream = File.ReadAllBytes(Path.Combine(Corpus, clip));
        if (Refused.Contains(Path.GetFileNameWithoutExtension(clip)))
        {
            Assert.Throws<UnsupportedFormatException>(() => Decode(stream, clip, HardwareVideoCodecs.Hevc));
            return;
        }

        List<YuvVideoFrame> expected = Decode(stream, clip, HevcCodec.Instance);
        List<YuvVideoFrame> actual = Decode(stream, clip, HardwareVideoCodecs.Hevc);
        try
        {
            Assert.Equal(expected.Count, actual.Count);
            for (int i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i].Timestamp, actual[i].Timestamp);
                Assert.Equal(expected[i].Planes!.Color, actual[i].Planes!.Color);
                Assert.Equal(expected[i].Hdr, actual[i].Hdr);
                Assert.Equal(expected[i].Hdr10Plus, actual[i].Hdr10Plus);
                if (expected[i].Planes is YuvRaster<byte> a)
                {
                    YuvRaster<byte> b = Assert.IsType<YuvRaster<byte>>(actual[i].Planes);
                    AssertSame(a.Y, b.Y, $"{clip} frame {i} Y");
                    AssertSame(a.U!, b.U!, $"{clip} frame {i} U");
                    AssertSame(a.V!, b.V!, $"{clip} frame {i} V");
                }
                else
                {
                    YuvRaster<ushort> c = Assert.IsType<YuvRaster<ushort>>(expected[i].Planes);
                    YuvRaster<ushort> d = Assert.IsType<YuvRaster<ushort>>(actual[i].Planes);
                    Assert.Equal(c.BitDepth, d.BitDepth);
                    AssertSame(c.Y, d.Y, $"{clip} frame {i} Y");
                    AssertSame(c.U!, d.U!, $"{clip} frame {i} U");
                    AssertSame(c.V!, d.V!, $"{clip} frame {i} V");
                }
            }
        }
        finally
        {
            expected.ForEach(f => f.Dispose());
            actual.ForEach(f => f.Dispose());
        }
    }

    [Fact]
    public void ResetRestartsAtTheNextKeyframe()
    {
        RequireDecoder();
        byte[] stream = File.ReadAllBytes(Path.Combine(Corpus, "b.h265"));
        using MemoryStream input = new(stream);
        using IDemuxer demuxer = HevcFormat.Instance.OpenDemuxer(input, MediaContext.Default, leaveOpen: true);
        List<EncodedPacket> packets = [];
        while (demuxer.ReadPacket() is { } packet)
        {
            packets.Add(packet);
        }

        using IVideoDecoder decoder = HardwareVideoCodecs.Hevc.CreateDecoder(demuxer.Info.VideoTrack!, null, MediaContext.Default);
        List<YuvVideoFrame> first = Run(decoder, packets);
        decoder.Reset();
        List<YuvVideoFrame> second = Run(decoder, packets);
        try
        {
            Assert.Equal(first.Count, second.Count);
            for (int i = 0; i < first.Count; i++)
            {
                AssertSame(((YuvRaster<byte>)first[i].Planes!).Y, ((YuvRaster<byte>)second[i].Planes!).Y, $"frame {i}");
            }
        }
        finally
        {
            first.ForEach(f => f.Dispose());
            second.ForEach(f => f.Dispose());
            packets.ForEach(p => p.Dispose());
        }
    }

    [Fact]
    public void CanDecodeOnlyFourTwoZeroUpToTenBits()
    {
        RequireDecoder();
        VideoTrackInfo track = new() { Number = 1, Kind = TrackKind.Video, CodecId = CodecIds.Hevc, TimeBase = new Rational(1, 25), Width = 64, Height = 64 };
        Assert.True(HardwareVideoCodecs.Hevc.CanDecode(track));
        Assert.True(HardwareVideoCodecs.Hevc.CanDecode(track with { BitDepth = 10 }));
        Assert.False(HardwareVideoCodecs.Hevc.CanDecode(track with { BitDepth = 12 }));
        Assert.False(HardwareVideoCodecs.Hevc.CanDecode(track with { ChromaFormat = ChromaFormat.Yuv422 }));
        Assert.False(HardwareVideoCodecs.Hevc.CanDecode(track with { CodecId = CodecIds.Avc }));
    }

    internal static void RequireDecoder() =>
        Assert.SkipUnless(HardwareVideoCodecs.IsAvailable(CodecIds.Hevc, HardwareDirection.Decode), "No GPU decodes HEVC: " + HardwareVideoCodecs.UnavailableReason);

    internal static List<YuvVideoFrame> Decode(byte[] stream, string name, VideoCodec codec)
    {
        using MemoryStream input = new(stream);
        MediaContext context = MediaContext.Default.WithHevc();
        using IDemuxer demuxer = name.EndsWith(".mp4", StringComparison.Ordinal)
            ? Mp4Format.Instance.OpenDemuxer(input, context, leaveOpen: true)
            : HevcFormat.Instance.OpenDemuxer(input, context, leaveOpen: true);
        using IVideoDecoder decoder = codec.CreateDecoder(demuxer.Info.VideoTrack!, null, context);
        List<EncodedPacket> packets = [];
        while (demuxer.ReadPacket() is { } packet)
        {
            if (packet.TrackNumber == demuxer.Info.VideoTrack!.Number)
            {
                packets.Add(packet);
            }
            else
            {
                packet.Dispose();
            }
        }

        try
        {
            return Run(decoder, packets);
        }
        finally
        {
            packets.ForEach(p => p.Dispose());
        }
    }

    private static List<YuvVideoFrame> Run(IVideoDecoder decoder, List<EncodedPacket> packets)
    {
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

    private static void AssertSame<T>(YuvPlane<T> expected, YuvPlane<T> actual, string what)
        where T : unmanaged, IEquatable<T>
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        for (int y = 0; y < expected.Height; y++)
        {
            ReadOnlySpan<T> a = expected.Row(y), b = actual.Row(y);
            if (!a.SequenceEqual(b))
            {
                int x = 0;
                while (a[x].Equals(b[x]))
                {
                    x++;
                }

                Assert.Fail($"{what} differs first at ({x}, {y}): {a[x]} expected, {b[x]} decoded.");
            }
        }
    }
}
