using Egami.Colors;
using Egami.Pixels;
using Egami.Video.Codecs;
using Egami.Video.Codecs.Av1;
using Egami.Video.Containers;

namespace Egami.Video.Tests.Codecs;

/// <summary>AV1 through the codec, VideoReader and the containers (V4.2 task 13).</summary>
public class Av1Tests
{
    public static TheoryData<string, string> ClipFiles
    {
        get
        {
            TheoryData<string, string> data = [];
            foreach (string clip in Av1Corpus.Clips())
            {
                foreach (string extension in (string[])[".ivf", ".webm", ".mkv"])
                {
                    if (File.Exists(Av1Corpus.PathOf(clip + extension)))
                    {
                        data.Add(clip, extension);
                    }
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ClipFiles))]
    public void FramesEqualLibaom(string clip, string extension)
    {
        List<(string Md5, string Md5NoGrain)> reference = Av1Corpus.ReadReference(clip);
        using (VideoReader reader = VideoReader.Open(Av1Corpus.PathOf(clip + extension), new VideoReaderOptions { Errors = DecodeErrorMode.Throw }))
        {
            Assert.Equal(CodecIds.Av1, reader.Track.CodecId);
            Assert.Equal(reference.Select(r => r.Md5), Av1Corpus.Md5s(reader));
        }

        VideoReaderOptions noGrain = new() { Errors = DecodeErrorMode.Throw, Decoder = new VideoDecoderOptions { ApplyFilmGrain = false } };
        using VideoReader plain = VideoReader.Open(Av1Corpus.PathOf(clip + extension), noGrain);
        Assert.Equal(reference.Select(r => r.Md5NoGrain), Av1Corpus.Md5s(plain));
    }

    [Theory]
    [MemberData(nameof(ClipFiles))]
    public void PacketsMatchFfprobe(string clip, string extension)
    {
        string path = Av1Corpus.PathOf(clip + extension);
        Assert.SkipUnless(File.Exists(path + ".json"), "No ffprobe reference for this file.");
        using IDemuxer demuxer = MediaFile.OpenDemuxer(path);
        Corpus.AssertMatchesProbe(Corpus.ReadProbe(path), Corpus.ReadAll(demuxer));
    }

    [Theory]
    [InlineData("av1-1-b8-03-sizeup.mkv")]
    [InlineData("av1-1-b8-03-sizedown.mkv")]
    public void SizeChangingVectorsEqualLibaom(string vector)
    {
        string? folder = Av1Corpus.Vectors;
        Assert.SkipWhen(folder is null, "The libaom test vectors are not fetched (python tools/make-av1-references.py).");
        string path = Path.Combine(folder!, vector);
        List<string> expected = [.. File.ReadAllLines(path + ".md5").Where(l => l.Length > 0).Select(l => l.Split(' ')[0])];
        using VideoReader reader = VideoReader.Open(path, new VideoReaderOptions { Errors = DecodeErrorMode.Throw });
        List<(int, int)> sizes = [];
        List<string> ours = [];
        foreach (YuvVideoFrame frame in reader.ReadFrames())
        {
            using (frame)
            {
                ours.Add(Av1Corpus.LibaomMd5(frame.Raster));
                sizes.Add((frame.Width, frame.Height));
            }
        }

        Assert.Equal(expected, ours);
        Assert.True(sizes.Distinct().Count() > 1, "the frames keep one size");
    }

    [Theory]
    [InlineData("tools-8bit", 8, ChromaFormat.Yuv420)]
    [InlineData("tools-10bit", 10, ChromaFormat.Yuv420)]
    [InlineData("tools-12bit", 12, ChromaFormat.Yuv420)]
    [InlineData("yuv422", 10, ChromaFormat.Yuv422)]
    [InlineData("yuv444", 8, ChromaFormat.Yuv444)]
    [InlineData("monochrome", 8, ChromaFormat.Yuv400)]
    public void FramesComeOutAtTheirBitDepthAndChroma(string clip, int bitDepth, ChromaFormat chroma)
    {
        using (IDemuxer demuxer = MediaFile.OpenDemuxer(Av1Corpus.PathOf(clip + ".ivf")))
        {
            VideoTrackInfo track = demuxer.Info.VideoTrack!;
            Assert.Equal(bitDepth, track.BitDepth);
            Assert.Equal(chroma, track.ChromaFormat);
            Assert.False(track.CodecPrivate.IsEmpty);
        }

        using VideoReader reader = VideoReader.Open(Av1Corpus.PathOf(clip + ".webm"));
        using YuvVideoFrame frame = reader.ReadFrames().First();
        Assert.Equal(bitDepth, frame.Raster.BitDepth);
        Assert.Equal(chroma, frame.Raster.ChromaFormat);
        Assert.Equal(bitDepth == 8, frame.Raster is YuvRaster<byte>);
    }

    [Fact]
    public void ForwardKeyFramesAreNotKeyframes()
    {
        // forward-keyframes.ivf hides a key frame and shows it later with show_existing_frame: neither unit is a point to
        // start from, as FFmpeg flags them too.
        using IDemuxer demuxer = MediaFile.OpenDemuxer(Av1Corpus.PathOf("forward-keyframes.ivf"));
        List<bool> ours = [.. Corpus.ReadAll(demuxer).Select(p => p.Key)];
        Probe probe = Corpus.ReadProbe(Av1Corpus.PathOf("forward-keyframes.webm"));
        Assert.Equal(probe.Packets.Select(p => p.Key), ours);
        Assert.Equal(2, ours.Count(k => k));
    }

    [Fact]
    public void AKeyframeFlagThatDisagreesIsReportedAndTheStreamWins()
    {
        List<EgamiDiagnostic> reported = [];
        MediaContext context = MediaContext.Default with { Egami = EgamiContext.Default with { Diagnostics = reported.Add } };
        using IDemuxer demuxer = MediaFile.OpenDemuxer(Av1Corpus.PathOf("tools-8bit.webm"));
        using IVideoDecoder decoder = VideoCodecs.CreateDecoder(demuxer.Info.VideoTrack!, null, context);
        List<string> md5 = [];
        for (int index = 0; demuxer.ReadPacket() is { } packet; index++)
        {
            using (packet)
            {
                if (index == 3)
                {
                    packet.IsKeyframe = true;
                }

                decoder.Send(packet);
            }

            while (decoder.TryReceive(out YuvVideoFrame? frame))
            {
                using (frame)
                {
                    md5.Add(Av1Corpus.Md5(frame.Raster));
                }
            }
        }

        Assert.Equal(Av1Corpus.ReadReference("tools-8bit").Select(r => r.Md5), md5);
        Assert.Equal(("Av1.KeyframeFlagMismatch", 3), Assert.Single(reported).Pipe(d => (d.Id, d.Frame)));
    }

    [Fact]
    public void TheSequenceHeaderComesFromAv1CWhenTheUnitsHaveNone()
    {
        // MP4 and Matroska may leave the sequence header out of the units: it is in av1C.
        using IDemuxer demuxer = MediaFile.OpenDemuxer(Av1Corpus.PathOf("tools-10bit.webm"));
        VideoTrackInfo track = demuxer.Info.VideoTrack!;
        Assert.False(track.CodecPrivate.IsEmpty);
        using IVideoDecoder decoder = VideoCodecs.CreateDecoder(track);
        List<string> md5 = [];
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                byte[] stripped = WithoutSequenceHeaders(packet.Data.Span);
                Assert.True(stripped.Length <= packet.Data.Length);
                decoder.Send(new EncodedPacket(packet.TrackNumber, stripped, packet.Timestamp) { IsKeyframe = packet.IsKeyframe });
            }

            while (decoder.TryReceive(out YuvVideoFrame? frame))
            {
                using (frame)
                {
                    md5.Add(Av1Corpus.Md5(frame.Raster));
                }
            }
        }

        Assert.Equal(Av1Corpus.ReadReference("tools-10bit").Select(r => r.Md5), md5);
    }

    [Theory]
    [InlineData("webm")]
    [InlineData("mkv")]
    [InlineData("ivf")]
    public void RemuxKeepsEveryPacket(string target)
    {
        string path = Av1Corpus.PathOf("forward-keyframes.webm");
        List<ReadPacket> original;
        IReadOnlyList<TrackInfo> tracks;
        MemoryStream output = new();
        using (IDemuxer demuxer = MediaFile.OpenDemuxer(path))
        {
            tracks = demuxer.Tracks;
            using IMuxer muxer = MediaFile.CreateMuxer(output, target, tracks, leaveOpen: true);
            original = [];
            while (demuxer.ReadPacket() is { } packet)
            {
                using (packet)
                {
                    original.Add(new ReadPacket(0, packet.Timestamp, packet.Data.Length, packet.IsKeyframe, HashCode.Combine(packet.Data.Length, Corpus.Hash(packet.Data.Span))));
                    muxer.WritePacket(packet);
                }
            }
        }

        output.Position = 0;
        using IDemuxer back = MediaFile.OpenDemuxer(output);
        Assert.Equal(tracks[0].CodecPrivate.ToArray(), back.Tracks[0].CodecPrivate.ToArray());
        List<ReadPacket> read = [.. Corpus.ReadAll(back)];
        Assert.Equal(original.Select(p => (p.Size, p.Hash, p.Key)), read.Select(p => (p.Size, p.Hash, p.Key)));
        Assert.Equal(original.Select(p => p.Timestamp.ToTimeSpan()), read.Select(p => p.Timestamp.ToTimeSpan()));
    }

    [Fact]
    public void MatroskaWriterBuildsAv1CFromTheFirstSequenceHeader()
    {
        // A track with no av1C, as a raw OBU stream gives: the writer takes it from the first packet's sequence header, as FFmpeg does.
        byte[] ffmpeg;
        using (IDemuxer webm = MediaFile.OpenDemuxer(Av1Corpus.PathOf("tools-12bit.webm")))
        {
            ffmpeg = webm.Tracks[0].CodecPrivate.ToArray();
        }

        MemoryStream output = new();
        using (IDemuxer demuxer = MediaFile.OpenDemuxer(Av1Corpus.PathOf("tools-12bit.ivf")))
        {
            VideoTrackInfo track = demuxer.Info.VideoTrack! with { CodecPrivate = ReadOnlyMemory<byte>.Empty };
            using IMuxer muxer = MediaFile.CreateMuxer(output, "mkv", [track], leaveOpen: true);
            while (demuxer.ReadPacket() is { } packet)
            {
                using (packet)
                {
                    muxer.WritePacket(packet);
                }
            }
        }

        output.Position = 0;
        using VideoReader reader = VideoReader.Open(output, new VideoReaderOptions { Errors = DecodeErrorMode.Throw });
        Assert.Equal(ffmpeg, reader.Track.CodecPrivate.ToArray());
        Assert.Equal(Av1Corpus.ReadReference("tools-12bit").Select(r => r.Md5), Av1Corpus.Md5s(reader));
    }

    [Fact]
    public void TheSequenceHeaderColorWinsOverTheContainers()
    {
        ColorCodePoints stream = new(9, 16, 9, false);
        Assert.Equal(stream, Av1VideoDecoder.MergeColor(stream, new ColorCodePoints(1, 1, 1, true)));
        Assert.Equal(new ColorCodePoints(1, 13, 9, false), Av1VideoDecoder.MergeColor(new ColorCodePoints(2, 2, 9, false), new ColorCodePoints(1, 13, 1, true)));
        Assert.Equal(new ColorCodePoints(2, 2, 2, true), Av1VideoDecoder.MergeColor(new ColorCodePoints(2, 2, 2, true), null));
    }

    internal static byte[] WithoutSequenceHeaders(ReadOnlySpan<byte> unit)
    {
        using MemoryStream stream = new();
        foreach (Av1Obu obu in Av1Obus.Split(unit))
        {
            if (obu.Type != Av1Obus.SequenceHeader)
            {
                stream.Write(unit[obu.Start..obu.End]);
            }
        }

        return stream.ToArray();
    }
}
