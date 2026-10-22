namespace Egami.Video.Tests.Core;

public class MediaTimeTests
{
    private static readonly Rational Ntsc = new(1001, 30000);

    [Fact]
    public void RationalReducesAndKeepsSignInNumerator()
    {
        Rational r = new(60000, -2002);
        Assert.Equal(-30000, r.Numerator);
        Assert.Equal(1001, r.Denominator);
        Assert.Equal("-30000/1001", r.ToString());
        Assert.Equal(new Rational(25, 1), Rational.Parse("25"));
        Assert.Equal(new Rational(30000, 1001), Rational.Parse("30000/1001"));
        Assert.Equal(1, default(Rational).Denominator);
    }

    [Fact]
    public void NtscOverTwentyFourHoursStaysExact()
    {
        // Frames across a day at 29.97 fps: the frame count converts to nanoseconds and back without drift.
        long frames = 24L * 3600 * 30000 / 1001;
        for (long frame = 0; frame <= frames; frame += 9973)
        {
            MediaTime t = new(frame, Ntsc);
            MediaTime ns = t.Rescale(Rational.Nanosecond);
            Assert.Equal(frame, ns.Rescale(Ntsc).Ticks);

            // The nanosecond value is the exact instant rounded once, never an accumulated sum.
            Int128 exactNumerator = (Int128)frame * 1001 * 1_000_000_000;
            Assert.True(Int128.Abs(((Int128)ns.Ticks * 30000) - exactNumerator) * 2 <= 30000);
        }

        MediaTime last = new(frames, Ntsc);
        Assert.Equal(TimeSpan.FromTicks((long)MediaTime.DivideRounded((Int128)frames * 1001 * 10_000_000, 30000)), last.ToTimeSpan());
        Assert.True(last.ToTimeSpan() < TimeSpan.FromDays(1));
    }

    [Fact]
    public void SummingFrameDurationsMatchesTheFrameCount()
    {
        MediaTime duration = new(1, Ntsc);
        MediaTime t = new(0, Ntsc);
        for (int i = 0; i < 100_000; i++)
        {
            t += duration;
        }

        Assert.Equal(new MediaTime(100_000, Ntsc), t);
        Assert.Equal((long)MediaTime.DivideRounded(100_000L * 1001 * 1_000_000_000, 30000), t.Rescale(Rational.Nanosecond).Ticks);
    }

    [Fact]
    public void ComparesAcrossTimeBases()
    {
        MediaTime oneSecondInFrames = new(25, new Rational(1, 25));
        MediaTime oneSecondInNs = new(1_000_000_000, Rational.Nanosecond);
        Assert.Equal(oneSecondInFrames, oneSecondInNs);
        Assert.Equal(oneSecondInFrames.GetHashCode(), oneSecondInNs.GetHashCode());
        Assert.True(new MediaTime(30, Ntsc) > new MediaTime(1, new Rational(1, 1)));
        Assert.True(new MediaTime(29, Ntsc) < new MediaTime(1, new Rational(1, 1)));
        Assert.True(new MediaTime(-1, Ntsc) < MediaTime.Zero);
        Assert.True(new MediaTime(long.MaxValue, Rational.Nanosecond) < new MediaTime(long.MaxValue, new Rational(1, 1)));
    }

    [Fact]
    public void RescaleRoundsHalvesAwayFromZero()
    {
        Assert.Equal(2, new MediaTime(3, new Rational(1, 2)).Rescale(new Rational(1, 1)).Ticks);
        Assert.Equal(-2, new MediaTime(-3, new Rational(1, 2)).Rescale(new Rational(1, 1)).Ticks);
        Assert.Equal(1, new MediaTime(2, new Rational(1, 3)).Rescale(new Rational(1, 1)).Ticks);
    }

    [Fact]
    public void RejectsBadTimeBases()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MediaTime(1, new Rational(-1, 25)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Rational(1, 0));
        Assert.Throws<OverflowException>(() => new MediaTime(long.MaxValue, new Rational(1, 1)).Rescale(Rational.Nanosecond));
    }
}
