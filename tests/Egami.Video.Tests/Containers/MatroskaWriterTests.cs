using Egami.Video.Containers;

namespace Egami.Video.Tests.Containers;

public class MatroskaWriterTests
{
    public static TheoryData<string> Clips => [.. Corpus.Clips(".mkv", ".webm")];

    public static TheoryData<string> AllClips => [.. Corpus.Clips(".mkv", ".webm", ".avi")];

    public static TheoryData<string> AviClips => [.. Corpus.Clips(".avi")];

    [Theory]
    [MemberData(nameof(Clips))]
    public void RoundTripKeepsPacketsByteIdentical(string clip)
    {
        string format = clip.EndsWith(".webm", StringComparison.Ordinal) ? "webm" : "mkv";
        using IDemuxer source = MediaFile.OpenDemuxer(Corpus.PathOf(clip));
        List<ReadPacket> original = Corpus.ReadAll(source);
        byte[] written = AviWriterTests.Remux(Corpus.PathOf(clip), format);
        using IDemuxer reread = MediaFile.OpenDemuxer(new MemoryStream(written));
        Assert.Equal(format == "webm" ? "WebM" : "Matroska", reread.Info.FormatName);
        for (int i = 0; i < source.Tracks.Count; i++)
        {
            Assert.Equal(source.Tracks[i].CodecId, reread.Tracks[i].CodecId);
            Assert.Equal(source.Tracks[i].CodecPrivate.ToArray(), reread.Tracks[i].CodecPrivate.ToArray());
            Assert.Equal(source.Tracks[i].CodecDelay, reread.Tracks[i].CodecDelay);
        }

        Assert.Equal(original, Corpus.ReadAll(reread));
    }

    [Theory]
    [MemberData(nameof(AviClips))]
    public void AviToMatroskaAndBackKeepsEveryPacketAndTimestamp(string clip)
    {
        string path = Corpus.PathOf(clip);
        using IDemuxer source = MediaFile.OpenDemuxer(path);
        List<ReadPacket> original = Corpus.ReadAll(source);
        string middle = WriterTempFile(clip, ".mkv");
        File.WriteAllBytes(middle, AviWriterTests.Remux(path, "mkv"));
        using (IDemuxer mkv = MediaFile.OpenDemuxer(middle))
        {
            List<ReadPacket> inMatroska = Corpus.ReadAll(mkv);
            Assert.Equal(original.Select(p => (p.Track, p.Size, p.Hash)).Order(), inMatroska.Select(p => (p.Track, p.Size, p.Hash)).Order());
        }

        using IDemuxer reread = MediaFile.OpenDemuxer(new MemoryStream(AviWriterTests.Remux(middle, "avi")));
        List<ReadPacket> result = Corpus.ReadAll(reread);
        foreach (int track in original.Select(p => p.Track).Distinct())
        {
            Assert.Equal(original.Where(p => p.Track == track), result.Where(p => p.Track == track));
        }
    }

    [Fact]
    public void NonSeekableOutputGetsUnknownSizesAndReadsBack()
    {
        using IDemuxer source = MediaFile.OpenDemuxer(Corpus.PathOf("mkv-mjpeg-pcm.mkv"));
        List<ReadPacket> original = Corpus.ReadAll(source);
        source.Seek(MediaTime.Zero);
        MemoryStream sink = new();
        using (IMuxer muxer = MediaFile.CreateMuxer(new WriteOnlyStream(sink), "mkv", source.Tracks))
        {
            while (source.ReadPacket() is { } packet)
            {
                using (packet)
                {
                    muxer.WritePacket(packet);
                }
            }
        }

        byte[] file = sink.ToArray();
        int segment = file.AsSpan().IndexOf((ReadOnlySpan<byte>)[0x18, 0x53, 0x80, 0x67]);
        Assert.Equal(0x01, file[segment + 4]);
        Assert.True(file.AsSpan(segment + 5, 7).IndexOfAnyExcept((byte)0xFF) < 0);
        Assert.True(file.AsSpan().IndexOf((ReadOnlySpan<byte>)[0x1C, 0x53, 0xBB, 0x6B]) < 0, "no Cues");
        using IDemuxer reread = MediaFile.OpenDemuxer(new MemoryStream(file));
        Assert.Equal(original, Corpus.ReadAll(reread));

    }

    [Fact]
    public void WebMRefusesMotionJpeg()
    {
        using IDemuxer source = MediaFile.OpenDemuxer(Corpus.PathOf("mkv-mjpeg-pcm.mkv"));
        Assert.Throws<NotSupportedException>(() => MediaFile.CreateMuxer(new MemoryStream(), "webm", source.Tracks));
    }

    [Fact]
    public void CuesLetUsSeekInOurOwnFiles()
    {
        byte[] written = AviWriterTests.Remux(Corpus.PathOf("avi-mjpeg-420-pcm.avi"), "mkv");
        Assert.True(written.AsSpan().IndexOf((ReadOnlySpan<byte>)[0x1C, 0x53, 0xBB, 0x6B]) > 0, "Cues written");
        using IDemuxer demuxer = MediaFile.OpenDemuxer(new MemoryStream(written));
        demuxer.Seek(MediaTime.FromSeconds(1.5));
        using EncodedPacket first = demuxer.ReadPacket()!;
        Assert.Equal(demuxer.Tracks[0].Number, first.TrackNumber);
        Assert.Equal(new MediaTime(1480, new Rational(1, 1000)), first.Timestamp);
    }

    private static string WriterTempFile(string clip, string extension) => AviWriterTests.TempFile(Path.GetFileNameWithoutExtension(clip) + extension);

    private sealed class WriteOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }
}
