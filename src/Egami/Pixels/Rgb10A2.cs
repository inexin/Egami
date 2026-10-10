using System.Numerics;
using System.Runtime.InteropServices;

namespace Egami.Pixels;

/// <summary>
/// A 32-bit packed pixel: red in bits 0 to 9, green in 10 to 19, blue in 20 to 29 and straight alpha in 30 and 31. The same
/// memory as the DXGI R10G10B10A2 and Vulkan A2B10G10R10 formats, used by 10-bit displays. Ten-bit levels round to the nearest
/// 16-bit value and convert through <see cref="Rgba16"/> exactly; two-bit alpha widens by multiplying.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ApiExamples.cs" region="pixel-types"/>
/// </example>
[StructLayout(LayoutKind.Sequential)]
public struct Rgb10A2 : IPixelType<Rgb10A2>
{
    /// <summary>The packed value.</summary>
    public uint Value;

    /// <summary>Initializes a new instance of the <see cref="Rgb10A2"/> struct from its packed value.</summary>
    /// <param name="value">The packed value.</param>
    public Rgb10A2(uint value)
    {
        Value = value;
    }

    /// <summary>Initializes a new instance of the <see cref="Rgb10A2"/> struct from channel levels.</summary>
    /// <param name="r">The red level, 0 to 1023.</param>
    /// <param name="g">The green level, 0 to 1023.</param>
    /// <param name="b">The blue level, 0 to 1023.</param>
    /// <param name="a">The alpha level, 0 to 3.</param>
    /// <exception cref="ArgumentOutOfRangeException">A level is out of its range.</exception>
    public Rgb10A2(int r, int g, int b, int a)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)r, 1023u, nameof(r));
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)g, 1023u, nameof(g));
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)b, 1023u, nameof(b));
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)a, 3u, nameof(a));
        Value = (uint)r | ((uint)g << 10) | ((uint)b << 20) | ((uint)a << 30);
    }

    /// <summary>Gets the red level, 0 to 1023.</summary>
    public readonly int R => (int)(Value & 1023);

    /// <summary>Gets the green level, 0 to 1023.</summary>
    public readonly int G => (int)((Value >> 10) & 1023);

    /// <summary>Gets the blue level, 0 to 1023.</summary>
    public readonly int B => (int)((Value >> 20) & 1023);

    /// <summary>Gets the alpha level, 0 to 3.</summary>
    public readonly int A => (int)(Value >> 30);

    /// <inheritdoc/>
    public static PixelLayoutInfo Layout { get; } = new(PixelChannelOrder.Rgba, PixelChannelType.Packed, AlphaKind.Straight, 32, 10, 10, 10, 2);

    /// <inheritdoc/>
    public static Rgb10A2 FromScaled(Vector4 rgba)
    {
        return Pack(Packing.Level(rgba.X, 1023), Packing.Level(rgba.Y, 1023), Packing.Level(rgba.Z, 1023), Packing.Level(rgba.W, 3));
    }

    /// <inheritdoc/>
    public static Rgb10A2 FromRgba8(Rgba8 value)
    {
        return Pack(Packing.Narrow8(value.R, 1023), Packing.Narrow8(value.G, 1023), Packing.Narrow8(value.B, 1023), Packing.Narrow8(value.A, 3));
    }

    /// <inheritdoc/>
    public static Rgb10A2 FromRgba16(Rgba16 value)
    {
        return Pack(Packing.Narrow16(value.R, 1023), Packing.Narrow16(value.G, 1023), Packing.Narrow16(value.B, 1023), Packing.Narrow16(value.A, 3));
    }

    private static Rgb10A2 Pack(int r, int g, int b, int a)
    {
        return new Rgb10A2((uint)r | ((uint)g << 10) | ((uint)b << 20) | ((uint)a << 30));
    }

    /// <inheritdoc/>
    public readonly Vector4 ToScaled()
    {
        return new Vector4(R / 1023f, G / 1023f, B / 1023f, A / 3f);
    }

    /// <inheritdoc/>
    public readonly Rgba8 ToRgba8()
    {
        return new Rgba8(Packing.Narrow10To8(R), Packing.Narrow10To8(G), Packing.Narrow10To8(B), (byte)(A * 85));
    }

    /// <inheritdoc/>
    public readonly Rgba16 ToRgba16()
    {
        return new Rgba16(Packing.Widen10(R), Packing.Widen10(G), Packing.Widen10(B), (ushort)(A * 21845));
    }

    /// <inheritdoc/>
    public readonly bool Equals(Rgb10A2 other)
    {
        return Value == other.Value;
    }

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj)
    {
        return obj is Rgb10A2 other && Equals(other);
    }

    /// <inheritdoc/>
    public override readonly int GetHashCode()
    {
        return Value.GetHashCode();
    }

    /// <inheritdoc/>
    public override readonly string ToString()
    {
        return FormattableString.Invariant($"Rgb10A2({R}, {G}, {B}, {A})");
    }

    /// <summary>Returns a value indicating whether two pixels are equal.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> if the packed values are equal.</returns>
    public static bool operator ==(Rgb10A2 left, Rgb10A2 right)
    {
        return left.Equals(right);
    }

    /// <summary>Returns a value indicating whether two pixels differ.</summary>
    /// <param name="left">The first pixel.</param>
    /// <param name="right">The second pixel.</param>
    /// <returns><see langword="true"/> if the packed values differ.</returns>
    public static bool operator !=(Rgb10A2 left, Rgb10A2 right)
    {
        return !left.Equals(right);
    }
}
