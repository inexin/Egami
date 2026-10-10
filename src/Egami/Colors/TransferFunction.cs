using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

using Egami.Pixels;

namespace Egami.Colors;

/// <summary>
/// How a color space encodes light: the curve from stored values to linear light and back. Single values use exact double
/// arithmetic; rows of floats use a vectorized form within 1e-5 of it (PQ rows use the double form), the same on every CPU with
/// or without SIMD.
/// </summary>
/// <remarks>
/// <para>
/// Linear 1.0 is the encoding's reference white. For PQ and HLG that is 203 cd/m² (ITU-R BT.2408), so HDR highlights are values
/// above 1. PQ covers 0 to 10,000 cd/m², about 0 to 49.3. HLG decodes for a 1,000 cd/m² display with system gamma 1.2, which puts
/// its reference white (a 75% signal) at 1.0; single values and rows apply its OOTF to each value as for a gray color, and the
/// full OOTF, which depends on the color's luminance, is applied by color conversion.
/// </para>
/// <para>
/// The SDR curves are extended to negative values by mirroring (f(−x) = −f(x)), as CSS Color 4 does, and to values above 1 by
/// their own formula. PQ and HLG treat negative values as 0.
/// </para>
/// </remarks>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ColorManagement.cs" region="transfer"/>
/// </example>
public sealed class TransferFunction : IEquatable<TransferFunction>
{
    // BT.2020's precise BT.709 constants (12-bit form), which the 10-bit and 8-bit forms round.
    private const double Rec709Alpha = 1.09929682680944;
    private const double Rec709Beta = 0.018053968510807;

    // SMPTE ST 2084.
    private const double PqM1 = 2610.0 / 16384;
    private const double PqM2 = 2523.0 / 4096 * 128;
    private const double PqC1 = 3424.0 / 4096;
    private const double PqC2 = 2413.0 / 4096 * 32;
    private const double PqC3 = 2392.0 / 4096 * 32;

    // ITU-R BT.2100 HLG, and the BT.2408 reference white.
    private const double HlgA = 0.17883277;
    private const double HlgB = 0.28466892;
    private const double HlgC = 0.55991073;
    private const double HlgPeak = 1000;
    private const double HlgGamma = 1.2;
    private const double ReferenceWhite = 203;

    private readonly Kind _kind;
    private readonly double _g, _a, _b, _c, _d, _e, _f;
    private readonly float[]? _table;

    private TransferFunction(Kind kind, double g = 1, double a = 1, double b = 0, double c = 0, double d = 0, double e = 0, double f = 0, float[]? table = null)
    {
        (_kind, _g, _a, _b, _c, _d, _e, _f, _table) = (kind, g, a, b, c, d, e, f, table);
    }

    internal enum Kind
    {
        Srgb,
        Linear,
        Gamma,
        Rec709,
        Parametric,
        Pq,
        Hlg,
        Table,
    }

    /// <summary>Gets the sRGB curve of IEC 61966-2-1: a linear segment below 0.04045, then a 2.4 power.</summary>
    public static TransferFunction Srgb { get; } = new(Kind.Srgb);

    /// <summary>Gets the identity: values are linear light already.</summary>
    public static TransferFunction Linear { get; } = new(Kind.Linear);

    /// <summary>Gets the ITU-R BT.709 and BT.2020 curve (the inverse of their camera OETF).</summary>
    public static TransferFunction Rec709 { get; } = new(Kind.Rec709);

    /// <summary>Gets the SMPTE ST 2084 perceptual quantizer of ITU-R BT.2100, scaled so 203 cd/m² is 1.0.</summary>
    public static TransferFunction Pq { get; } = new(Kind.Pq);

    /// <summary>Gets the hybrid log-gamma curve of ITU-R BT.2100, for a 1,000 cd/m² display, scaled so reference white is 1.0.</summary>
    public static TransferFunction Hlg { get; } = new(Kind.Hlg);

    /// <summary>Gets a value indicating whether this is an HDR curve (PQ or HLG), whose linear values go above 1.</summary>
    public bool IsHdr => _kind is Kind.Pq or Kind.Hlg;

    /// <summary>Gets a value indicating whether the curve is the identity.</summary>
    public bool IsLinear => _kind == Kind.Linear || (_kind == Kind.Gamma && _g == 1);

    internal Kind Shape => _kind;

    /// <summary>Gets the seven ICC parametric values (g, a, b, c, d, e, f) of this curve, for curves that have them.</summary>
    internal (double G, double A, double B, double C, double D, double E, double F)? ParametricValues => _kind switch
    {
        Kind.Srgb => (2.4, 1 / 1.055, 0.055 / 1.055, 1 / 12.92, 0.04045, 0, 0),
        Kind.Linear => (1, 1, 0, 0, 0, 0, 0),
        Kind.Gamma => (_g, 1, 0, 0, 0, 0, 0),
        Kind.Rec709 => (1 / 0.45, 1 / Rec709Alpha, (Rec709Alpha - 1) / Rec709Alpha, 1 / 4.5, Rec709Beta * 4.5, 0, 0),
        Kind.Parametric => (_g, _a, _b, _c, _d, _e, _f),
        _ => null,
    };

    internal ReadOnlySpan<float> TableValues => _table;

    /// <summary>Returns a pure power curve: linear = encoded^<paramref name="gamma"/> (2.2 for a typical display).</summary>
    /// <param name="gamma">The decoding exponent, above 0.</param>
    /// <returns>The curve.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="gamma"/> is not a positive finite number.</exception>
    public static TransferFunction Gamma(double gamma)
    {
        if (!(gamma > 0) || !double.IsFinite(gamma))
        {
            throw new ArgumentOutOfRangeException(nameof(gamma), gamma, "The gamma must be a positive finite number.");
        }

        return gamma == 1 ? Linear : new TransferFunction(Kind.Gamma, g: gamma);
    }

    /// <summary>
    /// Returns the ICC parametric curve: linear = (a·x + b)^g + e for x ≥ d, and c·x + f below d. Every ICC <c>para</c> curve and
    /// the sRGB, BT.709 and gamma curves are special cases.
    /// </summary>
    /// <param name="g">The exponent, above 0.</param>
    /// <param name="a">The scale inside the power.</param>
    /// <param name="b">The offset inside the power.</param>
    /// <param name="c">The slope of the linear segment.</param>
    /// <param name="d">Where the power segment starts.</param>
    /// <param name="e">The offset added to the power segment.</param>
    /// <param name="f">The offset of the linear segment.</param>
    /// <returns>The curve.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A value is not finite, or <paramref name="g"/> is not above 0.</exception>
    public static TransferFunction Parametric(double g, double a, double b, double c, double d, double e, double f)
    {
        foreach (double v in (ReadOnlySpan<double>)[g, a, b, c, d, e, f])
        {
            if (!double.IsFinite(v))
            {
                throw new ArgumentOutOfRangeException(nameof(g), "Every parametric value must be finite.");
            }
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(g);
        return new TransferFunction(Kind.Parametric, g, a, b, c, d, e, f);
    }

    /// <summary>
    /// Returns a sampled curve: <paramref name="linear"/>[i] is the linear value of encoded value i / (count − 1), and values in
    /// between are interpolated, as an ICC <c>curv</c> table is.
    /// </summary>
    /// <param name="linear">At least two samples, not decreasing.</param>
    /// <returns>The curve.</returns>
    /// <exception cref="ArgumentException">Fewer than two samples, a sample that is not finite, or samples that go down.</exception>
    public static TransferFunction Table(ReadOnlySpan<float> linear)
    {
        if (linear.Length < 2)
        {
            throw new ArgumentException("A table needs at least two samples.", nameof(linear));
        }

        for (int i = 0; i < linear.Length; i++)
        {
            if (!float.IsFinite(linear[i]) || (i > 0 && linear[i] < linear[i - 1]))
            {
                throw new ArgumentException("Table samples must be finite and must not decrease.", nameof(linear));
            }
        }

        return new TransferFunction(Kind.Table, table: linear.ToArray());
    }

    /// <summary>Decodes one value to linear light.</summary>
    /// <param name="encoded">The stored value, nominally 0 to 1.</param>
    /// <returns>The linear value.</returns>
    public double ToLinear(double encoded)
    {
        if (double.IsNaN(encoded))
        {
            return double.NaN;
        }

        switch (_kind)
        {
            case Kind.Linear:
                return encoded;
            case Kind.Pq:
                return PqToLinear(encoded);
            case Kind.Hlg:
                return HlgToLinear(encoded);
            case Kind.Table:
                return TableToLinear(encoded);
        }

        double x = Math.Abs(encoded);
        double y = _kind switch
        {
            Kind.Srgb => Pixels.Srgb.ToLinear(x),
            Kind.Gamma => Math.Pow(x, _g),
            Kind.Rec709 => x < Rec709Beta * 4.5 ? x / 4.5 : Math.Pow((x + (Rec709Alpha - 1)) / Rec709Alpha, 1 / 0.45),
            _ => x >= _d ? Math.Pow(Math.Max((_a * x) + _b, 0), _g) + _e : (_c * x) + _f,
        };
        return encoded < 0 ? -y : y;
    }

    /// <summary>Encodes one linear value.</summary>
    /// <param name="linear">The linear value.</param>
    /// <returns>The stored value.</returns>
    public double FromLinear(double linear)
    {
        if (double.IsNaN(linear))
        {
            return double.NaN;
        }

        switch (_kind)
        {
            case Kind.Linear:
                return linear;
            case Kind.Pq:
                return PqFromLinear(linear);
            case Kind.Hlg:
                return HlgFromLinear(linear);
            case Kind.Table:
                return TableFromLinear(linear);
        }

        double y = Math.Abs(linear);
        double x = _kind switch
        {
            Kind.Srgb => Pixels.Srgb.FromLinear(y),
            Kind.Gamma => Math.Pow(y, 1 / _g),
            Kind.Rec709 => y < Rec709Beta ? y * 4.5 : (Rec709Alpha * Math.Pow(y, 0.45)) - (Rec709Alpha - 1),
            _ => ParametricFromLinear(y),
        };
        return linear < 0 ? -x : x;
    }

    /// <summary>Decodes a row of values to linear light; <paramref name="destination"/> may be <paramref name="source"/>.</summary>
    /// <param name="source">The stored values.</param>
    /// <param name="destination">Where to write the linear values. It must be at least as long as <paramref name="source"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than <paramref name="source"/>.</exception>
    public void ToLinear(ReadOnlySpan<float> source, Span<float> destination)
    {
        Rows(source, destination, decode: true);
    }

    /// <summary>Encodes a row of linear values; <paramref name="destination"/> may be <paramref name="source"/>.</summary>
    /// <param name="source">The linear values.</param>
    /// <param name="destination">Where to write the stored values. It must be at least as long as <paramref name="source"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than <paramref name="source"/>.</exception>
    public void FromLinear(ReadOnlySpan<float> source, Span<float> destination)
    {
        Rows(source, destination, decode: false);
    }

    /// <inheritdoc/>
    public bool Equals(TransferFunction? other)
    {
        return other is not null && (ReferenceEquals(this, other) || (_kind == other._kind && _g == other._g && _a == other._a && _b == other._b
            && _c == other._c && _d == other._d && _e == other._e && _f == other._f && TableValues.SequenceEqual(other.TableValues)));
    }

    /// <summary>
    /// Returns whether two curves give the same linear values, within <paramref name="tolerance"/>, at 256 points from 0 to 1:
    /// a parametric curve written for sRGB is close to <see cref="Srgb"/>, for example.
    /// </summary>
    /// <param name="other">The other curve.</param>
    /// <param name="tolerance">The largest difference allowed.</param>
    /// <returns><see langword="true"/> if the curves agree.</returns>
    public bool IsCloseTo(TransferFunction other, double tolerance = 1e-4)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (IsHdr != other.IsHdr)
        {
            return false;
        }

        for (int i = 0; i <= 255; i++)
        {
            double x = i / 255.0;
            if (Math.Abs(ToLinear(x) - other.ToLinear(x)) > tolerance)
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is TransferFunction other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(_kind, _g, _a, _b, _c, _d, _e, _f);

    /// <inheritdoc/>
    public override string ToString() => _kind switch
    {
        Kind.Gamma => string.Create(CultureInfo.InvariantCulture, $"Gamma {_g}"),
        Kind.Parametric => string.Create(CultureInfo.InvariantCulture, $"Parametric (g {_g}, a {_a}, b {_b}, c {_c}, d {_d}, e {_e}, f {_f})"),
        Kind.Table => string.Create(CultureInfo.InvariantCulture, $"Table of {_table!.Length}"),
        _ => _kind.ToString(),
    };

    private static double PqToLinear(double encoded)
    {
        double p = Math.Pow(Math.Max(encoded, 0), 1 / PqM2);
        return Math.Pow(Math.Max(p - PqC1, 0) / (PqC2 - (PqC3 * p)), 1 / PqM1) * (10000 / ReferenceWhite);
    }

    private static double PqFromLinear(double linear)
    {
        double y = Math.Clamp(linear * (ReferenceWhite / 10000), 0, 1);
        double p = Math.Pow(y, PqM1);
        return Math.Pow((PqC1 + (PqC2 * p)) / (1 + (PqC3 * p)), PqM2);
    }

    private static double HlgToLinear(double encoded)
    {
        double e = Math.Max(encoded, 0);
        double scene = e <= 0.5 ? e * e / 3 : (Math.Exp((e - HlgC) / HlgA) + HlgB) / 12;
        return HlgPeak * Math.Pow(scene, HlgGamma) / ReferenceWhite;
    }

    private static double HlgFromLinear(double linear)
    {
        double scene = Math.Pow(Math.Max(linear, 0) * ReferenceWhite / HlgPeak, 1 / HlgGamma);
        return scene <= 1.0 / 12 ? Math.Sqrt(3 * scene) : (HlgA * Math.Log((12 * scene) - HlgB)) + HlgC;
    }

    private double KneeValue => Math.Pow(Math.Max((_a * _d) + _b, 0), _g) + _f;

    private double ParametricFromLinear(double y)
    {
        // The knee is the power segment's start plus f, as Little CMS inverts: for a continuous curve that is where the segments
        // meet, and a curve that jumps at d sends values in the jump to the linear segment.
        double knee = KneeValue;
        if (y >= knee && _a != 0)
        {
            return (Math.Pow(Math.Max(y - _e, 0), 1 / _g) - _b) / _a;
        }

        return _c == 0 ? 0 : (y - _f) / _c;
    }

    private double TableToLinear(double encoded)
    {
        float[] t = _table!;
        double position = Math.Clamp(encoded, 0, 1) * (t.Length - 1);
        int i = Math.Min((int)position, t.Length - 2);
        return t[i] + ((t[i + 1] - t[i]) * (position - i));
    }

    private double TableFromLinear(double linear)
    {
        float[] t = _table!;
        if (linear <= t[0])
        {
            return 0;
        }

        if (linear >= t[^1])
        {
            return 1;
        }

        int lo = 0, hi = t.Length - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (t[mid] <= linear)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        double span = t[hi] - t[lo];
        return (lo + (span > 0 ? (linear - t[lo]) / span : 0)) / (t.Length - 1);
    }

    private void Rows(ReadOnlySpan<float> source, Span<float> destination, bool decode)
    {
        if (destination.Length < source.Length)
        {
            throw new ArgumentException("The destination is shorter than the source.", nameof(destination));
        }

        if (_kind == Kind.Linear)
        {
            source.CopyTo(destination);
            return;
        }

        // PQ's denominator cancels badly near its top (c2 − c3·p), so its rows run in double, value by value, like tables.
        if (_kind is Kind.Table or Kind.Pq)
        {
            for (int i = 0; i < source.Length; i++)
            {
                destination[i] = (float)(decode ? ToLinear(source[i]) : FromLinear(source[i]));
            }

            return;
        }

        // One vector form for every CPU: the tail runs through the same code in a padded vector, so rows never depend on
        // the hardware or on where a value falls in the row.
        int n = 0;
        ref float src = ref MemoryMarshal.GetReference(source);
        ref float dst = ref MemoryMarshal.GetReference(destination);
        for (; n + 8 <= source.Length; n += 8)
        {
            Apply(Vector256.LoadUnsafe(ref src, (nuint)n), decode).StoreUnsafe(ref dst, (nuint)n);
        }

        if (n < source.Length)
        {
            Span<float> tail = stackalloc float[8];
            source[n..].CopyTo(tail);
            Vector256<float> result = Apply(Vector256.Create<float>(tail), decode);
            for (int i = 0; n + i < source.Length; i++)
            {
                destination[n + i] = result[i];
            }
        }
    }

    private Vector256<float> Apply(Vector256<float> v, bool decode)
    {
        Vector256<float> sign = v & Vector256.Create(-0f);
        Vector256<float> x = Vector256.Abs(v);
        Vector256<float> y;
        switch (_kind)
        {
            case Kind.Srgb:
                y = decode
                    ? Select(Vector256.LessThanOrEqual(x, Vector256.Create(0.04045f)), x / 12.92f, TransferMath.Pow((x + Vector256.Create(0.055f)) / 1.055f, 2.4f))
                    : Select(Vector256.LessThanOrEqual(x, Vector256.Create(0.0031308f)), x * 12.92f, (TransferMath.Pow(x, 1 / 2.4f) * 1.055f) - Vector256.Create(0.055f));
                return y | sign;
            case Kind.Gamma:
                y = TransferMath.Pow(x, (float)(decode ? _g : 1 / _g));
                return y | sign;
            case Kind.Rec709:
                y = decode
                    ? Select(Vector256.LessThan(x, Vector256.Create((float)(Rec709Beta * 4.5))), x / 4.5f, TransferMath.Pow((x + Vector256.Create((float)(Rec709Alpha - 1))) / (float)Rec709Alpha, 1 / 0.45f))
                    : Select(Vector256.LessThan(x, Vector256.Create((float)Rec709Beta)), x * 4.5f, (TransferMath.Pow(x, 0.45f) * (float)Rec709Alpha) - Vector256.Create((float)(Rec709Alpha - 1)));
                return y | sign;
            case Kind.Parametric:
                return ParametricRow(x, decode) | sign;
            default:
                return decode ? HlgRow(v) : HlgEncodeRow(v);
        }
    }

    private Vector256<float> ParametricRow(Vector256<float> x, bool decode)
    {
        float g = (float)_g, a = (float)_a, b = (float)_b, c = (float)_c, d = (float)_d, e = (float)_e, f = (float)_f;
        if (decode)
        {
            Vector256<float> power = TransferMath.Pow(Vector256.Max((x * a) + Vector256.Create(b), Vector256<float>.Zero), g) + Vector256.Create(e);
            return Select(Vector256.GreaterThanOrEqual(x, Vector256.Create(d)), power, (x * c) + Vector256.Create(f));
        }

        float knee = (float)KneeValue;
        Vector256<float> curve = a == 0 ? Vector256<float>.Zero : (TransferMath.Pow(Vector256.Max(x - Vector256.Create(e), Vector256<float>.Zero), 1 / g) - Vector256.Create(b)) / a;
        Vector256<float> line = c == 0 ? Vector256<float>.Zero : (x - Vector256.Create(f)) / c;
        return Select(Vector256.GreaterThanOrEqual(x, Vector256.Create(knee)), curve, line);
    }

    private static Vector256<float> HlgRow(Vector256<float> v)
    {
        Vector256<float> e = Vector256.Max(v, Vector256<float>.Zero);
        Vector256<float> low = e * e / 3f;
        Vector256<float> high = (TransferMath.Exp((e - Vector256.Create((float)HlgC)) / (float)HlgA) + Vector256.Create((float)HlgB)) / 12f;
        Vector256<float> scene = Select(Vector256.LessThanOrEqual(e, Vector256.Create(0.5f)), low, high);
        return TransferMath.Pow(scene, (float)HlgGamma) * (float)(HlgPeak / ReferenceWhite);
    }

    private static Vector256<float> HlgEncodeRow(Vector256<float> v)
    {
        Vector256<float> scene = TransferMath.Pow(Vector256.Max(v, Vector256<float>.Zero) * (float)(ReferenceWhite / HlgPeak), (float)(1 / HlgGamma));
        Vector256<float> low = Vector256.Sqrt(scene * 3f);
        Vector256<float> high = (TransferMath.Log(Vector256.Max((scene * 12f) - Vector256.Create((float)HlgB), Vector256.Create(1e-30f))) * (float)HlgA) + Vector256.Create((float)HlgC);
        return Select(Vector256.LessThanOrEqual(scene, Vector256.Create(1f / 12)), low, high);
    }

    private static Vector256<float> Select(Vector256<float> mask, Vector256<float> whenTrue, Vector256<float> whenFalse)
    {
        return Vector256.ConditionalSelect(mask, whenTrue, whenFalse);
    }
}
