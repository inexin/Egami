using System.Numerics;
using System.Runtime.InteropServices;

namespace Egami.Pixels;

/// <summary>
/// A pixel with 16-bit float (<see cref="Half"/>) red, green, blue and straight alpha channels, in that order, nominally 0 to 1.
/// Values outside 0..1 (HDR) are kept until written to an integer type; conversion to and from <see cref="float"/> rounds as
/// <c>(Half)f</c> does: to nearest even, with values above 65,504 becoming infinity.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ApiExamples.cs" region="pixel-types"/>
/// </example>
[StructLayout(LayoutKind.Sequential)]
public struct RgbaF16 : IPixelType<RgbaF16>
{
    /// <summary>The red channel.</summary>
    public Half R;

    /// <summary>The green channel.</summary>
    public Half G;

    /// <summary>The blue channel.</summary>
    public Half B;

    /// <summary>The alpha channel.</summary>
    public Half A;

    /// <summary>Initializes a new instance of the <see cref="RgbaF16"/> struct.</summary>
    /// <param name="r">The red channel.</param>
    /// <param name="g">The green channel.</param>
    /// <param name="b">The blue channel.</param>
    /// <param name="a">The alpha channel.</param>
    public RgbaF16(Half r, Half g, Half b, Half a)
    {
        R = r;
        G = g;
        B = b;
        A = a;
    }

    /// <inheritdoc/>
    public static PixelLayoutInfo Layout => new(PixelChannelOrder.Rgba, PixelChannelType.Float16, AlphaKind.Straight);

    /// <summary>Creates a pixel from red, green, blue and alpha. Values are rounded to half precision, without clamping.</summary>
    /// <param name="rgba">The color.</param>
    /// <returns>The pixel.</returns>
    public static RgbaF16 FromScaled(Vector4 rgba)
    {
        return new RgbaF16((Half)rgba.X, (Half)rgba.Y, (Half)rgba.Z, (Half)rgba.W);
    }

    /// <inheritdoc/>
    public static RgbaF16 FromRgba8(Rgba8 value)
    {
        return FromScaled(value.ToScaled());
    }

    /// <inheritdoc/>
    public readonly Vector4 ToScaled()
    {
        return new Vector4((float)R, (float)G, (float)B, (float)A);
    }

    /// <inheritdoc/>
    public readonly Rgba8 ToRgba8()
    {
        return new Rgba8(Scale.ToByte((float)R), Scale.ToByte((float)G), Scale.ToByte((float)B), Scale.ToByte((float)A));
    }

    /// <inheritdoc/>
    public readonly bool Equals(RgbaF16 other)
    {
        return R.Equals(other.R) && G.Equals(other.G) && B.Equals(other.B) && A.Equals(other.A);
    }

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj)
    {
        return obj is RgbaF16 other && Equals(other);
    }

    /// <inheritdoc/>
    public override readonly int GetHashCode()
    {
        return HashCode.Combine(R, G, B, A);
    }

    /// <inheritdoc/>
    public override readonly string ToString()
    {
        return FormattableString.Invariant($"RgbaF16({R}, {G}, {B}, {A})");
    }

    /// <summary>Returns a value indicating whether two pixels are equal.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> if every channel is equal.</returns>
    public static bool operator ==(RgbaF16 left, RgbaF16 right)
    {
        return left.Equals(right);
    }

    /// <summary>Returns a value indicating whether two pixels differ.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> if any channel differs.</returns>
    public static bool operator !=(RgbaF16 left, RgbaF16 right)
    {
        return !left.Equals(right);
    }
}
