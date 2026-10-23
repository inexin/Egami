using Egami.Metadata;
using Egami.Video.Containers;

namespace Egami.Video.Tests.Containers;

public class Mp4WriterTests
{
    public static TheoryData<string> Clips => Mp4ReaderTests.Clips;

    private static string PathOf(string clip) => Path.Combine(Corpus.Directory, "mp4", clip);

    internal sealed record Packet(int Track, long Pts, long Dts, long? Duration, bool Key, bool Discard, byte[] Data);

    internal static List<Packet> ReadAll(IDemuxer demuxer)
    {
        Dictionary<int, int> index = demuxer.Tracks.Select((t, i) => (t.Number, i)).ToDictionary();
        List<Packet> packets = [];
        while (demuxer.ReadPacket() is { } p)
        {
            using (p)
            {
                packets.Add(new Packet(index[p.TrackNumber], p.Timestamp.Ticks, (p.DecodeTimestamp ?? p.Timestamp).Ticks, p.Duration?.Ticks, p.IsKeyframe, p.IsDiscardable, p.Data.ToArray()));
            }
        }

        return packets;
    }

    internal static byte[] Remux(string path, Func<Stream, Stream>? wrap = null, Mp4Format? format = null)
    {
        using IDemuxer demuxer = MediaFile.OpenDemuxer(path);
        MemoryStream output = new();
        using (IMuxer muxer = (format ?? Mp4Format.Instance).CreateMuxer(wrap?.Invoke(output) ?? output, demuxer.Tracks, demuxer.Info.Metadata, MediaContext.Default, leaveOpen: true))
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

    [Theory]
    [MemberData(nameof(Clips))]
    public void ReadWriteReadKeepsEveryPacket(string clip)
    {
        using IDemuxer original = MediaFile.OpenDemuxer(PathOf(clip));
        bool fragmented = File.ReadAllBytes(PathOf(clip)).AsSpan().IndexOf("moof"u8) >= 0;
        List<Packet> before = ReadAll(original);
        foreach ((string layout, byte[] file) in new[]
        {
            ("moov first, in place", Remux(PathOf(clip))),
            ("moov first, from a temporary file", Remux(PathOf(clip), s => new NonSeekableWriteStream(s), new Mp4Format { NonSeekable = Mp4NonSeekableLayout.TempFile })),
            ("moov at the end", Remux(PathOf(clip), format: new Mp4Format { MoovAtEnd = true })),
        })
        {
            // tools/video/check-mp4-mux.py sets this to have FFmpeg and MP4Box check what we wrote.
            if (Environment.GetEnvironmentVariable("EGAMI_MP4_OUT") is { Length: > 0 } folder)
            {
                Directory.CreateDirectory(folder);
                File.WriteAllBytes(Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(clip)}.{layout.Split(',')[0].Replace(' ', '-')}{(layout.Contains("temporary", StringComparison.Ordinal) ? "-temp" : "")}.mp4"), file);
            }

            using IDemuxer again = MediaFile.OpenDemuxer(new MemoryStream(file));
            Assert.Equal(original.Tracks.Count, again.Tracks.Count);
            for (int i = 0; i < original.Tracks.Count; i++)
            {
                TrackInfo a = original.Tracks[i], b = again.Tracks[i];
                Assert.Equal(a.CodecId, b.CodecId);
                Assert.Equal(a.Kind, b.Kind);
                Assert.Equal(a.Language, b.Language);
                if (a.CodecId is not (CodecIds.Vp9 or CodecIds.Opus or CodecIds.Flac))
                {
                    // VP9, Opus and FLAC setups are rebuilt from their fields; the rest are kept byte for byte.
                    Assert.Equal(a.CodecPrivate.ToArray(), b.CodecPrivate.ToArray());
                }

                if (a is VideoTrackInfo va && b is VideoTrackInfo vb)
                {
                    Assert.Equal((va.Width, va.Height, va.Orientation, va.Color, va.PixelAspectRatio), (vb.Width, vb.Height, vb.Orientation, vb.Color, vb.PixelAspectRatio));
                }
            }

            List<Packet> after = ReadAll(again);
            Assert.True(before.Count == after.Count, $"{layout}: {after.Count} packets, {before.Count} before");
            int moov = file.AsSpan().IndexOf("moov"u8), mdat = file.AsSpan().IndexOf("mdat"u8);
            Assert.True(layout.StartsWith("moov first", StringComparison.Ordinal) ? moov < mdat : mdat < moov, $"{layout}: moov at {moov}, mdat at {mdat}");
            for (int i = 0; i < before.Count; i++)
            {
                Packet x = before[i], y = after.Where(p => p.Track == x.Track).ElementAt(before.Take(i).Count(p => p.Track == x.Track));
                Assert.True(x.Data.AsSpan().SequenceEqual(y.Data), $"{layout} packet {i}: bytes differ");
                Assert.True((x.Pts, x.Dts, x.Key) == (y.Pts, y.Dts, y.Key), $"{layout} packet {i}: {(y.Pts, y.Dts, y.Key)}, before {(x.Pts, x.Dts, x.Key)}");

                // A fragment's edit list only shifts its times; written flat, it cuts too.
                Assert.True(fragmented || x.Discard == y.Discard, $"{layout} packet {i}: discardable {y.Discard}, before {x.Discard}");
            }
        }
    }

    [Theory]
    [InlineData("mp4-metadata.mp4")]
    [InlineData("mov-metadata.mov")]
    [InlineData("mp4box-chapqt.mp4")]
    public void TagsChaptersAndCoverArtSurviveARemux(string clip)
    {
        MediaInfo before = MediaFile.Probe(PathOf(clip));
        MediaInfo after = MediaFile.Probe(new MemoryStream(Remux(PathOf(clip))));
        Assert.Equal(before.Tags.Where(t => t.Key != "MAJOR_BRAND").OrderBy(t => t.Key), after.Tags.Where(t => t.Key != "MAJOR_BRAND").OrderBy(t => t.Key));
        Assert.Equal(before.Chapters.Select(c => (c.Start.ToTimeSpan(), c.Title)), after.Chapters.Select(c => (c.Start.ToTimeSpan(), c.Title)));
        Assert.Equal(before.Attachments.Select(a => (a.MediaType, a.Data.ToArray())), after.Attachments.Select(a => (a.MediaType, a.Data.ToArray())));
    }

    [Fact]
    public void MatroskaToMp4ToMatroskaKeepsPacketsTagsAndChapters()
    {
        string source = Corpus.PathOf("mkv-metadata.mkv");
        byte[] mp4 = Remux(source);
        using IDemuxer original = MediaFile.OpenDemuxer(source);
        using IDemuxer fromMp4 = MediaFile.OpenDemuxer(new MemoryStream(mp4));
        MemoryStream mkv = new();
        using (IMuxer muxer = MatroskaFormat.Instance.CreateMuxer(mkv, fromMp4.Tracks, fromMp4.Info.Metadata, MediaContext.Default, leaveOpen: true))
        {
            while (fromMp4.ReadPacket() is { } packet)
            {
                using (packet)
                {
                    muxer.WritePacket(packet);
                }
            }
        }

        using IDemuxer back = MediaFile.OpenDemuxer(new MemoryStream(mkv.ToArray()));
        Assert.Equal(original.Info.Chapters.Select(c => (c.Start.ToTimeSpan(), c.Title)), back.Info.Chapters.Select(c => (c.Start.ToTimeSpan(), c.Title)));
        foreach (string tag in new[] { "TITLE", "ARTIST", "COMMENT" })
        {
            Assert.Equal(original.Info.Tags[tag], back.Info.Tags[tag]);
        }

        Assert.Equal(original.Info.Attachments.Single(a => a.IsCoverArt).Data.ToArray(), back.Info.Attachments.Single(a => a.IsCoverArt).Data.ToArray());
        List<ReadPacket> a = Corpus.ReadAll(original), b = Corpus.ReadAll(back);
        // MP4 interleaves half-second chunks, so the order across tracks changes; each track's packets stay as they were.
        Assert.Equal(a.OrderBy(p => p.Track).Select(p => (p.Track, p.Size, p.Hash, p.Timestamp.ToTimeSpan())), b.OrderBy(p => p.Track).Select(p => (p.Track, p.Size, p.Hash, p.Timestamp.ToTimeSpan())));
    }

    [Theory]
    [InlineData(ImageOrientation.Rotate90)]
    [InlineData(ImageOrientation.Rotate270)]
    [InlineData(ImageOrientation.Rotate180)]
    [InlineData(ImageOrientation.FlipHorizontal)]
    [InlineData(ImageOrientation.Transpose)]
    public void OrientationIsWrittenAsTheDisplayMatrix(ImageOrientation orientation)
    {
        using IDemuxer demuxer = MediaFile.OpenDemuxer(PathOf("mp4-mjpeg.mp4"));
        MemoryStream output = new();
        VideoTrackInfo track = (VideoTrackInfo)demuxer.Tracks[0] with { Orientation = orientation };
        using (IMuxer muxer = Mp4Format.Instance.CreateMuxer(output, [track], MediaContext.Default, leaveOpen: true))
        {
            using EncodedPacket packet = demuxer.ReadPacket()!;
            muxer.WritePacket(packet);
        }

        Assert.Equal(orientation, MediaFile.Probe(new MemoryStream(output.ToArray())).VideoTrack!.Orientation);
    }

    [Fact]
    public void UnsupportedCodecsAreRefused()
    {
        TrackInfo other = new AudioTrackInfo { Number = 1, Kind = TrackKind.Audio, CodecId = "X_CAF/QDMC", TimeBase = new Rational(1, 1000), SampleRate = 8000, Channels = 1 };
        Assert.Throws<NotSupportedException>(() => Mp4Format.Instance.CreateMuxer(new MemoryStream(), [other], MediaContext.Default, leaveOpen: false));
        TrackInfo video = new VideoTrackInfo { Number = 1, Kind = TrackKind.Video, CodecId = CodecIds.MotionJpeg, TimeBase = new Rational(1, 1000), Width = 16, Height = 16 };
        Assert.Throws<NotSupportedException>(() => new Mp4Format { MoovAtEnd = true }.CreateMuxer(new NonSeekableWriteStream(new MemoryStream()), [video], MediaContext.Default, leaveOpen: false));
    }

    /// <summary>A stream that can only be written forward, as a pipe or a network socket is.</summary>
    internal sealed class NonSeekableWriteStream(Stream inner) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }
}
