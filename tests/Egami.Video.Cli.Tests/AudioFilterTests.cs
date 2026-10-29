using Egami.Video.Audio;

namespace Egami.Video.Cli.Tests;

/// <summary>
/// V9.1 stage D (Design Q14): the audio filter options of <c>audio</c>, <c>encode</c> and <c>run</c>, and <c>--af</c> in
/// ffmpeg's <c>-af</c> syntax. ffmpeg mode's <c>-af</c> lines are in ffmpeg-lines.txt.
/// </summary>
public sealed class AudioFilterTests
{
    private static string Wav => Path.Combine(Tool.Corpus, "audio/wav-s16-stereo.wav");

    [Fact]
    public void GainScalesTheSamples()
    {
        using Tool tool = new();
        ToolRun run = tool.Run("audio", Wav, "-o", "out.wav", "--gain", "-6.0206");
        Assert.True(run.Code == 0, run.Error);
        int[] before = AudioTests.Samples(Wav), after = AudioTests.Samples(tool.PathOf("out.wav"));
        Assert.Equal(before.Length, after.Length);
        for (int i = 0; i < before.Length; i += 97)
        {
            Assert.InRange(after[i], (before[i] / 2) - 70000, (before[i] / 2) + 70000);
        }
    }

    [Fact]
    public void AfChainMatchesTheOptions()
    {
        using Tool tool = new();
        Assert.Equal(0, tool.Run("audio", Wav, "-o", "a.wav", "--af", "volume=-6dB,afade=t=in:d=0.1").Code);
        Assert.Equal(0, tool.Run("audio", Wav, "-o", "b.wav", "--gain", "-6", "--fade-in", "0.1").Code);
        Assert.Equal(AudioTests.Samples(tool.PathOf("a.wav")), AudioTests.Samples(tool.PathOf("b.wav")));
    }

    [Theory]
    [InlineData("highpass=f=200,lowpass=f=3000,equalizer=f=1000:t=q:w=1:g=3,bass=g=2,treble=g=-2")]
    [InlineData("bandpass=f=1000:t=o:w=2,bandreject=f=3000:t=h:w=200,allpass=f=500")]
    [InlineData("acompressor=threshold=-20dB:ratio=4:makeup=2,agate,alimiter=limit=0.9")]
    [InlineData("adelay=delays=100|200,apad=pad_dur=0.5,atrim=start=0.1:end=0.6,asetpts=PTS-STARTPTS")]
    [InlineData("atempo=1.25,loudnorm=I=-16:TP=-1.5")]
    [InlineData("loudnorm=linear=false,silenceremove=start_periods=1:stop_periods=1:start_threshold=-60dB")]
    public void AfTakesTheFilters(string chain)
    {
        using Tool tool = new();
        ToolRun run = tool.Run("audio", Wav, "-o", "out.flac", "--af", chain);
        Assert.True(run.Code == 0, run.Error);
        Assert.NotEmpty(AudioTests.Samples(tool.PathOf("out.flac")));
    }

    [Theory]
    [InlineData("--af", "aecho", "has no audio filter 'aecho'")]
    [InlineData("--af", "highpass=p=1", "two poles")]
    [InlineData("--af", "volume=loud", "not a number")]
    [InlineData("--af", "atempo=8", "0.25 to 4")]
    [InlineData("--af", "bass=t=o:w=1", "width_type q here")]
    [InlineData("--af", "loudnorm,loudnorm", "once")]
    [InlineData("--tempo", "5", "0.25 to 4")]
    [InlineData("--normalize", "loud", "not ebu, atsc")]
    public void BadFiltersAreUsageErrors(string option, string value, string message)
    {
        using Tool tool = new();
        ToolRun run = tool.Run("audio", Wav, "-o", "out.wav", option, value);
        Assert.Equal(2, run.Code);
        Assert.Contains(message, run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizeReachesTheTarget()
    {
        using Tool tool = new();
        ToolRun run = tool.Run("audio", Wav, "-o", "out.flac", "--normalize", "podcast");
        Assert.True(run.Code == 0, run.Error);
        LoudnessMeter meter = new();
        using (AudioReader reader = AudioReader.Open(tool.PathOf("out.flac")))
        {
            foreach (AudioFrame frame in reader.ReadFrames())
            {
                using (frame)
                {
                    meter.Add(frame);
                }
            }
        }

        Assert.InRange(meter.Integrated, -16.5, -15.5);
        Assert.True(meter.TruePeak <= -0.9, $"{meter.TruePeak:F2} dBTP");
    }

    [Fact]
    public void VideoFilesKeepTheirVideoAndRefuseSilenceCuts()
    {
        using Tool tool = new();
        string clip = Path.Combine(Tool.Corpus, "mkv-mjpeg-pcm.mkv");
        ToolRun run = tool.Run("audio", clip, "-o", "out.mkv", "--gain", "-3", "--fade-out", "0.2");
        Assert.True(run.Code == 0, run.Error);
        Assert.Equal(AudioTests.VideoPackets(clip), AudioTests.VideoPackets(tool.PathOf("out.mkv")));
        Assert.Equal(AudioTests.Samples(clip).Length, AudioTests.Samples(tool.PathOf("out.mkv")).Length);

        ToolRun trim = tool.Run("audio", clip, "-o", "trim.mkv", "--trim-silence");
        Assert.Equal(2, trim.Code);
        Assert.Contains("out of step with the video", trim.Error, StringComparison.Ordinal);
        Assert.Equal(0, tool.Run("audio", clip, "-o", "trim.flac", "--trim-silence").Code);
    }

    [Fact]
    public void EncodeFiltersTheAudioItAdds()
    {
        using Tool tool = new();
        EncodeTests.MakeImages(tool, "in", 10, 32, 32);
        ToolRun run = tool.Run("encode", "in/*.png", "-o", "clip.mkv", "--rate", "10", "--audio", Wav, "--gain", "-3", "--fade-out", "0.2");
        Assert.True(run.Code == 0, run.Error);
        AudioTrackInfo track = MediaFile.Probe(tool.PathOf("clip.mkv")).Tracks.OfType<AudioTrackInfo>().Single();
        Assert.Equal(CodecIds.Flac, track.CodecId);
        Assert.Equal(2, tool.Run("encode", "in/*.png", "-o", "clip2.mkv", "--gain", "-3").Code);
    }

    [Fact]
    public void RunFiltersTheAudioItCopies()
    {
        using Tool tool = new();
        string clip = Path.Combine(Tool.Corpus, "mkv-mjpeg-pcm.mkv");
        ToolRun run = tool.Run("run", clip, "grayscale", "+", "save", "out.mkv", "--af", "volume=0.5");
        Assert.True(run.Code == 0, run.Error);
        AudioTrackInfo track = MediaFile.Probe(tool.PathOf("out.mkv")).Tracks.OfType<AudioTrackInfo>().Single();
        Assert.Equal(CodecIds.Flac, track.CodecId);
        int[] before = AudioTests.Samples(clip), after = AudioTests.Samples(tool.PathOf("out.mkv"));
        Assert.Equal(before.Length, after.Length);
        Assert.Equal(2, tool.Run("run", clip, "grayscale", "+", "save", "x.mkv", "--trim-silence").Code);
    }
}
