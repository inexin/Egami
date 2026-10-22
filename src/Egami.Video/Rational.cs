using System.Globalization;

namespace Egami.Video;

/// <summary>
/// An exact fraction, such as a frame rate (30000/1001) or a time base (1/1000000000). Always stored in lowest terms with a
/// positive denominator.
/// </summary>
/// <example>
/// <code language="csharp">
/// Rational ntsc = new(30000, 1001);
/// double fps = ntsc.ToDouble(); // 29.97...
/// Rational frameDuration = ntsc.Reciprocal(); // 1001/30000
/// </code>
/// </example>
public readonly struct Rational : IEquatable<Rational>, IComparable<Rational>
{
    private readonly long _denominator;

    /// <summary>Creates a fraction and reduces it to lowest terms.</summary>
    /// <param name="numerator">The numerator.</param>
    /// <param name="denominator">The denominator. Must not be zero.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="denominator"/> is zero.</exception>
    public Rational(long numerator, long denominator)
    {
        ArgumentOutOfRangeException.ThrowIfZero(denominator);
        if (denominator < 0)
        {
            numerator = checked(-numerator);
            denominator = checked(-denominator);
        }

        long gcd = Gcd(numerator, denominator);
        Numerator = numerator / gcd;
        _denominator = denominator / gcd;
    }

    /// <summary>Gets the numerator.</summary>
    public long Numerator { get; }

    /// <summary>
    /// Gets the denominator, always positive. A default-constructed <see cref="Rational"/> reports 1, so it reads as zero.
    /// </summary>
    public long Denominator => _denominator == 0 ? 1 : _denominator;


    /// <summary>Gets zero (0/1).</summary>
    public static Rational Zero => new(0, 1);

    /// <summary>Gets one nanosecond (1/1000000000), Matroska's default time base.</summary>
    public static Rational Nanosecond => new(1, 1_000_000_000);

    /// <summary>Gets one .NET tick (1/10000000), the unit of <see cref="TimeSpan.Ticks"/>.</summary>
    public static Rational TimeSpanTick => new(1, TimeSpan.TicksPerSecond);

    /// <summary>Gets whether the value is zero.</summary>
    public bool IsZero => Numerator == 0;

    /// <summary>Returns the value as a double.</summary>
    /// <returns>The value.</returns>
    public double ToDouble() => (double)Numerator / Denominator;

    /// <summary>Returns the reciprocal, for example a frame duration from a frame rate.</summary>
    /// <returns>Denominator over numerator.</returns>
    /// <exception cref="DivideByZeroException">The value is zero.</exception>
    public Rational Reciprocal() => Numerator == 0 ? throw new DivideByZeroException("Zero has no reciprocal.") : new Rational(Denominator, Numerator);

    /// <summary>Parses "30000/1001" or a whole number such as "25".</summary>
    /// <param name="text">The text.</param>
    /// <returns>The fraction.</returns>
    /// <exception cref="FormatException">The text is not a fraction.</exception>
    public static Rational Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int slash = text.IndexOf('/', StringComparison.Ordinal);
        try
        {
            return slash < 0
                ? new Rational(long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture), 1)
                : new Rational(long.Parse(text.AsSpan(0, slash), NumberStyles.Integer, CultureInfo.InvariantCulture),
                    long.Parse(text.AsSpan(slash + 1), NumberStyles.Integer, CultureInfo.InvariantCulture));
        }
        catch (ArgumentOutOfRangeException e)
        {
            throw new FormatException($"'{text}' has a zero denominator.", e);
        }
    }

    /// <inheritdoc/>
    public bool Equals(Rational other) => Numerator == other.Numerator && Denominator == other.Denominator;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Rational other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Numerator, Denominator);

    /// <inheritdoc/>
    public int CompareTo(Rational other) => ((Int128)Numerator * other.Denominator).CompareTo((Int128)other.Numerator * Denominator);

    /// <summary>Returns "numerator/denominator".</summary>
    /// <returns>The text.</returns>
    public override string ToString() => Denominator == 1
        ? Numerator.ToString(CultureInfo.InvariantCulture)
        : $"{Numerator.ToString(CultureInfo.InvariantCulture)}/{Denominator.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>Compares two fractions for equality.</summary>
    /// <param name="left">The first fraction.</param>
    /// <param name="right">The second fraction.</param>
    /// <returns>Whether they are equal.</returns>
    public static bool operator ==(Rational left, Rational right) => left.Equals(right);

    /// <summary>Compares two fractions for inequality.</summary>
    /// <param name="left">The first fraction.</param>
    /// <param name="right">The second fraction.</param>
    /// <returns>Whether they differ.</returns>
    public static bool operator !=(Rational left, Rational right) => !left.Equals(right);

    /// <summary>Compares two fractions.</summary>
    /// <param name="left">The first fraction.</param>
    /// <param name="right">The second fraction.</param>
    /// <returns>Whether <paramref name="left"/> is smaller.</returns>
    public static bool operator <(Rational left, Rational right) => left.CompareTo(right) < 0;

    /// <summary>Compares two fractions.</summary>
    /// <param name="left">The first fraction.</param>
    /// <param name="right">The second fraction.</param>
    /// <returns>Whether <paramref name="left"/> is larger.</returns>
    public static bool operator >(Rational left, Rational right) => left.CompareTo(right) > 0;

    /// <summary>Compares two fractions.</summary>
    /// <param name="left">The first fraction.</param>
    /// <param name="right">The second fraction.</param>
    /// <returns>Whether <paramref name="left"/> is smaller or equal.</returns>
    public static bool operator <=(Rational left, Rational right) => left.CompareTo(right) <= 0;

    /// <summary>Compares two fractions.</summary>
    /// <param name="left">The first fraction.</param>
    /// <param name="right">The second fraction.</param>
    /// <returns>Whether <paramref name="left"/> is larger or equal.</returns>
    public static bool operator >=(Rational left, Rational right) => left.CompareTo(right) >= 0;

    private static long Gcd(long a, long b)
    {
        a = Math.Abs(a);
        b = Math.Abs(b);
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a == 0 ? 1 : a;
    }
}
