using System.Numerics;
using System.Runtime.InteropServices;

namespace Egami.Pixels;

/// <summary>
/// A 16-bit packed pixel: blue in bits 0 to 3, green in 4 to 7, red in 8 to 11 and straight alpha in 12 to 15. The same memory
/// as the DXGI and Vulkan B4G4R4A4 formats. Each level widens to 8 bits by multiplying by 17, and narrowing rounds to nearest.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ApiExamples.cs" region="pixel-types"/>
/// </example>
[StructLayout(LayoutKind.Sequential)]
public struct Bgra4444 : IPixelType<Bgra4444>
{
    /// <summary>The packed value.</summary>
    public ushort Value;

    /// <summary>Initializes a new instance of the <see cref="Bgra4444"/> struct from its packed value.</summary>
    /// <param name="value">The packed value.</param>
    public Bgra4444(ushort value)
    {
        Value = value;
    }

    /// <summary>Initializes a new instance of the <see cref="Bgra4444"/> struct from channel levels, each 0 to 15.</summary>
    /// <param name="r">The red level.</param>
    /// <param name="g">The green level.</param>
    /// <param name="b">The blue level.</param>
    /// <param name="a">The alpha level.</param>
    /// <exception cref="ArgumentOutOfRangeException">A level is greater than 15 or negative.</exception>
    public Bgra4444(int r, int g, int b, int a)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)r, 15u, nameof(r));
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)g, 15u, nameof(g));
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)b, 15u, nameof(b));
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)a, 15u, nameof(a));
        Value = (ushort)((a << 12) | (r << 8) | (g << 4) | b);
    }

    /// <summary>Gets the red level, 0 to 15.</summary>
    public readonly int R => (Value >> 8) & 15;

    /// <summary>Gets the green level, 0 to 15.</summary>
    public readonly int G => (Value >> 4) & 15;

    /// <summary>Gets the blue level, 0 to 15.</summary>
    public readonly int B => Value & 15;

    /// <summary>Gets the alpha level, 0 to 15.</summary>
    public readonly int A => Value >> 12;

    /// <inheritdoc/>
    public static PixelLayoutInfo Layout { get; } = new(PixelChannelOrder.Bgra, PixelChannelType.Packed, AlphaKind.Straight, 16, 4, 4, 4, 4);

    /// <inheritdoc/>
    public static Bgra4444 FromScaled(Vector4 rgba)
    {
        return Pack(Packing.Level(rgba.X, 15), Packing.Level(rgba.Y, 15), Packing.Level(rgba.Z, 15), Packing.Level(rgba.W, 15));
    }

    /// <inheritdoc/>
    public static Bgra4444 FromRgba8(Rgba8 value)
    {
        return Pack(Packing.Narrow8(value.R, 15), Packing.Narrow8(value.G, 15), Packing.Narrow8(value.B, 15), Packing.Narrow8(value.A, 15));
    }

    private static Bgra4444 Pack(int r, int g, int b, int a)
    {
        return new Bgra4444((ushort)((a << 12) | (r << 8) | (g << 4) | b));
    }

    /// <inheritdoc/>
    public readonly Vector4 ToScaled()
    {
        return new Vector4(R, G, B, A) / 15f;
    }

    /// <inheritdoc/>
    public readonly Rgba8 ToRgba8()
    {
        return new Rgba8((byte)(R * 17), (byte)(G * 17), (byte)(B * 17), (byte)(A * 17));
    }

    /// <inheritdoc/>
    public readonly bool Equals(Bgra4444 other)
    {
        return Value == other.Value;
    }

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj)
    {
        return obj is Bgra4444 other && Equals(other);
    }

    /// <inheritdoc/>
    public override readonly int GetHashCode()
    {
        return Value.GetHashCode();
    }

    /// <inheritdoc/>
    public override readonly string ToString()
    {
        return FormattableString.Invariant($"Bgra4444({R}, {G}, {B}, {A})");
    }

    /// <summary>Returns a value indicating whether two pixels are equal.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> if the packed values are equal.</returns>
    public static bool operator ==(Bgra4444 left, Bgra4444 right)
    {
        return left.Equals(right);
    }

    /// <summary>Returns a value indicating whether two pixels differ.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> if the packed values differ.</returns>
    public static bool operator !=(Bgra4444 left, Bgra4444 right)
    {
        return !left.Equals(right);
    }
}
