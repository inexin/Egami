using Egami.Pixels;
using Egami.Video.Codecs;

namespace Egami.Video.Cli.Tests;

/// <summary><c>encode</c> (Build Plan task 10): clips from images, image sequences and Y4M, round trips through <c>frames</c>.</summary>
public sealed class EncodeTests
{
    [Fact]
    public void ImagesToMotionJpegAndBack()
    {
        using Tool tool = new();
        MakeImages(tool, "in", 6, 64, 48);
        ToolRun run = tool.Run("encode", "in/*.png", "-o", "clip.mkv", "--quality", "90", "--rate", "10");
        Assert.True(run.Code == 0, run.Error);
        VideoTrackInfo track = Video(tool.PathOf("clip.mkv"));
        Assert.Equal(CodecIds.MotionJpeg, track.CodecId);
        Assert.Equal((64, 48), (track.Width, track.Height));
        Assert.Equal(new Rational(10, 1), track.FrameRate);

        Assert.Equal(0, tool.Run("frames", "clip.mkv", "-o", "back/{frame:000}.png").Code);
        Assert.Equal(6, FramesTests.Written(tool, "back").Length);
        Assert.True(Psnr(tool, "in/{0:000}.png", "back/{0:000}.png", 6) > 35);
    }

    [Theory]
    [InlineData("y4m-422.y4m", "out.mkv")]
    [InlineData("y4m-444alpha.y4m", "out.y4m")]
    [InlineData("y4m-420p12.y4m", "out.y4m")]
    [InlineData("y4m-mono.y4m", "out.avi")]
    public void UncompressedKeepsY4mFramesExactly(string clip, string output)
    {
        using Tool tool = new();
        ToolRun run = tool.Run("encode", Path.Combine(Tool.Corpus, clip), "-o", output, "--codec", "raw");
        Assert.True(run.Code == 0, run.Error);
        Assert.Equal(Hashes(Path.Combine(Tool.Corpus, clip)), Hashes(tool.PathOf(output)));
        Assert.Equal(Video(Path.Combine(Tool.Corpus, clip)).FrameRate, Video(tool.PathOf(output)).FrameRate);
    }

    [Fact]
    public void Y4mToMotionJpeg()
    {
        // Images to Y4M (uncompressed), Y4M to Motion JPEG at quality 90, and frames back: the pictures stay above 35 dB.
        using Tool tool = new();
        MakeImages(tool, "in", 6, 64, 48);
        Assert.Equal(0, tool.Run("encode", "in/*.png", "-o", "clip.y4m", "--chroma", "422").Code);
        Assert.Equal(ChromaFormat.Yuv422, Video(tool.PathOf("clip.y4m")).ChromaFormat);
        ToolRun run = tool.Run("encode", "clip.y4m", "-o", "out.avi", "--quality", "90");
        Assert.True(run.Code == 0, run.Error);
        Assert.Equal(CodecIds.MotionJpeg, Video(tool.PathOf("out.avi")).CodecId);
        Assert.Equal(0, tool.Run("frames", "clip.y4m", "-o", "y4m/{frame:000}.png").Code);
        Assert.Equal(0, tool.Run("frames", "out.avi", "-o", "avi/{frame:000}.png").Code);
        Assert.Equal(6, FramesTests.Written(tool, "avi").Length);
        Assert.True(Psnr(tool, "y4m/{0:000}.png", "avi/{0:000}.png", 6) > 35);
    }

    [Fact]
    public void RateOverridesTheVideosOwn()
    {
        using Tool tool = new();
        FramesTests.MakeClip(tool, "clip.y4m", 5, RawVideoCodec.Instance);
        Assert.Equal(0, tool.Run("encode", "clip.y4m", "-o", "fast.mkv", "--rate", "25").Code);
        Assert.Equal(new Rational(25, 1), Video(tool.PathOf("fast.mkv")).FrameRate);
        Assert.Equal(0, tool.Run("encode", "clip.y4m", "-o", "ntsc.mkv", "--rate", "29.97").Code);
        Assert.Equal(new Rational(30000, 1001), Video(tool.PathOf("ntsc.mkv")).FrameRate);
        Assert.Equal(0, tool.Run("encode", "clip.y4m", "-o", "same.mkv").Code);
        Assert.Equal(new Rational(10, 1), Video(tool.PathOf("same.mkv")).FrameRate);
    }

    [Theory]
    [InlineData("seq/{frame:0000}.png", 0)]
    [InlineData("seq/%04d.png", 0)]
    [InlineData("seq/%04d.png", 1)]
    public void ImageSequences(string pattern, int start)
    {
        using Tool tool = new();
        FramesTests.MakeClip(tool, "clip.mkv", 5, RawVideoCodec.Instance);
        Assert.Equal(0, tool.Run("frames", "clip.mkv", "-o", "seq/{frame:0000}.png").Code);
        if (start == 1)
        {
            File.Delete(tool.PathOf("seq/0000.png"));
        }

        ToolRun run = tool.Run("encode", pattern, "-o", "out.mkv", "--codec", "raw");
        Assert.True(run.Code == 0, run.Error);
        Assert.Equal(0, tool.Run("frames", "out.mkv", "-o", "back/{frame}.png").Code);
        string[] back = FramesTests.Written(tool, "back");
        Assert.Equal(5 - start, back.Length);
        Assert.Equal(start, FramesTests.Shade(tool.PathOf("back/0.png")));
    }

    [Fact]
    public void SizesAreFitted()
    {
        using Tool tool = new();
        using (Raster<Rgba8> wide = Raster.Create(64, 32, new Rgba8(255, 255, 255, 255)))
        {
            wide.Save(tool.PathOf("a.png"));
        }

        using (Raster<Rgba8> tall = Raster.Create(32, 64, new Rgba8(255, 255, 255, 255)))
        {
            tall.Save(tool.PathOf("b.png"));
        }

        ToolRun run = tool.Run("encode", "a.png", "b.png", "-o", "first.mkv", "--codec", "raw", "--chroma", "444");
        Assert.True(run.Code == 0, run.Error);
        Assert.Equal((64, 32), (Video(tool.PathOf("first.mkv")).Width, Video(tool.PathOf("first.mkv")).Height));
        Assert.Equal(0, tool.Run("frames", "first.mkv", "-o", "f/{frame}.png").Code);
        using (Raster second = Raster.Load(tool.PathOf("f/1.png")))
        using (Raster<Rgba8> rgba = second.As<Rgba8>())
        {
            // The tall image sits in the middle, black on both sides.
            Assert.True(rgba.GetPixel(2, 16).R < 30);
            Assert.True(rgba.GetPixel(32, 16).R > 220);
        }

        Assert.Equal(0, tool.Run("encode", "a.png", "b.png", "-o", "sized.mkv", "--size", "40x30").Code);
        Assert.Equal((40, 30), (Video(tool.PathOf("sized.mkv")).Width, Video(tool.PathOf("sized.mkv")).Height));
    }

    [Theory]
    [InlineData("420", ChromaFormat.Yuv420)]
    [InlineData("422", ChromaFormat.Yuv422)]
    [InlineData("444", ChromaFormat.Yuv444)]
    public void Chroma(string chroma, ChromaFormat expected)
    {
        using Tool tool = new();
        MakeImages(tool, "in", 2, 32, 32);
        Assert.Equal(0, tool.Run("encode", "in/*.png", "-o", "out.mkv", "--chroma", chroma).Code);
        using VideoReader reader = VideoReader.Open(tool.PathOf("out.mkv"));
        using YuvVideoFrame frame = reader.ReadFrame()!;
        Assert.Equal(expected, frame.Raster.ChromaFormat);
    }

    [Fact]
    public void AudioIsCopiedUpToTheEndOfTheVideo()
    {
        using Tool tool = new();
        MakeImages(tool, "in", 10, 32, 32);
        string sound = Path.Combine(Tool.Corpus, "webm-vp8-opus.webm");
        ToolRun run = tool.Run("encode", "in/*.png", "-o", "clip.mkv", "--rate", "10", "--audio", sound);
        Assert.True(run.Code == 0, run.Error);
        MediaInfo info = MediaFile.Probe(tool.PathOf("clip.mkv"));
        Assert.Equal([TrackKind.Audio, TrackKind.Video], info.Tracks.Select(t => t.Kind));

        List<(MediaTime Time, byte[] Data)> source = AudioPackets(sound);
        List<(MediaTime Time, byte[] Data)> copied = AudioPackets(tool.PathOf("clip.mkv"));
        Assert.NotEmpty(copied);
        Assert.True(copied.Count < source.Count);
        Assert.All(copied, p => Assert.True(p.Time.Seconds < 1.0));
        Assert.Equal(source.Take(copied.Count).Select(p => p.Data), copied.Select(p => p.Data));

        Assert.Equal(2, tool.Run("encode", "in/*.png", "-o", "clip.y4m", "--audio", sound).Code);
        ToolRun silent = tool.Run("encode", "in/*.png", "-o", "clip2.mkv", "--audio", Path.Combine(Tool.Corpus, "y4m-422.y4m"));
        Assert.Equal(2, silent.Code);
        Assert.Contains("has no audio track", silent.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new[] { "in/*.png" }, "No output")]
    [InlineData(new[] { "in/*.png", "-o", "a.mkv", "--codec", "vp8" }, "can decode VP8 but not encode")]
    [InlineData(new[] { "in/*.png", "-o", "a.mkv", "--codec", "nope" }, "not a codec")]
    [InlineData(new[] { "in/*.png", "-o", "a.y4m", "--codec", "mjpeg" }, "Y4M holds uncompressed video only")]
    [InlineData(new[] { "in/*.png", "-o", "a.mkv", "--quality", "101" }, "--quality must be")]
    [InlineData(new[] { "in/*.png", "-o", "a.mkv", "--rate", "0" }, "not a frame rate")]
    [InlineData(new[] { "in/*.png", "-o", "a.mkv", "--rate", "fast" }, "not a frame rate")]
    [InlineData(new[] { "in/*.png", "-o", "a.mkv", "--size", "640" }, "not a size")]
    [InlineData(new[] { "in/*.png", "-o", "a.mkv", "--chroma", "411" }, "not a subsampling")]
    [InlineData(new[] { "in/*.png", "-o", "-" }, "give --format")]
    [InlineData(new[] { "in/*.png", "-o", "-", "--format", "avi" }, "AVI needs a file")]
    [InlineData(new[] { "in/*.png", "clip.y4m", "-o", "a.mkv" }, "from one video")]
    [InlineData(new[] { "none/{frame}.png", "-o", "a.mkv" }, "No files for the image sequence")]
    [InlineData(new[] { "clip.y4m", "-o", "clip.y4m" }, "would replace the input")]
    [InlineData(new[] { "in/*.png", "-o", "a.webm" }, "cannot hold this")]
    public void UsageErrors(string[] args, string message)
    {
        using Tool tool = new();
        MakeImages(tool, "in", 2, 16, 16);
        FramesTests.MakeClip(tool, "clip.y4m", 2, RawVideoCodec.Instance);
        ToolRun run = tool.Run(["encode", .. args]);
        Assert.Equal(2, run.Code);
        Assert.Contains(message, run.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(tool.PathOf("a.mkv")));
    }

    [Fact]
    public void ProgressAndCancellation()
    {
        using (Tool tool = new())
        {
            MakeImages(tool, "in", 3, 16, 16);
            ToolRun run = tool.Run("encode", "in/*.png", "-o", "out.mkv", "--progress", "progress.txt");
            Assert.True(run.Code == 0, run.Error);
            Assert.Contains("progress=end", File.ReadAllText(tool.PathOf("progress.txt")), StringComparison.Ordinal);
        }

        using (Tool tool = new())
        {
            MakeImages(tool, "in", 3, 16, 16);
            tool.Cancellation = new CancellationToken(canceled: true);
            ToolRun run = tool.Run("encode", "in/*.png", "-o", "out.mkv");
            Assert.Equal(130, run.Code);
            Assert.Empty(Directory.GetFiles(tool.Folder, "*.mkv", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetFiles(tool.Folder, "*tmp", SearchOption.AllDirectories));
        }
    }

    /// <summary>Writes <paramref name="count"/> smooth test images, <c>{folder}/000.png</c> on.</summary>
    internal static void MakeImages(Tool tool, string folder, int count, int width, int height)
    {
        Directory.CreateDirectory(tool.PathOf(folder));
        for (int i = 0; i < count; i++)
        {
            using Raster<Rgba8> image = Raster.Create<Rgba8>(width, height);
            for (int y = 0; y < height; y++)
            {
                Span<Rgba8> row = image.Row(y);
                for (int x = 0; x < width; x++)
                {
                    row[x] = new Rgba8((byte)(x * 255 / width), (byte)(y * 255 / height), (byte)((i * 40) + ((x + y) / 2)), 255);
                }
            }

            image.Save(tool.PathOf($"{folder}/{i:000}.png"));
        }
    }

    /// <summary>The average PSNR in dB between two numbered image sets, as Egami.Video's <see cref="VideoQuality"/> measures it.</summary>
    internal static double Psnr(Tool tool, string reference, string distorted, int count)
    {
        VideoQuality quality = new();
        for (int i = 0; i < count; i++)
        {
            using Raster a = Raster.Load(tool.PathOf(string.Format(System.Globalization.CultureInfo.InvariantCulture, reference, i)));
            using Raster b = Raster.Load(tool.PathOf(string.Format(System.Globalization.CultureInfo.InvariantCulture, distorted, i)));
            using YuvRaster ya = YuvRaster.FromRaster(a, ChromaFormat.Yuv444, YuvColor.Jpeg);
            using YuvRaster yb = YuvRaster.FromRaster(b, ChromaFormat.Yuv444, YuvColor.Jpeg);
            quality.Add(ya, yb);
        }

        return quality.Psnr.Average;
    }

    /// <summary>The MD5 of every decoded frame's planes.</summary>
    internal static List<string> Hashes(string path)
    {
        using VideoReader reader = VideoReader.Open(path);
        List<string> hashes = [];
        foreach (YuvVideoFrame frame in reader.ReadFrames())
        {
            using (frame)
            {
                hashes.Add(VideoQuality.Md5(frame.Raster));
            }
        }

        return hashes;
    }

    internal static VideoTrackInfo Video(string path) => MediaFile.Probe(path).Tracks.OfType<VideoTrackInfo>().First();

    private static List<(MediaTime, byte[])> AudioPackets(string path)
    {
        using IDemuxer demuxer = MediaFile.OpenDemuxer(path);
        int track = demuxer.Tracks.First(t => t.Kind == TrackKind.Audio).Number;
        List<(MediaTime, byte[])> packets = [];
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                if (packet.TrackNumber == track)
                {
                    packets.Add((packet.Timestamp, packet.Data.ToArray()));
                }
            }
        }

        return packets;
    }
}
