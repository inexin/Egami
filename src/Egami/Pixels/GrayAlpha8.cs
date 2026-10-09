using System.Numerics;
using System.Runtime.InteropServices;

namespace Egami.Pixels;

/// <summary>A pixel with 8-bit luminance and straight alpha channels, in that order.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ApiExamples.cs" region="pixel-types"/>
/// </example>
[StructLayout(LayoutKind.Sequential)]
public struct GrayAlpha8 : IPixelType<GrayAlpha8>
{
    /// <summary>The luminance channel.</summary>
    public byte L;

    /// <summary>The alpha channel.</summary>
    public byte A;

    /// <summary>Initializes a new instance of the <see cref="GrayAlpha8"/> struct.</summary>
    /// <param name="l">The luminance channel.</param>
    /// <param name="a">The alpha channel.</param>
    public GrayAlpha8(byte l, byte a)
    {
        L = l;
        A = a;
    }

    /// <inheritdoc/>
    public static PixelLayoutInfo Layout => new(PixelChannelOrder.GrayAlpha, PixelChannelType.Unorm8, AlphaKind.Straight);

    /// <inheritdoc/>
    public static GrayAlpha8 FromScaled(Vector4 rgba)
    {
        return new GrayAlpha8(Scale.ToByte(Luma.OfScaled(Scale.Clamp(rgba.X), Scale.Clamp(rgba.Y), Scale.Clamp(rgba.Z))), Scale.ToByte(rgba.W));
    }

    /// <inheritdoc/>
    public static GrayAlpha8 FromRgba8(Rgba8 value)
    {
        return new GrayAlpha8(Luma.Of8(value.R, value.G, value.B), value.A);
    }

    /// <inheritdoc/>
    public readonly Vector4 ToScaled()
    {
        float l = L / 255f;
        return new Vector4(l, l, l, A / 255f);
    }

    /// <inheritdoc/>
    public readonly Rgba8 ToRgba8()
    {
        return new Rgba8(L, L, L, A);
    }

    /// <inheritdoc/>
    public readonly bool Equals(GrayAlpha8 other)
    {
        return L == other.L && A == other.A;
    }

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj)
    {
        return obj is GrayAlpha8 other && Equals(other);
    }

    /// <inheritdoc/>
    public override readonly int GetHashCode()
    {
        return HashCode.Combine(L, A);
    }

    /// <inheritdoc/>
    public override readonly string ToString()
    {
        return FormattableString.Invariant($"GrayAlpha8({L}, {A})");
    }

    /// <summary>Returns a value indicating whether two pixels are equal.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> if every channel is equal.</returns>
    public static bool operator ==(GrayAlpha8 left, GrayAlpha8 right)
    {
        return left.Equals(right);
    }

    /// <summary>Returns a value indicating whether two pixels differ.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> if any channel differs.</returns>
    public static bool operator !=(GrayAlpha8 left, GrayAlpha8 right)
    {
        return !left.Equals(right);
    }
}
