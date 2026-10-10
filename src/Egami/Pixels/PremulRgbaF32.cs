using System.Numerics;
using System.Runtime.InteropServices;

namespace Egami.Pixels;

/// <summary>
/// A pixel with 32-bit float red, green and blue channels premultiplied by the alpha channel, in that order, for float
/// compositing and blending. The conversions take and return straight colors; values are not clamped, except that a
/// premultiplied pixel with zero alpha has no color.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ApiExamples.cs" region="pixel-types"/>
/// </example>
[StructLayout(LayoutKind.Sequential)]
public struct PremulRgbaF32 : IPixelType<PremulRgbaF32>
{
    /// <summary>The red channel, premultiplied.</summary>
    public float R;

    /// <summary>The green channel, premultiplied.</summary>
    public float G;

    /// <summary>The blue channel, premultiplied.</summary>
    public float B;

    /// <summary>The alpha channel.</summary>
    public float A;

    /// <summary>Initializes a new instance of the <see cref="PremulRgbaF32"/> struct from premultiplied channels.</summary>
    /// <param name="r">The red channel, already premultiplied.</param>
    /// <param name="g">The green channel, already premultiplied.</param>
    /// <param name="b">The blue channel, already premultiplied.</param>
    /// <param name="a">The alpha channel.</param>
    public PremulRgbaF32(float r, float g, float b, float a)
    {
        R = r;
        G = g;
        B = b;
        A = a;
    }

    /// <inheritdoc/>
    public static PixelLayoutInfo Layout => new(PixelChannelOrder.Rgba, PixelChannelType.Float32, AlphaKind.Premultiplied);

    /// <summary>Creates a pixel from a straight color by multiplying red, green and blue by alpha. Values are not clamped.</summary>
    /// <param name="rgba">The straight color.</param>
    /// <returns>The pixel.</returns>
    public static PremulRgbaF32 FromScaled(Vector4 rgba)
    {
        return new PremulRgbaF32(rgba.X * rgba.W, rgba.Y * rgba.W, rgba.Z * rgba.W, rgba.W);
    }

    /// <inheritdoc/>
    public static PremulRgbaF32 FromRgba8(Rgba8 value)
    {
        return FromScaled(value.ToScaled());
    }

    /// <summary>Returns the straight color: red, green and blue divided by alpha, or transparent black when alpha is 0.</summary>
    /// <returns>The straight color.</returns>
    public readonly Vector4 ToScaled()
    {
        return A == 0f ? Vector4.Zero : new Vector4(R / A, G / A, B / A, A);
    }

    /// <inheritdoc/>
    public readonly Rgba8 ToRgba8()
    {
        return Rgba8.FromScaled(ToScaled());
    }

    /// <inheritdoc/>
    public readonly bool Equals(PremulRgbaF32 other)
    {
        return R.Equals(other.R) && G.Equals(other.G) && B.Equals(other.B) && A.Equals(other.A);
    }

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj)
    {
        return obj is PremulRgbaF32 other && Equals(other);
    }

    /// <inheritdoc/>
    public override readonly int GetHashCode()
    {
        return HashCode.Combine(R, G, B, A);
    }

    /// <inheritdoc/>
    public override readonly string ToString()
    {
        return FormattableString.Invariant($"PremulRgbaF32({R}, {G}, {B}, {A})");
    }

    /// <summary>Returns a value indicating whether two pixels are equal.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> if every stored channel is equal.</returns>
    public static bool operator ==(PremulRgbaF32 left, PremulRgbaF32 right)
    {
        return left.Equals(right);
    }

    /// <summary>Returns a value indicating whether two pixels differ.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> if any stored channel differs.</returns>
    public static bool operator !=(PremulRgbaF32 left, PremulRgbaF32 right)
    {
        return !left.Equals(right);
    }
}
