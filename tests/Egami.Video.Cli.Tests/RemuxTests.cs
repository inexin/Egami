using System.Security.Cryptography;

namespace Egami.Video.Cli.Tests;

/// <summary><c>remux</c> and <c>formats</c> (Build Plan task 5): packets are copied byte for byte, tracks picked, tags kept.</summary>
public sealed class RemuxTests
{
    [Theory]
    [InlineData("avi-mjpeg-420-pcm.avi", "out.mkv")]
    [InlineData("mkv-mjpeg-pcm.mkv", "out.avi")]
    [InlineData("webm-vp8-opus.webm", "out.mkv")]
    [InlineData("mkv-metadata.mkv", "out.mkv")]
    public void PacketsAreCopiedByteForByte(string clip, string output)
    {
        using Tool tool = new();
        ToolRun run = tool.Run("remux", Path.Combine(Tool.Corpus, clip), "-o", output);
        Assert.True(run.Code == 0, run.Error);
        Assert.Equal(Packets(Path.Combine(Tool.Corpus, clip)), Packets(tool.PathOf(output)));
    }

    [Fact]
    public void TagsChaptersAndAttachmentsAreKept()
    {
        using Tool tool = new();
        Assert.Equal(0, tool.Run("remux", Path.Combine(Tool.Corpus, "mkv-metadata.mkv"), "-o", "out.mkv").Code);
        MediaInfo source = MediaFile.Probe(Path.Combine(Tool.Corpus, "mkv-metadata.mkv"));
        MediaInfo copy = MediaFile.Probe(tool.PathOf("out.mkv"));
        Assert.Equal(source.Tags["TITLE"], copy.Tags["TITLE"]);
        Assert.Equal(source.Chapters.Select(c => c.Title), copy.Chapters.Select(c => c.Title));
        Assert.Equal(source.Attachments.Select(a => a.Name), copy.Attachments.Select(a => a.Name));
    }

    [Fact]
    public void TrackSelection()
    {
        using Tool tool = new();
        string clip = Path.Combine(Tool.Corpus, "avi-mjpeg-420-pcm.avi");
        Assert.Equal(0, tool.Run("remux", clip, "-o", "video.mkv", "--no-audio").Code);
        Assert.Equal([TrackKind.Video], MediaFile.Probe(tool.PathOf("video.mkv")).Tracks.Select(t => t.Kind));
        Assert.Equal(0, tool.Run("remux", clip, "-o", "audio.mkv", "--track", "a").Code);
        Assert.Equal([TrackKind.Audio], MediaFile.Probe(tool.PathOf("audio.mkv")).Tracks.Select(t => t.Kind));
    }

    [Fact]
    public void PipesInAndOut()
    {
        using Tool tool = new();
        byte[] clip = File.ReadAllBytes(Path.Combine(Tool.Corpus, "mkv-mjpeg-pcm.mkv"));
        ToolRun run = tool.Run(["remux", "-", "-o", "-", "--format", "mkv"], clip);
        Assert.True(run.Code == 0, run.Error);
        File.WriteAllBytes(tool.PathOf("piped.mkv"), run.Bytes);
        Assert.Equal(Packets(Path.Combine(Tool.Corpus, "mkv-mjpeg-pcm.mkv")), Packets(tool.PathOf("piped.mkv")));
        ToolRun noFormat = tool.Run(["remux", "-", "-o", "-"], clip);
        Assert.Equal(2, noFormat.Code);
        Assert.Contains("give --format too", noFormat.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void BatchWithTemplate()
    {
        using Tool tool = new();
        tool.Copy("avi-mjpeg-420-pcm.avi", "in/a.avi");
        tool.Copy("avi-tags.avi", "in/b.avi");
        ToolRun run = tool.Run("remux", "in/*.avi", "-o", "out/{name}.mkv");
        Assert.True(run.Code == 0, run.Error);
        Assert.True(File.Exists(tool.PathOf("out/a.mkv")));
        Assert.True(File.Exists(tool.PathOf("out/b.mkv")));
        Assert.Equal(2, tool.Run("remux", "in/*.avi", "-o", "one.mkv").Code);
    }

    [Fact]
    public void RefusesToReplaceItsInput()
    {
        using Tool tool = new();
        tool.Copy("mkv-mjpeg-pcm.mkv", "a.mkv");
        ToolRun run = tool.Run("remux", "a.mkv", "-o", "a.mkv");
        Assert.Equal(2, run.Code);
        Assert.Contains("would replace the input", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void CancelledJobLeavesNoFile()
    {
        using Tool tool = new() { Cancellation = new CancellationToken(canceled: true) };
        ToolRun run = tool.Run("remux", Path.Combine(Tool.Corpus, "avi-mjpeg-420-pcm.avi"), "-o", "out.mkv");
        Assert.Equal(130, run.Code);
        Assert.Empty(Directory.GetFiles(tool.Folder, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Formats()
    {
        using Tool tool = new();
        ToolRun run = tool.Run("formats");
        Assert.Equal(0, run.Code);
        Assert.Contains("RW Matroska", run.Text, StringComparison.Ordinal);
        Assert.Contains("DE Motion JPEG", run.Text, StringComparison.Ordinal);
        Assert.Equal(0, tool.Run("formats", "--json").Code);
    }

    /// <summary>Each packet's track kind, timestamp in seconds and hash, in file order per track.</summary>
    internal static List<string> Packets(string path)
    {
        using IDemuxer demuxer = MediaFile.OpenDemuxer(path);
        Dictionary<int, TrackKind> kinds = demuxer.Tracks.ToDictionary(t => t.Number, t => t.Kind);
        List<string> packets = [];
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                packets.Add($"{kinds[packet.TrackNumber]} {Math.Round(packet.Timestamp.Seconds, 3)} {Convert.ToHexString(SHA256.HashData(packet.Data.Span))[..16]}");
            }
        }

        return [.. packets.OrderBy(p => p, StringComparer.Ordinal)];
    }
}
