using System.Buffers.Binary;
using System.Text;

namespace Egami.Video.Cli.Tests;

/// <summary>
/// Fragmented MP4 and streaming segments (Video CLI Build Plan task 26): <c>--fragment</c> on <c>remux</c> and <c>encode</c>,
/// <c>segment --hls --dash</c>, and ffmpeg mode's <c>-movflags</c> and <c>-frag_duration</c>.
/// </summary>
public sealed class FragmentTests
{
    [Fact]
    public void RemuxFragmentWritesMoofsAndKeepsThePackets()
    {
        using Tool tool = new();
        string clip = tool.Copy("webm-vp8-opus.webm");
        ToolRun run = tool.Run("remux", clip, "--fragment", "-o", "out.mp4");
        Assert.True(run.Code == 0, run.Error);

        List<string> boxes = TopLevelBoxes(tool.PathOf("out.mp4"));
        Assert.Equal("ftyp", boxes[0]);
        Assert.Equal("moov", boxes[1]);
        Assert.Contains("moof", boxes);
        Assert.Equal(Packets(tool.PathOf(clip)), Packets(tool.PathOf("out.mp4")));
    }

    [Fact]
    public void ShorterFragmentsMakeMoreOfThem()
    {
        using Tool tool = new();
        string clip = tool.Copy(Path.Combine("vp8", "v0-keyframes.webm"));
        Assert.Equal(0, tool.Run("remux", clip, "--fragment", "-o", "default.mp4").Code);
        ToolRun run = tool.Run("remux", clip, "--fragment=keyframe", "-o", "short.mp4");
        Assert.True(run.Code == 0, run.Error);
        Assert.True(
            TopLevelBoxes(tool.PathOf("short.mp4")).Count(b => b == "moof") > TopLevelBoxes(tool.PathOf("default.mp4")).Count(b => b == "moof"),
            "--fragment=keyframe should start a fragment at every keyframe");
        Assert.Equal(5, TopLevelBoxes(tool.PathOf("short.mp4")).Count(b => b == "moof"));
    }

    [Fact]
    public void EveryKeyframeWithoutVideoIsOneFragment()
    {
        using Tool tool = new();
        string clip = tool.Copy("mp4/mp4-flac.mp4");
        ToolRun run = tool.Run("remux", clip, "--fragment=keyframe", "-o", "out.mp4");
        Assert.True(run.Code == 0, run.Error);
        Assert.Single(TopLevelBoxes(tool.PathOf("out.mp4")), b => b == "moof");
    }

    [Fact]
    public void FragmentNeedsAnMp4Output()
    {
        using Tool tool = new();
        string clip = tool.Copy("webm-vp8-opus.webm");
        ToolRun run = tool.Run("remux", clip, "--fragment", "-o", "out.mkv");
        Assert.Equal(2, run.Code);
        Assert.Contains("fragmented MP4", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void EncodeFragmentWritesFragmentedMp4()
    {
        using Tool tool = new();
        EncodeTests.MakeImages(tool, "in", 6, 64, 48);
        ToolRun run = tool.Run("encode", "in/*.png", "--rate", "10", "--fragment=0.2", "-o", "clip.mp4");
        Assert.True(run.Code == 0, run.Error);
        Assert.Contains("moof", TopLevelBoxes(tool.PathOf("clip.mp4")));
        Assert.Equal(6, Packets(tool.PathOf("clip.mp4")));
    }

    [Fact]
    public void SegmentWritesHlsAndDash()
    {
        using Tool tool = new();
        string clip = tool.Copy("webm-vp8-opus.webm");
        ToolRun run = tool.Run("segment", clip, "--hls", "--dash", "-o", "stream/");
        Assert.True(run.Code == 0, run.Error);

        string folder = tool.PathOf("stream");
        string master = File.ReadAllText(Path.Combine(folder, "master.m3u8"));
        Assert.StartsWith("#EXTM3U", master, StringComparison.Ordinal);
        Assert.Contains("<MPD", File.ReadAllText(Path.Combine(folder, "manifest.mpd")), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(folder, "video1-init.mp4")));
        Assert.True(File.Exists(Path.Combine(folder, "audio2-init.mp4")));
        Assert.Single(Directory.GetFiles(folder, "video1-*.m4s"));
    }

    [Fact]
    public void SegmentsStartAtKeyframesAfterEvery()
    {
        using Tool tool = new();
        string clip = tool.Copy(Path.Combine("vp8", "v0-keyframes.webm"));
        ToolRun run = tool.Run("segment", clip, "--hls", "--every", "0.1", "-o", "stream/");
        Assert.True(run.Code == 0, run.Error);

        // The clip has 5 keyframes, so 5 segments; the playlist lists each one.
        Assert.Equal(5, Directory.GetFiles(tool.PathOf("stream"), "video1-*.m4s").Length);
        Assert.Equal(5, File.ReadAllLines(tool.PathOf("stream/video1.m3u8")).Count(l => l.EndsWith(".m4s", StringComparison.Ordinal)));
    }

    [Fact]
    public void SegmentHlsOnlyWritesNoManifest()
    {
        using Tool tool = new();
        string clip = tool.Copy("webm-vp8-opus.webm");
        ToolRun run = tool.Run("segment", clip, "--hls", "-o", "out/{name}");
        Assert.True(run.Code == 0, run.Error);
        string folder = tool.PathOf("out/webm-vp8-opus");
        Assert.True(File.Exists(Path.Combine(folder, "master.m3u8")));
        Assert.False(File.Exists(Path.Combine(folder, "manifest.mpd")));
    }

    [Fact]
    public void SegmentHlsRefusesSplitPoints()
    {
        using Tool tool = new();
        string clip = tool.Copy("webm-vp8-opus.webm");
        ToolRun run = tool.Run("segment", clip, "--hls", "--at", "0.5", "-o", "stream/");
        Assert.Equal(2, run.Code);
        Assert.Contains("--every", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void FfmpegModeFragmentsAStreamCopy()
    {
        using Tool tool = new();
        tool.Copy("webm-vp8-opus.webm", "in.webm");
        ToolRun run = tool.Run("ffmpeg", "-i", "in.webm", "-c", "copy", "-movflags", "frag_keyframe+empty_moov+default_base_moof", "out.mp4");
        Assert.True(run.Code == 0, run.Error);
        Assert.Contains("moof", TopLevelBoxes(tool.PathOf("out.mp4")));
    }

    /// <summary>The types of a file's top-level boxes, in order.</summary>
    private static List<string> TopLevelBoxes(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        List<string> boxes = [];
        long at = 0;
        while (at + 8 <= data.Length)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan((int)at));
            string type = Encoding.ASCII.GetString(data, (int)at + 4, 4);
            if (size == 1)
            {
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan((int)at + 8));
            }
            else if (size == 0)
            {
                size = data.Length - at;
            }

            boxes.Add(type);
            at += size;
        }

        return boxes;
    }

    private static int Packets(string path)
    {
        using IDemuxer demuxer = MediaFile.OpenDemuxer(path);
        int count = 0;
        while (demuxer.ReadPacket() is { } packet)
        {
            packet.Dispose();
            count++;
        }

        return count;
    }
}
