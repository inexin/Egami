using System.Globalization;

namespace Egami.Css;

/// <summary>The page pseudo-classes a page selector tests (CSS Paged Media 3 section 4.3).</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Css.cs" region="page"/>
/// </example>
[Flags]
public enum CssPagePseudoClasses
{
    /// <summary>No pseudo-class.</summary>
    None = 0,

    /// <summary><c>:first</c>: the document's first page.</summary>
    First = 1,

    /// <summary><c>:left</c>: a left (verso) page.</summary>
    Left = 2,

    /// <summary><c>:right</c>: a right (recto) page.</summary>
    Right = 4,

    /// <summary><c>:blank</c>: a page left empty by a forced break, such as the page before <c>break-before: right</c>.</summary>
    Blank = 8,
}

/// <summary>The sixteen page-margin boxes around a page's content (CSS Paged Media 3 section 5.1), clockwise from the top left.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Css.cs" region="page"/>
/// </example>
public enum CssMarginBox
{
    /// <summary><c>@top-left-corner</c>.</summary>
    TopLeftCorner,

    /// <summary><c>@top-left</c>.</summary>
    TopLeft,

    /// <summary><c>@top-center</c>.</summary>
    TopCenter,

    /// <summary><c>@top-right</c>.</summary>
    TopRight,

    /// <summary><c>@top-right-corner</c>.</summary>
    TopRightCorner,

    /// <summary><c>@right-top</c>.</summary>
    RightTop,

    /// <summary><c>@right-middle</c>.</summary>
    RightMiddle,

    /// <summary><c>@right-bottom</c>.</summary>
    RightBottom,

    /// <summary><c>@bottom-right-corner</c>.</summary>
    BottomRightCorner,

    /// <summary><c>@bottom-right</c>.</summary>
    BottomRight,

    /// <summary><c>@bottom-center</c>.</summary>
    BottomCenter,

    /// <summary><c>@bottom-left</c>.</summary>
    BottomLeft,

    /// <summary><c>@bottom-left-corner</c>.</summary>
    BottomLeftCorner,

    /// <summary><c>@left-bottom</c>.</summary>
    LeftBottom,

    /// <summary><c>@left-middle</c>.</summary>
    LeftMiddle,

    /// <summary><c>@left-top</c>.</summary>
    LeftTop,
}

/// <summary>
/// A page being laid out, as page selectors see it: its page type (the <c>page</c> property of the content that starts it)
/// and whether it is the first, a left or a blank page.
/// </summary>
/// <param name="Name">The page type name, or <see langword="null"/> for an unnamed page (<c>page: auto</c> throughout).</param>
/// <param name="IsFirst">Whether this is the document's first page.</param>
/// <param name="IsLeft">Whether this is a left page; otherwise a right page.</param>
/// <param name="IsBlank">Whether the page has no content because a forced break skipped it.</param>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Css.cs" region="page"/>
/// </example>
public readonly record struct CssPage(string? Name, bool IsFirst, bool IsLeft, bool IsBlank = false);

/// <summary>One selector of an <c>@page</c> rule, such as <c>chapter:first</c>.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Css.cs" region="page"/>
/// </example>
public sealed class CssPageSelector
{
    private readonly string _text;

    internal CssPageSelector(string? name, CssPagePseudoClasses pseudoClasses, CssSpecificity specificity, string text)
    {
        Name = name;
        PseudoClasses = pseudoClasses;
        Specificity = specificity;
        _text = text;
    }

    /// <summary>Gets the page type name, case-sensitive, or <see langword="null"/> when the selector names none.</summary>
    public string? Name { get; }

    /// <summary>Gets the pseudo-classes the selector tests.</summary>
    public CssPagePseudoClasses PseudoClasses { get; }

    /// <summary>
    /// Gets the specificity: one for a page type name, then the number of <c>:first</c> and <c>:blank</c>, then the number of
    /// <c>:left</c> and <c>:right</c>.
    /// </summary>
    public CssSpecificity Specificity { get; }

    /// <summary>Tests the selector against a page.</summary>
    /// <param name="page">The page.</param>
    /// <returns>True when the page matches.</returns>
    public bool Matches(CssPage page)
    {
        return (Name is null || string.Equals(Name, page.Name, StringComparison.Ordinal))
            && (!PseudoClasses.HasFlag(CssPagePseudoClasses.First) || page.IsFirst)
            && (!PseudoClasses.HasFlag(CssPagePseudoClasses.Left) || page.IsLeft)
            && (!PseudoClasses.HasFlag(CssPagePseudoClasses.Right) || !page.IsLeft)
            && (!PseudoClasses.HasFlag(CssPagePseudoClasses.Blank) || page.IsBlank);
    }

    /// <summary>Gets the selector as CSS text.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => _text;

    // <page-selector-list>: an empty prelude (every page) or page selectors separated by commas; null when invalid.
    internal static List<CssPageSelector>? TryParseList(List<CssComponentValue> prelude)
    {
        List<CssPageSelector> selectors = [];
        if (prelude.Count == 0)
        {
            return selectors;
        }

        List<CssComponentValue> current = [];
        for (int i = 0; i <= prelude.Count; i++)
        {
            if (i < prelude.Count && !prelude[i].IsToken(CssTokenKind.Comma))
            {
                current.Add(prelude[i]);
                continue;
            }

            if (TryParse(CssSyntax.Trim(current)) is not { } selector)
            {
                return null;
            }

            selectors.Add(selector);
            current.Clear();
        }

        return selectors;
    }

    // <page-selector> = [ <ident-token>? <pseudo-page>* ]!, with no whitespace inside.
    private static CssPageSelector? TryParse(List<CssComponentValue> items)
    {
        if (items.Count == 0)
        {
            return null;
        }

        int i = 0;
        string? name = null;
        if (items[0] is CssTokenValue { Token: { Kind: CssTokenKind.Ident } ident })
        {
            name = ident.Value;
            i = 1;
        }

        CssPagePseudoClasses pseudoClasses = CssPagePseudoClasses.None;
        int firstOrBlank = 0, leftOrRight = 0;
        for (; i < items.Count; i += 2)
        {
            if (!items[i].IsToken(CssTokenKind.Colon) || i + 1 >= items.Count || items[i + 1] is not CssTokenValue { Token: { Kind: CssTokenKind.Ident } pseudo })
            {
                return null;
            }

            switch (CssText.ToAsciiLower(pseudo.Value))
            {
                case "first":
                    pseudoClasses |= CssPagePseudoClasses.First;
                    firstOrBlank++;
                    break;
                case "blank":
                    pseudoClasses |= CssPagePseudoClasses.Blank;
                    firstOrBlank++;
                    break;
                case "left":
                    pseudoClasses |= CssPagePseudoClasses.Left;
                    leftOrRight++;
                    break;
                case "right":
                    pseudoClasses |= CssPagePseudoClasses.Right;
                    leftOrRight++;
                    break;
                default:
                    return null;
            }
        }

        return new CssPageSelector(name, pseudoClasses, new CssSpecificity(name is null ? 0 : 1, firstOrBlank, leftOrRight), CssComponentValue.Serialize(items));
    }
}

/// <summary>A page-margin rule inside <c>@page</c>, such as <c>@bottom-center { content: counter(page) }</c>.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Css.cs" region="page"/>
/// </example>
public sealed class CssPageMarginRule : CssRule
{
    internal CssPageMarginRule(CssMarginBox box, IReadOnlyList<CssDeclaration> declarations)
    {
        Box = box;
        Declarations = declarations;
    }

    /// <summary>Gets the margin box the rule styles.</summary>
    public CssMarginBox Box { get; }

    /// <summary>Gets the declarations, in order.</summary>
    public IReadOnlyList<CssDeclaration> Declarations { get; }

    private static readonly string[] s_names =
    [
        "top-left-corner", "top-left", "top-center", "top-right", "top-right-corner", "right-top", "right-middle", "right-bottom",
        "bottom-right-corner", "bottom-right", "bottom-center", "bottom-left", "bottom-left-corner", "left-bottom", "left-middle", "left-top",
    ];

    /// <summary>Gets the at-rule name of a margin box without the <c>@</c>, such as <c>top-center</c>.</summary>
    /// <param name="box">The margin box.</param>
    /// <returns>The name.</returns>
    public static string GetName(CssMarginBox box) => (uint)box < (uint)s_names.Length ? s_names[(int)box] : throw new ArgumentOutOfRangeException(nameof(box));

    internal static bool TryGetBox(string lowercaseName, out CssMarginBox box)
    {
        int index = Array.IndexOf(s_names, lowercaseName);
        box = (CssMarginBox)Math.Max(index, 0);
        return index >= 0;
    }
}

/// <summary>
/// An <c>@page</c> rule (CSS Paged Media 3): page selectors, the declarations for the page context (<c>size</c>,
/// <c>margin</c>, <c>padding</c>, <c>border</c>, <c>background</c>, inherited text properties) and its page-margin rules.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Css.cs" region="page"/>
/// </example>
public sealed class CssPageRule : CssRule
{
    internal CssPageRule(IReadOnlyList<CssPageSelector> selectors, IReadOnlyList<CssDeclaration> declarations, IReadOnlyList<CssPageMarginRule> marginRules)
    {
        Selectors = selectors;
        Declarations = declarations;
        MarginRules = marginRules;
    }

    /// <summary>Gets the page selectors; an empty list matches every page.</summary>
    public IReadOnlyList<CssPageSelector> Selectors { get; }

    /// <summary>Gets the selectors as CSS text (empty for every page).</summary>
    public string SelectorText => string.Join(", ", Selectors);

    /// <summary>Gets the declarations for the page context, in order.</summary>
    public IReadOnlyList<CssDeclaration> Declarations { get; }

    /// <summary>Gets the page-margin rules, in order.</summary>
    public IReadOnlyList<CssPageMarginRule> MarginRules { get; }

    // The highest specificity among the selectors that match the page, or null when none matches.
    internal CssSpecificity? Match(CssPage page)
    {
        if (Selectors.Count == 0)
        {
            return default(CssSpecificity);
        }

        CssSpecificity? best = null;
        foreach (CssPageSelector selector in Selectors)
        {
            if (selector.Matches(page) && (best is null || selector.Specificity.CompareTo(best.Value) > 0))
            {
                best = selector.Specificity;
            }
        }

        return best;
    }
}

/// <summary>
/// The cascaded style of one page: the page context's winning declarations and those of each page-margin box any rule
/// styles. Compute the page context with the root element's computed style as its parent, and each margin box with the page
/// context's computed style as its parent.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Css.cs" region="page"/>
/// </example>
public sealed class CssPageStyle
{
    internal CssPageStyle(CssCascadedStyle page, IReadOnlyDictionary<CssMarginBox, CssCascadedStyle> marginBoxes)
    {
        Page = page;
        MarginBoxes = marginBoxes;
    }

    /// <summary>Gets the page context's cascaded style.</summary>
    public CssCascadedStyle Page { get; }

    /// <summary>
    /// Gets the cascaded style of each margin box a matching rule styles. A box is generated only when its computed
    /// <c>content</c> is not <c>none</c> or <c>normal</c>; its default alignment (CSS Paged Media 3 section 5.3) is the consumer's.
    /// </summary>
    public IReadOnlyDictionary<CssMarginBox, CssCascadedStyle> MarginBoxes { get; }
}

/// <summary>Whether a page size asks for portrait or landscape pages.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Css.cs" region="page"/>
/// </example>
public enum CssPageOrientation
{
    /// <summary>No orientation given.</summary>
    Auto,

    /// <summary><c>portrait</c>: the shorter side across.</summary>
    Portrait,

    /// <summary><c>landscape</c>: the longer side across.</summary>
    Landscape,
}

/// <summary>
/// The <c>size</c> descriptor of <c>@page</c> (CSS Paged Media 3 section 7.1): <c>auto</c>, one or two lengths, or a page size
/// name such as <c>A4</c> or <c>letter</c> with an optional orientation.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Css.cs" region="page"/>
/// </example>
public readonly record struct CssPageSize
{
    private static readonly Dictionary<string, (double Width, double Height, CssUnit Unit)> s_sizes = new(StringComparer.Ordinal)
    {
        ["a5"] = (148, 210, CssUnit.Mm),
        ["a4"] = (210, 297, CssUnit.Mm),
        ["a3"] = (297, 420, CssUnit.Mm),
        ["b5"] = (176, 250, CssUnit.Mm),
        ["b4"] = (250, 353, CssUnit.Mm),
        ["jis-b5"] = (182, 257, CssUnit.Mm),
        ["jis-b4"] = (257, 364, CssUnit.Mm),
        ["letter"] = (8.5, 11, CssUnit.In),
        ["legal"] = (8.5, 14, CssUnit.In),
        ["ledger"] = (11, 17, CssUnit.In),
    };

    /// <summary>Gets the page width, or <see langword="null"/> when the size leaves it to the user agent (<c>auto</c>, or an orientation alone).</summary>
    public CssLength? Width { get; init; }

    /// <summary>Gets the page height, or <see langword="null"/> when <see cref="Width"/> is.</summary>
    public CssLength? Height { get; init; }

    /// <summary>Gets the page size name in lowercase, such as <c>a4</c>, or <see langword="null"/>.</summary>
    public string? Name { get; init; }

    /// <summary>
    /// Gets the orientation given. With a size it is already applied to <see cref="Width"/> and <see cref="Height"/>; alone it
    /// asks the user agent's default size to be turned.
    /// </summary>
    public CssPageOrientation Orientation { get; init; }

    /// <summary>Gets whether the user agent picks the size (no width and height given).</summary>
    public bool IsAuto => Width is null;

    /// <summary>Gets the page size names, in lowercase.</summary>
    public static IEnumerable<string> Names => s_sizes.Keys;

    /// <summary>Parses a <c>size</c> value, such as <c>A4 landscape</c> or <c>8.5in 11in</c>.</summary>
    /// <param name="text">The CSS text.</param>
    /// <param name="size">The page size.</param>
    /// <returns>True when the text is a valid <c>size</c> value.</returns>
    public static bool TryParse(string text, out CssPageSize size)
    {
        ArgumentNullException.ThrowIfNull(text);
        return TryParse(CssSyntax.ParseComponentValues(text, CssParseOptions.Default), out size);
    }

    /// <summary>Parses a <c>size</c> value from component values.</summary>
    /// <param name="values">The component values.</param>
    /// <param name="size">The page size.</param>
    /// <returns>True when the values are a valid <c>size</c> value.</returns>
    public static bool TryParse(IReadOnlyList<CssComponentValue> values, out CssPageSize size)
    {
        ArgumentNullException.ThrowIfNull(values);
        size = default;
        List<CssComponentValue> items = CssBoxProperties.Items(values);
        if (items is [var only] && only.IsIdent("auto"))
        {
            return true;
        }

        if (items.Count is < 1 or > 2)
        {
            return false;
        }

        if (TryLength(items[0], out CssLength first))
        {
            CssLength second = first;
            if (items.Count == 2 && !TryLength(items[1], out second))
            {
                return false;
            }

            size = new CssPageSize { Width = first, Height = second };
            return true;
        }

        string? name = null;
        CssPageOrientation orientation = CssPageOrientation.Auto;
        foreach (CssComponentValue item in items)
        {
            if (item is not CssTokenValue { Token: { Kind: CssTokenKind.Ident } ident })
            {
                return false;
            }

            string word = CssText.ToAsciiLower(ident.Value);
            if (orientation == CssPageOrientation.Auto && word is "portrait" or "landscape")
            {
                orientation = word == "portrait" ? CssPageOrientation.Portrait : CssPageOrientation.Landscape;
            }
            else if (name is null && s_sizes.ContainsKey(word))
            {
                name = word;
            }
            else
            {
                return false;
            }
        }

        if (name is null)
        {
            size = new CssPageSize { Orientation = orientation };
            return true;
        }

        (double width, double height, CssUnit unit) = s_sizes[name];
        if (orientation == CssPageOrientation.Landscape)
        {
            (width, height) = (height, width);
        }

        size = new CssPageSize { Width = new CssLength(width, unit), Height = new CssLength(height, unit), Name = name, Orientation = orientation };
        return true;

        static bool TryLength(CssComponentValue value, out CssLength length) =>
            CssLength.TryParse(value, out length) && length.Unit is not (CssUnit.None or CssUnit.Percent) && length.Value >= 0 && double.IsFinite(length.Value);
    }

    /// <summary>Formats the size as CSS text, such as <c>a4 landscape</c> or <c>8.5in 11in</c>.</summary>
    /// <returns>The text.</returns>
    public override string ToString()
    {
        string orientation = Orientation switch { CssPageOrientation.Portrait => "portrait", CssPageOrientation.Landscape => "landscape", _ => "" };
        if (Name is not null)
        {
            return orientation.Length > 0 ? Name + " " + orientation : Name;
        }

        if (Width is { } width && Height is { } height)
        {
            return width == height ? width.ToString() : string.Create(CultureInfo.InvariantCulture, $"{width} {height}");
        }

        return orientation.Length > 0 ? orientation : "auto";
    }
}
