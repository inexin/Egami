using System.Numerics;
using System.Runtime.InteropServices;

namespace Egami.Pixels;

/// <summary>A pixel with 8-bit red, green and blue channels premultiplied by the alpha channel, in that order: Skia's N32 premultiplied.</summary>
/// <remarks>
/// The conversions take and return straight colors, as every pixel type's do: premultiplying rounds round(c × a / 255) and
/// unpremultiplying rounds round(c × 255 / a), so every valid stored pixel survives a round trip through <see cref="Rgba8"/>.
/// A stored color greater than its alpha, which no straight color produces, converts as if it equalled the alpha. Resize, blur,
/// convolution, rotation and <c>DrawImage</c> work on the stored values directly, without premultiplying first.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ApiExamples.cs" region="pixel-types"/>
/// </example>
[StructLayout(LayoutKind.Sequential)]
public struct PremulRgba8 : IPixelType<PremulRgba8>
{
    /// <summary>The red channel, premultiplied.</summary>
    public byte R;

    /// <summary>The green channel, premultiplied.</summary>
    public byte G;

    /// <summary>The blue channel, premultiplied.</summary>
    public byte B;

    /// <summary>The alpha channel.</summary>
    public byte A;

    /// <summary>Initializes a new instance of the <see cref="PremulRgba8"/> struct from premultiplied channels.</summary>
    /// <param name="r">The red channel, already premultiplied.</param>
    /// <param name="g">The green channel, already premultiplied.</param>
    /// <param name="b">The blue channel, already premultiplied.</param>
    /// <param name="a">The alpha channel.</param>
    public PremulRgba8(byte r, byte g, byte b, byte a)
    {
        R = r;
        G = g;
        B = b;
        A = a;
    }

    /// <inheritdoc/>
    public static PixelLayoutInfo Layout => new(PixelChannelOrder.Rgba, PixelChannelType.Unorm8, AlphaKind.Premultiplied);

    /// <inheritdoc/>
    public static PremulRgba8 FromScaled(Vector4 rgba)
    {
        float alpha = Scale.Clamp(rgba.W);
        return new PremulRgba8(Premultiply.Scaled(rgba.X, alpha), Premultiply.Scaled(rgba.Y, alpha), Premultiply.Scaled(rgba.Z, alpha), Scale.ToByte(alpha));
    }

    /// <inheritdoc/>
    public static PremulRgba8 FromRgba8(Rgba8 value)
    {
        return new PremulRgba8(Premultiply.Times(value.R, value.A), Premultiply.Times(value.G, value.A), Premultiply.Times(value.B, value.A), value.A);
    }

    /// <inheritdoc/>
    public readonly Vector4 ToScaled()
    {
        return ToRgba8().ToScaled();
    }

    /// <inheritdoc/>
    public readonly Rgba8 ToRgba8()
    {
        return new Rgba8(Premultiply.Over(R, A), Premultiply.Over(G, A), Premultiply.Over(B, A), A);
    }

    /// <inheritdoc/>
    public readonly bool Equals(PremulRgba8 other)
    {
        return R == other.R && G == other.G && B == other.B && A == other.A;
    }

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj)
    {
        return obj is PremulRgba8 other && Equals(other);
    }

    /// <inheritdoc/>
    public override readonly int GetHashCode()
    {
        return HashCode.Combine(R, G, B, A);
    }

    /// <inheritdoc/>
    public override readonly string ToString()
    {
        return FormattableString.Invariant($"PremulRgba8({R}, {G}, {B}, {A})");
    }

    /// <summary>Returns a value indicating whether two pixels are equal.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> if every stored channel is equal.</returns>
    public static bool operator ==(PremulRgba8 left, PremulRgba8 right)
    {
        return left.Equals(right);
    }

    /// <summary>Returns a value indicating whether two pixels differ.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> if any stored channel differs.</returns>
    public static bool operator !=(PremulRgba8 left, PremulRgba8 right)
    {
        return !left.Equals(right);
    }
}
