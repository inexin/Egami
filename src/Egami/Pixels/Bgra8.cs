using System.Numerics;
using System.Runtime.InteropServices;

namespace Egami.Pixels;

/// <summary>A pixel with 8-bit blue, green, red and straight alpha channels, in that order.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Bgra8 : IPixelType<Bgra8>
{
    /// <summary>The blue channel.</summary>
    public byte B;

    /// <summary>The green channel.</summary>
    public byte G;

    /// <summary>The red channel.</summary>
    public byte R;

    /// <summary>The alpha channel.</summary>
    public byte A;

    /// <summary>Initializes a new instance of the <see cref="Bgra8"/> struct.</summary>
    /// <param name="b">The blue channel.</param>
    /// <param name="g">The green channel.</param>
    /// <param name="r">The red channel.</param>
    /// <param name="a">The alpha channel.</param>
    public Bgra8(byte b, byte g, byte r, byte a)
    {
        B = b;
        G = g;
        R = r;
        A = a;
    }

    /// <inheritdoc/>
    public static PixelLayoutInfo Layout => new(PixelChannelOrder.Bgra, PixelChannelType.Unorm8, AlphaKind.Straight);

    /// <inheritdoc/>
    public static Bgra8 FromScaled(Vector4 rgba)
    {
        return new Bgra8(Scale.ToByte(rgba.Z), Scale.ToByte(rgba.Y), Scale.ToByte(rgba.X), Scale.ToByte(rgba.W));
    }

    /// <inheritdoc/>
    public static Bgra8 FromRgba8(Rgba8 value)
    {
        return new Bgra8(value.B, value.G, value.R, value.A);
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
    public readonly bool Equals(Bgra8 other)
    {
        return B == other.B && G == other.G && R == other.R && A == other.A;
    }

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj)
    {
        return obj is Bgra8 other && Equals(other);
    }

    /// <inheritdoc/>
    public override readonly int GetHashCode()
    {
        return HashCode.Combine(B, G, R, A);
    }

    /// <inheritdoc/>
    public override readonly string ToString()
    {
        return FormattableString.Invariant($"Bgra8({B}, {G}, {R}, {A})");
    }

    /// <summary>Returns a value indicating whether two pixels are equal.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> if every channel is equal.</returns>
    public static bool operator ==(Bgra8 left, Bgra8 right)
    {
        return left.Equals(right);
    }

    /// <summary>Returns a value indicating whether two pixels differ.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> if any channel differs.</returns>
    public static bool operator !=(Bgra8 left, Bgra8 right)
    {
        return !left.Equals(right);
    }
}
