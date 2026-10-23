using System.Diagnostics;
using System.Security.Cryptography;

namespace Egami.Video.Cli.Tests;

/// <summary><c>extract</c> and <c>embed</c> (Build Plan task 8): tracks and attachments out and back in, packets unchanged.</summary>
public sealed class TrackTests
{
    [Theory]
    [InlineData("mkv-metadata.mkv", "video.mkv", "audio.mka")]
    [InlineData("webm-vp8-opus.webm", "video.ivf", "audio.mka")]
    [InlineData("avi-mjpeg-420-pcm.avi", "video.avi", "audio.mka")]
    [InlineData("mp4/mp4-vp9-opus.mp4", "video.ivf", "audio.mka")]
    [InlineData("mp4/mp4-h264-aac.mp4", "video.mp4", "audio.mkv")]
    public void ExtractThenEmbedGivesBackThePackets(string clip, string video, string audio)
    {
        using Tool tool = new();
        string source = Path.Combine(Tool.Corpus, clip);
        Assert.Equal(0, Ok(tool.Run("extract", source, "--track", "v", "-o", video)));
        Assert.Equal(0, Ok(tool.Run("extract", source, "--track", "a", "-o", audio)));
        Assert.Equal(0, Ok(tool.Run("embed", video, "--add", audio, "-o", "back.mkv")));
        SamePackets(source, tool.PathOf("back.mkv"));
        MediaInfo original = MediaFile.Probe(source);
        MediaInfo back = MediaFile.Probe(tool.PathOf("back.mkv"));
        foreach ((string key, string value) in original.Tags.Where(t => t.Key is "TITLE" or "ARTIST" or "COMMENT"))
        {
            Assert.Equal(value, back.Tags[key]);
        }

        Assert.Equal(original.Chapters.Select(c => c.Title), back.Chapters.Select(c => c.Title));
        FfprobeReads(tool.PathOf(video), tool.PathOf(audio), tool.PathOf("back.mkv"));
    }

    [Theory]
    [InlineData("webm-vp8-opus.webm", "v", "ivf")]
    [InlineData("webm-vp8-opus.webm", "a", "ogg")]
    [InlineData("mkv-metadata.mkv", "v", "mkv")]
    [InlineData("mkv-metadata.mkv", "a", "wav")]
    [InlineData("mkv-raw-i420.mkv", "v", "y4m")]
    [InlineData("avi-raw-bgr24.avi", "v", "mkv")]
    [InlineData("mkv-ac3.mkv", "a", "mka")]
    [InlineData("audio/flac-tags-picture.flac", "a", "flac")]
    [InlineData("mp4/mp4-av1.mp4", "v", "ivf")]
    public void ExtractPicksAContainerForTheCodec(string clip, string track, string extension)
    {
        using Tool tool = new();
        string source = Path.Combine(Tool.Corpus, clip);
        Assert.Equal(0, Ok(tool.Run("extract", source, "--track", track, "-o", "out/")));
        string file = tool.PathOf($"out/{Path.GetFileNameWithoutExtension(clip)}.{extension}");
        Assert.True(File.Exists(file), string.Join(", ", Directory.GetFiles(tool.PathOf("out"))));
        TrackKind kind = track == "v" ? TrackKind.Video : TrackKind.Audio;
        Assert.Equal(Bytes(source, kind), Bytes(file, kind));
        FfprobeReads(file);
    }

    [Fact]
    public void OpusThroughOggKeepsThePackets()
    {
        using Tool tool = new();
        string source = Path.Combine(Tool.Corpus, "webm-vp8-opus.webm");
        Assert.Equal(0, Ok(tool.Run("extract", source, "-t", "a", "-o", "audio.ogg")));
        Assert.Equal(Hashes(source, TrackKind.Audio), Hashes(tool.PathOf("audio.ogg"), TrackKind.Audio));
        FfprobeReads(tool.PathOf("audio.ogg"));
    }

    [Fact]
    public void PcmThroughWaveKeepsTheSamples()
    {
        using Tool tool = new();
        string source = Path.Combine(Tool.Corpus, "mkv-metadata.mkv");
        Assert.Equal(0, Ok(tool.Run("extract", source, "-t", "a", "-o", "audio.wav")));
        Assert.Equal(Bytes(source, TrackKind.Audio), Bytes(tool.PathOf("audio.wav"), TrackKind.Audio));
    }

    [Fact]
    public void SeveralTracksNeedAFolderOrATemplate()
    {
        using Tool tool = new();
        string source = Path.Combine(Tool.Corpus, "webm-vp8-opus.webm");
        ToolRun none = tool.Run("extract", source, "-o", "out.mkv");
        Assert.Equal(2, none.Code);
        Assert.Contains("give --track", none.Error, StringComparison.Ordinal);
        ToolRun one = tool.Run("extract", source, "-t", "v,a", "-o", "out.mkv");
        Assert.Equal(2, one.Code);
        Assert.Contains("{track}", one.Error, StringComparison.Ordinal);
        Assert.Equal(0, Ok(tool.Run("extract", source, "-t", "v,a", "-o", "{name}-{track}.mkv")));
        Assert.True(File.Exists(tool.PathOf("webm-vp8-opus-1.mkv")));
        Assert.True(File.Exists(tool.PathOf("webm-vp8-opus-2.mkv")));
        Assert.Equal(0, Ok(tool.Run("extract", source, "-t", "v,a", "-o", "dir/")));
        Assert.Equal(2, Directory.GetFiles(tool.PathOf("dir")).Length);
    }

    [Fact]
    public void ExtractToAContainerThatCannotHoldTheTrackFails()
    {
        using Tool tool = new();
        ToolRun run = tool.Run("extract", Path.Combine(Tool.Corpus, "mkv-metadata.mkv"), "-t", "v", "-o", "video.ivf");
        Assert.Equal(1, run.Code);
        Assert.Contains("Write .mkv", run.Error, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(tool.Folder));
    }

    [Fact]
    public void ExtractPipesInAndOut()
    {
        using Tool tool = new();
        byte[] clip = File.ReadAllBytes(Path.Combine(Tool.Corpus, "webm-vp8-opus.webm"));
        ToolRun run = tool.Run(["extract", "-", "-t", "v", "-o", "-", "--format", "ivf"], clip);
        Assert.True(run.Code == 0, run.Error);
        File.WriteAllBytes(tool.PathOf("piped.ivf"), run.Bytes);
        Assert.Equal(50, RemuxTests.Packets(tool.PathOf("piped.ivf")).Count);
        Assert.Equal(2, tool.Run(["extract", "-", "-t", "v,a", "-o", "dir/"], clip).Code);
    }

    [Fact]
    public void ExtractAttachments()
    {
        using Tool tool = new();
        string source = Path.Combine(Tool.Corpus, "mkv-metadata.mkv");
        byte[] cover = MediaFile.Probe(source).Attachments.Single(a => a.Name == "cover.jpg").Data.ToArray();
        Assert.Equal(0, Ok(tool.Run("extract", source, "--attachment", "all", "-o", "all/")));
        Assert.Equal(cover, File.ReadAllBytes(tool.PathOf("all/cover.jpg")));
        Assert.Equal(0, Ok(tool.Run("extract", source, "--cover", "-o", "art.jpg")));
        Assert.Equal(cover, File.ReadAllBytes(tool.PathOf("art.jpg")));
        Assert.Equal(0, Ok(tool.Run("extract", source, "--attachment", "0", "-o", "first.jpg")));
        Assert.Equal(0, Ok(tool.Run("extract", source, "--attachment", "cover.jpg", "-o", "named.jpg")));
        ToolRun piped = tool.Run("extract", source, "--cover", "-o", "-");
        Assert.Equal(cover, piped.Bytes);

        ToolRun missing = tool.Run("extract", source, "--attachment", "font.ttf", "-o", "x/");
        Assert.Equal(2, missing.Code);
        Assert.Contains("cover.jpg", missing.Error, StringComparison.Ordinal);
        ToolRun both = tool.Run("extract", source, "--attachment", "all", "-t", "v", "-o", "x/");
        Assert.Equal(2, both.Code);
        ToolRun noCover = tool.Run("extract", Path.Combine(Tool.Corpus, "webm-vp8-opus.webm"), "--cover", "-o", "c.jpg");
        Assert.Equal(1, noCover.Code);
        Assert.Contains("no cover art", noCover.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void FlacPictureIsCoverArt()
    {
        using Tool tool = new();
        Assert.Equal(0, Ok(tool.Run("extract", Path.Combine(Tool.Corpus, "audio/flac-tags-picture.flac"), "--cover", "-o", "pictures/")));
        Assert.Single(Directory.GetFiles(tool.PathOf("pictures")));
    }

    [Fact]
    public void EmbedSetsTrackFieldsAttachmentsAndCover()
    {
        using Tool tool = new();
        string source = Path.Combine(Tool.Corpus, "mkv-metadata.mkv");
        File.WriteAllBytes(tool.PathOf("font.ttf"), [1, 2, 3, 4]);
        tool.CopyImage("analysis/tiny-a.png", "new.png");
        ToolRun run = tool.Run(
            "embed", source, "--add", Path.Combine(Tool.Corpus, "webm-vp8-opus.webm") + ":a:0", "--attach", "font.ttf", "--cover", "new.png",
            "--language", "3=eng", "--name", "a:0=Tone two", "--default", "a:1", "-o", "out.mkv");
        Assert.True(run.Code == 0, run.Error);
        MediaInfo info = MediaFile.Probe(tool.PathOf("out.mkv"));
        Assert.Equal([TrackKind.Video, TrackKind.Audio, TrackKind.Audio], info.Tracks.Select(t => t.Kind));
        Assert.Equal("eng", info.Tracks[2].Language);
        Assert.Equal("Tone two", info.Tracks[1].Name);
        Assert.False(info.Tracks[1].IsDefault);
        Assert.True(info.Tracks[2].IsDefault);
        Assert.Contains(info.Attachments, a => a.Name == "font.ttf" && a.MediaType == "font/ttf");
        AttachmentInfo cover = Assert.Single(info.Attachments, a => a.IsCoverArt);
        Assert.Equal("cover.png", cover.Name);
        Assert.Equal("image/png", cover.MediaType);
        Assert.Equal("Corpus clip", info.Tags["TITLE"]);
        Assert.Equal(2, info.Chapters.Count);
        FfprobeReads(tool.PathOf("out.mkv"));
    }

    [Fact]
    public void EmbedInterleavesByTime()
    {
        using Tool tool = new();
        string video = tool.Copy("webm-vp8-opus.webm", "a.webm");
        Assert.Equal(0, Ok(tool.Run("embed", video, "--add", Path.Combine(Tool.Corpus, "webm-vp8-opus.webm") + ":a", "--track", "v", "-o", "out.webm")));
        using IDemuxer demuxer = MediaFile.OpenDemuxer(tool.PathOf("out.webm"));
        double last = double.NegativeInfinity;
        double worst = 0;
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                worst = Math.Max(worst, last - packet.Timestamp.Seconds);
                last = Math.Max(last, packet.Timestamp.Seconds);
            }
        }

        Assert.True(worst < 0.1, $"packets out of order by {worst} s");
    }

    [Fact]
    public void EmbedUsageErrors()
    {
        using Tool tool = new();
        string source = Path.Combine(Tool.Corpus, "mkv-metadata.mkv");
        File.WriteAllBytes(tool.PathOf("font.ttf"), [1, 2, 3, 4]);
        Assert.Contains("Nothing to embed", Usage(tool.Run("embed", source, "-o", "out.mkv")), StringComparison.Ordinal);
        Assert.Contains("does not exist", Usage(tool.Run("embed", source, "--add", "missing.flac", "-o", "out.mkv")), StringComparison.Ordinal);
        Assert.Contains("track=value", Usage(tool.Run("embed", source, "--language", "eng", "-o", "out.mkv")), StringComparison.Ordinal);
        Assert.Contains("matches no track", Usage(tool.Run("embed", source, "--language", "9=eng", "-o", "out.mkv")), StringComparison.Ordinal);
        Assert.Contains("holds no attachments", Usage(tool.Run("embed", source, "--attach", "font.ttf", "-o", "out.avi")), StringComparison.Ordinal);
        Assert.Contains("MP4 holds cover art", Usage(tool.Run("embed", source, "--attach", "font.ttf", "-o", "out.mp4")), StringComparison.Ordinal);
        Assert.Contains("--cover takes", Usage(tool.Run("embed", source, "--cover", "font.ttf", "-o", "out.mkv")), StringComparison.Ordinal);
        Assert.Contains("matches no track", Usage(tool.Run("embed", source, "--add", Path.Combine(Tool.Corpus, "webm-vp8-opus.webm") + ":s", "-o", "out.mkv")), StringComparison.Ordinal);
        Assert.Contains("would replace", Usage(tool.Run("embed", source, "--add", tool.Copy("webm-vp8-opus.webm", "other.webm"), "-o", "other.webm")), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(tool.Folder, "out*"));
    }

    [Fact]
    public void EmbedIntoAContainerThatCannotHoldTheTracksFails()
    {
        using Tool tool = new();
        ToolRun run = tool.Run("embed", Path.Combine(Tool.Corpus, "webm-vp8-opus.webm"), "--add", Path.Combine(Tool.Corpus, "audio/flac-l5-stereo.flac"), "-o", "out.ivf");
        Assert.Equal(1, run.Code);
        Assert.Contains("Write .mkv", run.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(tool.PathOf("out.ivf")));
    }

    [Fact]
    public void EmbedReadsStandardInput()
    {
        using Tool tool = new();
        byte[] clip = File.ReadAllBytes(Path.Combine(Tool.Corpus, "webm-vp8-opus.webm"));
        ToolRun run = tool.Run(["embed", "-", "--name", "1=Picture", "-o", "-", "--format", "webm"], clip);
        Assert.True(run.Code == 0, run.Error);
        File.WriteAllBytes(tool.PathOf("piped.webm"), run.Bytes);
        Assert.Equal("Picture", MediaFile.Probe(tool.PathOf("piped.webm")).Tracks[0].Name);
        Assert.Equal(RemuxTests.Packets(Path.Combine(Tool.Corpus, "webm-vp8-opus.webm")), RemuxTests.Packets(tool.PathOf("piped.webm")));
    }

    [Fact]
    public void AddSplitsTheTrackSpecFromThePath()
    {
        using Tool tool = new();
        tool.Copy("webm-vp8-opus.webm", "other.webm");
        Assert.Equal(0, Ok(tool.Run("embed", Path.Combine(Tool.Corpus, "mkv-metadata.mkv"), "--add", "other.webm:v:0", "--add", Path.Combine(tool.Folder, "other.webm") + ":a", "-o", "out.mkv")));
        Assert.Equal(
            [CodecIds.MotionJpeg, CodecIds.PcmLittleEndian, CodecIds.Vp8, CodecIds.Opus],
            MediaFile.Probe(tool.PathOf("out.mkv")).Tracks.Select(t => t.CodecId));
    }

    [Fact]
    public void CancelledJobsLeaveNoFile()
    {
        using Tool tool = new() { Cancellation = new CancellationToken(canceled: true) };
        string source = Path.Combine(Tool.Corpus, "mkv-metadata.mkv");
        Assert.Equal(130, tool.Run("extract", source, "-t", "v", "-o", "out.mkv").Code);
        Assert.Equal(130, tool.Run("extract", source, "--cover", "-o", "out.jpg").Code);
        Assert.Equal(130, tool.Run("embed", source, "--add", Path.Combine(Tool.Corpus, "webm-vp8-opus.webm"), "-o", "out.mkv").Code);
        Assert.Empty(Directory.GetFiles(tool.Folder, "*", SearchOption.AllDirectories));
    }

    /// <summary>Checks that ffprobe, when it is on the PATH, reads every packet of the files without an error.</summary>
    internal static void FfprobeReads(params string[] files)
    {
        foreach (string file in files)
        {
            ProcessStartInfo start = new("ffprobe", ["-v", "error", "-count_packets", "-show_entries", "stream=nb_read_packets", "-of", "csv", file])
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            Process process;
            try
            {
                process = Process.Start(start)!;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return;
            }

            using (process)
            {
                _ = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                Assert.True(process.ExitCode == 0 && error.Length == 0, $"ffprobe {file}: {error}");
            }
        }
    }

    private static int Ok(ToolRun run)
    {
        Assert.True(run.Code == 0, run.Error);
        return run.Code;
    }

    private static string Usage(ToolRun run)
    {
        Assert.True(run.Code == 2, $"exit {run.Code}: {run.Error}");
        return run.Error;
    }

    /// <summary>
    /// Asserts that two files hold the same packets per track kind, in order, with timestamps within 1 ms (Matroska stores
    /// milliseconds, so an MP4 time of 6.5 ms comes back as 7 ms).
    /// </summary>
    internal static void SamePackets(string expected, string actual)
    {
        foreach (TrackKind kind in Enum.GetValues<TrackKind>())
        {
            List<(string Hash, double Seconds)> want = Read(expected, kind);
            List<(string Hash, double Seconds)> got = Read(actual, kind);
            Assert.Equal(want.Select(p => p.Hash), got.Select(p => p.Hash));
            for (int i = 0; i < want.Count; i++)
            {
                Assert.True(Math.Abs(want[i].Seconds - got[i].Seconds) <= 0.0011, $"{kind} packet {i}: {want[i].Seconds} s became {got[i].Seconds} s");
            }
        }

        static List<(string, double)> Read(string path, TrackKind kind)
        {
            using IDemuxer demuxer = MediaFile.OpenDemuxer(path);
            HashSet<int> numbers = [.. demuxer.Tracks.Where(t => t.Kind == kind).Select(t => t.Number)];
            List<(string, double)> packets = [];
            while (demuxer.ReadPacket() is { } packet)
            {
                using (packet)
                {
                    if (numbers.Contains(packet.TrackNumber))
                    {
                        packets.Add((Convert.ToHexString(SHA256.HashData(packet.Data.Span))[..16], packet.Timestamp.Seconds));
                    }
                }
            }

            return [.. packets.OrderBy(p => p.Item2).ThenBy(p => p.Item1, StringComparer.Ordinal)];
        }
    }

    /// <summary>The hashes of one kind of track's packets, in file order.</summary>
    private static List<string> Hashes(string path, TrackKind kind)
    {
        using IDemuxer demuxer = MediaFile.OpenDemuxer(path);
        HashSet<int> numbers = [.. demuxer.Tracks.Where(t => t.Kind == kind).Select(t => t.Number)];
        List<string> hashes = [];
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                if (numbers.Contains(packet.TrackNumber))
                {
                    hashes.Add(Convert.ToHexString(SHA256.HashData(packet.Data.Span)));
                }
            }
        }

        return hashes;
    }

    /// <summary>The hash of all the bytes of one kind of track, packets joined.</summary>
    private static string Bytes(string path, TrackKind kind)
    {
        using IDemuxer demuxer = MediaFile.OpenDemuxer(path);
        HashSet<int> numbers = [.. demuxer.Tracks.Where(t => t.Kind == kind).Select(t => t.Number)];
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                if (numbers.Contains(packet.TrackNumber))
                {
                    hash.AppendData(packet.Data.Span);
                }
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
