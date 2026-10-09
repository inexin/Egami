using System.Numerics;
using System.Runtime.InteropServices;

namespace Egami.Pixels;

/// <summary>A pixel with 8-bit red, green and blue channels, in that order, and no alpha.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Formats.cs" region="natural-types"/>
/// </example>
[StructLayout(LayoutKind.Sequential)]
public struct Rgb8 : IPixelType<Rgb8>
{
    /// <summary>The red channel.</summary>
    public byte R;

    /// <summary>The green channel.</summary>
    public byte G;

    /// <summary>The blue channel.</summary>
    public byte B;

    /// <summary>Initializes a new instance of the <see cref="Rgb8"/> struct.</summary>
    /// <param name="r">The red channel.</param>
    /// <param name="g">The green channel.</param>
    /// <param name="b">The blue channel.</param>
    public Rgb8(byte r, byte g, byte b)
    {
        R = r;
        G = g;
        B = b;
    }

    /// <inheritdoc/>
    public static PixelLayoutInfo Layout => new(PixelChannelOrder.Rgb, PixelChannelType.Unorm8, AlphaKind.None);

    /// <inheritdoc/>
    public static Rgb8 FromScaled(Vector4 rgba)
    {
        return new Rgb8(Scale.ToByte(rgba.X), Scale.ToByte(rgba.Y), Scale.ToByte(rgba.Z));
    }

    /// <inheritdoc/>
    public static Rgb8 FromRgba8(Rgba8 value)
    {
        return new Rgb8(value.R, value.G, value.B);
    }

    /// <inheritdoc/>
    public readonly Vector4 ToScaled()
    {
        return new Vector4(R / 255f, G / 255f, B / 255f, 1f);
    }

    /// <inheritdoc/>
    public readonly Rgba8 ToRgba8()
    {
        return new Rgba8(R, G, B, 255);
    }

    /// <inheritdoc/>
    public readonly bool Equals(Rgb8 other)
    {
        return R == other.R && G == other.G && B == other.B;
    }

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj)
    {
        return obj is Rgb8 other && Equals(other);
    }

    /// <inheritdoc/>
    public override readonly int GetHashCode()
    {
        return HashCode.Combine(R, G, B);
    }

    /// <inheritdoc/>
    public override readonly string ToString()
    {
        return FormattableString.Invariant($"Rgb8({R}, {G}, {B})");
    }

    /// <summary>Returns a value indicating whether two pixels are equal.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> if every channel is equal.</returns>
    public static bool operator ==(Rgb8 left, Rgb8 right)
    {
        return left.Equals(right);
    }

    /// <summary>Returns a value indicating whether two pixels differ.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> if any channel differs.</returns>
    public static bool operator !=(Rgb8 left, Rgb8 right)
    {
        return !left.Equals(right);
    }
}
