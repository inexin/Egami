using System.Buffers.Binary;

using Egami.Colors;
using Egami.Pixels;
using Egami.Video.Codecs;

namespace Egami.Video.Cli.Tests;

/// <summary><c>frames</c> (Build Plan task 9): frames as images, equal to Egami.Video's own decode, picked in every way.</summary>
public sealed class FramesTests
{
    [Theory]
    [InlineData("vp8/v0-altref.webm")]
    [InlineData("vp8/alpha-ramp.webm")]
    [InlineData("vp9/p0-altref.webm")]
    [InlineData("av1/forward-keyframes.webm")]
    [InlineData("mp4/mp4-vp9-opus.mp4")]
    [InlineData("y4m-422.y4m")]
    [InlineData("avi-raw-yuy2.avi")]
    public void EveryFrameEqualsTheLibrarysDecode(string clip)
    {
        using Tool tool = new();
        ToolRun run = tool.Run("frames", Path.Combine(Tool.Corpus, clip), "-o", "shots/{frame:0000}.png");
        Assert.True(run.Code == 0, run.Error);
        List<byte[]> expected = Decode(Path.Combine(Tool.Corpus, clip));
        string[] files = Written(tool, "shots");
        Assert.Equal(expected.Count, files.Length);
        for (int i = 0; i < files.Length; i++)
        {
            Assert.Equal($"{i:0000}.png", Path.GetFileName(files[i]));
            Assert.True(expected[i].AsSpan().SequenceEqual(Pixels(files[i])), $"frame {i}");
        }
    }

    [Theory]
    [InlineData("avi-mjpeg-420-pcm.avi")]
    [InlineData("mkv-mjpeg-pcm.mkv")]
    [InlineData("mp4/mp4-mjpeg.mp4")]
    public void MotionJpegFramesAreThePackets(string clip)
    {
        using Tool tool = new();
        ToolRun run = tool.Run("frames", Path.Combine(Tool.Corpus, clip), "-o", "shots/{frame:000}.jpg");
        Assert.True(run.Code == 0, run.Error);
        List<byte[]> packets = VideoPackets(Path.Combine(Tool.Corpus, clip));
        string[] files = Written(tool, "shots");
        Assert.Equal(packets.Count, files.Length);
        for (int i = 0; i < files.Length; i++)
        {
            Assert.Equal(UpToEoi(packets[i]), File.ReadAllBytes(files[i]));
        }

        // --quality encodes again.
        Assert.Equal(0, tool.Run("frames", Path.Combine(Tool.Corpus, clip), "-o", "q/{frame:000}.jpg", "--quality", "50", "--count", "1").Code);
        Assert.NotEqual(UpToEoi(packets[0]), File.ReadAllBytes(Written(tool, "q")[0]));
    }

    [Fact]
    public void MotionJpegWithoutHuffmanTablesGetsTheStandardOnes()
    {
        // Many AVI cameras leave out DHT and rely on the standard tables of T.81 Annex K.3. Egami's JPEG writer codes with
        // those tables, so a clip with its DHT segments cut out stands for such a camera; the written JPEGs get them back.
        using Tool tool = new();
        MakeClip(tool, "full.mkv", 3, VideoCodecs.MotionJpeg);
        List<byte[]> original = VideoPackets(tool.PathOf("full.mkv"));
        List<byte[]> stripped = [.. original.Select(StripDht)];
        Assert.All(stripped, p => Assert.True(p.Length < original[0].Length + 1000));
        using (IDemuxer demuxer = MediaFile.OpenDemuxer(tool.PathOf("full.mkv")))
        using (IMuxer muxer = MediaFile.CreateMuxer(tool.PathOf("nodht.mkv"), demuxer.Tracks))
        {
            for (int i = 0; i < stripped.Count; i++)
            {
                using EncodedPacket packet = new(demuxer.Tracks[0].Number, stripped[i], new MediaTime(i, new Rational(1, 10))) { IsKeyframe = true };
                muxer.WritePacket(packet);
            }

            muxer.Finish();
        }

        ToolRun run = tool.Run("frames", "nodht.mkv", "-o", "shots/{frame}.jpg");
        Assert.True(run.Code == 0, run.Error);
        for (int i = 0; i < stripped.Count; i++)
        {
            byte[] written = File.ReadAllBytes(tool.PathOf($"shots/{i}.jpg"));
            int sos = IndexOfMarker(stripped[i], 0xDA);
            Assert.Equal(stripped[i][..sos], written[..sos]);
            Assert.Equal(stripped[i][sos..], written[^(stripped[i].Length - sos)..]);
            Assert.Equal(0xC4, written[sos + 1]);
            using Raster expected = Raster.Load(original[i]);
            using Raster actual = Raster.Load(written);
            Assert.Equal(Bytes(expected), Bytes(actual));
        }
    }

    [Fact]
    public void HdrFramesAreToneMappedToSrgb()
    {
        using Tool tool = new();
        string clip = Path.Combine(Tool.Corpus, "vp9/hdr10.mkv");
        ToolRun run = tool.Run("frames", clip, "--at", "0", "-o", "first.png");
        Assert.True(run.Code == 0, run.Error);
        using VideoReader reader = VideoReader.Open(clip);
        using YuvVideoFrame frame = reader.ReadFrame()!;
        Assert.Equal(16, frame.Color!.Value.TransferCharacteristics);
        using Raster<Rgba8> expected = frame.ToRaster<Rgba8>(ColorProfile.Srgb);
        Assert.Equal(Bytes(expected), Pixels(tool.PathOf("first.png")));
    }

    [Fact]
    public void AutoOrientTurnsTheFrames()
    {
        using Tool tool = new();
        string clip = Path.Combine(Tool.Corpus, "mp4/mov-rotate90.mov");
        Assert.Equal(0, tool.Run("frames", clip, "--count", "1", "-o", "plain.png").Code);
        ToolRun run = tool.Run("frames", clip, "--count", "1", "-o", "upright.jpg", "--auto-orient");
        Assert.True(run.Code == 0, run.Error);
        using Raster plain = Raster.Load(tool.PathOf("plain.png"));
        using Raster upright = Raster.Load(tool.PathOf("upright.jpg"));
        Assert.Equal((plain.Height, plain.Width), (upright.Width, upright.Height));
    }

    [Theory]
    [InlineData(new string[0], "0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,22,23,24,25,26,27,28,29")]
    [InlineData(new[] { "--every", "1s" }, "0,10,20")]
    [InlineData(new[] { "--every", "0.75" }, "0,8,15,23")]
    [InlineData(new[] { "--every", "#7" }, "0,7,14,21,28")]
    [InlineData(new[] { "--at", "0.55,2,#7" }, "5,7,20")]
    [InlineData(new[] { "--at", "#3" }, "3")]
    [InlineData(new[] { "--from", "1", "--to", "1.5" }, "10,11,12,13,14")]
    [InlineData(new[] { "--from", "#25" }, "25,26,27,28,29")]
    [InlineData(new[] { "--from", "#20", "--every", "#4" }, "20,24,28")]
    [InlineData(new[] { "--count", "4" }, "0,10,19,29")]
    [InlineData(new[] { "--count", "3", "--from", "1", "--to", "2" }, "10,15,19")]
    [InlineData(new[] { "--count", "1" }, "0")]
    [InlineData(new[] { "--keyframes", "--to", "#3" }, "0,1,2")]
    public void PicksFrames(string[] pick, string expected)
    {
        using Tool tool = new();
        MakeClip(tool, "clip.mkv", 30, RawVideoCodec.Instance);
        ToolRun run = tool.Run(["frames", "clip.mkv", "-o", "out/{name}-{frame:000}.png", .. pick]);
        Assert.True(run.Code == 0, run.Error);
        string[] files = Written(tool, "out");
        Assert.Equal(expected, string.Join(',', files.Select(f => int.Parse(Path.GetFileNameWithoutExtension(f)[5..], System.Globalization.CultureInfo.InvariantCulture))));

        // The frame number in the name is the frame in the file.
        foreach (string file in files)
        {
            Assert.Equal(int.Parse(Path.GetFileNameWithoutExtension(file)[5..], System.Globalization.CultureInfo.InvariantCulture), Shade(file));
        }
    }

    [Theory]
    [InlineData(new string[0], 30)]
    [InlineData(new[] { "--every", "1s" }, 3)]
    [InlineData(new[] { "--at", "0.55,2,#7" }, 3)]
    [InlineData(new[] { "--from", "#25" }, 5)]
    public void MotionJpegPicksTheSameFrames(string[] pick, int count)
    {
        using Tool tool = new();
        MakeClip(tool, "clip.mkv", 30, RawVideoCodec.Instance);
        MakeClip(tool, "mjpeg.mkv", 30, VideoCodecs.MotionJpeg);
        Assert.Equal(0, tool.Run(["frames", "clip.mkv", "-o", "raw/{frame:000}.png", .. pick]).Code);
        Assert.Equal(0, tool.Run(["frames", "mjpeg.mkv", "-o", "jpeg/{frame:000}.jpg", .. pick]).Code);
        Assert.Equal(count, Written(tool, "jpeg").Length);
        Assert.Equal(Written(tool, "raw").Select(Path.GetFileNameWithoutExtension), Written(tool, "jpeg").Select(Path.GetFileNameWithoutExtension));
    }

    [Fact]
    public void KeyframesAreTheKeyPackets()
    {
        using Tool tool = new();
        string clip = Path.Combine(Tool.Corpus, "vp8/v0-keyframes.webm");
        ToolRun run = tool.Run("frames", clip, "--keyframes", "-o", "keys/{frame:0000}.png");
        Assert.True(run.Code == 0, run.Error);
        List<int> keys = [];
        int index = 0;
        using (IDemuxer demuxer = MediaFile.OpenDemuxer(clip))
        {
            while (demuxer.ReadPacket() is { } packet)
            {
                using (packet)
                {
                    if (packet.IsKeyframe)
                    {
                        keys.Add(index);
                    }

                    index++;
                }
            }
        }

        Assert.True(keys.Count > 1);
        Assert.Equal(keys.Select(k => $"{k:0000}"), Written(tool, "keys").Select(Path.GetFileNameWithoutExtension));
        List<byte[]> decoded = Decode(clip);
        Assert.All(keys, k => Assert.Equal(decoded[k], Pixels(tool.PathOf($"keys/{k:0000}.png"))));
    }

    [Fact]
    public void FolderOutputAndBatch()
    {
        using Tool tool = new();
        MakeClip(tool, "in/a.mkv", 3, RawVideoCodec.Instance);
        MakeClip(tool, "in/b.mkv", 2, RawVideoCodec.Instance);
        ToolRun run = tool.Run("frames", "in/*.mkv", "-o", "shots/", "--format", "webp", "--set", "webp.lossless=true");
        Assert.True(run.Code == 0, run.Error);
        Assert.Equal(["a-0000.webp", "a-0001.webp", "a-0002.webp", "b-0000.webp", "b-0001.webp"], Written(tool, "shots").Select(Path.GetFileName));
        Assert.Equal(2, tool.Run("frames", "in/*.mkv", "-o", "shots/{frame}.png").Code);
    }

    [Fact]
    public void TrackSelection()
    {
        using Tool tool = new();
        string clip = Path.Combine(Tool.Corpus, "webm-vp8-opus.webm");
        Assert.Equal(0, tool.Run("frames", clip, "--track", "v:0", "--count", "1", "-o", "one.png").Code);
        ToolRun audio = tool.Run("frames", clip, "--track", "a", "--count", "1", "-o", "two.png");
        Assert.Equal(2, audio.Code);
        Assert.Contains("no video track", audio.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void PipesInAndOut()
    {
        using Tool tool = new();
        MakeClip(tool, "clip.y4m", 30, RawVideoCodec.Instance);
        MakeClip(tool, "mjpeg.mkv", 30, VideoCodecs.MotionJpeg);
        byte[] y4m = File.ReadAllBytes(tool.PathOf("clip.y4m"));

        // One frame to standard output.
        ToolRun one = tool.Run(["frames", "-", "--at", "1", "-o", "-", "--format", "png"], y4m);
        Assert.True(one.Code == 0, one.Error);
        File.WriteAllBytes(tool.PathOf("one.png"), one.Bytes);
        Assert.Equal(10, Shade(tool.PathOf("one.png")));

        // Every frame from standard input, as it arrives.
        ToolRun all = tool.Run(["frames", "-", "--every", "#10", "-o", "piped/{frame:00}.png"], y4m);
        Assert.True(all.Code == 0, all.Error);
        Assert.Equal(["00", "10", "20"], Written(tool, "piped").Select(Path.GetFileNameWithoutExtension));

        // Motion JPEG from standard input: the packets, read once.
        ToolRun jpeg = tool.Run(["frames", "-", "--at", "#4", "-o", "-", "--format", "jpg"], File.ReadAllBytes(tool.PathOf("mjpeg.mkv")));
        Assert.True(jpeg.Code == 0, jpeg.Error);
        Assert.Equal(UpToEoi(VideoPackets(tool.PathOf("mjpeg.mkv"))[4]), jpeg.Bytes);

        // A non-Motion JPEG input to JPEG reads standard input again after the first look.
        ToolRun again = tool.Run(["frames", "-", "--at", "#4", "-o", "-", "--format", "jpg"], y4m);
        Assert.True(again.Code == 0, again.Error);
        Assert.Equal(0xFF, again.Bytes[0]);
    }

    [Theory]
    [InlineData(new[] { "clip.mkv" }, "No output")]
    [InlineData(new[] { "clip.mkv", "-o", "-" }, "-o - writes one image")]
    [InlineData(new[] { "clip.mkv", "-o", "-", "--at", "1" }, "give --format")]
    [InlineData(new[] { "clip.mkv", "-o", "one.png" }, "names one file")]
    [InlineData(new[] { "clip.mkv", "-o", "{frame}.png", "--every", "1", "--at", "2" }, "one way")]
    [InlineData(new[] { "clip.mkv", "-o", "{frame}.png", "--count", "0" }, "--count must be")]
    [InlineData(new[] { "clip.mkv", "-o", "{frame}.png", "--at", "1", "--from", "1" }, "leave out --from")]
    [InlineData(new[] { "clip.mkv", "-o", "{frame}.png", "--every", "0" }, "more than zero")]
    [InlineData(new[] { "clip.mkv", "-o", "{frame}.nope" }, "no extension egami writes")]
    [InlineData(new[] { "clip.mkv", "-o", "{frame}.png", "--format", "nope" }, "not an image format")]
    [InlineData(new[] { "clip.mkv", "-o", "{frame}.png", "--quality", "101" }, "--quality must be")]
    [InlineData(new[] { "clip.mkv", "-o", "{frame}.png", "--from", "#40", "--count", "2" }, "leave no frames")]
    [InlineData(new[] { "clip.mkv", "-o", "{frame}.png", "--at", "#40" }, "past the last frame")]
    [InlineData(new[] { "-", "-o", "{frame}.png", "--count", "2" }, "needs a file")]
    [InlineData(new[] { "-", "-o", "{frame}.png", "--keyframes" }, "needs a file")]
    public void UsageErrors(string[] args, string message)
    {
        using Tool tool = new();
        MakeClip(tool, "clip.mkv", 30, RawVideoCodec.Instance);
        ToolRun run = tool.Run(["frames", .. args], File.ReadAllBytes(tool.PathOf("clip.mkv")));
        Assert.Equal(2, run.Code);
        Assert.Contains(message, run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void TimesPastTheEndAreSkipped()
    {
        using Tool tool = new();
        MakeClip(tool, "clip.mkv", 30, RawVideoCodec.Instance);
        ToolRun run = tool.Run("frames", "clip.mkv", "--at", "1,9", "-o", "{frame}.png");
        Assert.Equal(0, run.Code);
        Assert.Contains("--at 9 is past the end", run.Error, StringComparison.Ordinal);
        Assert.True(File.Exists(tool.PathOf("10.png")));
    }

    [Fact]
    public void ProgressAndCancellation()
    {
        using (Tool tool = new())
        {
            MakeClip(tool, "clip.mkv", 30, RawVideoCodec.Instance);
            ToolRun run = tool.Run("frames", "clip.mkv", "-o", "out/{frame}.png", "--progress", "progress.txt");
            Assert.True(run.Code == 0, run.Error);
            Assert.Contains("progress=end", File.ReadAllText(tool.PathOf("progress.txt")), StringComparison.Ordinal);
        }

        using (Tool tool = new() { Cancellation = new CancellationToken(canceled: true) })
        {
            ToolRun run = tool.Run("frames", Path.Combine(Tool.Corpus, "avi-mjpeg-420-pcm.avi"), "-o", "out/{frame}.png");
            Assert.Equal(130, run.Code);
            Assert.Empty(Directory.GetFiles(tool.Folder, "*", SearchOption.AllDirectories));
        }
    }

    /// <summary>Writes a clip of <paramref name="frames"/> gray frames at 10 fps; frame i has the shade 8 × i.</summary>
    internal static void MakeClip(Tool tool, string name, int frames, VideoCodec codec, int width = 32, int height = 24)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(tool.PathOf(name))!);
        using VideoWriter writer = VideoWriter.Create(tool.PathOf(name), new VideoWriterOptions { Codec = codec, FrameRate = new Rational(10, 1), Quality = 95 });
        for (int i = 0; i < frames; i++)
        {
            byte shade = (byte)(8 * i);
            using Raster<Rgba8> image = Raster.Create(width, height, new Rgba8(shade, shade, shade, 255));
            writer.WriteFrame(image);
        }
    }

    /// <summary>Which frame of <see cref="MakeClip"/> an image is, from its shade.</summary>
    internal static int Shade(string path)
    {
        using Raster image = Raster.Load(path);
        using Raster<Rgba8> rgba = image.As<Rgba8>();
        return (int)Math.Round(rgba.GetPixel(4, 4).R / 8.0);
    }

    /// <summary>The files in a scratch subfolder, sorted.</summary>
    internal static string[] Written(Tool tool, string folder)
    {
        string[] files = Directory.GetFiles(tool.PathOf(folder));
        Array.Sort(files, StringComparer.Ordinal);
        return files;
    }

    /// <summary>Every frame of a clip as Egami.Video decodes it, as RGBA bytes.</summary>
    internal static List<byte[]> Decode(string path)
    {
        using VideoReader reader = VideoReader.Open(path);
        List<byte[]> frames = [];
        foreach (YuvVideoFrame frame in reader.ReadFrames())
        {
            using (frame)
            {
                using Raster<Rgba8> image = frame.ToRaster<Rgba8>();
                frames.Add(Bytes(image));
            }
        }

        return frames;
    }

    /// <summary>An image file's pixels as RGBA bytes.</summary>
    internal static byte[] Pixels(string path)
    {
        using Raster image = Raster.Load(path);
        return Bytes(image);
    }

    internal static byte[] Bytes(Raster image)
    {
        using Raster<Rgba8> rgba = image.As<Rgba8>();
        byte[] bytes = new byte[rgba.Width * rgba.Height * 4];
        rgba.CopyPixelsTo(bytes);
        return bytes;
    }

    /// <summary>The first video track's packets, in file order.</summary>
    internal static List<byte[]> VideoPackets(string path)
    {
        using IDemuxer demuxer = MediaFile.OpenDemuxer(path);
        int track = demuxer.Tracks.First(t => t.Kind == TrackKind.Video).Number;
        List<byte[]> packets = [];
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                if (packet.TrackNumber == track)
                {
                    packets.Add(packet.Data.ToArray());
                }
            }
        }

        return packets;
    }

    /// <summary>A packet up to and with its EOI marker (the padding some files add after it is not part of the JPEG).</summary>
    private static byte[] UpToEoi(byte[] packet)
    {
        int end = packet.Length;
        while (end > 2 && !(packet[end - 2] == 0xFF && packet[end - 1] == 0xD9))
        {
            end--;
        }

        return packet[..end];
    }

    private static byte[] StripDht(byte[] jpeg)
    {
        List<byte> kept = [0xFF, 0xD8];
        int position = 2;
        while (true)
        {
            byte marker = jpeg[position + 1];
            int length = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(position + 2));
            if (marker == 0xDA)
            {
                kept.AddRange(jpeg[position..]);
                return [.. kept];
            }

            if (marker != 0xC4)
            {
                kept.AddRange(jpeg.AsSpan(position, 2 + length));
            }

            position += 2 + length;
        }
    }

    private static int IndexOfMarker(byte[] jpeg, byte marker)
    {
        int position = 2;
        while (jpeg[position + 1] != marker)
        {
            position += 2 + BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(position + 2));
        }

        return position;
    }
}
