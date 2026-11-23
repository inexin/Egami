using Egami.Svg.Animation;
using Egami.Svg.Reading;

namespace Egami.Svg.Tests;

/// <summary>SVG animation stage A, task 1: clock values, begin and end lists, SMIL intervals and what the timeline finds.</summary>
public class AnimationTimingTests
{
    private const string Ns = "xmlns='http://www.w3.org/2000/svg' xmlns:xlink='http://www.w3.org/1999/xlink'";

    internal static SvgTimeline Timeline(string body, out SvgReport report)
    {
        SvgDocument document = SvgDocument.Parse($"<svg {Ns} width='100' height='100'>{body}</svg>");
        report = new SvgReport(strict: false);
        return SvgTimeline.Build(document, report);
    }

    internal static SvgTimeline Timeline(string body) => Timeline(body, out _);

    private static List<SvgInterval> Intervals(string body, string id = "a") =>
        Timeline(body).FindById(id)!.Intervals;

    private static void AssertIntervals(List<SvgInterval> actual, params (double Begin, double End)[] expected)
    {
        Assert.Equal(expected.Length, actual.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Begin, actual[i].Begin, 9);
            Assert.Equal(expected[i].End, actual[i].End, 9);
        }
    }

    [Theory]
    [InlineData("02:30:03", 9003)]
    [InlineData("50:00:10.25", 180010.25)]
    [InlineData("02:33", 153)]
    [InlineData("00:10.5", 10.5)]
    [InlineData("3.2h", 11520)]
    [InlineData("45min", 2700)]
    [InlineData("30s", 30)]
    [InlineData("5ms", 0.005)]
    [InlineData("12.467", 12.467)]
    [InlineData(" 1.5s ", 1.5)]
    public void ClockValuesRead(string text, double seconds)
    {
        Assert.True(SvgTiming.TryParseClock(text, out double value));
        Assert.Equal(seconds, value, 9);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1:2")]
    [InlineData("02:60")]
    [InlineData("60:00")]
    [InlineData("5 s")]
    [InlineData("abc")]
    [InlineData("1.s")]
    [InlineData("-1s")]
    [InlineData("1sec")]
    [InlineData("01:02:03:04")]
    public void BadClockValuesAreRefused(string text) => Assert.False(SvgTiming.TryParseClock(text, out _));

    [Fact]
    public void BeginValuesRead()
    {
        static SvgTimingValue Read(string text)
        {
            Assert.True(SvgTiming.TryParseValue(text, out SvgTimingValue value), text);
            return value;
        }

        Assert.Equal(new SvgTimingValue(SvgTimingKind.Offset, 2), Read("2s"));
        Assert.Equal(new SvgTimingValue(SvgTimingKind.Offset, -1), Read("-1s"));
        Assert.Equal(new SvgTimingValue(SvgTimingKind.Offset, 1.5), Read("+1.5s"));
        Assert.Equal(new SvgTimingValue(SvgTimingKind.SyncBegin, 0, "a"), Read("a.begin"));
        Assert.Equal(new SvgTimingValue(SvgTimingKind.SyncEnd, 1, "a"), Read("a.end+1s"));
        Assert.Equal(new SvgTimingValue(SvgTimingKind.SyncEnd, -0.5, "a"), Read("a.end - 0.5s"));
        Assert.Equal(new SvgTimingValue(SvgTimingKind.Repeat, 1, "a", 2), Read("a.repeat(2)+1s"));
        Assert.Equal(new SvgTimingValue(SvgTimingKind.SyncBegin, 0, "a.b"), Read(@"a\.b.begin"));
        Assert.Equal(SvgTimingKind.Event, Read("click").Kind);
        Assert.Equal(new SvgTimingValue(SvgTimingKind.Event, 2), Read("button.click+2s"));
        Assert.Equal(SvgTimingKind.Event, Read("accessKey(x)").Kind);
        Assert.Equal(SvgTimingKind.Event, Read("wallclock(2026-01-01T00:00Z)").Kind);
        Assert.Equal(SvgTimingKind.Event, Read("repeat(1)").Kind);
        Assert.Equal(SvgTimingKind.Indefinite, Read("indefinite").Kind);
    }

    [Theory]
    [InlineData("a.begin+")]
    [InlineData("a.repeat(x)")]
    [InlineData(".begin")]
    [InlineData("a.end 1s")]
    [InlineData("accessKey(x")]
    [InlineData("a.begin+1:2")]
    public void BadBeginValuesAreRefused(string text) => Assert.False(SvgTiming.TryParseValue(text, out _));

    [Fact]
    public void ListsKeepWhatReads()
    {
        List<SvgTimingValue> values = SvgTiming.ParseList("0; a.end; bad value;", out int errors);
        Assert.Equal(2, values.Count);
        Assert.Equal(1, errors);
    }

    [Fact]
    public void ASimpleIntervalRunsFromBeginForDur() =>
        AssertIntervals(Intervals("<rect><animate id='a' attributeName='x' from='0' to='1' begin='1s' dur='2s'/></rect>"), (1, 3));

    [Theory]
    [InlineData("repeatCount='2.5'", 5)]
    [InlineData("repeatDur='3s'", 3)]
    [InlineData("repeatCount='4' repeatDur='3s'", 3)]
    [InlineData("end='1.5s'", 1.5)]
    [InlineData("end='indefinite'", 2)]
    [InlineData("min='3s'", 3)]
    [InlineData("max='0.5s'", 0.5)]
    [InlineData("min='3s' max='1s'", 2)]
    public void ActiveDurationsFollowSmil(string attributes, double active) =>
        AssertIntervals(Intervals($"<rect><animate id='a' attributeName='x' from='0' to='1' dur='2s' {attributes}/></rect>"), (0, active));

    [Fact]
    public void IndefiniteRepeatsNeverEnd() =>
        AssertIntervals(Intervals("<rect><animate id='a' attributeName='x' from='0' to='1' dur='2s' repeatCount='indefinite'/></rect>"), (0, double.PositiveInfinity));

    [Fact]
    public void EndWithoutDurCutsAnIndefiniteSimpleDuration() =>
        AssertIntervals(Intervals("<rect><set id='a' attributeName='x' to='1' begin='1s' end='4s'/></rect>"), (1, 4));

    [Fact]
    public void ABeginListGivesSeveralIntervals() =>
        AssertIntervals(Intervals("<rect><animate id='a' attributeName='x' from='0' to='1' begin='5s; 0s' dur='2s'/></rect>"), (0, 2), (5, 7));

    [Fact]
    public void AnEndListOfOffsetsEndsTheIntervalsItCanEnd() =>
        AssertIntervals(Intervals("<rect><set id='a' attributeName='x' to='1' begin='0;5s' end='3s'/></rect>"), (0, 3));

    [Fact]
    public void RestartDecidesWhatABeginDuringAnIntervalDoes()
    {
        static List<SvgInterval> With(string restart) =>
            Intervals($"<rect><animate id='a' attributeName='x' from='0' to='1' begin='0;1s;2.5s' dur='2s' restart='{restart}'/></rect>");

        AssertIntervals(With("always"), (0, 1), (1, 2.5), (2.5, 4.5));
        AssertIntervals(With("whenNotActive"), (0, 2), (2.5, 4.5));
        AssertIntervals(With("never"), (0, 2));
    }

    [Fact]
    public void SyncbaseValuesFollowTheElementTheyName()
    {
        SvgTimeline timeline = Timeline("""
            <rect>
              <animate id='b' attributeName='y' from='0' to='1' begin='a.end+0.5s' dur='1s'/>
              <animate id='c' attributeName='y' from='0' to='1' begin='b.begin-1s' dur='1s'/>
              <animate id='a' attributeName='x' from='0' to='1' begin='0' dur='1s'/>
            </rect>
            """);
        AssertIntervals(timeline.FindById("b")!.Intervals, (1.5, 2.5));
        AssertIntervals(timeline.FindById("c")!.Intervals, (0.5, 1.5));
    }

    [Fact]
    public void RepeatValuesNameARepetition() =>
        AssertIntervals(
            Intervals("<rect><animate id='r' attributeName='x' from='0' to='1' dur='1s' repeatCount='3'/><set id='a' attributeName='y' to='5' begin='r.repeat(2)' dur='0.5s'/></rect>"),
            (2, 2.5));

    [Fact]
    public void ALoopOfTwoAnimationsRunsToTheHorizonAndLoops()
    {
        SvgTimeline timeline = Timeline("""
            <rect>
              <animate id='a' attributeName='x' from='0' to='10' begin='0;b.end' dur='1s'/>
              <animate id='b' attributeName='x' from='10' to='0' begin='a.end' dur='1s'/>
            </rect>
            """, out SvgReport report);
        List<SvgInterval> a = timeline.FindById("a")!.Intervals;
        Assert.Equal((0.0, 1.0), (a[0].Begin, a[0].End));
        Assert.Equal((2.0, 3.0), (a[1].Begin, a[1].End));
        Assert.True(a[^1].End >= timeline.Horizon - 2);
        Assert.Empty(report.Warnings);
        SvgAnimationInfo info = SvgAnimationInfo.From(timeline);
        Assert.Null(info.Duration);
        Assert.Equal(TimeSpan.FromSeconds(2), info.LoopPeriod);
    }

    [Fact]
    public void BeginsBeforeTheDocumentCount() =>
        AssertIntervals(Intervals("<rect><animate id='a' attributeName='x' from='0' to='1' begin='-1s' dur='3s'/></rect>"), (-1, 2));

    [Fact]
    public void IntervalsThatEndBeforeTheDocumentBeginsAreSkipped() =>
        Assert.Empty(Intervals("<rect><animate id='a' attributeName='x' from='0' to='1' begin='-5s' dur='1s' fill='freeze'/></rect>"));

    [Fact]
    public void ALoopThatNeverMovesTimeForwardIsCutAndReported()
    {
        SvgTimeline timeline = Timeline("""
            <rect>
              <set id='a' attributeName='x' to='1' begin='0;b.begin-1s' dur='1s'/>
              <set id='b' attributeName='y' to='1' begin='a.begin' dur='1s'/>
            </rect>
            """, out SvgReport report);
        Assert.Contains(report.Warnings, w => w.Id == "Svg.AnimationCycle");
        // The link back to the earlier element is cut; b still begins with a.
        AssertIntervals(timeline.FindById("a")!.Intervals, (0, 1));
        AssertIntervals(timeline.FindById("b")!.Intervals, (0, 1));
    }

    [Fact]
    public void EventsNeverFire()
    {
        SvgTimeline timeline = Timeline("<rect><set id='a' attributeName='x' to='1' begin='click'/><set id='b' attributeName='y' to='1' begin='0' end='click'/></rect>");
        Assert.Empty(timeline.FindById("a")!.Intervals);
        AssertIntervals(timeline.FindById("b")!.Intervals, (0, double.PositiveInfinity));
        SvgAnimationInfo info = SvgAnimationInfo.From(timeline);
        Assert.True(info.HasEventTriggers);
        Assert.True(info.HasAnimations);
    }

    [Fact]
    public void BadTimingIsReported()
    {
        SvgTimeline timeline = Timeline("<rect><animate id='a' attributeName='x' from='0' to='1' begin='0; nonsense+' dur='-1s' repeatCount='0'/></rect>", out SvgReport report);
        Assert.Equal(3, report.Warnings.Count(w => w.Id == "Svg.BadAnimation"));
        AssertIntervals(timeline.FindById("a")!.Intervals, (0, double.PositiveInfinity));
    }

    [Fact]
    public void AnimationsWithoutATargetOrAttributeAreLeftOut()
    {
        SvgTimeline timeline = Timeline("<rect/><animate href='#missing' attributeName='x' to='1'/><rect><animate to='1'/></rect>", out SvgReport report);
        Assert.Empty(timeline.Animations);
        Assert.Equal(2, report.Warnings.Count(w => w.Id == "Svg.BadAnimation"));
    }

    [Fact]
    public void TheInfoOfAFiniteDocument()
    {
        SvgAnimationInfo info = SvgAnimationInfo.From(Timeline("<rect><animate attributeName='x' from='0' to='1' begin='1s' dur='2s'/><set attributeName='y' to='1' dur='0.5s'/></rect>"));
        Assert.True(info.HasAnimations);
        Assert.Equal(TimeSpan.FromSeconds(3), info.Duration);
        Assert.Null(info.LoopPeriod);
        Assert.False(info.HasEventTriggers);
    }

    [Fact]
    public void TheInfoOfAStaticDocument()
    {
        SvgDocument document = SvgDocument.Parse($"<svg {Ns} width='10' height='10'><rect width='5' height='5'/></svg>");
        SvgAnimationInfo info = document.Animation;
        Assert.False(info.HasAnimations);
        Assert.Null(info.Duration);
        Assert.Null(info.LoopPeriod);
    }

    [Theory]
    [InlineData("0.4s", "0.6s", 1200)]
    [InlineData("1s", "1s", 1000)]
    [InlineData("3s", "7s", -1)]
    [InlineData("0.25s", "1/3", -1)]
    public void LoopPeriodsAreTheLeastCommonMultiple(string first, string second, int expected)
    {
        string secondDur = second == "1/3" ? "0.3333s" : second;
        SvgAnimationInfo info = SvgAnimationInfo.From(Timeline($"""
            <rect>
              <animate attributeName='x' from='0' to='1' dur='{first}' repeatCount='indefinite'/>
              <animate attributeName='y' from='0' to='1' dur='{secondDur}' repeatCount='indefinite'/>
            </rect>
            """));
        Assert.Equal(expected < 0 ? null : TimeSpan.FromMilliseconds(expected), info.LoopPeriod);
        Assert.Null(info.Duration);
    }

    [Fact]
    public void AFiniteAnimationOrAccumulationBreaksTheLoop()
    {
        Assert.Null(SvgAnimationInfo.From(Timeline("<rect><animate attributeName='x' from='0' to='1' dur='1s' repeatCount='indefinite'/><set attributeName='y' to='1' dur='3s'/></rect>")).LoopPeriod);
        Assert.Null(SvgAnimationInfo.From(Timeline("<rect><animate attributeName='x' by='1' dur='1s' accumulate='sum' repeatCount='indefinite'/></rect>")).LoopPeriod);
    }

    [Fact]
    public void ALoopMustHaveBegunByItsPeriod()
    {
        Assert.Null(SvgAnimationInfo.From(Timeline("<rect><animate attributeName='x' from='0' to='1' begin='3s' dur='1s' repeatCount='indefinite'/></rect>")).LoopPeriod);
        Assert.Equal(TimeSpan.FromSeconds(1), SvgAnimationInfo.From(Timeline("<rect><animate attributeName='x' from='0' to='1' begin='0.5s' dur='1s' repeatCount='indefinite'/></rect>")).LoopPeriod);
    }

    [Fact]
    public void HostileTimingStopsAtTheBudget()
    {
        // A 1 ms ping-pong over the default horizon needs far more steps than the budget allows.
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        SvgTimeline timeline = Timeline("""
            <rect>
              <set id='a' attributeName='x' to='1' begin='0;b.end' dur='1ms'/>
              <set id='b' attributeName='x' to='2' begin='a.end' dur='1ms'/>
            </rect>
            """, out SvgReport report);
        Assert.True(timeline.Truncated);
        Assert.Contains(report.Warnings, w => w.Id == "Svg.AnimationLimit");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), watch.Elapsed.ToString());
        Assert.Null(SvgAnimationInfo.From(timeline).Duration);
    }

    [Fact]
    public void HugeRepeatCountsStayFinite() =>
        AssertIntervals(Intervals("<rect><animate id='a' attributeName='x' from='0' to='1' dur='1ms' repeatCount='1e9'/></rect>"), (0, 1e6));

    [Fact]
    public void DiscardIsAMoment() =>
        AssertIntervals(Intervals("<rect><discard id='a' begin='2s'/></rect>"), (2, double.PositiveInfinity));
}
