using System.Numerics;
using System.Runtime.InteropServices;

namespace Egami.Pixels;

/// <summary>A pixel with 16-bit cyan, magenta, yellow and black ink amounts, in that order, 0 meaning no ink. 16-bit CMYK TIFF and PSD.</summary>
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
public struct Cmyk16 : IPixelType<Cmyk16>
{
    /// <summary>The cyan ink, 0 for none.</summary>
    public ushort C;

    /// <summary>The magenta ink, 0 for none.</summary>
    public ushort M;

    /// <summary>The yellow ink, 0 for none.</summary>
    public ushort Y;

    /// <summary>The black ink, 0 for none.</summary>
    public ushort K;

    /// <summary>Initializes a new instance of the <see cref="Cmyk16"/> struct.</summary>
    /// <param name="c">The cyan ink.</param>
    /// <param name="m">The magenta ink.</param>
    /// <param name="y">The yellow ink.</param>
    /// <param name="k">The black ink.</param>
    public Cmyk16(ushort c, ushort m, ushort y, ushort k)
    {
        C = c;
        M = m;
        Y = y;
        K = k;
    }

    /// <inheritdoc/>
    public static PixelLayoutInfo Layout => new(PixelChannelOrder.Cmyk, PixelChannelType.Unorm16, AlphaKind.None);

    /// <inheritdoc/>
    public static Cmyk16 FromScaled(Vector4 rgba)
    {
        Vector4 ink = CmykInk.FromRgb(rgba);
        return new Cmyk16((ushort)((ink.X * 65535f) + 0.5f), (ushort)((ink.Y * 65535f) + 0.5f), (ushort)((ink.Z * 65535f) + 0.5f), (ushort)((ink.W * 65535f) + 0.5f));
    }

    /// <inheritdoc/>
    public static Cmyk16 FromRgba8(Rgba8 value)
    {
        return FromRgba16(Rgba16.FromRgba8(value));
    }

    /// <inheritdoc/>
    public static Cmyk16 FromRgba16(Rgba16 value)
    {
        int max = Math.Max(value.R, Math.Max(value.G, value.B));
        if (max == 0)
        {
            return new Cmyk16(0, 0, 0, 65535);
        }

        return new Cmyk16(Ink(value.R, max), Ink(value.G, max), Ink(value.B, max), (ushort)(65535 - max));

        static ushort Ink(int channel, int max) => (ushort)(((((long)max - channel) * 65535) + (max / 2)) / max);
    }

    /// <inheritdoc/>
    public readonly Rgba16 ToRgba16()
    {
        return new Rgba16(Mix(C, K), Mix(M, K), Mix(Y, K), 65535);

        static ushort Mix(ushort ink, ushort black) => (ushort)(((((long)65535 - ink) * (65535 - black)) + 32767) / 65535);
    }

    /// <inheritdoc/>
    public readonly Vector4 ToScaled()
    {
        float k = 1f - (K / 65535f);
        return new Vector4((1f - (C / 65535f)) * k, (1f - (M / 65535f)) * k, (1f - (Y / 65535f)) * k, 1f);
    }

    /// <inheritdoc/>
    public readonly Rgba8 ToRgba8()
    {
        return Rgba8.FromScaled(ToScaled());
    }

    /// <inheritdoc/>
    public readonly bool Equals(Cmyk16 other)
    {
        return C == other.C && M == other.M && Y == other.Y && K == other.K;
    }

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj)
    {
        return obj is Cmyk16 other && Equals(other);
    }

    /// <inheritdoc/>
    public override readonly int GetHashCode()
    {
        return HashCode.Combine(C, M, Y, K);
    }

    /// <inheritdoc/>
    public override readonly string ToString()
    {
        return FormattableString.Invariant($"Cmyk16({C}, {M}, {Y}, {K})");
    }

    /// <summary>Returns a value indicating whether two pixels are equal.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> if every channel is equal.</returns>
    public static bool operator ==(Cmyk16 left, Cmyk16 right)
    {
        return left.Equals(right);
    }

    /// <summary>Returns a value indicating whether two pixels differ.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> if any channel differs.</returns>
    public static bool operator !=(Cmyk16 left, Cmyk16 right)
    {
        return !left.Equals(right);
    }
}
