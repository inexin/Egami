using Egami.Video.Subtitles;

namespace Egami.Video.Tests.Subtitles;

public class SubtitleTimingTests
{
    private static readonly Rational Ms = new(1, 1000);

    private static MediaTime At(long ms) => new(ms, Ms);

    private static SubtitleDocument Document(params (long Start, long End)[] cues)
    {
        SubtitleDocument document = new();
        foreach ((long start, long end) in cues)
        {
            document.Cues.Add(new SubtitleCue(At(start), At(end), "x"));
        }

        return document;
    }

    [Fact]
    public void ShiftMovesEveryCueAndKaraokeTime()
    {
        SubtitleDocument document = Document((1000, 2000), (5000, 6000));
        document.Cues[0].Spans[0] = new SubtitleSpan("k") { Timestamp = At(1500) };
        document.Shift(MediaTime.FromSeconds(-1.5));
        Assert.Equal([At(-500), At(3500)], document.Cues.Select(c => c.Start));
        Assert.Equal(At(0), document.Cues[0].Spans[0].Timestamp);
    }

    [Fact]
    public void ShiftByAFrameIsExact()
    {
        // 1001/30000 s does not fit milliseconds; about 3 hours of single-frame shifts must still land on the frame grid.
        SubtitleDocument document = Document((0, 1000));
        MediaTime frame = new(1001, new Rational(1, 30000));
        for (int i = 0; i < 323_676; i++)
        {
            document.Shift(frame);
        }

        Assert.Equal(new MediaTime(323_676L * 1001, new Rational(1, 30000)), document.Cues[0].Start);
        Assert.Equal("02:59:59,989 -->", document.ToText().Split('\n')[1][..16]);
    }

    [Fact]
    public void ScaleFromFilmToPalIsExactOverThreeHours()
    {
        SubtitleDocument document = Document((10_800_000, 10_801_000));
        document.Scale(new Rational(24000, 25025));
        Assert.Equal(new MediaTime(10_800L * 24000, new Rational(1, 25025)), document.Cues[0].Start);
        Assert.Equal(10_357_642, document.Cues[0].Start.Rescale(Ms).Ticks);
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Scale(Rational.Zero));
    }

    [Fact]
    public void SyncFixesOffsetAndSpeedTogether()
    {
        SubtitleDocument document = Document((10_000, 11_000), (3_600_000, 3_601_000), (7_200_000, 7_201_000));

        // The subtitles run 0.1% fast and 2 s late.
        document.Sync(At(10_000), At(8_000), At(7_200_000), At(7_190_810));
        Assert.Equal([At(8_000), At(3_594_410), At(7_190_810)], document.Cues.Select(c => c.Start));
        Assert.Throws<ArgumentException>(() => document.Sync(At(1), At(2), At(1), At(3)));
    }

    [Fact]
    public void ClipRemovesAndCutsCues()
    {
        SubtitleDocument document = Document((0, 1000), (1500, 2500), (3000, 4000), (4500, 6000), (7000, 8000));
        Assert.Equal(2, document.Clip(At(2000), At(5000)));
        Assert.Equal([(At(2000), At(2500)), (At(3000), At(4000)), (At(4500), At(5000))], document.Cues.Select(c => (c.Start, c.End)));
        Assert.Throws<ArgumentException>(() => document.Clip(At(2), At(1)));
    }

    [Fact]
    public void SortAndFixOverlaps()
    {
        SubtitleDocument document = Document((5000, 7000), (1000, 3000), (2000, 4000), (2000, 2500), (6000, 6500));
        document.Cues[4].Layer = 1;
        // Cues that start together are left alone, as are cues on other layers.
        Assert.Equal(1, document.FixOverlaps());
        Assert.Equal([(1000, 2000), (2000, 2500), (2000, 4000), (5000, 7000), (6000, 6500)],
            document.Cues.Select(c => (c.Start.Rescale(Ms).Ticks, c.End.Rescale(Ms).Ticks)));
    }

    [Fact]
    public void TimesAreWrittenRoundedPerFormat()
    {
        SubtitleDocument document = Document((0, 0));
        document.Cues[0].Start = new MediaTime(1001, new Rational(1, 30000));
        document.Cues[0].End = new MediaTime(36_000_000_005, new Rational(1, 1_000_000_000));
        string srt = document.ToText(SubtitleFormat.Srt);
        Assert.Contains("00:00:00,033 --> 00:00:36,000", srt, StringComparison.Ordinal);
        string ass = document.ToText(SubtitleFormat.Ass);
        Assert.Contains("0:00:00.03,0:00:36.00", ass, StringComparison.Ordinal);
    }
}
