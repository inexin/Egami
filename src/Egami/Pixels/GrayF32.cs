using System.Numerics;
using System.Runtime.InteropServices;

namespace Egami.Pixels;

/// <summary>
/// A pixel with one 32-bit float luminance channel, nominally 0 to 1, and no alpha: PFM gray maps, depth and height maps.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ApiExamples.cs" region="pixel-types"/>
/// </example>
[StructLayout(LayoutKind.Sequential)]
public struct GrayF32 : IPixelType<GrayF32>
{
    /// <summary>The luminance channel.</summary>
    public float L;

    /// <summary>Initializes a new instance of the <see cref="GrayF32"/> struct.</summary>
    /// <param name="l">The luminance channel.</param>
    public GrayF32(float l)
    {
        L = l;
    }

    /// <inheritdoc/>
    public static PixelLayoutInfo Layout => new(PixelChannelOrder.Gray, PixelChannelType.Float32, AlphaKind.None);

    /// <summary>
    /// Creates a pixel from the Rec. 709 luma of a color, weighted in linear light, as the other gray types do. Gray colors keep
    /// their value, and values outside 0..1 are kept rather than clamped.
    /// </summary>
    /// <param name="rgba">The color.</param>
    /// <returns>The pixel.</returns>
    public static GrayF32 FromScaled(Vector4 rgba)
    {
        if (rgba.X == rgba.Y && rgba.Y == rgba.Z)
        {
            return new GrayF32(rgba.X);
        }

        double linear = (0.2126 * Srgb.ToLinear(rgba.X)) + (0.7152 * Srgb.ToLinear(rgba.Y)) + (0.0722 * Srgb.ToLinear(rgba.Z));
        return new GrayF32((float)Srgb.FromLinear(linear));
    }

    /// <inheritdoc/>
    public static GrayF32 FromRgba8(Rgba8 value)
    {
        return FromScaled(value.ToScaled());
    }

    /// <inheritdoc/>
    public readonly Vector4 ToScaled()
    {
        return new Vector4(L, L, L, 1f);
    }

    /// <inheritdoc/>
    public readonly Rgba8 ToRgba8()
    {
        byte l = Scale.ToByte(L);
        return new Rgba8(l, l, l, 255);
    }

    /// <inheritdoc/>
    public readonly bool Equals(GrayF32 other)
    {
        return L.Equals(other.L);
    }

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj)
    {
        return obj is GrayF32 other && Equals(other);
    }

    /// <inheritdoc/>
    public override readonly int GetHashCode()
    {
        return L.GetHashCode();
    }

    /// <inheritdoc/>
    public override readonly string ToString()
    {
        return FormattableString.Invariant($"GrayF32({L})");
    }

    /// <summary>Returns a value indicating whether two pixels are equal.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> if every channel is equal.</returns>
    public static bool operator ==(GrayF32 left, GrayF32 right)
    {
        return left.Equals(right);
    }

    /// <summary>Returns a value indicating whether two pixels differ.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> if any channel differs.</returns>
    public static bool operator !=(GrayF32 left, GrayF32 right)
    {
        return !left.Equals(right);
    }
}
