using System.Globalization;

namespace Egami.Css;

/// <summary>CSS length units (CSS Values 4), plus <see cref="None"/> for a unitless number and <see cref="Percent"/>.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Css.cs" region="lengths"/>
/// </example>
public enum CssUnit
{
    /// <summary>A unitless number, such as SVG's user units or a line-height multiplier.</summary>
    None,

    /// <summary>A percentage of a reference length the property defines.</summary>
    Percent,

    /// <summary>CSS pixels.</summary>
    Px,

    /// <summary>Centimeters (96px / 2.54).</summary>
    Cm,

    /// <summary>Millimeters.</summary>
    Mm,

    /// <summary>Quarter-millimeters.</summary>
    Q,

    /// <summary>Inches (96px).</summary>
    In,

    /// <summary>Points (1/72 in).</summary>
    Pt,

    /// <summary>Picas (12pt).</summary>
    Pc,

    /// <summary>The element's font size.</summary>
    Em,

    /// <summary>The root element's font size.</summary>
    Rem,

    /// <summary>The font's x-height.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711", Justification = "The CSS unit's name.")]
    Ex,

    /// <summary>The root font's x-height.</summary>
    Rex,

    /// <summary>The font's cap height.</summary>
    Cap,

    /// <summary>The root font's cap height.</summary>
    Rcap,

    /// <summary>The advance of the font's "0".</summary>
    Ch,

    /// <summary>The advance of the root font's "0".</summary>
    Rch,

    /// <summary>The advance of the font's "水" (CJK water ideograph).</summary>
    Ic,

    /// <summary>The root font's <see cref="Ic"/>.</summary>
    Ric,

    /// <summary>The element's line height.</summary>
    Lh,

    /// <summary>The root element's line height.</summary>
    Rlh,

    /// <summary>1% of the viewport width.</summary>
    Vw,

    /// <summary>1% of the viewport height.</summary>
    Vh,

    /// <summary>1% of the viewport size in the inline axis.</summary>
    Vi,

    /// <summary>1% of the viewport size in the block axis.</summary>
    Vb,

    /// <summary>The smaller of <see cref="Vw"/> and <see cref="Vh"/>.</summary>
    Vmin,

    /// <summary>The larger of <see cref="Vw"/> and <see cref="Vh"/>.</summary>
    Vmax,

    /// <summary>1% of the small viewport width.</summary>
    Svw,

    /// <summary>1% of the small viewport height.</summary>
    Svh,

    /// <summary>1% of the large viewport width.</summary>
    Lvw,

    /// <summary>1% of the large viewport height.</summary>
    Lvh,

    /// <summary>1% of the dynamic viewport width.</summary>
    Dvw,

    /// <summary>1% of the dynamic viewport height.</summary>
    Dvh,
}

/// <summary>A length (or percentage, or unitless number) with its unit, such as <c>12px</c> or <c>50%</c>.</summary>
/// <param name="Value">The number.</param>
/// <param name="Unit">The unit.</param>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Css.cs" region="lengths"/>
/// </example>
public readonly record struct CssLength(double Value, CssUnit Unit)
{
    /// <summary>Gets a zero length in pixels.</summary>
    public static CssLength Zero => new(0, CssUnit.Px);

    /// <summary>Gets whether the unit is absolute (px, cm, mm, Q, in, pt, pc), so <see cref="TryToPixels(out double)"/> succeeds.</summary>
    public bool IsAbsolute => !double.IsNaN(AbsolutePixelsPerUnit(Unit));

    /// <summary>Converts an absolute length to CSS pixels.</summary>
    /// <param name="pixels">The length in pixels.</param>
    /// <returns>True when the unit is absolute; false for relative units, percentages and unitless numbers.</returns>
    public bool TryToPixels(out double pixels)
    {
        double perUnit = AbsolutePixelsPerUnit(Unit);
        pixels = Value * perUnit;
        return !double.IsNaN(perUnit);
    }

    /// <summary>Parses a length, percentage or unitless number, such as <c>1.5em</c>.</summary>
    /// <param name="text">The CSS text.</param>
    /// <param name="length">The length.</param>
    /// <returns>True when the text is one length, percentage or number.</returns>
    public static bool TryParse(string text, out CssLength length)
    {
        ArgumentNullException.ThrowIfNull(text);
        return TryParse(CssSyntax.ParseComponentValues(text, CssParseOptions.Default), out length);
    }

    /// <summary>Parses a length, percentage or unitless number from component values (whitespace around it is ignored).</summary>
    /// <param name="values">The component values.</param>
    /// <param name="length">The length.</param>
    /// <returns>True when the values are one length, percentage or number.</returns>
    public static bool TryParse(IReadOnlyList<CssComponentValue> values, out CssLength length)
    {
        ArgumentNullException.ThrowIfNull(values);
        length = default;
        CssComponentValue? single = Single(values);
        return single is not null && TryParse(single, out length);
    }

    /// <summary>Parses one component value as a length, percentage or unitless number.</summary>
    /// <param name="value">The component value.</param>
    /// <param name="length">The length.</param>
    /// <returns>True when the value is a length, percentage or number.</returns>
    public static bool TryParse(CssComponentValue value, out CssLength length)
    {
        ArgumentNullException.ThrowIfNull(value);
        length = default;
        if (value is not CssTokenValue { Token: var token })
        {
            return false;
        }

        switch (token.Kind)
        {
            case CssTokenKind.Number:
                length = new CssLength(token.Number, CssUnit.None);
                return true;
            case CssTokenKind.Percentage:
                length = new CssLength(token.Number, CssUnit.Percent);
                return true;
            case CssTokenKind.Dimension when TryParseUnit(token.Unit, out CssUnit unit):
                length = new CssLength(token.Number, unit);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Formats the length as CSS text, such as <c>12px</c>.</summary>
    /// <returns>The text.</returns>
    public override string ToString()
    {
        string number = Value.ToString("R", CultureInfo.InvariantCulture);
        return Unit switch
        {
            CssUnit.None => number,
            CssUnit.Percent => number + "%",
            CssUnit.Q => number + "Q",
            _ => number + UnitName(Unit),
        };
    }

    internal static CssComponentValue? Single(IReadOnlyList<CssComponentValue> values)
    {
        CssComponentValue? single = null;
        foreach (CssComponentValue value in values)
        {
            if (value.IsWhitespace)
            {
                continue;
            }

            if (single is not null)
            {
                return null;
            }

            single = value;
        }

        return single;
    }

    internal static string UnitName(CssUnit unit) => unit switch
    {
        CssUnit.None => "",
        CssUnit.Percent => "%",
        _ => CssText.ToAsciiLower(unit.ToString()),
    };

    internal static bool TryParseUnit(string unit, out CssUnit result)
    {
        result = CssText.ToAsciiLower(unit) switch
        {
            "px" => CssUnit.Px,
            "cm" => CssUnit.Cm,
            "mm" => CssUnit.Mm,
            "q" => CssUnit.Q,
            "in" => CssUnit.In,
            "pt" => CssUnit.Pt,
            "pc" => CssUnit.Pc,
            "em" => CssUnit.Em,
            "rem" => CssUnit.Rem,
            "ex" => CssUnit.Ex,
            "rex" => CssUnit.Rex,
            "cap" => CssUnit.Cap,
            "rcap" => CssUnit.Rcap,
            "ch" => CssUnit.Ch,
            "rch" => CssUnit.Rch,
            "ic" => CssUnit.Ic,
            "ric" => CssUnit.Ric,
            "lh" => CssUnit.Lh,
            "rlh" => CssUnit.Rlh,
            "vw" => CssUnit.Vw,
            "vh" => CssUnit.Vh,
            "vi" => CssUnit.Vi,
            "vb" => CssUnit.Vb,
            "vmin" => CssUnit.Vmin,
            "vmax" => CssUnit.Vmax,
            "svw" => CssUnit.Svw,
            "svh" => CssUnit.Svh,
            "lvw" => CssUnit.Lvw,
            "lvh" => CssUnit.Lvh,
            "dvw" => CssUnit.Dvw,
            "dvh" => CssUnit.Dvh,
            _ => CssUnit.None,
        };
        return result != CssUnit.None;
    }

    internal static double AbsolutePixelsPerUnit(CssUnit unit) => unit switch
    {
        CssUnit.Px => 1,
        CssUnit.In => 96,
        CssUnit.Cm => 96 / 2.54,
        CssUnit.Mm => 96 / 25.4,
        CssUnit.Q => 96 / 101.6,
        CssUnit.Pt => 96.0 / 72,
        CssUnit.Pc => 16,
        _ => double.NaN,
    };
}

/// <summary>An angle, converted to degrees from deg, grad, rad or turn.</summary>
/// <param name="Degrees">The angle in degrees.</param>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Css.cs" region="lengths"/>
/// </example>
public readonly record struct CssAngle(double Degrees)
{
    /// <summary>Gets the angle in radians.</summary>
    public double Radians => Degrees * Math.PI / 180;

    /// <summary>Parses an angle with its unit, such as <c>45deg</c>, <c>0.25turn</c> or <c>1.2rad</c>.</summary>
    /// <param name="text">The CSS text.</param>
    /// <param name="angle">The angle.</param>
    /// <returns>True when the text is one angle.</returns>
    public static bool TryParse(string text, out CssAngle angle)
    {
        ArgumentNullException.ThrowIfNull(text);
        angle = default;
        CssComponentValue? single = CssLength.Single(CssSyntax.ParseComponentValues(text, CssParseOptions.Default));
        return single is not null && TryParse(single, false, out angle);
    }

    /// <summary>Parses one component value as an angle.</summary>
    /// <param name="value">The component value.</param>
    /// <param name="allowNumber">Whether a unitless number counts as degrees, as in SVG and in the hue of <c>hsl()</c>.</param>
    /// <param name="angle">The angle.</param>
    /// <returns>True when the value is an angle.</returns>
    public static bool TryParse(CssComponentValue value, bool allowNumber, out CssAngle angle)
    {
        ArgumentNullException.ThrowIfNull(value);
        angle = default;
        if (value is not CssTokenValue { Token: var token })
        {
            return false;
        }

        if (token.Kind == CssTokenKind.Number && allowNumber)
        {
            angle = new CssAngle(token.Number);
            return true;
        }

        if (token.Kind != CssTokenKind.Dimension || DegreesPerUnit(token.Unit) is not { } perUnit)
        {
            return false;
        }

        angle = new CssAngle(token.Number * perUnit);
        return true;
    }

    internal static double? DegreesPerUnit(string unit) => CssText.ToAsciiLower(unit) switch
    {
        "deg" => 1,
        "grad" => 0.9,
        "rad" => 180 / Math.PI,
        "turn" => 360,
        _ => null,
    };

    /// <summary>Formats the angle as CSS text in degrees.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => Degrees.ToString("R", CultureInfo.InvariantCulture) + "deg";
}

/// <summary>A URL from <c>url(...)</c> (quoted or not) or <c>src(...)</c>.</summary>
/// <param name="Url">The URL text, unresolved.</param>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Css.cs" region="lengths"/>
/// </example>
public readonly record struct CssUrl(string Url)
{
    /// <summary>Parses <c>url(...)</c> or <c>src(...)</c> text.</summary>
    /// <param name="text">The CSS text.</param>
    /// <param name="url">The URL.</param>
    /// <returns>True when the text is one URL.</returns>
    public static bool TryParse(string text, out CssUrl url)
    {
        ArgumentNullException.ThrowIfNull(text);
        url = default;
        CssComponentValue? single = CssLength.Single(CssSyntax.ParseComponentValues(text, CssParseOptions.Default));
        return single is not null && TryParse(single, out url);
    }

    /// <summary>Parses one component value: a url token, or a <c>url()</c> or <c>src()</c> function whose first argument is a string.</summary>
    /// <param name="value">The component value.</param>
    /// <param name="url">The URL.</param>
    /// <returns>True when the value is a URL. URL modifiers after the string are ignored.</returns>
    public static bool TryParse(CssComponentValue value, out CssUrl url)
    {
        ArgumentNullException.ThrowIfNull(value);
        url = default;
        if (value is CssTokenValue { Token: { Kind: CssTokenKind.Url } token })
        {
            url = new CssUrl(token.Value);
            return true;
        }

        if (value is CssFunction function && (function.NameIs("url") || function.NameIs("src")))
        {
            foreach (CssComponentValue argument in function.Arguments)
            {
                if (argument.IsWhitespace)
                {
                    continue;
                }

                if (argument is CssTokenValue { Token: { Kind: CssTokenKind.String } text })
                {
                    url = new CssUrl(text.Value);
                    return true;
                }

                return false;
            }
        }

        return false;
    }

    /// <summary>Formats the URL as CSS text: <c>url("...")</c>.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => "url(" + CssSerializer.QuotedString(Url) + ")";
}
