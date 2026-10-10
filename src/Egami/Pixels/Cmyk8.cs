using System.Numerics;
using System.Runtime.InteropServices;

namespace Egami.Pixels;

/// <summary>A pixel with 8-bit cyan, magenta, yellow and black ink amounts, in that order, 0 meaning no ink. CMYK JPEG, TIFF and PSD.</summary>
/// <remarks>
/// Without a color profile, conversion uses the plain formula: red = (1 − C)(1 − K), and from RGB, K = 1 − max(R, G, B) and
/// C = (1 − R − K) / (1 − K). That is what the JPEG reader has always used to turn CMYK into RGB, but real print colors need the
/// file's ICC profile. Resize, blur, sharpen, convolution and rotation work on the four ink channels, so black stays black;
/// color adjustments go through RGB and so rebuild K from the formula.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ApiExamples.cs" region="pixel-types"/>
/// </example>
[StructLayout(LayoutKind.Sequential)]
public struct Cmyk8 : IPixelType<Cmyk8>
{
    /// <summary>The cyan ink, 0 for none.</summary>
    public byte C;

    /// <summary>The magenta ink, 0 for none.</summary>
    public byte M;

    /// <summary>The yellow ink, 0 for none.</summary>
    public byte Y;

    /// <summary>The black ink, 0 for none.</summary>
    public byte K;

    /// <summary>Initializes a new instance of the <see cref="Cmyk8"/> struct.</summary>
    /// <param name="c">The cyan ink.</param>
    /// <param name="m">The magenta ink.</param>
    /// <param name="y">The yellow ink.</param>
    /// <param name="k">The black ink.</param>
    public Cmyk8(byte c, byte m, byte y, byte k)
    {
        C = c;
        M = m;
        Y = y;
        K = k;
    }

    /// <inheritdoc/>
    public static PixelLayoutInfo Layout => new(PixelChannelOrder.Cmyk, PixelChannelType.Unorm8, AlphaKind.None);

    /// <inheritdoc/>
    public static Cmyk8 FromScaled(Vector4 rgba)
    {
        Vector4 ink = CmykInk.FromRgb(rgba);
        return new Cmyk8((byte)((ink.X * 255f) + 0.5f), (byte)((ink.Y * 255f) + 0.5f), (byte)((ink.Z * 255f) + 0.5f), (byte)((ink.W * 255f) + 0.5f));
    }

    /// <inheritdoc/>
    public static Cmyk8 FromRgba8(Rgba8 value)
    {
        int max = Math.Max(value.R, Math.Max(value.G, value.B));
        if (max == 0)
        {
            return new Cmyk8(0, 0, 0, 255);
        }

        return new Cmyk8(Ink(value.R, max), Ink(value.G, max), Ink(value.B, max), (byte)(255 - max));

        static byte Ink(int channel, int max) => (byte)((((max - channel) * 255) + (max / 2)) / max);
    }

    /// <inheritdoc/>
    public readonly Vector4 ToScaled()
    {
        float k = 1f - (K / 255f);
        return new Vector4((1f - (C / 255f)) * k, (1f - (M / 255f)) * k, (1f - (Y / 255f)) * k, 1f);
    }

    /// <inheritdoc/>
    public readonly Rgba8 ToRgba8()
    {
        return new Rgba8(Mix(C, K), Mix(M, K), Mix(Y, K), 255);

        static byte Mix(byte ink, byte black) => (byte)((((255 - ink) * (255 - black)) + 127) / 255);
    }

    /// <inheritdoc/>
    public readonly bool Equals(Cmyk8 other)
    {
        return C == other.C && M == other.M && Y == other.Y && K == other.K;
    }

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj)
    {
        return obj is Cmyk8 other && Equals(other);
    }

    /// <inheritdoc/>
    public override readonly int GetHashCode()
    {
        return HashCode.Combine(C, M, Y, K);
    }

    /// <inheritdoc/>
    public override readonly string ToString()
    {
        return FormattableString.Invariant($"Cmyk8({C}, {M}, {Y}, {K})");
    }

    /// <summary>Returns a value indicating whether two pixels are equal.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> if every channel is equal.</returns>
    public static bool operator ==(Cmyk8 left, Cmyk8 right)
    {
        return left.Equals(right);
    }

    /// <summary>Returns a value indicating whether two pixels differ.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> if any channel differs.</returns>
    public static bool operator !=(Cmyk8 left, Cmyk8 right)
    {
        return !left.Equals(right);
    }
}
