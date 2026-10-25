namespace Egami.Video.Editing;

/// <summary>
/// Sums and differences of times on the finer of the two time bases: <see cref="MediaTime"/>'s own operators keep the left
/// one, so <see cref="MediaTime.Zero"/> (whole seconds) plus 0.25 s would round to 0.
/// </summary>
internal static class Times
{
    public static MediaTime Add(MediaTime a, MediaTime b) => Finer(a, b) ? a + b : b + a;

    public static MediaTime Sub(MediaTime a, MediaTime b)
    {
        if (Finer(a, b))
        {
            return a - b;
        }

        MediaTime negative = b - a;
        return new MediaTime(-negative.Ticks, negative.TimeBase);
    }

    private static bool Finer(MediaTime a, MediaTime b) => a.TimeBase.ToDouble() <= b.TimeBase.ToDouble();
}
