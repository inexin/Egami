using System.Numerics;
using System.Runtime.InteropServices;

namespace Egami.Pixels;

/// <summary>A pixel with one 8-bit luminance channel and no alpha.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Gray8 : IPixelType<Gray8>
{
    /// <summary>The luminance channel.</summary>
    public byte L;

    /// <summary>Initializes a new instance of the <see cref="Gray8"/> struct.</summary>
    /// <param name="l">The luminance channel.</param>
    public Gray8(byte l)
    {
        L = l;
    }

    /// <inheritdoc/>
    public static PixelLayoutInfo Layout => new(PixelChannelOrder.Gray, PixelChannelType.Unorm8, AlphaKind.None);

    /// <inheritdoc/>
    public static Gray8 FromScaled(Vector4 rgba)
    {
        return new Gray8(Scale.ToByte(Luma.OfScaled(Scale.Clamp(rgba.X), Scale.Clamp(rgba.Y), Scale.Clamp(rgba.Z))));
    }

    /// <inheritdoc/>
    public static Gray8 FromRgba8(Rgba8 value)
    {
        return new Gray8(Luma.Of8(value.R, value.G, value.B));
    }

    /// <inheritdoc/>
    public readonly Vector4 ToScaled()
    {
        float l = L / 255f;
        return new Vector4(l, l, l, 1f);
    }

    /// <inheritdoc/>
    public readonly Rgba8 ToRgba8()
    {
        return new Rgba8(L, L, L, 255);
    }

    /// <inheritdoc/>
    public readonly bool Equals(Gray8 other)
    {
        return L == other.L;
    }

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj)
    {
        return obj is Gray8 other && Equals(other);
    }

    /// <inheritdoc/>
    public override readonly int GetHashCode()
    {
        return HashCode.Combine(L);
    }

    /// <inheritdoc/>
    public override readonly string ToString()
    {
        return FormattableString.Invariant($"Gray8({L})");
    }

    /// <summary>Returns a value indicating whether two pixels are equal.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> if every channel is equal.</returns>
    public static bool operator ==(Gray8 left, Gray8 right)
    {
        return left.Equals(right);
    }

    /// <summary>Returns a value indicating whether two pixels differ.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> if any channel differs.</returns>
    public static bool operator !=(Gray8 left, Gray8 right)
    {
        return !left.Equals(right);
    }
}
