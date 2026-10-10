using Egami.Pixels;

namespace Egami.Colors;

/// <summary>
/// A color value in one color model, convertible to and from CIE XYZ (D65) and sRGB pixels, so <see cref="ColorConvert"/> can
/// convert between any two models and between pixels and values.
/// </summary>
/// <typeparam name="TSelf">The color value type.</typeparam>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ColorManagement.cs" region="color-values"/>
/// </example>
public interface IColorValue<TSelf>
    where TSelf : struct, IColorValue<TSelf>
{
    /// <summary>Returns the value for an XYZ color (D65, white Y = 1).</summary>
    /// <param name="xyz">The color.</param>
    /// <returns>The value; components may fall outside their usual range for colors outside sRGB.</returns>
    static abstract TSelf FromXyz(XyzColor xyz);

    /// <summary>Returns the value of an sRGB pixel; alpha is ignored.</summary>
    /// <param name="color">The pixel.</param>
    /// <returns>The value.</returns>
    static abstract TSelf From(Rgba8 color);

    /// <summary>Returns the value of an sRGB pixel, whose channels may be outside 0..1; alpha is ignored.</summary>
    /// <param name="color">The pixel.</param>
    /// <returns>The value.</returns>
    static abstract TSelf From(RgbaF32 color);

    /// <summary>Returns the color as XYZ (D65, white Y = 1).</summary>
    /// <returns>The color.</returns>
    XyzColor ToXyz();

    /// <summary>Returns the color as an opaque sRGB pixel, each channel clamped into range.</summary>
    /// <returns>The pixel.</returns>
    Rgba8 ToRgba8();

    /// <summary>Returns the color as an opaque sRGB pixel, unclamped, so colors outside sRGB keep channels outside 0..1.</summary>
    /// <returns>The pixel.</returns>
    RgbaF32 ToRgbaF32();

    /// <summary>Returns whether every component of the two values differs by at most <paramref name="tolerance"/>.</summary>
    /// <param name="other">The other value.</param>
    /// <param name="tolerance">The largest difference allowed in a component.</param>
    /// <returns><see langword="true"/> if they are that close.</returns>
    bool IsCloseTo(TSelf other, float tolerance = 1e-4f);
}

/// <summary>The shared math of the color values, in double precision: sRGB, XYZ D65, Lab D50 and Oklab.</summary>
internal static class ColorValueMath
{
    /// <summary>Linear sRGB to XYZ D65, from the BT.709 primaries (CSS Color 4).</summary>
    public static readonly Matrix3d RgbToXyz = ColorProfile.Srgb.ToXyz();

    public static readonly Matrix3d XyzToRgb = RgbToXyz.Inverse();

    /// <summary>Bradford from D65 to D50, as CSS Color 4 adapts.</summary>
    public static readonly Matrix3d D65ToD50 = Matrix3d.Bradford(Chromaticity.D65.ToXyz(), Chromaticity.D50.ToXyz());

    public static readonly Matrix3d D50ToD65 = D65ToD50.Inverse();

    /// <summary>The D50 white Lab is relative to, from its chromaticity (CSS Color 4).</summary>
    public static readonly Vector3d D50 = Chromaticity.D50.ToXyz();

    // Ottosson's matrices: linear sRGB to LMS, and the cube roots of LMS to Lab.
    private static readonly Matrix3d RgbToLms = new(0.4122214708, 0.5363325363, 0.0514459929, 0.2119034982, 0.6806995451, 0.1073969566, 0.0883024619, 0.2817188376, 0.6299787005);
    private static readonly Matrix3d LmsToOklab = new(0.2104542553, 0.7936177850, -0.0040720468, 1.9779984951, -2.4285922050, 0.4505937099, 0.0259040371, 0.7827717662, -0.8086757660);
    private static readonly Matrix3d XyzToLms = RgbToLms * XyzToRgb;
    private static readonly Matrix3d LmsToXyz = XyzToLms.Inverse();
    private static readonly Matrix3d OklabToLms = LmsToOklab.Inverse();

    public static double Decode(double encoded) => TransferFunction.Srgb.ToLinear(encoded);

    public static double Encode(double linear) => TransferFunction.Srgb.FromLinear(linear);

    public static Vector3d XyzFromSrgb(double r, double g, double b) => RgbToXyz * new Vector3d(Decode(r), Decode(g), Decode(b));

    /// <summary>Returns encoded sRGB, unclamped (the curve is mirrored below 0 and extended above 1).</summary>
    public static Vector3d SrgbFromXyz(Vector3d xyz)
    {
        Vector3d linear = XyzToRgb * xyz;
        return new Vector3d(Encode(linear.X), Encode(linear.Y), Encode(linear.Z));
    }

    public static Vector3d Oklab(Vector3d xyz)
    {
        Vector3d lms = XyzToLms * xyz;
        return LmsToOklab * new Vector3d(Math.Cbrt(lms.X), Math.Cbrt(lms.Y), Math.Cbrt(lms.Z));
    }

    public static Vector3d XyzFromOklab(Vector3d lab)
    {
        Vector3d c = OklabToLms * lab;
        return LmsToXyz * new Vector3d(c.X * c.X * c.X, c.Y * c.Y * c.Y, c.Z * c.Z * c.Z);
    }

    /// <summary>Returns polar (lightness, chroma, hue in degrees 0..360) from Cartesian (lightness, a, b).</summary>
    public static (double L, double C, double H) Polar(double l, double a, double b)
    {
        double h = Math.Atan2(b, a) * (180 / Math.PI);
        return (l, Math.Sqrt((a * a) + (b * b)), h < 0 ? h + 360 : h);
    }

    public static (double L, double A, double B) Cartesian(double l, double c, double h)
    {
        double r = h * (Math.PI / 180);
        return (l, c * Math.Cos(r), c * Math.Sin(r));
    }

    public static Rgba8 ToRgba8(Vector3d srgb) => new(Byte(srgb.X), Byte(srgb.Y), Byte(srgb.Z), 255);

    public static RgbaF32 ToRgbaF32(Vector3d srgb) => new((float)srgb.X, (float)srgb.Y, (float)srgb.Z, 1);

    public static bool Close(float a, float b, float tolerance) => Math.Abs(a - b) <= tolerance;

    private static byte Byte(double v) => v > 0 ? v < 1 ? (byte)((v * 255) + 0.5) : (byte)255 : (byte)0;
}
