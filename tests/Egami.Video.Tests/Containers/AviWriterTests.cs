using Egami.Video.Containers;
using Egami.Video.Containers.Avi;

namespace Egami.Video.Tests.Containers;

public class AviWriterTests
{
    public static TheoryData<string> AviClips => [.. Corpus.Clips(".avi")];

    [Theory]
    [MemberData(nameof(AviClips))]
    public void RoundTripKeepsPacketsByteIdentical(string clip)
    {
        using IDemuxer source = MediaFile.OpenDemuxer(Corpus.PathOf(clip));
        List<ReadPacket> original = Corpus.ReadAll(source);
        byte[] written = Remux(Corpus.PathOf(clip), "avi");
        using IDemuxer reread = MediaFile.OpenDemuxer(new MemoryStream(written));
        Assert.Equal(source.Tracks.Count, reread.Tracks.Count);
        for (int i = 0; i < source.Tracks.Count; i++)
        {
            Assert.Equal(source.Tracks[i].CodecId, reread.Tracks[i].CodecId);
            Assert.Equal(source.Tracks[i].TimeBase, reread.Tracks[i].TimeBase);
        }

        Assert.Equal(original, Corpus.ReadAll(reread));
    }

    [Fact]
    public void SmallSegmentsGiveOpenDmlRiffLists()
    {
        using IDemuxer source = MediaFile.OpenDemuxer(Corpus.PathOf("avi-mjpeg-420-pcm.avi"));
        List<ReadPacket> original = Corpus.ReadAll(source);
        source.Seek(MediaTime.Zero);
        MemoryStream output = new();
        using (AviMuxer muxer = new(output, source.Tracks, leaveOpen: true) { SegmentLimit = 60_000 })
        {
            while (source.ReadPacket() is { } packet)
            {
                using (packet)
                {
                    muxer.WritePacket(packet);
                }
            }
        }

        byte[] file = output.ToArray();
        Assert.True(CountOf(file, "AVIX"u8) >= 4);
        using IDemuxer reread = MediaFile.OpenDemuxer(new MemoryStream(file));
        Assert.Equal(original, Corpus.ReadAll(reread));
        reread.Seek(MediaTime.FromSeconds(1.5));
        Assert.Equal(original.Last(p => p.Timestamp <= MediaTime.FromSeconds(1.5) && p.Track == 0), Corpus.ReadAll(reread).First(p => p.Track == 0));
    }

    [Fact]
    public void GapsInVideoTimestampsBecomeDropFrames()
    {
        VideoTrackInfo track = new() { Number = 7, Kind = TrackKind.Video, CodecId = CodecIds.MotionJpeg, TimeBase = new Rational(1, 25), Width = 16, Height = 16, FrameRate = new Rational(25, 1) };
        MemoryStream output = new();
        using (IMuxer muxer = AviFormat.Instance.CreateMuxer(output, [track], MediaContext.Default, leaveOpen: true))
        {
            foreach (int frame in (int[])[0, 1, 5, 6])
            {
                using EncodedPacket packet = new(7, new byte[] { 0xFF, 0xD8, (byte)frame, 0xFF, 0xD9 }, new MediaTime(frame, new Rational(1, 25))) { IsKeyframe = true };
                muxer.WritePacket(packet);
            }
        }

        using IDemuxer reread = MediaFile.OpenDemuxer(new MemoryStream(output.ToArray()));
        Assert.Equal([0L, 1, 5, 6], Corpus.ReadAll(reread).Select(p => p.Timestamp.Ticks));
        Assert.Equal(new MediaTime(7, new Rational(1, 25)), reread.Tracks[0].Duration);
    }

    [Fact]
    public void NonSeekableOutputIsRefused()
    {
        VideoTrackInfo track = new() { Number = 0, Kind = TrackKind.Video, CodecId = CodecIds.MotionJpeg, TimeBase = new Rational(1, 25), Width = 16, Height = 16 };
        Assert.Throws<NotSupportedException>(() => AviFormat.Instance.CreateMuxer(new NonSeekableStream([]), [track], MediaContext.Default, leaveOpen: false));
    }

    [Fact]
    public void UnsupportedCodecsAreRefused()
    {
        VideoTrackInfo track = new() { Number = 0, Kind = TrackKind.Video, CodecId = CodecIds.Vp9, TimeBase = new Rational(1, 25), Width = 16, Height = 16 };
        Assert.Throws<NotSupportedException>(() => AviFormat.Instance.CreateMuxer(new MemoryStream(), [track], MediaContext.Default, leaveOpen: false));
    }

    internal static byte[] Remux(string path, string format)
    {
        MemoryStream output = new();
        using (IDemuxer source = MediaFile.OpenDemuxer(path))
        using (IMuxer muxer = MediaFile.CreateMuxer(output, format, source.Tracks, leaveOpen: true))
        {
            while (source.ReadPacket() is { } packet)
            {
                using (packet)
                {
                    muxer.WritePacket(packet);
                }
            }
        }

        return output.ToArray();
    }

    internal static string TempFile(string name)
    {
        string directory = Path.Combine(Path.GetTempPath(), "egami-video-tests");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, name);
    }

    private static int CountOf(byte[] data, ReadOnlySpan<byte> pattern)
    {
        int count = 0;
        int at = 0;
        while (data.AsSpan(at).IndexOf(pattern) is var i and >= 0)
        {
            count++;
            at += i + pattern.Length;
        }

        return count;
    }
}
