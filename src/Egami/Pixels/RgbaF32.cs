using System.Numerics;
using System.Runtime.InteropServices;

namespace Egami.Pixels;

/// <summary>A pixel with 32-bit float red, green, blue and straight alpha channels, in that order, nominally 0 to 1.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct RgbaF32 : IPixelType<RgbaF32>
{
    /// <summary>The red channel.</summary>
    public float R;

    /// <summary>The green channel.</summary>
    public float G;

    /// <summary>The blue channel.</summary>
    public float B;

    /// <summary>The alpha channel.</summary>
    public float A;

    /// <summary>Initializes a new instance of the <see cref="RgbaF32"/> struct.</summary>
    /// <param name="r">The red channel.</param>
    /// <param name="g">The green channel.</param>
    /// <param name="b">The blue channel.</param>
    /// <param name="a">The alpha channel.</param>
    public RgbaF32(float r, float g, float b, float a)
    {
        R = r;
        G = g;
        B = b;
        A = a;
    }

    /// <inheritdoc/>
    public static PixelLayoutInfo Layout => new(PixelChannelOrder.Rgba, PixelChannelType.Float32, AlphaKind.Straight);

    /// <summary>Creates a pixel from red, green, blue and alpha. Values are stored as given, without clamping.</summary>
    /// <param name="rgba">The color.</param>
    /// <returns>The pixel.</returns>
    public static RgbaF32 FromScaled(Vector4 rgba)
    {
        return new RgbaF32(rgba.X, rgba.Y, rgba.Z, rgba.W);
    }

    /// <inheritdoc/>
    public static RgbaF32 FromRgba8(Rgba8 value)
    {
        Vector4 v = value.ToScaled();
        return new RgbaF32(v.X, v.Y, v.Z, v.W);
    }

    /// <inheritdoc/>
    public readonly Vector4 ToScaled()
    {
        return new Vector4(R, G, B, A);
    }

    /// <inheritdoc/>
    public readonly Rgba8 ToRgba8()
    {
        return new Rgba8(Scale.ToByte(R), Scale.ToByte(G), Scale.ToByte(B), Scale.ToByte(A));
    }

    /// <inheritdoc/>
    public readonly bool Equals(RgbaF32 other)
    {
        return R.Equals(other.R) && G.Equals(other.G) && B.Equals(other.B) && A.Equals(other.A);
    }

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj)
    {
        return obj is RgbaF32 other && Equals(other);
    }

    /// <inheritdoc/>
    public override readonly int GetHashCode()
    {
        return HashCode.Combine(R, G, B, A);
    }

    /// <inheritdoc/>
    public override readonly string ToString()
    {
        return FormattableString.Invariant($"RgbaF32({R}, {G}, {B}, {A})");
    }

    /// <summary>Returns a value indicating whether two pixels are equal.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> if every channel is equal.</returns>
    public static bool operator ==(RgbaF32 left, RgbaF32 right)
    {
        return left.Equals(right);
    }

    /// <summary>Returns a value indicating whether two pixels differ.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> if any channel differs.</returns>
    public static bool operator !=(RgbaF32 left, RgbaF32 right)
    {
        return !left.Equals(right);
    }
}
