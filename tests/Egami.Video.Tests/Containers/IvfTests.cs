using System.Buffers.Binary;

using Egami.Video.Containers;

namespace Egami.Video.Tests.Containers;

public class IvfTests
{
    public static TheoryData<string> Clips => [.. Corpus.Vp8Clips()];

    [Theory]
    [MemberData(nameof(Clips))]
    public void PacketsMatchFfprobe(string clip)
    {
        string path = Corpus.Vp8PathOf(clip + ".ivf");
        Probe probe = Corpus.ReadProbe(path);
        using IDemuxer demuxer = MediaFile.OpenDemuxer(path);
        Assert.Equal("IVF", demuxer.Info.FormatName);
        VideoTrackInfo track = Assert.IsType<VideoTrackInfo>(Assert.Single(demuxer.Tracks));
        Assert.Equal(CodecIds.Vp8, track.CodecId);
        Assert.Equal((probe.Streams[0].Width, probe.Streams[0].Height), (track.Width, track.Height));
        Assert.Equal(probe.Streams[0].TimeBase, track.TimeBase);
        Corpus.AssertMatchesProbe(probe, Corpus.ReadAll(demuxer));
    }

    [Theory]
    [MemberData(nameof(Clips))]
    public void ReadWriteReadIsByteIdentical(string clip)
    {
        byte[] file = File.ReadAllBytes(Corpus.Vp8PathOf(clip + ".ivf"));
        Assert.Equal(file, Rewrite(new MemoryStream(file), new MemoryStream()));
    }

    [Theory]
    [MemberData(nameof(Clips))]
    public void WebMAndIvfHoldTheSamePackets(string clip)
    {
        using IDemuxer ivf = MediaFile.OpenDemuxer(Corpus.Vp8PathOf(clip + ".ivf"));
        using IDemuxer webm = MediaFile.OpenDemuxer(Corpus.Vp8PathOf(clip + ".webm"));
        Assert.Equal(Corpus.ReadAll(ivf).Select(p => (p.Size, p.Key, p.Hash)), Corpus.ReadAll(webm).Select(p => (p.Size, p.Key, p.Hash)));
    }

    [Fact]
    public void WebMRemuxedToIvfReadsBackWithTheSameTimes()
    {
        string path = Corpus.Vp8PathOf("v0-altref.webm");
        using IDemuxer webm = MediaFile.OpenDemuxer(path);
        List<ReadPacket> original = Corpus.ReadAll(webm);
        byte[] written = Rewrite(File.OpenRead(path), new MemoryStream(), IvfFormat.Instance);
        using IDemuxer reread = MediaFile.OpenDemuxer(new MemoryStream(written));
        Assert.Equal(new Rational(1, 1000), reread.Tracks[0].TimeBase);
        Assert.Equal(original, Corpus.ReadAll(reread));
        Assert.Equal((uint)original.Count, BinaryPrimitives.ReadUInt32LittleEndian(written.AsSpan(24)));
    }

    [Fact]
    public void NonSeekableInputAndOutputWork()
    {
        byte[] file = File.ReadAllBytes(Corpus.Vp8PathOf("v0-keyframes.ivf"));
        MemoryStream output = new();
        byte[] written = Rewrite(new NonSeekableStream(file), new WriteOnlyStream(output));

        // The frame count cannot be patched without seeking; everything else is the same.
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(written.AsSpan(24)));
        BinaryPrimitives.WriteUInt32LittleEndian(written.AsSpan(24), BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(24)));
        Assert.Equal(file, written);
    }

    [Theory]
    [InlineData("v0-keyframes")]
    [InlineData("v0-altref")]
    public void SeekLandsOnTheKeyframeAtOrBeforeTheTime(string clip)
    {
        string path = Corpus.Vp8PathOf(clip + ".ivf");
        using IDemuxer reader = MediaFile.OpenDemuxer(path);
        List<ReadPacket> all = Corpus.ReadAll(reader);
        using IDemuxer demuxer = MediaFile.OpenDemuxer(path);

        // Some packets read first, so the index is built from where reading stopped.
        demuxer.ReadPacket()!.Dispose();
        demuxer.ReadPacket()!.Dispose();
        foreach (double seconds in (double[])[0, 0.1, 0.25, 0.5, 0.75, 0.99, 5, 0.3])
        {
            MediaTime time = MediaTime.FromSeconds(seconds);
            int expected = all.FindLastIndex(p => p.Key && p.Timestamp <= time);
            demuxer.Seek(time);
            Assert.Equal(all[Math.Max(0, expected)..], Corpus.ReadAll(demuxer));
        }
    }

    [Fact]
    public void TruncatedLastFrameEndsTheFile()
    {
        byte[] file = File.ReadAllBytes(Corpus.Vp8PathOf("v0-keyframes.ivf"));
        using IDemuxer whole = MediaFile.OpenDemuxer(new MemoryStream(file));
        int count = Corpus.ReadAll(whole).Count;
        foreach (int cut in (int[])[1, 6, 13])
        {
            using IDemuxer demuxer = MediaFile.OpenDemuxer(new MemoryStream(file[..^cut]));
            Assert.Equal(count - 1, Corpus.ReadAll(demuxer).Count);
        }
    }

    [Fact]
    public void UnknownFourCCIsReadAsVp8OnlyWhenTheFirstFrameIsAVp8Keyframe()
    {
        // libvpx's segmentation-01 to -04 vectors name FourCC I420; libvpx's tools decode them as VP8 anyway.
        byte[] file = File.ReadAllBytes(Corpus.Vp8PathOf("v0-keyframes.ivf"));
        "I420"u8.CopyTo(file.AsSpan(8));
        List<MediaWarning> warnings = [];
        using (IDemuxer demuxer = MediaFile.OpenDemuxer(new MemoryStream(file), MediaContext.Default with { Warnings = warnings.Add }))
        {
            Assert.Equal(CodecIds.Vp8, demuxer.Tracks[0].CodecId);
        }

        Assert.Equal("ivf-fourcc", Assert.Single(warnings).Code);
        file[32 + 12 + 3] = 0;
        Assert.Throws<UnsupportedFormatException>(() => MediaFile.OpenDemuxer(new MemoryStream(file)));
        Assert.Throws<UnsupportedFormatException>(() => MediaFile.OpenDemuxer(new NonSeekableStream(file)));
    }

    [Fact]
    public void BrokenHeadersAreCorrupt()
    {
        byte[] file = File.ReadAllBytes(Corpus.Vp8PathOf("v0-keyframes.ivf"));
        byte[] zeroRate = (byte[])file.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(zeroRate.AsSpan(16), 0);
        Assert.Throws<CorruptDataException>(() => MediaFile.OpenDemuxer(new MemoryStream(zeroRate)));
        byte[] shortHeader = (byte[])file.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(shortHeader.AsSpan(6), 12);
        Assert.Throws<CorruptDataException>(() => MediaFile.OpenDemuxer(new MemoryStream(shortHeader)));
        Assert.Throws<CorruptDataException>(() => MediaFile.OpenDemuxer(new MemoryStream(file[..20])));
    }

    [Fact]
    public void HugeFrameSizeBreaksTheLimit()
    {
        byte[] file = File.ReadAllBytes(Corpus.Vp8PathOf("v0-keyframes.ivf"));
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(32), 0x7FFFFFF0);
        using IDemuxer demuxer = MediaFile.OpenDemuxer(new MemoryStream(file));
        Assert.Throws<LimitExceededException>(() => demuxer.ReadPacket());
    }

    [Fact]
    public void OnlyVp8Vp9AndAv1TracksCanBeWritten()
    {
        using IDemuxer mjpeg = MediaFile.OpenDemuxer(Corpus.PathOf("mkv-mjpeg-pcm.mkv"));
        Assert.Throws<NotSupportedException>(() => IvfFormat.Instance.CreateMuxer(new MemoryStream(), [mjpeg.Tracks[0]], MediaContext.Default, false));
        Assert.Throws<NotSupportedException>(() => IvfFormat.Instance.CreateMuxer(new MemoryStream(), mjpeg.Tracks, MediaContext.Default, false));
    }

    [Theory]
    [InlineData(new byte[] { 0x82, 0x49 }, true)] // profile 0, show_existing_frame 0, frame_type 0
    [InlineData(new byte[] { 0x82, 0x4D }, true)]
    [InlineData(new byte[] { 0x86, 0x00 }, false)] // profile 0, frame_type 1
    [InlineData(new byte[] { 0x88, 0x00 }, false)] // show_existing_frame
    [InlineData(new byte[] { 0xB0, 0x00 }, true)] // profile 3, reserved 0, frame_type 0
    [InlineData(new byte[] { 0xB2, 0x00 }, false)] // profile 3, frame_type 1
    public void Vp9KeyframesComeFromTheUncompressedHeader(byte[] frame, bool key)
    {
        Assert.Equal(key, Egami.Video.Codecs.FrameKind.IsKeyframe(CodecIds.Vp9, frame));
    }

    private static byte[] Rewrite(Stream input, Stream output, ContainerFormat? format = null)
    {
        using IDemuxer demuxer = MediaFile.OpenDemuxer(input);
        using (IMuxer muxer = (format ?? IvfFormat.Instance).CreateMuxer(output, demuxer.Tracks, MediaContext.Default, leaveOpen: true))
        {
            while (demuxer.ReadPacket() is { } packet)
            {
                using (packet)
                {
                    muxer.WritePacket(packet);
                }
            }
        }

        return output is WriteOnlyStream w ? w.Inner.ToArray() : ((MemoryStream)output).ToArray();
    }

    private sealed class WriteOnlyStream(MemoryStream inner) : Stream
    {
        public MemoryStream Inner => inner;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }
}
