using Egami.Pixels;

namespace Egami.Colors;

/// <summary>An HSL color: hue, saturation and lightness of encoded sRGB, as CSS hsl() defines them.</summary>
/// <param name="H">Hue in degrees, 0 to 360; 0 for grays.</param>
/// <param name="S">Saturation, 0 to 1.</param>
/// <param name="L">Lightness, 0 to 1.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ColorManagement.cs" region="color-values"/>
/// </example>
public readonly record struct HslColor(float H, float S, float L) : IColorValue<HslColor>
{
    /// <inheritdoc/>
    public static HslColor FromXyz(XyzColor xyz) => FromSrgb(ColorValueMath.SrgbFromXyz(xyz.Vector));

    /// <inheritdoc/>
    public static HslColor From(Rgba8 color) => FromSrgb(new Vector3d(color.R / 255.0, color.G / 255.0, color.B / 255.0));

    /// <inheritdoc/>
    public static HslColor From(RgbaF32 color) => FromSrgb(new Vector3d(color.R, color.G, color.B));

    /// <inheritdoc/>
    public XyzColor ToXyz()
    {
        Vector3d c = ToSrgb();
        return XyzColor.Of(ColorValueMath.XyzFromSrgb(c.X, c.Y, c.Z));
    }

    /// <inheritdoc/>
    public Rgba8 ToRgba8() => ColorValueMath.ToRgba8(ToSrgb());

    /// <inheritdoc/>
    public RgbaF32 ToRgbaF32() => ColorValueMath.ToRgbaF32(ToSrgb());

    /// <inheritdoc/>
    public bool IsCloseTo(HslColor other, float tolerance = 1e-4f) =>
        (ColorValueMath.Close(H, other.H, tolerance) || Math.Abs(Math.Abs(H - other.H) - 360) <= tolerance) && ColorValueMath.Close(S, other.S, tolerance) && ColorValueMath.Close(L, other.L, tolerance);

    internal static HslColor FromSrgb(Vector3d c)
    {
        double max = Math.Max(c.X, Math.Max(c.Y, c.Z)), min = Math.Min(c.X, Math.Min(c.Y, c.Z));
        double l = (max + min) / 2, d = max - min;
        double s = d == 0 || l <= 0 || l >= 1 ? 0 : d / (1 - Math.Abs((2 * l) - 1));
        return new HslColor((float)HueOf(c, max, d), (float)s, (float)l);
    }

    // The hue of a color with largest channel max and spread d, in degrees 0..360.
    internal static double HueOf(Vector3d c, double max, double d)
    {
        if (d == 0)
        {
            return 0;
        }

        double h = max == c.X ? ((c.Y - c.Z) / d) + (c.Y < c.Z ? 6 : 0) : max == c.Y ? ((c.Z - c.X) / d) + 2 : ((c.X - c.Y) / d) + 4;
        return h * 60;
    }

    // CSS Color 4: f(n) = L − a · max(−1, min(k − 3, 9 − k, 1)), k = (n + H / 30) mod 12, a = S · min(L, 1 − L).
    internal Vector3d ToSrgb()
    {
        double h = ((H % 360) + 360) % 360, l = L, a = S * Math.Min(L, 1 - L);
        return new Vector3d(F(0, h, l, a), F(8, h, l, a), F(4, h, l, a));

        static double F(double n, double h, double l, double a)
        {
            double k = (n + (h / 30)) % 12;
            return l - (a * Math.Max(-1, Math.Min(Math.Min(k - 3, 9 - k), 1)));
        }
    }
}

/// <summary>An HSV (HSB) color: hue, saturation and value of encoded sRGB, as color pickers use them.</summary>
/// <param name="H">Hue in degrees, 0 to 360; 0 for grays.</param>
/// <param name="S">Saturation, 0 to 1.</param>
/// <param name="V">Value (brightness), 0 to 1.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ColorManagement.cs" region="color-values"/>
/// </example>
public readonly record struct HsvColor(float H, float S, float V) : IColorValue<HsvColor>
{
    /// <inheritdoc/>
    public static HsvColor FromXyz(XyzColor xyz) => FromSrgb(ColorValueMath.SrgbFromXyz(xyz.Vector));

    /// <inheritdoc/>
    public static HsvColor From(Rgba8 color) => FromSrgb(new Vector3d(color.R / 255.0, color.G / 255.0, color.B / 255.0));

    /// <inheritdoc/>
    public static HsvColor From(RgbaF32 color) => FromSrgb(new Vector3d(color.R, color.G, color.B));

    /// <inheritdoc/>
    public XyzColor ToXyz()
    {
        Vector3d c = ToSrgb();
        return XyzColor.Of(ColorValueMath.XyzFromSrgb(c.X, c.Y, c.Z));
    }

    /// <inheritdoc/>
    public Rgba8 ToRgba8() => ColorValueMath.ToRgba8(ToSrgb());

    /// <inheritdoc/>
    public RgbaF32 ToRgbaF32() => ColorValueMath.ToRgbaF32(ToSrgb());

    /// <inheritdoc/>
    public bool IsCloseTo(HsvColor other, float tolerance = 1e-4f) =>
        (ColorValueMath.Close(H, other.H, tolerance) || Math.Abs(Math.Abs(H - other.H) - 360) <= tolerance) && ColorValueMath.Close(S, other.S, tolerance) && ColorValueMath.Close(V, other.V, tolerance);

    internal static HsvColor FromSrgb(Vector3d c)
    {
        double max = Math.Max(c.X, Math.Max(c.Y, c.Z)), min = Math.Min(c.X, Math.Min(c.Y, c.Z));
        double d = max - min;
        return new HsvColor((float)HslColor.HueOf(c, max, d), (float)(max <= 0 ? 0 : d / max), (float)max);
    }

    // f(n) = V − V · S · max(0, min(k, 4 − k, 1)), k = (n + H / 60) mod 6.
    internal Vector3d ToSrgb()
    {
        double h = ((H % 360) + 360) % 360;
        return new Vector3d(F(5, h, V, S), F(3, h, V, S), F(1, h, V, S));

        static double F(double n, double h, double v, double s)
        {
            double k = (n + (h / 60)) % 6;
            return v - (v * s * Math.Max(0, Math.Min(Math.Min(k, 4 - k), 1)));
        }
    }
}
