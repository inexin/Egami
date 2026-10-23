namespace Egami.Video.Subtitles;

/// <summary>The timing tools: shift, scale, sync by two points, clip, sort and fix overlaps.</summary>
public sealed partial class SubtitleDocument
{
    /// <summary>Moves every cue by <paramref name="offset"/>: positive to show later, negative to show sooner. Exact for any time base.</summary>
    /// <param name="offset">How far to move.</param>
    public void Shift(MediaTime offset)
    {
        foreach (SubtitleCue cue in Cues)
        {
            cue.Start = ExactTime.Add(cue.Start, offset);
            cue.End = ExactTime.Add(cue.End, offset);
            ShiftKaraoke(cue, t => ExactTime.Add(t, offset));
        }
    }

    /// <summary>
    /// Multiplies every time by <paramref name="factor"/>, exactly. To fit subtitles made for 23.976 fps video to the same
    /// video sped up to 25 fps, scale by <c>new Rational(24000, 25025)</c> (23.976 / 25).
    /// </summary>
    /// <param name="factor">The factor. Must be positive.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="factor"/> is zero or negative.</exception>
    public void Scale(Rational factor)
    {
        if (factor.Numerator <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(factor), factor, "The factor must be positive.");
        }

        foreach (SubtitleCue cue in Cues)
        {
            cue.Start = ExactTime.Multiply(cue.Start, factor);
            cue.End = ExactTime.Multiply(cue.End, factor);
            ShiftKaraoke(cue, t => ExactTime.Multiply(t, factor));
        }
    }

    /// <summary>
    /// Retimes every cue by two sync points: what is at <paramref name="from1"/> moves to <paramref name="to1"/>, what is at
    /// <paramref name="from2"/> to <paramref name="to2"/>, and everything else in proportion. Fixes both an offset and a
    /// speed difference at once; each time is computed from its original, rounded to the nanosecond, so nothing drifts.
    /// </summary>
    /// <param name="from1">A time in the subtitles, such as the first line's start.</param>
    /// <param name="to1">Where it should be in the video.</param>
    /// <param name="from2">A second time in the subtitles, far from the first, such as the last line's start.</param>
    /// <param name="to2">Where it should be in the video.</param>
    /// <exception cref="ArgumentException"><paramref name="from1"/> and <paramref name="from2"/> are the same time.</exception>
    public void Sync(MediaTime from1, MediaTime to1, MediaTime from2, MediaTime to2)
    {
        if (from1 == from2)
        {
            throw new ArgumentException("The two sync points must be at different times.", nameof(from2));
        }

        foreach (SubtitleCue cue in Cues)
        {
            cue.Start = ExactTime.Map(cue.Start, from1, to1, from2, to2);
            cue.End = ExactTime.Map(cue.End, from1, to1, from2, to2);
            ShiftKaraoke(cue, t => ExactTime.Map(t, from1, to1, from2, to2));
        }
    }

    /// <summary>
    /// Keeps only what shows between <paramref name="start"/> and <paramref name="end"/>: cues wholly outside are removed and
    /// cues across an edge are cut at it. Times stay on the same clock; <see cref="Shift"/> by minus <paramref name="start"/>
    /// to make the range start at zero.
    /// </summary>
    /// <param name="start">The start of the range.</param>
    /// <param name="end">The end of the range.</param>
    /// <returns>The number of cues removed.</returns>
    /// <exception cref="ArgumentException"><paramref name="end"/> is before <paramref name="start"/>.</exception>
    public int Clip(MediaTime start, MediaTime end)
    {
        if (end < start)
        {
            throw new ArgumentException("The range ends before it starts.", nameof(end));
        }

        int removed = Cues.RemoveAll(c => c.End <= start || c.Start >= end);
        foreach (SubtitleCue cue in Cues)
        {
            if (cue.Start < start)
            {
                cue.Start = start;
            }

            if (cue.End > end)
            {
                cue.End = end;
            }
        }

        return removed;
    }

    /// <summary>Puts the cues in order of start time, then end time; cues that start together keep their order.</summary>
    public void Sort()
    {
        SubtitleCue[] sorted = [.. Cues.OrderBy(c => c.Start).ThenBy(c => c.End)];
        Cues.Clear();
        Cues.AddRange(sorted);
    }

    /// <summary>
    /// Sorts the cues, then ends each cue no later than the next cue on the same layer starts, so two lines never show at once
    /// by accident. Cues that start at the same time, and ASS comments, are left alone: they are meant to show together.
    /// </summary>
    /// <returns>The number of cues shortened.</returns>
    public int FixOverlaps()
    {
        Sort();
        int fixedCount = 0;
        for (int i = 0; i < Cues.Count; i++)
        {
            SubtitleCue cue = Cues[i];
            if (cue.IsComment)
            {
                continue;
            }

            SubtitleCue? next = Cues.Skip(i + 1).FirstOrDefault(c => !c.IsComment && c.Layer == cue.Layer && c.Start > cue.Start);
            if (next is not null && cue.End > next.Start)
            {
                cue.End = next.Start;
                fixedCount++;
            }
        }

        return fixedCount;
    }

    private static void ShiftKaraoke(SubtitleCue cue, Func<MediaTime, MediaTime> map)
    {
        for (int i = 0; i < cue.Spans.Count; i++)
        {
            if (cue.Spans[i].Timestamp is { } time)
            {
                cue.Spans[i] = cue.Spans[i] with { Timestamp = map(time) };
            }
        }
    }
}

/// <summary>
/// Exact time arithmetic: a result is kept as a fraction of a second with the smallest denominator that holds it, falling back
/// to the nearest nanosecond only when that does not fit in 64 bits.
/// </summary>
internal static class ExactTime
{
    public static MediaTime Add(MediaTime a, MediaTime b)
    {
        (Int128 an, Int128 ad) = Fraction(a);
        (Int128 bn, Int128 bd) = Fraction(b);
        return From((an * bd) + (bn * ad), ad * bd);
    }

    public static MediaTime Multiply(MediaTime t, Rational factor)
    {
        (Int128 n, Int128 d) = Fraction(t);
        return From(n * factor.Numerator, d * factor.Denominator);
    }

    /// <summary>Maps <paramref name="t"/> by the line through (from1, to1) and (from2, to2).</summary>
    public static MediaTime Map(MediaTime t, MediaTime from1, MediaTime to1, MediaTime from2, MediaTime to2)
    {
        // to1 + (t - from1) * (to2 - to1) / (from2 - from1), in nanoseconds: every input rescaled once, rounded once at the end.
        Int128 tn = Nanoseconds(t), f1 = Nanoseconds(from1), t1 = Nanoseconds(to1), f2 = Nanoseconds(from2), t2 = Nanoseconds(to2);
        Int128 scaled = MediaTime.DivideRounded((tn - f1) * (t2 - t1), f2 - f1);
        return new MediaTime((long)(t1 + scaled), Rational.Nanosecond);
    }

    private static Int128 Nanoseconds(MediaTime t) => MediaTime.DivideRounded((Int128)t.Ticks * t.TimeBase.Numerator * 1_000_000_000, t.TimeBase.Denominator);

    private static (Int128 Numerator, Int128 Denominator) Fraction(MediaTime t) => ((Int128)t.Ticks * t.TimeBase.Numerator, t.TimeBase.Denominator);

    private static MediaTime From(Int128 numerator, Int128 denominator)
    {
        Int128 gcd = Gcd(numerator, denominator);
        numerator /= gcd;
        denominator /= gcd;
        if (denominator <= long.MaxValue && numerator >= long.MinValue && numerator <= long.MaxValue)
        {
            return new MediaTime((long)numerator, new Rational(1, (long)denominator));
        }

        return new MediaTime((long)MediaTime.DivideRounded(numerator * 1_000_000_000, denominator), Rational.Nanosecond);
    }

    private static Int128 Gcd(Int128 a, Int128 b)
    {
        a = Int128.Abs(a);
        b = Int128.Abs(b);
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a == 0 ? 1 : a;
    }
}
