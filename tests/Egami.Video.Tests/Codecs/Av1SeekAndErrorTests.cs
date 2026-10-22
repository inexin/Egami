using Egami.Video.Codecs;
using Egami.Video.Containers;

namespace Egami.Video.Tests.Codecs;

/// <summary>Exact seeks and broken AV1 data: what Throw, Skip and Conceal do, and the diagnostic each case reports (V4.2 task 17).</summary>
public class Av1SeekAndErrorTests
{
    // forward-keyframes: 24 temporal units, each showing one frame; shown key frames start units 0 and 12. Units 1, 7, 13 and 18
    // also hold hidden frames, and units 3, 6, 8, 14, 17, 20 and 23 show one again with show_existing_frame.
    private const string Clip = "forward-keyframes";

    private static List<string> Reference => [.. Av1Corpus.ReadReference(Clip).Select(r => r.Md5)];

    [Theory]
    [InlineData(".ivf")]
    [InlineData(".webm")]
    [InlineData(".mkv")]
    [InlineData(".obu")]
    [InlineData(".annexb.obu")]
    public void ExactSeeksMatchDecodingFromTheStart(string extension)
    {
        string path = Av1Corpus.PathOf(Clip + extension);
        List<(MediaTime Time, string Md5)> all = [];
        using (VideoReader whole = VideoReader.Open(path, new VideoReaderOptions { Errors = DecodeErrorMode.Throw }))
        {
            foreach (YuvVideoFrame frame in whole.ReadFrames())
            {
                using (frame)
                {
                    all.Add((frame.Timestamp, Av1Corpus.Md5(frame.Raster)));
                }
            }
        }

        Assert.Equal(Reference, all.Select(f => f.Md5));
        Random random = new(42);
        double end = all[^1].Time.ToTimeSpan().TotalSeconds + 0.05;
        using VideoReader reader = VideoReader.Open(path, new VideoReaderOptions { Errors = DecodeErrorMode.Throw });
        for (int i = 0; i < 100; i++)
        {
            MediaTime target = MediaTime.FromSeconds(random.NextDouble() * end);
            int expected = Math.Max(0, all.FindLastIndex(f => f.Time <= target));
            reader.Seek(target);
            using YuvVideoFrame frame = reader.ReadFrame()!;
            Assert.True(all[expected].Md5 == Av1Corpus.Md5(frame.Raster), $"seek {i} to {target}: frame {expected} differs");
            Assert.Equal(all[expected].Time, frame.Timestamp);

            // The frames after the target follow as when decoding from the start.
            if (expected + 1 < all.Count && i % 10 == 0)
            {
                using YuvVideoFrame next = reader.ReadFrame()!;
                Assert.Equal(all[expected + 1].Md5, Av1Corpus.Md5(next.Raster));
            }
        }
    }

    [Fact]
    public void KeyframeSeeksLandOnShownKeyFrames()
    {
        using VideoReader reader = VideoReader.Open(Av1Corpus.PathOf(Clip + ".ivf"));
        reader.Seek(MediaTime.FromSeconds(0.6), SeekMode.Keyframe);
        using YuvVideoFrame frame = reader.ReadFrame()!;
        Assert.Equal(Reference[12], Av1Corpus.Md5(frame.Raster));
    }

    [Fact]
    public void ACutUnitThrowsInThrowMode()
    {
        byte[] file = Build(units => units[2] = units[2][..60]);
        using VideoReader reader = VideoReader.Open(new MemoryStream(file), new VideoReaderOptions { Errors = DecodeErrorMode.Throw });
        Assert.Throws<CorruptDataException>(() => reader.ReadFrames().Select(f => { f.Dispose(); return 0; }).Count());
    }

    [Fact]
    public void SkipResumesAtTheNextShownKeyFrameAndReportsCorruptFrame()
    {
        byte[] file = Build(units => units[2] = units[2][..60]);
        (List<(int Index, string Md5)> frames, List<EgamiDiagnostic> reported) = Read(file, DecodeErrorMode.Skip);
        Assert.Equal([0, 1, .. Enumerable.Range(12, 12)], frames.Select(f => f.Index));
        Assert.All(frames, f => Assert.Equal(Reference[f.Index], f.Md5));
        Assert.Equal(("Av1.CorruptFrame", 2), Assert.Single(reported).Pipe(d => (d.Id, d.Frame)));
    }

    [Fact]
    public void ConcealRepeatsTheLastGoodFrameUntilTheNextShownKeyFrame()
    {
        byte[] file = Build(units => units[2] = units[2][..60]);
        (List<(int Index, string Md5)> frames, List<EgamiDiagnostic> reported) = Read(file, DecodeErrorMode.Conceal);
        Assert.Equal(Enumerable.Range(0, 24), frames.Select(f => f.Index));
        Assert.All(frames.Where(f => f.Index is < 2 or >= 12), f => Assert.Equal(Reference[f.Index], f.Md5));
        Assert.All(frames.Where(f => f.Index is >= 2 and < 12), f => Assert.Equal(Reference[1], f.Md5));
        Assert.Equal(("Av1.CorruptFrame", 2), Assert.Single(reported).Pipe(d => (d.Id, d.Frame)));
    }

    [Fact]
    public void BrokenObusReportCorruptFrame()
    {
        // The forbidden bit set in a unit's first OBU header: the unit cannot even be split.
        byte[] file = Build(units => units[5][0] |= 0x80);
        (List<(int Index, string Md5)> frames, List<EgamiDiagnostic> reported) = Read(file, DecodeErrorMode.Skip);
        Assert.Equal([.. Enumerable.Range(0, 5), .. Enumerable.Range(12, 12)], frames.Select(f => f.Index));
        Assert.Equal(("Av1.CorruptFrame", 5), Assert.Single(reported).Pipe(d => (d.Id, d.Frame)));
    }

    [Theory]
    [InlineData(".ivf")]
    [InlineData(".webm")]
    public void AStreamStartingOnAnInterFrameReportsMissingReference(string extension)
    {
        // Units 0 and 1 dropped: the stream starts on an inter frame, without the key frame (or, in IVF, the sequence header).
        byte[] file = extension == ".ivf" ? Build(units => units.RemoveRange(0, 2)) : BuildWebM(2);
        (List<(int Index, string Md5)> skipped, List<EgamiDiagnostic> reports) = Read(file, DecodeErrorMode.Skip);
        Assert.Equal(Enumerable.Range(12, 12), skipped.Select(f => f.Index + (extension == ".ivf" ? 2 : 0)));
        Assert.All(skipped, f => Assert.Equal(Reference[f.Index + (extension == ".ivf" ? 2 : 0)], f.Md5));
        Assert.Equal(("Av1.MissingReference", 0), Assert.Single(reports).Pipe(d => (d.Id, d.Frame)));

        // With no key frame yet there is nothing to repeat: those frames are mid-gray at the container's size.
        (List<(int Index, string Md5)> concealed, _) = Read(file, DecodeErrorMode.Conceal);
        Assert.Equal(22, concealed.Count);

        using VideoReader strict = VideoReader.Open(new MemoryStream(file), new VideoReaderOptions { Errors = DecodeErrorMode.Throw });
        Assert.Throws<CorruptDataException>(() => strict.ReadFrame());
    }

    [Fact]
    public void AfterAMissingReferenceABrokenUnitIsACorruptFrame()
    {
        byte[] file = Build(units =>
        {
            units.RemoveRange(0, 2);
            units[14] = units[14][..40];
        });
        (_, List<EgamiDiagnostic> reported) = Read(file, DecodeErrorMode.Skip);
        Assert.Equal([("Av1.MissingReference", 0), ("Av1.CorruptFrame", 14)], reported.Select(d => (d.Id, d.Frame ?? -1)));
    }

    /// <summary>The clip's IVF with its units changed, the keyframe flags of IVF coming from the units themselves.</summary>
    private static byte[] Build(Action<List<byte[]>> damage)
    {
        List<byte[]> units = [];
        VideoTrackInfo track;
        using (IDemuxer demuxer = MediaFile.OpenDemuxer(Av1Corpus.PathOf(Clip + ".ivf")))
        {
            track = (VideoTrackInfo)demuxer.Tracks[0];
            while (demuxer.ReadPacket() is { } packet)
            {
                using (packet)
                {
                    units.Add(packet.Data.ToArray());
                }
            }
        }

        damage(units);
        MemoryStream output = new();
        using (IMuxer muxer = IvfFormat.Instance.CreateMuxer(output, [track], MediaContext.Default, leaveOpen: true))
        {
            for (int i = 0; i < units.Count; i++)
            {
                using EncodedPacket packet = new(track.Number, units[i], new MediaTime(i, track.TimeBase));
                muxer.WritePacket(packet);
            }
        }

        return output.ToArray();
    }

    /// <summary>The clip's WebM without its first units (the track keeps its av1C, so the sequence header is known).</summary>
    private static byte[] BuildWebM(int drop)
    {
        MemoryStream output = new();
        using IDemuxer demuxer = MediaFile.OpenDemuxer(Av1Corpus.PathOf(Clip + ".webm"));
        using (IMuxer muxer = MediaFile.CreateMuxer(output, "webm", demuxer.Tracks, leaveOpen: true))
        {
            for (int i = 0; demuxer.ReadPacket() is { } packet; i++)
            {
                using (packet)
                {
                    if (i >= drop)
                    {
                        muxer.WritePacket(packet);
                    }
                }
            }
        }

        return output.ToArray();
    }

    /// <summary>Reads every frame with its unit index (from its timestamp) and MD5, and what was reported.</summary>
    private static (List<(int Index, string Md5)>, List<EgamiDiagnostic>) Read(byte[] file, DecodeErrorMode mode)
    {
        List<EgamiDiagnostic> reported = [];
        MediaContext context = MediaContext.Default with { Egami = EgamiContext.Default with { Diagnostics = reported.Add } };
        List<(int, string)> frames = [];
        using (VideoReader reader = VideoReader.Open(new MemoryStream(file), new VideoReaderOptions { Errors = mode }, context))
        {
            foreach (YuvVideoFrame frame in reader.ReadFrames())
            {
                using (frame)
                {
                    frames.Add(((int)Math.Round(frame.Timestamp.ToTimeSpan().TotalSeconds * 30), Av1Corpus.Md5(frame.Raster)));
                }
            }
        }

        return (frames, reported);
    }
}
