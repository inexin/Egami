namespace Egami.Css;

/// <summary>The origin of a style sheet in the cascade.</summary>
public enum CssOrigin
{
    /// <summary>The user agent's default style sheet.</summary>
    UserAgent,

    /// <summary>The user's style sheet.</summary>
    User,

    /// <summary>The document's style sheets, presentation hints and style attributes.</summary>
    Author,
}

/// <summary>Options for building a <see cref="CssCascade"/>.</summary>
public sealed record class CssCascadeOptions
{
    /// <summary>Gets the default options: <see cref="CssMediaContext.Default"/> and <see cref="CssPropertyRegistry.CreateDefault"/>.</summary>
    public static CssCascadeOptions Default { get; } = new();

    /// <summary>Gets the media context <c>@media</c> rules and <c>@import</c>-free media lists are evaluated against.</summary>
    public CssMediaContext Media { get; init; } = CssMediaContext.Default;

    /// <summary>
    /// Gets a callback answering whether a declaration in an <c>@supports</c> condition is supported, or <see langword="null"/>
    /// to accept a declaration whose property is registered (or custom) and whose value passes the property's validation.
    /// </summary>
    public Func<CssDeclaration, bool>? Supports { get; init; }

    /// <summary>Gets the property registry, or <see langword="null"/> for <see cref="CssPropertyRegistry.CreateDefault"/>.</summary>
    public CssPropertyRegistry? Registry { get; init; }
}

/// <summary>
/// The cascade (CSS Cascade 5, without animations, transitions and cascade layers): finds the declarations that apply to an
/// element and picks the winner for each property.
/// </summary>
/// <remarks>
/// Rules are indexed by the ID, class or type of their rightmost compound selector, so each element is tested against few
/// rules. <c>@media</c> rules apply when their query matches <see cref="CssCascadeOptions.Media"/> (build a new cascade for a
/// new context); <c>@supports</c> rules when their condition holds; <c>@layer</c> blocks are treated as ordinary rules;
/// <c>@import</c> rules are not followed (load the sheets and pass them in order instead). Shorthands are expanded into
/// longhands while the cascade is built, so a shorthand and a longhand compete per longhand. A cascade can be used from
/// several threads at once.
/// </remarks>
public sealed class CssCascade
{
    private static readonly IReadOnlyList<string> s_allExcluded = ["direction", "unicode-bidi"];

    private readonly Dictionary<string, List<RuleEntry>> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<RuleEntry>> _byClass = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<RuleEntry>> _byType = new(StringComparer.Ordinal);
    private readonly List<RuleEntry> _universal = [];
    private readonly CssCascadeOptions _options;
    private int _order;

    /// <summary>Builds a cascade from style sheets in order of appearance.</summary>
    /// <param name="sheets">The style sheets and their origins, in document order.</param>
    /// <param name="options">The media context, the <c>@supports</c> callback and the property registry.</param>
    public CssCascade(IEnumerable<(CssStyleSheet Sheet, CssOrigin Origin)> sheets, CssCascadeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sheets);
        _options = options ?? CssCascadeOptions.Default;
        Registry = _options.Registry ?? CssPropertyRegistry.CreateDefault();
        foreach ((CssStyleSheet sheet, CssOrigin origin) in sheets)
        {
            ArgumentNullException.ThrowIfNull(sheet);
            AddRules(sheet.Rules, origin, 0);
        }
    }

    /// <summary>Gets the property registry the cascade expands shorthands and computes values with.</summary>
    public CssPropertyRegistry Registry { get; }

    private void AddRules(IReadOnlyList<CssRule> rules, CssOrigin origin, int depth)
    {
        foreach (CssRule rule in rules)
        {
            switch (rule)
            {
                case CssStyleRule style:
                    AddStyleRule(style, origin);
                    break;
                case CssMediaRule media when media.Media.Matches(_options.Media):
                    AddRules(media.Rules, origin, depth + 1);
                    break;
                case CssSupportsRule supports when supports.Matches(_options.Supports ?? DefaultSupports):
                    AddRules(supports.Rules, origin, depth + 1);
                    break;
                case CssLayerRule { Rules: { } layerRules }:
                    AddRules(layerRules, origin, depth + 1);
                    break;
            }
        }
    }

    private bool DefaultSupports(CssDeclaration declaration)
    {
        if (declaration.IsCustomProperty)
        {
            return true;
        }

        if (Registry.TryGetShorthand(declaration.Name, out CssShorthandDefinition? shorthand))
        {
            return IsCssWideKeyword(declaration.Value) is not null || shorthand.Expand(declaration.Value) is not null;
        }

        return Registry.TryGetProperty(declaration.Name, out CssPropertyDefinition? definition)
            && (definition.Validate is null || IsCssWideKeyword(declaration.Value) is not null || definition.Validate(declaration.Value));
    }

    private void AddStyleRule(CssStyleRule rule, CssOrigin origin)
    {
        CssDeclaration[] declarations = Expand(rule.Declarations);
        if (declarations.Length == 0)
        {
            return;
        }

        foreach (CssSelector selector in rule.Selectors.Selectors)
        {
            RuleEntry entry = new(selector.Complex, origin, _order++, declarations);
            Compound subject = selector.Complex.Compounds[0];
            string? id = null;
            string? className = null;
            foreach (SimpleSelector simple in subject.Simples)
            {
                if (simple is IdSelector idSelector)
                {
                    id = idSelector.Id;
                    break;
                }

                if (simple is ClassSelector classSelector && className is null)
                {
                    className = classSelector.Name;
                }
            }

            if (id is not null)
            {
                Bucket(_byId, id).Add(entry);
            }
            else if (className is not null)
            {
                Bucket(_byClass, className).Add(entry);
            }
            else if (subject.LocalNameLower is { } type)
            {
                Bucket(_byType, type).Add(entry);
            }
            else
            {
                _universal.Add(entry);
            }
        }

        static List<RuleEntry> Bucket(Dictionary<string, List<RuleEntry>> index, string key)
        {
            if (!index.TryGetValue(key, out List<RuleEntry>? list))
            {
                list = [];
                index[key] = list;
            }

            return list;
        }
    }

    // Expands shorthands into longhands and drops declarations the registry finds invalid.
    internal CssDeclaration[] Expand(IReadOnlyList<CssDeclaration> declarations)
    {
        List<CssDeclaration> result = new(declarations.Count);
        foreach (CssDeclaration declaration in declarations)
        {
            ExpandOne(declaration, result);
        }

        return [.. result];
    }

    private void ExpandOne(CssDeclaration declaration, List<CssDeclaration> result)
    {
        if (declaration.IsCustomProperty)
        {
            result.Add(declaration);
            return;
        }

        string? keyword = IsCssWideKeyword(declaration.Value);
        if (declaration.Name == "all")
        {
            if (keyword is null)
            {
                return;
            }

            foreach (CssPropertyDefinition property in Registry.Properties)
            {
                if (!s_allExcluded.Contains(property.Name))
                {
                    result.Add(new CssDeclaration(property.Name, declaration.Value, declaration.Important) { Line = declaration.Line, Column = declaration.Column });
                }
            }

            return;
        }

        if (Registry.TryGetShorthand(declaration.Name, out CssShorthandDefinition? shorthand))
        {
            if (keyword is not null || CssVariables.ContainsVar(declaration.Value))
            {
                // A CSS-wide keyword sets every longhand; var() is substituted at computed-value time, then expanded.
                CssDeclaration? pending = keyword is null ? declaration : null;
                foreach (string longhand in shorthand.Longhands)
                {
                    result.Add(new CssDeclaration(longhand, declaration.Value, declaration.Important) { Line = declaration.Line, Column = declaration.Column, PendingShorthand = pending });
                }

                return;
            }

            IReadOnlyList<CssDeclaration>? expanded = shorthand.Expand(declaration.Value);
            if (expanded is null)
            {
                return;
            }

            foreach (string longhand in shorthand.Longhands)
            {
                CssDeclaration? set = null;
                foreach (CssDeclaration candidate in expanded)
                {
                    if (candidate.Name == longhand)
                    {
                        set = candidate;
                    }
                }

                IReadOnlyList<CssComponentValue> value = set?.Value ?? s_initial;
                result.Add(new CssDeclaration(longhand, value, declaration.Important) { Line = declaration.Line, Column = declaration.Column });
            }

            return;
        }

        if (keyword is null && !CssVariables.ContainsVar(declaration.Value)
            && Registry.TryGetProperty(declaration.Name, out CssPropertyDefinition? definition) && definition.Validate is { } validate && !validate(declaration.Value))
        {
            return;
        }

        result.Add(declaration);
    }

    private static readonly IReadOnlyList<CssComponentValue> s_initial = [new CssTokenValue(new CssToken(CssTokenKind.Ident, "initial"))];

    /// <summary>Returns the CSS-wide keyword a value consists of (lowercase), or null.</summary>
    internal static string? IsCssWideKeyword(IReadOnlyList<CssComponentValue> value)
    {
        if (value.Count != 1 || value[0] is not CssTokenValue { Token: { Kind: CssTokenKind.Ident } token })
        {
            return null;
        }

        string lower = CssText.ToAsciiLower(token.Value);
        return lower is "inherit" or "initial" or "unset" or "revert" or "revert-layer" ? lower : null;
    }

    /// <summary>Cascades the declarations that apply to an element (or one of its pseudo-elements).</summary>
    /// <param name="element">The element.</param>
    /// <param name="presentationHints">
    /// Declarations from the document language's presentation attributes (SVG's <c>fill="red"</c>, HTML's legacy attributes):
    /// author origin, specificity zero, before every author style sheet rule.
    /// </param>
    /// <param name="inlineStyle">The declarations of the element's <c>style</c> attribute (see <see cref="CssDeclaration.ParseList(string, CssParseOptions?)"/>).</param>
    /// <param name="pseudoElement">A pseudo-element such as <c>before</c>, or <see langword="null"/> for the element itself.</param>
    /// <returns>The winning declaration of each property.</returns>
    public CssCascadedStyle Cascade(ICssElement element, IReadOnlyList<CssDeclaration>? presentationHints = null, IReadOnlyList<CssDeclaration>? inlineStyle = null, string? pseudoElement = null)
    {
        ArgumentNullException.ThrowIfNull(element);
        CssCascadedStyle style = new();

        if (pseudoElement is null && presentationHints is { Count: > 0 })
        {
            CssDeclaration[] hints = Expand(presentationHints);
            for (int i = 0; i < hints.Length; i++)
            {
                // Presentation hints cannot be !important.
                CssDeclaration hint = hints[i];
                style.Offer(hint, CssOrigin.Author, important: false, styleAttribute: false, default, -1, i);
            }
        }

        if (element.Id is { Length: > 0 } id && _byId.TryGetValue(id, out List<RuleEntry>? byId))
        {
            Apply(byId, element, pseudoElement, style);
        }

        if (_byClass.Count > 0 && element.ClassName is { Length: > 0 } classNames)
        {
            Dictionary<string, List<RuleEntry>>.AlternateLookup<ReadOnlySpan<char>> lookup = _byClass.GetAlternateLookup<ReadOnlySpan<char>>();
            ReadOnlySpan<char> rest = classNames;
            while (rest.Length > 0)
            {
                int start = 0;
                while (start < rest.Length && CssText.IsWhitespace(rest[start]))
                {
                    start++;
                }

                rest = rest[start..];
                int end = 0;
                while (end < rest.Length && !CssText.IsWhitespace(rest[end]))
                {
                    end++;
                }

                ReadOnlySpan<char> name = rest[..end];
                rest = rest[end..];
                if (name.Length > 0 && !RepeatsEarlier(classNames, name, rest.Length) && lookup.TryGetValue(name, out List<RuleEntry>? byClass))
                {
                    Apply(byClass, element, pseudoElement, style);
                }
            }
        }

        if (_byType.Count > 0)
        {
            string localName = element.LocalName;
            Span<char> lower = localName.Length <= 128 ? stackalloc char[localName.Length] : new char[localName.Length];
            for (int i = 0; i < localName.Length; i++)
            {
                lower[i] = CssText.ToAsciiLower(localName[i]);
            }

            if (_byType.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(lower, out List<RuleEntry>? byType))
            {
                Apply(byType, element, pseudoElement, style);
            }
        }

        Apply(_universal, element, pseudoElement, style);

        if (pseudoElement is null && inlineStyle is { Count: > 0 })
        {
            CssDeclaration[] inline = Expand(inlineStyle);
            for (int i = 0; i < inline.Length; i++)
            {
                style.Offer(inline[i], CssOrigin.Author, inline[i].Important, styleAttribute: true, default, int.MaxValue, i);
            }
        }

        return style;
    }

    // Whether the class name ending just before the remaining text also appears earlier in the list.
    private static bool RepeatsEarlier(ReadOnlySpan<char> all, ReadOnlySpan<char> name, int remaining)
    {
        ReadOnlySpan<char> before = all[..(all.Length - remaining - name.Length)];
        while (before.Length > 0)
        {
            int start = 0;
            while (start < before.Length && CssText.IsWhitespace(before[start]))
            {
                start++;
            }

            before = before[start..];
            int end = 0;
            while (end < before.Length && !CssText.IsWhitespace(before[end]))
            {
                end++;
            }

            if (before[..end].SequenceEqual(name))
            {
                return true;
            }

            before = before[end..];
        }

        return false;
    }

    private static void Apply(List<RuleEntry> entries, ICssElement element, string? pseudoElement, CssCascadedStyle style)
    {
        MatchContext context = default;
        foreach (RuleEntry entry in entries)
        {
            if (!string.Equals(entry.Selector.PseudoElement, pseudoElement, StringComparison.Ordinal) || !SelectorMatcher.Matches(entry.Selector, element, context))
            {
                continue;
            }

            CssDeclaration[] declarations = entry.Declarations;
            for (int i = 0; i < declarations.Length; i++)
            {
                style.Offer(declarations[i], entry.Origin, declarations[i].Important, styleAttribute: false, entry.Selector.Specificity, entry.Order, i);
            }
        }
    }

    /// <summary>Computes an element's style from its cascaded style and its parent's computed style.</summary>
    /// <param name="cascaded">The element's cascaded style.</param>
    /// <param name="parent">The parent element's computed style, or <see langword="null"/> for the root.</param>
    /// <returns>The computed style.</returns>
    public CssComputedStyle Compute(CssCascadedStyle cascaded, CssComputedStyle? parent) => CssComputedStyle.Compute(cascaded, parent, Registry);

    private sealed class RuleEntry(ComplexSelector selector, CssOrigin origin, int order, CssDeclaration[] declarations)
    {
        public ComplexSelector Selector { get; } = selector;

        public CssOrigin Origin { get; } = origin;

        public int Order { get; } = order;

        public CssDeclaration[] Declarations { get; } = declarations;
    }
}

/// <summary>The cascaded style of an element: the winning declaration of each property, with its origin.</summary>
public sealed class CssCascadedStyle
{
    private readonly Dictionary<string, Slots> _properties = new(StringComparer.Ordinal);

    /// <summary>Creates an empty cascaded style (no declarations apply).</summary>
    public CssCascadedStyle()
    {
    }

    /// <summary>Gets the number of properties with a winning declaration.</summary>
    public int Count => _properties.Count;

    /// <summary>Gets the names of the properties with a winning declaration (longhands and custom properties).</summary>
    public IEnumerable<string> Properties => _properties.Keys;

    /// <summary>Gets the winning declaration of a property.</summary>
    /// <param name="name">The property name (lowercase, or a custom property name as written).</param>
    /// <param name="declaration">The winning declaration.</param>
    /// <returns>True when a declaration applies.</returns>
    public bool TryGetDeclaration(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out CssDeclaration? declaration) => TryGetDeclaration(name, out declaration, out _);

    /// <summary>Gets the winning declaration of a property and its origin.</summary>
    /// <param name="name">The property name.</param>
    /// <param name="declaration">The winning declaration.</param>
    /// <param name="origin">Its origin.</param>
    /// <returns>True when a declaration applies.</returns>
    public bool TryGetDeclaration(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out CssDeclaration? declaration, out CssOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(name);
        declaration = null;
        origin = default;
        if (!_properties.TryGetValue(name, out Slots slots))
        {
            return false;
        }

        int best = slots.Best(int.MaxValue);
        if (best < 0)
        {
            return false;
        }

        declaration = slots.Get(best).Declaration!;
        origin = (CssOrigin)best;
        return true;
    }

    /// <summary>Gets the value text of a property's winning declaration, or <see langword="null"/>.</summary>
    /// <param name="name">The property name.</param>
    /// <returns>The value text.</returns>
    public string? GetValueText(string name) => TryGetDeclaration(name, out CssDeclaration? declaration) ? declaration.ValueText : null;

    // The winner after rolling back origins for 'revert': the best declaration of an origin below 'below', or null.
    internal CssDeclaration? GetRolledBack(string name, CssOrigin below, out CssOrigin origin)
    {
        origin = default;
        if (!_properties.TryGetValue(name, out Slots slots))
        {
            return null;
        }

        int best = slots.Best((int)below);
        if (best < 0)
        {
            return null;
        }

        origin = (CssOrigin)best;
        return slots.Get(best).Declaration;
    }

    internal void Offer(CssDeclaration declaration, CssOrigin origin, bool important, bool styleAttribute, CssSpecificity specificity, int order, int index)
    {
        Key key = new(important, styleAttribute, specificity, order, index);
        _properties.TryGetValue(declaration.Name, out Slots slots);
        Entry current = slots.Get((int)origin);
        if (current.Declaration is null || key.CompareTo(current.Key) >= 0)
        {
            slots.Set((int)origin, new Entry(declaration, key));
            _properties[declaration.Name] = slots;
        }
    }

    // Precedence within one origin: importance, then the style attribute, specificity, order of appearance and position in the rule.
    internal readonly record struct Key(bool Important, bool StyleAttribute, CssSpecificity Specificity, int Order, int Index) : IComparable<Key>
    {
        public int CompareTo(Key other)
        {
            int c = Important.CompareTo(other.Important);
            if (c == 0)
            {
                c = StyleAttribute.CompareTo(other.StyleAttribute);
            }

            if (c == 0)
            {
                c = Specificity.CompareTo(other.Specificity);
            }

            if (c == 0)
            {
                c = Order.CompareTo(other.Order);
            }

            return c != 0 ? c : Index.CompareTo(other.Index);
        }
    }

    internal readonly record struct Entry(CssDeclaration? Declaration, Key Key);

    // The best declaration of each origin: UserAgent, User, Author.
    private struct Slots
    {
        private Entry _userAgent;
        private Entry _user;
        private Entry _author;

        public readonly Entry Get(int origin) => origin switch { 0 => _userAgent, 1 => _user, _ => _author };

        public void Set(int origin, Entry entry)
        {
            switch (origin)
            {
                case 0:
                    _userAgent = entry;
                    break;
                case 1:
                    _user = entry;
                    break;
                default:
                    _author = entry;
                    break;
            }
        }

        // The origin whose declaration wins among the origins below 'limit': normal declarations rank UA < user < author,
        // important ones author < user < UA, above every normal one.
        public readonly int Best(int limit)
        {
            int best = -1;
            int bestRank = -1;
            for (int origin = 0; origin < 3 && origin < limit; origin++)
            {
                Entry entry = Get(origin);
                if (entry.Declaration is null)
                {
                    continue;
                }

                int rank = entry.Key.Important ? 5 - origin : origin;
                if (rank > bestRank)
                {
                    bestRank = rank;
                    best = origin;
                }
            }

            return best;
        }
    }
}
