using System.Numerics;
using System.Runtime.InteropServices;

namespace Egami.Pixels;

/// <summary>A pixel with 16-bit red, green, blue and straight alpha channels, in that order.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ApiExamples.cs" region="pixel-types"/>
/// </example>
[StructLayout(LayoutKind.Sequential)]
public struct Rgba16 : IPixelType<Rgba16>
{
    /// <summary>The red channel.</summary>
    public ushort R;

    /// <summary>The green channel.</summary>
    public ushort G;

    /// <summary>The blue channel.</summary>
    public ushort B;

    /// <summary>The alpha channel.</summary>
    public ushort A;

    /// <summary>Initializes a new instance of the <see cref="Rgba16"/> struct.</summary>
    /// <param name="r">The red channel.</param>
    /// <param name="g">The green channel.</param>
    /// <param name="b">The blue channel.</param>
    /// <param name="a">The alpha channel.</param>
    public Rgba16(ushort r, ushort g, ushort b, ushort a)
    {
        R = r;
        G = g;
        B = b;
        A = a;
    }

    /// <inheritdoc/>
    public static PixelLayoutInfo Layout => new(PixelChannelOrder.Rgba, PixelChannelType.Unorm16, AlphaKind.Straight);

    /// <inheritdoc/>
    public static Rgba16 FromScaled(Vector4 rgba)
    {
        return new Rgba16(Scale.ToUInt16(rgba.X), Scale.ToUInt16(rgba.Y), Scale.ToUInt16(rgba.Z), Scale.ToUInt16(rgba.W));
    }

    /// <inheritdoc/>
    public static Rgba16 FromRgba8(Rgba8 value)
    {
        return new Rgba16(Scale.Widen(value.R), Scale.Widen(value.G), Scale.Widen(value.B), Scale.Widen(value.A));
    }

    /// <inheritdoc/>
    public readonly Vector4 ToScaled()
    {
        return new Vector4(R, G, B, A) / 65535f;
    }

    /// <inheritdoc/>
    public readonly Rgba8 ToRgba8()
    {
        return new Rgba8(Scale.Narrow(R), Scale.Narrow(G), Scale.Narrow(B), Scale.Narrow(A));
    }

    /// <inheritdoc/>
    public readonly bool Equals(Rgba16 other)
    {
        return R == other.R && G == other.G && B == other.B && A == other.A;
    }

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj)
    {
        return obj is Rgba16 other && Equals(other);
    }

    /// <inheritdoc/>
    public override readonly int GetHashCode()
    {
        return HashCode.Combine(R, G, B, A);
    }

    /// <inheritdoc/>
    public override readonly string ToString()
    {
        return FormattableString.Invariant($"Rgba16({R}, {G}, {B}, {A})");
    }

    /// <summary>Returns a value indicating whether two pixels are equal.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> if every channel is equal.</returns>
    public static bool operator ==(Rgba16 left, Rgba16 right)
    {
        return left.Equals(right);
    }

    /// <summary>Returns a value indicating whether two pixels differ.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> if any channel differs.</returns>
    public static bool operator !=(Rgba16 left, Rgba16 right)
    {
        return !left.Equals(right);
    }
}
