using System.Globalization;
using System.Text;

namespace Egami.Css;

/// <summary>A range of numbers, such as a weight range <c>100 900</c>; a single value has equal ends.</summary>
/// <param name="Min">The lower end.</param>
/// <param name="Max">The upper end.</param>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Css.cs" region="fonts"/>
/// </example>
public readonly record struct CssRange(double Min, double Max);

/// <summary>A range of code points from <c>unicode-range</c>, such as <c>U+0000-00FF</c>.</summary>
/// <param name="Start">The first code point.</param>
/// <param name="End">The last code point (inclusive).</param>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Css.cs" region="fonts"/>
/// </example>
public readonly record struct CssUnicodeRange(int Start, int End)
{
    /// <summary>Gets whether the range contains a code point.</summary>
    /// <param name="codePoint">The code point.</param>
    /// <returns>True when it lies in the range.</returns>
    public bool Contains(int codePoint) => codePoint >= Start && codePoint <= End;

    /// <summary>Formats the range as CSS text, such as <c>U+0-FF</c>.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => Start == End
        ? string.Create(CultureInfo.InvariantCulture, $"U+{Start:X}")
        : string.Create(CultureInfo.InvariantCulture, $"U+{Start:X}-{End:X}");
}

/// <summary>One source of an <c>@font-face</c> <c>src</c> descriptor: a URL with its format and technologies, or a local font.</summary>
/// <param name="Url">The URL, unresolved, or <see langword="null"/> for a local font.</param>
/// <param name="LocalName">The full font name of <c>local()</c>, or <see langword="null"/> for a URL.</param>
/// <param name="Format">The <c>format()</c> hint in lowercase, such as <c>woff2</c>, or <see langword="null"/>.</param>
/// <param name="Techs">The <c>tech()</c> keywords in lowercase, such as <c>variations</c>; empty when not given.</param>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Css.cs" region="fonts"/>
/// </example>
public sealed record class CssFontSource(string? Url, string? LocalName, string? Format, IReadOnlyList<string> Techs);

/// <summary>The descriptors of an <c>@font-face</c> rule (CSS Fonts 4), parsed. Invalid descriptors keep their defaults.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Css.cs" region="fonts"/>
/// </example>
public sealed class CssFontFace
{
    private CssFontFace()
    {
    }

    /// <summary>Gets the family name the face is known by, or <see langword="null"/> when missing (the rule is then unusable).</summary>
    public string? Family { get; private set; }

    /// <summary>Gets the sources, in order of preference. Entries that do not parse are skipped.</summary>
    public IReadOnlyList<CssFontSource> Sources { get; private set; } = [];

    /// <summary>Gets the weight range, or <see langword="null"/> for <c>auto</c> (the font's own).</summary>
    public CssRange? Weight { get; private set; }

    /// <summary>Gets the style: <c>normal</c>, <c>italic</c> or <c>oblique</c>; <see langword="null"/> for <c>auto</c>.</summary>
    public string? Style { get; private set; }

    /// <summary>Gets the oblique angle range in degrees for an <c>oblique</c> style (14 degrees when not given).</summary>
    public CssRange? ObliqueAngle { get; private set; }

    /// <summary>Gets the width range as percentages (<c>condensed</c> is 75), or <see langword="null"/> for <c>auto</c>.</summary>
    public CssRange? Stretch { get; private set; }

    /// <summary>Gets the code point ranges the face covers; all of Unicode when not given.</summary>
    public IReadOnlyList<CssUnicodeRange> UnicodeRanges { get; private set; } = [new CssUnicodeRange(0, 0x10FFFF)];

    /// <summary>Gets the <c>font-display</c> keyword: <c>auto</c> (the default), <c>block</c>, <c>swap</c>, <c>fallback</c> or <c>optional</c>.</summary>
    public string Display { get; private set; } = "auto";

    /// <summary>Gets the <c>font-feature-settings</c> descriptor as CSS text, or <see langword="null"/>.</summary>
    public string? FeatureSettings { get; private set; }

    /// <summary>Gets the <c>font-variation-settings</c> descriptor as CSS text, or <see langword="null"/>.</summary>
    public string? VariationSettings { get; private set; }

    /// <summary>Reads the descriptors of an <c>@font-face</c> rule.</summary>
    /// <param name="declarations">The rule's declarations.</param>
    /// <returns>The parsed descriptors.</returns>
    public static CssFontFace FromDeclarations(IEnumerable<CssDeclaration> declarations)
    {
        ArgumentNullException.ThrowIfNull(declarations);
        CssFontFace face = new();
        foreach (CssDeclaration declaration in declarations)
        {
            IReadOnlyList<CssComponentValue> value = declaration.Value;
            switch (declaration.Name)
            {
                case "font-family":
                    if (CssFontFamilyList.TryParse(value, out CssFontFamilyList? list) && list.Families is [{ IsGeneric: false } family])
                    {
                        face.Family = family.Name;
                    }

                    break;
                case "src":
                    face.Sources = ParseSources(value);
                    break;
                case "font-weight":
                    if (ParseRange(value, WeightValue) is { } weight)
                    {
                        face.Weight = weight.Min < 0 ? null : weight;
                    }

                    break;
                case "font-stretch" or "font-width":
                    if (ParseRange(value, StretchValue) is { } stretch)
                    {
                        face.Stretch = stretch.Min < 0 ? null : stretch;
                    }

                    break;
                case "font-style":
                    ParseStyle(face, value);
                    break;
                case "unicode-range":
                    if (ParseUnicodeRanges(value) is { } ranges)
                    {
                        face.UnicodeRanges = ranges;
                    }

                    break;
                case "font-display":
                    if (CssLength.Single(value) is CssTokenValue { Token: { Kind: CssTokenKind.Ident } display }
                        && CssText.ToAsciiLower(display.Value) is "auto" or "block" or "swap" or "fallback" or "optional")
                    {
                        face.Display = CssText.ToAsciiLower(display.Value);
                    }

                    break;
                case "font-feature-settings":
                    face.FeatureSettings = declaration.ValueText;
                    break;
                case "font-variation-settings":
                    face.VariationSettings = declaration.ValueText;
                    break;
            }
        }

        return face;
    }

    private static List<CssFontSource> ParseSources(IReadOnlyList<CssComponentValue> value)
    {
        List<CssFontSource> sources = [];
        foreach (List<CssComponentValue> part in SplitCommas(value))
        {
            if (part.Count == 0)
            {
                continue;
            }

            if (part[0] is CssFunction local && local.NameIs("local"))
            {
                if (part.Count == 1 && CssFontFamilyList.TryParse(local.Arguments, out CssFontFamilyList? names) && names.Families is [{ IsGeneric: false } name])
                {
                    sources.Add(new CssFontSource(null, name.Name, null, []));
                }

                continue;
            }

            if (!CssUrl.TryParse(part[0], out CssUrl url))
            {
                continue;
            }

            string? format = null;
            List<string> techs = [];
            bool valid = true;
            for (int i = 1; i < part.Count && valid; i++)
            {
                if (part[i] is not CssFunction function)
                {
                    valid = false;
                }
                else if (function.NameIs("format") && format is null && techs.Count == 0)
                {
                    List<CssComponentValue> arguments = CssSyntax.Trim(function.Arguments);
                    format = arguments is [CssTokenValue { Token: { Kind: CssTokenKind.String or CssTokenKind.Ident } formatToken }] ? CssText.ToAsciiLower(formatToken.Value) : null;
                    valid = format is not null;
                }
                else if (function.NameIs("tech") && techs.Count == 0)
                {
                    foreach (List<CssComponentValue> tech in SplitCommas(function.Arguments))
                    {
                        if (tech is [CssTokenValue { Token: { Kind: CssTokenKind.Ident } techToken }])
                        {
                            techs.Add(CssText.ToAsciiLower(techToken.Value));
                        }
                        else if (tech is [CssFunction techFunction])
                        {
                            techs.Add(CssText.ToAsciiLower(techFunction.ToString()));
                        }
                        else
                        {
                            valid = false;
                        }
                    }

                    valid &= techs.Count > 0;
                }
                else
                {
                    valid = false;
                }
            }

            if (valid)
            {
                sources.Add(new CssFontSource(url.Url, null, format, techs));
            }
        }

        return sources;
    }

    // Splits at top-level commas, dropping whitespace.
    private static List<List<CssComponentValue>> SplitCommas(IReadOnlyList<CssComponentValue> value)
    {
        List<List<CssComponentValue>> parts = [[]];
        foreach (CssComponentValue item in value)
        {
            if (item.IsToken(CssTokenKind.Comma))
            {
                parts.Add([]);
            }
            else if (!item.IsWhitespace)
            {
                parts[^1].Add(item);
            }
        }

        return parts;
    }

    // "auto", or one or two values; a negative Min stands for auto.
    private static CssRange? ParseRange(IReadOnlyList<CssComponentValue> value, Func<CssComponentValue, double?> parse)
    {
        List<CssComponentValue> items = [.. value.Where(v => !v.IsWhitespace)];
        if (items is [var only] && only.IsIdent("auto"))
        {
            return new CssRange(-1, -1);
        }

        if (items.Count is < 1 or > 2)
        {
            return null;
        }

        double? a = parse(items[0]);
        double? b = items.Count == 2 ? parse(items[1]) : a;
        if (a is null || b is null)
        {
            return null;
        }

        return new CssRange(Math.Min(a.Value, b.Value), Math.Max(a.Value, b.Value));
    }

    private static double? WeightValue(CssComponentValue value)
    {
        return CssFontWeight.TryParse(value, out CssFontWeight weight) && weight.Kind == CssFontWeightKind.Absolute ? weight.Value : null;
    }

    private static double? StretchValue(CssComponentValue value)
    {
        if (value is CssTokenValue { Token: { Kind: CssTokenKind.Percentage } percentage })
        {
            return percentage.Number >= 0 ? percentage.Number : null;
        }

        return value is CssTokenValue { Token: { Kind: CssTokenKind.Ident } ident } ? CssText.ToAsciiLower(ident.Value) switch
        {
            "ultra-condensed" => 50,
            "extra-condensed" => 62.5,
            "condensed" => 75,
            "semi-condensed" => 87.5,
            "normal" => 100,
            "semi-expanded" => 112.5,
            "expanded" => 125,
            "extra-expanded" => 150,
            "ultra-expanded" => 200,
            _ => null,
        } : null;
    }

    private static void ParseStyle(CssFontFace face, IReadOnlyList<CssComponentValue> value)
    {
        List<CssComponentValue> items = [.. value.Where(v => !v.IsWhitespace)];
        if (items.Count == 0 || items[0] is not CssTokenValue { Token: { Kind: CssTokenKind.Ident } ident })
        {
            return;
        }

        string word = CssText.ToAsciiLower(ident.Value);
        if (items.Count == 1 && word is "auto" or "normal" or "italic" or "oblique")
        {
            face.Style = word == "auto" ? null : word;
            face.ObliqueAngle = word == "oblique" ? new CssRange(14, 14) : null;
            return;
        }

        if (word != "oblique" || items.Count > 3)
        {
            return;
        }

        double[] angles = new double[items.Count - 1];
        for (int i = 1; i < items.Count; i++)
        {
            if (!CssAngle.TryParse(items[i], false, out CssAngle angle) || angle.Degrees is < -90 or > 90)
            {
                return;
            }

            angles[i - 1] = angle.Degrees;
        }

        face.Style = "oblique";
        face.ObliqueAngle = new CssRange(angles.Min(), angles.Max());
    }

    // <urange>#, read from the tokens' text as CSS Syntax 3 section 7 says.
    internal static List<CssUnicodeRange>? ParseUnicodeRanges(IReadOnlyList<CssComponentValue> value)
    {
        List<CssUnicodeRange> ranges = [];
        foreach (List<CssComponentValue> part in SplitCommasKeepingAdjacency(value))
        {
            if (part.Count == 0 || part[0] is not CssTokenValue { Token: { Kind: CssTokenKind.Ident } first } || !CssText.EqualsIgnoreAsciiCase(first.Value, "u") && !CssText.StartsWithIgnoreAsciiCase(first.Value, "u"))
            {
                return null;
            }

            StringBuilder text = new();
            foreach (CssComponentValue item in part)
            {
                if (item is not CssTokenValue { Token: var token })
                {
                    return null;
                }

                switch (token.Kind)
                {
                    case CssTokenKind.Ident:
                        text.Append(token.Value);
                        break;
                    case CssTokenKind.Delim when token.IsDelim('+') || token.IsDelim('?'):
                        text.Append(token.Value);
                        break;
                    case CssTokenKind.Number:
                        text.Append(token.NumberText);
                        break;
                    case CssTokenKind.Dimension:
                        text.Append(token.NumberText).Append(token.Unit);
                        break;
                    default:
                        return null;
                }
            }

            if (ParseUnicodeRange(text.ToString()) is not { } range)
            {
                return null;
            }

            ranges.Add(range);
        }

        return ranges.Count > 0 ? ranges : null;
    }

    // Splits at commas; whitespace inside a range is not allowed, so a part with whitespace in its middle is invalid.
    private static List<List<CssComponentValue>> SplitCommasKeepingAdjacency(IReadOnlyList<CssComponentValue> value)
    {
        List<List<CssComponentValue>> parts = [];
        foreach (List<CssComponentValue> part in SplitRaw(value))
        {
            List<CssComponentValue> trimmed = CssSyntax.Trim(part);
            if (trimmed.Exists(v => v.IsWhitespace))
            {
                parts.Add([]);
                continue;
            }

            parts.Add(trimmed);
        }

        return parts;

        static List<List<CssComponentValue>> SplitRaw(IReadOnlyList<CssComponentValue> value)
        {
            List<List<CssComponentValue>> raw = [[]];
            foreach (CssComponentValue item in value)
            {
                if (item.IsToken(CssTokenKind.Comma))
                {
                    raw.Add([]);
                }
                else
                {
                    raw[^1].Add(item);
                }
            }

            return raw;
        }
    }

    private static CssUnicodeRange? ParseUnicodeRange(string text)
    {
        if (text.Length < 3 || (text[0] | 0x20) != 'u' || text[1] != '+')
        {
            return null;
        }

        int i = 2;
        int start = 0;
        int digits = 0;
        while (i < text.Length && CssText.IsHexDigit(text[i]) && digits < 6)
        {
            start = (start * 16) + CssText.HexValue(text[i]);
            i++;
            digits++;
        }

        int questions = 0;
        while (i < text.Length && text[i] == '?' && digits + questions < 6)
        {
            questions++;
            i++;
        }

        if (digits + questions == 0)
        {
            return null;
        }

        int end;
        if (questions > 0)
        {
            if (i != text.Length)
            {
                return null;
            }

            end = start;
            for (int q = 0; q < questions; q++)
            {
                start *= 16;
                end = (end * 16) + 15;
            }
        }
        else if (i == text.Length)
        {
            end = start;
        }
        else
        {
            if (text[i] != '-')
            {
                return null;
            }

            i++;
            end = 0;
            int endDigits = 0;
            while (i < text.Length && CssText.IsHexDigit(text[i]) && endDigits < 6)
            {
                end = (end * 16) + CssText.HexValue(text[i]);
                i++;
                endDigits++;
            }

            if (endDigits == 0 || i != text.Length)
            {
                return null;
            }
        }

        if (end > 0x10FFFF || start > end)
        {
            return null;
        }

        return new CssUnicodeRange(start, end);
    }
}
