using Egami.Pixels;

namespace Egami.Colors;

/// <summary>A CIE LCh color: the polar form of <see cref="LabColor"/> (D50), with chroma and hue instead of a and b.</summary>
/// <param name="L">Lightness, 0 to 100.</param>
/// <param name="C">Chroma: 0 for gray, rising with colorfulness.</param>
/// <param name="H">Hue in degrees, 0 to 360; 0 for grays.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ColorManagement.cs" region="color-values"/>
/// </example>
public readonly record struct LchColor(float L, float C, float H) : IColorValue<LchColor>
{
    /// <inheritdoc/>
    public static LchColor FromXyz(XyzColor xyz) => FromLab(LabColor.FromXyz(xyz));

    /// <inheritdoc/>
    public static LchColor From(Rgba8 color) => FromXyz(XyzColor.From(color));

    /// <inheritdoc/>
    public static LchColor From(RgbaF32 color) => FromXyz(XyzColor.From(color));

    /// <summary>Returns the polar form of a Lab color.</summary>
    /// <param name="lab">The color.</param>
    /// <returns>The same color as LCh.</returns>
    public static LchColor FromLab(LabColor lab)
    {
        (double l, double c, double h) = ColorValueMath.Polar(lab.L, lab.A, lab.B);
        return new((float)l, (float)c, (float)h);
    }

    /// <summary>Returns the Cartesian form.</summary>
    /// <returns>The same color as Lab.</returns>
    public LabColor ToLab()
    {
        (double l, double a, double b) = ColorValueMath.Cartesian(L, C, H);
        return new((float)l, (float)a, (float)b);
    }

    /// <inheritdoc/>
    public XyzColor ToXyz() => ToLab().ToXyz();

    /// <inheritdoc/>
    public Rgba8 ToRgba8() => ToXyz().ToRgba8();

    /// <inheritdoc/>
    public RgbaF32 ToRgbaF32() => ToXyz().ToRgbaF32();

    /// <inheritdoc/>
    public bool IsCloseTo(LchColor other, float tolerance = 1e-4f) =>
        ColorValueMath.Close(L, other.L, tolerance) && ColorValueMath.Close(C, other.C, tolerance) && (ColorValueMath.Close(H, other.H, tolerance) || Math.Abs(Math.Abs(H - other.H) - 360) <= tolerance);
}
