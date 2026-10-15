namespace Egami.Css;

/// <summary>A longhand property the cascade knows: whether it inherits, its initial value, and optionally how to validate a value.</summary>
/// <param name="Name">The property name in lowercase.</param>
/// <param name="Inherited">Whether the property inherits by default.</param>
/// <param name="InitialValue">The initial value as CSS text, or <see langword="null"/> for none.</param>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Css.cs" region="cascade"/>
/// </example>
public sealed record class CssPropertyDefinition(string Name, bool Inherited, string? InitialValue)
{
    private IReadOnlyList<CssComponentValue>? _initial;

    /// <summary>
    /// Gets a check of a value (without var() or CSS-wide keywords), or <see langword="null"/> to accept any value. A declaration
    /// that fails it is dropped while the cascade is built, as browsers drop invalid declarations, so an earlier valid one can
    /// win; after var() substitution a failing value makes the property invalid at computed-value time.
    /// </summary>
    public Func<IReadOnlyList<CssComponentValue>, bool>? Validate { get; init; }

    internal IReadOnlyList<CssComponentValue>? Initial => InitialValue is null ? null : _initial ??= CssSyntax.Trim(CssSyntax.ParseComponentValues(InitialValue, CssParseOptions.Default));
}

/// <summary>A shorthand property: its longhands and how to expand a value into them.</summary>
/// <param name="Name">The shorthand name in lowercase.</param>
/// <param name="Longhands">The longhands it sets, in lowercase.</param>
/// <param name="Expand">
/// Expands a value (without var() or CSS-wide keywords) into longhand declarations, or returns <see langword="null"/> when the
/// value is invalid. A longhand it leaves out is set to <c>initial</c>.
/// </param>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Css.cs" region="cascade"/>
/// </example>
public sealed record class CssShorthandDefinition(string Name, IReadOnlyList<string> Longhands, Func<IReadOnlyList<CssComponentValue>, IReadOnlyList<CssDeclaration>?> Expand);

/// <summary>
/// The properties the cascade knows. <see cref="CreateDefault"/> gives the common text properties of CSS; consumers add their
/// own, such as Egami.Svg's <c>fill</c> and <c>stroke</c>. Unregistered properties cascade too, as non-inherited properties
/// without an initial value. Do not change a registry while a cascade built from it is in use.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Css.cs" region="cascade"/>
/// </example>
public sealed class CssPropertyRegistry
{
    private readonly Dictionary<string, CssPropertyDefinition> _properties = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CssShorthandDefinition> _shorthands = new(StringComparer.Ordinal);

    /// <summary>Gets the registered longhand properties.</summary>
    public IEnumerable<CssPropertyDefinition> Properties => _properties.Values;

    /// <summary>Gets the registered shorthands.</summary>
    public IEnumerable<CssShorthandDefinition> Shorthands => _shorthands.Values;

    /// <summary>Registers a longhand property, replacing one of the same name.</summary>
    /// <param name="definition">The property.</param>
    public void Register(CssPropertyDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        _properties[definition.Name] = definition;
    }

    /// <summary>Registers a shorthand, replacing one of the same name.</summary>
    /// <param name="definition">The shorthand.</param>
    public void RegisterShorthand(CssShorthandDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        _shorthands[definition.Name] = definition;
    }

    /// <summary>Looks up a longhand property.</summary>
    /// <param name="name">The name in lowercase.</param>
    /// <param name="definition">The property when registered.</param>
    /// <returns>True when registered.</returns>
    public bool TryGetProperty(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out CssPropertyDefinition? definition)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _properties.TryGetValue(name, out definition);
    }

    /// <summary>Looks up a shorthand.</summary>
    /// <param name="name">The name in lowercase.</param>
    /// <param name="definition">The shorthand when registered.</param>
    /// <returns>True when registered.</returns>
    public bool TryGetShorthand(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out CssShorthandDefinition? definition)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _shorthands.TryGetValue(name, out definition);
    }

    /// <summary>
    /// Creates a registry with the CSS properties SVG and HTML documents share, each with its value grammar, so an invalid
    /// declaration is dropped. Text: <c>color</c>, the <c>font-*</c> longhands and the <c>font</c> and <c>font-variant</c>
    /// shorthands, <c>line-height</c>, <c>letter-spacing</c>, <c>word-spacing</c>, <c>text-decoration</c> and its longhands,
    /// <c>text-align</c>, <c>text-indent</c>, <c>text-transform</c>, <c>white-space</c>, <c>direction</c>, <c>unicode-bidi</c>,
    /// <c>writing-mode</c>, <c>text-orientation</c>, <c>visibility</c>, <c>display</c> and <c>opacity</c>. Boxes: the
    /// <c>margin-*</c>, <c>padding-*</c> and <c>border-*</c> longhands with the <c>margin</c>, <c>padding</c>, <c>border</c>,
    /// <c>border-width</c>, <c>border-style</c>, <c>border-color</c>, <c>border-top</c> (and the other sides) and
    /// <c>border-radius</c> shorthands; <c>width</c>, <c>height</c>, their <c>min-</c> and <c>max-</c> forms and
    /// <c>box-sizing</c>. Backgrounds: the <c>background-*</c> longhands, one comma-separated item per layer, and the
    /// <c>background</c> shorthand. Lists: <c>list-style-type</c>, <c>list-style-position</c>, <c>list-style-image</c> and
    /// <c>list-style</c>. Tables: <c>border-collapse</c>, <c>border-spacing</c>, <c>caption-side</c>, <c>empty-cells</c>,
    /// <c>table-layout</c> and <c>vertical-align</c>. Flow: <c>float</c>, <c>clear</c>, <c>overflow-x</c>, <c>overflow-y</c>
    /// and <c>overflow</c>. Fragmentation: <c>break-before</c>, <c>break-after</c>, <c>break-inside</c>, <c>orphans</c>,
    /// <c>widows</c>, <c>box-decoration-break</c>, <c>page</c> and the legacy <c>page-break-*</c> shorthands. Pages (for
    /// <see cref="CssCascade.CascadePage"/>): <c>size</c>, <c>page-orientation</c>, <c>marks</c> and <c>bleed</c>. Generated
    /// content: <c>content</c>, <c>counter-reset</c>, <c>counter-increment</c> and <c>counter-set</c>. The <c>all</c>
    /// shorthand is always understood.
    /// </summary>
    /// <returns>A new registry the caller may extend.</returns>
    public static CssPropertyRegistry CreateDefault()
    {
        CssPropertyRegistry registry = new();
        CssDefaultProperties.AddTo(registry);
        return registry;
    }
}
