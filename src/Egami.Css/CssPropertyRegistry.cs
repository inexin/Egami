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
    /// Creates a registry with the common CSS text properties SVG and HTML share: <c>color</c>, the <c>font-*</c> longhands and
    /// the <c>font</c> and <c>font-variant</c> shorthands, <c>line-height</c>, <c>letter-spacing</c>, <c>word-spacing</c>,
    /// <c>text-decoration</c> and its longhands, <c>text-align</c>, <c>text-indent</c>, <c>text-transform</c>,
    /// <c>white-space</c>, <c>direction</c>, <c>unicode-bidi</c>, <c>writing-mode</c>, <c>text-orientation</c>,
    /// <c>visibility</c>, <c>display</c> and <c>opacity</c>. The <c>all</c> shorthand is always understood.
    /// </summary>
    /// <returns>A new registry the caller may extend.</returns>
    public static CssPropertyRegistry CreateDefault()
    {
        CssPropertyRegistry registry = new();
        CssDefaultProperties.AddTo(registry);
        return registry;
    }
}
