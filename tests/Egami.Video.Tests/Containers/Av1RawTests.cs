using Egami.Video.Containers;
using Egami.Video.Tests.Codecs;

namespace Egami.Video.Tests.Containers;

/// <summary>
/// Raw AV1 streams (V4.2 task 14): low-overhead OBU files as FFmpeg writes them, and Annex B files as
/// tools/video/make-av1-corpus.py writes them, both checked there to decode in aomdec and dav1d to libaom's frames.
/// </summary>
public class Av1RawTests
{
    private static readonly string[] Raw = ["forward-keyframes", "monochrome", "tools-10bit", "tools-8bit"];

    public static TheoryData<string, string> RawFiles
    {
        get
        {
            TheoryData<string, string> data = [];
            foreach (string clip in Raw)
            {
                data.Add(clip, ".obu");
                data.Add(clip, ".annexb.obu");
            }

            return data;
        }
    }

    /// <summary>ffprobe times raw AV1 at 25 frames a second.</summary>
    private static MediaContext At25 => MediaContext.Default
        .WithContainer(new Av1ObuFormat { FrameRate = new Rational(25, 1) })
        .WithContainer(new Av1AnnexBFormat { FrameRate = new Rational(25, 1) });

    [Theory]
    [MemberData(nameof(RawFiles))]
    public void PacketsMatchFfprobe(string clip, string extension)
    {
        string path = Av1Corpus.PathOf(clip + extension);
        using IDemuxer demuxer = MediaFile.OpenDemuxer(path, At25);
        Assert.Equal(extension == ".obu" ? "AV1 OBU" : "AV1 Annex B", demuxer.Info.FormatName);
        Corpus.AssertMatchesProbe(Corpus.ReadProbe(path), Corpus.ReadAll(demuxer));
    }

    [Theory]
    [MemberData(nameof(RawFiles))]
    public void FramesEqualLibaom(string clip, string extension)
    {
        using VideoReader reader = VideoReader.Open(Av1Corpus.PathOf(clip + extension), new VideoReaderOptions { Errors = DecodeErrorMode.Throw });
        Assert.Equal(Av1Corpus.ReadReference(clip).Select(r => r.Md5), Av1Corpus.Md5s(reader));
        Assert.Equal(new Rational(30, 1), reader.Track.FrameRate);
    }

    [Theory]
    [MemberData(nameof(RawFiles))]
    public void ReadingAndWritingBackGivesTheSameBytes(string clip, string extension)
    {
        byte[] file = File.ReadAllBytes(Av1Corpus.PathOf(clip + extension));
        ContainerFormat format = extension == ".obu" ? Av1ObuFormat.Instance : Av1AnnexBFormat.Instance;
        Assert.Equal(file, Rewrite(Av1Corpus.PathOf(clip + extension), format));
    }

    [Theory]
    [MemberData(nameof(RawFiles))]
    public void TheIvfClipWritesTheSameFile(string clip, string extension)
    {
        // The .obu files are FFmpeg's, the Annex B ones make-av1-corpus.py's: what aomdec and dav1d were checked to read.
        ContainerFormat format = extension == ".obu" ? Av1ObuFormat.Instance : Av1AnnexBFormat.Instance;
        Assert.Equal(File.ReadAllBytes(Av1Corpus.PathOf(clip + extension)), Rewrite(Av1Corpus.PathOf(clip + ".ivf"), format));
    }

    [Theory]
    [MemberData(nameof(RawFiles))]
    public void PacketsEqualTheIvfClips(string clip, string extension)
    {
        using IDemuxer ivf = MediaFile.OpenDemuxer(Av1Corpus.PathOf(clip + ".ivf"));
        using IDemuxer raw = MediaFile.OpenDemuxer(Av1Corpus.PathOf(clip + extension));
        Assert.Equal(ivf.Tracks[0].CodecPrivate.ToArray(), raw.Tracks[0].CodecPrivate.ToArray());
        VideoTrackInfo a = (VideoTrackInfo)ivf.Tracks[0], b = (VideoTrackInfo)raw.Tracks[0];
        Assert.Equal((a.Width, a.Height, a.BitDepth, a.ChromaFormat), (b.Width, b.Height, b.BitDepth, b.ChromaFormat));
        Assert.Equal(Corpus.ReadAll(ivf).Select(p => (p.Size, p.Hash, p.Key)), Corpus.ReadAll(raw).Select(p => (p.Size, p.Hash, p.Key)));
    }

    [Theory]
    [MemberData(nameof(RawFiles))]
    public void NonSeekableStreamsGiveTheSamePackets(string clip, string extension)
    {
        byte[] file = File.ReadAllBytes(Av1Corpus.PathOf(clip + extension));
        using IDemuxer seekable = MediaFile.OpenDemuxer(new MemoryStream(file));
        using IDemuxer streaming = MediaFile.OpenDemuxer(new NonSeekableStream(file));
        Assert.False(streaming.CanSeek);
        Assert.Equal(Corpus.ReadAll(seekable), Corpus.ReadAll(streaming));
    }

    [Fact]
    public void AWriterAddsATemporalDelimiterToAUnitWithout()
    {
        byte[] unit;
        VideoTrackInfo track;
        using (IDemuxer ivf = MediaFile.OpenDemuxer(Av1Corpus.PathOf("tools-8bit.ivf")))
        {
            track = (VideoTrackInfo)ivf.Tracks[0];
            using EncodedPacket first = ivf.ReadPacket()!;
            unit = first.Data.ToArray();
        }

        Assert.Equal([0x12, 0x00], unit[..2]);
        foreach (ContainerFormat format in (ContainerFormat[])[Av1ObuFormat.Instance, Av1AnnexBFormat.Instance])
        {
            MemoryStream output = new();
            using (IMuxer muxer = format.CreateMuxer(output, [track], MediaContext.Default, leaveOpen: true))
            {
                using EncodedPacket packet = new(track.Number, unit.AsMemory(2), MediaTime.Zero);
                muxer.WritePacket(packet);
            }

            output.Position = 0;
            using IDemuxer back = format.OpenDemuxer(output, MediaContext.Default, leaveOpen: false);
            using EncodedPacket read = back.ReadPacket()!;
            Assert.Equal(unit, read.Data.ToArray());
            Assert.True(read.IsKeyframe);
        }
    }

    [Fact]
    public void OtherTracksAreRefused()
    {
        VideoTrackInfo vp9 = new() { Number = 1, Kind = TrackKind.Video, CodecId = CodecIds.Vp9, TimeBase = new Rational(1, 30), Width = 16, Height = 16 };
        Assert.Throws<NotSupportedException>(() => Av1ObuFormat.Instance.CreateMuxer(new MemoryStream(), [vp9], MediaContext.Default, false));
        Assert.Throws<NotSupportedException>(() => Av1AnnexBFormat.Instance.CreateMuxer(new MemoryStream(), [vp9], MediaContext.Default, false));
    }

    [Theory]
    [InlineData(new byte[] { 0x12, 0x00, 0x0A })]
    [InlineData(new byte[] { 0x16, 0x00, 0x00 })]
    public void LowOverheadFilesAreDetected(byte[] start)
    {
        Assert.True(Av1ObuFormat.Instance.IsMatch(start));
        Assert.False(Av1AnnexBFormat.Instance.IsMatch(start));
    }

    [Fact]
    public void AnnexBFilesAreDetectedByTheirFirstTemporalDelimiter()
    {
        byte[] file = File.ReadAllBytes(Av1Corpus.PathOf("tools-8bit.annexb.obu"));
        Assert.True(Av1AnnexBFormat.Instance.IsMatch(file.AsSpan(0, 16)));
        Assert.False(Av1ObuFormat.Instance.IsMatch(file.AsSpan(0, 3)));
        Assert.False(Av1AnnexBFormat.Instance.IsMatch(File.ReadAllBytes(Av1Corpus.PathOf("tools-8bit.obu")).AsSpan(0, 16)));
    }

    [Fact]
    public void ACutOffFileEndsAtItsLastWholeUnit()
    {
        foreach (string file in (string[])["tools-8bit.obu", "tools-8bit.annexb.obu"])
        {
            byte[] data = File.ReadAllBytes(Av1Corpus.PathOf(file));
            using IDemuxer demuxer = MediaFile.OpenDemuxer(new MemoryStream(data[..^1]));
            int count = Corpus.ReadAll(demuxer).Count;
            Assert.Equal(Av1Corpus.ReadReference("tools-8bit").Count - 1, count);
        }
    }

    private static byte[] Rewrite(string path, ContainerFormat format)
    {
        MemoryStream output = new();
        using IDemuxer demuxer = MediaFile.OpenDemuxer(path);
        using (IMuxer muxer = format.CreateMuxer(output, demuxer.Tracks, MediaContext.Default, leaveOpen: true))
        {
            while (demuxer.ReadPacket() is { } packet)
            {
                using (packet)
                {
                    muxer.WritePacket(packet);
                }
            }
        }

        return output.ToArray();
    }
}
