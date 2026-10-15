namespace Egami.Css;

/// <summary>The properties <see cref="CssPropertyRegistry.CreateDefault"/> registers, with simple validation and the shorthands' expansions.</summary>
internal static class CssDefaultProperties
{
    public static void AddTo(CssPropertyRegistry registry)
    {
        // Inherited text properties.
        registry.Register(new("color", true, "black") { Validate = v => CssColor.TryParse(v, out _) });
        registry.Register(new("font-family", true, "serif") { Validate = v => CssFontFamilyList.TryParse(v, out _) });
        registry.Register(new("font-size", true, "medium") { Validate = v => CssLength.Single(v) is { } s && CssFont.IsSize(s) });
        registry.Register(new("font-style", true, "normal") { Validate = IsFontStyle });
        registry.Register(new("font-weight", true, "normal") { Validate = v => CssFontWeight.TryParse(v, out _) });
        registry.Register(new("font-stretch", true, "normal") { Validate = IsFontStretch });
        registry.Register(new("font-variant-caps", true, "normal") { Validate = v => Keyword(v, "normal", "small-caps", "all-small-caps", "petite-caps", "all-petite-caps", "unicase", "titling-caps") });
        registry.Register(new("font-variant-ligatures", true, "normal"));
        registry.Register(new("font-variant-numeric", true, "normal"));
        registry.Register(new("font-variant-east-asian", true, "normal"));
        registry.Register(new("font-variant-alternates", true, "normal"));
        registry.Register(new("font-variant-position", true, "normal") { Validate = v => Keyword(v, "normal", "sub", "super") });
        registry.Register(new("font-size-adjust", true, "none"));
        registry.Register(new("font-kerning", true, "auto") { Validate = v => Keyword(v, "auto", "normal", "none") });
        registry.Register(new("font-optical-sizing", true, "auto") { Validate = v => Keyword(v, "auto", "none") });
        registry.Register(new("font-feature-settings", true, "normal"));
        registry.Register(new("font-variation-settings", true, "normal"));
        registry.Register(new("font-language-override", true, "normal"));
        registry.Register(new("line-height", true, "normal") { Validate = v => CssLength.Single(v) is { } s && CssFont.IsLineHeight(s) });
        registry.Register(new("letter-spacing", true, "normal") { Validate = IsSpacing });
        registry.Register(new("word-spacing", true, "normal") { Validate = IsSpacing });
        registry.Register(new("text-align", true, "start") { Validate = v => Keyword(v, "start", "end", "left", "right", "center", "justify", "match-parent", "justify-all") });
        registry.Register(new("text-indent", true, "0"));
        registry.Register(new("text-transform", true, "none"));
        registry.Register(new("white-space", true, "normal") { Validate = v => Keyword(v, "normal", "pre", "nowrap", "pre-wrap", "break-spaces", "pre-line") });
        registry.Register(new("direction", true, "ltr") { Validate = v => Keyword(v, "ltr", "rtl") });
        registry.Register(new("writing-mode", true, "horizontal-tb")
        {
            Validate = v => Keyword(v, "horizontal-tb", "vertical-rl", "vertical-lr", "sideways-rl", "sideways-lr", "lr", "lr-tb", "rl", "rl-tb", "tb", "tb-rl"),
        });
        registry.Register(new("text-orientation", true, "mixed") { Validate = v => Keyword(v, "mixed", "upright", "sideways", "sideways-right") });
        registry.Register(new("visibility", true, "visible") { Validate = v => Keyword(v, "visible", "hidden", "collapse") });

        // Non-inherited.
        registry.Register(new("display", false, "inline") { Validate = v => v.Count > 0 && v.All(i => i.IsWhitespace || i is CssTokenValue { Token.Kind: CssTokenKind.Ident }) });
        registry.Register(new("opacity", false, "1") { Validate = IsAlpha });
        registry.Register(new("unicode-bidi", false, "normal") { Validate = v => Keyword(v, "normal", "embed", "isolate", "bidi-override", "isolate-override", "plaintext") });
        registry.Register(new("text-decoration-line", false, "none") { Validate = IsDecorationLine });
        registry.Register(new("text-decoration-style", false, "solid") { Validate = v => Keyword(v, "solid", "double", "dotted", "dashed", "wavy") });
        registry.Register(new("text-decoration-color", false, "currentcolor") { Validate = v => CssColor.TryParse(v, out _) });
        registry.Register(new("text-decoration-thickness", false, "auto") { Validate = IsThickness });

        registry.RegisterShorthand(new CssShorthandDefinition("font", FontLonghands, ExpandFont));
        registry.RegisterShorthand(new CssShorthandDefinition("font-variant", FontVariantLonghands, ExpandFontVariant));
        registry.RegisterShorthand(new CssShorthandDefinition(
            "text-decoration",
            ["text-decoration-line", "text-decoration-style", "text-decoration-color", "text-decoration-thickness"],
            ExpandTextDecoration));

        CssBoxProperties.AddTo(registry);
    }

    private static readonly string[] FontVariantLonghands =
    [
        "font-variant-ligatures", "font-variant-caps", "font-variant-numeric", "font-variant-east-asian", "font-variant-alternates", "font-variant-position",
    ];

    private static readonly string[] FontLonghands =
    [
        "font-style", "font-variant-caps", "font-weight", "font-stretch", "font-size", "line-height", "font-family",
        "font-variant-ligatures", "font-variant-numeric", "font-variant-east-asian", "font-variant-alternates", "font-variant-position",
        "font-size-adjust", "font-kerning", "font-optical-sizing", "font-variation-settings", "font-language-override",
    ];

    private static IReadOnlyList<CssComponentValue> Ident(string value) => [new CssTokenValue(new CssToken(CssTokenKind.Ident, value))];

    private static List<CssComponentValue> Parse(string value) => CssSyntax.Trim(CssSyntax.ParseComponentValues(value, CssParseOptions.Default));

    private static bool Keyword(IReadOnlyList<CssComponentValue> value, params ReadOnlySpan<string> keywords)
    {
        if (CssLength.Single(value) is not CssTokenValue { Token: { Kind: CssTokenKind.Ident } token })
        {
            return false;
        }

        foreach (string keyword in keywords)
        {
            if (CssText.EqualsIgnoreAsciiCase(token.Value, keyword))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsFontStyle(IReadOnlyList<CssComponentValue> value)
    {
        List<CssComponentValue> items = [.. value.Where(v => !v.IsWhitespace)];
        if (items is [var only])
        {
            return only.IsIdent("normal") || only.IsIdent("italic") || only.IsIdent("oblique");
        }

        return items is [var first, var angle] && first.IsIdent("oblique") && CssAngle.TryParse(angle, false, out CssAngle a) && a.Degrees is >= -90 and <= 90;
    }

    private static bool IsFontStretch(IReadOnlyList<CssComponentValue> value)
    {
        if (CssLength.Single(value) is not CssTokenValue { Token: var token })
        {
            return false;
        }

        if (token.Kind == CssTokenKind.Percentage)
        {
            return token.Number >= 0;
        }

        return token.Kind == CssTokenKind.Ident && (token.IsIdent("normal") || CssFont.StretchKeywords.Contains(CssText.ToAsciiLower(token.Value)));
    }

    private static bool IsSpacing(IReadOnlyList<CssComponentValue> value)
    {
        CssComponentValue? single = CssLength.Single(value);
        if (single is null)
        {
            return false;
        }

        if (single.IsIdent("normal"))
        {
            return true;
        }

        if (single is CssFunction)
        {
            return CssCalc.TryParse(single, out CssCalc? calc) && !calc.IsNumber;
        }

        return CssLength.TryParse(single, out CssLength length) && (length.Unit != CssUnit.None || length.Value == 0);
    }

    private static bool IsAlpha(IReadOnlyList<CssComponentValue> value)
    {
        CssComponentValue? single = CssLength.Single(value);
        if (single is CssFunction)
        {
            return CssCalc.TryParse(single, out CssCalc? calc) && calc.IsNumber;
        }

        return single is CssTokenValue { Token.Kind: CssTokenKind.Number or CssTokenKind.Percentage };
    }

    private static bool IsThickness(IReadOnlyList<CssComponentValue> value)
    {
        CssComponentValue? single = CssLength.Single(value);
        return single is not null && IsThickness(single);
    }

    private static bool IsThickness(CssComponentValue single)
    {
        if (single.IsIdent("auto") || single.IsIdent("from-font"))
        {
            return true;
        }

        if (single is CssFunction)
        {
            return CssCalc.TryParse(single, out CssCalc? calc) && !calc.IsNumber;
        }

        return CssLength.TryParse(single, out CssLength length) && (length.Unit != CssUnit.None || length.Value == 0);
    }

    private static bool IsDecorationLine(IReadOnlyList<CssComponentValue> value)
    {
        List<CssComponentValue> items = [.. value.Where(v => !v.IsWhitespace)];
        if (items is [var only] && only.IsIdent("none"))
        {
            return true;
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (CssComponentValue item in items)
        {
            if (item is not CssTokenValue { Token: { Kind: CssTokenKind.Ident } ident })
            {
                return false;
            }

            string word = CssText.ToAsciiLower(ident.Value);
            if (word is not ("underline" or "overline" or "line-through" or "blink") || !seen.Add(word))
            {
                return false;
            }
        }

        return items.Count > 0;
    }

    private static List<CssDeclaration>? ExpandFont(IReadOnlyList<CssComponentValue> value)
    {
        if (!CssFont.TryParse(value, out CssFont? font))
        {
            return null;
        }

        List<CssDeclaration> result = [];
        if (font.SystemFont is not null)
        {
            // System fonts are implementation-defined: the system UI family at the default size.
            result.Add(new CssDeclaration("font-family", Ident("system-ui")));
            result.Add(new CssDeclaration("font-size", Ident("medium")));
            return result;
        }

        result.Add(new CssDeclaration("font-style", font.ObliqueAngle is { } angle ? Parse("oblique " + new CssAngle(angle)) : Ident(font.Style)));
        result.Add(new CssDeclaration("font-variant-caps", Ident(font.VariantCaps)));
        result.Add(new CssDeclaration("font-weight", Parse(font.Weight.ToString())));
        result.Add(new CssDeclaration("font-stretch", Ident(font.Stretch)));
        result.Add(new CssDeclaration("font-size", font.Size));
        result.Add(new CssDeclaration("line-height", font.LineHeight ?? Ident("normal")));
        result.Add(new CssDeclaration("font-family", Parse(font.Families!.ToString())));
        return result;
    }

    private static List<CssDeclaration>? ExpandFontVariant(IReadOnlyList<CssComponentValue> value)
    {
        List<CssComponentValue> items = [.. value.Where(v => !v.IsWhitespace)];
        if (items is [var only] && (only.IsIdent("normal") || only.IsIdent("none")))
        {
            return only.IsIdent("none") ? [new CssDeclaration("font-variant-ligatures", Ident("none"))] : [];
        }

        Dictionary<string, List<CssComponentValue>> parts = new(StringComparer.Ordinal);
        foreach (CssComponentValue item in items)
        {
            string? longhand = item switch
            {
                CssTokenValue { Token: { Kind: CssTokenKind.Ident } ident } => CssText.ToAsciiLower(ident.Value) switch
                {
                    "common-ligatures" or "no-common-ligatures" or "discretionary-ligatures" or "no-discretionary-ligatures"
                        or "historical-ligatures" or "no-historical-ligatures" or "contextual" or "no-contextual" => "font-variant-ligatures",
                    "small-caps" or "all-small-caps" or "petite-caps" or "all-petite-caps" or "unicase" or "titling-caps" => "font-variant-caps",
                    "lining-nums" or "oldstyle-nums" or "proportional-nums" or "tabular-nums" or "diagonal-fractions" or "stacked-fractions"
                        or "ordinal" or "slashed-zero" => "font-variant-numeric",
                    "jis78" or "jis83" or "jis90" or "jis04" or "simplified" or "traditional" or "full-width" or "proportional-width" or "ruby" => "font-variant-east-asian",
                    "historical-forms" => "font-variant-alternates",
                    "sub" or "super" => "font-variant-position",
                    _ => null,
                },
                CssFunction function when function.NameIs("stylistic") || function.NameIs("styleset") || function.NameIs("character-variant")
                    || function.NameIs("swash") || function.NameIs("ornaments") || function.NameIs("annotation") => "font-variant-alternates",
                _ => null,
            };
            if (longhand is null)
            {
                return null;
            }

            if (!parts.TryGetValue(longhand, out List<CssComponentValue>? list))
            {
                list = [];
                parts[longhand] = list;
            }
            else
            {
                list.Add(new CssTokenValue(new CssToken(CssTokenKind.Whitespace)));
            }

            list.Add(item);
        }

        if ((parts.TryGetValue("font-variant-caps", out List<CssComponentValue>? caps) && caps.Count > 1)
            || (parts.TryGetValue("font-variant-position", out List<CssComponentValue>? position) && position.Count > 1))
        {
            return null;
        }

        return [.. parts.Select(p => new CssDeclaration(p.Key, p.Value))];
    }

    private static List<CssDeclaration>? ExpandTextDecoration(IReadOnlyList<CssComponentValue> value)
    {
        List<CssComponentValue> items = [.. value.Where(v => !v.IsWhitespace)];
        List<CssComponentValue> line = [];
        CssComponentValue? style = null;
        CssComponentValue? color = null;
        CssComponentValue? thickness = null;
        foreach (CssComponentValue item in items)
        {
            if (item.IsIdent("none") && line.Count == 0)
            {
                line.Add(item);
                continue;
            }

            if (item.IsIdent("underline") || item.IsIdent("overline") || item.IsIdent("line-through") || item.IsIdent("blink"))
            {
                if (line.Count > 0)
                {
                    if (line[0].IsIdent("none"))
                    {
                        return null;
                    }

                    line.Add(new CssTokenValue(new CssToken(CssTokenKind.Whitespace)));
                }

                line.Add(item);
                continue;
            }

            if (style is null && Keyword([item], "solid", "double", "dotted", "dashed", "wavy"))
            {
                style = item;
                continue;
            }

            if (thickness is null && IsThickness(item))
            {
                thickness = item;
                continue;
            }

            if (color is null && CssColor.TryParse(item, out _))
            {
                color = item;
                continue;
            }

            return null;
        }

        if (items.Count == 0 || (line.Count > 0 && !IsDecorationLine(line)))
        {
            return null;
        }

        List<CssDeclaration> result = [];
        if (line.Count > 0)
        {
            result.Add(new CssDeclaration("text-decoration-line", line));
        }

        if (style is not null)
        {
            result.Add(new CssDeclaration("text-decoration-style", [style]));
        }

        if (color is not null)
        {
            result.Add(new CssDeclaration("text-decoration-color", [color]));
        }

        if (thickness is not null)
        {
            result.Add(new CssDeclaration("text-decoration-thickness", [thickness]));
        }

        return result;
    }
}
