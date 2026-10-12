namespace Egami.Css;

/// <summary>One complex selector of a selector list, such as <c>svg|g &gt; rect.highlight:first-child</c>.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Css.cs" region="selectors"/>
/// </example>
public sealed class CssSelector
{
    internal CssSelector(ComplexSelector selector, string text)
    {
        Complex = selector;
        Text = text;
    }

    internal ComplexSelector Complex { get; }

    /// <summary>Gets the selector's specificity (Selectors 4).</summary>
    public CssSpecificity Specificity => Complex.Specificity;

    /// <summary>
    /// Gets the pseudo-element the selector ends in, in lowercase without colons (such as <c>before</c>), or
    /// <see langword="null"/>. A selector with a pseudo-element never matches an element itself.
    /// </summary>
    public string? PseudoElement => Complex.PseudoElement;

    /// <summary>Gets the selector as CSS text.</summary>
    public string Text { get; }

    /// <summary>Tests whether the selector matches an element.</summary>
    /// <param name="element">The element.</param>
    /// <param name="scope">The scoping root for <c>:scope</c>, or <see langword="null"/> to make <c>:scope</c> match the root element.</param>
    /// <returns>True when it matches; always false for a selector with a pseudo-element.</returns>
    public bool Matches(ICssElement element, ICssElement? scope = null)
    {
        ArgumentNullException.ThrowIfNull(element);
        return Complex.PseudoElement is null && SelectorMatcher.Matches(Complex, element, new MatchContext(scope, null));
    }

    /// <summary>Tests whether the selector matches a pseudo-element of an element, such as its <c>::before</c>.</summary>
    /// <param name="element">The originating element.</param>
    /// <param name="pseudoElement">The pseudo-element in lowercase without colons, or <see langword="null"/> for the element itself.</param>
    /// <returns>True when the selector's pseudo-element is <paramref name="pseudoElement"/> and the rest matches the element.</returns>
    public bool MatchesPseudoElement(ICssElement element, string? pseudoElement)
    {
        ArgumentNullException.ThrowIfNull(element);
        return string.Equals(Complex.PseudoElement, pseudoElement, StringComparison.Ordinal) && SelectorMatcher.Matches(Complex, element, default);
    }

    /// <summary>Returns the selector as CSS text.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => Text;
}

/// <summary>A selector list (Selectors 4), such as a style rule's prelude or the argument of <c>querySelectorAll</c>.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Css.cs" region="selectors"/>
/// </example>
public sealed class CssSelectorList
{
    internal CssSelectorList(IReadOnlyList<CssSelector> selectors, string text)
    {
        Selectors = selectors;
        Text = text;
    }

    /// <summary>Gets the complex selectors, in order.</summary>
    public IReadOnlyList<CssSelector> Selectors { get; }

    /// <summary>Gets the list as CSS text.</summary>
    public string Text { get; }

    /// <summary>Parses a selector list.</summary>
    /// <param name="text">The selectors, such as <c>a:hover, .note &gt; p</c>.</param>
    /// <param name="namespaces">The namespace prefixes the selectors may use, or <see langword="null"/> for none.</param>
    /// <returns>The selector list.</returns>
    /// <exception cref="FormatException">The text is not a valid selector list.</exception>
    public static CssSelectorList Parse(string text, CssNamespaces? namespaces = null)
    {
        return TryParse(text, out CssSelectorList? list, namespaces) ? list : throw new FormatException($"'{text}' is not a valid selector list.");
    }

    /// <summary>Parses a selector list, without throwing.</summary>
    /// <param name="text">The selectors.</param>
    /// <param name="list">The selector list, when valid.</param>
    /// <param name="namespaces">The namespace prefixes the selectors may use, or <see langword="null"/> for none.</param>
    /// <returns>True when the text is a valid selector list.</returns>
    public static bool TryParse(string text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out CssSelectorList? list, CssNamespaces? namespaces = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        list = TryParse(CssSyntax.ParseComponentValues(text, CssParseOptions.Default), namespaces);
        return list is not null;
    }

    internal static CssSelectorList? TryParse(IReadOnlyList<CssComponentValue> items, CssNamespaces? namespaces)
    {
        List<(int Start, int End)> ranges = [];
        ComplexSelector[]? complex = SelectorParser.ParseList(items, namespaces, ranges);
        if (complex is null || complex.Length == 0)
        {
            return null;
        }

        CssSelector[] selectors = new CssSelector[complex.Length];
        for (int i = 0; i < complex.Length; i++)
        {
            (int start, int end) = ranges[i];
            List<CssComponentValue> slice = [];
            for (int k = start; k < end; k++)
            {
                slice.Add(items[k]);
            }

            selectors[i] = new CssSelector(complex[i], CssComponentValue.Serialize(CssSyntax.Trim(slice)));
        }

        return new CssSelectorList(selectors, string.Join(", ", selectors.Select(s => s.Text)));
    }

    /// <summary>Tests whether any selector in the list matches an element.</summary>
    /// <param name="element">The element.</param>
    /// <param name="scope">The scoping root for <c>:scope</c>, or <see langword="null"/> to make <c>:scope</c> match the root element.</param>
    /// <returns>True when a selector without a pseudo-element matches.</returns>
    public bool Matches(ICssElement element, ICssElement? scope = null)
    {
        ArgumentNullException.ThrowIfNull(element);
        MatchContext context = new(scope, null);
        foreach (CssSelector selector in Selectors)
        {
            if (selector.Complex.PseudoElement is null && SelectorMatcher.Matches(selector.Complex, element, context))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Returns the list as CSS text.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => Text;
}
