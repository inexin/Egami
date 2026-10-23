using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Egami.Video.Cli.Tests;

/// <summary>
/// <c>hash</c> and <c>compare</c>. The FFmpeg checks run <c>ffmpeg</c> as a black box when it is on the PATH and are skipped
/// otherwise. Clips whose hashes legitimately differ from FFmpeg's: Motion JPEG (the IDCT and chroma upsampling of two JPEG
/// decoders are not bit-exact; checked against <see cref="VideoQuality.Md5(Egami.Pixels.YuvRaster)"/> instead), VP8 and VP9 with
/// alpha (FFmpeg's own decoders drop the alpha plane; libvpx keeps it), and WAV, whose packets FFmpeg cuts at other places, so
/// its frames differ while the whole stream (<c>--total</c>, <c>ffmpeg -f md5</c>) is the same.
/// </summary>
public sealed class CheckTests
{
    public static TheoryData<string, string, string[], string[]> FrameClips => new()
    {
        { "vp8/v1-pan.webm", "v", [], [] },
        { "vp9/p0-altref.webm", "v", [], [] },
        { "vp9/p2-10bit.webm", "v", [], [] },
        { "vp9/p3-12bit-444.webm", "v", [], [] },
        { "av1/monochrome.webm", "v", [], [] },
        { "y4m-444alpha.y4m", "v", [], [] },
        { "avi-raw-yuy2.avi", "v", [], ["-pix_fmt", "yuv422p"] },
        { "avi-raw-bgr24.avi", "v", [], ["-pix_fmt", "gbrp"] },
        { "audio/flac-l5-stereo.flac", "a", [], [] },
        { "audio/flac-24-51.flac", "a", [], ["-c:a", "pcm_s24le"] },
        { "audio/flac-8-mono.flac", "a", [], ["-c:a", "pcm_u8"] },
        { "mkv-mjpeg-pcm.mkv", "a", [], ["-map", "0:a"] },
    };

    [Theory]
    [MemberData(nameof(FrameClips))]
    public void FrameHashesMatchFfmpegFramemd5(string clip, string track, string[] before, string[] after)
    {
        string ffmpeg = Ffmpeg.Require();
        string path = Path.Combine(Tool.Corpus, clip);
        using Tool tool = new();
        ToolRun run = tool.Run("hash", path, "--csv", "--track", track);
        Assert.True(run.Code == 0, run.Error);
        List<string> ours = [.. run.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(l => l.Split(',')).Select(f => $"{f[5]} {f[6]}")];
        string[] map = track == "v" ? ["-map", "0:v:0"] : [];
        string text = Ffmpeg.Run(ffmpeg, [.. before, "-i", path, .. map, .. after, "-f", "framemd5", "-"]);
        List<string> theirs = [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => !l.StartsWith('#')).Select(l => l.Split(',', StringSplitOptions.TrimEntries)).Select(f => $"{f[4]} {f[5]}")];
        Assert.NotEmpty(ours);
        Assert.Equal(theirs, ours);
    }

    [Theory]
    [InlineData("audio/wav-s16-stereo.wav", "pcm_s16le")]
    [InlineData("audio/wav-s24-51.wav", "pcm_s24le")]
    [InlineData("audio/wav-s32-stereo.wav", "pcm_s32le")]
    [InlineData("audio/wav-f32-stereo.wav", "pcm_f32le")]
    [InlineData("audio/wav-u8-mono.wav", "pcm_u8")]
    [InlineData("audio/w64-s16-stereo.w64", "pcm_s16le")]
    public void WholeStreamHashMatchesFfmpegMd5(string clip, string codec)
    {
        string ffmpeg = Ffmpeg.Require();
        string path = Path.Combine(Tool.Corpus, clip);
        using Tool tool = new();
        ToolRun run = tool.Run("hash", path, "--total");
        Assert.True(run.Code == 0, run.Error);
        Assert.Contains($"#codec_id 0: {codec}\n", run.Text, StringComparison.Ordinal);
        string total = run.Text.Split('\n').Single(l => l.StartsWith("#total 0:", StringComparison.Ordinal)).Split(' ')[^1];
        string theirs = Ffmpeg.Run(ffmpeg, ["-i", path, "-c:a", codec, "-f", "md5", "-"]).Trim();
        Assert.Equal($"MD5={total}", theirs);
    }

    [Theory]
    [InlineData("avi-mjpeg-420-pcm.avi")]
    [InlineData("vp9/p2-10bit.webm")]
    [InlineData("y4m-444alpha.y4m")]
    public void FrameHashesAreVideoQualityMd5(string clip)
    {
        string path = Path.Combine(Tool.Corpus, clip);
        using Tool tool = new();
        ToolRun run = tool.Run("hash", path, "--csv", "-t", "v");
        Assert.True(run.Code == 0, run.Error);
        List<string> ours = [.. run.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(l => l.Split(',')[6])];
        using VideoReader reader = VideoReader.Open(path);
        List<string> expected = [];
        while (reader.ReadFrame() is { } frame)
        {
            using (frame)
            {
                expected.Add(VideoQuality.Md5(frame.Raster));
            }
        }

        Assert.Equal(expected, ours);
    }

    [Fact]
    public void PacketHashesMatchFfmpegStreamCopy()
    {
        string path = Path.Combine(Tool.Corpus, "webm-vp8-opus.webm");
        using Tool tool = new();
        ToolRun run = tool.Run("hash", path, "--packets", "--csv");
        Assert.True(run.Code == 0, run.Error);
        List<string[]> rows = [.. run.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(l => l.Split(','))];

        using IDemuxer demuxer = MediaFile.OpenDemuxer(path);
        List<string> expected = [];
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                expected.Add(VideoQuality.Md5(packet.Data.Span));
            }
        }

        Assert.Equal(expected, rows.Select(r => r[6]));
        if (Ffmpeg.Find() is { } ffmpeg)
        {
            string text = Ffmpeg.Run(ffmpeg, ["-i", path, "-map", "0", "-c", "copy", "-f", "framemd5", "-"]);
            List<string> theirs = [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => !l.StartsWith('#')).Select(l => l.Split(',', StringSplitOptions.TrimEntries)[5])];
            Assert.Equal(theirs.Order(StringComparer.Ordinal), rows.Select(r => r[6]).Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public void TextHasFramemd5Lines()
    {
        using Tool tool = new();
        ToolRun run = tool.Run("hash", Path.Combine(Tool.Corpus, "avi-mjpeg-420-pcm.avi"), "--total");
        Assert.True(run.Code == 0, run.Error);
        string[] lines = run.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("#format: frame checksums", lines[0]);
        Assert.Contains("#hash: MD5", lines);
        Assert.Contains("#tb 1: 1/8000", lines);
        Assert.Contains("#codec_id 0: rawvideo", lines);
        Assert.Contains("#dimensions 0: 160x120", lines);
        Assert.Contains("#stream#, dts,        pts, duration,     size, hash", lines);
        Assert.Contains("1,          0,          0,     1024,     2048, b2d19726eb72f6f60ed70db875b0e4a6", lines);
        Assert.StartsWith("#total 0: ", lines[^2], StringComparison.Ordinal);
        Assert.StartsWith("#total 1: ", lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void Sha256AndJson()
    {
        using Tool tool = new();
        string path = Path.Combine(Tool.Corpus, "vp8/v0-keyframes.webm");
        ToolRun run = tool.Run("hash", path, "--algorithm", "sha256", "--json");
        Assert.True(run.Code == 0, run.Error);
        using JsonDocument json = JsonDocument.Parse(run.Text);
        Assert.Equal("sha256", json.RootElement.GetProperty("hash").GetString());
        JsonElement file = json.RootElement.GetProperty("files")[0];
        JsonElement stream = file.GetProperty("streams")[0];
        Assert.Equal("rawvideo", stream.GetProperty("codec").GetString());
        JsonElement first = file.GetProperty("frames")[0];
        Assert.Equal(64, first.GetProperty("hash").GetString()!.Length);
        Assert.Equal(stream.GetProperty("count").GetInt64(), file.GetProperty("frames").GetArrayLength());
        Assert.Equal(64, stream.GetProperty("total").GetString()!.Length);
    }

    [Fact]
    public void SampleFormatConvertsAudio()
    {
        using Tool tool = new();
        ToolRun run = tool.Run("hash", Path.Combine(Tool.Corpus, "audio/flac-24-51.flac"), "--sample-format", "s16");
        Assert.True(run.Code == 0, run.Error);
        Assert.Contains("#codec_id 0: pcm_s16le\n", run.Text, StringComparison.Ordinal);
        Assert.Contains("    49152, ", run.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TrackSelectionAndBatches()
    {
        using Tool tool = new();
        tool.Copy("avi-mjpeg-420-pcm.avi", "in/a.avi");
        tool.Copy("mkv-mjpeg-pcm.mkv", "in/b.mkv");
        ToolRun run = tool.Run("hash", "in/*", "-t", "a");
        Assert.True(run.Code == 0, run.Error);
        Assert.Contains("#input: in/a.avi\n", run.Text.Replace('\\', '/'), StringComparison.Ordinal);
        Assert.DoesNotContain("rawvideo", run.Text, StringComparison.Ordinal);
        Assert.Equal(2, run.Text.Split('\n').Count(l => l.StartsWith("#codec_id 1: pcm_s16le", StringComparison.Ordinal)));

        ToolRun none = tool.Run("hash", "in/a.avi", "-t", "s");
        Assert.Equal(2, none.Code);
        Assert.Contains("matches no track", none.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadsStandardInput()
    {
        using Tool tool = new();
        string path = Path.Combine(Tool.Corpus, "mkv-mjpeg-pcm.mkv");
        ToolRun file = tool.Run("hash", path, "--csv");
        ToolRun piped = tool.Run(["hash", "-", "--csv"], File.ReadAllBytes(path));
        Assert.True(piped.Code == 0, piped.Error);
        Assert.Equal(file.Text.Split('\n').Skip(1), piped.Text.Split('\n').Skip(1).Select(l => l.Replace("-,", $"{path},", StringComparison.Ordinal)));
        ToolRun packets = tool.Run(["hash", "-", "--packets"], File.ReadAllBytes(path));
        Assert.True(packets.Code == 0, packets.Error);
    }

    [Theory]
    [InlineData("--algorithm", "sha1")]
    [InlineData("--sample-format", "s24")]
    [InlineData("--format", "xml")]
    [InlineData("--json", "--csv")]
    [InlineData("--packets", "--sample-format=s16")]
    public void HashUsageErrors(string option, string value)
    {
        using Tool tool = new();
        ToolRun run = tool.Run("hash", Path.Combine(Tool.Corpus, "avi-mjpeg-420-pcm.avi"), option, value);
        Assert.Equal(2, run.Code);
        Assert.NotEmpty(run.Error);
    }

    [Fact]
    public void CancelledHashStops()
    {
        using Tool tool = new() { Cancellation = new CancellationToken(canceled: true) };
        Assert.Equal(130, tool.Run("hash", Path.Combine(Tool.Corpus, "avi-mjpeg-420-pcm.avi")).Code);
        Assert.Equal(130, tool.Run("compare", Path.Combine(Tool.Corpus, "vp8/v1-pan.webm"), Path.Combine(Tool.Corpus, "vp8/v2-pan.webm")).Code);
    }

    [Theory]
    [InlineData("vp8/v2-pan.webm")]
    [InlineData("vp8/v3-pan.webm")]
    public void PsnrMatchesFfmpeg(string clip)
    {
        string ffmpeg = Ffmpeg.Require();
        string reference = Path.Combine(Tool.Corpus, "vp8/v1-pan.webm");
        string distorted = Path.Combine(Tool.Corpus, clip);
        using Tool tool = new();
        ToolRun run = tool.Run("compare", reference, distorted, "--json", "--metric", "psnr");
        Assert.True(run.Code == 0, run.Error);
        using JsonDocument json = JsonDocument.Parse(run.Text);
        JsonElement psnr = json.RootElement.GetProperty("files")[0].GetProperty("psnr");

        // ffmpeg's psnr filter averages each plane's squared error over the frames, and its "average" weights the planes by
        // their sizes, the same definition VideoQuality uses.
        string log = Ffmpeg.Run(ffmpeg, ["-hide_banner", "-i", distorted, "-i", reference, "-lavfi", "psnr", "-f", "null", "-"], stderr: true);
        string line = log.Split('\n').Single(l => l.Contains("PSNR y:", StringComparison.Ordinal));
        Dictionary<string, double> theirs = line[line.IndexOf("PSNR ", StringComparison.Ordinal)..].Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .Select(p => p.Split(':')).Where(p => p[1] != "inf").ToDictionary(p => p[0], p => double.Parse(p[1], CultureInfo.InvariantCulture));
        Assert.Equal(theirs["y"], psnr.GetProperty("y").GetDouble(), 0.01);
        Assert.Equal(theirs["u"], psnr.GetProperty("u").GetDouble(), 0.01);
        Assert.Equal(theirs["v"], psnr.GetProperty("v").GetDouble(), 0.01);
        Assert.Equal(theirs["average"], psnr.GetProperty("average").GetDouble(), 0.01);
        Assert.Equal(theirs["min"], psnr.GetProperty("min").GetDouble(), 0.01);
    }

    [Fact]
    public void IdenticalClipsScorePerfectly()
    {
        using Tool tool = new();
        string clip = Path.Combine(Tool.Corpus, "vp8/v1-pan.webm");
        ToolRun run = tool.Run("compare", clip, clip, "--json");
        Assert.True(run.Code == 0, run.Error);
        using JsonDocument json = JsonDocument.Parse(run.Text);
        JsonElement file = json.RootElement.GetProperty("files")[0];
        Assert.Equal(JsonValueKind.Null, file.GetProperty("psnr").GetProperty("average").ValueKind);
        Assert.Equal(1, file.GetProperty("ssim").GetProperty("average").GetDouble(), 1e-9);
        ToolRun text = tool.Run("compare", clip, clip);
        Assert.Contains("PSNR y:inf u:inf v:inf average:inf", text.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ThresholdsExitWithOne()
    {
        using Tool tool = new();
        string reference = Path.Combine(Tool.Corpus, "vp8/v1-pan.webm");
        string clip = Path.Combine(Tool.Corpus, "vp8/v3-pan.webm");
        Assert.Equal(0, tool.Run("compare", reference, clip, "--min-psnr", "35", "--min-ssim", "0.9").Code);
        ToolRun psnr = tool.Run("compare", reference, clip, "--min-psnr", "45");
        Assert.Equal(1, psnr.Code);
        Assert.Contains("below --min-psnr", psnr.Error, StringComparison.Ordinal);
        Assert.Contains("PSNR y:", psnr.Text, StringComparison.Ordinal);
        Assert.Equal(1, tool.Run("compare", reference, clip, "--min-ssim", "0.999").Code);
    }

    [Fact]
    public void PerFrameJsonAndCsv()
    {
        using Tool tool = new();
        string reference = Path.Combine(Tool.Corpus, "vp8/v1-pan.webm");
        string clip = Path.Combine(Tool.Corpus, "vp8/v2-pan.webm");
        ToolRun json = tool.Run("compare", reference, clip, "--per-frame", "--json");
        Assert.True(json.Code == 0, json.Error);
        using JsonDocument document = JsonDocument.Parse(json.Text);
        JsonElement file = document.RootElement.GetProperty("files")[0];
        Assert.Equal(file.GetProperty("frames").GetInt64(), file.GetProperty("perFrame").GetArrayLength());

        ToolRun csv = tool.Run("compare", reference, clip, "--per-frame", "--csv", "--metric", "ssim");
        Assert.True(csv.Code == 0, csv.Error);
        string[] lines = csv.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("path,frame,time,ssimY,ssimU,ssimV,ssimA,ssimAverage", lines[0]);
        Assert.Equal(file.GetProperty("frames").GetInt64() + 2, lines.Length);
        Assert.Contains(",all,", lines[^1], StringComparison.Ordinal);

        ToolRun text = tool.Run("compare", reference, clip, "--per-frame");
        Assert.Contains("  frame 0 (0.000s): PSNR y:", text.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void SizesMustMatchUnlessScaled()
    {
        using Tool tool = new();
        string reference = Path.Combine(Tool.Corpus, "vp8/v1-pan.webm");
        string clip = Path.Combine(Tool.Corpus, "vp8/v0-keyframes.webm");
        ToolRun run = tool.Run("compare", reference, clip);
        Assert.Equal(2, run.Code);
        Assert.Contains("give --scale", run.Error, StringComparison.Ordinal);
        ToolRun scaled = tool.Run("compare", reference, clip, "--scale");
        Assert.True(scaled.Code == 0, scaled.Error);
        ToolRun chroma = tool.Run("compare", reference, Path.Combine(Tool.Corpus, "vp9/p1-444.webm"));
        Assert.Equal(2, chroma.Code);
        Assert.Contains("same chroma format", chroma.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ComparesBatchesAndStandardInput()
    {
        using Tool tool = new();
        tool.Copy("vp8/v2-pan.webm", "in/v2.webm");
        tool.Copy("vp8/v3-pan.webm", "in/v3.webm");
        string reference = Path.Combine(Tool.Corpus, "vp8/v1-pan.webm");
        ToolRun run = tool.Run("compare", reference, "in/*.webm", "--json");
        Assert.True(run.Code == 0, run.Error);
        using JsonDocument json = JsonDocument.Parse(run.Text);
        Assert.Equal(2, json.RootElement.GetProperty("files").GetArrayLength());

        ToolRun piped = tool.Run(["compare", "-", "in/v3.webm"], File.ReadAllBytes(reference));
        Assert.True(piped.Code == 0, piped.Error);
        Assert.Contains("in/v3.webm: 20 frames", piped.Text.Replace('\\', '/'), StringComparison.Ordinal);
        ToolRun clipPiped = tool.Run(["compare", reference, "-"], File.ReadAllBytes(tool.PathOf("in/v3.webm")));
        Assert.True(clipPiped.Code == 0, clipPiped.Error);
        Assert.Equal(2, tool.Run(["compare", "-", "-"], File.ReadAllBytes(reference)).Code);
    }

    [Fact]
    public void CountsUnpairedFrames()
    {
        using Tool tool = new();
        ToolRun run = tool.Run("compare", Path.Combine(Tool.Corpus, "vp8/v0-keyframes.webm"), Path.Combine(Tool.Corpus, "vp9/p0-keyframes.webm"), "--json");
        Assert.True(run.Code == 0, run.Error);
        using JsonDocument json = JsonDocument.Parse(run.Text);
        JsonElement file = json.RootElement.GetProperty("files")[0];
        long frames = file.GetProperty("frames").GetInt64();
        Assert.Equal(Math.Min(file.GetProperty("referenceFrames").GetInt64(), file.GetProperty("clipFrames").GetInt64()), frames);
        if (file.GetProperty("referenceFrames").GetInt64() != file.GetProperty("clipFrames").GetInt64())
        {
            Assert.Contains("were compared", run.Error, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("--metric", "vmaf")]
    [InlineData("--min-ssim", "2")]
    [InlineData("--format", "xml")]
    public void CompareUsageErrors(string option, string value)
    {
        using Tool tool = new();
        string clip = Path.Combine(Tool.Corpus, "vp8/v1-pan.webm");
        ToolRun run = tool.Run("compare", clip, clip, option, value);
        Assert.Equal(2, run.Code);
        Assert.NotEmpty(run.Error);
    }

    [Fact]
    public void CompareUsageErrorsForMetricsAndTracks()
    {
        using Tool tool = new();
        string clip = Path.Combine(Tool.Corpus, "vp8/v1-pan.webm");
        Assert.Equal(2, tool.Run("compare", clip, clip, "--metric", "ssim", "--min-psnr", "30").Code);
        ToolRun audio = tool.Run("compare", Path.Combine(Tool.Corpus, "audio/wav-s16-stereo.wav"), clip);
        Assert.Equal(2, audio.Code);
        Assert.Contains("no video track", audio.Error, StringComparison.Ordinal);
        Assert.Equal(2, tool.Run("compare", "missing-*.webm", clip).Code);
    }
}

/// <summary>Runs ffmpeg from the PATH as a black box, for checks against its output.</summary>
internal static class Ffmpeg
{
    private static readonly Lazy<string?> Path = new(() =>
    {
        string name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(folder => System.IO.Path.Combine(folder, name))
            .FirstOrDefault(File.Exists);
    });

    /// <summary>The ffmpeg program, or <see langword="null"/> when it is not on the PATH.</summary>
    public static string? Find() => Path.Value;

    /// <summary>The ffmpeg program; skips the test when it is not on the PATH.</summary>
    public static string Require()
    {
        Assert.SkipWhen(Path.Value is null, "ffmpeg is not on the PATH.");
        return Path.Value!;
    }

    /// <summary>Runs ffmpeg and returns its standard output, or its standard error with <paramref name="stderr"/>.</summary>
    public static string Run(string program, IEnumerable<string> args, bool stderr = false)
    {
        ProcessStartInfo start = new(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("-nostdin");
        if (!stderr)
        {
            start.ArgumentList.Add("-v");
            start.ArgumentList.Add("error");
        }

        foreach (string arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using Process process = Process.Start(start)!;
        Task<string> error = process.StandardError.ReadToEndAsync();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error.Result);
        return (stderr ? error.Result : output).Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
