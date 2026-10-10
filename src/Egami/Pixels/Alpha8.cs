using System.Numerics;
using System.Runtime.InteropServices;

namespace Egami.Pixels;

/// <summary>
/// A pixel with one 8-bit alpha channel: a mask, or glyph coverage. As a color it is black with that alpha; converting a color
/// to it keeps only the alpha, so a type without alpha gives 255.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ApiExamples.cs" region="pixel-types"/>
/// </example>
[StructLayout(LayoutKind.Sequential)]
public struct Alpha8 : IPixelType<Alpha8>
{
    /// <summary>The alpha channel.</summary>
    public byte A;

    /// <summary>Initializes a new instance of the <see cref="Alpha8"/> struct.</summary>
    /// <param name="a">The alpha channel.</param>
    public Alpha8(byte a)
    {
        A = a;
    }

    /// <inheritdoc/>
    public static PixelLayoutInfo Layout => new(PixelChannelOrder.Alpha, PixelChannelType.Unorm8, AlphaKind.Straight);

    /// <inheritdoc/>
    public static Alpha8 FromScaled(Vector4 rgba)
    {
        return new Alpha8(Scale.ToByte(rgba.W));
    }

    /// <inheritdoc/>
    public static Alpha8 FromRgba8(Rgba8 value)
    {
        return new Alpha8(value.A);
    }

    /// <inheritdoc/>
    public readonly Vector4 ToScaled()
    {
        return new Vector4(0f, 0f, 0f, A / 255f);
    }

    /// <inheritdoc/>
    public readonly Rgba8 ToRgba8()
    {
        return new Rgba8(0, 0, 0, A);
    }

    /// <inheritdoc/>
    public readonly bool Equals(Alpha8 other)
    {
        return A == other.A;
    }

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj)
    {
        return obj is Alpha8 other && Equals(other);
    }

    /// <inheritdoc/>
    public override readonly int GetHashCode()
    {
        return A;
    }

    /// <inheritdoc/>
    public override readonly string ToString()
    {
        return FormattableString.Invariant($"Alpha8({A})");
    }

    /// <summary>Returns a value indicating whether two pixels are equal.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> if the alpha is equal.</returns>
    public static bool operator ==(Alpha8 left, Alpha8 right)
    {
        return left.Equals(right);
    }

    /// <summary>Returns a value indicating whether two pixels differ.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> if the alpha differs.</returns>
    public static bool operator !=(Alpha8 left, Alpha8 right)
    {
        return !left.Equals(right);
    }
}
