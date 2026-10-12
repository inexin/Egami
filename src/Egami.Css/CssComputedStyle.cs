using System.Collections;

namespace Egami.Css;

/// <summary>
/// An element's computed style, as far as CSS defines it generically: CSS-wide keywords resolved, inheritance applied, custom
/// properties and <c>var()</c> substituted. Values stay component values; the consumer parses each property by its type (and
/// resolves relative units, percentages and <c>currentcolor</c>).
/// </summary>
/// <remarks>
/// A computed style stores only what its element sets and shares the rest with its parent: inherited properties are looked
/// up through the parent chain, and the others fall back to their initial value from the registry. Enumerating a computed
/// style lists the properties with a value that the element or an ancestor set (not initial values).
/// </remarks>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Css.cs" region="cascade"/>
/// </example>
public sealed class CssComputedStyle : IEnumerable<KeyValuePair<string, IReadOnlyList<CssComponentValue>>>
{
    // The values this element sets; null means "no value" (a custom property that is invalid at computed-value time).
    private readonly Dictionary<string, IReadOnlyList<CssComponentValue>?> _own;
    private readonly CssComputedStyle? _parent;
    private readonly CssPropertyRegistry _registry;

    private CssComputedStyle(Dictionary<string, IReadOnlyList<CssComponentValue>?> own, CssComputedStyle? parent, CssPropertyRegistry registry)
    {
        _own = own;
        _parent = parent;
        _registry = registry;
    }

    /// <summary>Gets the parent element's computed style, or <see langword="null"/> for the root.</summary>
    public CssComputedStyle? Parent => _parent;

    /// <summary>Gets a property's computed value.</summary>
    /// <param name="name">The property name (lowercase, or a custom property name as written).</param>
    /// <param name="value">The value as component values.</param>
    /// <returns>True when the property has a value: set, inherited or initial.</returns>
    public bool TryGetValue(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IReadOnlyList<CssComponentValue>? value)
    {
        ArgumentNullException.ThrowIfNull(name);
        value = Lookup(name);
        return value is not null;
    }

    /// <summary>Gets a property's computed value as CSS text, or <see langword="null"/> when it has none.</summary>
    /// <param name="name">The property name.</param>
    /// <returns>The value text.</returns>
    public string? GetValueText(string name)
    {
        return TryGetValue(name, out IReadOnlyList<CssComponentValue>? value) ? CssComponentValue.Serialize(value) : null;
    }

    /// <summary>Gets whether the element itself sets the property (rather than inheriting it or taking its initial value).</summary>
    /// <param name="name">The property name.</param>
    /// <returns>True when the element's own declarations set it.</returns>
    public bool IsSetOnElement(string name) => _own.ContainsKey(name);

    internal IReadOnlyList<CssComponentValue>? Lookup(string name)
    {
        if (_own.TryGetValue(name, out IReadOnlyList<CssComponentValue>? own))
        {
            return own;
        }

        bool custom = name.StartsWith("--", StringComparison.Ordinal);
        if (!custom && !(_registry.TryGetProperty(name, out CssPropertyDefinition? definition) && definition.Inherited))
        {
            return Initial(name);
        }

        for (CssComputedStyle? style = _parent; style is not null; style = style._parent)
        {
            if (style._own.TryGetValue(name, out own))
            {
                return own;
            }
        }

        return custom ? null : Initial(name);
    }

    private IReadOnlyList<CssComponentValue>? Initial(string name) => _registry.TryGetProperty(name, out CssPropertyDefinition? definition) ? definition.Initial : null;

    /// <summary>Enumerates the properties with a value that this element or an ancestor set.</summary>
    /// <returns>The properties and their values.</returns>
    public IEnumerator<KeyValuePair<string, IReadOnlyList<CssComponentValue>>> GetEnumerator()
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        for (CssComputedStyle? style = this; style is not null; style = style._parent)
        {
            foreach (KeyValuePair<string, IReadOnlyList<CssComponentValue>?> pair in style._own)
            {
                if (!seen.Add(pair.Key))
                {
                    continue;
                }

                bool inheritable = pair.Key.StartsWith("--", StringComparison.Ordinal) || (_registry.TryGetProperty(pair.Key, out CssPropertyDefinition? definition) && definition.Inherited);
                if ((style == this || inheritable) && Lookup(pair.Key) is { } value)
                {
                    yield return new KeyValuePair<string, IReadOnlyList<CssComponentValue>>(pair.Key, value);
                }
            }
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Creates the computed style of an element no declaration applies to.</summary>
    /// <param name="parent">The parent's computed style, or <see langword="null"/> for the root.</param>
    /// <param name="registry">The property registry.</param>
    /// <returns>The computed style: inherited and initial values only.</returns>
    public static CssComputedStyle CreateInherited(CssComputedStyle? parent, CssPropertyRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return new CssComputedStyle([], parent, registry);
    }

    /// <summary>
    /// Computes an element's style: resolves <c>inherit</c>, <c>initial</c>, <c>unset</c>, <c>revert</c> and
    /// <c>revert-layer</c> (treated as <c>revert</c>), applies inheritance, and substitutes <c>var()</c> with fallbacks and
    /// cycle detection. A property whose substitution fails is invalid at computed-value time and behaves as <c>unset</c>.
    /// </summary>
    /// <param name="cascaded">The element's cascaded style.</param>
    /// <param name="parent">The parent element's computed style, or <see langword="null"/> for the root.</param>
    /// <param name="registry">The property registry (inheritance, initial values, shorthands, validation).</param>
    /// <returns>The computed style.</returns>
    public static CssComputedStyle Compute(CssCascadedStyle cascaded, CssComputedStyle? parent, CssPropertyRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(cascaded);
        ArgumentNullException.ThrowIfNull(registry);
        Dictionary<string, IReadOnlyList<CssComponentValue>?> own = new(StringComparer.Ordinal);
        CssComputedStyle style = new(own, parent, registry);

        // Resolve 'revert' by rolling back origins, then split custom properties from the others.
        Dictionary<string, IReadOnlyList<CssComponentValue>> customRaw = new(StringComparer.Ordinal);
        List<(string Name, CssDeclaration? Declaration)> regular = [];
        foreach (string name in cascaded.Properties)
        {
            if (!cascaded.TryGetDeclaration(name, out CssDeclaration? declaration, out CssOrigin origin))
            {
                continue;
            }

            while (declaration is not null && CssCascade.IsCssWideKeyword(declaration.Value) is "revert" or "revert-layer")
            {
                declaration = origin == CssOrigin.UserAgent ? null : cascaded.GetRolledBack(name, origin, out origin);
            }

            if (name.StartsWith("--", StringComparison.Ordinal))
            {
                switch (declaration is null ? "unset" : CssCascade.IsCssWideKeyword(declaration.Value))
                {
                    case "initial":
                        own[name] = null;
                        break;
                    case "inherit" or "unset":
                        break;
                    default:
                        customRaw[name] = declaration!.Value;
                        break;
                }
            }
            else
            {
                regular.Add((name, declaration));
            }
        }

        CssVariables variables = new(customRaw, own, parent);
        variables.ResolveAll();

        foreach ((string name, CssDeclaration? declaration) in regular)
        {
            bool inherited = registry.TryGetProperty(name, out CssPropertyDefinition? definition) && definition.Inherited;
            IReadOnlyList<CssComponentValue>? value = declaration?.Value;
            string? keyword = value is null ? "unset" : CssCascade.IsCssWideKeyword(value);
            if (keyword is null && declaration is not null)
            {
                value = ComputeValue(name, declaration, definition, variables, registry);
                if (value is null || CssCascade.IsCssWideKeyword(value) is not null)
                {
                    keyword = "unset";
                }
            }

            if (keyword == "unset")
            {
                keyword = inherited ? "inherit" : "initial";
            }

            switch (keyword)
            {
                case "inherit":
                    if (!inherited && parent is not null)
                    {
                        own[name] = parent.Lookup(name);
                    }

                    break;
                case "initial":
                    if (inherited)
                    {
                        own[name] = definition?.Initial;
                    }

                    break;
                default:
                    own[name] = value;
                    break;
            }
        }

        return style;
    }

    // The value of a non-custom property after var() substitution and shorthand expansion; null when invalid.
    private static IReadOnlyList<CssComponentValue>? ComputeValue(string name, CssDeclaration declaration, CssPropertyDefinition? definition, CssVariables variables, CssPropertyRegistry registry)
    {
        IReadOnlyList<CssComponentValue>? value;
        if (declaration.PendingShorthand is { } shorthandDeclaration)
        {
            IReadOnlyList<CssComponentValue>? substituted = variables.Substitute(shorthandDeclaration.Value);
            if (substituted is null || !registry.TryGetShorthand(shorthandDeclaration.Name, out CssShorthandDefinition? shorthand))
            {
                return null;
            }

            if (CssCascade.IsCssWideKeyword(substituted) is not null)
            {
                return null;
            }

            IReadOnlyList<CssDeclaration>? expanded = shorthand.Expand(substituted);
            if (expanded is null)
            {
                return null;
            }

            value = definition?.Initial;
            foreach (CssDeclaration longhand in expanded)
            {
                if (longhand.Name == name)
                {
                    value = longhand.Value;
                }
            }

            if (value is not null && CssCascade.IsCssWideKeyword(value) is not null)
            {
                value = definition?.Initial;
            }

            return value;
        }

        if (!CssVariables.ContainsVar(declaration.Value))
        {
            return declaration.Value;
        }

        value = variables.Substitute(declaration.Value);
        if (value is null || (definition?.Validate is { } validate && !validate(value)))
        {
            return null;
        }

        return value;
    }
}
