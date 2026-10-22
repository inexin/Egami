using Egami.Pixels;
using Egami.Video.Codecs;
using Egami.Video.Containers;
using Egami.Video.Tests.Containers;

namespace Egami.Video.Tests.Codecs;

/// <summary>Task 16: YUV4MPEG2, headerless raw video, uncompressed AVI and Matroska tracks, quality measures and target size.</summary>
public class RawVideoTests
{
    public static TheoryData<string> Y4mClips => [.. Corpus.Clips(".y4m")];

    public static TheoryData<string> UncompressedClips => [.. Corpus.Clips(".y4m", ".avi", ".mkv").Where(c => c.StartsWith("y4m", StringComparison.Ordinal) || c.Contains("raw", StringComparison.Ordinal) || c.Contains("v210", StringComparison.Ordinal))];

    [Theory]
    [MemberData(nameof(Y4mClips))]
    public void Y4mPacketsMatchFfprobe(string clip)
    {
        string path = Corpus.PathOf(clip);
        using IDemuxer demuxer = MediaFile.OpenDemuxer(path);
        Assert.Equal("YUV4MPEG2", demuxer.Info.FormatName);
        Corpus.AssertMatchesProbe(Corpus.ReadProbe(path), Corpus.ReadAll(demuxer));
    }

    [Theory]
    [MemberData(nameof(Y4mClips))]
    public void Y4mRoundTripIsByteIdentical(string clip)
    {
        string output = AviWriterTests.TempFile("roundtrip-" + clip);
        MediaFile.Remux(Corpus.PathOf(clip), output, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(File.ReadAllBytes(Corpus.PathOf(clip)), File.ReadAllBytes(output));
    }

    [Theory]
    [MemberData(nameof(UncompressedClips))]
    public void FramesDecodeAndEncodeBackToTheSameBytes(string clip)
    {
        using IDemuxer demuxer = MediaFile.OpenDemuxer(Corpus.PathOf(clip));
        VideoTrackInfo track = demuxer.Info.VideoTrack!;
        Assert.True(RawVideoCodec.Instance.CanDecode(track), $"{track.CodecId} {track.FourCC}");
        using IVideoDecoder decoder = RawVideoCodec.Instance.CreateDecoder(track, null, MediaContext.Default);
        Video.Codecs.Raw.RawLayout layout = Video.Codecs.Raw.RawLayout.ForTrack(track)!;
        int frames = 0;
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                if (packet.TrackNumber != track.Number)
                {
                    continue;
                }

                decoder.Send(packet);
                Assert.True(decoder.TryReceive(out YuvVideoFrame? frame));
                using (frame)
                {
                    Assert.Equal((track.Width, track.Height), (frame.Width, frame.Height));
                    byte[] again = new byte[packet.Data.Length];
                    layout.Write(frame.Raster, again);
                    Assert.True(again.AsSpan().SequenceEqual(packet.Data.Span), $"frame {frames} differs");
                    frames++;
                }
            }
        }

        Assert.True(frames >= 5);
    }

    [Theory]
    [InlineData("avi-raw-yuy2.avi", "mkv")]
    [InlineData("avi-v210.avi", "mkv")]
    [InlineData("mkv-raw-uyvy.mkv", "avi")]
    [InlineData("mkv-raw-i420.mkv", "avi")]
    public void UncompressedTracksRemuxBetweenAviAndMatroska(string clip, string format)
    {
        using IDemuxer source = MediaFile.OpenDemuxer(Corpus.PathOf(clip));
        List<ReadPacket> original = Corpus.ReadAll(source);
        using IDemuxer remuxed = MediaFile.OpenDemuxer(new MemoryStream(AviWriterTests.Remux(Corpus.PathOf(clip), format)));
        VideoTrackInfo a = source.Info.VideoTrack!, b = remuxed.Info.VideoTrack!;
        Assert.Equal((a.CodecId, a.FourCC), (b.CodecId, b.FourCC));
        Assert.Equal(original.Select(p => (p.Size, p.Hash)), Corpus.ReadAll(remuxed).Select(p => (p.Size, p.Hash)));
    }

    [Fact]
    public void Y4mWriterMakesFilesFromPlanes()
    {
        string path = AviWriterTests.TempFile("written.y4m");
        using (VideoWriter writer = VideoWriter.Create(path, new VideoWriterOptions { Codec = RawVideoCodec.Instance, FrameRate = new Rational(30000, 1001) }))
        {
            for (int i = 0; i < 3; i++)
            {
                using YuvRaster<ushort> planes = YuvRaster.Create(20, 10, ChromaFormat.Yuv422, 10, new YuvColor(YuvMatrix.Bt709, YuvRange.Limited, ChromaSiting.Left));
                planes.Y.Row(0).Fill((ushort)(100 * i));
                writer.WriteFrame(planes);
            }
        }

        string header = File.ReadAllText(path)[..60];
        Assert.StartsWith("YUV4MPEG2 W20 H10 F30000:1001 Ip A1:1 C422p10", header, StringComparison.Ordinal);
        using VideoReader reader = VideoReader.Open(path);
        List<YuvVideoFrame> frames = [.. reader.ReadFrames()];
        Assert.Equal(3, frames.Count);
        Assert.Equal(200, ((YuvRaster<ushort>)frames[2].Raster).Y.Row(0)[5]);
        frames.ForEach(f => f.Dispose());
    }

    [Fact]
    public void HeaderlessRawVideoReadsAndWrites()
    {
        VideoTrackInfo track = new() { Number = 1, Kind = TrackKind.Video, CodecId = CodecIds.Uncompressed, FourCC = "NV12", TimeBase = new Rational(1, 25), FrameRate = new Rational(25, 1), Width = 8, Height = 4 };
        byte[] frames = [.. Enumerable.Range(0, 48 * 3).Select(i => (byte)i)];
        using IDemuxer demuxer = RawVideo.OpenDemuxer(new MemoryStream(frames), track);
        MemoryStream output = new();
        using (IMuxer muxer = RawVideo.CreateMuxer(output, track, leaveOpen: true))
        {
            while (demuxer.ReadPacket() is { } packet)
            {
                using (packet)
                {
                    Assert.Equal(48, packet.Data.Length);
                    muxer.WritePacket(packet);
                }
            }
        }

        Assert.Equal(frames, output.ToArray());
        demuxer.Seek(new MediaTime(2, new Rational(1, 25)));
        using EncodedPacket last = demuxer.ReadPacket()!;
        Assert.Equal(96, last.Data.Span[0]);
    }

    [Fact]
    public void QualityScoresIdenticalAndNoisyFrames()
    {
        using YuvRaster<byte> a = YuvRaster.Create(64, 32, ChromaFormat.Yuv420, YuvColor.Jpeg);
        using YuvRaster<byte> b = YuvRaster.Create(64, 32, ChromaFormat.Yuv420, YuvColor.Jpeg);
        Random random = new(3);
        foreach ((YuvPlane<byte> pa, YuvPlane<byte> pb) in (ValueTuple<YuvPlane<byte>, YuvPlane<byte>>[])[(a.Y, b.Y), (a.U!, b.U!), (a.V!, b.V!)])
        {
            for (int y = 0; y < pa.Height; y++)
            {
                random.NextBytes(pa.Row(y));
                pa.Row(y).CopyTo(pb.Row(y));
            }
        }

        VideoQuality same = new();
        (PlaneScores psnr, PlaneScores ssim) = same.Add(a, b);
        Assert.True(double.IsPositiveInfinity(psnr.Average));
        Assert.Equal(1, ssim.Average, 10);

        // Every luma sample off by 2: MSE 4 on luma, 0 on chroma.
        for (int y = 0; y < b.Y.Height; y++)
        {
            Span<byte> row = b.Y.Row(y);
            for (int x = 0; x < row.Length; x++)
            {
                row[x] = (byte)(row[x] < 128 ? row[x] + 2 : row[x] - 2);
            }
        }

        VideoQuality noisy = new();
        noisy.Add(a, b);
        Assert.Equal(10 * Math.Log10(255.0 * 255 / 4), noisy.Psnr.Y, 6);
        Assert.Equal(10 * Math.Log10(255.0 * 255 / (4.0 * 2048 / 3072)), noisy.Psnr.Average, 6);
        Assert.InRange(noisy.Ssim.Y, 0.9, 1);
        Assert.Equal(VideoQuality.Md5(a), VideoQuality.Md5(a));
        Assert.NotEqual(VideoQuality.Md5(a), VideoQuality.Md5(b));
    }

    [Fact]
    public void MotionJpegTargetSizeFitsEachFrame()
    {
        using YuvVideoFrame frame = DecodeFirst("y4m-420jpeg.y4m");
        int full = Encode(frame, new VideoEncoderOptions { Width = frame.Width, Height = frame.Height, Quality = 100 });
        int target = full / 3;
        int fitted = Encode(frame, new VideoEncoderOptions { Width = frame.Width, Height = frame.Height, TargetFrameBytes = target });
        Assert.InRange(fitted, target / 2, target);
    }

    private static YuvVideoFrame DecodeFirst(string clip)
    {
        using IDemuxer demuxer = MediaFile.OpenDemuxer(Corpus.PathOf(clip));
        using IVideoDecoder decoder = VideoCodecs.CreateDecoder(demuxer.Info.VideoTrack!);
        using EncodedPacket packet = demuxer.ReadPacket()!;
        decoder.Send(packet);
        Assert.True(decoder.TryReceive(out YuvVideoFrame? frame));
        return frame;
    }

    private static int Encode(YuvVideoFrame frame, VideoEncoderOptions options)
    {
        using IVideoEncoder encoder = VideoCodecs.MotionJpeg.CreateEncoder(options, MediaContext.Default);
        encoder.Send(frame);
        Assert.True(encoder.TryReceive(out EncodedPacket? packet));
        using (packet)
        {
            return packet.Data.Length;
        }
    }
}
