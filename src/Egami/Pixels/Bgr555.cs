using System.Numerics;
using System.Runtime.InteropServices;

namespace Egami.Pixels;

/// <summary>
/// A 16-bit packed pixel: blue in bits 0 to 4, green in bits 5 to 9, red in bits 10 to 14, and no alpha; bit 15 is unused and
/// written as 0. The same memory as the default 16-bit BMP, 15-bit TGA and the GDI+ 16-bit RGB 555 format. Widening and narrowing
/// both round to the nearest value, so every value with bit 15 clear survives a round trip through <see cref="Rgba8"/>.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ApiExamples.cs" region="pixel-types"/>
/// </example>
[StructLayout(LayoutKind.Sequential)]
public struct Bgr555 : IPixelType<Bgr555>
{
    /// <summary>The packed value.</summary>
    public ushort Value;

    /// <summary>Initializes a new instance of the <see cref="Bgr555"/> struct from its packed value.</summary>
    /// <param name="value">The packed value.</param>
    public Bgr555(ushort value)
    {
        Value = value;
    }

    /// <summary>Initializes a new instance of the <see cref="Bgr555"/> struct from channel levels.</summary>
    /// <param name="r">The red level, 0 to 31.</param>
    /// <param name="g">The green level, 0 to 31.</param>
    /// <param name="b">The blue level, 0 to 31.</param>
    /// <exception cref="ArgumentOutOfRangeException">A level is out of its range.</exception>
    public Bgr555(int r, int g, int b)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)r, 31u, nameof(r));
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)g, 31u, nameof(g));
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)b, 31u, nameof(b));
        Value = (ushort)((r << 10) | (g << 5) | b);
    }

    /// <summary>Gets the red level, 0 to 31.</summary>
    public readonly int R => (Value >> 10) & 31;

    /// <summary>Gets the green level, 0 to 31.</summary>
    public readonly int G => (Value >> 5) & 31;

    /// <summary>Gets the blue level, 0 to 31.</summary>
    public readonly int B => Value & 31;

    /// <inheritdoc/>
    public static PixelLayoutInfo Layout { get; } = new(PixelChannelOrder.Bgr, PixelChannelType.Packed, AlphaKind.None, 16, 5, 5, 5);

    /// <inheritdoc/>
    public static Bgr555 FromScaled(Vector4 rgba)
    {
        return Pack(Packing.Level(rgba.X, 31), Packing.Level(rgba.Y, 31), Packing.Level(rgba.Z, 31));
    }

    /// <inheritdoc/>
    public static Bgr555 FromRgba8(Rgba8 value)
    {
        return Pack(Packing.Narrow8(value.R, 31), Packing.Narrow8(value.G, 31), Packing.Narrow8(value.B, 31));
    }

    private static Bgr555 Pack(int r, int g, int b)
    {
        return new Bgr555((ushort)((r << 10) | (g << 5) | b));
    }

    /// <inheritdoc/>
    public readonly Vector4 ToScaled()
    {
        return new Vector4(R / 31f, G / 31f, B / 31f, 1f);
    }

    /// <inheritdoc/>
    public readonly Rgba8 ToRgba8()
    {
        return new Rgba8(Packing.Widen5(R), Packing.Widen5(G), Packing.Widen5(B), 255);
    }

    /// <inheritdoc/>
    public readonly bool Equals(Bgr555 other)
    {
        return Value == other.Value;
    }

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj)
    {
        return obj is Bgr555 other && Equals(other);
    }

    /// <inheritdoc/>
    public override readonly int GetHashCode()
    {
        return Value.GetHashCode();
    }

    /// <inheritdoc/>
    public override readonly string ToString()
    {
        return FormattableString.Invariant($"Bgr555({R}, {G}, {B})");
    }

    /// <summary>Returns a value indicating whether two pixels are equal.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> if the packed values are equal.</returns>
    public static bool operator ==(Bgr555 left, Bgr555 right)
    {
        return left.Equals(right);
    }

    /// <summary>Returns a value indicating whether two pixels differ.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> if the packed values differ.</returns>
    public static bool operator !=(Bgr555 left, Bgr555 right)
    {
        return !left.Equals(right);
    }
}
