using System.Numerics;
using System.Runtime.InteropServices;

namespace Egami.Pixels;

/// <summary>A pixel with 8-bit red, green, blue and straight alpha channels, in that order. Egami's default working type.</summary>
[StructLayout(LayoutKind.Sequential)]
public partial struct Rgba8 : IPixelType<Rgba8>
{
    /// <summary>The red channel.</summary>
    public byte R;

    /// <summary>The green channel.</summary>
    public byte G;

    /// <summary>The blue channel.</summary>
    public byte B;

    /// <summary>The alpha channel.</summary>
    public byte A;

    /// <summary>Initializes a new instance of the <see cref="Rgba8"/> struct.</summary>
    /// <param name="r">The red channel.</param>
    /// <param name="g">The green channel.</param>
    /// <param name="b">The blue channel.</param>
    /// <param name="a">The alpha channel.</param>
    public Rgba8(byte r, byte g, byte b, byte a)
    {
        R = r;
        G = g;
        B = b;
        A = a;
    }

    /// <inheritdoc/>
    public static PixelLayoutInfo Layout => new(PixelChannelOrder.Rgba, PixelChannelType.Unorm8, AlphaKind.Straight);

    /// <inheritdoc/>
    public static Rgba8 FromScaled(Vector4 rgba)
    {
        return new Rgba8(Scale.ToByte(rgba.X), Scale.ToByte(rgba.Y), Scale.ToByte(rgba.Z), Scale.ToByte(rgba.W));
    }

    /// <inheritdoc/>
    public static Rgba8 FromRgba8(Rgba8 value)
    {
        return value;
    }

    /// <inheritdoc/>
    public readonly Vector4 ToScaled()
    {
        return new Vector4(R, G, B, A) / 255f;
    }

    /// <inheritdoc/>
    public readonly Rgba8 ToRgba8()
    {
        return this;
    }

    /// <inheritdoc/>
    public readonly bool Equals(Rgba8 other)
    {
        return R == other.R && G == other.G && B == other.B && A == other.A;
    }

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj)
    {
        return obj is Rgba8 other && Equals(other);
    }

    /// <inheritdoc/>
    public override readonly int GetHashCode()
    {
        return HashCode.Combine(R, G, B, A);
    }

    /// <inheritdoc/>
    public override readonly string ToString()
    {
        return FormattableString.Invariant($"Rgba8({R}, {G}, {B}, {A})");
    }

    /// <summary>Returns a value indicating whether two pixels are equal.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> if every channel is equal.</returns>
    public static bool operator ==(Rgba8 left, Rgba8 right)
    {
        return left.Equals(right);
    }

    /// <summary>Returns a value indicating whether two pixels differ.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> if any channel differs.</returns>
    public static bool operator !=(Rgba8 left, Rgba8 right)
    {
        return !left.Equals(right);
    }
}
