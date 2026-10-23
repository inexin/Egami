namespace Egami.Video.Cli.Tests;

/// <summary><c>timestamps</c> (Build Plan task 7): shift, constant frame rate, repair and start at zero, without decoding.</summary>
public sealed class TimestampsTests
{
    [Fact]
    public void ShiftMovesOnlyTheTracksPicked()
    {
        using Tool tool = new();
        string clip = Path.Combine(Tool.Corpus, "mkv-mjpeg-pcm.mkv");
        ToolRun run = tool.Run("timestamps", clip, "--shift", "0.5", "--track", "a", "-o", "out.mkv", "-v");
        Assert.True(run.Code == 0, run.Error);
        Assert.Contains("shifted by 0:00:00.500", run.Error, StringComparison.Ordinal);
        List<CutTests.Packet> source = CutTests.Read(clip);
        List<CutTests.Packet> output = CutTests.Read(tool.PathOf("out.mkv"));
        Assert.Equal(Times(source, TrackKind.Video), Times(output, TrackKind.Video));
        Assert.Equal(Times(source, TrackKind.Audio).Select(t => Math.Round(t + 0.5, 3)), Times(output, TrackKind.Audio));
        Assert.Equal(source.Select(p => p.Hash).Order(), output.Select(p => p.Hash).Order());
    }

    [Theory]
    [InlineData("--shift", "-0.25")]
    [InlineData("--shift=-0:00.25", null)]
    public void NegativeShiftAndStartAtZero(string option, string? value)
    {
        using Tool tool = new();
        string clip = Path.Combine(Tool.Corpus, "mkv-mjpeg-pcm.mkv");
        string[] args = value is null ? ["timestamps", clip, option, "--track", "v", "-o", "out.mkv"] : ["timestamps", clip, option, value, "--track", "v", "-o", "out.mkv"];
        ToolRun run = tool.Run(args);
        Assert.True(run.Code == 0, run.Error);
        Assert.Contains("before 0", run.Error, StringComparison.Ordinal);
        Assert.Equal(-0.25, CutTests.Read(tool.PathOf("out.mkv")).Where(p => p.Kind == TrackKind.Video).Min(p => p.Time), 3);

        ToolRun zero = tool.Run([.. args[..^2], "--start-at-zero", "-o", "zero.mkv"]);
        Assert.True(zero.Code == 0, zero.Error);
        List<CutTests.Packet> output = CutTests.Read(tool.PathOf("zero.mkv"));
        Assert.Equal(0, output.Min(p => p.Time), 3);
        Assert.Equal(0.25, output.Where(p => p.Kind == TrackKind.Audio).Min(p => p.Time), 3);
    }

    [Fact]
    public void RateRetimesVideoInPresentationOrder()
    {
        using Tool tool = new();
        string clip = Path.Combine(Tool.Corpus, "mkv-mjpeg-pcm.mkv");
        ToolRun run = tool.Run("timestamps", clip, "--rate", "50", "-o", "out.mkv");
        Assert.True(run.Code == 0, run.Error);
        List<CutTests.Packet> output = CutTests.Read(tool.PathOf("out.mkv"));
        Assert.Equal(Enumerable.Range(0, 50).Select(i => Math.Round(i * 0.02, 3)), Times(output, TrackKind.Video));
        Assert.Equal(Times(CutTests.Read(clip), TrackKind.Audio), Times(output, TrackKind.Audio));
        Assert.Equal(new Rational(50, 1), MediaFile.Probe(tool.PathOf("out.mkv")).VideoTrack!.FrameRate);
    }

    [Fact]
    public void RateKeepsBFramesInDecodeOrder()
    {
        using Tool tool = new();
        ToolRun run = tool.Run("timestamps", Path.Combine(Tool.Corpus, "mp4", "mp4-h264-aac.mp4"), "--rate", "30000/1001", "-o", "out.mp4");
        Assert.True(run.Code == 0, run.Error);
        using IDemuxer demuxer = MediaFile.OpenDemuxer(tool.PathOf("out.mp4"));
        int video = demuxer.Tracks.First(t => t.Kind == TrackKind.Video).Number;
        List<(double Pts, double Dts)> times = [];
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                if (packet.TrackNumber == video)
                {
                    times.Add((packet.Timestamp.Seconds, (packet.DecodeTimestamp ?? packet.Timestamp).Seconds));
                }
            }
        }

        Assert.Equal(Enumerable.Range(0, times.Count).Select(i => Math.Round(i * 1001 / 30000.0, 3)), times.Select(t => Math.Round(t.Pts, 3)).Order());
        Assert.All(times, t => Assert.True(t.Dts <= t.Pts + 1e-6));
        Assert.True(times.Zip(times.Skip(1)).All(p => p.Second.Dts > p.First.Dts));
        if (Ffprobe.Packets(tool.PathOf("out.mp4")) is not null)
        {
            Assert.Empty(Ffprobe.Warnings(tool.PathOf("out.mp4")));
        }
    }

    [Fact]
    public void FixRepairsDuplicatedAndBackwardTimestamps()
    {
        using Tool tool = new();
        string broken = tool.PathOf("broken.mkv");
        WriteBroken(Path.Combine(Tool.Corpus, "vp8", "v0-keyframes.webm"), broken);
        List<double> before = Times(CutTests.Read(broken), TrackKind.Video);
        Assert.False(before.Zip(before.Skip(1)).All(p => p.Second > p.First));

        ToolRun run = tool.Run("timestamps", "broken.mkv", "--fix", "-o", "fixed.mkv", "-v");
        Assert.True(run.Code == 0, run.Error);
        Assert.Contains("timestamps made increasing", run.Error, StringComparison.Ordinal);
        List<CutTests.Packet> fixedPackets = CutTests.Read(tool.PathOf("fixed.mkv"));
        List<double> after = [.. fixedPackets.Select(p => p.Time)];
        Assert.True(after.Zip(after.Skip(1)).All(p => p.Second > p.First), string.Join(", ", after));
        Assert.Equal(CutTests.Read(broken).Select(p => p.Hash), fixedPackets.Select(p => p.Hash));
        if (Ffprobe.Packets(tool.PathOf("fixed.mkv")) is { } probed)
        {
            Assert.Empty(Ffprobe.Warnings(tool.PathOf("fixed.mkv")));
            Assert.True(probed.Zip(probed.Skip(1)).All(p => p.Second.Time > p.First.Time));
        }
    }

    [Fact]
    public void FixFillsMissingDurations()
    {
        using Tool tool = new();
        WriteBroken(Path.Combine(Tool.Corpus, "vp8", "v0-keyframes.webm"), tool.PathOf("broken.mkv"));
        // Matroska keeps block durations only in a default duration, so the filled ones are checked in MP4, which stores each.
        ToolRun run = tool.Run("timestamps", "broken.mkv", "--fix", "-o", "fixed.mp4", "-v");
        Assert.True(run.Code == 0, run.Error);
        Assert.Contains("durations filled in", run.Error, StringComparison.Ordinal);
        using IDemuxer demuxer = MediaFile.OpenDemuxer(tool.PathOf("fixed.mp4"));
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                Assert.NotNull(packet.Duration);
            }
        }
    }

    [Fact]
    public void PipesInAndOut()
    {
        using Tool tool = new();
        byte[] clip = File.ReadAllBytes(Path.Combine(Tool.Corpus, "webm-vp8-opus.webm"));
        ToolRun run = tool.Run(["timestamps", "-", "--start-at-zero", "-o", "-", "--format", "mkv"], clip);
        Assert.True(run.Code == 0, run.Error);
        File.WriteAllBytes(tool.PathOf("out.mkv"), run.Bytes);
        Assert.Equal(0, CutTests.Read(tool.PathOf("out.mkv")).Min(p => p.Time), 3);
    }

    [Fact]
    public void UsageErrors()
    {
        using Tool tool = new();
        string clip = Path.Combine(Tool.Corpus, "mkv-mjpeg-pcm.mkv");
        ToolRun nothing = tool.Run("timestamps", clip, "-o", "out.mkv");
        Assert.Equal(2, nothing.Code);
        Assert.Contains("needs --shift, --rate, --fix or --start-at-zero", nothing.Error, StringComparison.Ordinal);
        ToolRun rate = tool.Run("timestamps", clip, "--rate", "fast", "-o", "out.mkv");
        Assert.Equal(2, rate.Code);
        Assert.Contains("is not a frame rate", rate.Error, StringComparison.Ordinal);
        Assert.Equal(2, tool.Run("timestamps", clip, "--rate", "0", "-o", "out.mkv").Code);
        Assert.Equal(2, tool.Run("timestamps", clip, "--rate", "25/0", "-o", "out.mkv").Code);
        ToolRun frame = tool.Run("timestamps", clip, "--shift", "#3", "-o", "out.mkv");
        Assert.Equal(2, frame.Code);
        Assert.Contains("not a frame number", frame.Error, StringComparison.Ordinal);
        ToolRun track = tool.Run("timestamps", clip, "--shift", "1", "--track", "s", "-o", "out.mkv");
        Assert.Equal(2, track.Code);
        Assert.Contains("matches no track", track.Error, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(tool.Folder, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void RateWithoutVideoWarns()
    {
        using Tool tool = new();
        ToolRun run = tool.Run("timestamps", Path.Combine(Tool.Corpus, "mkv-mjpeg-pcm.mkv"), "--rate", "25", "--track", "a", "-o", "out.mkv");
        Assert.Equal(0, run.Code);
        Assert.Contains("no video track is picked", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void CancelledJobLeavesNoFile()
    {
        using Tool tool = new() { Cancellation = new CancellationToken(canceled: true) };
        ToolRun run = tool.Run("timestamps", Path.Combine(Tool.Corpus, "mkv-mjpeg-pcm.mkv"), "--fix", "-o", "out.mkv");
        Assert.Equal(130, run.Code);
        Assert.Empty(Directory.GetFiles(tool.Folder, "*", SearchOption.AllDirectories));
    }

    private static List<double> Times(List<CutTests.Packet> packets, TrackKind kind) => [.. packets.Where(p => p.Kind == kind).Select(p => Math.Round(p.Time, 3))];

    /// <summary>Copies a clip's packets into Matroska with broken times: no durations, a repeated timestamp and one that goes back.</summary>
    private static void WriteBroken(string source, string path)
    {
        using IDemuxer demuxer = MediaFile.OpenDemuxer(source);
        TrackInfo[] tracks = [.. demuxer.Tracks.Select(t => t is VideoTrackInfo v ? v with { FrameRate = null } : t)];
        using FileStream file = File.Create(path);
        using (IMuxer muxer = MatroskaFormat.Instance.CreateMuxer(file, tracks, MediaMetadata.Empty, MediaContext.Default, leaveOpen: true))
        {
            int index = 0;
            MediaTime previous = MediaTime.Zero;
            while (demuxer.ReadPacket() is { } packet)
            {
                using (packet)
                {
                    MediaTime time = packet.Timestamp;
                    packet.Duration = null;
                    if (index is 5 or 6)
                    {
                        packet.Timestamp = previous;
                    }
                    else if (index == 12)
                    {
                        packet.Timestamp = MediaTime.FromSeconds(0.1);
                    }

                    previous = time;
                    muxer.WritePacket(packet);
                    index++;
                }
            }

            muxer.Finish();
        }
    }
}
