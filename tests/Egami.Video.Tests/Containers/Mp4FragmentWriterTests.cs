using System.Buffers.Binary;
using System.Text;

using Egami.Metadata;
using Egami.Video.Containers;

using static Egami.Video.Tests.Containers.Mp4WriterTests;

namespace Egami.Video.Tests.Containers;

public class Mp4FragmentWriterTests
{
    public static TheoryData<string> Clips => Mp4ReaderTests.Clips;

    private static readonly Mp4Format Fragmented = new() { Fragments = new Mp4FragmentOptions() };

    private static string PathOf(string clip) => Path.Combine(Corpus.Directory, "mp4", clip);

    /// <summary>Lists the top-level boxes of a file: type, offset and size.</summary>
    private static List<(string Type, long At, long Size)> Boxes(byte[] file)
    {
        List<(string, long, long)> boxes = [];
        for (long at = 0; at + 8 <= file.Length;)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan((int)at));
            string type = Encoding.Latin1.GetString(file, (int)at + 4, 4);
            if (size == 1)
            {
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(file.AsSpan((int)at + 8));
            }

            boxes.Add((type, at, size));
            at += size;
        }

        return boxes;
    }

    [Theory]
    [MemberData(nameof(Clips))]
    public void ReadWriteReadKeepsEveryPacket(string clip)
    {
        using IDemuxer original = MediaFile.OpenDemuxer(PathOf(clip));
        List<Packet> before = ReadAll(original);
        foreach ((string layout, byte[] file) in new[]
        {
            ("seekable", Remux(PathOf(clip), format: Fragmented)),
            ("non-seekable", Remux(PathOf(clip), s => new NonSeekableWriteStream(s))),
        })
        {
            // tools/video/check-mp4-mux.py sets this to have FFmpeg, MP4Box and Bento4 check what we wrote.
            if (Environment.GetEnvironmentVariable("EGAMI_MP4_OUT") is { Length: > 0 } folder)
            {
                Directory.CreateDirectory(folder);
                File.WriteAllBytes(Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(clip)}.fragmented-{layout}.mp4"), file);
            }

            List<string> types = [.. Boxes(file).Select(b => b.Type)];
            Assert.Equal(["ftyp", "moov"], types.Take(2));
            Assert.Equal("mfra", types[^1]);
            Assert.Contains("moof", types);
            Assert.DoesNotContain("free", types);

            using IDemuxer again = MediaFile.OpenDemuxer(new MemoryStream(file));
            Assert.Equal(original.Tracks.Select(t => (t.CodecId, t.Kind, t.Language)), again.Tracks.Select(t => (t.CodecId, t.Kind, t.Language)));
            List<Packet> after = ReadAll(again);
            Assert.True(before.Count == after.Count, $"{layout}: {after.Count} packets, {before.Count} before");
            for (int track = 0; track < original.Tracks.Count; track++)
            {
                List<Packet> x = [.. before.Where(p => p.Track == track)], y = [.. after.Where(p => p.Track == track)];
                for (int i = 0; i < x.Count; i++)
                {
                    Assert.True(x[i].Data.AsSpan().SequenceEqual(y[i].Data), $"{layout} track {track} packet {i}: bytes differ");
                    Assert.True((x[i].Pts, x[i].Dts, x[i].Key) == (y[i].Pts, y[i].Dts, y[i].Key), $"{layout} track {track} packet {i}: {(y[i].Pts, y[i].Dts, y[i].Key)}, before {(x[i].Pts, x[i].Dts, x[i].Key)}");

                    // Leading discardable packets (AAC priming) keep their edit list, but fragments' edits only shift times
                    // as FFmpeg reads them, so they come back without the flag.
                    Assert.True(x[i].Discard || !y[i].Discard, $"{layout} track {track} packet {i}: discardable, which it was not before");
                }
            }
        }
    }

    [Theory]
    [InlineData(true, 4)]
    [InlineData(false, 4)]
    public void FragmentsStartAtAKeyframeAfterTheirDuration(bool withVideo, int fragments)
    {
        // 4 seconds: video at 25 frames a second with a keyframe every 10, audio in 1024-sample packets at 8 kHz. Cut a second
        // after each start, video at frames 30, 60 and 90 (the keyframes after 25, 55 and 85), audio-only every 8 packets.
        VideoTrackInfo video = new() { Number = 1, Kind = TrackKind.Video, CodecId = CodecIds.MotionJpeg, TimeBase = new Rational(1, 25), Width = 16, Height = 16 };
        AudioTrackInfo audio = new() { Number = 2, Kind = TrackKind.Audio, CodecId = CodecIds.PcmLittleEndian, TimeBase = new Rational(1, 8000), SampleRate = 8000, Channels = 1, BitsPerSample = 16 };
        TrackInfo[] tracks = withVideo ? [video, audio] : [audio];
        MemoryStream output = new();
        Mp4Format format = new() { Fragments = new Mp4FragmentOptions { Duration = TimeSpan.FromSeconds(1) } };
        List<(int Track, long Time, bool Key)> written = [];
        using (IMuxer muxer = format.CreateMuxer(output, tracks, MediaContext.Default, leaveOpen: true))
        {
            int v = 0, a = 0;
            while ((withVideo && v < 100) || a < 32)
            {
                bool videoNext = withVideo && v < 100 && (a >= 32 || v * 320 <= a * 1024);
                (int track, long time, bool key, Rational timeBase) = videoNext ? (1, v, v % 10 == 0, video.TimeBase) : (2, a * 1024L, true, audio.TimeBase);
                using EncodedPacket packet = new(track, new byte[videoNext ? 100 : 2048], new MediaTime(time, timeBase)) { IsKeyframe = key };
                muxer.WritePacket(packet);
                written.Add((track, time, key));
                _ = videoNext ? v++ : a++;
            }
        }

        byte[] file = output.ToArray();
        Assert.Equal(fragments, Boxes(file).Count(b => b.Type == "moof"));
        using IDemuxer again = MediaFile.OpenDemuxer(new MemoryStream(file));
        List<Packet> read = ReadAll(again);
        foreach (int track in tracks.Select((_, i) => i))
        {
            Assert.Equal(written.Where(w => w.Track == tracks[track].Number).Select(w => (w.Time, w.Key)), read.Where(p => p.Track == track).Select(p => (p.Pts, p.Key)));
        }
    }

    [Fact]
    public void MaxBytesClosesAFragmentEarly()
    {
        VideoTrackInfo track = new() { Number = 1, Kind = TrackKind.Video, CodecId = CodecIds.MotionJpeg, TimeBase = new Rational(1, 30), Width = 16, Height = 16 };
        MemoryStream output = new();
        Mp4Format format = new() { Fragments = new Mp4FragmentOptions { Duration = TimeSpan.FromHours(1), MaxBytes = 10_000 } };
        using (IMuxer muxer = format.CreateMuxer(output, [track], MediaContext.Default, leaveOpen: true))
        {
            byte[] data = new byte[1000];
            for (int i = 0; i < 100; i++)
            {
                using EncodedPacket packet = new(1, data, new MediaTime(i, track.TimeBase)) { IsKeyframe = i == 0 };
                muxer.WritePacket(packet);
            }
        }

        byte[] file = output.ToArray();
        Assert.InRange(Boxes(file).Count(b => b.Type == "moof"), 10, 12);
        using IDemuxer again = MediaFile.OpenDemuxer(new MemoryStream(file));
        List<Packet> packets = ReadAll(again);
        Assert.Equal(Enumerable.Range(0, 100).Select(i => (long)i), packets.Select(p => p.Pts));
        Assert.Equal(Enumerable.Range(0, 100).Select(i => i == 0), packets.Select(p => p.Key));
    }

    [Fact]
    public void MoovAtEndAndFragmentsCannotBothBeSet()
    {
        Assert.Throws<ArgumentException>(() => new Mp4Format { MoovAtEnd = true, Fragments = new Mp4FragmentOptions() });
        Assert.Throws<ArgumentException>(() => new Mp4Format { Fragments = new Mp4FragmentOptions(), MoovAtEnd = true });
    }

    [Fact]
    public void NonSeekableOutputIsFragmentedWithAWarningUnlessTempFileIsAsked()
    {
        List<MediaWarning> warnings = [];
        MediaContext context = MediaContext.Default with { Warnings = warnings.Add };
        using IDemuxer demuxer = MediaFile.OpenDemuxer(PathOf("mp4-h264-aac.mp4"));
        MemoryStream fragmented = new(), flat = new();
        using (Mp4Format.Instance.CreateMuxer(new NonSeekableWriteStream(fragmented), demuxer.Tracks, MediaMetadata.Empty, context, leaveOpen: true))
        {
        }

        Assert.Contains(warnings, w => w.Code == "mp4-fragmented");
        Assert.Contains(Boxes(fragmented.ToArray()), b => b.Type == "mfra");
        warnings.Clear();
        using (new Mp4Format { NonSeekable = Mp4NonSeekableLayout.TempFile }.CreateMuxer(new NonSeekableWriteStream(flat), demuxer.Tracks, MediaMetadata.Empty, context, leaveOpen: true))
        {
        }

        Assert.Empty(warnings);
        Assert.DoesNotContain(Boxes(flat.ToArray()), b => b.Type == "mfra");
    }

    [Fact]
    public void RemuxToAForwardOnlyStreamWritesFragments()
    {
        List<MediaWarning> warnings = [];
        MemoryStream output = new();
        using (FileStream input = File.OpenRead(PathOf("mp4-vp9-opus.mp4")))
        {
            MediaFile.Remux(input, new NonSeekableWriteStream(output), "mp4", context: MediaContext.Default with { Warnings = warnings.Add }, leaveOpen: true, cancellationToken: TestContext.Current.CancellationToken);
        }

        Assert.Contains(warnings, w => w.Code == "mp4-fragmented");
        byte[] file = output.ToArray();
        Assert.Contains(Boxes(file), b => b.Type == "moof");
        using IDemuxer original = MediaFile.OpenDemuxer(PathOf("mp4-vp9-opus.mp4"));
        using IDemuxer again = MediaFile.OpenDemuxer(new MemoryStream(file));
        Assert.Equal(ReadAll(original).Select(p => (p.Track, p.Pts, p.Data.Length)).Order(), ReadAll(again).Select(p => (p.Track, p.Pts, p.Data.Length)).Order());
    }

    [Fact]
    public void SeekableOutputGetsItsDurationsWrittenBack()
    {
        string source = PathOf("mp4-h264-aac.mp4");
        MediaInfo flat = MediaFile.Probe(new MemoryStream(Remux(source)));
        byte[] seekable = Remux(source, format: Fragmented), forward = Remux(source, s => new NonSeekableWriteStream(s));
        MediaInfo fragmented = MediaFile.Probe(new MemoryStream(seekable));
        Assert.Equal(flat.Duration!.Value.ToTimeSpan(), fragmented.Duration!.Value.ToTimeSpan());
        Assert.True(seekable.AsSpan().IndexOf("mehd"u8) > 0);
        Assert.True(forward.AsSpan().IndexOf("mehd"u8) < 0);
    }

    [Fact]
    public void MfraListsEachFragmentsMoof()
    {
        byte[] file = Remux(PathOf("mp4-h264-aac.mp4"), format: new Mp4Format { Fragments = new Mp4FragmentOptions { Duration = TimeSpan.FromSeconds(1) } });
        List<(string Type, long At, long Size)> boxes = Boxes(file);
        HashSet<long> moofs = [.. boxes.Where(b => b.Type == "moof").Select(b => b.At)];
        (_, long mfraAt, long mfraSize) = boxes[^1];
        Assert.Equal(mfraSize, BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan((int)(mfraAt + mfraSize - 4))));

        // The video track's tfra: version 1, so each entry is time, moof offset, then three 4-byte numbers.
        int tfra = (int)mfraAt + 8;
        Assert.Equal("tfra", Encoding.Latin1.GetString(file, tfra + 4, 4));
        int count = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(tfra + 20));
        Assert.Equal(moofs.Count, count);
        for (int i = 0; i < count; i++)
        {
            Assert.Contains((long)BinaryPrimitives.ReadUInt64BigEndian(file.AsSpan(tfra + 24 + (i * 28) + 8)), moofs);
        }
    }

    [Theory]
    [InlineData("mp4-metadata.mp4")]
    [InlineData("mov-metadata.mov")]
    [InlineData("mp4box-chapqt.mp4")]
    public void TagsChaptersAndCoverArtSurvive(string clip)
    {
        List<MediaWarning> warnings = [];
        MediaInfo before = MediaFile.Probe(PathOf(clip));
        using IDemuxer demuxer = MediaFile.OpenDemuxer(PathOf(clip));
        MemoryStream output = new();
        using (IMuxer muxer = Fragmented.CreateMuxer(output, demuxer.Tracks, demuxer.Info.Metadata, MediaContext.Default with { Warnings = warnings.Add }, leaveOpen: true))
        {
            while (demuxer.ReadPacket() is { } packet)
            {
                using (packet)
                {
                    muxer.WritePacket(packet);
                }
            }
        }

        MediaInfo after = MediaFile.Probe(new MemoryStream(output.ToArray()));
        Assert.Equal(before.Tags.Where(t => t.Key != "MAJOR_BRAND").OrderBy(t => t.Key), after.Tags.Where(t => t.Key != "MAJOR_BRAND").OrderBy(t => t.Key));
        Assert.Equal(before.Chapters.Select(c => (c.Start.ToTimeSpan(), c.Title)), after.Chapters.Select(c => (c.Start.ToTimeSpan(), c.Title)));
        Assert.Equal(before.Attachments.Select(a => (a.MediaType, a.Data.ToArray())), after.Attachments.Select(a => (a.MediaType, a.Data.ToArray())));
        Assert.Equal(before.Chapters.Count > 0, warnings.Any(w => w.Code == "mp4-chapter-track"));
    }
}
