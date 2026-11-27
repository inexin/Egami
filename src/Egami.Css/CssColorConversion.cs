namespace Egami.Css;

/// <summary>
/// Conversions between the CSS Color 4 color spaces, written from the equations, primaries and white points in CSS Color 4
/// and the Oklab definition. Every space reaches CIE XYZ (D65); the inverse matrices are computed from the forward ones,
/// so a round trip through any two spaces returns the components it started with.
/// </summary>
internal static class CssColorConversion
{
    // Linear-light RGB to XYZ, per space; XYZ (D65) to linear sRGB.
    private static readonly double[] s_xyzToLinearSrgb =
        [3.2409699419045226, -1.537383177570094, -0.4986107602930034, -0.9692436362808796, 1.8759675015077202, 0.04155505740717559, 0.05563007969699366, -0.20397695888897652, 1.0569715142428786];

    private static readonly double[] s_displayP3ToXyz =
        [0.4865709486482162, 0.26566769316909306, 0.1982172852343625, 0.2289745640697488, 0.6917385218365064, 0.079286914093745, 0, 0.04511338185890264, 1.043944368900976];

    private static readonly double[] s_a98ToXyz =
        [0.5766690429101305, 0.1855582379065463, 0.1882286462349947, 0.29734497525053605, 0.6273635662554661, 0.07529145849399788, 0.02703136138641234, 0.07068885253582723, 0.9913375368376388];

    // To XYZ relative to D50.
    private static readonly double[] s_proPhotoToXyzD50 =
        [0.7977604896723027, 0.13518583717574031, 0.0313493495815248, 0.2880711282292934, 0.7118432178101014, 0.00008565396060525902, 0, 0, 0.8251046025104601];

    private static readonly double[] s_rec2020ToXyz =
        [0.6369580483012914, 0.14461690358620832, 0.1688809751641721, 0.2627002120112671, 0.6779980715188708, 0.05930171646986196, 0, 0.028072693049087428, 1.060985057710791];

    // Bradford chromatic adaptation from D50 to D65.
    private static readonly double[] s_d50ToD65 =
        [0.955473421488075, -0.02309845494876471, 0.06325924320057072, -0.0283697093338637, 1.0099953980813041, 0.021041441191917323, 0.012314014864481998, -0.020507649298898964, 1.330365926242124];

    // Oklab: L, a, b to the cube roots of LMS, and LMS to linear sRGB.
    private static readonly double[] s_oklabToLmsRoot =
        [1, 0.3963377773761749, 0.2158037573099136, 1, -0.1055613458156586, -0.0638541728258133, 1, -0.0894841775298119, -1.2914855480194092];

    private static readonly double[] s_lmsToLinearSrgb =
        [4.0767416621, -3.3077115913, 0.2309699292, -1.2684380046, 2.6097574011, -0.3413193965, -0.0041960863, -0.7034186147, 1.7076147010];

    private static readonly double[] s_linearSrgbToXyz = Invert(s_xyzToLinearSrgb);
    private static readonly double[] s_xyzToDisplayP3 = Invert(s_displayP3ToXyz);
    private static readonly double[] s_xyzToA98 = Invert(s_a98ToXyz);
    private static readonly double[] s_xyzD50ToProPhoto = Invert(s_proPhotoToXyzD50);
    private static readonly double[] s_xyzToRec2020 = Invert(s_rec2020ToXyz);
    private static readonly double[] s_d65ToD50 = Invert(s_d50ToD65);
    private static readonly double[] s_lmsRootToOklab = Invert(s_oklabToLmsRoot);
    private static readonly double[] s_linearSrgbToLms = Invert(s_lmsToLinearSrgb);

    // The D50 white point's X and Z (Y = 1), from its chromaticity 0.3457, 0.3585.
    private const double D50X = 0.3457 / 0.3585;
    private const double D50Z = (1.0 - 0.3457 - 0.3585) / 0.3585;
    private const double Kappa = 24389.0 / 27;
    private const double Epsilon = 216.0 / 24389;

    /// <summary>Converts components to gamma-encoded sRGB, unclipped.</summary>
    public static (double R, double G, double B) ToSrgb(CssColorSpace space, double c0, double c1, double c2)
    {
        switch (space)
        {
            case CssColorSpace.Srgb:
                return (c0, c1, c2);
            case CssColorSpace.SrgbLinear:
                return (Encode(c0), Encode(c1), Encode(c2));
            case CssColorSpace.Oklab:
                return LinearToSrgb(OklabToLinearSrgb(c0, c1, c2));
            case CssColorSpace.Oklch:
                {
                    (double a, double b) = FromPolar(c1, c2);
                    return LinearToSrgb(OklabToLinearSrgb(c0, a, b));
                }

            default:
                {
                    (double x, double y, double z) = ToXyzD65(space, c0, c1, c2);
                    return LinearToSrgb(Multiply(s_xyzToLinearSrgb, x, y, z));
                }
        }
    }

    /// <summary>Converts components from one space to another, unclipped.</summary>
    public static (double C0, double C1, double C2) Convert(CssColorSpace from, double c0, double c1, double c2, CssColorSpace to)
    {
        if (from == to)
        {
            return (c0, c1, c2);
        }

        if (to == CssColorSpace.Srgb)
        {
            return ToSrgb(from, c0, c1, c2);
        }

        // The polar forms convert to and from their rectangular ones directly.
        if ((from, to) is (CssColorSpace.Lab, CssColorSpace.Lch) or (CssColorSpace.Oklab, CssColorSpace.Oklch))
        {
            (double chroma, double hue) = ToPolar(c1, c2);
            return (c0, chroma, hue);
        }

        if ((from, to) is (CssColorSpace.Lch, CssColorSpace.Lab) or (CssColorSpace.Oklch, CssColorSpace.Oklab))
        {
            (double a, double b) = FromPolar(c1, c2);
            return (c0, a, b);
        }

        // Oklab is defined on linear sRGB: go there directly rather than through XYZ.
        if (to is CssColorSpace.Oklab or CssColorSpace.Oklch && from is CssColorSpace.Srgb or CssColorSpace.SrgbLinear)
        {
            (double r, double g, double b) = from == CssColorSpace.Srgb ? (Decode(c0), Decode(c1), Decode(c2)) : (c0, c1, c2);
            return FromLinearSrgb(to, r, g, b);
        }

        (double x, double y, double z) = ToXyzD65(from, c0, c1, c2);
        return FromXyzD65(to, x, y, z);
    }

    private static (double, double, double) FromLinearSrgb(CssColorSpace to, double r, double g, double b)
    {
        (double l, double a, double bb) = LinearSrgbToOklab(r, g, b);
        if (to == CssColorSpace.Oklab)
        {
            return (l, a, bb);
        }

        (double chroma, double hue) = ToPolar(a, bb);
        return (l, chroma, hue);
    }

    /// <summary>Converts components to CIE XYZ relative to D65.</summary>
    public static (double X, double Y, double Z) ToXyzD65(CssColorSpace space, double c0, double c1, double c2)
    {
        switch (space)
        {
            case CssColorSpace.Srgb:
                return Multiply(s_linearSrgbToXyz, Decode(c0), Decode(c1), Decode(c2));
            case CssColorSpace.SrgbLinear:
                return Multiply(s_linearSrgbToXyz, c0, c1, c2);
            case CssColorSpace.DisplayP3:
                return Multiply(s_displayP3ToXyz, Decode(c0), Decode(c1), Decode(c2));
            case CssColorSpace.A98Rgb:
                return Multiply(s_a98ToXyz, A98Decode(c0), A98Decode(c1), A98Decode(c2));
            case CssColorSpace.ProPhotoRgb:
                {
                    (double x, double y, double z) = Multiply(s_proPhotoToXyzD50, ProPhotoDecode(c0), ProPhotoDecode(c1), ProPhotoDecode(c2));
                    return Multiply(s_d50ToD65, x, y, z);
                }

            case CssColorSpace.Rec2020:
                return Multiply(s_rec2020ToXyz, Rec2020Decode(c0), Rec2020Decode(c1), Rec2020Decode(c2));
            case CssColorSpace.XyzD50:
                return Multiply(s_d50ToD65, c0, c1, c2);
            case CssColorSpace.XyzD65:
                return (c0, c1, c2);
            case CssColorSpace.Lab:
                {
                    (double x, double y, double z) = LabToXyzD50(c0, c1, c2);
                    return Multiply(s_d50ToD65, x, y, z);
                }

            case CssColorSpace.Lch:
                {
                    (double a, double b) = FromPolar(c1, c2);
                    (double x, double y, double z) = LabToXyzD50(c0, a, b);
                    return Multiply(s_d50ToD65, x, y, z);
                }

            case CssColorSpace.Oklab:
                {
                    (double r, double g, double b) = OklabToLinearSrgb(c0, c1, c2);
                    return Multiply(s_linearSrgbToXyz, r, g, b);
                }

            case CssColorSpace.Oklch:
                {
                    (double a, double b) = FromPolar(c1, c2);
                    (double r, double g, double bl) = OklabToLinearSrgb(c0, a, b);
                    return Multiply(s_linearSrgbToXyz, r, g, bl);
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(space));
        }
    }

    /// <summary>Converts CIE XYZ relative to D65 to components of a space.</summary>
    public static (double C0, double C1, double C2) FromXyzD65(CssColorSpace space, double x, double y, double z)
    {
        switch (space)
        {
            case CssColorSpace.Srgb:
                return LinearToSrgb(Multiply(s_xyzToLinearSrgb, x, y, z));
            case CssColorSpace.SrgbLinear:
                return Multiply(s_xyzToLinearSrgb, x, y, z);
            case CssColorSpace.DisplayP3:
                {
                    (double r, double g, double b) = Multiply(s_xyzToDisplayP3, x, y, z);
                    return (Encode(r), Encode(g), Encode(b));
                }

            case CssColorSpace.A98Rgb:
                {
                    (double r, double g, double b) = Multiply(s_xyzToA98, x, y, z);
                    return (A98Encode(r), A98Encode(g), A98Encode(b));
                }

            case CssColorSpace.ProPhotoRgb:
                {
                    (double x50, double y50, double z50) = Multiply(s_d65ToD50, x, y, z);
                    (double r, double g, double b) = Multiply(s_xyzD50ToProPhoto, x50, y50, z50);
                    return (ProPhotoEncode(r), ProPhotoEncode(g), ProPhotoEncode(b));
                }

            case CssColorSpace.Rec2020:
                {
                    (double r, double g, double b) = Multiply(s_xyzToRec2020, x, y, z);
                    return (Rec2020Encode(r), Rec2020Encode(g), Rec2020Encode(b));
                }

            case CssColorSpace.XyzD50:
                return Multiply(s_d65ToD50, x, y, z);
            case CssColorSpace.XyzD65:
                return (x, y, z);
            case CssColorSpace.Lab or CssColorSpace.Lch:
                {
                    (double x50, double y50, double z50) = Multiply(s_d65ToD50, x, y, z);
                    (double l, double a, double b) = XyzD50ToLab(x50, y50, z50);
                    if (space == CssColorSpace.Lab)
                    {
                        return (l, a, b);
                    }

                    (double chroma, double hue) = ToPolar(a, b);
                    return (l, chroma, hue);
                }

            case CssColorSpace.Oklab or CssColorSpace.Oklch:
                {
                    (double r, double g, double b) = Multiply(s_xyzToLinearSrgb, x, y, z);
                    return FromLinearSrgb(space, r, g, b);
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(space));
        }
    }

    /// <summary>Converts gamma-encoded sRGB to Oklab.</summary>
    public static (double L, double A, double B) SrgbToOklab(double r, double g, double b) => LinearSrgbToOklab(Decode(r), Decode(g), Decode(b));

    /// <summary>Gets chroma and hue (degrees, 0 to 360) from a and b.</summary>
    public static (double Chroma, double Hue) ToPolar(double a, double b)
    {
        double chroma = Math.Sqrt((a * a) + (b * b));
        double hue = Math.Atan2(b, a) * 180 / Math.PI;
        if (hue < 0)
        {
            hue += 360;
        }

        return (chroma, hue >= 360 ? 0 : hue);
    }

    /// <summary>Gets a and b from chroma and hue in degrees.</summary>
    public static (double A, double B) FromPolar(double chroma, double hue)
    {
        double radians = hue * Math.PI / 180;
        return (chroma * Math.Cos(radians), chroma * Math.Sin(radians));
    }

    /// <summary>The sRGB transfer function (linear to gamma-encoded), extended to negative values by symmetry.</summary>
    public static double Encode(double c)
    {
        double abs = Math.Abs(c);
        return abs <= 0.0031308 ? 12.92 * c : double.CopySign((1.055 * Math.Pow(abs, 1 / 2.4)) - 0.055, c);
    }

    /// <summary>The inverse sRGB transfer function (gamma-encoded to linear), extended to negative values by symmetry.</summary>
    public static double Decode(double c)
    {
        double abs = Math.Abs(c);
        return abs <= 0.04045 ? c / 12.92 : double.CopySign(Math.Pow((abs + 0.055) / 1.055, 2.4), c);
    }

    public static (double, double, double) LabToXyzD50(double l, double a, double b)
    {
        double f1 = (l + 16) / 116;
        double f0 = (a / 500) + f1;
        double f2 = f1 - (b / 200);
        double x = (f0 * f0 * f0) > Epsilon ? f0 * f0 * f0 : ((116 * f0) - 16) / Kappa;
        double y = l > Kappa * Epsilon ? f1 * f1 * f1 : l / Kappa;
        double z = (f2 * f2 * f2) > Epsilon ? f2 * f2 * f2 : ((116 * f2) - 16) / Kappa;
        return (x * D50X, y, z * D50Z);
    }

    private static (double, double, double) XyzD50ToLab(double x, double y, double z)
    {
        double f0 = F(x / D50X);
        double f1 = F(y);
        double f2 = F(z / D50Z);
        return ((116 * f1) - 16, 500 * (f0 - f1), 200 * (f1 - f2));

        static double F(double t) => t > Epsilon ? Math.Cbrt(t) : ((Kappa * t) + 16) / 116;
    }

    private static (double, double, double) OklabToLinearSrgb(double l, double a, double b)
    {
        double lp = l + (s_oklabToLmsRoot[1] * a) + (s_oklabToLmsRoot[2] * b);
        double mp = l + (s_oklabToLmsRoot[4] * a) + (s_oklabToLmsRoot[5] * b);
        double sp = l + (s_oklabToLmsRoot[7] * a) + (s_oklabToLmsRoot[8] * b);
        return Multiply(s_lmsToLinearSrgb, lp * lp * lp, mp * mp * mp, sp * sp * sp);
    }

    private static (double, double, double) LinearSrgbToOklab(double r, double g, double b)
    {
        (double l, double m, double s) = Multiply(s_linearSrgbToLms, r, g, b);
        return Multiply(s_lmsRootToOklab, Math.Cbrt(l), Math.Cbrt(m), Math.Cbrt(s));
    }

    private static (double, double, double) LinearToSrgb((double R, double G, double B) linear) => (Encode(linear.R), Encode(linear.G), Encode(linear.B));

    private static double A98Decode(double c) => double.CopySign(Math.Pow(Math.Abs(c), 563.0 / 256), c);

    private static double A98Encode(double c) => double.CopySign(Math.Pow(Math.Abs(c), 256.0 / 563), c);

    private static double ProPhotoDecode(double c) => Math.Abs(c) <= 16.0 / 512 ? c / 16 : double.CopySign(Math.Pow(Math.Abs(c), 1.8), c);

    private static double ProPhotoEncode(double c) => Math.Abs(c) < 1.0 / 512 ? c * 16 : double.CopySign(Math.Pow(Math.Abs(c), 1 / 1.8), c);

    private const double Rec2020Alpha = 1.09929682680944;
    private const double Rec2020Beta = 0.018053968510807;

    private static double Rec2020Decode(double c)
    {
        return Math.Abs(c) < Rec2020Beta * 4.5 ? c / 4.5 : double.CopySign(Math.Pow((Math.Abs(c) + Rec2020Alpha - 1) / Rec2020Alpha, 1 / 0.45), c);
    }

    private static double Rec2020Encode(double c)
    {
        return Math.Abs(c) < Rec2020Beta ? c * 4.5 : double.CopySign((Rec2020Alpha * Math.Pow(Math.Abs(c), 0.45)) - (Rec2020Alpha - 1), c);
    }

    private static (double, double, double) Multiply(double[] m, double a, double b, double c)
    {
        return ((m[0] * a) + (m[1] * b) + (m[2] * c), (m[3] * a) + (m[4] * b) + (m[5] * c), (m[6] * a) + (m[7] * b) + (m[8] * c));
    }

    // The inverse of a 3×3 matrix (row-major), from its adjugate.
    private static double[] Invert(double[] m)
    {
        double c00 = (m[4] * m[8]) - (m[5] * m[7]);
        double c01 = (m[5] * m[6]) - (m[3] * m[8]);
        double c02 = (m[3] * m[7]) - (m[4] * m[6]);
        double determinant = (m[0] * c00) + (m[1] * c01) + (m[2] * c02);
        double inverse = 1 / determinant;
        return
        [
            c00 * inverse, ((m[2] * m[7]) - (m[1] * m[8])) * inverse, ((m[1] * m[5]) - (m[2] * m[4])) * inverse,
            c01 * inverse, ((m[0] * m[8]) - (m[2] * m[6])) * inverse, ((m[2] * m[3]) - (m[0] * m[5])) * inverse,
            c02 * inverse, ((m[1] * m[6]) - (m[0] * m[7])) * inverse, ((m[0] * m[4]) - (m[1] * m[3])) * inverse,
        ];
    }
}
