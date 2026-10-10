using System.Numerics;
using System.Runtime.InteropServices;

namespace Egami.Pixels;

/// <summary>A pixel with 8-bit blue, green and red channels premultiplied by the alpha channel, in that order: the layout of Windows and GPU surfaces.</summary>
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
public struct PremulBgra8 : IPixelType<PremulBgra8>
{
    /// <summary>The blue channel, premultiplied.</summary>
    public byte B;

    /// <summary>The green channel, premultiplied.</summary>
    public byte G;

    /// <summary>The red channel, premultiplied.</summary>
    public byte R;

    /// <summary>The alpha channel.</summary>
    public byte A;

    /// <summary>Initializes a new instance of the <see cref="PremulBgra8"/> struct from premultiplied channels.</summary>
    /// <param name="b">The blue channel, already premultiplied.</param>
    /// <param name="g">The green channel, already premultiplied.</param>
    /// <param name="r">The red channel, already premultiplied.</param>
    /// <param name="a">The alpha channel.</param>
    public PremulBgra8(byte b, byte g, byte r, byte a)
    {
        B = b;
        G = g;
        R = r;
        A = a;
    }

    /// <inheritdoc/>
    public static PixelLayoutInfo Layout => new(PixelChannelOrder.Bgra, PixelChannelType.Unorm8, AlphaKind.Premultiplied);

    /// <inheritdoc/>
    public static PremulBgra8 FromScaled(Vector4 rgba)
    {
        float alpha = Scale.Clamp(rgba.W);
        return new PremulBgra8(Premultiply.Scaled(rgba.Z, alpha), Premultiply.Scaled(rgba.Y, alpha), Premultiply.Scaled(rgba.X, alpha), Scale.ToByte(alpha));
    }

    /// <inheritdoc/>
    public static PremulBgra8 FromRgba8(Rgba8 value)
    {
        return new PremulBgra8(Premultiply.Times(value.B, value.A), Premultiply.Times(value.G, value.A), Premultiply.Times(value.R, value.A), value.A);
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
    public readonly bool Equals(PremulBgra8 other)
    {
        return B == other.B && G == other.G && R == other.R && A == other.A;
    }

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj)
    {
        return obj is PremulBgra8 other && Equals(other);
    }

    /// <inheritdoc/>
    public override readonly int GetHashCode()
    {
        return HashCode.Combine(B, G, R, A);
    }

    /// <inheritdoc/>
    public override readonly string ToString()
    {
        return FormattableString.Invariant($"PremulBgra8({B}, {G}, {R}, {A})");
    }

    /// <summary>Returns a value indicating whether two pixels are equal.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> if every stored channel is equal.</returns>
    public static bool operator ==(PremulBgra8 left, PremulBgra8 right)
    {
        return left.Equals(right);
    }

    /// <summary>Returns a value indicating whether two pixels differ.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> if any stored channel differs.</returns>
    public static bool operator !=(PremulBgra8 left, PremulBgra8 right)
    {
        return !left.Equals(right);
    }
}
