using System.Numerics;
using System.Runtime.InteropServices;

namespace Egami.Pixels;

/// <summary>
/// A 16-bit packed pixel: blue in bits 0 to 4, green in bits 5 to 9, red in bits 10 to 14 and a 1-bit straight alpha in bit 15
/// (1 is opaque). The same memory as 16-bit TGA with one attribute bit, the BMP 1-5-5-5 bit field and the GDI+ 16-bit ARGB 1555
/// format. Color levels round to the nearest value; alpha 128 and above narrows to opaque. Every 16-bit value survives a round
/// trip through <see cref="Rgba8"/>.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ApiExamples.cs" region="pixel-types"/>
/// </example>
[StructLayout(LayoutKind.Sequential)]
public struct Bgra5551 : IPixelType<Bgra5551>
{
    /// <summary>The packed value.</summary>
    public ushort Value;

    /// <summary>Initializes a new instance of the <see cref="Bgra5551"/> struct from its packed value.</summary>
    /// <param name="value">The packed value.</param>
    public Bgra5551(ushort value)
    {
        Value = value;
    }

    /// <summary>Initializes a new instance of the <see cref="Bgra5551"/> struct from channel levels.</summary>
    /// <param name="r">The red level, 0 to 31.</param>
    /// <param name="g">The green level, 0 to 31.</param>
    /// <param name="b">The blue level, 0 to 31.</param>
    /// <param name="a">The alpha level: 0 transparent, 1 opaque.</param>
    /// <exception cref="ArgumentOutOfRangeException">A level is out of its range.</exception>
    public Bgra5551(int r, int g, int b, int a)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)r, 31u, nameof(r));
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)g, 31u, nameof(g));
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)b, 31u, nameof(b));
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)a, 1u, nameof(a));
        Value = (ushort)((a << 15) | (r << 10) | (g << 5) | b);
    }

    /// <summary>Gets the red level, 0 to 31.</summary>
    public readonly int R => (Value >> 10) & 31;

    /// <summary>Gets the green level, 0 to 31.</summary>
    public readonly int G => (Value >> 5) & 31;

    /// <summary>Gets the blue level, 0 to 31.</summary>
    public readonly int B => Value & 31;

    /// <summary>Gets the alpha level: 0 transparent, 1 opaque.</summary>
    public readonly int A => Value >> 15;

    /// <inheritdoc/>
    public static PixelLayoutInfo Layout { get; } = new(PixelChannelOrder.Bgra, PixelChannelType.Packed, AlphaKind.Straight, 16, 5, 5, 5, 1);

    /// <inheritdoc/>
    public static Bgra5551 FromScaled(Vector4 rgba)
    {
        return Pack(Packing.Level(rgba.X, 31), Packing.Level(rgba.Y, 31), Packing.Level(rgba.Z, 31), Packing.Level(rgba.W, 1));
    }

    /// <inheritdoc/>
    public static Bgra5551 FromRgba8(Rgba8 value)
    {
        return Pack(Packing.Narrow8(value.R, 31), Packing.Narrow8(value.G, 31), Packing.Narrow8(value.B, 31), Packing.Narrow8(value.A, 1));
    }

    private static Bgra5551 Pack(int r, int g, int b, int a)
    {
        return new Bgra5551((ushort)((a << 15) | (r << 10) | (g << 5) | b));
    }

    /// <inheritdoc/>
    public readonly Vector4 ToScaled()
    {
        return new Vector4(R / 31f, G / 31f, B / 31f, A);
    }

    /// <inheritdoc/>
    public readonly Rgba8 ToRgba8()
    {
        return new Rgba8(Packing.Widen5(R), Packing.Widen5(G), Packing.Widen5(B), (byte)(A * 255));
    }

    /// <inheritdoc/>
    public readonly bool Equals(Bgra5551 other)
    {
        return Value == other.Value;
    }

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj)
    {
        return obj is Bgra5551 other && Equals(other);
    }

    /// <inheritdoc/>
    public override readonly int GetHashCode()
    {
        return Value.GetHashCode();
    }

    /// <inheritdoc/>
    public override readonly string ToString()
    {
        return FormattableString.Invariant($"Bgra5551({R}, {G}, {B}, {A})");
    }

    /// <summary>Returns a value indicating whether two pixels are equal.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> if the packed values are equal.</returns>
    public static bool operator ==(Bgra5551 left, Bgra5551 right)
    {
        return left.Equals(right);
    }

    /// <summary>Returns a value indicating whether two pixels differ.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> if the packed values differ.</returns>
    public static bool operator !=(Bgra5551 left, Bgra5551 right)
    {
        return !left.Equals(right);
    }
}
