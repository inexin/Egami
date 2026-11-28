using System.Buffers.Binary;
using System.Text;

using Egami.Colors;
using Egami.Metadata;
using Egami.Pixels;
using Egami.Video.Codecs;
using Egami.Video.Containers;
using Egami.Video.Containers.Mp4;

namespace Egami.Video.Tests.Containers;

/// <summary>The QuickTime writer (<see cref="MovFormat"/>): its registration, the boxes it writes, and what it refuses.</summary>
public class MovWriterTests
{
    private static string PathOf(string clip) => Path.Combine(Corpus.Directory, "mp4", clip);

    /// <summary>The corpus clips whose every track QuickTime holds.</summary>
    public static TheoryData<string> Clips => [.. Directory.GetFiles(Path.Combine(Corpus.Directory, "mp4"))
        .Where(f => !f.EndsWith(".json", StringComparison.Ordinal) && File.Exists(f + ".json")).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal)
        .Where(c =>
        {
            using IDemuxer demuxer = MediaFile.OpenDemuxer(PathOf(c));
            return demuxer.Tracks.All(t => MovSampleEntries.Unsupported(t) is null);
        })];

    private static byte[] Remux(string path, MovFormat? format = null, Func<Stream, Stream>? wrap = null)
    {
        using IDemuxer demuxer = MediaFile.OpenDemuxer(path);
        return Write(demuxer.Tracks, demuxer.Info.Metadata, Mp4WriterTests.ReadAll(demuxer).Select((p, i) => (demuxer.Tracks[p.Track].Number, p)).ToList(), format, wrap);
    }

    private static byte[] Write(IReadOnlyList<TrackInfo> tracks, MediaMetadata metadata, List<(int Track, Mp4WriterTests.Packet Packet)> packets, MovFormat? format = null, Func<Stream, Stream>? wrap = null)
    {
        MemoryStream output = new();
        using (IMuxer muxer = (format ?? MovFormat.Instance).CreateMuxer(wrap?.Invoke(output) ?? output, tracks, metadata, MediaContext.Default, leaveOpen: true))
        {
            foreach ((int track, Mp4WriterTests.Packet p) in packets)
            {
                Rational timeBase = tracks.First(t => t.Number == track).TimeBase;
                using EncodedPacket packet = new(track, p.Data, new MediaTime(p.Pts, timeBase))
                {
                    DecodeTimestamp = new MediaTime(p.Dts, timeBase),
                    Duration = p.Duration is { } d ? new MediaTime(d, timeBase) : null,
                    IsKeyframe = p.Key,
                    IsDiscardable = p.Discard,
                };
                muxer.WritePacket(packet);
            }
        }

        return output.ToArray();
    }

    /// <summary>A box found in a file: its path of types from the top, where its content starts and how long it is.</summary>
    internal sealed record Box(string Path, int Content, int Length);

    private static readonly HashSet<string> Containers = ["moov", "trak", "mdia", "minf", "stbl", "dinf", "edts", "udta", "tref", "gmhd", "wave"];

    /// <summary>Lists every box of a QuickTime or MP4 file, descending into containers and into each sample description's atoms.</summary>
    internal static List<Box> Boxes(byte[] file)
    {
        List<Box> boxes = [];
        Walk(file, 0, file.Length, "", null, false, boxes);
        return boxes;
    }

    private static void Walk(byte[] file, int start, int end, string parent, string? handler, bool stsd, List<Box> boxes)
    {
        for (int at = start; at + 8 <= end;)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(at));
            string type = Encoding.Latin1.GetString(file, at + 4, 4);
            int header = 8;
            if (size == 1)
            {
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(file.AsSpan(at + 8));
                header = 16;
            }
            else if (size == 0)
            {
                size = end - at;
            }

            if (size < 8 || at + size > end)
            {
                return;
            }

            string path = parent.Length == 0 ? type : parent + "/" + type;
            boxes.Add(new Box(path, at + header, (int)size - header));
            int contentStart = at + header, contentEnd = (int)(at + size);
            if (type == "hdlr" && file.AsSpan(contentStart + 4, 4).SequenceEqual("mhlr"u8))
            {
                handler = Encoding.Latin1.GetString(file, contentStart + 8, 4);
            }

            if (type == "stsd")
            {
                Walk(file, contentStart + 8, contentEnd, path, handler, true, boxes);
            }
            else if (stsd)
            {
                int fields = handler switch
                {
                    "vide" => 78,
                    "soun" => BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(contentStart + 8)) switch { 1 => 44, 2 => 64, _ => 28 },
                    _ => -1,
                };
                if (fields > 0)
                {
                    Walk(file, contentStart + fields, contentEnd, path, handler, false, boxes);
                }
            }
            else if (Containers.Contains(type))
            {
                Walk(file, contentStart, contentEnd, path, handler, false, boxes);
            }

            at += (int)size;
        }
    }

    private static ReadOnlySpan<byte> Content(byte[] file, Box box) => file.AsSpan(box.Content, box.Length);

    private static Box Single(List<Box> boxes, string suffix) => Assert.Single(boxes, b => b.Path.EndsWith(suffix, StringComparison.Ordinal));

    [Fact]
    public void MovOutputIsQuickTimeAndMp4ByNameIsStillMp4()
    {
        MediaContext context = MediaContext.Default;
        Assert.IsType<MovFormat>(context.FindContainer(".mov"));
        Assert.IsType<MovFormat>(context.FindContainer("qt"));
        Assert.IsType<MovFormat>(context.FindContainer("QuickTime"));
        Assert.IsType<Mp4Format>(context.FindContainer("MP4"));
        Assert.IsType<Mp4Format>(context.FindContainer(".mp4"));
        Assert.IsType<Mp4Format>(context.FindContainer(".m4a"));
        int mov = context.Containers.ToList().IndexOf(MovFormat.Instance), mp4 = context.Containers.ToList().IndexOf(Mp4Format.Instance);
        Assert.True(mov >= 0 && mov < mp4, $"QuickTime is at {mov}, MP4 at {mp4}");

        string path = Path.Combine(Path.GetTempPath(), $"egami-mov-{Guid.NewGuid():N}.mov");
        try
        {
            MediaFile.Remux(PathOf("mp4-h264-aac.mp4"), path, cancellationToken: TestContext.Current.CancellationToken);
            byte[] file = File.ReadAllBytes(path);
            Assert.Equal("ftypqt  ", Encoding.Latin1.GetString(file, 4, 8));
        }
        finally
        {
            File.Delete(path);
        }

        MemoryStream mp4File = new();
        MediaFile.Remux(File.OpenRead(PathOf("mov-mjpeg-sowt.mov")), mp4File, "MP4", leaveOpen: true, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("ftypisom", Encoding.Latin1.GetString(mp4File.ToArray(), 4, 8));
    }

    [Theory]
    [MemberData(nameof(Mp4ReaderTests.Clips), MemberType = typeof(Mp4ReaderTests))]
    public void ReadingIsMp4Formats(string clip)
    {
        // The QuickTime format reads with the MP4 reader, so detection finding it first changes nothing.
        using IDemuxer viaMov = MovFormat.Instance.OpenDemuxer(File.OpenRead(PathOf(clip)), MediaContext.Default, leaveOpen: false);
        using IDemuxer viaMp4 = Mp4Format.Instance.OpenDemuxer(File.OpenRead(PathOf(clip)), MediaContext.Default, leaveOpen: false);
        Assert.Equal(viaMp4.Info.FormatName, viaMov.Info.FormatName);
        Assert.Equal(viaMp4.Tracks.Select(t => (t.CodecId, t.CodecPrivate.ToArray())), viaMov.Tracks.Select(t => (t.CodecId, t.CodecPrivate.ToArray())));
        Assert.Equal(Mp4WriterTests.ReadAll(viaMp4).Select(p => (p.Track, p.Pts, p.Data.Length)), Mp4WriterTests.ReadAll(viaMov).Select(p => (p.Track, p.Pts, p.Data.Length)));
    }

    [Theory]
    [MemberData(nameof(Clips))]
    public void ReadWriteReadKeepsEveryPacket(string clip)
    {
        using IDemuxer original = MediaFile.OpenDemuxer(PathOf(clip));
        List<Mp4WriterTests.Packet> before = Mp4WriterTests.ReadAll(original);
        bool fragmented = File.ReadAllBytes(PathOf(clip)).AsSpan().IndexOf("moof"u8) >= 0;
        foreach ((string layout, byte[] file) in new[]
        {
            ("moov first, in place", Remux(PathOf(clip))),
            ("moov first, from a temporary file", Remux(PathOf(clip), wrap: s => new Mp4WriterTests.NonSeekableWriteStream(s))),
            ("moov at the end", Remux(PathOf(clip), new MovFormat { MoovAtEnd = true })),
        })
        {
            Assert.Equal("ftypqt  ", Encoding.Latin1.GetString(file, 4, 8));
            List<Box> boxes = Boxes(file);
            int wide = boxes.FindIndex(b => b.Path == "wide"), mdat = boxes.FindIndex(b => b.Path == "mdat");
            Assert.True(wide >= 0 && wide + 1 == mdat, $"{layout}: wide is not right before mdat");
            Assert.All(boxes.Where(b => b.Path.EndsWith("/mdia/hdlr", StringComparison.Ordinal)), b => Assert.Equal("mhlr", Encoding.Latin1.GetString(file, b.Content + 4, 4)));
            Assert.All(boxes.Where(b => b.Path.EndsWith("/minf/hdlr", StringComparison.Ordinal)), b => Assert.Equal("dhlralis", Encoding.Latin1.GetString(file, b.Content + 4, 8)));

            using IDemuxer again = MediaFile.OpenDemuxer(new MemoryStream(file));
            Assert.Equal("QuickTime", again.Info.FormatName);
            Assert.Equal(original.Tracks.Count, again.Tracks.Count);
            for (int i = 0; i < original.Tracks.Count; i++)
            {
                TrackInfo a = original.Tracks[i], b = again.Tracks[i];
                Assert.Equal((a.CodecId, a.Kind, a.Language), (b.CodecId, b.Kind, b.Language));
                Assert.Equal(a.CodecPrivate.ToArray(), b.CodecPrivate.ToArray());
                if (a is VideoTrackInfo va && b is VideoTrackInfo vb)
                {
                    Assert.Equal((va.Width, va.Height, va.Orientation, va.Color, va.PixelAspectRatio), (vb.Width, vb.Height, vb.Orientation, vb.Color, vb.PixelAspectRatio));
                }
                else if (a is AudioTrackInfo aa && b is AudioTrackInfo ab)
                {
                    Assert.Equal((aa.SampleRate, aa.Channels, aa.BitsPerSample), (ab.SampleRate, ab.Channels, ab.BitsPerSample));
                }
            }

            List<Mp4WriterTests.Packet> after = Mp4WriterTests.ReadAll(again);
            Assert.True(before.Count == after.Count, $"{layout}: {after.Count} packets, {before.Count} before");
            for (int i = 0; i < before.Count; i++)
            {
                Mp4WriterTests.Packet x = before[i], y = after.Where(p => p.Track == x.Track).ElementAt(before.Take(i).Count(p => p.Track == x.Track));
                Assert.True(x.Data.AsSpan().SequenceEqual(y.Data), $"{layout} packet {i}: bytes differ");
                Assert.True((x.Pts, x.Dts, x.Key) == (y.Pts, y.Dts, y.Key), $"{layout} packet {i}: {(y.Pts, y.Dts, y.Key)}, before {(x.Pts, x.Dts, x.Key)}");
                Assert.True(fragmented || x.Discard == y.Discard, $"{layout} packet {i}: discardable {y.Discard}, before {x.Discard}");
            }
        }
    }

    [Fact]
    public void QuickTimeOnlyTracksAreWrittenBackByteForByte()
    {
        // mov-tmcd.mov's timecode track is passed through: its sample description and samples come back unchanged.
        byte[] source = File.ReadAllBytes(PathOf("mov-tmcd.mov"));
        byte[] file = Remux(PathOf("mov-tmcd.mov"));
        static Box Entry(byte[] f) => Boxes(f).Single(b => b.Path.EndsWith("stsd/tmcd", StringComparison.Ordinal));
        Assert.Equal(Content(source, Entry(source)).ToArray(), Content(file, Entry(file)).ToArray());
        using IDemuxer demuxer = MediaFile.OpenDemuxer(new MemoryStream(file));
        Assert.Single(demuxer.Tracks, t => t.CodecId == CodecIds.QuickTimeOther);
        Assert.Contains(Boxes(file), b => b.Path.EndsWith("mdia/hdlr", StringComparison.Ordinal) && Encoding.Latin1.GetString(file, b.Content + 4, 8) == "mhlrtmcd");
        Assert.Equal(MediaFile.Probe(PathOf("mov-tmcd.mov")).Timecode, MediaFile.Probe(new MemoryStream(file)).Timecode);
    }

    [Fact]
    public void AacGoesInAWaveAtomAndReadsBackAsAac()
    {
        byte[] file = Remux(PathOf("mp4-h264-aac.mp4"));
        List<Box> boxes = Boxes(file);
        Box entry = Single(boxes, "stsd/mp4a");
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(Content(file, entry)[8..]));
        Assert.Equal(-2, BinaryPrimitives.ReadInt16BigEndian(Content(file, entry)[20..]));
        Assert.Equal(1024u, BinaryPrimitives.ReadUInt32BigEndian(Content(file, entry)[28..]));
        Assert.Equal("mp4a", Encoding.Latin1.GetString(Content(file, Single(boxes, "mp4a/wave/frma"))));
        Single(boxes, "mp4a/wave/esds");
        Single(boxes, "mp4a/chan");
        Single(boxes, "stsd/avc1/avcC");
        Single(boxes, "stsd/avc1/pasp");
        using IDemuxer original = MediaFile.OpenDemuxer(PathOf("mp4-h264-aac.mp4"));
        using IDemuxer again = MediaFile.OpenDemuxer(new MemoryStream(file));
        Assert.Equal(CodecIds.Aac, again.Tracks[1].CodecId);
        Assert.Equal(original.Tracks[1].CodecPrivate.ToArray(), again.Tracks[1].CodecPrivate.ToArray());
    }

    [Fact]
    public void AlacGoesInAWaveAtom()
    {
        byte[] file = Remux(Path.Combine(Corpus.Directory, "audio", "alac-files", "mov-alac-mono.mov"));
        List<Box> boxes = Boxes(file);
        Assert.Equal("alac", Encoding.Latin1.GetString(Content(file, Single(boxes, "alac/wave/frma"))));
        Assert.Equal(4 + 24, Single(boxes, "alac/wave/alac").Length);
        using IDemuxer demuxer = MediaFile.OpenDemuxer(new MemoryStream(file));
        AudioTrackInfo track = Assert.IsType<AudioTrackInfo>(demuxer.Tracks[0]);
        Assert.Equal((CodecIds.Alac, 22050, 1, 16), (track.CodecId, track.SampleRate, track.Channels, track.BitsPerSample));
    }

    public static TheoryData<string, int, int, int, string, int> Pcm => new()
    {
        { CodecIds.PcmLittleEndian, 16, 2, 48000, "sowt", 0 },
        { CodecIds.PcmBigEndian, 16, 1, 44100, "twos", 0 },
        { CodecIds.PcmLittleEndian, 8, 1, 8000, "raw ", 0 },
        { CodecIds.PcmLittleEndian, 24, 2, 48000, "in24", 1 },
        { CodecIds.PcmBigEndian, 24, 6, 48000, "in24", 1 },
        { CodecIds.PcmLittleEndian, 32, 1, 48000, "in32", 1 },
        { CodecIds.PcmBigEndian, 32, 2, 32000, "in32", 1 },
        { CodecIds.PcmFloat, 32, 2, 48000, "fl32", 1 },
        { CodecIds.PcmFloat, 64, 1, 44100, "fl64", 1 },
        { CodecIds.PcmLittleEndian, 24, 2, 96000, "lpcm", 2 },
        { CodecIds.PcmBigEndian, 16, 2, 192000, "lpcm", 2 },
        { CodecIds.PcmFloat, 32, 8, 96000, "lpcm", 2 },
    };

    [Theory]
    [MemberData(nameof(Pcm))]
    public void PcmTakesQuickTimesSoundDescriptions(string codec, int bits, int channels, int rate, string type, int version)
    {
        AudioTrackInfo track = new() { Number = 1, Kind = TrackKind.Audio, CodecId = codec, TimeBase = new Rational(1, rate), SampleRate = rate, Channels = channels, BitsPerSample = bits };
        int frame = bits / 8 * channels;
        byte[] data = new byte[frame * 1000];
        new Random(bits * channels).NextBytes(data);
        byte[] file = Write([track], MediaMetadata.Empty, [(1, new Mp4WriterTests.Packet(0, 0, 0, 1000, true, false, data))]);
        List<Box> boxes = Boxes(file);
        Box entry = Single(boxes, "stsd/" + type);
        Assert.Equal(version, BinaryPrimitives.ReadUInt16BigEndian(Content(file, entry)[8..]));
        if (type is "in24" or "in32" or "fl32" or "fl64")
        {
            Assert.Equal(type, Encoding.Latin1.GetString(Content(file, Single(boxes, type + "/wave/frma"))));
            Assert.Equal(codec == CodecIds.PcmBigEndian ? 0 : 1, BinaryPrimitives.ReadUInt16BigEndian(Content(file, Single(boxes, type + "/wave/enda"))));
        }

        using IDemuxer demuxer = MediaFile.OpenDemuxer(new MemoryStream(file));
        AudioTrackInfo back = Assert.IsType<AudioTrackInfo>(demuxer.Tracks[0]);
        Assert.Equal((codec, bits, channels, rate), (back.CodecId, back.BitsPerSample, back.Channels, back.SampleRate));
        Assert.Equal(data, Mp4WriterTests.ReadAll(demuxer).SelectMany(p => p.Data).ToArray());
    }

    [Fact]
    public void VideoDescriptionsCarryColorFieldsAndAspect()
    {
        using IDemuxer demuxer = MediaFile.OpenDemuxer(PathOf("mp4-hevc.mp4"));
        VideoTrackInfo track = (VideoTrackInfo)demuxer.Tracks[0] with
        {
            Color = new ColorCodePoints(9, 16, 9, false),
            FieldOrder = FieldOrder.BottomFieldFirst,
            PixelAspectRatio = new Rational(4, 3),
        };
        List<(int, Mp4WriterTests.Packet)> packets = [.. Mp4WriterTests.ReadAll(demuxer).Select(p => (track.Number, p))];
        byte[] file = Write([track], MediaMetadata.Empty, packets);
        List<Box> boxes = Boxes(file);
        Box entry = Single(boxes, "stsd/hvc1");
        Assert.Equal("HEVC", Encoding.ASCII.GetString(Content(file, entry).Slice(43, Content(file, entry)[42])));
        Assert.Equal((byte[])[.. "nclc"u8, 0, 9, 0, 16, 0, 9], Content(file, Single(boxes, "hvc1/colr")).ToArray());
        Assert.Equal((byte[])[2, 6], Content(file, Single(boxes, "hvc1/fiel")).ToArray());
        Assert.Equal((byte[])[0, 0, 0, 4, 0, 0, 0, 3], Content(file, Single(boxes, "hvc1/pasp")).ToArray());
        VideoTrackInfo back = Assert.IsType<VideoTrackInfo>(MediaFile.OpenDemuxer(new MemoryStream(file)).Tracks[0]);
        Assert.Equal((CodecIds.Hevc, track.Color, FieldOrder.BottomFieldFirst, new Rational(4, 3)), (back.CodecId, back.Color, back.FieldOrder, back.PixelAspectRatio));

        // Full range has no nclc field: ISO's nclx carries it.
        byte[] full = Write([track with { Color = new ColorCodePoints(1, 1, 1, true) }], MediaMetadata.Empty, packets);
        Assert.Equal((byte[])[.. "nclx"u8, 0, 1, 0, 1, 0, 1, 0x80], Content(full, Single(Boxes(full), "hvc1/colr")).ToArray());
    }

    [Fact]
    public void MotionJpegIsPhotoJpeg()
    {
        byte[] file = Remux(PathOf("mp4-mjpeg.mp4"));
        List<Box> boxes = Boxes(file);
        Single(boxes, "stsd/jpeg");
        Assert.Equal((byte[])[1, 0], Content(file, Single(boxes, "jpeg/fiel")).ToArray());
        Assert.Equal(CodecIds.MotionJpeg, MediaFile.Probe(new MemoryStream(file)).VideoTrack!.CodecId);
    }

    private static VideoTrackInfo Raw(string? fourCC, ChromaFormat chroma, int depth, int width = 50, int height = 4) => new()
    {
        Number = 1,
        Kind = TrackKind.Video,
        CodecId = CodecIds.Uncompressed,
        TimeBase = new Rational(1, 25),
        FrameRate = new Rational(25, 1),
        Width = width,
        Height = height,
        FourCC = fourCC,
        ChromaFormat = chroma,
        BitDepth = depth,
    };

    /// <summary>Writes one frame of a track and returns the file, its sample description and the frame as stored.</summary>
    private static (byte[] File, List<Box> Boxes, byte[] Stored) WriteFrame(VideoTrackInfo track, byte[] frame)
    {
        byte[] file = Write([track], MediaMetadata.Empty, [(1, new Mp4WriterTests.Packet(0, 0, 0, 1, true, false, frame))]);
        using IDemuxer demuxer = MediaFile.OpenDemuxer(new MemoryStream(file));
        return (file, Boxes(file), demuxer.ReadPacket()!.Data.ToArray());
    }

    [Fact]
    public void UyvyAndV210AreStoredAsTheyCome()
    {
        foreach ((string fourCC, string type, int depth, int size) in new[] { ("UYVY", "2vuy", 8, 25 * 4 * 4), ("v210", "v210", 10, 128 * 2 * 4) })
        {
            byte[] frame = new byte[size];
            new Random(size).NextBytes(frame);
            (byte[] file, List<Box> boxes, byte[] stored) = WriteFrame(Raw(fourCC, ChromaFormat.Yuv422, depth), frame);
            Single(boxes, $"stsd/{type}");
            Assert.Equal((byte[])[1, 0], Content(file, Single(boxes, $"{type}/fiel")).ToArray());
            Single(boxes, $"{type}/pasp");
            Box clap = Single(boxes, $"{type}/clap");
            Assert.Equal(50u, BinaryPrimitives.ReadUInt32BigEndian(Content(file, clap)));
            Assert.Equal(frame, stored);
            Assert.Equal(CodecIds.QuickTimeVideo, MediaFile.Probe(new MemoryStream(file)).VideoTrack!.CodecId);
        }
    }

    [Fact]
    public void PlanarAndYuyvFramesArePackedAs2vuy()
    {
        int w = 5, h = 2, half = 3;
        byte[] planar = new byte[(w * h) + (2 * half * h)];
        for (int i = 0; i < planar.Length; i++)
        {
            planar[i] = (byte)(i + 1);
        }

        (_, List<Box> boxes, byte[] stored) = WriteFrame(Raw("Y42B", ChromaFormat.Yuv422, 8, w, h), planar);
        Single(boxes, "stsd/2vuy");
        // Row 0: Cb Y0 Cr Y1 for pixels 0-1, 2-3, and 4 with its Y repeated.
        Assert.Equal((byte[])[11, 1, 17, 2, 12, 3, 18, 4, 13, 5, 19, 5], stored[..12]);
        Assert.Equal((byte[])[14, 6, 20, 7, 15, 8, 21, 9, 16, 10, 22, 10], stored[12..]);

        byte[] yuyv = [1, 2, 3, 4, 5, 6, 7, 8];
        (_, _, byte[] swapped) = WriteFrame(Raw("YUY2", ChromaFormat.Yuv422, 8, 2, 2), yuyv);
        Assert.Equal((byte[])[2, 1, 4, 3, 6, 5, 8, 7], swapped);
    }

    [Fact]
    public void Planar10BitFramesArePackedAsV210()
    {
        int w = 7, h = 2, half = 4;
        ushort[] samples = [.. Enumerable.Range(0, (w * h) + (2 * half * h)).Select(i => (ushort)((i * 37) & 0x3FF))];
        byte[] planar = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(planar.AsSpan(i * 2), samples[i]);
        }

        (_, List<Box> boxes, byte[] stored) = WriteFrame(Raw(null, ChromaFormat.Yuv422, 10, w, h), planar);
        Single(boxes, "stsd/v210");
        Assert.Equal(128 * h, stored.Length);

        // The same frame read back through the uncompressed codec's v210 layout gives the planar samples.
        VideoTrackInfo v210 = Raw("v210", ChromaFormat.Yuv422, 10, w, h);
        using IVideoDecoder decoder = RawVideoCodec.Instance.CreateDecoder(v210, null, MediaContext.Default);
        decoder.Send(new EncodedPacket(1, stored, MediaTime.Zero) { IsKeyframe = true });
        Assert.True(decoder.TryReceive(out YuvVideoFrame? frame));
        using (frame)
        {
            YuvRaster<ushort> planes = Assert.IsType<YuvRaster<ushort>>(frame.Raster);
            for (int y = 0; y < h; y++)
            {
                Assert.Equal(samples.AsSpan(y * w, w).ToArray(), planes.Y.Row(y).ToArray());
                Assert.Equal(samples.AsSpan((w * h) + (y * half), half).ToArray(), planes.U!.Row(y).ToArray());
                Assert.Equal(samples.AsSpan((w * h) + (half * h) + (y * half), half).ToArray(), planes.V!.Row(y).ToArray());
            }
        }
    }

    [Fact]
    public void BgrAndGbrFramesAreStoredAsRawRgb()
    {
        // AVI's BI_RGB: bottom-up rows of B, G, R padded to four bytes.
        byte[] header = new byte[40];
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0), 40);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), 2);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14), 24);
        VideoTrackInfo bgr = new() { Number = 1, Kind = TrackKind.Video, CodecId = CodecIds.VfwFourCC, CodecPrivate = header, TimeBase = new Rational(1, 25), Width = 2, Height = 2 };
        byte[] frame = [1, 2, 3, 4, 5, 6, 0, 0, 7, 8, 9, 10, 11, 12, 0, 0];
        (byte[] file, List<Box> boxes, byte[] stored) = WriteFrame(bgr, frame);
        Single(boxes, "stsd/raw ");
        Assert.DoesNotContain(boxes, b => b.Path.EndsWith("/clap", StringComparison.Ordinal));
        Assert.Equal((byte[])[9, 8, 7, 12, 11, 10, 3, 2, 1, 6, 5, 4], stored);
        Assert.Equal("ftypqt  ", Encoding.Latin1.GetString(file, 4, 8));

        // Planar G, B, R (the identity matrix).
        VideoTrackInfo gbr = Raw("444P", ChromaFormat.Yuv444, 8, 2, 1) with { Color = new ColorCodePoints(1, 13, 0, true) };
        (_, _, byte[] rgb) = WriteFrame(gbr, [1, 2, 3, 4, 5, 6]);
        Assert.Equal((byte[])[5, 1, 3, 6, 2, 4], rgb);
    }

    [Fact]
    public void VideoWriterWrites2vuyFromRgbFrames()
    {
        string path = Path.Combine(Path.GetTempPath(), $"egami-mov-2vuy-{Guid.NewGuid():N}.mov");
        try
        {
            using (Raster<Rgb8> picture = Raster.Create<Rgb8>(32, 16))
            using (VideoWriter writer = VideoWriter.Create(path, new VideoWriterOptions { Codec = RawVideoCodec.Instance, ChromaFormat = ChromaFormat.Yuv422, FrameRate = new Rational(25, 1) }))
            {
                writer.WriteFrame(picture);
                writer.WriteFrame(picture);
            }

            byte[] file = File.ReadAllBytes(path);
            Single(Boxes(file), "stsd/2vuy");
            using IDemuxer demuxer = MediaFile.OpenDemuxer(path);
            Assert.Equal(16 * 4 * 16, demuxer.ReadPacket()!.Data.Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MovTextGoesOnASubtitleTrack()
    {
        byte[] file = Remux(PathOf("mp4-movtext.mp4"));
        List<Box> boxes = Boxes(file);
        Box hdlr = Assert.Single(boxes, b => b.Path.EndsWith("mdia/hdlr", StringComparison.Ordinal) && Encoding.Latin1.GetString(file, b.Content + 8, 4) == "sbtl");
        Assert.NotNull(hdlr);
        Single(boxes, "minf/gmhd/gmin");
        Single(boxes, "stsd/tx3g");
        Assert.Equal(CodecIds.MovText, MediaFile.Probe(new MemoryStream(file)).Tracks[1].CodecId);
    }

    [Theory]
    [InlineData("mov-metadata.mov")]
    [InlineData("mp4-metadata.mp4")]
    [InlineData("mp4box-chapqt.mp4")]
    public void TagsChaptersAndCoverArtSurviveARemux(string clip)
    {
        MediaInfo before = MediaFile.Probe(PathOf(clip));
        byte[] file = Remux(PathOf(clip));
        MediaInfo after = MediaFile.Probe(new MemoryStream(file));
        Assert.Equal(before.Tags.Where(t => t.Key is not ("MAJOR_BRAND" or "iTunSMPB")).OrderBy(t => t.Key), after.Tags.Where(t => t.Key is not ("MAJOR_BRAND" or "iTunSMPB")).OrderBy(t => t.Key));
        Assert.Equal(before.Chapters.Select(c => (c.Start.ToTimeSpan(), c.Title)), after.Chapters.Select(c => (c.Start.ToTimeSpan(), c.Title)));
        Assert.Equal(before.Attachments.Select(a => (a.MediaType, a.Data.ToArray())), after.Attachments.Select(a => (a.MediaType, a.Data.ToArray())));
        if (before.Tags.TryGetValue("TITLE", out string? title))
        {
            // The title is also QuickTime user data text: length, the packed language "und", then UTF-8.
            Box nam = Boxes(file).Single(b => b.Path == "moov/udta/©nam");
            Assert.Equal(Encoding.UTF8.GetByteCount(title), BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(nam.Content)));
            Assert.Equal(0x55C4, BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(nam.Content + 2)));
            Assert.Equal(title, Encoding.UTF8.GetString(file, nam.Content + 4, Encoding.UTF8.GetByteCount(title)));
        }

        if (before.Chapters.Count > 0)
        {
            // The chapter track is a QuickTime text track, with the text media header.
            List<Box> boxes = Boxes(file);
            Assert.Contains(boxes, b => b.Path.EndsWith("mdia/hdlr", StringComparison.Ordinal) && Encoding.Latin1.GetString(file, b.Content + 4, 8) == "mhlrtext");
            Single(boxes, "gmhd/text");
        }
    }

    [Theory]
    [InlineData(ImageOrientation.Rotate90)]
    [InlineData(ImageOrientation.FlipHorizontal)]
    public void OrientationIsWrittenAsTheDisplayMatrix(ImageOrientation orientation)
    {
        using IDemuxer demuxer = MediaFile.OpenDemuxer(PathOf("mp4-mjpeg.mp4"));
        VideoTrackInfo track = (VideoTrackInfo)demuxer.Tracks[0] with { Orientation = orientation };
        byte[] file = Write([track], MediaMetadata.Empty, [(track.Number, Mp4WriterTests.ReadAll(demuxer)[0])]);
        Assert.Equal(orientation, MediaFile.Probe(new MemoryStream(file)).VideoTrack!.Orientation);
    }

    public static TheoryData<string, string> Refused => new()
    {
        { CodecIds.Vp9, "MP4" },
        { CodecIds.Av1, "MP4" },
        { CodecIds.Vp8, "MP4" },
        { CodecIds.Ffv1, "Matroska" },
        { CodecIds.Opus, "MP4" },
        { CodecIds.Flac, "MP4" },
        { CodecIds.Vorbis, "MP4" },
        { CodecIds.Ac3, "MP4" },
        { CodecIds.Mp4WebVtt, "MP4" },
        { CodecIds.SubRip, "Matroska" },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void CodecsQuickTimeCannotHoldAreRefusedNamingAnotherFormat(string codec, string instead)
    {
        TrackInfo track = codec switch
        {
            CodecIds.Vp9 or CodecIds.Av1 or CodecIds.Vp8 or CodecIds.Ffv1 => new VideoTrackInfo { Number = 1, Kind = TrackKind.Video, CodecId = codec, CodecPrivate = new byte[4], TimeBase = new Rational(1, 25), Width = 16, Height = 16 },
            CodecIds.Mp4WebVtt or CodecIds.SubRip => new TrackInfo { Number = 1, Kind = TrackKind.Subtitle, CodecId = codec, TimeBase = new Rational(1, 1000) },
            _ => new AudioTrackInfo { Number = 1, Kind = TrackKind.Audio, CodecId = codec, TimeBase = new Rational(1, 48000), SampleRate = 48000, Channels = 2 },
        };
        NotSupportedException e = Assert.Throws<NotSupportedException>(() => MovFormat.Instance.CreateMuxer(new MemoryStream(), [track], MediaContext.Default, leaveOpen: false));
        Assert.Contains(instead, e.Message, StringComparison.Ordinal);
        Assert.Contains(codec, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UncompressedLayoutsWithoutAQuickTimeTypeAreRefused()
    {
        NotSupportedException e = Assert.Throws<NotSupportedException>(() => MovFormat.Instance.CreateMuxer(new MemoryStream(), [Raw("I420", ChromaFormat.Yuv420, 8)], MediaContext.Default, leaveOpen: false));
        Assert.Contains("Matroska", e.Message, StringComparison.Ordinal);
        Assert.Throws<NotSupportedException>(() => new MovFormat { MoovAtEnd = true }.CreateMuxer(new Mp4WriterTests.NonSeekableWriteStream(new MemoryStream()), [Raw("UYVY", ChromaFormat.Yuv422, 8)], MediaContext.Default, leaveOpen: false));
    }
}
