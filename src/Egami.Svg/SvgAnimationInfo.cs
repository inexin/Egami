using Egami.Svg.Animation;

namespace Egami.Svg;

/// <summary>
/// What a document's SMIL and CSS animations do over time, as <see cref="SvgDocument.Animation"/> finds them: whether it
/// animates, how long it plays and whether it loops. Animations that wait for a click, a key or a wall-clock time never begin, as in a browser
/// nobody touches.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Svg.cs" region="animation"/>
/// </example>
public sealed class SvgAnimationInfo
{
    internal SvgAnimationInfo(bool hasAnimations, TimeSpan? duration, TimeSpan? loopPeriod, bool hasEventTriggers)
    {
        HasAnimations = hasAnimations;
        Duration = duration;
        LoopPeriod = loopPeriod;
        HasEventTriggers = hasEventTriggers;
    }

    /// <summary>
    /// Gets a value indicating whether the document has animation elements (animate, set, animateTransform, animateMotion,
    /// discard) with a target, or elements whose style names an @keyframes rule in animation-name.
    /// </summary>
    public bool HasAnimations { get; }

    /// <summary>
    /// Gets when the last animation ends, or <see langword="null"/> when one runs forever (or the document has none); zero when
    /// no animation ever begins.
    /// </summary>
    public TimeSpan? Duration { get; }

    /// <summary>
    /// Gets the period after which the whole document repeats, when every animation repeats forever: the least common multiple
    /// of their periods on a 1 ms grid, if it is at most 10 seconds and every animation has begun by then; otherwise
    /// <see langword="null"/>.
    /// </summary>
    public TimeSpan? LoopPeriod { get; }

    /// <summary>Gets a value indicating whether some animation waits for a user event, an access key or a wall-clock time to begin, and so never plays.</summary>
    public bool HasEventTriggers { get; }

    /// <summary>The longest loop period reported, in milliseconds.</summary>
    internal const long MaxLoopMilliseconds = 10_000;

    /// <summary>The latest end, in seconds, a CSS animation is given as a duration; past it (about 31 years) it counts as never ending.</summary>
    internal const double MaxEndSeconds = 1e9;

    /// <summary>Reads the duration and loop period off a timeline.</summary>
    internal static SvgAnimationInfo From(SvgTimeline timeline)
    {
        List<SvgAnimation> animations = timeline.Animations;
        if (animations.Count == 0 && timeline.CssAnimations.Count == 0)
        {
            return new SvgAnimationInfo(false, null, null, false);
        }

        bool events = animations.Exists(a => a.WaitsForEvent);
        bool forever = timeline.Truncated;
        double last = 0;
        long lcm = 0;
        bool periodic = true;
        double latestBegin = 0;
        foreach (SvgAnimation a in animations)
        {
            if (a.Intervals.Count == 0)
            {
                continue;
            }

            SvgInterval final = a.Intervals[^1];
            bool runsForever = double.IsPositiveInfinity(final.End) || final.End >= timeline.Horizon || a.ReachesHorizon;
            forever |= runsForever;
            if (double.IsFinite(final.End))
            {
                last = Math.Max(last, final.End);
            }

            latestBegin = Math.Max(latestBegin, a.Intervals[0].Begin);
            if (!periodic)
            {
                continue;
            }

            if (!runsForever || a.Function?.Accumulates == true || a.Motion?.Accumulates == true)
            {
                periodic = false;
                continue;
            }

            double period = Period(a);
            if (double.IsNaN(period))
            {
                periodic = false;
            }
            else if (period > 0)
            {
                long ms = (long)Math.Round(period * 1000);
                if (ms <= 0)
                {
                    periodic = false;
                    continue;
                }

                lcm = lcm == 0 ? ms : Lcm(lcm, ms);
                if (lcm > MaxLoopMilliseconds)
                {
                    periodic = false;
                }
            }
        }

        // CSS animations: one that repeats forever loops every duration (two for alternate directions); a paused one never moves.
        foreach ((_, SvgCssAnimation css) in timeline.CssAnimations)
        {
            if (css.Paused || css.Duration == 0)
            {
                continue;
            }

            if (double.IsPositiveInfinity(css.Iterations))
            {
                forever = true;
                latestBegin = Math.Max(latestBegin, css.Delay);
                // In double first: a huge duration must not wrap round in long.
                double periodMs = Math.Round(css.Duration * (css.Direction is SvgCssDirection.Alternate or SvgCssDirection.AlternateReverse ? 2 : 1) * 1000);
                if (!(periodMs > 0 && periodMs <= MaxLoopMilliseconds))
                {
                    periodic = false;
                    continue;
                }

                long ms = (long)periodMs;

                lcm = lcm == 0 ? ms : Lcm(lcm, ms);
                periodic &= lcm <= MaxLoopMilliseconds;
            }
            else if (double.IsFinite(css.End) && css.End <= MaxEndSeconds)
            {
                last = Math.Max(last, css.End);
                periodic = false;
            }
            else
            {
                // An end too far off to be a TimeSpan (animation: a 1e12s, or 1e300 iterations) counts as never ending.
                forever = true;
                periodic = false;
            }
        }

        TimeSpan? duration = forever ? null : TimeSpan.FromSeconds(last);
        TimeSpan? loop = periodic && lcm > 0 && latestBegin <= lcm / 1000.0 ? TimeSpan.FromMilliseconds(lcm) : null;
        return new SvgAnimationInfo(true, duration, loop, events);
    }

    /// <summary>
    /// The period of an animation that runs forever: its simple duration when one interval repeats forever (0 when its value
    /// never changes), or the spacing of a loop's intervals when they come at a steady pace; NaN when it has none.
    /// </summary>
    private static double Period(SvgAnimation a)
    {
        SvgInterval final = a.Intervals[^1];
        if (double.IsPositiveInfinity(final.End))
        {
            if (!double.IsPositiveInfinity(final.PlayEnd))
            {
                // Played once, then held by min: constant from then on.
                return 0;
            }

            double d = a.SimpleDuration;
            return double.IsFinite(d) ? d : 0;
        }

        int n = a.Intervals.Count;
        if (n < 3)
        {
            return double.NaN;
        }

        double spacing = a.Intervals[^1].Begin - a.Intervals[^2].Begin;
        double length = a.Intervals[^1].End - a.Intervals[^1].Begin;
        for (int i = Math.Max(1, n - 4); i < n; i++)
        {
            SvgInterval x = a.Intervals[i];
            SvgInterval p = a.Intervals[i - 1];
            if (Math.Abs(x.Begin - p.Begin - spacing) > 1e-6 || Math.Abs(x.End - x.Begin - length) > 1e-6)
            {
                return double.NaN;
            }
        }

        return spacing;
    }

    private static long Lcm(long a, long b)
    {
        long x = a, y = b;
        while (y != 0)
        {
            (x, y) = (y, x % y);
        }

        return a / x > long.MaxValue / b ? long.MaxValue : a / x * b;
    }
}
