using System.Security.Cryptography;

using Egami.Pixels;
using Egami.Video.Codecs;
using Egami.Video.Containers;

namespace Egami.Video.Tests.Codecs;

public class Vp8Tests
{
    public static TheoryData<string, string> ClipFiles
    {
        get
        {
            TheoryData<string, string> data = [];
            foreach (string clip in Corpus.Vp8Clips())
            {
                foreach (string extension in (string[])[".ivf", ".webm", ".mkv"])
                {
                    if (File.Exists(Corpus.Vp8PathOf(clip + extension)))
                    {
                        data.Add(clip, extension);
                    }
                }
            }

            return data;
        }
    }

    public static TheoryData<string> AlphaClips => [.. Corpus.Vp8Clips(alpha: true)];

    [Theory]
    [MemberData(nameof(ClipFiles))]
    public void FramesEqualLibvpx(string clip, string extension)
    {
        string path = Corpus.Vp8PathOf(clip + extension);
        Vp8Reference reference = Corpus.ReadVp8Reference(Corpus.Vp8PathOf(clip + ".ivf"));
        using VideoReader reader = VideoReader.Open(path, new VideoReaderOptions { Errors = DecodeErrorMode.Throw });
        Assert.Equal(CodecIds.Vp8, reader.Track.CodecId);
        Assert.Equal(reference.Md5, Md5s(reader));

        // The design's cross-check: FFmpeg's own VP8 decoder gave libvpx's frames too (tools/video/make-vp8-corpus.py).
        Assert.True(reference.FfmpegAgrees);
    }

    [Theory]
    [MemberData(nameof(ClipFiles))]
    [InlineData("alpha-ramp", ".webm")]
    [InlineData("alpha-odd-75x45", ".webm")]
    public void PacketsMatchFfprobe(string clip, string extension)
    {
        string path = Corpus.Vp8PathOf(clip + extension);
        using IDemuxer demuxer = MediaFile.OpenDemuxer(path);
        Corpus.AssertMatchesProbe(Corpus.ReadProbe(path), Corpus.ReadAll(demuxer));
    }

    [Fact]
    public void TestVectorsEqualLibvpx()
    {
        IReadOnlyList<string> vectors = Corpus.Vp8Vectors;
        Assert.SkipWhen(vectors.Count == 0, "The VP8 test vectors are not fetched (python tools/video/make-vp8-corpus.py --vectors).");
        foreach (string vector in vectors)
        {
            Vp8Reference reference = Corpus.ReadVp8Reference(vector);
            using VideoReader reader = VideoReader.Open(vector, new VideoReaderOptions { Errors = DecodeErrorMode.Throw });
            List<string> ours = Md5s(reader);
            Assert.True(reference.Md5.SequenceEqual(ours), $"{Path.GetFileName(vector)}: {ours.Count} frames, {reference.Md5.Count} expected, first difference at {FirstDifference(reference.Md5, ours)}");
            Assert.True(reference.FfmpegAgrees || reference.Errors.Count > 0 || Path.GetFileName(vector) is "vp80-03-segmentation-1425.ivf" or "vp80-03-segmentation-1436.ivf",
                $"{Path.GetFileName(vector)}: FFmpeg's VP8 decoder disagrees with libvpx");
        }
    }

    [Fact]
    public void TestVectorPacketsMatchFfprobe()
    {
        IReadOnlyList<string> vectors = Corpus.Vp8Vectors;
        Assert.SkipWhen(vectors.Count == 0, "The VP8 test vectors are not fetched (python tools/video/make-vp8-corpus.py --vectors).");
        foreach (string vector in vectors)
        {
            using IDemuxer demuxer = MediaFile.OpenDemuxer(vector);
            Probe probe = Corpus.ReadProbe(vector);

            // Four vectors name FourCC I420; FFmpeg then reads raw video, every packet a keyframe, where we read VP8.
            Corpus.AssertMatchesProbe(probe, Corpus.ReadAll(demuxer), checkKeyframes: probe.Streams[0].Codec == "vp8");
        }
    }

    [Fact]
    public void HiddenFramesGiveNoFrame()
    {
        Vp8Reference reference = Corpus.ReadVp8Reference(Corpus.Vp8PathOf("v0-altref.ivf"));
        using IDemuxer demuxer = MediaFile.OpenDemuxer(Corpus.Vp8PathOf("v0-altref.webm"));
        using IVideoDecoder decoder = VideoCodecs.CreateDecoder(demuxer.Info.VideoTrack!);
        List<int> shown = [];
        for (int index = 0; demuxer.ReadPacket() is { } packet; index++)
        {
            using (packet)
            {
                decoder.Send(packet);
            }

            while (decoder.TryReceive(out YuvVideoFrame? frame))
            {
                using (frame)
                {
                    Assert.Equal(ChromaFormat.Yuv420, frame.Raster.ChromaFormat);
                    Assert.Equal(new Egami.Colors.ColorCodePoints(2, 2, 6, false), frame.Color);
                    shown.Add(index);
                }
            }
        }

        Assert.Equal(reference.Packets, reference.Shown.Count + 2);
        Assert.Equal(reference.Shown, shown);
    }

    [Theory]
    [MemberData(nameof(AlphaClips))]
    public void AlphaPlanesEqualLibvpx(string clip)
    {
        Vp8Reference reference = Corpus.ReadVp8Reference(Corpus.Vp8PathOf(clip + ".webm"));
        using VideoReader reader = VideoReader.Open(Corpus.Vp8PathOf(clip + ".webm"), new VideoReaderOptions { Errors = DecodeErrorMode.Throw });
        Assert.True(reader.Track.HasAlpha);
        List<string[]> ours = [];
        foreach (YuvVideoFrame frame in reader.ReadFrames())
        {
            using (frame)
            {
                YuvRaster<byte> raster = Assert.IsType<YuvRaster<byte>>(frame.Raster);
                ours.Add([PlaneMd5(raster.Y), PlaneMd5(raster.U!), PlaneMd5(raster.V!), PlaneMd5(raster.A!)]);
            }
        }

        Assert.Equal(reference.PlaneMd5.Count, ours.Count);
        for (int i = 0; i < ours.Count; i++)
        {
            Assert.True(reference.PlaneMd5[i].SequenceEqual(ours[i]), $"frame {i}: y u v a {string.Join(' ', ours[i])}, libvpx {string.Join(' ', reference.PlaneMd5[i])}");
        }
    }

    [Fact]
    public void AlphaCanBeSkipped()
    {
        VideoReaderOptions options = new() { Decoder = new VideoDecoderOptions { DecodeAlpha = false } };
        using VideoReader reader = VideoReader.Open(Corpus.Vp8PathOf("alpha-ramp.webm"), options);
        Vp8Reference reference = Corpus.ReadVp8Reference(Corpus.Vp8PathOf("alpha-ramp.webm"));
        int count = 0;
        foreach (YuvVideoFrame frame in reader.ReadFrames())
        {
            using (frame)
            {
                Assert.False(frame.Raster.HasAlpha);
                Assert.Equal(reference.PlaneMd5[count][0], PlaneMd5(((YuvRaster<byte>)frame.Raster).Y));
                count++;
            }
        }

        Assert.Equal(reference.PlaneMd5.Count, count);
    }

    [Theory]
    [InlineData("alpha-ramp", "webm")]
    [InlineData("alpha-odd-75x45", "mkv")]
    [InlineData("v0-altref", "webm")]
    [InlineData("v0-altref", "mkv")]
    public void RemuxKeepsEveryPacketAndAddition(string clip, string format)
    {
        string path = Corpus.Vp8PathOf(clip + ".webm");
        using IDemuxer source = MediaFile.OpenDemuxer(path);
        List<(ReadPacket Packet, string Additions)> original = ReadWithAdditions(source);
        byte[] written = Containers.AviWriterTests.Remux(path, format);
        using IDemuxer reread = MediaFile.OpenDemuxer(new MemoryStream(written));
        Assert.Equal(source.Info.VideoTrack!.HasAlpha, reread.Info.VideoTrack!.HasAlpha);
        Assert.Equal(original, ReadWithAdditions(reread));

        // And the remuxed file decodes to the same frames.
        using VideoReader a = VideoReader.Open(path);
        using VideoReader b = VideoReader.Open(new MemoryStream(written));
        Assert.Equal(AllPlaneMd5s(a), AllPlaneMd5s(b));
    }

    [Fact]
    public void WebMToIvfRemuxDecodesTheSame()
    {
        string path = Corpus.Vp8PathOf("v0-odd-97x61.webm");
        byte[] ivf = Containers.AviWriterTests.Remux(path, "ivf");
        using VideoReader reader = VideoReader.Open(new MemoryStream(ivf));
        Assert.Equal("IVF", reader.Info.FormatName);
        Assert.Equal(Corpus.ReadVp8Reference(Corpus.Vp8PathOf("v0-odd-97x61.ivf")).Md5, Md5s(reader));
    }

    [Fact]
    public void KeyframeFlagMismatchIsReportedAndTheFrameWins()
    {
        List<EgamiDiagnostic> reported = [];
        MediaContext context = MediaContext.Default with { Egami = EgamiContext.Default with { Diagnostics = reported.Add } };
        using IDemuxer demuxer = MediaFile.OpenDemuxer(Corpus.Vp8PathOf("v0-keyframes.webm"));
        using IVideoDecoder decoder = VideoCodecs.CreateDecoder(demuxer.Info.VideoTrack!, context: context);
        List<string> md5 = [];
        for (int index = 0; demuxer.ReadPacket() is { } packet; index++)
        {
            using (packet)
            {
                if (index is 3 or 7)
                {
                    // Packet 3 is an inter frame marked key, packet 7 a keyframe marked inter.
                    packet.IsKeyframe = !packet.IsKeyframe;
                }

                decoder.Send(packet);
            }

            while (decoder.TryReceive(out YuvVideoFrame? frame))
            {
                using (frame)
                {
                    md5.Add(Md5((YuvRaster<byte>)frame.Raster));
                }
            }
        }

        decoder.Dispose();
        Assert.Equal(Corpus.ReadVp8Reference(Corpus.Vp8PathOf("v0-keyframes.ivf")).Md5, md5);
        Assert.Equal([3, 7], reported.Where(d => d.Id == "Vp8.KeyframeFlagMismatch").Select(d => d.Frame));
    }

    internal static List<string> Md5s(VideoReader reader)
    {
        List<string> md5 = [];
        foreach (YuvVideoFrame frame in reader.ReadFrames())
        {
            using (frame)
            {
                md5.Add(Md5(Assert.IsType<YuvRaster<byte>>(frame.Raster)));
            }
        }

        return md5;
    }

    /// <summary>The MD5 of a frame as vpxdec writes it: packed I420 at the display size.</summary>
    internal static string Md5(YuvRaster<byte> raster)
    {
        using IncrementalHash md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        Append(md5, raster.Y);
        Append(md5, raster.U!);
        Append(md5, raster.V!);
        return Convert.ToHexStringLower(md5.GetHashAndReset());
    }

    internal static string PlaneMd5(YuvPlane<byte> plane)
    {
        using IncrementalHash md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        Append(md5, plane);
        return Convert.ToHexStringLower(md5.GetHashAndReset());
    }

    private static void Append(IncrementalHash hash, YuvPlane<byte> plane)
    {
        for (int y = 0; y < plane.Height; y++)
        {
            hash.AppendData(plane.Row(y));
        }
    }

    private static List<string> AllPlaneMd5s(VideoReader reader)
    {
        List<string> md5 = [];
        foreach (YuvVideoFrame frame in reader.ReadFrames())
        {
            using (frame)
            {
                YuvRaster<byte> raster = (YuvRaster<byte>)frame.Raster;
                md5.Add(Md5(raster) + (raster.A is { } a ? PlaneMd5(a) : ""));
            }
        }

        return md5;
    }

    private static List<(ReadPacket, string)> ReadWithAdditions(IDemuxer demuxer)
    {
        List<(ReadPacket, string)> packets = [];
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                string additions = string.Join(",", packet.Additions.Select(a => $"{a.Id}:{Convert.ToHexString(SHA256.HashData(a.Data.Span))}"));
                packets.Add((new ReadPacket(0, packet.Timestamp, packet.Data.Length, packet.IsKeyframe, Convert.ToHexString(SHA256.HashData(packet.Data.Span)).GetHashCode(StringComparison.Ordinal)), additions));
            }
        }

        return packets;
    }

    private static int FirstDifference(IReadOnlyList<string> a, List<string> b)
    {
        int i = 0;
        while (i < a.Count && i < b.Count && a[i] == b[i])
        {
            i++;
        }

        return i;
    }
}
