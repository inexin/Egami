using System.Globalization;

namespace Egami.Svg.Animation;

/// <summary>
/// Which moments of a document become frames: from the start, one every 1/rate seconds, for the length the options or the
/// document give: one loop period, else up to the end of the last animation, else the maximum length.
/// </summary>
internal readonly record struct SvgFramePlan(TimeSpan Start, long LengthTicks, long Count, double Rate, bool Loops, string? Warning)
{
    /// <summary>Plans the frames of a document with the given options.</summary>
    public static SvgFramePlan Of(SvgAnimationInfo info, SvgAnimationOptions options)
    {
        double rate = options.FrameRate;
        string? warning = null;
        long length;
        bool endInclusive = false;
        bool loops = info.LoopPeriod is not null;
        if (options.Length is TimeSpan given)
        {
            length = given.Ticks;
        }
        else if (!info.HasAnimations)
        {
            length = 0;
            endInclusive = true;
        }
        else if (info.LoopPeriod is TimeSpan period)
        {
            length = period.Ticks;
            if (length > options.MaxLength.Ticks)
            {
                // Part of a loop does not loop.
                warning = string.Create(CultureInfo.InvariantCulture, $"The animation loops every {period.TotalSeconds:0.###} s; frames are drawn for the first {options.MaxLength.TotalSeconds:0.###} s (SvgAnimationOptions.MaxLength).");
                length = options.MaxLength.Ticks;
                loops = false;
            }
        }
        else if (info.Duration is TimeSpan end)
        {
            // Up to the moment the last animation ends, which is drawn too: a document that plays once ends on its final state.
            length = Math.Max(0, end.Ticks - options.Start.Ticks);
            endInclusive = true;
            if (length > options.MaxLength.Ticks)
            {
                warning = string.Create(CultureInfo.InvariantCulture, $"The animation ends after {end.TotalSeconds:0.###} s; frames are drawn for the first {options.MaxLength.TotalSeconds:0.###} s (SvgAnimationOptions.MaxLength).");
                length = options.MaxLength.Ticks;
                endInclusive = false;
            }
        }
        else
        {
            warning = string.Create(CultureInfo.InvariantCulture, $"The animation runs forever without a common loop period; frames are drawn for the first {options.MaxLength.TotalSeconds:0.###} s (SvgAnimationOptions.MaxLength).");
            length = options.MaxLength.Ticks;
        }

        // In double first: a long length at a high rate must not wrap round in long.
        double frames = length / (double)TimeSpan.TicksPerSecond * rate;
        double count = endInclusive ? Math.Floor(frames + 1e-6) + 1 : Math.Max(1, Math.Ceiling(frames - 1e-6));
        return new SvgFramePlan(options.Start, length, (long)Math.Min(count, long.MaxValue / 2), rate, loops, warning);
    }

    /// <summary>The offset of frame <paramref name="index"/> from the start, in ticks.</summary>
    public long Offset(long index) => (long)Math.Round(index * (TimeSpan.TicksPerSecond / Rate));

    /// <summary>The moment frame <paramref name="index"/> shows.</summary>
    public TimeSpan TimeOf(long index) => Start + TimeSpan.FromTicks(Offset(index));

    /// <summary>How long frame <paramref name="index"/> lasts: to the next frame, the last one cut at the length when the length ends between frames.</summary>
    public TimeSpan DurationOf(long index)
    {
        long next = Offset(index + 1);
        if (index == Count - 1 && LengthTicks > Offset(index) && next > LengthTicks)
        {
            next = LengthTicks;
        }

        return TimeSpan.FromTicks(next - Offset(index));
    }
}
