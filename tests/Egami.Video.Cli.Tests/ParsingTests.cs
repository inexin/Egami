using Egami.Video.Cli.Host;

namespace Egami.Video.Cli.Tests;

/// <summary>The --track and time parsers (Build Plan task 3): every form, and every error with its message.</summary>
public sealed class ParsingTests
{
    [Theory]
    [InlineData("v", TrackKind.Video, null, null, null)]
    [InlineData("a", TrackKind.Audio, null, null, null)]
    [InlineData("s", TrackKind.Subtitle, null, null, null)]
    [InlineData("d", TrackKind.Other, null, null, null)]
    [InlineData("video", TrackKind.Video, null, null, null)]
    [InlineData("a:1", TrackKind.Audio, 1, null, null)]
    [InlineData("v:0", TrackKind.Video, 0, null, null)]
    [InlineData("s:eng", TrackKind.Subtitle, null, "eng", null)]
    [InlineData("a:pt-BR", TrackKind.Audio, null, "pt-BR", null)]
    [InlineData("3", null, null, null, 3)]
    [InlineData("0", null, null, null, 0)]
    public void TrackForms(string text, TrackKind? kind, int? index, string? language, int? number)
    {
        TrackSpec spec = TrackSpec.Parse(text);
        Assert.Equal(kind, spec.Kind);
        Assert.Equal(index, spec.Index);
        Assert.Equal(language, spec.Language);
        Assert.Equal(number, spec.Number);
    }

    [Theory]
    [InlineData("")]
    [InlineData("x")]
    [InlineData("a:")]
    [InlineData("a:1.5")]
    [InlineData("a:e n")]
    [InlineData("-1")]
    [InlineData("99999999999")]
    [InlineData("v:1:2")]
    public void TrackErrors(string text)
    {
        UsageException error = Assert.Throws<UsageException>(() => TrackSpec.Parse(text));
        Assert.Contains($"--track '{text}' is not a track", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TrackSelectionPicksByKindIndexLanguageAndNumber()
    {
        TrackInfo[] tracks =
        [
            new VideoTrackInfo { Number = 1, Kind = TrackKind.Video, CodecId = CodecIds.Vp9, TimeBase = new Rational(1, 1000), Width = 2, Height = 2 },
            new AudioTrackInfo { Number = 2, Kind = TrackKind.Audio, CodecId = CodecIds.Opus, TimeBase = new Rational(1, 1000), SampleRate = 48000, Channels = 2, Language = "eng" },
            new AudioTrackInfo { Number = 3, Kind = TrackKind.Audio, CodecId = CodecIds.Opus, TimeBase = new Rational(1, 1000), SampleRate = 48000, Channels = 2, Language = "nor" },
            new TrackInfo { Number = 4, Kind = TrackKind.Subtitle, CodecId = "S_TEXT/UTF8", TimeBase = new Rational(1, 1000), Language = "en" },
        ];
        Assert.Equal([1, 2, 3, 4], TrackSelection.All.Select(tracks, "x").Select(t => t.Number));
        Assert.Equal([3], TrackSelection.Parse(["a:1"], false, false).Select(tracks, "x").Select(t => t.Number));
        Assert.Equal([4], TrackSelection.Parse(["s:eng"], false, false).Select(tracks, "x").Select(t => t.Number));
        Assert.Equal([2], TrackSelection.Parse(["a:en"], false, false).Select(tracks, "x").Select(t => t.Number));
        Assert.Equal([1, 3], TrackSelection.Parse(["3", "v"], false, false).Select(tracks, "x").Select(t => t.Number));
        Assert.Equal([1, 3], TrackSelection.Parse(["v,3"], false, false).Select(tracks, "x").Select(t => t.Number));
        Assert.Equal([1, 4], TrackSelection.Parse(null, true, false).Select(tracks, "x").Select(t => t.Number));
        Assert.Equal([1, 2, 3], TrackSelection.Parse(null, false, true).Select(tracks, "x").Select(t => t.Number));

        UsageException error = Assert.Throws<UsageException>(() => TrackSelection.Parse(["a:2"], false, false).Select(tracks, "in.mkv"));
        Assert.Equal("in.mkv: --track a:2 matches no track; it has 1 (video VP9), 2 (audio Opus, eng), 3 (audio Opus, nor), 4 (subtitle SubRip, en).", error.Message);
    }

    [Theory]
    [InlineData("90.5", 90.5)]
    [InlineData("0", 0)]
    [InlineData("90.5s", 90.5)]
    [InlineData("500ms", 0.5)]
    [InlineData("2m", 120)]
    [InlineData("1h", 3600)]
    [InlineData("1:30", 90)]
    [InlineData("1:30.25", 90.25)]
    [InlineData("01:02:03.250", 3723.25)]
    [InlineData("0:00:00.001", 0.001)]
    public void TimeForms(string text, double seconds)
    {
        TimeValue value = TimeValue.Parse(text, "--from");
        Assert.False(value.IsFrame);
        Assert.Equal(seconds, value.Seconds, 9);
    }

    [Fact]
    public void FrameNumber()
    {
        TimeValue value = TimeValue.Parse("#1234", "--at");
        Assert.True(value.IsFrame);
        Assert.Equal(1234, value.Frame);
        MediaTime[] times = [MediaTime.FromSeconds(0), MediaTime.FromSeconds(0.04)];
        Assert.Equal(0.04, TimeValue.Parse("#1", "--at").Resolve(() => times, "x", "--at").Seconds, 9);
        UsageException error = Assert.Throws<UsageException>(() => TimeValue.Parse("#2", "--at").Resolve(() => times, "in.avi", "--at"));
        Assert.Equal("in.avi: --at #2 is past the last frame; the video has 2 frames (#0 to #1).", error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1:60")]
    [InlineData("1:2:3:4")]
    [InlineData("1::2")]
    [InlineData("-5")]
    [InlineData("#")]
    [InlineData("#-1")]
    [InlineData("#1.5")]
    [InlineData("1,5")]
    [InlineData("1:30:")]
    public void TimeErrors(string text)
    {
        UsageException error = Assert.Throws<UsageException>(() => TimeValue.Parse(text, "--from"));
        Assert.Equal($"--from '{text}' is not a time: give seconds (90.5), [HH:]MM:SS[.mmm] (1:30, 01:02:03.250) or a frame number (#1234).", error.Message);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("#0")]
    public void DurationsMustBePositive(string text)
    {
        Assert.Throws<UsageException>(() => TimeValue.ParseDuration(text, "--every"));
    }

    [Theory]
    [InlineData(0, "0:00:00.000")]
    [InlineData(90.5, "0:01:30.500")]
    [InlineData(3723.25, "1:02:03.250")]
    [InlineData(-1.5, "-0:00:01.500")]
    public void FormatTimes(double seconds, string text) => Assert.Equal(text, TimeValue.Format(seconds));
}
