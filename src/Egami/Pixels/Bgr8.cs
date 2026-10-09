using System.Numerics;
using System.Runtime.InteropServices;

namespace Egami.Pixels;

/// <summary>A pixel with 8-bit blue, green and red channels, in that order, and no alpha.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ApiExamples.cs" region="pixel-types"/>
/// </example>
[StructLayout(LayoutKind.Sequential)]
public struct Bgr8 : IPixelType<Bgr8>
{
    /// <summary>The blue channel.</summary>
    public byte B;

    /// <summary>The green channel.</summary>
    public byte G;

    /// <summary>The red channel.</summary>
    public byte R;

    /// <summary>Initializes a new instance of the <see cref="Bgr8"/> struct.</summary>
    /// <param name="b">The blue channel.</param>
    /// <param name="g">The green channel.</param>
    /// <param name="r">The red channel.</param>
    public Bgr8(byte b, byte g, byte r)
    {
        B = b;
        G = g;
        R = r;
    }

    /// <inheritdoc/>
    public static PixelLayoutInfo Layout => new(PixelChannelOrder.Bgr, PixelChannelType.Unorm8, AlphaKind.None);

    /// <inheritdoc/>
    public static Bgr8 FromScaled(Vector4 rgba)
    {
        return new Bgr8(Scale.ToByte(rgba.Z), Scale.ToByte(rgba.Y), Scale.ToByte(rgba.X));
    }

    /// <inheritdoc/>
    public static Bgr8 FromRgba8(Rgba8 value)
    {
        return new Bgr8(value.B, value.G, value.R);
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
    public readonly bool Equals(Bgr8 other)
    {
        return B == other.B && G == other.G && R == other.R;
    }

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj)
    {
        return obj is Bgr8 other && Equals(other);
    }

    /// <inheritdoc/>
    public override readonly int GetHashCode()
    {
        return HashCode.Combine(B, G, R);
    }

    /// <inheritdoc/>
    public override readonly string ToString()
    {
        return FormattableString.Invariant($"Bgr8({B}, {G}, {R})");
    }

    /// <summary>Returns a value indicating whether two pixels are equal.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> if every channel is equal.</returns>
    public static bool operator ==(Bgr8 left, Bgr8 right)
    {
        return left.Equals(right);
    }

    /// <summary>Returns a value indicating whether two pixels differ.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> if any channel differs.</returns>
    public static bool operator !=(Bgr8 left, Bgr8 right)
    {
        return !left.Equals(right);
    }
}
