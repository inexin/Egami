using Egami.Css;
using Egami.Svg.Values;

namespace Egami.Svg.Styling;

/// <summary>What a fill or stroke paint names: none, a color, the current color, a paint server (with a fallback), or the context's paint.</summary>
internal enum SvgPaintKind : byte
{
    None,
    Color,
    CurrentColor,
    Server,
    ContextFill,
    ContextStroke,
}

/// <summary>A fill or stroke value: <c>none</c>, a color, <c>currentColor</c>, <c>url(#id) [fallback]</c>, <c>context-fill</c> or <c>context-stroke</c>.</summary>
internal readonly record struct SvgPaintSpec(SvgPaintKind Kind, CssColor Color, string? Url, SvgPaintKind FallbackKind, CssColor FallbackColor)
{
    public static SvgPaintSpec None { get; } = new(SvgPaintKind.None, default, null, SvgPaintKind.None, default);

    public static SvgPaintSpec Black { get; } = new(SvgPaintKind.Color, new CssColor(0, 0, 0), null, SvgPaintKind.None, default);
}

/// <summary>Reads SVG's property values from CSS component values.</summary>
internal static class SvgStyleValues
{
    /// <summary>The values without leading, trailing and doubled whitespace, as a short list.</summary>
    public static int Items(IReadOnlyList<CssComponentValue> values, Span<CssComponentValue?> items)
    {
        int n = 0;
        foreach (CssComponentValue value in values)
        {
            if (value is CssTokenValue { Token.Kind: CssTokenKind.Whitespace })
            {
                continue;
            }

            if (n == items.Length)
            {
                return -1;
            }

            items[n++] = value;
        }

        return n;
    }

    /// <summary>The one non-whitespace value, or <see langword="null"/>.</summary>
    public static CssComponentValue? Single(IReadOnlyList<CssComponentValue> values)
    {
        Span<CssComponentValue?> items = new CssComponentValue?[2];
        return Items(values, items) == 1 ? items[0] : null;
    }

    /// <summary>The single identifier's text, lowercased by the caller's comparisons, or <see langword="null"/>.</summary>
    public static string? Keyword(IReadOnlyList<CssComponentValue> values) =>
        Single(values) is CssTokenValue { Token: { Kind: CssTokenKind.Ident } token } ? token.Value : null;

    /// <summary>Whether the value is one identifier, compared ASCII-case-insensitively.</summary>
    public static bool IsKeyword(IReadOnlyList<CssComponentValue> values, string keyword) =>
        Keyword(values) is string k && k.Equals(keyword, StringComparison.OrdinalIgnoreCase);

    /// <summary>A plain number.</summary>
    public static bool TryNumber(IReadOnlyList<CssComponentValue> values, out float number)
    {
        if (Single(values) is CssTokenValue { Token: { Kind: CssTokenKind.Number } token } && double.IsFinite(token.Number))
        {
            number = (float)token.Number;
            return true;
        }

        number = 0;
        return false;
    }

    /// <summary>An opacity: a number or a percentage, clamped to 0 to 1.</summary>
    public static bool TryOpacity(IReadOnlyList<CssComponentValue> values, out float opacity)
    {
        switch (Single(values))
        {
            case CssTokenValue { Token: { Kind: CssTokenKind.Number } n } when double.IsFinite(n.Number):
                opacity = Math.Clamp((float)n.Number, 0, 1);
                return true;
            case CssTokenValue { Token: { Kind: CssTokenKind.Percentage } p } when double.IsFinite(p.Number):
                opacity = Math.Clamp((float)p.Number / 100, 0, 1);
                return true;
            default:
                opacity = 1;
                return false;
        }
    }

    /// <summary>A length, percentage or unitless number, as an SVG length.</summary>
    public static bool TryLength(IReadOnlyList<CssComponentValue> values, out SvgLength length)
    {
        CssComponentValue? single = Single(values);
        if (single is not null && TryLength(single, out length))
        {
            return true;
        }

        length = default;
        return false;
    }

    /// <summary>One component value as an SVG length.</summary>
    public static bool TryLength(CssComponentValue value, out SvgLength length)
    {
        length = default;
        if (!CssLength.TryParse(value, out CssLength css) || !double.IsFinite(css.Value))
        {
            return false;
        }

        SvgUnit? unit = css.Unit switch
        {
            CssUnit.None => SvgUnit.None,
            CssUnit.Px => SvgUnit.Px,
            CssUnit.Percent => SvgUnit.Percent,
            CssUnit.Em => SvgUnit.Em,
            CssUnit.Ex => SvgUnit.Ex,
            CssUnit.Ch => SvgUnit.Ch,
            CssUnit.Rem => SvgUnit.Rem,
            CssUnit.In => SvgUnit.In,
            CssUnit.Cm => SvgUnit.Cm,
            CssUnit.Mm => SvgUnit.Mm,
            CssUnit.Q => SvgUnit.Q,
            CssUnit.Pt => SvgUnit.Pt,
            CssUnit.Pc => SvgUnit.Pc,
            CssUnit.Vw => SvgUnit.Vw,
            CssUnit.Vh => SvgUnit.Vh,
            CssUnit.Vmin => SvgUnit.Vmin,
            CssUnit.Vmax => SvgUnit.Vmax,
            _ => null,
        };
        if (unit is not SvgUnit u)
        {
            return false;
        }

        length = new SvgLength((float)css.Value, u);
        return true;
    }

    /// <summary>A list of lengths separated by commas and/or whitespace (stroke-dasharray).</summary>
    public static bool TryLengthList(IReadOnlyList<CssComponentValue> values, List<SvgLength> lengths)
    {
        bool comma = true;
        foreach (CssComponentValue value in values)
        {
            switch (value)
            {
                case CssTokenValue { Token.Kind: CssTokenKind.Whitespace }:
                    continue;
                case CssTokenValue { Token.Kind: CssTokenKind.Comma }:
                    if (comma)
                    {
                        return false;
                    }

                    comma = true;
                    continue;
            }

            if (!TryLength(value, out SvgLength length))
            {
                return false;
            }

            lengths.Add(length);
            comma = false;
        }

        return lengths.Count > 0 && !comma;
    }

    /// <summary>
    /// A color: keywords, #rgb to #rrggbbaa, rgb(), rgba(), hsl() and hsla() in either syntax, currentColor and transparent. The
    /// CSS Color 4 color functions (hwb, lab, lch, oklab, oklch, color, device-cmyk) are refused and flagged, as the design's color
    /// rule says.
    /// </summary>
    public static bool TryColor(IReadOnlyList<CssComponentValue> values, out CssColor color, out bool level4)
    {
        level4 = Single(values) is CssFunction function &&
            function.Name.ToLowerInvariant() is "hwb" or "lab" or "lch" or "oklab" or "oklch" or "color" or "device-cmyk" or "color-mix";
        if (level4)
        {
            color = default;
            return false;
        }

        return CssColor.TryParse(values, out color);
    }

    /// <summary>A paint: none, a color, currentColor, context-fill, context-stroke, or url(...) with an optional none or color fallback.</summary>
    public static bool TryPaint(IReadOnlyList<CssComponentValue> values, out SvgPaintSpec paint, out bool level4)
    {
        level4 = false;
        paint = SvgPaintSpec.None;
        Span<CssComponentValue?> items = new CssComponentValue?[3];
        int n = Items(values, items);
        if (n < 1)
        {
            return false;
        }

        if (CssUrl.TryParse(items[0]!, out CssUrl url))
        {
            if (n == 1)
            {
                paint = new SvgPaintSpec(SvgPaintKind.Server, default, url.Url, SvgPaintKind.None, default);
                return true;
            }

            if (n == 2 && TryPaint([items[1]!], out SvgPaintSpec fallback, out level4) && fallback.Kind is SvgPaintKind.None or SvgPaintKind.Color or SvgPaintKind.CurrentColor)
            {
                paint = new SvgPaintSpec(SvgPaintKind.Server, default, url.Url, fallback.Kind, fallback.Color);
                return true;
            }

            return false;
        }

        if (n != 1)
        {
            return false;
        }

        if (items[0] is CssTokenValue { Token: { Kind: CssTokenKind.Ident } ident })
        {
            switch (ident.Value.ToLowerInvariant())
            {
                case "none":
                    paint = SvgPaintSpec.None;
                    return true;
                case "currentcolor":
                    paint = paint with { Kind = SvgPaintKind.CurrentColor };
                    return true;
                case "context-fill":
                    paint = paint with { Kind = SvgPaintKind.ContextFill };
                    return true;
                case "context-stroke":
                    paint = paint with { Kind = SvgPaintKind.ContextStroke };
                    return true;
            }
        }

        if (TryColor(values, out CssColor color, out level4))
        {
            paint = new SvgPaintSpec(color.IsCurrentColor ? SvgPaintKind.CurrentColor : SvgPaintKind.Color, color, null, SvgPaintKind.None, default);
            return true;
        }

        return false;
    }

    /// <summary>A reference property (clip-path, mask, marker-*): <c>none</c> is an empty string, <c>url(...)</c> its URL.</summary>
    public static bool TryReference(IReadOnlyList<CssComponentValue> values, out string? url)
    {
        url = null;
        CssComponentValue? single = Single(values);
        if (single is CssTokenValue { Token: { Kind: CssTokenKind.Ident } t } && t.Value.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            url = "";
            return true;
        }

        if (single is not null && CssUrl.TryParse(single, out CssUrl u))
        {
            url = u.Url;
            return true;
        }

        return false;
    }

    /// <summary>The element id a local URL names (<c>#id</c>, or a URL into the same document), or <see langword="null"/>.</summary>
    public static string? LocalId(string url)
    {
        int hash = url.IndexOf('#', StringComparison.Ordinal);
        if (hash < 0 || hash == url.Length - 1)
        {
            return null;
        }

        string id = url[(hash + 1)..].Trim();
        return id.Length == 0 ? null : Uri.UnescapeDataString(id);
    }
}
