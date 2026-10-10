using System.Numerics;
using System.Runtime.InteropServices;

namespace Egami.Pixels;

/// <summary>A pixel with 16-bit luminance and straight alpha channels, in that order.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ApiExamples.cs" region="pixel-types"/>
/// </example>
[StructLayout(LayoutKind.Sequential)]
public struct GrayAlpha16 : IPixelType<GrayAlpha16>
{
    /// <summary>The luminance channel.</summary>
    public ushort L;

    /// <summary>The alpha channel.</summary>
    public ushort A;

    /// <summary>Initializes a new instance of the <see cref="GrayAlpha16"/> struct.</summary>
    /// <param name="l">The luminance channel.</param>
    /// <param name="a">The alpha channel.</param>
    public GrayAlpha16(ushort l, ushort a)
    {
        L = l;
        A = a;
    }

    /// <inheritdoc/>
    public static PixelLayoutInfo Layout => new(PixelChannelOrder.GrayAlpha, PixelChannelType.Unorm16, AlphaKind.Straight);

    /// <inheritdoc/>
    public static GrayAlpha16 FromScaled(Vector4 rgba)
    {
        return new GrayAlpha16(Luma16.OfScaled(Scale.Clamp(rgba.X), Scale.Clamp(rgba.Y), Scale.Clamp(rgba.Z)), Scale.ToUInt16(rgba.W));
    }

    /// <inheritdoc/>
    public static GrayAlpha16 FromRgba8(Rgba8 value)
    {
        // The same luminance Gray16 takes, so a color converts to the same gray with or without alpha.
        ushort l = value.R == value.G && value.G == value.B ? Scale.Widen(value.R) : Luma16.Of8(value.R, value.G, value.B);
        return new GrayAlpha16(l, Scale.Widen(value.A));
    }

    /// <inheritdoc/>
    public readonly Vector4 ToScaled()
    {
        float l = L / 65535f;
        return new Vector4(l, l, l, A / 65535f);
    }

    /// <inheritdoc/>
    public readonly Rgba8 ToRgba8()
    {
        byte l = Scale.Narrow(L);
        return new Rgba8(l, l, l, Scale.Narrow(A));
    }

    /// <inheritdoc/>
    public readonly Rgba16 ToRgba16()
    {
        return new Rgba16(L, L, L, A);
    }

    /// <inheritdoc/>
    public readonly bool Equals(GrayAlpha16 other)
    {
        return L == other.L && A == other.A;
    }

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj)
    {
        return obj is GrayAlpha16 other && Equals(other);
    }

    /// <inheritdoc/>
    public override readonly int GetHashCode()
    {
        return HashCode.Combine(L, A);
    }

    /// <inheritdoc/>
    public override readonly string ToString()
    {
        return FormattableString.Invariant($"GrayAlpha16({L}, {A})");
    }

    /// <summary>Returns a value indicating whether two pixels are equal.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> if every channel is equal.</returns>
    public static bool operator ==(GrayAlpha16 left, GrayAlpha16 right)
    {
        return left.Equals(right);
    }

    /// <summary>Returns a value indicating whether two pixels differ.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> if any channel differs.</returns>
    public static bool operator !=(GrayAlpha16 left, GrayAlpha16 right)
    {
        return !left.Equals(right);
    }
}
