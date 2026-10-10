using Egami.Pixels;

namespace Egami.Colors;

/// <summary>A CIE L*a*b* color relative to D50, as ICC and CSS Color 4 define Lab.</summary>
/// <param name="L">Lightness, 0 for black to 100 for white.</param>
/// <param name="A">Green (negative) to red (positive), about −128 to 127 for real colors.</param>
/// <param name="B">Blue (negative) to yellow (positive), about −128 to 127 for real colors.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ColorManagement.cs" region="color-values"/>
/// </example>
public readonly record struct LabColor(float L, float A, float B) : IColorValue<LabColor>
{
    /// <inheritdoc/>
    public static LabColor FromXyz(XyzColor xyz) => Of(Cie.XyzToLab(ColorValueMath.D65ToD50 * xyz.Vector, ColorValueMath.D50));

    /// <inheritdoc/>
    public static LabColor From(Rgba8 color) => FromXyz(XyzColor.From(color));

    /// <inheritdoc/>
    public static LabColor From(RgbaF32 color) => FromXyz(XyzColor.From(color));

    /// <inheritdoc/>
    public XyzColor ToXyz() => XyzColor.Of(ColorValueMath.D50ToD65 * Cie.LabToXyz(new Vector3d(L, A, B), ColorValueMath.D50));

    /// <inheritdoc/>
    public Rgba8 ToRgba8() => ToXyz().ToRgba8();

    /// <inheritdoc/>
    public RgbaF32 ToRgbaF32() => ToXyz().ToRgbaF32();

    /// <summary>
    /// Returns the CIEDE2000 color difference (CIE 142-2001), the industry's measure of how different two colors look: under 1
    /// is barely visible, 2 is noticeable side by side.
    /// </summary>
    /// <param name="other">The other color.</param>
    /// <returns>ΔE₀₀, 0 or more.</returns>
    public double DeltaE2000(LabColor other)
    {
        double l1 = L, a1 = A, b1 = B, l2 = other.L, a2 = other.A, b2 = other.B;
        double c1 = Math.Sqrt((a1 * a1) + (b1 * b1)), c2 = Math.Sqrt((a2 * a2) + (b2 * b2));
        double cMean7 = Math.Pow((c1 + c2) / 2, 7);
        double g = 0.5 * (1 - Math.Sqrt(cMean7 / (cMean7 + 6103515625.0)));   // 25^7
        double ap1 = (1 + g) * a1, ap2 = (1 + g) * a2;
        double cp1 = Math.Sqrt((ap1 * ap1) + (b1 * b1)), cp2 = Math.Sqrt((ap2 * ap2) + (b2 * b2));
        double hp1 = Hue(b1, ap1), hp2 = Hue(b2, ap2);

        double dL = l2 - l1, dC = cp2 - cp1;
        double dh = cp1 * cp2 == 0 ? 0 : hp2 - hp1;
        if (dh > 180)
        {
            dh -= 360;
        }
        else if (dh < -180)
        {
            dh += 360;
        }

        double dH = 2 * Math.Sqrt(cp1 * cp2) * Math.Sin(Radians(dh / 2));
        double lMean = (l1 + l2) / 2, cMean = (cp1 + cp2) / 2;
        double hMean = hp1 + hp2;
        if (cp1 * cp2 != 0)
        {
            hMean = Math.Abs(hp1 - hp2) <= 180 ? hMean / 2 : (hp1 + hp2 < 360 ? (hMean + 360) / 2 : (hMean - 360) / 2);
        }

        double t = 1 - (0.17 * Math.Cos(Radians(hMean - 30))) + (0.24 * Math.Cos(Radians(2 * hMean))) + (0.32 * Math.Cos(Radians((3 * hMean) + 6))) - (0.20 * Math.Cos(Radians((4 * hMean) - 63)));
        double dTheta = 30 * Math.Exp(-Math.Pow((hMean - 275) / 25, 2));
        double cMeanP7 = Math.Pow(cMean, 7);
        double rc = 2 * Math.Sqrt(cMeanP7 / (cMeanP7 + 6103515625.0));
        double l50 = (lMean - 50) * (lMean - 50);
        double sl = 1 + (0.015 * l50 / Math.Sqrt(20 + l50));
        double sc = 1 + (0.045 * cMean);
        double sh = 1 + (0.015 * cMean * t);
        double rt = -Math.Sin(Radians(2 * dTheta)) * rc;
        double x = dL / sl, y = dC / sc, z = dH / sh;
        return Math.Sqrt((x * x) + (y * y) + (z * z) + (rt * y * z));

        static double Hue(double b, double a)
        {
            if (a == 0 && b == 0)
            {
                return 0;
            }

            double h = Math.Atan2(b, a) * (180 / Math.PI);
            return h < 0 ? h + 360 : h;
        }

        static double Radians(double degrees) => degrees * (Math.PI / 180);
    }

    /// <inheritdoc/>
    public bool IsCloseTo(LabColor other, float tolerance = 1e-4f) =>
        ColorValueMath.Close(L, other.L, tolerance) && ColorValueMath.Close(A, other.A, tolerance) && ColorValueMath.Close(B, other.B, tolerance);

    internal static LabColor Of(Vector3d v) => new((float)v.X, (float)v.Y, (float)v.Z);
}
