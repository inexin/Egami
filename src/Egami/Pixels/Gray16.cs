using System.Numerics;
using System.Runtime.InteropServices;

namespace Egami.Pixels;

/// <summary>A pixel with one 16-bit luminance channel and no alpha.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ApiExamples.cs" region="pixel-types"/>
/// </example>
[StructLayout(LayoutKind.Sequential)]
public struct Gray16 : IPixelType<Gray16>
{
    /// <summary>The luminance channel.</summary>
    public ushort L;

    /// <summary>Initializes a new instance of the <see cref="Gray16"/> struct.</summary>
    /// <param name="l">The luminance channel.</param>
    public Gray16(ushort l)
    {
        L = l;
    }

    /// <inheritdoc/>
    public static PixelLayoutInfo Layout => new(PixelChannelOrder.Gray, PixelChannelType.Unorm16, AlphaKind.None);

    /// <inheritdoc/>
    public static Gray16 FromScaled(Vector4 rgba)
    {
        return new Gray16(Luma16.OfScaled(Scale.Clamp(rgba.X), Scale.Clamp(rgba.Y), Scale.Clamp(rgba.Z)));
    }

    /// <inheritdoc/>
    public static Gray16 FromRgba8(Rgba8 value)
    {
        return value.R == value.G && value.G == value.B
            ? new Gray16(Scale.Widen(value.R))
            : new Gray16(Luma16.Of8(value.R, value.G, value.B));
    }

    /// <inheritdoc/>
    public readonly Vector4 ToScaled()
    {
        float l = L / 65535f;
        return new Vector4(l, l, l, 1f);
    }

    /// <inheritdoc/>
    public readonly Rgba8 ToRgba8()
    {
        byte l = Scale.Narrow(L);
        return new Rgba8(l, l, l, 255);
    }

    /// <inheritdoc/>
    public readonly Rgba16 ToRgba16()
    {
        return new Rgba16(L, L, L, ushort.MaxValue);
    }

    /// <inheritdoc/>
    public readonly bool Equals(Gray16 other)
    {
        return L == other.L;
    }

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj)
    {
        return obj is Gray16 other && Equals(other);
    }

    /// <inheritdoc/>
    public override readonly int GetHashCode()
    {
        return HashCode.Combine(L);
    }

    /// <inheritdoc/>
    public override readonly string ToString()
    {
        return FormattableString.Invariant($"Gray16({L})");
    }

    /// <summary>Returns a value indicating whether two pixels are equal.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> if every channel is equal.</returns>
    public static bool operator ==(Gray16 left, Gray16 right)
    {
        return left.Equals(right);
    }

    /// <summary>Returns a value indicating whether two pixels differ.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> if any channel differs.</returns>
    public static bool operator !=(Gray16 left, Gray16 right)
    {
        return !left.Equals(right);
    }
}
