using Egami.Video.Audio;

namespace Egami.Video.Cli.Tests;

/// <summary>
/// <c>audio</c>: decoded audio written to audio files, and video files whose audio is encoded again with the video packets
/// copied unchanged. FLAC outputs are checked with the reference decoder (<c>flac -t</c>) when it is on the PATH, else with
/// <c>ffmpeg</c>'s decoder, and always against the source's samples decoded by Egami.Video.
/// </summary>
public sealed class AudioTests
{
    [Theory]
    [InlineData("audio/wav-s16-stereo.wav")]
    [InlineData("audio/wav-s24-51.wav")]
    [InlineData("audio/wav-s32-stereo.wav")]
    [InlineData("audio/wav-u8-mono.wav")]
    [InlineData("audio/w64-s16-stereo.w64")]
    [InlineData("audio/flac-l8-stereo.flac")]
    [InlineData("audio/mka-flac-stereo.mka")]
    [InlineData("audio/mp4-flac-stereo.mp4")]
    public void LosslessToFlacKeepsEverySample(string clip)
    {
        using Tool tool = new();
        string source = Path.Combine(Tool.Corpus, clip);
        ToolRun run = tool.Run("audio", source, "-o", "out.flac");
        Assert.True(run.Code == 0, run.Error);
        Assert.Equal(CodecIds.Flac, MediaFile.Probe(tool.PathOf("out.flac")).Tracks.Single().CodecId);
        Assert.Equal(Samples(source), Samples(tool.PathOf("out.flac")));
        VerifyFlac(tool.PathOf("out.flac"));
    }

    [Theory]
    [InlineData("audio/flac-24-51.flac", "out.wav", 24)]
    [InlineData("audio/flac-l5-stereo.flac", "out.w64", 16)]
    [InlineData("audio/wav-f32-stereo.wav", "out.wav", 32)]
    public void DecodesToPcm(string clip, string output, int bits)
    {
        using Tool tool = new();
        string source = Path.Combine(Tool.Corpus, clip);
        ToolRun run = tool.Run("audio", source, "-o", output);
        Assert.True(run.Code == 0, run.Error);
        AudioTrackInfo track = (AudioTrackInfo)MediaFile.Probe(tool.PathOf(output)).Tracks.Single();
        Assert.Equal(bits, track.BitsPerSample);
        Assert.Equal(Samples(source), Samples(tool.PathOf(output)));
    }

    [Fact]
    public void ResamplesRemixesAndConverts()
    {
        using Tool tool = new();
        ToolRun run = tool.Run("audio", Path.Combine(Tool.Corpus, "audio/opus/opus-stereo.opus"), "-o", "out.wav", "--rate", "44100", "--channels", "mono", "--sample-format", "s24");
        Assert.True(run.Code == 0, run.Error);
        AudioTrackInfo track = (AudioTrackInfo)MediaFile.Probe(tool.PathOf("out.wav")).Tracks.Single();
        Assert.Equal(44100, track.SampleRate);
        Assert.Equal(1, track.Channels);
        Assert.Equal(24, track.BitsPerSample);

        ToolRun vorbis = tool.Run("audio", Path.Combine(Tool.Corpus, "audio/vorbis/q6-stereo-48k.ogg"), "-o", "out.oga", "--channels", "1", "--level", "8");
        Assert.True(vorbis.Code == 0, vorbis.Error);
        AudioTrackInfo flac = (AudioTrackInfo)MediaFile.Probe(tool.PathOf("out.oga")).Tracks.Single();
        Assert.Equal(CodecIds.Flac, flac.CodecId);
        Assert.Equal(1, flac.Channels);

        ToolRun floats = tool.Run("audio", Path.Combine(Tool.Corpus, "audio/wav-s16-stereo.wav"), "-o", "out.w64", "--sample-format", "f32");
        Assert.True(floats.Code == 0, floats.Error);
        Assert.Equal(CodecIds.PcmFloat, MediaFile.Probe(tool.PathOf("out.w64")).Tracks.Single().CodecId);
    }

    [Theory]
    [InlineData("avi-mjpeg-420-pcm.avi", "out.mkv")]
    [InlineData("mkv-mjpeg-pcm.mkv", "out.mkv")]
    [InlineData("webm-vp8-opus.webm", "out.mkv")]
    [InlineData("mkv-vorbis-xiph-laced.mkv", "out.mkv")]
    [InlineData("mkv-mjpeg-pcm.mkv", "out.mp4")]
    public void VideoPacketsStayAndAudioBecomesFlac(string clip, string output)
    {
        using Tool tool = new();
        string source = Path.Combine(Tool.Corpus, clip);
        ToolRun run = tool.Run("audio", source, "-o", output);
        Assert.True(run.Code == 0, run.Error);
        Assert.Equal(VideoPackets(source), VideoPackets(tool.PathOf(output)));
        MediaInfo info = MediaFile.Probe(tool.PathOf(output));
        Assert.All(info.Tracks.Where(t => t.Kind == TrackKind.Audio), t => Assert.Equal(CodecIds.Flac, t.CodecId));
        if (MediaFile.Probe(source).Tracks.Single(t => t.Kind == TrackKind.Audio).CodecId is CodecIds.PcmLittleEndian)
        {
            Assert.Equal(Samples(source), Samples(tool.PathOf(output)));
        }

        if (Ffmpeg.Find() is { } ffmpeg)
        {
            Ffmpeg.Run(ffmpeg, ["-i", tool.PathOf(output), "-f", "null", "-"]);
        }
    }

    [Fact]
    public void ConvertsAudioBesideVideo()
    {
        using Tool tool = new();
        string source = Path.Combine(Tool.Corpus, "avi-mjpeg-420-pcm.avi");
        ToolRun run = tool.Run("audio", source, "-o", "out.mkv", "--rate", "16000", "--channels", "stereo", "--codec", "pcm", "--sample-format", "s24");
        Assert.True(run.Code == 0, run.Error);
        Assert.Equal(VideoPackets(source), VideoPackets(tool.PathOf("out.mkv")));
        AudioTrackInfo audio = MediaFile.Probe(tool.PathOf("out.mkv")).Tracks.OfType<AudioTrackInfo>().Single();
        Assert.Equal(16000, audio.SampleRate);
        Assert.Equal(2, audio.Channels);
        Assert.Equal(24, audio.BitsPerSample);
        Assert.Equal(CodecIds.PcmLittleEndian, audio.CodecId);
        double seconds = MediaFile.Probe(source).Tracks.OfType<AudioTrackInfo>().Single() is { } original ? Samples(source).Length / (double)original.Channels / original.SampleRate : 0;
        Assert.Equal(seconds, Samples(tool.PathOf("out.mkv")).Length / 2.0 / 16000, 0.01);
    }

    [Fact]
    public void NoVideoAndTrackSelection()
    {
        using Tool tool = new();
        string source = Path.Combine(Tool.Corpus, "mkv-mjpeg-pcm.mkv");
        ToolRun run = tool.Run("audio", source, "-o", "out.mka", "--no-video");
        Assert.True(run.Code == 0, run.Error);
        Assert.Equal([TrackKind.Audio], MediaFile.Probe(tool.PathOf("out.mka")).Tracks.Select(t => t.Kind));

        ToolRun picked = tool.Run("audio", source, "-o", "a.flac", "--track", "a:0");
        Assert.True(picked.Code == 0, picked.Error);
        ToolRun missing = tool.Run("audio", source, "-o", "b.flac", "--track", "a:1");
        Assert.Equal(2, missing.Code);
        Assert.Contains("matches no track", missing.Error, StringComparison.Ordinal);
        ToolRun silent = tool.Run("audio", Path.Combine(Tool.Corpus, "mkv-raw-i420.mkv"), "-o", "c.flac");
        Assert.Equal(2, silent.Code);
        Assert.Contains("no audio track", silent.Error, StringComparison.Ordinal);
        ToolRun silentVideo = tool.Run("audio", Path.Combine(Tool.Corpus, "mkv-raw-i420.mkv"), "-o", "c.mkv");
        Assert.Equal(2, silentVideo.Code);
    }

    [Fact]
    public void PipesInAndOut()
    {
        using Tool tool = new();
        string source = Path.Combine(Tool.Corpus, "audio/wav-s16-stereo.wav");
        ToolRun run = tool.Run(["audio", "-", "-o", "-", "--format", "flac"], File.ReadAllBytes(source));
        Assert.True(run.Code == 0, run.Error);
        File.WriteAllBytes(tool.PathOf("piped.flac"), run.Bytes);
        Assert.Equal(Samples(source), Samples(tool.PathOf("piped.flac")));

        ToolRun video = tool.Run(["audio", "-", "-o", "piped.mkv"], File.ReadAllBytes(Path.Combine(Tool.Corpus, "mkv-mjpeg-pcm.mkv")));
        Assert.True(video.Code == 0, video.Error);
        Assert.Equal(VideoPackets(Path.Combine(Tool.Corpus, "mkv-mjpeg-pcm.mkv")), VideoPackets(tool.PathOf("piped.mkv")));
    }

    [Fact]
    public void BatchWithTemplate()
    {
        using Tool tool = new();
        tool.Copy("audio/wav-s16-stereo.wav", "in/a.wav");
        tool.Copy("audio/wav-u8-mono.wav", "in/b.wav");
        ToolRun run = tool.Run("audio", "in/*.wav", "-o", "out/{name}.flac");
        Assert.True(run.Code == 0, run.Error);
        Assert.True(File.Exists(tool.PathOf("out/a.flac")));
        Assert.True(File.Exists(tool.PathOf("out/b.flac")));
        Assert.Equal(2, tool.Run("audio", "in/*.wav", "-o", "one.flac").Code);
    }

    [Theory]
    [InlineData("out.flac", "--codec", "mp3")]
    [InlineData("out.flac", "--sample-format", "f32")]
    [InlineData("out.wav", "--sample-format", "s20")]
    [InlineData("out.wav", "--level", "5")]
    [InlineData("out.flac", "--level", "9")]
    [InlineData("out.flac", "--rate", "0")]
    [InlineData("out.flac", "--channels", "0")]
    [InlineData("out.flac", "--channels", "surround")]
    [InlineData("out.webm", "--codec", "flac")]
    [InlineData("out.flac", "--codec", "pcm")]
    [InlineData("out.wav", "--codec", "flac")]
    [InlineData("out.mkv", "--no-audio", "--codec=flac")]
    public void UsageErrors(string output, string option, string value)
    {
        using Tool tool = new();
        ToolRun run = tool.Run("audio", Path.Combine(Tool.Corpus, "mkv-mjpeg-pcm.mkv"), "-o", output, option, value);
        Assert.Equal(2, run.Code);
        Assert.NotEmpty(run.Error);
        Assert.False(File.Exists(tool.PathOf(output)));
    }

    [Fact]
    public void AviCannotHoldFlac()
    {
        using Tool tool = new();
        ToolRun run = tool.Run("audio", Path.Combine(Tool.Corpus, "mkv-mjpeg-pcm.mkv"), "-o", "out.avi", "--codec", "flac");
        Assert.Equal(2, run.Code);
        Assert.Contains("cannot hold", run.Error, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(tool.Folder, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("audio/wav-s16-stereo.wav", "out.flac")]
    [InlineData("avi-mjpeg-420-pcm.avi", "out.mkv")]
    public void CancelledJobLeavesNoFile(string clip, string output)
    {
        using Tool tool = new() { Cancellation = new CancellationToken(canceled: true) };
        ToolRun run = tool.Run("audio", Path.Combine(Tool.Corpus, clip), "-o", output);
        Assert.Equal(130, run.Code);
        Assert.Empty(Directory.GetFiles(tool.Folder, "*", SearchOption.AllDirectories));
        ToolRun converted = tool.Run("audio", Path.Combine(Tool.Corpus, clip), "-o", output, "--rate", "16000");
        Assert.Equal(130, converted.Code);
        Assert.Empty(Directory.GetFiles(tool.Folder, "*", SearchOption.AllDirectories));
    }

    /// <summary>Every sample of a file's first audio track, decoded by Egami.Video as 32-bit integers.</summary>
    internal static int[] Samples(string path)
    {
        using AudioReader reader = AudioReader.Open(path, new AudioReaderOptions { SampleFormat = SampleFormat.S32 });
        List<int> samples = [];
        while (reader.TryRead(out AudioFrame? frame))
        {
            using (frame)
            {
                // Samples of fewer valid bits (24-bit) may come in their low bits; scale them all to 32 bits.
                int shift = frame.ValidBits is > 0 and < 32 ? 32 - frame.ValidBits : 0;
                foreach (int sample in frame.AsSpan<int>())
                {
                    samples.Add(sample << shift);
                }
            }
        }

        return [.. samples];
    }

    /// <summary>The video packets of a file, as <see cref="RemuxTests.Packets(string)"/> lists them.</summary>
    private static List<string> VideoPackets(string path) => [.. RemuxTests.Packets(path).Where(p => p.StartsWith("Video ", StringComparison.Ordinal))];

    /// <summary>Checks a FLAC file with the reference decoder, or ffmpeg's, when either is on the PATH.</summary>
    private static void VerifyFlac(string path)
    {
        string? flac = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(folder => Path.Combine(folder, OperatingSystem.IsWindows() ? "flac.exe" : "flac"))
            .FirstOrDefault(File.Exists);
        if (flac is not null)
        {
            using System.Diagnostics.Process process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(flac, ["-t", "-s", path]) { RedirectStandardError = true, UseShellExecute = false })!;
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, error);
        }
        else if (Ffmpeg.Find() is { } ffmpeg)
        {
            // FFmpeg checks each frame's CRC and, with -xerror, stops on the first error.
            Ffmpeg.Run(ffmpeg, ["-xerror", "-i", path, "-f", "null", "-"]);
        }
    }
}
