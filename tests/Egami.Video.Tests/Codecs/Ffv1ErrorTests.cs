using Egami.Pixels;
using Egami.Video.Codecs;
using Egami.Video.Containers;

namespace Egami.Video.Tests.Codecs;

/// <summary>Broken FFV1 data: what Throw, Skip and Conceal do, and the diagnostic each case reports (V6 task 7).</summary>
public class Ffv1ErrorTests
{
    [Fact]
    public void ADamagedSliceThrowsInThrowMode()
    {
        byte[] file = Build("v3-420", (packets, _) => Damage(packets[1]));
        using VideoReader reader = VideoReader.Open(new MemoryStream(file), new VideoReaderOptions { Errors = DecodeErrorMode.Throw });
        Assert.Throws<CorruptDataException>(() => reader.ReadFrames().Select(f => { f.Dispose(); return 0; }).Count());
    }

    [Fact]
    public void SkipDropsAFrameWithADamagedSliceAndReportsSliceCrc()
    {
        byte[] file = Build("v3-420", (packets, _) => Damage(packets[1]));
        (List<string> frames, List<EgamiDiagnostic> reported) = Read(file, "v3-420", DecodeErrorMode.Skip);
        List<string> reference = Ffv1Tests.ReadReference("v3-420").Md5;
        Assert.Equal([reference[0], reference[2]], frames);
        Assert.Equal(("Ffv1.SliceCrc", 1), Assert.Single(reported).Pipe(d => (d.Id, d.Frame)));
    }

    [Fact]
    public void ConcealFillsADamagedSliceFromTheLastGoodFrame()
    {
        byte[] file = Build("v3-420", (packets, _) => Damage(packets[1]));
        using YuvRaster clean0 = Frame(File.ReadAllBytes(Ffv1Tests.PathOf("v3-420.mkv")), 0, DecodeErrorMode.Throw);
        using YuvRaster clean1 = Frame(File.ReadAllBytes(Ffv1Tests.PathOf("v3-420.mkv")), 1, DecodeErrorMode.Throw);
        using YuvRaster concealed = Frame(file, 1, DecodeErrorMode.Conceal);
        var c0 = (YuvRaster<byte>)clean0;
        var c1 = (YuvRaster<byte>)clean1;
        var co = (YuvRaster<byte>)concealed;
        int fromLast = 0;
        for (int y = 0; y < co.Height; y++)
        {
            for (int x = 0; x < co.Width; x++)
            {
                byte v = co.Y.Row(y)[x];
                Assert.True(v == c1.Y.Row(y)[x] || v == c0.Y.Row(y)[x], $"({x}, {y})");
                fromLast += v != c1.Y.Row(y)[x] ? 1 : 0;
            }
        }

        // One of the four slices (a quarter of the frame) came from frame 0.
        Assert.InRange(fromLast, 1, co.Width * co.Height / 4);
    }

    [Fact]
    public void AStreamStartingOnANonKeyframeReportsMissingReference()
    {
        // v3-gop: 7 frames, keyframes 0 and 4.
        byte[] file = Build("v3-gop", (packets, _) => packets.RemoveRange(0, 2));
        (List<string> frames, List<EgamiDiagnostic> reported) = Read(file, "v3-gop", DecodeErrorMode.Skip);
        Assert.Equal(Ffv1Tests.ReadReference("v3-gop").Md5[4..], frames);
        Assert.Equal(("Ffv1.MissingReference", 0), Assert.Single(reported).Pipe(d => (d.Id, d.Frame)));

        using VideoReader strict = VideoReader.Open(new MemoryStream(file), new VideoReaderOptions { Errors = DecodeErrorMode.Throw });
        Assert.Throws<CorruptDataException>(() => strict.ReadFrame());
    }

    [Fact]
    public void ADamagedConfigurationRecordIsReported()
    {
        byte[] file = Build("v3-420", (_, track) => track[^1] ^= 0x40);
        (List<string> frames, List<EgamiDiagnostic> reported) = Read(file, "v3-420", DecodeErrorMode.Skip);
        Assert.Equal(Ffv1Tests.ReadReference("v3-420").Md5, frames);
        Assert.Equal("Ffv1.ConfigCrc", Assert.Single(reported).Id);

        using VideoReader strict = VideoReader.Open(new MemoryStream(file), new VideoReaderOptions { Errors = DecodeErrorMode.Throw });
        Assert.Throws<CorruptDataException>(() => strict.ReadFrame());
    }

    [Fact]
    public void ACutFrameIsReportedInSkipMode()
    {
        byte[] file = Build("v3-420", (packets, _) => packets[2] = packets[2][..(packets[2].Length / 3)]);
        (List<string> frames, List<EgamiDiagnostic> reported) = Read(file, "v3-420", DecodeErrorMode.Skip);
        Assert.Equal(Ffv1Tests.ReadReference("v3-420").Md5[..2], frames);
        Assert.Contains(reported, d => d.Frame == 2 && d.Id is "Ffv1.CorruptFrame" or "Ffv1.SliceCrc");
    }

    private static void Damage(byte[] packet) => packet[packet.Length / 2] ^= 0x5A;

    private static YuvRaster Frame(byte[] file, int index, DecodeErrorMode mode)
    {
        using VideoReader reader = VideoReader.Open(new MemoryStream(file), new VideoReaderOptions { Errors = mode });
        YuvRaster? kept = null;
        int i = 0;
        foreach (YuvVideoFrame frame in reader.ReadFrames())
        {
            if (i++ == index)
            {
                kept = frame.Raster;
                continue;
            }

            frame.Dispose();
        }

        return kept!;
    }

    /// <summary>Rewrites a corpus clip as Matroska after <paramref name="damage"/> changed its packets or its CodecPrivate.</summary>
    private static byte[] Build(string clip, Action<List<byte[]>, byte[]> damage)
    {
        List<(byte[] Data, MediaTime Time, bool Key)> packets = [];
        VideoTrackInfo track;
        using (IDemuxer demuxer = MediaFile.OpenDemuxer(Ffv1Tests.PathOf(clip + ".mkv")))
        {
            track = demuxer.Info.VideoTrack!;
            while (demuxer.ReadPacket() is { } packet)
            {
                using (packet)
                {
                    packets.Add((packet.Data.ToArray(), packet.Timestamp, packet.IsKeyframe));
                }
            }
        }

        List<byte[]> data = [.. packets.Select(p => p.Data)];
        byte[] codecPrivate = track.CodecPrivate.ToArray();
        int before = data.Count;
        damage(data, codecPrivate);
        int dropped = before - data.Count;
        track = track with { CodecPrivate = codecPrivate };
        MemoryStream output = new();
        using (IMuxer muxer = MediaFile.CreateMuxer(output, "mkv", [track], leaveOpen: true))
        {
            for (int i = 0; i < data.Count; i++)
            {
                (_, MediaTime time, bool key) = packets[i + dropped];
                using EncodedPacket packet = new(track.Number, data[i], time) { IsKeyframe = key };
                muxer.WritePacket(packet);
            }
        }

        return output.ToArray();
    }

    private static (List<string>, List<EgamiDiagnostic>) Read(byte[] file, string clip, DecodeErrorMode mode)
    {
        int depth = Ffv1Tests.ReadReference(clip).Depth;
        List<EgamiDiagnostic> reported = [];
        MediaContext context = MediaContext.Default with { Egami = EgamiContext.Default with { Diagnostics = reported.Add } };
        List<string> frames = [];
        using (VideoReader reader = VideoReader.Open(new MemoryStream(file), new VideoReaderOptions { Errors = mode }, context))
        {
            foreach (YuvVideoFrame frame in reader.ReadFrames())
            {
                using (frame)
                {
                    frames.Add(Ffv1Tests.Md5(frame.Raster, depth));
                }
            }
        }

        return (frames, reported);
    }
}
