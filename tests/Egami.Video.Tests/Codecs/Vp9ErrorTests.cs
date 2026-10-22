using Egami.Pixels;
using Egami.Video.Codecs;
using Egami.Video.Containers;

namespace Egami.Video.Tests.Codecs;

/// <summary>Broken VP9 data: what Throw, Skip and Conceal do, and the diagnostic each case reports (V3 task 13).</summary>
public class Vp9ErrorTests
{
    // p0-keyframes: 24 packets, each with one shown frame, a keyframe every 5 (0, 5, 10, 15, 20); packets 1, 6, 11, 16 and 21
    // are superframes with a hidden frame.
    private const string Clip = "p0-keyframes";

    private static IReadOnlyList<string> Reference => Vp9Corpus.ReadReference(Vp9Corpus.PathOf(Clip + ".ivf")).Md5;

    [Fact]
    public void ACutFrameThrowsInThrowMode()
    {
        byte[] file = Build(packets => packets[2] = packets[2][..40]);
        using VideoReader reader = VideoReader.Open(new MemoryStream(file), new VideoReaderOptions { Errors = DecodeErrorMode.Throw });
        Assert.Throws<CorruptDataException>(() => reader.ReadFrames().Select(f => { f.Dispose(); return 0; }).Count());
    }

    [Fact]
    public void SkipResumesAtTheNextKeyframeAndReportsCorruptFrame()
    {
        byte[] file = Build(packets => packets[2] = packets[2][..40]);
        (List<(int Index, string Md5)> frames, List<EgamiDiagnostic> reported) = Read(file, DecodeErrorMode.Skip);
        Assert.Equal([0, 1, .. Enumerable.Range(5, 19)], frames.Select(f => f.Index));
        Assert.All(frames, f => Assert.Equal(Reference[f.Index], f.Md5));
        Assert.Equal(("Vp9.CorruptFrame", 2), Assert.Single(reported).Pipe(d => (d.Id, d.Frame)));
    }

    [Fact]
    public void ConcealReturnsEveryFrameRepeatingTheLastGoodOne()
    {
        byte[] file = Build(packets => packets[2] = packets[2][..40]);
        (List<(int Index, string Md5)> frames, List<EgamiDiagnostic> reported) = Read(file, DecodeErrorMode.Conceal);
        Assert.Equal(Enumerable.Range(0, 24), frames.Select(f => f.Index));
        Assert.All(frames.Where(f => f.Index is < 2 or >= 5), f => Assert.Equal(Reference[f.Index], f.Md5));
        Assert.All(frames.Where(f => f.Index is >= 2 and < 5), f => Assert.Equal(Reference[1], f.Md5));
        Assert.Equal(("Vp9.CorruptFrame", 2), Assert.Single(reported).Pipe(d => (d.Id, d.Frame)));
    }

    [Fact]
    public void AStreamStartingOnAnInterFrameReportsMissingReference()
    {
        byte[] file = Build(packets => packets.RemoveRange(0, 2));
        (List<(int Index, string Md5)> skipped, List<EgamiDiagnostic> skipReports) = Read(file, DecodeErrorMode.Skip);
        Assert.Equal(Enumerable.Range(3, 19), skipped.Select(f => f.Index));
        Assert.All(skipped, f => Assert.Equal(Reference[f.Index + 2], f.Md5));
        Assert.Equal(("Vp9.MissingReference", 0), Assert.Single(skipReports).Pipe(d => (d.Id, d.Frame)));

        // With no keyframe yet there is nothing to repeat: those frames are mid-gray at the container's size.
        (List<(int Index, string Md5)> concealed, _) = Read(file, DecodeErrorMode.Conceal);
        Assert.Equal(Enumerable.Range(0, 22), concealed.Select(f => f.Index));
        Assert.All(concealed.Where(f => f.Index >= 3), f => Assert.Equal(Reference[f.Index + 2], f.Md5));
        using YuvRaster<byte> gray = Gray(128, 96);
        Assert.All(concealed.Take(3), f => Assert.Equal(Vp9Tests.Md5(gray), f.Md5));

        using VideoReader strict = VideoReader.Open(new MemoryStream(file), new VideoReaderOptions { Errors = DecodeErrorMode.Throw });
        Assert.Throws<CorruptDataException>(() => strict.ReadFrame());
    }

    [Fact]
    public void A440StreamReportsUnsupportedSubsampling()
    {
        byte[] file = File.ReadAllBytes(Vp9Corpus.PathOf("p1-440.webm"));
        (List<(int Index, string Md5)> frames, List<EgamiDiagnostic> reported) = Read(file, DecodeErrorMode.Skip);
        Assert.Empty(frames);
        Assert.Equal("Vp9.UnsupportedSubsampling", Assert.Single(reported).Id);

        using VideoReader strict = VideoReader.Open(new MemoryStream(file), new VideoReaderOptions { Errors = DecodeErrorMode.Throw });
        Assert.Throws<UnsupportedFormatException>(() => strict.ReadFrame());
    }

    [Fact]
    public void ACodecPrivateThatDisagreesIsReportedAndTheStreamWins()
    {
        // Profile 2, 10 bit, 4:4:4 for an 8-bit 4:2:0 profile 0 stream.
        string path = Vp9Corpus.PathOf(Clip + ".webm");
        MemoryStream output = new();
        using (IDemuxer demuxer = MediaFile.OpenDemuxer(path))
        {
            VideoTrackInfo track = demuxer.Info.VideoTrack! with { CodecPrivate = new byte[] { 1, 1, 2, 3, 1, 10, 4, 1, 3 } };
            using IMuxer muxer = MediaFile.CreateMuxer(output, "webm", [track], leaveOpen: true);
            while (demuxer.ReadPacket() is { } packet)
            {
                using (packet)
                {
                    muxer.WritePacket(packet);
                }
            }
        }

        (List<(int Index, string Md5)> frames, List<EgamiDiagnostic> reported) = Read(output.ToArray(), DecodeErrorMode.Skip);
        Assert.Equal(Reference, frames.Select(f => f.Md5));
        Assert.Equal(("Vp9.CodecPrivateMismatch", 0), Assert.Single(reported).Pipe(d => (d.Id, d.Frame)));
    }

    [Fact]
    public void ABrokenAlphaFrameIsHandledLikeABrokenFrame()
    {
        string path = Vp9Corpus.PathOf("alpha-ramp.webm");
        MemoryStream output = new();
        using (IDemuxer demuxer = MediaFile.OpenDemuxer(path))
        using (IMuxer muxer = MediaFile.CreateMuxer(output, "webm", demuxer.Tracks, leaveOpen: true))
        {
            for (int index = 0; demuxer.ReadPacket() is { } packet; index++)
            {
                using (packet)
                {
                    if (index == 5)
                    {
                        packet.Additions = [new PacketAddition(1, packet.Additions[0].Data[..12])];
                    }

                    muxer.WritePacket(packet);
                }
            }
        }

        List<EgamiDiagnostic> reported = [];
        MediaContext context = MediaContext.Default with { Egami = EgamiContext.Default with { Diagnostics = reported.Add } };
        using VideoReader reader = VideoReader.Open(new MemoryStream(output.ToArray()), new VideoReaderOptions { Errors = DecodeErrorMode.Skip }, context);
        int count = reader.ReadFrames().Select(f => { f.Dispose(); return 0; }).Count();
        Assert.Equal(("Vp9.CorruptFrame", 5), Assert.Single(reported).Pipe(d => (d.Id, d.Frame)));
        Assert.True(count is >= 5 and < 20, $"{count} frames");
    }

    private static YuvRaster<byte> Gray(int width, int height)
    {
        YuvRaster<byte> raster = YuvRaster.Create(width, height, ChromaFormat.Yuv420, YuvColor.Bt601Limited);
        foreach (YuvPlane<byte> plane in (YuvPlane<byte>[])[raster.Y, raster.U!, raster.V!])
        {
            for (int y = 0; y < plane.Height; y++)
            {
                plane.Row(y).Fill(128);
            }
        }

        return raster;
    }

    private static byte[] Build(Action<List<byte[]>> damage)
    {
        string path = Vp9Corpus.PathOf(Clip + ".ivf");
        List<byte[]> packets = [];
        TrackInfo track;
        using (IDemuxer demuxer = MediaFile.OpenDemuxer(path))
        {
            track = demuxer.Tracks[0];
            while (demuxer.ReadPacket() is { } packet)
            {
                using (packet)
                {
                    packets.Add(packet.Data.ToArray());
                }
            }
        }

        damage(packets);
        MemoryStream output = new();
        using (IMuxer muxer = IvfFormat.Instance.CreateMuxer(output, [track], MediaContext.Default, leaveOpen: true))
        {
            for (int i = 0; i < packets.Count; i++)
            {
                bool key = FrameKind.IsKeyframe(CodecIds.Vp9, packets[i]) ?? false;
                using EncodedPacket packet = new(track.Number, packets[i], new MediaTime(i, track.TimeBase)) { IsKeyframe = key };
                muxer.WritePacket(packet);
            }
        }

        return output.ToArray();
    }

    /// <summary>Reads every frame with its index in the stream (from its timestamp) and MD5, and what was reported.</summary>
    private static (List<(int Index, string Md5)>, List<EgamiDiagnostic>) Read(byte[] file, DecodeErrorMode mode)
    {
        List<EgamiDiagnostic> reported = [];
        MediaContext context = MediaContext.Default with { Egami = EgamiContext.Default with { Diagnostics = reported.Add } };
        List<(int, string)> frames = [];
        using (VideoReader reader = VideoReader.Open(new MemoryStream(file), new VideoReaderOptions { Errors = mode }, context))
        {
            Rational timeBase = reader.Track.TimeBase;
            foreach (YuvVideoFrame frame in reader.ReadFrames())
            {
                using (frame)
                {
                    frames.Add(((int)frame.Timestamp.Rescale(timeBase).Ticks, Vp9Tests.Md5(frame.Raster)));
                }
            }
        }

        return (frames, reported);
    }
}
