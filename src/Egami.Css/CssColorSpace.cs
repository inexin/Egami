namespace Egami.Css;

/// <summary>
/// The color spaces of CSS Color 4 that a <see cref="CssColor"/> keeps its components in: the space its text was written in.
/// </summary>
/// <remarks>
/// Hex colors, named and system colors, <c>rgb()</c>, <c>hsl()</c> and <c>hwb()</c> are all <see cref="Srgb"/>, as CSS Color 4
/// defines them. The components of each space are the ones its CSS function takes, as numbers: 0 to 1 for the RGB spaces
/// (beyond for colors outside their gamut), X, Y and Z with Y = 1 for white, Lab lightness from 0 to 100, Oklab lightness from
/// 0 to 1, and hues in degrees.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Css.cs" region="widegamut"/>
/// </example>
public enum CssColorSpace
{
    /// <summary>sRGB, gamma-encoded: red, green, blue. <c>color(srgb ...)</c> and every legacy syntax.</summary>
    Srgb,

    /// <summary>Linear-light sRGB: red, green, blue. <c>color(srgb-linear ...)</c>.</summary>
    SrgbLinear,

    /// <summary>Display P3, with the sRGB transfer function: red, green, blue. <c>color(display-p3 ...)</c>.</summary>
    DisplayP3,

    /// <summary>Adobe RGB (1998)-compatible: red, green, blue. <c>color(a98-rgb ...)</c>.</summary>
    A98Rgb,

    /// <summary>ProPhoto RGB, D50 white: red, green, blue. <c>color(prophoto-rgb ...)</c>.</summary>
    ProPhotoRgb,

    /// <summary>ITU-R BT.2020: red, green, blue. <c>color(rec2020 ...)</c>.</summary>
    Rec2020,

    /// <summary>CIE XYZ relative to a D50 white: X, Y, Z. <c>color(xyz-d50 ...)</c>.</summary>
    XyzD50,

    /// <summary>CIE XYZ relative to a D65 white: X, Y, Z. <c>color(xyz-d65 ...)</c> and <c>color(xyz ...)</c>.</summary>
    XyzD65,

    /// <summary>CIE Lab (D50): lightness 0 to 100, a, b. <c>lab()</c>.</summary>
    Lab,

    /// <summary>CIE LCH, the polar form of <see cref="Lab"/>: lightness, chroma, hue in degrees. <c>lch()</c>.</summary>
    Lch,

    /// <summary>Oklab: lightness 0 to 1, a, b. <c>oklab()</c>.</summary>
    Oklab,

    /// <summary>OkLCh, the polar form of <see cref="Oklab"/>: lightness, chroma, hue in degrees. <c>oklch()</c>.</summary>
    Oklch,
}
