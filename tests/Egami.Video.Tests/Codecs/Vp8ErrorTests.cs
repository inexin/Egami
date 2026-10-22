using Egami.Pixels;
using Egami.Video.Containers;

namespace Egami.Video.Tests.Codecs;

/// <summary>Broken VP8 data (Design Q8): what Throw, Skip and Conceal do, and the diagnostics each case reports.</summary>
public class Vp8ErrorTests
{
    // v0-keyframes: 30 packets, all shown, a keyframe every 7 (0, 7, 14, 21, 28).
    private const string Clip = "v0-keyframes";

    [Fact]
    public void ACutFrameThrowsInThrowMode()
    {
        byte[] file = Build(packets => packets[3] = packets[3][..40]);
        using VideoReader reader = VideoReader.Open(new MemoryStream(file), new VideoReaderOptions { Errors = DecodeErrorMode.Throw });
        Assert.Throws<CorruptDataException>(() => reader.ReadFrames().Select(f => { f.Dispose(); return 0; }).Count());
    }

    [Fact]
    public void SkipDropsFramesUpToTheNextKeyframeAndReportsCorruptFrame()
    {
        byte[] file = Build(packets => packets[3] = packets[3][..40]);
        (List<(int Index, string Md5)> frames, List<EgamiDiagnostic> reported) = Read(file, DecodeErrorMode.Skip);
        IReadOnlyList<string> reference = Corpus.ReadVp8Reference(Corpus.Vp8PathOf(Clip + ".ivf")).Md5;
        Assert.Equal([0, 1, 2, .. Enumerable.Range(7, 23)], frames.Select(f => f.Index));
        Assert.All(frames, f => Assert.Equal(reference[f.Index], f.Md5));
        EgamiDiagnostic diagnostic = Assert.Single(reported);
        Assert.Equal(("Vp8.CorruptFrame", 3), (diagnostic.Id, diagnostic.Frame));
    }

    [Fact]
    public void ConcealReturnsEveryFrameAndTheUndamagedOnesExactly()
    {
        byte[] file = Build(packets => packets[3] = packets[3][..40]);
        (List<(int Index, string Md5)> frames, List<EgamiDiagnostic> reported) = Read(file, DecodeErrorMode.Conceal);
        IReadOnlyList<string> reference = Corpus.ReadVp8Reference(Corpus.Vp8PathOf(Clip + ".ivf")).Md5;
        Assert.Equal(30, frames.Count);

        // Frames 3 to 6 are the damaged one and those predicted from it; the rest are as libvpx decodes the whole file.
        Assert.All(frames.Where(f => f.Index is < 3 or >= 7), f => Assert.Equal(reference[f.Index], f.Md5));
        Assert.NotEqual(reference[3], frames[3].Md5);

        // The damage can carry on through the frames predicted from it (packet 3's probability updates were cut too).
        Assert.Equal(3, reported[0].Frame);
        Assert.All(reported, d => Assert.True(d.Id == "Vp8.CorruptFrame" && d.Frame is >= 3 and < 7, $"{d.Id} at {d.Frame}"));
    }

    [Fact]
    public void AStreamStartingOnAnInterFrameReportsMissingReference()
    {
        byte[] file = Build(packets => packets.RemoveRange(0, 3));
        IReadOnlyList<string> reference = Corpus.ReadVp8Reference(Corpus.Vp8PathOf(Clip + ".ivf")).Md5;

        (List<(int Index, string Md5)> skipped, List<EgamiDiagnostic> skipReports) = Read(file, DecodeErrorMode.Skip);
        Assert.Equal(Enumerable.Range(4, 23), skipped.Select(f => f.Index));
        Assert.All(skipped, f => Assert.Equal(reference[f.Index + 3], f.Md5));
        Assert.Equal(("Vp8.MissingReference", 0), Assert.Single(skipReports).Pipe(d => (d.Id, d.Frame)));

        (List<(int Index, string Md5)> concealed, List<EgamiDiagnostic> concealReports) = Read(file, DecodeErrorMode.Conceal);
        Assert.Equal(Enumerable.Range(0, 27), concealed.Select(f => f.Index));
        Assert.All(concealed.Where(f => f.Index >= 4), f => Assert.Equal(reference[f.Index + 3], f.Md5));

        // With no keyframe yet there is no picture to fill from: those frames are mid-gray at the container's size.
        using YuvRaster<byte> grayFrame = Gray(128, 96);
        string gray = Vp8Tests.Md5(grayFrame);
        Assert.All(concealed.Take(4), f => Assert.Equal(gray, f.Md5));
        Assert.Equal(Enumerable.Repeat("Vp8.MissingReference", 4), concealReports.Select(d => d.Id));

        using VideoReader strict = VideoReader.Open(new MemoryStream(file), new VideoReaderOptions { Errors = DecodeErrorMode.Throw });
        Assert.Throws<CorruptDataException>(() => strict.ReadFrame());
    }

    [Fact]
    public void ABrokenAlphaFrameIsHandledLikeABrokenFrame()
    {
        string path = Corpus.Vp8PathOf("alpha-ramp.webm");
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

        (List<(int Index, string Md5)> frames, List<EgamiDiagnostic> reported) = Read(output.ToArray(), DecodeErrorMode.Skip);
        Assert.Equal("Vp8.CorruptFrame", Assert.Single(reported).Id);
        // Timestamps are in milliseconds at 25 frames a second.
        Assert.Equal([0, 40, 80, 120, 160], frames.Select(f => f.Index));
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
        string path = Corpus.Vp8PathOf(Clip + ".ivf");
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
                using EncodedPacket packet = new(track.Number, packets[i], new MediaTime(i, track.TimeBase)) { IsKeyframe = (packets[i][0] & 1) == 0 };
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
                    frames.Add(((int)frame.Timestamp.Rescale(timeBase).Ticks, Vp8Tests.Md5((YuvRaster<byte>)frame.Raster)));
                }
            }
        }

        return (frames, reported);
    }
}

internal static class PipeExtensions
{
    public static TResult Pipe<T, TResult>(this T value, Func<T, TResult> f) => f(value);
}
