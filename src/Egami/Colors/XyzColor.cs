using Egami.Pixels;

namespace Egami.Colors;

/// <summary>A CIE 1931 XYZ color relative to D65 white (Y = 1 for white), CSS Color 4's XYZ; <see cref="ToD50"/> adapts it.</summary>
/// <param name="X">The X component.</param>
/// <param name="Y">The Y component: relative luminance.</param>
/// <param name="Z">The Z component.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ColorManagement.cs" region="color-values"/>
/// </example>
public readonly record struct XyzColor(float X, float Y, float Z) : IColorValue<XyzColor>
{
    /// <inheritdoc/>
    public static XyzColor FromXyz(XyzColor xyz) => xyz;

    /// <inheritdoc/>
    public static XyzColor From(Rgba8 color) => Of(ColorValueMath.XyzFromSrgb(color.R / 255.0, color.G / 255.0, color.B / 255.0));

    /// <inheritdoc/>
    public static XyzColor From(RgbaF32 color) => Of(ColorValueMath.XyzFromSrgb(color.R, color.G, color.B));

    /// <summary>Returns the color adapted from D50 to D65 with Bradford, as ICC and CSS Color 4 adapt.</summary>
    /// <param name="d50">XYZ relative to D50 white.</param>
    /// <returns>The color relative to D65.</returns>
    public static XyzColor FromD50(XyzColor d50) => Of(ColorValueMath.D50ToD65 * d50.Vector);

    /// <inheritdoc/>
    public XyzColor ToXyz() => this;

    /// <summary>Returns the color adapted from D65 to D50 with Bradford, as ICC and CSS Color 4 adapt.</summary>
    /// <returns>XYZ relative to D50 white.</returns>
    public XyzColor ToD50() => Of(ColorValueMath.D65ToD50 * Vector);

    /// <inheritdoc/>
    public Rgba8 ToRgba8() => ColorValueMath.ToRgba8(ColorValueMath.SrgbFromXyz(Vector));

    /// <inheritdoc/>
    public RgbaF32 ToRgbaF32() => ColorValueMath.ToRgbaF32(ColorValueMath.SrgbFromXyz(Vector));

    /// <inheritdoc/>
    public bool IsCloseTo(XyzColor other, float tolerance = 1e-4f) =>
        ColorValueMath.Close(X, other.X, tolerance) && ColorValueMath.Close(Y, other.Y, tolerance) && ColorValueMath.Close(Z, other.Z, tolerance);

    internal Vector3d Vector => new(X, Y, Z);

    internal static XyzColor Of(Vector3d v) => new((float)v.X, (float)v.Y, (float)v.Z);
}
