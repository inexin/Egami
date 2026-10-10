using System.Numerics;
using System.Runtime.InteropServices;

namespace Egami.Pixels;

/// <summary>A pixel with 8-bit alpha, blue, green and red channels, in that order, with straight alpha. Matches the bytes of a little-endian 32-bit RGBA value read as an integer.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ApiExamples.cs" region="pixel-types"/>
/// </example>
[StructLayout(LayoutKind.Sequential)]
public struct Abgr8 : IPixelType<Abgr8>
{
    /// <summary>The alpha channel.</summary>
    public byte A;

    /// <summary>The blue channel.</summary>
    public byte B;

    /// <summary>The green channel.</summary>
    public byte G;

    /// <summary>The red channel.</summary>
    public byte R;

    /// <summary>Initializes a new instance of the <see cref="Abgr8"/> struct.</summary>
    /// <param name="a">The alpha channel.</param>
    /// <param name="b">The blue channel.</param>
    /// <param name="g">The green channel.</param>
    /// <param name="r">The red channel.</param>
    public Abgr8(byte a, byte b, byte g, byte r)
    {
        A = a;
        B = b;
        G = g;
        R = r;
    }

    /// <inheritdoc/>
    public static PixelLayoutInfo Layout => new(PixelChannelOrder.Abgr, PixelChannelType.Unorm8, AlphaKind.Straight);

    /// <inheritdoc/>
    public static Abgr8 FromScaled(Vector4 rgba)
    {
        return new Abgr8(Scale.ToByte(rgba.W), Scale.ToByte(rgba.Z), Scale.ToByte(rgba.Y), Scale.ToByte(rgba.X));
    }

    /// <inheritdoc/>
    public static Abgr8 FromRgba8(Rgba8 value)
    {
        return new Abgr8(value.A, value.B, value.G, value.R);
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
    public readonly bool Equals(Abgr8 other)
    {
        return A == other.A && B == other.B && G == other.G && R == other.R;
    }

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj)
    {
        return obj is Abgr8 other && Equals(other);
    }

    /// <inheritdoc/>
    public override readonly int GetHashCode()
    {
        return HashCode.Combine(A, B, G, R);
    }

    /// <inheritdoc/>
    public override readonly string ToString()
    {
        return FormattableString.Invariant($"Abgr8({A}, {B}, {G}, {R})");
    }

    /// <summary>Returns a value indicating whether two pixels are equal.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> if every channel is equal.</returns>
    public static bool operator ==(Abgr8 left, Abgr8 right)
    {
        return left.Equals(right);
    }

    /// <summary>Returns a value indicating whether two pixels differ.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> if any channel differs.</returns>
    public static bool operator !=(Abgr8 left, Abgr8 right)
    {
        return !left.Equals(right);
    }
}
