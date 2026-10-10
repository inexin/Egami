using Egami.Pixels;

namespace Egami.Colors;

/// <summary>
/// An Oklab color (Björn Ottosson, 2020): a perceptual space built on linear sRGB, where equal distances look about equally
/// different and hues stay put when lightness or chroma changes. CSS Color 4's oklab().
/// </summary>
/// <param name="L">Lightness, 0 for black to 1 for white.</param>
/// <param name="A">Green (negative) to red (positive), about −0.4 to 0.4.</param>
/// <param name="B">Blue (negative) to yellow (positive), about −0.4 to 0.4.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ColorManagement.cs" region="color-values"/>
/// </example>
public readonly record struct OklabColor(float L, float A, float B) : IColorValue<OklabColor>
{
    /// <inheritdoc/>
    public static OklabColor FromXyz(XyzColor xyz) => Of(ColorValueMath.Oklab(xyz.Vector));

    /// <inheritdoc/>
    public static OklabColor From(Rgba8 color) => FromXyz(XyzColor.From(color));

    /// <inheritdoc/>
    public static OklabColor From(RgbaF32 color) => FromXyz(XyzColor.From(color));

    /// <inheritdoc/>
    public XyzColor ToXyz() => XyzColor.Of(ColorValueMath.XyzFromOklab(new Vector3d(L, A, B)));

    /// <inheritdoc/>
    public Rgba8 ToRgba8() => ToXyz().ToRgba8();

    /// <inheritdoc/>
    public RgbaF32 ToRgbaF32() => ToXyz().ToRgbaF32();

    /// <summary>Returns the Euclidean distance in Oklab, its color difference: about 0.02 is just noticeable.</summary>
    /// <param name="other">The other color.</param>
    /// <returns>The distance.</returns>
    public double Distance(OklabColor other)
    {
        double l = L - other.L, a = A - other.A, b = B - other.B;
        return Math.Sqrt((l * l) + (a * a) + (b * b));
    }

    /// <inheritdoc/>
    public bool IsCloseTo(OklabColor other, float tolerance = 1e-4f) =>
        ColorValueMath.Close(L, other.L, tolerance) && ColorValueMath.Close(A, other.A, tolerance) && ColorValueMath.Close(B, other.B, tolerance);

    internal static OklabColor Of(Vector3d v) => new((float)v.X, (float)v.Y, (float)v.Z);
}

/// <summary>An Oklch color: the polar form of <see cref="OklabColor"/>, CSS Color 4's oklch().</summary>
/// <param name="L">Lightness, 0 to 1.</param>
/// <param name="C">Chroma: 0 for gray, up to about 0.37 for sRGB colors.</param>
/// <param name="H">Hue in degrees, 0 to 360; 0 for grays.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ColorManagement.cs" region="color-values"/>
/// </example>
public readonly record struct OklchColor(float L, float C, float H) : IColorValue<OklchColor>
{
    /// <inheritdoc/>
    public static OklchColor FromXyz(XyzColor xyz) => FromOklab(OklabColor.FromXyz(xyz));

    /// <inheritdoc/>
    public static OklchColor From(Rgba8 color) => FromXyz(XyzColor.From(color));

    /// <inheritdoc/>
    public static OklchColor From(RgbaF32 color) => FromXyz(XyzColor.From(color));

    /// <summary>Returns the polar form of an Oklab color.</summary>
    /// <param name="lab">The color.</param>
    /// <returns>The same color as Oklch.</returns>
    public static OklchColor FromOklab(OklabColor lab)
    {
        (double l, double c, double h) = ColorValueMath.Polar(lab.L, lab.A, lab.B);
        return new((float)l, (float)c, (float)h);
    }

    /// <summary>Returns the Cartesian form.</summary>
    /// <returns>The same color as Oklab.</returns>
    public OklabColor ToOklab()
    {
        (double l, double a, double b) = ColorValueMath.Cartesian(L, C, H);
        return new((float)l, (float)a, (float)b);
    }

    /// <inheritdoc/>
    public XyzColor ToXyz() => ToOklab().ToXyz();

    /// <inheritdoc/>
    public Rgba8 ToRgba8() => ToXyz().ToRgba8();

    /// <inheritdoc/>
    public RgbaF32 ToRgbaF32() => ToXyz().ToRgbaF32();

    /// <inheritdoc/>
    public bool IsCloseTo(OklchColor other, float tolerance = 1e-4f) =>
        ColorValueMath.Close(L, other.L, tolerance) && ColorValueMath.Close(C, other.C, tolerance) && (ColorValueMath.Close(H, other.H, tolerance) || Math.Abs(Math.Abs(H - other.H) - 360) <= tolerance);
}
