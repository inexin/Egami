using Egami.Pixels;
using Egami.Video.Codecs;
using Egami.Video.Containers;

namespace Egami.Video.Hardware.Tests;

/// <summary>
/// The hardware H.264 decoder: every progressive 8-bit 4:2:0 clip of the H.264 test set decodes to the same frames, sample for
/// sample, as Egami's software decoder, with the same timestamps; the other clips are refused. The GPU tests skip without a GPU
/// that decodes H.264.
/// </summary>
[Trait("Category", "Gpu")]
public sealed class H264DecoderTests
{
    /// <summary>The clips the hardware decoder refuses: interlaced, monochrome, 4:2:2 and more than 8 bits.</summary>
    private static readonly string[] Refused =
    [
        "gray", "high10", "high10-intra", "high10-qp-low", "high422", "high422-10", "high422-10-intra", "high422-cavlc",
        "mbaff-8x8", "mbaff-b-cavlc", "mbaff-b-spatial", "mbaff-b-temporal", "mbaff-bff", "mbaff-fake", "mbaff-high10",
        "mbaff-high422", "mbaff-intra", "mbaff-intra-cavlc", "mbaff-p", "mbaff-p-cavlc", "mbaff-pic-struct", "mbaff-slices",
        "mbaff-weighted",
    ];

    public static TheoryData<string> Clips
    {
        get
        {
            TheoryData<string> clips = [];
            foreach (string path in Directory.EnumerateFiles(Corpus, "*.h264").Order(StringComparer.Ordinal))
            {
                clips.Add(Path.GetFileNameWithoutExtension(path));
            }

            return clips;
        }
    }

    private static string Corpus => Path.Combine(AppContext.BaseDirectory, "corpus", "h264");

    [Theory]
    [MemberData(nameof(Clips))]
    public void ClipsMatchTheSoftwareDecoder(string clip)
    {
        RequireDecoder();
        byte[] stream = File.ReadAllBytes(Path.Combine(Corpus, clip + ".h264"));
        if (Refused.Contains(clip))
        {
            Assert.Throws<UnsupportedFormatException>(() => Decode(stream, HardwareVideoCodecs.H264));
            return;
        }

        List<YuvVideoFrame> expected = Decode(stream, H264Codec.Instance);
        List<YuvVideoFrame> actual = Decode(stream, HardwareVideoCodecs.H264);
        try
        {
            Assert.Equal(expected.Count, actual.Count);
            for (int i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i].Timestamp, actual[i].Timestamp);
                YuvRaster<byte> a = Assert.IsType<YuvRaster<byte>>(expected[i].Planes);
                YuvRaster<byte> b = Assert.IsType<YuvRaster<byte>>(actual[i].Planes);
                Assert.Equal(a.Color, b.Color);
                AssertSame(a.Y, b.Y, $"{clip} frame {i} Y");
                AssertSame(a.U!, b.U!, $"{clip} frame {i} U");
                AssertSame(a.V!, b.V!, $"{clip} frame {i} V");
            }
        }
        finally
        {
            expected.ForEach(f => f.Dispose());
            actual.ForEach(f => f.Dispose());
        }
    }

    [Theory]
    [InlineData(14, true)]
    [InlineData(75, false)]
    [InlineData(255, false)]
    public void LevelsAreReadAsTheNextLevelUp(int levelIdc, bool decodes)
    {
        RequireDecoder();
        byte[] stream = File.ReadAllBytes(Path.Combine(Corpus, "cabac-slices.h264"));
        int sps = stream.AsSpan().IndexOf((ReadOnlySpan<byte>)[0, 0, 1, 0x67]);
        stream[sps + 6] = (byte)levelIdc;
        if (!decodes)
        {
            // Beyond level 6.2: no GPU decodes it, and the error says so in terms the fallback to software understands.
            Assert.Throws<UnsupportedFormatException>(() => Decode(stream, HardwareVideoCodecs.H264));
            return;
        }

        List<YuvVideoFrame> frames = Decode(stream, HardwareVideoCodecs.H264);
        Assert.NotEmpty(frames);
        frames.ForEach(f => f.Dispose());
    }

    [Fact]
    public void ResetRestartsAtTheNextKeyframe()
    {
        RequireDecoder();
        byte[] stream = File.ReadAllBytes(Path.Combine(Corpus, "cabac-p.h264"));
        using MemoryStream input = new(stream);
        using IDemuxer demuxer = H264Format.Instance.OpenDemuxer(input, MediaContext.Default, leaveOpen: true);
        List<EncodedPacket> packets = [];
        while (demuxer.ReadPacket() is { } packet)
        {
            packets.Add(packet);
        }

        using IVideoDecoder decoder = HardwareVideoCodecs.H264.CreateDecoder(demuxer.Info.VideoTrack!, null, MediaContext.Default);
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
    public void CanDecodeOnlyEightBitFourTwoZero()
    {
        RequireDecoder();
        VideoTrackInfo track = new() { Number = 1, Kind = TrackKind.Video, CodecId = CodecIds.Avc, TimeBase = new Rational(1, 25), Width = 64, Height = 64 };
        Assert.True(HardwareVideoCodecs.H264.CanDecode(track));
        Assert.False(HardwareVideoCodecs.H264.CanDecode(track with { BitDepth = 10 }));
        Assert.False(HardwareVideoCodecs.H264.CanDecode(track with { ChromaFormat = ChromaFormat.Yuv422 }));
        Assert.False(HardwareVideoCodecs.H264.CanDecode(track with { CodecId = CodecIds.Hevc }));
    }

    internal static void RequireDecoder() =>
        Assert.SkipUnless(HardwareVideoCodecs.IsAvailable(CodecIds.Avc, HardwareDirection.Decode), "No GPU decodes H.264: " + HardwareVideoCodecs.UnavailableReason);

    internal static List<YuvVideoFrame> Decode(byte[] stream, VideoCodec codec)
    {
        using MemoryStream input = new(stream);
        using IDemuxer demuxer = H264Format.Instance.OpenDemuxer(input, MediaContext.Default, leaveOpen: true);
        using IVideoDecoder decoder = codec.CreateDecoder(demuxer.Info.VideoTrack!, null, MediaContext.Default.WithH264());
        List<EncodedPacket> packets = [];
        while (demuxer.ReadPacket() is { } packet)
        {
            packets.Add(packet);
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

    private static void AssertSame(YuvPlane<byte> expected, YuvPlane<byte> actual, string what)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        for (int y = 0; y < expected.Height; y++)
        {
            ReadOnlySpan<byte> a = expected.Row(y), b = actual.Row(y);
            if (!a.SequenceEqual(b))
            {
                int x = 0;
                while (a[x] == b[x])
                {
                    x++;
                }

                Assert.Fail($"{what} differs first at ({x}, {y}): {a[x]} expected, {b[x]} decoded.");
            }
        }
    }
}
