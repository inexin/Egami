namespace Egami.Operations;

/// <summary>
/// A curve from 0..1 to 0..1 for one channel, as a photo editor's curves dialog draws it: inputs outside 0..1 are clamped first and
/// results are clamped to 0..1. Use one in <see cref="ColorFilter.Curves(ToneCurve)"/>; the tone steps (Level, Gamma and the
/// rest) are curves too, evaluated exactly.
/// </summary>
/// <remarks>Immutable and safe to share between threads.</remarks>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Tone.cs" region="curves"/>
/// </example>
public sealed class ToneCurve
{
    private readonly Func<double, double> _map;

    private ToneCurve(Func<double, double> map, bool identity = false, bool smooth = true)
    {
        _map = map;
        IsIdentity = identity;
        IsSmooth = smooth;
    }

    /// <summary>Gets the curve that changes nothing.</summary>
    public static ToneCurve Identity { get; } = new(x => x, identity: true);

    /// <summary>Gets whether this is <see cref="Identity"/>.</summary>
    internal bool IsIdentity { get; }

    /// <summary>
    /// Gets whether the curve has no jumps, so a table read with linear interpolation stays within its error; stepped curves
    /// (posterize, solarize, thresholds, equalization) are evaluated exactly instead.
    /// </summary>
    internal bool IsSmooth { get; }

    /// <summary>
    /// Returns the smooth curve through <paramref name="points"/>: a monotone cubic (Fritsch and Carlson, 1980), so a curve through
    /// rising points rises everywhere and never overshoots, as photo editors' curves do. Before the first point and after the last
    /// the curve stays flat at their values.
    /// </summary>
    /// <param name="points">At least two points, each coordinate from 0 to 1, with strictly increasing <c>X</c>.</param>
    /// <returns>The curve.</returns>
    /// <exception cref="ArgumentException">
    /// There are fewer than two points, a coordinate is outside 0..1, or the <c>X</c> values do not strictly increase.
    /// </exception>
    public static ToneCurve FromPoints(ReadOnlySpan<(float X, float Y)> points)
    {
        if (points.Length < 2)
        {
            throw new ArgumentException("A curve needs at least two points.", nameof(points));
        }

        double[] x = new double[points.Length], y = new double[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            (float px, float py) = points[i];
            if (!(px >= 0 && px <= 1 && py >= 0 && py <= 1))
            {
                throw new ArgumentException($"Point {i} ({px}, {py}) is outside 0..1.", nameof(points));
            }

            if (i > 0 && !(px > x[i - 1]))
            {
                throw new ArgumentException($"Point {i} does not lie to the right of point {i - 1}.", nameof(points));
            }

            x[i] = px;
            y[i] = py;
        }

        double[] m = MonotoneTangents(x, y);
        return new ToneCurve(v => Hermite(x, y, m, v));
    }

    /// <summary>
    /// Returns the curve that passes through <paramref name="table"/>'s values at evenly spaced inputs (the first at 0, the last at 1)
    /// and runs straight between them, as a .cube file's 1D table and Skia's TableARGB do.
    /// </summary>
    /// <param name="table">At least two values, each from 0 to 1. The table is copied.</param>
    /// <returns>The curve.</returns>
    /// <exception cref="ArgumentException">The table has fewer than two values or a value outside 0..1.</exception>
    public static ToneCurve FromTable(ReadOnlySpan<float> table)
    {
        if (table.Length < 2)
        {
            throw new ArgumentException("A table needs at least two values.", nameof(table));
        }

        float[] copy = table.ToArray();
        for (int i = 0; i < copy.Length; i++)
        {
            if (!(copy[i] >= 0 && copy[i] <= 1))
            {
                throw new ArgumentException($"Value {i} ({copy[i]}) is outside 0..1.", nameof(table));
            }
        }

        return new ToneCurve(v => Interpolate(copy, v));
    }

    /// <summary>
    /// Returns a curve sampled from <paramref name="function"/> at <paramref name="samples"/> evenly spaced inputs and run straight
    /// between them. The function is called only here, so the curve is safe to share whatever the function captures.
    /// </summary>
    /// <param name="function">The function; results are clamped to 0..1, and NaN reads as 0.</param>
    /// <param name="samples">How many samples, from 2 to 65,536. Defaults to 4,096.</param>
    /// <returns>The curve.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="function"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="samples"/> is outside 2 to 65,536.</exception>
    public static ToneCurve FromFunction(Func<float, float> function, int samples = 4096)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentOutOfRangeException.ThrowIfLessThan(samples, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(samples, 65536);
        float[] table = new float[samples];
        for (int i = 0; i < samples; i++)
        {
            float v = function(i / (float)(samples - 1));
            table[i] = float.IsNaN(v) ? 0 : Math.Clamp(v, 0, 1);
        }

        return new ToneCurve(v => Interpolate(table, v));
    }

    /// <summary>Returns the curve's value at <paramref name="x"/>.</summary>
    /// <param name="x">The input; values outside 0..1 are clamped, and NaN reads as 0.</param>
    /// <returns>The output, from 0 to 1.</returns>
    public float Evaluate(float x)
    {
        return (float)Map(x);
    }

    /// <summary>Returns the curve that applies this one and then <paramref name="next"/>.</summary>
    /// <param name="next">The curve applied second.</param>
    /// <returns>The combined curve.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="next"/> is <see langword="null"/>.</exception>
    public ToneCurve Then(ToneCurve next)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (IsIdentity)
        {
            return next;
        }

        if (next.IsIdentity)
        {
            return this;
        }

        ToneCurve first = this;
        return new ToneCurve(v => next.Map(first.Map(v)), smooth: IsSmooth && next.IsSmooth);
    }

    /// <summary>Wraps an exact formula; its results are clamped to 0..1 when read. A formula with jumps is not smooth.</summary>
    internal static ToneCurve FromFormula(Func<double, double> formula, bool smooth = true) => new(formula, smooth: smooth);

    /// <summary>Evaluates in double precision: the input clamped to 0..1, the output clamped to 0..1.</summary>
    internal double Map(double x)
    {
        x = double.IsNaN(x) ? 0 : x < 0 ? 0 : x > 1 ? 1 : x;
        double v = _map(x);
        return double.IsNaN(v) ? 0 : v < 0 ? 0 : v > 1 ? 1 : v;
    }

    /// <summary>Linear interpolation in a table spanning 0..1.</summary>
    internal static double Interpolate(float[] table, double x)
    {
        double position = x * (table.Length - 1);
        int i = (int)position;
        if (i >= table.Length - 1)
        {
            return table[^1];
        }

        double f = position - i;
        return table[i] + ((table[i + 1] - table[i]) * f);
    }

    /// <summary>Fritsch and Carlson's tangents: secant averages, zero at local extrema, scaled down where they would overshoot.</summary>
    private static double[] MonotoneTangents(double[] x, double[] y)
    {
        int n = x.Length;
        double[] d = new double[n - 1];
        for (int k = 0; k < n - 1; k++)
        {
            d[k] = (y[k + 1] - y[k]) / (x[k + 1] - x[k]);
        }

        double[] m = new double[n];
        m[0] = d[0];
        m[n - 1] = d[n - 2];
        for (int k = 1; k < n - 1; k++)
        {
            m[k] = d[k - 1] * d[k] <= 0 ? 0 : (d[k - 1] + d[k]) / 2;
        }

        for (int k = 0; k < n - 1; k++)
        {
            if (d[k] == 0)
            {
                m[k] = 0;
                m[k + 1] = 0;
                continue;
            }

            double a = m[k] / d[k], b = m[k + 1] / d[k];
            if (a < 0)
            {
                m[k] = 0;
                a = 0;
            }

            if (b < 0)
            {
                m[k + 1] = 0;
                b = 0;
            }

            double s = (a * a) + (b * b);
            if (s > 9)
            {
                double tau = 3 / Math.Sqrt(s);
                m[k] = tau * a * d[k];
                m[k + 1] = tau * b * d[k];
            }
        }

        return m;
    }

    /// <summary>The cubic Hermite segment holding <paramref name="v"/>; flat outside the points.</summary>
    private static double Hermite(double[] x, double[] y, double[] m, double v)
    {
        if (v <= x[0])
        {
            return y[0];
        }

        if (v >= x[^1])
        {
            return y[^1];
        }

        int k = Array.BinarySearch(x, v);
        if (k >= 0)
        {
            return y[k];
        }

        k = ~k - 1;
        double h = x[k + 1] - x[k], t = (v - x[k]) / h, t2 = t * t, t3 = t2 * t;
        return (((2 * t3) - (3 * t2) + 1) * y[k]) + ((t3 - (2 * t2) + t) * h * m[k]) + (((-2 * t3) + (3 * t2)) * y[k + 1]) + ((t3 - t2) * h * m[k + 1]);
    }
}
