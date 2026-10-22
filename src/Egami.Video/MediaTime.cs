using System.Globalization;

namespace Egami.Video;

/// <summary>
/// A point in time or a duration in a media stream: a whole number of ticks of a <see cref="Rational"/> time base. Exact for
/// every container: AVI counts frames at the frame rate's reciprocal (for example 1001/30000 s), Matroska counts its
/// timestamp scale in nanoseconds. <see cref="TimeSpan"/> rounds to 100 ns and drifts over long files; this type does not.
/// </summary>
/// <example>
/// <code language="csharp">
/// MediaTime frame = new(2_589_410, new Rational(1001, 30000)); // frame 2,589,410 of a 29.97 fps clip
/// TimeSpan at = frame.ToTimeSpan(); // 23:59:59.966...
/// MediaTime ns = frame.Rescale(Rational.Nanosecond); // the same instant in nanoseconds, rounded to the nearest
/// </code>
/// </example>
public readonly struct MediaTime : IEquatable<MediaTime>, IComparable<MediaTime>
{
    /// <summary>Creates a time of <paramref name="ticks"/> units of <paramref name="timeBase"/> seconds.</summary>
    /// <param name="ticks">The number of time base units. May be negative.</param>
    /// <param name="timeBase">The length of one tick in seconds. Must be positive.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeBase"/> is zero or negative.</exception>
    public MediaTime(long ticks, Rational timeBase)
    {
        if (timeBase.Numerator <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timeBase), timeBase, "The time base must be positive.");
        }

        Ticks = ticks;
        _timeBase = timeBase;
    }

    private readonly Rational _timeBase;

    /// <summary>Gets the number of time base units.</summary>
    public long Ticks { get; }

    /// <summary>Gets the length of one tick in seconds. A default-constructed value reports one second.</summary>
    public Rational TimeBase => _timeBase.IsZero ? new Rational(1, 1) : _timeBase;

    /// <summary>Gets zero in a one-second time base.</summary>
    public static MediaTime Zero => new(0, new Rational(1, 1));

    /// <summary>Gets the time in seconds, as a double.</summary>
    public double Seconds => Ticks * TimeBase.ToDouble();

    /// <summary>Returns the time as a <see cref="TimeSpan"/>, rounded to the nearest 100 ns tick.</summary>
    /// <returns>The time span.</returns>
    /// <exception cref="OverflowException">The time is outside the range of <see cref="TimeSpan"/>.</exception>
    public TimeSpan ToTimeSpan() => TimeSpan.FromTicks(Rescale(Rational.TimeSpanTick).Ticks);

    /// <summary>Creates a time from a <see cref="TimeSpan"/>, in 100 ns ticks.</summary>
    /// <param name="time">The time span.</param>
    /// <returns>The time.</returns>
    public static MediaTime FromTimeSpan(TimeSpan time) => new(time.Ticks, Rational.TimeSpanTick);

    /// <summary>Creates a time from seconds, rounded to the nearest nanosecond.</summary>
    /// <param name="seconds">The time in seconds.</param>
    /// <returns>The time.</returns>
    public static MediaTime FromSeconds(double seconds) => new(checked((long)Math.Round(seconds * 1e9)), Rational.Nanosecond);

    /// <summary>
    /// Converts to another time base, rounding to the nearest tick (halves away from zero). Exact when the new base divides the
    /// old one, such as frames to nanoseconds at 25 fps.
    /// </summary>
    /// <param name="timeBase">The new time base. Must be positive.</param>
    /// <returns>The same time in the new base.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeBase"/> is zero or negative.</exception>
    /// <exception cref="OverflowException">The result does not fit in 64 bits.</exception>
    public MediaTime Rescale(Rational timeBase)
    {
        if (timeBase.Numerator <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timeBase), timeBase, "The time base must be positive.");
        }

        Rational from = TimeBase;
        if (from == timeBase)
        {
            return this;
        }

        // ticks * from / to = ticks * from.N * to.D / (from.D * to.N)
        Int128 numerator = checked((Int128)Ticks * from.Numerator * timeBase.Denominator);
        Int128 denominator = checked((Int128)from.Denominator * timeBase.Numerator);
        return new MediaTime(checked((long)DivideRounded(numerator, denominator)), timeBase);
    }

    /// <summary>Adds two times. The result is in this time's base; <paramref name="other"/> is rescaled to it.</summary>
    /// <param name="other">The time to add.</param>
    /// <returns>The sum.</returns>
    public MediaTime Add(MediaTime other) => new(checked(Ticks + other.Rescale(TimeBase).Ticks), TimeBase);

    /// <summary>Subtracts a time. The result is in this time's base; <paramref name="other"/> is rescaled to it.</summary>
    /// <param name="other">The time to subtract.</param>
    /// <returns>The difference.</returns>
    public MediaTime Subtract(MediaTime other) => new(checked(Ticks - other.Rescale(TimeBase).Ticks), TimeBase);

    /// <summary>Compares the instants exactly, whatever the time bases.</summary>
    /// <param name="other">The other time.</param>
    /// <returns>Negative, zero or positive.</returns>
    public int CompareTo(MediaTime other)
    {
        Rational a = TimeBase, b = other.TimeBase;
        if (a == b)
        {
            return Ticks.CompareTo(other.Ticks);
        }

        // Ticks * a.N / a.D against other.Ticks * b.N / b.D: the products fit in 128 bits, and CompareFractions keeps
        // its own products in range by comparing whole parts first.
        Int128 left = (Int128)Ticks * a.Numerator;
        Int128 right = (Int128)other.Ticks * b.Numerator;
        return CompareFractions(left, a.Denominator, right, b.Denominator);
    }

    /// <summary>Gets whether two times are the same instant, whatever their time bases.</summary>
    /// <param name="other">The other time.</param>
    /// <returns>Whether they are equal.</returns>
    public bool Equals(MediaTime other) => CompareTo(other) == 0;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is MediaTime other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        // Equal instants in different bases must hash alike, so hash the reduced fraction of seconds.
        Rational b = TimeBase;
        Int128 numerator = (Int128)Ticks * b.Numerator;
        Int128 denominator = b.Denominator;
        Int128 gcd = Gcd(numerator, denominator);
        return HashCode.Combine(numerator / gcd, denominator / gcd);
    }

    /// <summary>Returns the ticks, the time base and the time, for example "1001@1/30000 (00:00:00.0333667)".</summary>
    /// <returns>The text.</returns>
    public override string ToString() => $"{Ticks.ToString(CultureInfo.InvariantCulture)}@{TimeBase} ({ToTimeSpanSafe()})";

    /// <summary>Compares two times.</summary>
    /// <param name="left">The first time.</param>
    /// <param name="right">The second time.</param>
    /// <returns>Whether they are the same instant.</returns>
    public static bool operator ==(MediaTime left, MediaTime right) => left.Equals(right);

    /// <summary>Compares two times.</summary>
    /// <param name="left">The first time.</param>
    /// <param name="right">The second time.</param>
    /// <returns>Whether they differ.</returns>
    public static bool operator !=(MediaTime left, MediaTime right) => !left.Equals(right);

    /// <summary>Compares two times.</summary>
    /// <param name="left">The first time.</param>
    /// <param name="right">The second time.</param>
    /// <returns>Whether <paramref name="left"/> is earlier.</returns>
    public static bool operator <(MediaTime left, MediaTime right) => left.CompareTo(right) < 0;

    /// <summary>Compares two times.</summary>
    /// <param name="left">The first time.</param>
    /// <param name="right">The second time.</param>
    /// <returns>Whether <paramref name="left"/> is later.</returns>
    public static bool operator >(MediaTime left, MediaTime right) => left.CompareTo(right) > 0;

    /// <summary>Compares two times.</summary>
    /// <param name="left">The first time.</param>
    /// <param name="right">The second time.</param>
    /// <returns>Whether <paramref name="left"/> is earlier or the same.</returns>
    public static bool operator <=(MediaTime left, MediaTime right) => left.CompareTo(right) <= 0;

    /// <summary>Compares two times.</summary>
    /// <param name="left">The first time.</param>
    /// <param name="right">The second time.</param>
    /// <returns>Whether <paramref name="left"/> is later or the same.</returns>
    public static bool operator >=(MediaTime left, MediaTime right) => left.CompareTo(right) >= 0;

    /// <summary>Adds two times. See <see cref="Add(MediaTime)"/>.</summary>
    /// <param name="left">The first time.</param>
    /// <param name="right">The second time.</param>
    /// <returns>The sum, in <paramref name="left"/>'s base.</returns>
    public static MediaTime operator +(MediaTime left, MediaTime right) => left.Add(right);

    /// <summary>Subtracts two times. See <see cref="Subtract(MediaTime)"/>.</summary>
    /// <param name="left">The first time.</param>
    /// <param name="right">The second time.</param>
    /// <returns>The difference, in <paramref name="left"/>'s base.</returns>
    public static MediaTime operator -(MediaTime left, MediaTime right) => left.Subtract(right);

    private string ToTimeSpanSafe()
    {
        try
        {
            return ToTimeSpan().ToString("c", CultureInfo.InvariantCulture);
        }
        catch (OverflowException)
        {
            return Seconds.ToString("R", CultureInfo.InvariantCulture) + " s";
        }
    }

    internal static Int128 DivideRounded(Int128 numerator, Int128 denominator)
    {
        if (denominator < 0)
        {
            numerator = -numerator;
            denominator = -denominator;
        }

        Int128 quotient = Int128.DivRem(numerator, denominator).Quotient;
        Int128 remainder = numerator - (quotient * denominator);
        Int128 twice = Int128.Abs(remainder) * 2;
        if (twice >= denominator)
        {
            quotient += numerator < 0 ? -1 : 1;
        }

        return quotient;
    }

    private static int CompareFractions(Int128 a, long aDen, Int128 b, long bDen)
    {
        // a / aDen against b / bDen with positive denominators: compare the whole parts, then the remainders.
        (Int128 aq, Int128 ar) = FloorDivRem(a, aDen);
        (Int128 bq, Int128 br) = FloorDivRem(b, bDen);
        int whole = aq.CompareTo(bq);
        return whole != 0 ? whole : ((Int128)(long)ar * bDen).CompareTo((Int128)(long)br * aDen);
    }

    private static (Int128 Quotient, Int128 Remainder) FloorDivRem(Int128 value, long divisor)
    {
        Int128 quotient = Int128.DivRem(value, divisor).Quotient;
        Int128 remainder = value - (quotient * divisor);
        if (remainder < 0)
        {
            quotient--;
            remainder += divisor;
        }

        return (quotient, remainder);
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
