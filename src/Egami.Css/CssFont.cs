using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace Egami.Css;

/// <summary>One entry of a font family list: a family name or a generic family keyword.</summary>
/// <param name="Name">The family name, or the generic keyword in lowercase (such as <c>sans-serif</c>).</param>
/// <param name="IsGeneric">Whether this is a generic family (an unquoted keyword such as <c>serif</c> or <c>system-ui</c>).</param>
public readonly record struct CssFontFamily(string Name, bool IsGeneric)
{
    /// <summary>Formats the family as CSS text: a generic keyword, or a quoted name.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => IsGeneric ? Name : CssSerializer.QuotedString(Name);
}

/// <summary>A <c>font-family</c> value: family names (quoted, or unquoted identifier sequences) and generic families.</summary>
public sealed class CssFontFamilyList
{
    private static readonly HashSet<string> s_generic = new(StringComparer.Ordinal)
    {
        "serif", "sans-serif", "cursive", "fantasy", "monospace", "system-ui", "ui-serif", "ui-sans-serif", "ui-monospace",
        "ui-rounded", "math", "emoji", "fangsong",
    };

    private CssFontFamilyList(IReadOnlyList<CssFontFamily> families)
    {
        Families = families;
    }

    /// <summary>Gets the families, in order of preference.</summary>
    public IReadOnlyList<CssFontFamily> Families { get; }

    /// <summary>Parses a font family list, such as <c>"Helvetica Neue", Arial, sans-serif</c>.</summary>
    /// <param name="text">The CSS text.</param>
    /// <param name="list">The list.</param>
    /// <returns>True when the text is a valid list.</returns>
    public static bool TryParse(string text, [NotNullWhen(true)] out CssFontFamilyList? list)
    {
        ArgumentNullException.ThrowIfNull(text);
        return TryParse(CssSyntax.ParseComponentValues(text, CssParseOptions.Default), out list);
    }

    /// <summary>Parses component values as a font family list.</summary>
    /// <param name="values">The component values.</param>
    /// <param name="list">The list.</param>
    /// <returns>True when the values are a valid list.</returns>
    public static bool TryParse(IReadOnlyList<CssComponentValue> values, [NotNullWhen(true)] out CssFontFamilyList? list)
    {
        ArgumentNullException.ThrowIfNull(values);
        list = null;
        List<CssFontFamily> families = [];
        List<string> words = [];
        string? quoted = null;
        bool any = false;
        for (int i = 0; i <= values.Count; i++)
        {
            CssComponentValue? item = i < values.Count ? values[i] : null;
            if (item is not null && !item.IsToken(CssTokenKind.Comma))
            {
                if (item.IsWhitespace)
                {
                    continue;
                }

                if (item is CssTokenValue { Token: { Kind: CssTokenKind.String } text } && words.Count == 0 && quoted is null)
                {
                    quoted = text.Value;
                    any = true;
                    continue;
                }

                if (item is CssTokenValue { Token: { Kind: CssTokenKind.Ident } ident } && quoted is null)
                {
                    words.Add(ident.Value);
                    any = true;
                    continue;
                }

                return false;
            }

            if (quoted is not null)
            {
                families.Add(new CssFontFamily(quoted, false));
            }
            else if (words.Count == 1 && s_generic.Contains(CssText.ToAsciiLower(words[0])))
            {
                families.Add(new CssFontFamily(CssText.ToAsciiLower(words[0]), true));
            }
            else if (words.Count > 0)
            {
                if (words.Count == 1 && CssText.ToAsciiLower(words[0]) is "inherit" or "initial" or "unset" or "default" or "revert" or "revert-layer")
                {
                    return false;
                }

                families.Add(new CssFontFamily(string.Join(' ', words), false));
            }
            else
            {
                return false;
            }

            words.Clear();
            quoted = null;
        }

        if (!any || families.Count == 0)
        {
            return false;
        }

        list = new CssFontFamilyList(families);
        return true;
    }

    /// <summary>Formats the list as CSS text.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => string.Join(", ", Families);
}

/// <summary>How a <see cref="CssFontWeight"/> was given.</summary>
public enum CssFontWeightKind
{
    /// <summary>A number from 1 to 1000, or <c>normal</c> (400) or <c>bold</c> (700).</summary>
    Absolute,

    /// <summary><c>bolder</c>: relative to the parent's weight.</summary>
    Bolder,

    /// <summary><c>lighter</c>: relative to the parent's weight.</summary>
    Lighter,
}

/// <summary>A <c>font-weight</c> value.</summary>
/// <param name="Value">The weight from 1 to 1000 for <see cref="CssFontWeightKind.Absolute"/>; 0 otherwise.</param>
/// <param name="Kind">Whether the weight is absolute or relative.</param>
public readonly record struct CssFontWeight(double Value, CssFontWeightKind Kind = CssFontWeightKind.Absolute)
{
    /// <summary>Gets <c>normal</c>: 400.</summary>
    public static CssFontWeight Normal => new(400);

    /// <summary>Gets <c>bold</c>: 700.</summary>
    public static CssFontWeight Bold => new(700);

    /// <summary>Parses <c>normal</c>, <c>bold</c>, <c>bolder</c>, <c>lighter</c> or a number from 1 to 1000.</summary>
    /// <param name="text">The CSS text.</param>
    /// <param name="weight">The weight.</param>
    /// <returns>True when valid.</returns>
    public static bool TryParse(string text, out CssFontWeight weight)
    {
        ArgumentNullException.ThrowIfNull(text);
        return TryParse(CssSyntax.ParseComponentValues(text, CssParseOptions.Default), out weight);
    }

    /// <summary>Parses component values as a font weight.</summary>
    /// <param name="values">The component values.</param>
    /// <param name="weight">The weight.</param>
    /// <returns>True when valid.</returns>
    public static bool TryParse(IReadOnlyList<CssComponentValue> values, out CssFontWeight weight)
    {
        ArgumentNullException.ThrowIfNull(values);
        weight = default;
        CssComponentValue? single = CssLength.Single(values);
        return single is not null && TryParse(single, out weight);
    }

    /// <summary>Parses one component value as a font weight.</summary>
    /// <param name="value">The component value.</param>
    /// <param name="weight">The weight.</param>
    /// <returns>True when valid.</returns>
    public static bool TryParse(CssComponentValue value, out CssFontWeight weight)
    {
        ArgumentNullException.ThrowIfNull(value);
        weight = default;
        if (value is CssFunction function && CssCalc.TryParse(function, out CssCalc? calc) && calc.IsNumber)
        {
            weight = new CssFontWeight(Math.Clamp(calc.Resolve(static _ => double.NaN), 1, 1000));
            return true;
        }

        if (value is not CssTokenValue { Token: var token })
        {
            return false;
        }

        if (token.Kind == CssTokenKind.Number)
        {
            if (token.Number is < 1 or > 1000)
            {
                return false;
            }

            weight = new CssFontWeight(token.Number);
            return true;
        }

        if (token.Kind != CssTokenKind.Ident)
        {
            return false;
        }

        switch (CssText.ToAsciiLower(token.Value))
        {
            case "normal":
                weight = Normal;
                return true;
            case "bold":
                weight = Bold;
                return true;
            case "bolder":
                weight = new CssFontWeight(0, CssFontWeightKind.Bolder);
                return true;
            case "lighter":
                weight = new CssFontWeight(0, CssFontWeightKind.Lighter);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Resolves the weight against the parent's computed weight (CSS Fonts 4, "Determination of bolder and lighter").</summary>
    /// <param name="parentWeight">The parent element's weight.</param>
    /// <returns>The absolute weight.</returns>
    public double Resolve(double parentWeight)
    {
        return Kind switch
        {
            CssFontWeightKind.Bolder => parentWeight switch
            {
                < 350 => 400,
                < 550 => 700,
                < 900 => 900,
                _ => parentWeight,
            },
            CssFontWeightKind.Lighter => parentWeight switch
            {
                < 100 => parentWeight,
                < 550 => 100,
                < 750 => 400,
                _ => 700,
            },
            _ => Value,
        };
    }

    /// <summary>Formats the weight as CSS text.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => Kind switch
    {
        CssFontWeightKind.Bolder => "bolder",
        CssFontWeightKind.Lighter => "lighter",
        _ => Value.ToString("R", CultureInfo.InvariantCulture),
    };
}

/// <summary>The <c>font</c> shorthand (CSS Fonts 4), parsed.</summary>
public sealed class CssFont
{
    private static readonly HashSet<string> s_systemFonts = new(StringComparer.Ordinal) { "caption", "icon", "menu", "message-box", "small-caption", "status-bar" };

    internal static readonly HashSet<string> StretchKeywords = new(StringComparer.Ordinal)
    {
        "ultra-condensed", "extra-condensed", "condensed", "semi-condensed", "semi-expanded", "expanded", "extra-expanded", "ultra-expanded",
    };

    internal static readonly HashSet<string> SizeKeywords = new(StringComparer.Ordinal)
    {
        "xx-small", "x-small", "small", "medium", "large", "x-large", "xx-large", "xxx-large", "larger", "smaller",
    };

    private CssFont()
    {
    }

    /// <summary>Gets the system font keyword (<c>caption</c>, <c>icon</c>, <c>menu</c>, <c>message-box</c>, <c>small-caption</c> or <c>status-bar</c>), or <see langword="null"/>.</summary>
    public string? SystemFont { get; private init; }

    /// <summary>Gets the font style: <c>normal</c>, <c>italic</c> or <c>oblique</c>.</summary>
    public string Style { get; private init; } = "normal";

    /// <summary>Gets the angle of <c>oblique &lt;angle&gt;</c> in degrees, or <see langword="null"/>.</summary>
    public double? ObliqueAngle { get; private init; }

    /// <summary>Gets the variant: <c>normal</c> or <c>small-caps</c>.</summary>
    public string VariantCaps { get; private init; } = "normal";

    /// <summary>Gets the weight.</summary>
    public CssFontWeight Weight { get; private init; } = CssFontWeight.Normal;

    /// <summary>Gets the width keyword, such as <c>condensed</c>, or <c>normal</c>.</summary>
    public string Stretch { get; private init; } = "normal";

    /// <summary>Gets the size: a keyword such as <c>medium</c>, a length, a percentage or a math function. Empty for a system font.</summary>
    public IReadOnlyList<CssComponentValue> Size { get; private init; } = [];

    /// <summary>Gets the line height, or <see langword="null"/> when not given (<c>normal</c>).</summary>
    public IReadOnlyList<CssComponentValue>? LineHeight { get; private init; }

    /// <summary>Gets the font families, or <see langword="null"/> for a system font.</summary>
    public CssFontFamilyList? Families { get; private init; }

    /// <summary>Parses a <c>font</c> shorthand value, such as <c>italic bold 12px/1.5 "Open Sans", sans-serif</c>.</summary>
    /// <param name="text">The CSS text.</param>
    /// <param name="font">The font.</param>
    /// <returns>True when valid.</returns>
    public static bool TryParse(string text, [NotNullWhen(true)] out CssFont? font)
    {
        ArgumentNullException.ThrowIfNull(text);
        return TryParse(CssSyntax.ParseComponentValues(text, CssParseOptions.Default), out font);
    }

    /// <summary>Parses component values as a <c>font</c> shorthand value.</summary>
    /// <param name="values">The component values.</param>
    /// <param name="font">The font.</param>
    /// <returns>True when valid.</returns>
    public static bool TryParse(IReadOnlyList<CssComponentValue> values, [NotNullWhen(true)] out CssFont? font)
    {
        ArgumentNullException.ThrowIfNull(values);
        font = null;
        List<CssComponentValue> items = CssSyntax.Trim(values);
        if (items is [CssTokenValue { Token: { Kind: CssTokenKind.Ident } system }] && s_systemFonts.Contains(CssText.ToAsciiLower(system.Value)))
        {
            font = new CssFont { SystemFont = CssText.ToAsciiLower(system.Value) };
            return true;
        }

        int i = 0;
        string? style = null;
        double? obliqueAngle = null;
        string? variant = null;
        CssFontWeight? weight = null;
        string? stretch = null;
        int prefixCount = 0;
        while (i < items.Count)
        {
            if (items[i].IsWhitespace)
            {
                i++;
                continue;
            }

            if (prefixCount >= 4 || IsSize(items[i]))
            {
                break;
            }

            CssComponentValue item = items[i];
            string? word = item is CssTokenValue { Token: { Kind: CssTokenKind.Ident } ident } ? CssText.ToAsciiLower(ident.Value) : null;
            if (word == "normal")
            {
                prefixCount++;
                i++;
                continue;
            }

            if (style is null && word is "italic" or "oblique")
            {
                style = word;
                i++;
                if (word == "oblique")
                {
                    int j = i;
                    while (j < items.Count && items[j].IsWhitespace)
                    {
                        j++;
                    }

                    if (j < items.Count && CssAngle.TryParse(items[j], false, out CssAngle angle) && angle.Degrees is >= -90 and <= 90)
                    {
                        obliqueAngle = angle.Degrees;
                        i = j + 1;
                    }
                }

                prefixCount++;
                continue;
            }

            if (variant is null && word == "small-caps")
            {
                variant = word;
                prefixCount++;
                i++;
                continue;
            }

            if (stretch is null && word is not null && StretchKeywords.Contains(word))
            {
                stretch = word;
                prefixCount++;
                i++;
                continue;
            }

            if (weight is null && CssFontWeight.TryParse(item, out CssFontWeight parsedWeight) && parsedWeight.Kind == CssFontWeightKind.Absolute)
            {
                weight = parsedWeight;
                prefixCount++;
                i++;
                continue;
            }

            return false;
        }

        while (i < items.Count && items[i].IsWhitespace)
        {
            i++;
        }

        if (i >= items.Count || !IsSize(items[i]))
        {
            return false;
        }

        IReadOnlyList<CssComponentValue> size = [items[i]];
        i++;
        while (i < items.Count && items[i].IsWhitespace)
        {
            i++;
        }

        IReadOnlyList<CssComponentValue>? lineHeight = null;
        if (i < items.Count && items[i].IsDelim('/'))
        {
            i++;
            while (i < items.Count && items[i].IsWhitespace)
            {
                i++;
            }

            if (i >= items.Count || !IsLineHeight(items[i]))
            {
                return false;
            }

            lineHeight = [items[i]];
            i++;
        }

        if (!CssFontFamilyList.TryParse(items.GetRange(i, items.Count - i), out CssFontFamilyList? families))
        {
            return false;
        }

        font = new CssFont
        {
            Style = style ?? "normal",
            ObliqueAngle = obliqueAngle,
            VariantCaps = variant ?? "normal",
            Weight = weight ?? CssFontWeight.Normal,
            Stretch = stretch ?? "normal",
            Size = size,
            LineHeight = lineHeight,
            Families = families,
        };
        return true;
    }

    /// <summary>Whether a value is a valid <c>font-size</c>: a keyword, a non-negative length or percentage, or a math function.</summary>
    internal static bool IsSize(CssComponentValue value)
    {
        if (value is CssTokenValue { Token: { Kind: CssTokenKind.Ident } ident })
        {
            return SizeKeywords.Contains(CssText.ToAsciiLower(ident.Value));
        }

        if (value is CssFunction)
        {
            return CssCalc.TryParse(value, out CssCalc? calc) && !calc.IsNumber;
        }

        return CssLength.TryParse(value, out CssLength length) && length.Value >= 0 && (length.Unit != CssUnit.None || length.Value == 0);
    }

    internal static bool IsLineHeight(CssComponentValue value)
    {
        if (value.IsIdent("normal"))
        {
            return true;
        }

        if (value is CssFunction)
        {
            return CssCalc.TryParse(value, out _);
        }

        return CssLength.TryParse(value, out CssLength length) && length.Value >= 0;
    }

    /// <summary>Formats the font as CSS text.</summary>
    /// <returns>The text.</returns>
    public override string ToString()
    {
        if (SystemFont is not null)
        {
            return SystemFont;
        }

        StringBuilder builder = new();
        if (Style != "normal")
        {
            builder.Append(Style).Append(' ');
            if (ObliqueAngle is { } angle)
            {
                builder.Append(new CssAngle(angle)).Append(' ');
            }
        }

        if (VariantCaps != "normal")
        {
            builder.Append(VariantCaps).Append(' ');
        }

        if (Weight != CssFontWeight.Normal)
        {
            builder.Append(Weight).Append(' ');
        }

        if (Stretch != "normal")
        {
            builder.Append(Stretch).Append(' ');
        }

        builder.Append(CssComponentValue.Serialize(Size));
        if (LineHeight is not null)
        {
            builder.Append('/').Append(CssComponentValue.Serialize(LineHeight));
        }

        builder.Append(' ').Append(Families);
        return builder.ToString();
    }
}
