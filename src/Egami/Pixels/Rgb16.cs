using System.Numerics;
using System.Runtime.InteropServices;

namespace Egami.Pixels;

/// <summary>A pixel with 16-bit red, green and blue channels, in that order, and no alpha.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ApiExamples.cs" region="pixel-types"/>
/// </example>
[StructLayout(LayoutKind.Sequential)]
public struct Rgb16 : IPixelType<Rgb16>
{
    /// <summary>The red channel.</summary>
    public ushort R;

    /// <summary>The green channel.</summary>
    public ushort G;

    /// <summary>The blue channel.</summary>
    public ushort B;

    /// <summary>Initializes a new instance of the <see cref="Rgb16"/> struct.</summary>
    /// <param name="r">The red channel.</param>
    /// <param name="g">The green channel.</param>
    /// <param name="b">The blue channel.</param>
    public Rgb16(ushort r, ushort g, ushort b)
    {
        R = r;
        G = g;
        B = b;
    }

    /// <inheritdoc/>
    public static PixelLayoutInfo Layout => new(PixelChannelOrder.Rgb, PixelChannelType.Unorm16, AlphaKind.None);

    /// <inheritdoc/>
    public static Rgb16 FromScaled(Vector4 rgba)
    {
        return new Rgb16(Scale.ToUInt16(rgba.X), Scale.ToUInt16(rgba.Y), Scale.ToUInt16(rgba.Z));
    }

    /// <inheritdoc/>
    public static Rgb16 FromRgba8(Rgba8 value)
    {
        return new Rgb16(Scale.Widen(value.R), Scale.Widen(value.G), Scale.Widen(value.B));
    }

    /// <inheritdoc/>
    public static Rgb16 FromRgba16(Rgba16 value)
    {
        return new Rgb16(value.R, value.G, value.B);
    }

    /// <inheritdoc/>
    public readonly Vector4 ToScaled()
    {
        return new Vector4(R / 65535f, G / 65535f, B / 65535f, 1f);
    }

    /// <inheritdoc/>
    public readonly Rgba8 ToRgba8()
    {
        return new Rgba8(Scale.Narrow(R), Scale.Narrow(G), Scale.Narrow(B), 255);
    }

    /// <inheritdoc/>
    public readonly Rgba16 ToRgba16()
    {
        return new Rgba16(R, G, B, ushort.MaxValue);
    }

    /// <inheritdoc/>
    public readonly bool Equals(Rgb16 other)
    {
        return R == other.R && G == other.G && B == other.B;
    }

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj)
    {
        return obj is Rgb16 other && Equals(other);
    }

    /// <inheritdoc/>
    public override readonly int GetHashCode()
    {
        return HashCode.Combine(R, G, B);
    }

    /// <inheritdoc/>
    public override readonly string ToString()
    {
        return FormattableString.Invariant($"Rgb16({R}, {G}, {B})");
    }

    /// <summary>Returns a value indicating whether two pixels are equal.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> if every channel is equal.</returns>
    public static bool operator ==(Rgb16 left, Rgb16 right)
    {
        return left.Equals(right);
    }

    /// <summary>Returns a value indicating whether two pixels differ.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> if any channel differs.</returns>
    public static bool operator !=(Rgb16 left, Rgb16 right)
    {
        return !left.Equals(right);
    }
}
