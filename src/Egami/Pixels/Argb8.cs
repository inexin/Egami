using System.Numerics;
using System.Runtime.InteropServices;

namespace Egami.Pixels;

/// <summary>A pixel with 8-bit alpha, red, green and blue channels, in that order, with straight alpha. Matches the byte order of big-endian ARGB buffers, such as Java's and some TIFF and PSD files.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ApiExamples.cs" region="pixel-types"/>
/// </example>
[StructLayout(LayoutKind.Sequential)]
public struct Argb8 : IPixelType<Argb8>
{
    /// <summary>The alpha channel.</summary>
    public byte A;

    /// <summary>The red channel.</summary>
    public byte R;

    /// <summary>The green channel.</summary>
    public byte G;

    /// <summary>The blue channel.</summary>
    public byte B;

    /// <summary>Initializes a new instance of the <see cref="Argb8"/> struct.</summary>
    /// <param name="a">The alpha channel.</param>
    /// <param name="r">The red channel.</param>
    /// <param name="g">The green channel.</param>
    /// <param name="b">The blue channel.</param>
    public Argb8(byte a, byte r, byte g, byte b)
    {
        A = a;
        R = r;
        G = g;
        B = b;
    }

    /// <inheritdoc/>
    public static PixelLayoutInfo Layout => new(PixelChannelOrder.Argb, PixelChannelType.Unorm8, AlphaKind.Straight);

    /// <inheritdoc/>
    public static Argb8 FromScaled(Vector4 rgba)
    {
        return new Argb8(Scale.ToByte(rgba.W), Scale.ToByte(rgba.X), Scale.ToByte(rgba.Y), Scale.ToByte(rgba.Z));
    }

    /// <inheritdoc/>
    public static Argb8 FromRgba8(Rgba8 value)
    {
        return new Argb8(value.A, value.R, value.G, value.B);
    }

    /// <inheritdoc/>
    public readonly Vector4 ToScaled()
    {
        return new Vector4(R, G, B, A) / 255f;
    }

    /// <inheritdoc/>
    public readonly Rgba8 ToRgba8()
    {
        return new Rgba8(R, G, B, A);
    }

    /// <inheritdoc/>
    public readonly bool Equals(Argb8 other)
    {
        return A == other.A && R == other.R && G == other.G && B == other.B;
    }

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj)
    {
        return obj is Argb8 other && Equals(other);
    }

    /// <inheritdoc/>
    public override readonly int GetHashCode()
    {
        return HashCode.Combine(A, R, G, B);
    }

    /// <inheritdoc/>
    public override readonly string ToString()
    {
        return FormattableString.Invariant($"Argb8({A}, {R}, {G}, {B})");
    }

    /// <summary>Returns a value indicating whether two pixels are equal.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> if every channel is equal.</returns>
    public static bool operator ==(Argb8 left, Argb8 right)
    {
        return left.Equals(right);
    }

    /// <summary>Returns a value indicating whether two pixels differ.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> if any channel differs.</returns>
    public static bool operator !=(Argb8 left, Argb8 right)
    {
        return !left.Equals(right);
    }
}
