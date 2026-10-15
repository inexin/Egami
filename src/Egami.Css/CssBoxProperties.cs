namespace Egami.Css;

/// <summary>
/// The box, background, list, table, float, overflow, fragmentation, page and generated-content properties
/// <see cref="CssPropertyRegistry.CreateDefault"/> registers, with each value's grammar and the shorthands' expansions.
/// </summary>
internal static class CssBoxProperties
{
    private static readonly string[] s_sides = ["top", "right", "bottom", "left"];
    private static readonly string[] s_corners = ["top-left", "top-right", "bottom-right", "bottom-left"];

    private static readonly string[] s_backgroundLonghands =
    [
        "background-image", "background-position", "background-size", "background-repeat", "background-attachment",
        "background-origin", "background-clip", "background-color",
    ];

    private static readonly CssComponentValue s_space = new CssTokenValue(new CssToken(CssTokenKind.Whitespace));
    private static readonly CssComponentValue s_comma = new CssTokenValue(new CssToken(CssTokenKind.Comma));

    public static void AddTo(CssPropertyRegistry registry)
    {
        AddBox(registry);
        AddBackgrounds(registry);
        AddLists(registry);
        AddTables(registry);
        AddFlow(registry);
        AddFragmentation(registry);
        AddPage(registry);
        AddGeneratedContent(registry);
    }

    private static void AddBox(CssPropertyRegistry registry)
    {
        foreach (string side in s_sides)
        {
            registry.Register(new("margin-" + side, false, "0") { Validate = v => One(v, IsMargin) });
            registry.Register(new("padding-" + side, false, "0") { Validate = v => One(v, IsPadding) });
            registry.Register(new("border-" + side + "-width", false, "medium") { Validate = v => One(v, IsLineWidth) });
            registry.Register(new("border-" + side + "-style", false, "none") { Validate = v => One(v, IsLineStyle) });
            registry.Register(new("border-" + side + "-color", false, "currentcolor") { Validate = v => One(v, IsColor) });
        }

        foreach (string corner in s_corners)
        {
            registry.Register(new("border-" + corner + "-radius", false, "0") { Validate = v => Items(v) is { Count: 1 or 2 } items && items.All(IsPadding) });
        }

        registry.RegisterShorthand(Sides("margin", "margin-*", IsMargin));
        registry.RegisterShorthand(Sides("padding", "padding-*", IsPadding));
        registry.RegisterShorthand(Sides("border-width", "border-*-width", IsLineWidth));
        registry.RegisterShorthand(Sides("border-style", "border-*-style", IsLineStyle));
        registry.RegisterShorthand(Sides("border-color", "border-*-color", IsColor));
        foreach (string side in s_sides)
        {
            string[] longhands = ["border-" + side + "-width", "border-" + side + "-style", "border-" + side + "-color"];
            registry.RegisterShorthand(new CssShorthandDefinition("border-" + side, longhands, v => ExpandBorder(v, [side])));
        }

        registry.RegisterShorthand(new CssShorthandDefinition(
            "border",
            [.. s_sides.SelectMany(s => (string[])["border-" + s + "-width", "border-" + s + "-style", "border-" + s + "-color"])],
            v => ExpandBorder(v, s_sides)));
        registry.RegisterShorthand(new CssShorthandDefinition("border-radius", [.. s_corners.Select(c => "border-" + c + "-radius")], ExpandBorderRadius));

        registry.Register(new("width", false, "auto") { Validate = v => One(v, i => i.IsIdent("auto") || IsSize(i)) });
        registry.Register(new("height", false, "auto") { Validate = v => One(v, i => i.IsIdent("auto") || IsSize(i)) });
        registry.Register(new("min-width", false, "auto") { Validate = v => One(v, i => i.IsIdent("auto") || IsSize(i)) });
        registry.Register(new("min-height", false, "auto") { Validate = v => One(v, i => i.IsIdent("auto") || IsSize(i)) });
        registry.Register(new("max-width", false, "none") { Validate = v => One(v, i => i.IsIdent("none") || IsSize(i)) });
        registry.Register(new("max-height", false, "none") { Validate = v => One(v, i => i.IsIdent("none") || IsSize(i)) });
        registry.Register(new("box-sizing", false, "content-box") { Validate = v => Keyword(v, "content-box", "border-box") });
    }

    private static void AddBackgrounds(CssPropertyRegistry registry)
    {
        registry.Register(new("background-color", false, "transparent") { Validate = v => One(v, IsColor) });
        registry.Register(new("background-image", false, "none") { Validate = v => Layers(v, l => l is [var only] && (only.IsIdent("none") || IsImage(only))) });
        registry.Register(new("background-position", false, "0% 0%") { Validate = v => Layers(v, IsPosition) });
        registry.Register(new("background-size", false, "auto") { Validate = v => Layers(v, IsBackgroundSize) });
        registry.Register(new("background-repeat", false, "repeat") { Validate = v => Layers(v, IsRepeat) });
        registry.Register(new("background-attachment", false, "scroll") { Validate = v => Layers(v, l => l is [var only] && Is(only, "scroll", "fixed", "local")) });
        registry.Register(new("background-origin", false, "padding-box") { Validate = v => Layers(v, l => l is [var only] && IsVisualBox(only)) });
        registry.Register(new("background-clip", false, "border-box") { Validate = v => Layers(v, l => l is [var only] && (IsVisualBox(only) || only.IsIdent("text"))) });
        registry.RegisterShorthand(new CssShorthandDefinition("background", s_backgroundLonghands, ExpandBackground));
    }

    private static void AddLists(CssPropertyRegistry registry)
    {
        registry.Register(new("list-style-type", true, "disc") { Validate = v => One(v, IsListStyleType) });
        registry.Register(new("list-style-position", true, "outside") { Validate = v => Keyword(v, "inside", "outside") });
        registry.Register(new("list-style-image", true, "none") { Validate = v => One(v, i => i.IsIdent("none") || IsImage(i)) });
        registry.RegisterShorthand(new CssShorthandDefinition("list-style", ["list-style-position", "list-style-image", "list-style-type"], ExpandListStyle));
    }

    private static void AddTables(CssPropertyRegistry registry)
    {
        registry.Register(new("border-collapse", true, "separate") { Validate = v => Keyword(v, "separate", "collapse") });
        registry.Register(new("border-spacing", true, "0") { Validate = v => Items(v) is { Count: 1 or 2 } items && items.All(i => IsLength(i, negative: false)) });
        registry.Register(new("caption-side", true, "top") { Validate = v => Keyword(v, "top", "bottom") });
        registry.Register(new("empty-cells", true, "show") { Validate = v => Keyword(v, "show", "hide") });
        registry.Register(new("table-layout", false, "auto") { Validate = v => Keyword(v, "auto", "fixed") });
        registry.Register(new("vertical-align", false, "baseline")
        {
            Validate = v => One(v, i => Is(i, "baseline", "sub", "super", "text-top", "text-bottom", "middle", "top", "bottom") || IsLengthPercentage(i, negative: true)),
        });
    }

    private static void AddFlow(CssPropertyRegistry registry)
    {
        registry.Register(new("float", false, "none") { Validate = v => Keyword(v, "none", "left", "right", "inline-start", "inline-end") });
        registry.Register(new("clear", false, "none") { Validate = v => Keyword(v, "none", "left", "right", "both", "inline-start", "inline-end") });
        registry.Register(new("overflow-x", false, "visible") { Validate = v => One(v, IsOverflow) });
        registry.Register(new("overflow-y", false, "visible") { Validate = v => One(v, IsOverflow) });
        registry.RegisterShorthand(new CssShorthandDefinition("overflow", ["overflow-x", "overflow-y"], v =>
            Items(v) switch
            {
                [var both] when IsOverflow(both) => [new CssDeclaration("overflow-x", [both]), new CssDeclaration("overflow-y", [both])],
                [var x, var y] when IsOverflow(x) && IsOverflow(y) => [new CssDeclaration("overflow-x", [x]), new CssDeclaration("overflow-y", [y])],
                _ => null,
            }));
    }

    private static void AddFragmentation(CssPropertyRegistry registry)
    {
        string[] between =
        [
            "auto", "avoid", "always", "all", "avoid-page", "page", "left", "right", "recto", "verso", "avoid-column", "column", "avoid-region", "region",
        ];
        registry.Register(new("break-before", false, "auto") { Validate = v => Keyword(v, between) });
        registry.Register(new("break-after", false, "auto") { Validate = v => Keyword(v, between) });
        registry.Register(new("break-inside", false, "auto") { Validate = v => Keyword(v, "auto", "avoid", "avoid-page", "avoid-column", "avoid-region") });
        registry.Register(new("orphans", true, "2") { Validate = IsPositiveInteger });
        registry.Register(new("widows", true, "2") { Validate = IsPositiveInteger });
        registry.Register(new("box-decoration-break", false, "slice") { Validate = v => Keyword(v, "slice", "clone") });
        registry.Register(new("page", false, "auto") { Validate = v => One(v, i => i is CssTokenValue { Token.Kind: CssTokenKind.Ident } && !IsCssWide(i)) });

        // The legacy page-break-* properties are shorthands (aliases) of break-* (CSS Fragmentation 3 section 3.1).
        registry.RegisterShorthand(new CssShorthandDefinition("page-break-before", ["break-before"], v => LegacyPageBreak(v, "break-before")));
        registry.RegisterShorthand(new CssShorthandDefinition("page-break-after", ["break-after"], v => LegacyPageBreak(v, "break-after")));
        registry.RegisterShorthand(new CssShorthandDefinition("page-break-inside", ["break-inside"], v =>
            Keyword(v, "auto", "avoid") ? [new CssDeclaration("break-inside", v)] : null));
    }

    private static void AddPage(CssPropertyRegistry registry)
    {
        // Descriptors of the page context (CSS Paged Media 3), cascaded like properties.
        registry.Register(new("size", false, "auto") { Validate = v => CssPageSize.TryParse(v, out _) });
        registry.Register(new("page-orientation", false, "upright") { Validate = v => Keyword(v, "upright", "rotate-left", "rotate-right") });
        registry.Register(new("marks", false, "none") { Validate = IsMarks });
        registry.Register(new("bleed", false, "auto") { Validate = v => One(v, i => i.IsIdent("auto") || IsLength(i, negative: true)) });
    }

    private static void AddGeneratedContent(CssPropertyRegistry registry)
    {
        // Values the consumer reads: strings, counter(), counters(), attr(), url() and quotes in content; names and integers in the counters.
        registry.Register(new("content", false, "normal"));
        registry.Register(new("counter-reset", false, "none") { Validate = v => IsCounterList(v, reset: true) });
        registry.Register(new("counter-increment", false, "none") { Validate = v => IsCounterList(v, reset: false) });
        registry.Register(new("counter-set", false, "none") { Validate = v => IsCounterList(v, reset: false) });
    }

    // Grammar helpers.
    internal static List<CssComponentValue> Items(IReadOnlyList<CssComponentValue> value) => [.. value.Where(v => !v.IsWhitespace)];

    private static bool One(IReadOnlyList<CssComponentValue> value, Func<CssComponentValue, bool> valid) => CssLength.Single(value) is { } single && valid(single);

    private static bool Keyword(IReadOnlyList<CssComponentValue> value, params ReadOnlySpan<string> keywords) => CssLength.Single(value) is { } single && Is(single, keywords);

    private static bool Is(CssComponentValue value, params ReadOnlySpan<string> keywords)
    {
        if (value is not CssTokenValue { Token: { Kind: CssTokenKind.Ident } token })
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

    private static bool IsCssWide(CssComponentValue value) => Is(value, "inherit", "initial", "unset", "revert", "revert-layer", "default");

    internal static bool IsLengthPercentage(CssComponentValue value, bool negative) => IsLengthOrPercentage(value, negative, percent: true);

    internal static bool IsLength(CssComponentValue value, bool negative) => IsLengthOrPercentage(value, negative, percent: false);

    private static bool IsLengthOrPercentage(CssComponentValue value, bool negative, bool percent)
    {
        if (value is CssFunction)
        {
            return CssCalc.TryParse(value, out CssCalc? calc) && !calc.IsNumber && (percent || !calc.HasPercentage);
        }

        if (!CssLength.TryParse(value, out CssLength length) || double.IsNaN(length.Value) || double.IsInfinity(length.Value))
        {
            return false;
        }

        if (length.Unit == CssUnit.None)
        {
            return length.Value == 0;
        }

        return (percent || length.Unit != CssUnit.Percent) && (negative || length.Value >= 0);
    }

    private static bool IsMargin(CssComponentValue value) => value.IsIdent("auto") || IsLengthPercentage(value, negative: true);

    private static bool IsPadding(CssComponentValue value) => IsLengthPercentage(value, negative: false);

    private static bool IsLineWidth(CssComponentValue value) => Is(value, "thin", "medium", "thick") || IsLength(value, negative: false);

    private static bool IsLineStyle(CssComponentValue value) => Is(value, "none", "hidden", "dotted", "dashed", "solid", "double", "groove", "ridge", "inset", "outset");

    private static bool IsColor(CssComponentValue value) => CssColor.TryParse(value, out _);

    private static bool IsOverflow(CssComponentValue value) => Is(value, "visible", "hidden", "clip", "scroll", "auto");

    private static bool IsVisualBox(CssComponentValue value) => Is(value, "border-box", "padding-box", "content-box");

    private static bool IsSize(CssComponentValue value)
    {
        if (Is(value, "min-content", "max-content", "fit-content") || IsLengthPercentage(value, negative: false))
        {
            return true;
        }

        return value is CssFunction function && function.NameIs("fit-content") && CssLength.Single(function.Arguments) is { } argument && IsLengthPercentage(argument, negative: false);
    }

    private static bool IsPositiveInteger(IReadOnlyList<CssComponentValue> value) =>
        CssLength.Single(value) is CssTokenValue { Token: { Kind: CssTokenKind.Number, IsInteger: true } token } && token.Number >= 1;

    internal static bool IsImage(CssComponentValue value)
    {
        if (CssUrl.TryParse(value, out _))
        {
            return true;
        }

        if (value is not CssFunction function)
        {
            return false;
        }

        string name = CssText.ToAsciiLower(function.Name);
        return name.EndsWith("gradient", StringComparison.Ordinal) || name is "image" or "image-set" or "-webkit-image-set" or "cross-fade" or "element";
    }

    private static bool IsListStyleType(CssComponentValue value) =>
        (value is CssTokenValue { Token.Kind: CssTokenKind.Ident } && !IsCssWide(value))
        || value.IsToken(CssTokenKind.String)
        || (value is CssFunction function && function.NameIs("symbols"));

    private static bool IsMarks(IReadOnlyList<CssComponentValue> value)
    {
        List<CssComponentValue> items = Items(value);
        return items switch
        {
            [var only] => Is(only, "none", "crop", "cross"),
            [var a, var b] => (a.IsIdent("crop") && b.IsIdent("cross")) || (a.IsIdent("cross") && b.IsIdent("crop")),
            _ => false,
        };
    }

    // none | [ <counter-name> <integer>? ]+, where counter-reset also takes reversed(<counter-name>).
    private static bool IsCounterList(IReadOnlyList<CssComponentValue> value, bool reset)
    {
        List<CssComponentValue> items = Items(value);
        if (items is [var only] && only.IsIdent("none"))
        {
            return true;
        }

        bool afterName = false;
        foreach (CssComponentValue item in items)
        {
            if (item is CssTokenValue { Token.Kind: CssTokenKind.Ident } && !IsCssWide(item) && !item.IsIdent("none"))
            {
                afterName = true;
            }
            else if (reset && item is CssFunction { } function && function.NameIs("reversed") && CssLength.Single(function.Arguments) is CssTokenValue { Token.Kind: CssTokenKind.Ident })
            {
                afterName = true;
            }
            else if (afterName && item is CssTokenValue { Token: { Kind: CssTokenKind.Number, IsInteger: true } })
            {
                afterName = false;
            }
            else
            {
                return false;
            }
        }

        return items.Count > 0;
    }

    // <bg-position>: one or two values, or three or four with keyword offsets (CSS Backgrounds 3 section 3.6).
    internal static bool IsPosition(List<CssComponentValue> items)
    {
        switch (items.Count)
        {
            case 1:
                return Is(items[0], "left", "right", "top", "bottom", "center") || IsLengthPercentage(items[0], negative: true);
            case 2:
                CssComponentValue a = items[0], b = items[1];
                if ((Is(a, "left", "right", "center") || IsLengthPercentage(a, negative: true)) && (Is(b, "top", "bottom", "center") || IsLengthPercentage(b, negative: true)))
                {
                    return true;
                }

                return Is(a, "top", "bottom", "center") && Is(b, "left", "right", "center");
            case 3 or 4:
                int i = 0;
                int first = Group(items, ref i);
                int second = Group(items, ref i);
                if (first < 0 || second < 0 || i != items.Count)
                {
                    return false;
                }

                // 1 = horizontal, 2 = vertical, 3 = center (either); the two groups need different axes.
                return first == 3 || second == 3 || first != second;
            default:
                return false;
        }

        // A keyword and, unless it is center, an optional offset: returns the axis (1, 2 or 3 for center), or -1.
        static int Group(List<CssComponentValue> items, ref int i)
        {
            if (i >= items.Count)
            {
                return -1;
            }

            CssComponentValue keyword = items[i++];
            int axis = Is(keyword, "left", "right") ? 1 : Is(keyword, "top", "bottom") ? 2 : keyword.IsIdent("center") ? 3 : -1;
            if (axis is 1 or 2 && i < items.Count && IsLengthPercentage(items[i], negative: true))
            {
                i++;
            }

            return axis;
        }
    }

    private static bool IsBackgroundSize(List<CssComponentValue> items) => items switch
    {
        [var only] => Is(only, "cover", "contain", "auto") || IsLengthPercentage(only, negative: false),
        [var w, var h] => (w.IsIdent("auto") || IsLengthPercentage(w, negative: false)) && (h.IsIdent("auto") || IsLengthPercentage(h, negative: false)),
        _ => false,
    };

    private static bool IsRepeat(List<CssComponentValue> items) => items switch
    {
        [var only] => Is(only, "repeat-x", "repeat-y", "repeat", "space", "round", "no-repeat"),
        [var x, var y] => Is(x, "repeat", "space", "round", "no-repeat") && Is(y, "repeat", "space", "round", "no-repeat"),
        _ => false,
    };

    // A comma-separated list whose every item passes, such as the background-* longhands' layers.
    private static bool Layers(IReadOnlyList<CssComponentValue> value, Func<List<CssComponentValue>, bool> valid) =>
        SplitCommas(value) is { } layers && layers.All(valid);

    // The items between commas (whitespace dropped), or null when an item is empty.
    private static List<List<CssComponentValue>>? SplitCommas(IReadOnlyList<CssComponentValue> value)
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

        return parts.Any(p => p.Count == 0) ? null : parts;
    }

    private static List<CssComponentValue> Spaced(IEnumerable<CssComponentValue> items)
    {
        List<CssComponentValue> result = [];
        foreach (CssComponentValue item in items)
        {
            if (result.Count > 0)
            {
                result.Add(s_space);
            }

            result.Add(item);
        }

        return result;
    }

    private static List<CssComponentValue> CommaJoined(List<List<CssComponentValue>> layers)
    {
        List<CssComponentValue> result = [];
        foreach (List<CssComponentValue> layer in layers)
        {
            if (result.Count > 0)
            {
                result.Add(s_comma);
                result.Add(s_space);
            }

            result.AddRange(layer);
        }

        return result;
    }

    private static List<CssComponentValue> Parse(string value) => CssSyntax.Trim(CssSyntax.ParseComponentValues(value, CssParseOptions.Default));

    // Shorthand expansions.

    // A four-sides shorthand: one to four values for top, right, bottom and left.
    private static CssShorthandDefinition Sides(string name, string pattern, Func<CssComponentValue, bool> valid)
    {
        string[] longhands = [.. s_sides.Select(s => pattern.Replace("*", s, StringComparison.Ordinal))];
        return new CssShorthandDefinition(name, longhands, value =>
        {
            List<CssComponentValue> items = Items(value);
            if (items.Count is < 1 or > 4 || !items.All(valid))
            {
                return null;
            }

            CssComponentValue[] four = FourSides(items);
            return [.. longhands.Select((longhand, i) => new CssDeclaration(longhand, [four[i]]))];
        });
    }

    private static T[] FourSides<T>(List<T> items) => items.Count switch
    {
        1 => [items[0], items[0], items[0], items[0]],
        2 => [items[0], items[1], items[0], items[1]],
        3 => [items[0], items[1], items[2], items[1]],
        _ => [items[0], items[1], items[2], items[3]],
    };

    // <line-width> || <line-style> || <color> for the sides given; what is left out takes its initial value.
    private static List<CssDeclaration>? ExpandBorder(IReadOnlyList<CssComponentValue> value, string[] sides)
    {
        List<CssComponentValue> items = Items(value);
        CssComponentValue? width = null, style = null, color = null;
        if (items.Count is < 1 or > 3)
        {
            return null;
        }

        foreach (CssComponentValue item in items)
        {
            if (width is null && IsLineWidth(item))
            {
                width = item;
            }
            else if (style is null && IsLineStyle(item))
            {
                style = item;
            }
            else if (color is null && IsColor(item))
            {
                color = item;
            }
            else
            {
                return null;
            }
        }

        List<CssDeclaration> result = [];
        foreach (string side in sides)
        {
            if (width is not null)
            {
                result.Add(new CssDeclaration("border-" + side + "-width", [width]));
            }

            if (style is not null)
            {
                result.Add(new CssDeclaration("border-" + side + "-style", [style]));
            }

            if (color is not null)
            {
                result.Add(new CssDeclaration("border-" + side + "-color", [color]));
            }
        }

        return result;
    }

    // <length-percentage [0,∞]>{1,4} [ / <length-percentage [0,∞]>{1,4} ]?
    private static List<CssDeclaration>? ExpandBorderRadius(IReadOnlyList<CssComponentValue> value)
    {
        List<CssComponentValue> items = Items(value);
        int slash = items.FindIndex(i => i.IsDelim('/'));
        List<CssComponentValue> horizontal = slash < 0 ? items : items[..slash];
        List<CssComponentValue>? vertical = slash < 0 ? null : items[(slash + 1)..];
        if (horizontal.Count is < 1 or > 4 || !horizontal.All(IsPadding) || (vertical is not null && (vertical.Count is < 1 or > 4 || !vertical.All(IsPadding))))
        {
            return null;
        }

        CssComponentValue[] h = FourSides(horizontal);
        CssComponentValue[]? v = vertical is null ? null : FourSides(vertical);
        return [.. s_corners.Select((corner, i) => new CssDeclaration("border-" + corner + "-radius", v is null ? [h[i]] : [h[i], s_space, v[i]]))];
    }

    private static List<CssDeclaration>? LegacyPageBreak(IReadOnlyList<CssComponentValue> value, string longhand)
    {
        if (!Keyword(value, "auto", "always", "avoid", "left", "right"))
        {
            return null;
        }

        return [new CssDeclaration(longhand, CssLength.Single(value)!.IsIdent("always") ? [new CssTokenValue(new CssToken(CssTokenKind.Ident, "page"))] : value)];
    }

    // <'list-style-position'> || <'list-style-image'> || <'list-style-type'>, where none sets whichever of image and type is
    // not otherwise given (CSS Lists 3 section 3.4).
    private static List<CssDeclaration>? ExpandListStyle(IReadOnlyList<CssComponentValue> value)
    {
        List<CssComponentValue> items = Items(value);
        CssComponentValue? position = null, image = null, type = null;
        int nones = 0;
        foreach (CssComponentValue item in items)
        {
            if (item.IsIdent("none"))
            {
                nones++;
            }
            else if (position is null && Is(item, "inside", "outside"))
            {
                position = item;
            }
            else if (image is null && IsImage(item))
            {
                image = item;
            }
            else if (type is null && IsListStyleType(item))
            {
                type = item;
            }
            else
            {
                return null;
            }
        }

        if (items.Count == 0 || nones > 2 || (nones == 2 && (image is not null || type is not null)) || (nones == 1 && image is not null && type is not null))
        {
            return null;
        }

        CssComponentValue none = new CssTokenValue(new CssToken(CssTokenKind.Ident, "none"));
        if (nones == 2 || (nones == 1 && image is null && type is null))
        {
            image = none;
            type = none;
        }
        else if (nones == 1)
        {
            image ??= none;
            type ??= none;
        }

        List<CssDeclaration> result = [];
        if (position is not null)
        {
            result.Add(new CssDeclaration("list-style-position", [position]));
        }

        if (image is not null)
        {
            result.Add(new CssDeclaration("list-style-image", [image]));
        }

        if (type is not null)
        {
            result.Add(new CssDeclaration("list-style-type", [type]));
        }

        return result;
    }

    // <bg-layer>#, <final-bg-layer> (CSS Backgrounds 3 section 3.10): each longhand gets one comma-separated item per layer.
    private static List<CssDeclaration>? ExpandBackground(IReadOnlyList<CssComponentValue> value)
    {
        if (SplitCommas(value) is not { } layers)
        {
            return null;
        }

        List<List<CssComponentValue>> images = [], positions = [], sizes = [], repeats = [], attachments = [], origins = [], clips = [];
        CssComponentValue? color = null;
        for (int layerIndex = 0; layerIndex < layers.Count; layerIndex++)
        {
            List<CssComponentValue> layer = layers[layerIndex];
            bool last = layerIndex == layers.Count - 1;
            List<CssComponentValue>? image = null, position = null, size = null, repeat = null, attachment = null;
            CssComponentValue? origin = null, clip = null;
            int i = 0;
            while (i < layer.Count)
            {
                CssComponentValue item = layer[i];
                if (image is null && (item.IsIdent("none") || IsImage(item)))
                {
                    image = [item];
                    i++;
                    continue;
                }

                if (position is null && TakeLongest(layer, ref i, 4, IsPosition) is { } p)
                {
                    position = p;
                    if (i < layer.Count && layer[i].IsDelim('/'))
                    {
                        i++;
                        size = TakeLongest(layer, ref i, 2, IsBackgroundSize);
                        if (size is null)
                        {
                            return null;
                        }
                    }

                    continue;
                }

                if (repeat is null && TakeLongest(layer, ref i, 2, IsRepeat) is { } r)
                {
                    repeat = r;
                    continue;
                }

                if (attachment is null && Is(item, "scroll", "fixed", "local"))
                {
                    attachment = [item];
                    i++;
                    continue;
                }

                if (IsVisualBox(item) && clip is null)
                {
                    if (origin is null)
                    {
                        origin = item;
                    }
                    else
                    {
                        clip = item;
                    }

                    i++;
                    continue;
                }

                if (last && color is null && IsColor(item))
                {
                    color = item;
                    i++;
                    continue;
                }

                return null;
            }

            images.Add(image ?? Parse("none"));
            positions.Add(position is null ? Parse("0% 0%") : Spaced(position));
            sizes.Add(size is null ? Parse("auto") : Spaced(size));
            repeats.Add(repeat is null ? Parse("repeat") : Spaced(repeat));
            attachments.Add(attachment ?? Parse("scroll"));
            origins.Add(origin is null ? Parse("padding-box") : [origin]);
            clips.Add(clip is not null ? [clip] : origin is not null ? [origin] : Parse("border-box"));
        }

        return
        [
            new CssDeclaration("background-image", CommaJoined(images)),
            new CssDeclaration("background-position", CommaJoined(positions)),
            new CssDeclaration("background-size", CommaJoined(sizes)),
            new CssDeclaration("background-repeat", CommaJoined(repeats)),
            new CssDeclaration("background-attachment", CommaJoined(attachments)),
            new CssDeclaration("background-origin", CommaJoined(origins)),
            new CssDeclaration("background-clip", CommaJoined(clips)),
            new CssDeclaration("background-color", color is null ? Parse("transparent") : [color]),
        ];
    }

    // The longest run of at most 'max' items from i that passes, consumed; null when none does.
    private static List<CssComponentValue>? TakeLongest(List<CssComponentValue> items, ref int i, int max, Func<List<CssComponentValue>, bool> valid)
    {
        for (int count = Math.Min(max, items.Count - i); count >= 1; count--)
        {
            List<CssComponentValue> run = items.GetRange(i, count);
            if (valid(run))
            {
                i += count;
                return run;
            }
        }

        return null;
    }
}
