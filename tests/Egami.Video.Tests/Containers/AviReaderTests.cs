using System.Buffers.Binary;

using Egami.Video.Containers;

namespace Egami.Video.Tests.Containers;

public class AviReaderTests
{
    public static TheoryData<string> AviClips => [.. Corpus.Clips(".avi")];

    [Theory]
    [MemberData(nameof(AviClips))]
    public void PacketsMatchFfprobe(string clip)
    {
        string path = Corpus.PathOf(clip);
        Probe probe = Corpus.ReadProbe(path);
        using IDemuxer demuxer = MediaFile.OpenDemuxer(path);
        Assert.Equal("AVI", demuxer.Info.FormatName);
        Assert.Equal(probe.Streams.Count, demuxer.Tracks.Count);
        for (int i = 0; i < probe.Streams.Count; i++)
        {
            ProbeStream expected = probe.Streams[i];
            TrackInfo track = demuxer.Tracks[i];
            Assert.Equal(expected.TimeBase, track.TimeBase);
            if (track is VideoTrackInfo video)
            {
                Assert.Equal("video", expected.Type);
                Assert.Equal(clip switch
                {
                    "avi-raw-bgr24.avi" => CodecIds.VfwFourCC,
                    _ when clip.Contains("raw", StringComparison.Ordinal) || clip.Contains("v210", StringComparison.Ordinal) => CodecIds.Uncompressed,
                    _ => CodecIds.MotionJpeg,
                }, video.CodecId);
                Assert.Equal((expected.Width, expected.Height), (video.Width, video.Height));
            }
            else if (track is AudioTrackInfo audio)
            {
                Assert.Equal("audio", expected.Type);
                Assert.Equal((expected.SampleRate, expected.Channels), (audio.SampleRate, audio.Channels));
            }
        }

        Corpus.AssertMatchesProbe(probe, Corpus.ReadAll(demuxer));
    }

    [Theory]
    [MemberData(nameof(AviClips))]
    public void NonSeekableStreamsGiveTheSamePackets(string clip)
    {
        byte[] file = File.ReadAllBytes(Corpus.PathOf(clip));
        using IDemuxer seekable = MediaFile.OpenDemuxer(new MemoryStream(file));
        using IDemuxer streaming = MediaFile.OpenDemuxer(new NonSeekableStream(file));
        Assert.False(streaming.CanSeek);
        Assert.Equal(Corpus.ReadAll(seekable), Corpus.ReadAll(streaming));
    }

    [Fact]
    public void FilesWithoutAnIndexAreScannedForSeeking()
    {
        byte[] file = File.ReadAllBytes(Corpus.PathOf("avi-mjpeg-420-pcm.avi"));
        byte[] noIndex = (byte[])file.Clone();
        int idx1 = FindChunk(noIndex, "idx1");
        "JUNK"u8.CopyTo(noIndex.AsSpan(idx1));

        using IDemuxer original = MediaFile.OpenDemuxer(new MemoryStream(file));
        using IDemuxer scanned = MediaFile.OpenDemuxer(new MemoryStream(noIndex));
        Assert.Equal(Corpus.ReadAll(original), Corpus.ReadAll(scanned));

        original.Seek(MediaTime.FromSeconds(1.01));
        scanned.Seek(MediaTime.FromSeconds(1.01));
        Assert.Equal(Corpus.ReadAll(original), Corpus.ReadAll(scanned));
    }

    [Fact]
    public void AbsoluteIdx1OffsetsAreDetected()
    {
        byte[] file = File.ReadAllBytes(Corpus.PathOf("avi-mjpeg-420-pcm.avi"));
        int movi = FindChunk(file, "movi");
        int idx1 = FindChunk(file, "idx1");
        int size = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(idx1 + 4));
        for (int e = idx1 + 8; e < idx1 + 8 + size; e += 16)
        {
            Span<byte> offset = file.AsSpan(e + 8, 4);
            BinaryPrimitives.WriteInt32LittleEndian(offset, BinaryPrimitives.ReadInt32LittleEndian(offset) + movi);
        }

        Probe probe = Corpus.ReadProbe(Corpus.PathOf("avi-mjpeg-420-pcm.avi"));
        using IDemuxer demuxer = MediaFile.OpenDemuxer(new MemoryStream(file));
        demuxer.Seek(MediaTime.FromSeconds(1));
        ReadPacket first = Corpus.ReadAll(demuxer)[0];
        Assert.Equal(new MediaTime(25, new Rational(1, 25)), first.Timestamp);
        Assert.NotEmpty(probe.Packets);
    }

    [Fact]
    public void SeekLandsOnTheFrameAtOrBeforeTheTime()
    {
        // FFmpeg writes empty "drop" chunks in this 29.97 fps file, so some frame numbers have no packet.
        string path = Corpus.PathOf("avi-mjpeg-422-mp3-ntsc.avi");
        using IDemuxer reader = MediaFile.OpenDemuxer(path);
        List<MediaTime> frames = [.. Corpus.ReadAll(reader).Where(p => p.Track == 0).Select(p => p.Timestamp)];
        using IDemuxer demuxer = MediaFile.OpenDemuxer(path);
        MediaTime millisecond = MediaTime.FromSeconds(0.001);
        for (int n = 0; n < 62; n++)
        {
            MediaTime start = new MediaTime(n, new Rational(1001, 30000)).Rescale(Rational.Nanosecond);
            foreach (MediaTime time in (MediaTime[])[start, start + millisecond, start - millisecond])
            {
                MediaTime expected = frames.LastOrDefault(f => f <= time, frames[0]);
                demuxer.Seek(time);
                Assert.Equal(expected, FirstVideo(demuxer).Timestamp);
            }
        }

        demuxer.Seek(MediaTime.FromSeconds(500));
        Assert.Equal(frames[^1], FirstVideo(demuxer).Timestamp);
    }

    [Fact]
    public void SeekKeepsOtherTracksTimestampsRight()
    {
        string path = Corpus.PathOf("avi-mjpeg-420-pcm.avi");
        using IDemuxer full = MediaFile.OpenDemuxer(path);
        List<ReadPacket> all = Corpus.ReadAll(full);
        using IDemuxer demuxer = MediaFile.OpenDemuxer(path);
        demuxer.Seek(MediaTime.FromSeconds(1.2));
        List<ReadPacket> tail = Corpus.ReadAll(demuxer);
        Assert.Equal(all[^tail.Count..], tail);
    }

    [Fact]
    public void NonSeekableStreamsRefuseToSeek()
    {
        using IDemuxer demuxer = MediaFile.OpenDemuxer(new NonSeekableStream(File.ReadAllBytes(Corpus.PathOf("avi-mjpeg-444-noaudio.avi"))));
        Assert.Throws<NotSupportedException>(() => demuxer.Seek(MediaTime.Zero));
    }

    [Fact]
    public void TruncatedFilesReadUpToTheCut()
    {
        byte[] file = File.ReadAllBytes(Corpus.PathOf("avi-mjpeg-420-pcm.avi"));
        int movi = FindChunk(file, "movi");
        byte[] cut = file[..(movi + 40_000)];
        using IDemuxer demuxer = MediaFile.OpenDemuxer(new MemoryStream(cut));
        List<ReadPacket> packets = Corpus.ReadAll(demuxer);
        Assert.InRange(packets.Count, 5, 20);
    }

    [Fact]
    public void GarbageBetweenChunksIsSkipped()
    {
        byte[] file = File.ReadAllBytes(Corpus.PathOf("avi-mjpeg-444-noaudio.avi"));
        int movi = FindChunk(file, "movi");
        int first = movi + 4;
        int size = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(first + 4));
        int second = first + 8 + size + (size & 1);

        // Overwrite the second frame's header with junk: the reader skips to the third frame.
        file.AsSpan(second, 8).Fill(0xEE);
        using IDemuxer demuxer = MediaFile.OpenDemuxer(new MemoryStream(file));
        List<ReadPacket> packets = Corpus.ReadAll(demuxer);
        Assert.Equal(19, packets.Count);
    }

    [Fact]
    public void LargeOpenDmlFilesMatchFfprobe()
    {
        Assert.SkipWhen(Corpus.LargeAvi is null, "Run tools/video/make-corpus.py --large to make the OpenDML clip.");
        string path = Corpus.LargeAvi!;
        Probe probe = Corpus.ReadProbe(path);
        using IDemuxer demuxer = MediaFile.OpenDemuxer(path);
        Corpus.AssertMatchesProbe(probe, Corpus.ReadAll(demuxer));
        Assert.Equal(new MediaTime(4520, new Rational(1, 25)), demuxer.Tracks[0].Duration);

        // Seek into the last AVIX list.
        demuxer.Seek(MediaTime.FromSeconds(170));
        Assert.Equal(new MediaTime(170 * 25, new Rational(1, 25)), FirstVideo(demuxer).Timestamp);
    }

    private static ReadPacket FirstVideo(IDemuxer demuxer)
    {
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                if (packet.TrackNumber == 0)
                {
                    return new ReadPacket(0, packet.Timestamp, packet.Data.Length, packet.IsKeyframe, 0);
                }
            }
        }

        throw new InvalidOperationException("No video packet.");
    }

    internal static int FindChunk(byte[] file, string fourCC)
    {
        byte[] id = System.Text.Encoding.ASCII.GetBytes(fourCC);
        int at = file.AsSpan().IndexOf(id);
        Assert.True(at >= 0, $"{fourCC} not found");
        return at;
    }
}

internal sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
{
    public override bool CanSeek => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => base.Position;
        set => throw new NotSupportedException();
    }
}
