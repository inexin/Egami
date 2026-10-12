namespace Egami.Css;

/// <summary>A CSS declaration: a property name, its value as component values, and whether it is <c>!important</c>.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Css.cs" region="declarations"/>
/// </example>
public sealed class CssDeclaration
{
    private string? _valueText;

    /// <summary>Creates a declaration.</summary>
    /// <param name="name">
    /// The property name. It is lowercased (ASCII) unless it is a custom property such as <c>--Main-Color</c>, which keeps its case.
    /// </param>
    /// <param name="value">The value, without the <c>!important</c> flag. Whitespace at either end is removed.</param>
    /// <param name="important">Whether the declaration is <c>!important</c>.</param>
    public CssDeclaration(string name, IReadOnlyList<CssComponentValue> value, bool important = false)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        Name = name.StartsWith("--", StringComparison.Ordinal) ? name : CssText.ToAsciiLower(name);
        Value = value.Count > 0 && (value[0].IsWhitespace || value[^1].IsWhitespace) ? CssSyntax.Trim(value) : value;
        Important = important;
    }

    /// <summary>Gets the property name: lowercase, except for custom properties.</summary>
    public string Name { get; }

    /// <summary>Gets the value as component values, without whitespace at either end and without <c>!important</c>.</summary>
    public IReadOnlyList<CssComponentValue> Value { get; }

    /// <summary>Gets whether the declaration is <c>!important</c>.</summary>
    public bool Important { get; }

    /// <summary>Gets the value serialized as CSS text.</summary>
    public string ValueText => _valueText ??= CssComponentValue.Serialize(Value);

    /// <summary>Gets whether this declares a custom property (a name starting with <c>--</c>).</summary>
    public bool IsCustomProperty => Name.StartsWith("--", StringComparison.Ordinal);

    /// <summary>Gets the 1-based line the declaration starts on, or 0 when unknown.</summary>
    public int Line { get; init; }

    /// <summary>Gets the 1-based column the declaration starts at, or 0 when unknown.</summary>
    public int Column { get; init; }

    // For a longhand of a shorthand whose value holds var(): the shorthand's declaration, expanded after substitution.
    internal CssDeclaration? PendingShorthand { get; init; }

    /// <summary>Creates a declaration from a property name and value text, such as an SVG presentation attribute.</summary>
    /// <param name="name">The property name.</param>
    /// <param name="value">The value as CSS text, without <c>!important</c>.</param>
    /// <param name="important">Whether the declaration is <c>!important</c>.</param>
    /// <returns>The declaration.</returns>
    public static CssDeclaration Create(string name, string value, bool important = false)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new CssDeclaration(name, CssSyntax.Trim(CssSyntax.ParseComponentValues(value, CssParseOptions.Default)), important);
    }

    /// <summary>
    /// Parses a list of declarations, such as the content of a <c>style</c> attribute (CSS Style Attributes). Invalid
    /// declarations are skipped and reported; nested rules and at-rules are dropped and reported.
    /// </summary>
    /// <param name="styleAttribute">The declarations, such as <c>color: red; font-size: 12px</c>.</param>
    /// <param name="options">Options for error reporting.</param>
    /// <returns>The declarations, in order.</returns>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Css.cs" region="declarations"/>
    /// </example>
    public static IReadOnlyList<CssDeclaration> ParseList(string styleAttribute, CssParseOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(styleAttribute);
        options ??= CssParseOptions.Default;
        List<CssComponentValue> items = CssSyntax.ParseComponentValues(styleAttribute, options);
        return ParseList(items, options, "a declaration list");
    }

    internal static List<CssDeclaration> ParseList(IReadOnlyList<CssComponentValue> items, CssParseOptions options, string context)
    {
        List<CssDeclaration> declarations = [];
        List<object> parsed = [];
        CssSyntax.Parser parser = new(items, options);
        parser.ConsumeBlockContents(parsed);
        foreach (object item in parsed)
        {
            switch (item)
            {
                case CssDeclaration declaration:
                    declarations.Add(declaration);
                    break;
                case RawAtRule atRule:
                    options.Report(atRule.Line, atRule.Column, $"The at-rule @{atRule.Name} is not allowed in {context}; it was dropped.");
                    break;
                case RawQualifiedRule qualified:
                    options.Report(qualified.Line, qualified.Column, $"A nested rule in {context} was dropped: CSS Nesting is not supported.");
                    break;
            }
        }

        return declarations;
    }

    /// <summary>Serializes the declaration as CSS text, such as <c>color: red !important</c>.</summary>
    /// <returns>The CSS text.</returns>
    public override string ToString()
    {
        string name = IsCustomProperty ? Name : CssSerializer.Identifier(Name);
        return Important ? $"{name}: {ValueText} !important" : $"{name}: {ValueText}";
    }
}
