using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Egami.Operations;

/// <summary>The luma weights a grayscale conversion uses.</summary>
public enum GrayscaleMode
{
    /// <summary>Rec. 709 (sRGB) weights: 0.2126 red, 0.7152 green, 0.0722 blue. The default.</summary>
    Rec709,

    /// <summary>Rec. 601 (older video) weights: 0.299 red, 0.587 green, 0.114 blue.</summary>
    Rec601,
}

/// <summary>
/// A 4 × 5 color matrix applied to straight (not premultiplied) RGBA scaled to 0..1:
/// each output channel is <c>m[row, 0] × R + m[row, 1] × G + m[row, 2] × B + m[row, 3] × A + m[row, 4]</c>, rows in R, G, B, A order,
/// and results are clamped to 0..1. The factories follow the W3C Filter Effects definitions.
/// </summary>
/// <remarks>
/// <c>default(ColorMatrix)</c> is <see cref="Identity"/>, so a matrix left unset changes nothing.
/// </remarks>
public readonly struct ColorMatrix : IEquatable<ColorMatrix>
{
    private readonly Values _values;

    /// <summary>False only for <c>default</c>, which reads as the identity.</summary>
    private readonly bool _set;

    /// <summary>Creates a matrix from 20 values, row by row.</summary>
    /// <param name="values">Four rows of five: the R, G, B and A weights, then the offset.</param>
    /// <exception cref="ArgumentException"><paramref name="values"/> does not hold exactly 20 finite values.</exception>
    public ColorMatrix(ReadOnlySpan<float> values)
    {
        if (values.Length != 20)
        {
            throw new ArgumentException("A color matrix has 4 rows of 5 values.", nameof(values));
        }

        foreach (float v in values)
        {
            if (!float.IsFinite(v))
            {
                throw new ArgumentException("Every value must be finite.", nameof(values));
            }
        }

        values.CopyTo(_values);
        _set = true;
    }

    /// <summary>Gets the matrix that changes nothing.</summary>
    public static ColorMatrix Identity { get; } = new([1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0]);

    /// <summary>Gets one value.</summary>
    /// <param name="row">The output channel: 0 red, 1 green, 2 blue, 3 alpha.</param>
    /// <param name="column">The input channel in the same order, or 4 for the offset.</param>
    /// <returns>The value.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="row"/> or <paramref name="column"/> is out of range.</exception>
    [SuppressMessage("Design", "CA1043:Use Integral Or String Argument For Indexers", Justification = "Integral.")]
    public float this[int row, int column]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)row, 3u, nameof(row));
            ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)column, 4u, nameof(column));
            return Value((row * 5) + column);
        }
    }

    /// <summary>Gets whether no channel mixes into another, so each channel maps through its own curve.</summary>
    internal bool IsPerChannel
    {
        get
        {
            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++)
                {
                    if (r != c && Value((r * 5) + c) != 0)
                    {
                        return false;
                    }
                }
            }

            return true;
        }
    }

    /// <summary>Multiplies red, green and blue by <paramref name="amount"/>. 1 changes nothing, 0 gives black.</summary>
    /// <param name="amount">The factor, 0 or more.</param>
    /// <returns>The matrix.</returns>
    public static ColorMatrix Brightness(float amount)
    {
        CheckAtLeastZero(amount);
        return Diagonal(amount, amount, amount, 1, 0, 0);
    }

    /// <summary>Scales red, green and blue around 0.5 by <paramref name="amount"/>. 1 changes nothing, 0 gives flat gray.</summary>
    /// <param name="amount">The factor, 0 or more.</param>
    /// <returns>The matrix.</returns>
    public static ColorMatrix Contrast(float amount)
    {
        CheckAtLeastZero(amount);
        return Diagonal(amount, amount, amount, 1, 0.5f - (0.5f * amount), 0);
    }

    /// <summary>Replaces red, green and blue with <c>1 − value</c>, blended by <paramref name="amount"/>.</summary>
    /// <param name="amount">0 changes nothing, 1 inverts fully.</param>
    /// <returns>The matrix.</returns>
    public static ColorMatrix Invert(float amount = 1)
    {
        CheckUnit(amount);
        float slope = 1 - (2 * amount);
        return Diagonal(slope, slope, slope, 1, amount, 0);
    }

    /// <summary>Multiplies alpha by <paramref name="amount"/>.</summary>
    /// <param name="amount">0 makes the image transparent, 1 changes nothing.</param>
    /// <returns>The matrix.</returns>
    public static ColorMatrix Opacity(float amount)
    {
        CheckUnit(amount);
        return Diagonal(1, 1, 1, amount, 0, 0);
    }

    /// <summary>Mixes each color with its luma, using the W3C Filter Effects weights (0.213, 0.715, 0.072).</summary>
    /// <param name="amount">1 changes nothing, 0 gives gray, above 1 oversaturates.</param>
    /// <returns>The matrix.</returns>
    public static ColorMatrix Saturation(float amount)
    {
        CheckAtLeastZero(amount);
        return LumaMix(0.213f, 0.715f, 0.072f, amount);
    }

    /// <summary>Converts toward gray with the given luma weights, applied to the stored (gamma-encoded) values.</summary>
    /// <param name="mode">The luma weights.</param>
    /// <param name="amount">0 changes nothing, 1 gives full gray.</param>
    /// <returns>The matrix.</returns>
    public static ColorMatrix Grayscale(GrayscaleMode mode = GrayscaleMode.Rec709, float amount = 1)
    {
        CheckUnit(amount);
        (float r, float g, float b) = LumaWeights(mode);
        return LumaMix(r, g, b, 1 - amount);
    }

    /// <summary>Tints toward sepia with the W3C Filter Effects sepia matrix.</summary>
    /// <param name="amount">0 changes nothing, 1 gives full sepia.</param>
    /// <returns>The matrix.</returns>
    public static ColorMatrix Sepia(float amount = 1)
    {
        CheckUnit(amount);
        float s = 1 - amount;
        return new ColorMatrix(
        [
            0.393f + (0.607f * s), 0.769f - (0.769f * s), 0.189f - (0.189f * s), 0, 0,
            0.349f - (0.349f * s), 0.686f + (0.314f * s), 0.168f - (0.168f * s), 0, 0,
            0.272f - (0.272f * s), 0.534f - (0.534f * s), 0.131f + (0.869f * s), 0, 0,
            0, 0, 0, 1, 0,
        ]);
    }

    /// <summary>Rotates hue around the luma axis with the W3C Filter Effects matrix.</summary>
    /// <param name="degrees">The angle; 0 and 360 change nothing.</param>
    /// <returns>The matrix.</returns>
    public static ColorMatrix HueRotate(float degrees)
    {
        if (!float.IsFinite(degrees))
        {
            throw new ArgumentOutOfRangeException(nameof(degrees), degrees, "The angle must be finite.");
        }

        double radians = degrees * Math.PI / 180;
        float cos = (float)Math.Cos(radians);
        float sin = (float)Math.Sin(radians);
        return new ColorMatrix(
        [
            0.213f + (cos * 0.787f) - (sin * 0.213f), 0.715f - (cos * 0.715f) - (sin * 0.715f), 0.072f - (cos * 0.072f) + (sin * 0.928f), 0, 0,
            0.213f - (cos * 0.213f) + (sin * 0.143f), 0.715f + (cos * 0.285f) + (sin * 0.140f), 0.072f - (cos * 0.072f) - (sin * 0.283f), 0, 0,
            0.213f - (cos * 0.213f) - (sin * 0.787f), 0.715f - (cos * 0.715f) + (sin * 0.715f), 0.072f + (cos * 0.928f) + (sin * 0.072f), 0, 0,
            0, 0, 0, 1, 0,
        ]);
    }

    /// <summary>Returns the matrix that applies this one and then <paramref name="next"/>, without clamping in between.</summary>
    /// <param name="next">The matrix applied second.</param>
    /// <returns>The combined matrix.</returns>
    public ColorMatrix Then(ColorMatrix next)
    {
        Span<float> result = stackalloc float[20];
        for (int r = 0; r < 4; r++)
        {
            for (int c = 0; c < 5; c++)
            {
                // Composition of affine maps: next × this, with this's offset column carried through next's weights.
                double sum = c == 4 ? next[r, 4] : 0;
                for (int k = 0; k < 4; k++)
                {
                    sum += next[r, k] * (double)this[k, c];
                }

                result[(r * 5) + c] = (float)sum;
            }
        }

        return new ColorMatrix(result);
    }

    /// <inheritdoc/>
    public bool Equals(ColorMatrix other)
    {
        for (int i = 0; i < 20; i++)
        {
            if (Value(i) != other.Value(i))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj)
    {
        return obj is ColorMatrix other && Equals(other);
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = default;
        for (int i = 0; i < 20; i++)
        {
            hash.Add(Value(i));
        }

        return hash.ToHashCode();
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        StringBuilder text = new();
        for (int r = 0; r < 4; r++)
        {
            text.Append(r == 0 ? "[" : "; ");
            for (int c = 0; c < 5; c++)
            {
                text.Append(c == 0 ? string.Empty : " ").Append(this[r, c].ToString(CultureInfo.InvariantCulture));
            }
        }

        return text.Append(']').ToString();
    }

    /// <summary>Returns whether two matrices hold the same values.</summary>
    /// <param name="left">The first matrix.</param>
    /// <param name="right">The second matrix.</param>
    /// <returns><see langword="true"/> if every value is equal.</returns>
    public static bool operator ==(ColorMatrix left, ColorMatrix right)
    {
        return left.Equals(right);
    }

    /// <summary>Returns whether two matrices differ.</summary>
    /// <param name="left">The first matrix.</param>
    /// <param name="right">The second matrix.</param>
    /// <returns><see langword="true"/> if any value differs.</returns>
    public static bool operator !=(ColorMatrix left, ColorMatrix right)
    {
        return !left.Equals(right);
    }

    /// <summary>Returns the value at flat index <paramref name="i"/>, row by row; an unset matrix reads as the identity.</summary>
    private float Value(int i)
    {
        return _set ? _values[i] : i % 5 == i / 5 ? 1f : 0f;
    }

    /// <summary>Gets the luma weights of a mode.</summary>
    internal static (float R, float G, float B) LumaWeights(GrayscaleMode mode)
    {
        return mode switch
        {
            GrayscaleMode.Rec709 => (0.2126f, 0.7152f, 0.0722f),
            GrayscaleMode.Rec601 => (0.299f, 0.587f, 0.114f),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
        };
    }

    /// <summary>The W3C saturate form: <c>s</c> = 1 keeps the color, 0 gives the luma.</summary>
    private static ColorMatrix LumaMix(float r, float g, float b, float s)
    {
        return new ColorMatrix(
        [
            r + ((1 - r) * s), g - (g * s), b - (b * s), 0, 0,
            r - (r * s), g + ((1 - g) * s), b - (b * s), 0, 0,
            r - (r * s), g - (g * s), b + ((1 - b) * s), 0, 0,
            0, 0, 0, 1, 0,
        ]);
    }

    private static ColorMatrix Diagonal(float r, float g, float b, float a, float colorOffset, float alphaOffset)
    {
        return new ColorMatrix([r, 0, 0, 0, colorOffset, 0, g, 0, 0, colorOffset, 0, 0, b, 0, colorOffset, 0, 0, 0, a, alphaOffset]);
    }

    private static void CheckAtLeastZero(float amount, [CallerArgumentExpression(nameof(amount))] string? name = null)
    {
        if (!(amount >= 0) || float.IsPositiveInfinity(amount))
        {
            throw new ArgumentOutOfRangeException(name, amount, "The amount must be finite and 0 or more.");
        }
    }

    private static void CheckUnit(float amount, [CallerArgumentExpression(nameof(amount))] string? name = null)
    {
        if (!(amount >= 0 && amount <= 1))
        {
            throw new ArgumentOutOfRangeException(name, amount, "The amount must be from 0 to 1.");
        }
    }

    [InlineArray(20)]
    private struct Values
    {
        private float _element;
    }
}
