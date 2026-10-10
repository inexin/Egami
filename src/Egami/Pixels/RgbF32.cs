using System.Numerics;
using System.Runtime.InteropServices;

namespace Egami.Pixels;

/// <summary>A pixel with 32-bit float red, green and blue channels, in that order, nominally 0 to 1, and no alpha.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ApiExamples.cs" region="pixel-types"/>
/// </example>
[StructLayout(LayoutKind.Sequential)]
public struct RgbF32 : IPixelType<RgbF32>
{
    /// <summary>The red channel.</summary>
    public float R;

    /// <summary>The green channel.</summary>
    public float G;

    /// <summary>The blue channel.</summary>
    public float B;

    /// <summary>Initializes a new instance of the <see cref="RgbF32"/> struct.</summary>
    /// <param name="r">The red channel.</param>
    /// <param name="g">The green channel.</param>
    /// <param name="b">The blue channel.</param>
    public RgbF32(float r, float g, float b)
    {
        R = r;
        G = g;
        B = b;
    }

    /// <inheritdoc/>
    public static PixelLayoutInfo Layout => new(PixelChannelOrder.Rgb, PixelChannelType.Float32, AlphaKind.None);

    /// <summary>Creates a pixel from red, green and blue, dropping alpha. Values are stored as given, without clamping.</summary>
    /// <param name="rgba">The color.</param>
    /// <returns>The pixel.</returns>
    public static RgbF32 FromScaled(Vector4 rgba)
    {
        return new RgbF32(rgba.X, rgba.Y, rgba.Z);
    }

    /// <inheritdoc/>
    public static RgbF32 FromRgba8(Rgba8 value)
    {
        return FromScaled(value.ToScaled());
    }

    /// <inheritdoc/>
    public readonly Vector4 ToScaled()
    {
        return new Vector4(R, G, B, 1f);
    }

    /// <inheritdoc/>
    public readonly Rgba8 ToRgba8()
    {
        return new Rgba8(Scale.ToByte(R), Scale.ToByte(G), Scale.ToByte(B), 255);
    }

    /// <inheritdoc/>
    public readonly bool Equals(RgbF32 other)
    {
        return R.Equals(other.R) && G.Equals(other.G) && B.Equals(other.B);
    }

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj)
    {
        return obj is RgbF32 other && Equals(other);
    }

    /// <inheritdoc/>
    public override readonly int GetHashCode()
    {
        return HashCode.Combine(R, G, B);
    }

    /// <inheritdoc/>
    public override readonly string ToString()
    {
        return FormattableString.Invariant($"RgbF32({R}, {G}, {B})");
    }

    /// <summary>Returns a value indicating whether two pixels are equal.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> if every channel is equal.</returns>
    public static bool operator ==(RgbF32 left, RgbF32 right)
    {
        return left.Equals(right);
    }

    /// <summary>Returns a value indicating whether two pixels differ.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> if any channel differs.</returns>
    public static bool operator !=(RgbF32 left, RgbF32 right)
    {
        return !left.Equals(right);
    }
}
