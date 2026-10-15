namespace Egami.Css;

/// <summary>A rule of a style sheet.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Css.cs" region="parse"/>
/// </example>
public abstract class CssRule
{
    private protected CssRule()
    {
    }

    /// <summary>Gets the 1-based line the rule starts on, or 0 when unknown.</summary>
    public int Line { get; internal init; }

    /// <summary>Gets the 1-based column the rule starts at, or 0 when unknown.</summary>
    public int Column { get; internal init; }
}

/// <summary>A style rule: a selector list and declarations, such as <c>rect.a { fill: red }</c>.</summary>
/// <remarks>Rules nested inside a style rule (CSS Nesting) are not supported: they are dropped and reported.</remarks>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Css.cs" region="parse"/>
/// </example>
public sealed class CssStyleRule : CssRule
{
    internal CssStyleRule(CssSelectorList selectors, IReadOnlyList<CssDeclaration> declarations)
    {
        Selectors = selectors;
        Declarations = declarations;
    }

    /// <summary>Gets the selectors.</summary>
    public CssSelectorList Selectors { get; }

    /// <summary>Gets the selectors as CSS text.</summary>
    public string SelectorText => Selectors.Text;

    /// <summary>Gets the declarations, in order.</summary>
    public IReadOnlyList<CssDeclaration> Declarations { get; }
}

/// <summary>An <c>@media</c> rule: rules that apply when the media query list matches.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Css.cs" region="parse"/>
/// </example>
public sealed class CssMediaRule : CssRule
{
    internal CssMediaRule(CssMediaQueryList media, IReadOnlyList<CssRule> rules)
    {
        Media = media;
        Rules = rules;
    }

    /// <summary>Gets the media query list.</summary>
    public CssMediaQueryList Media { get; }

    /// <summary>Gets the rules inside.</summary>
    public IReadOnlyList<CssRule> Rules { get; }
}

/// <summary>An <c>@supports</c> rule: rules that apply when the condition holds.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Css.cs" region="parse"/>
/// </example>
public sealed class CssSupportsRule : CssRule
{
    private readonly SupportsNode? _condition;

    internal CssSupportsRule(IReadOnlyList<CssComponentValue> prelude, IReadOnlyList<CssRule> rules, CssNamespaces? namespaces)
    {
        List<CssComponentValue> trimmed = CssSyntax.Trim(prelude);
        ConditionText = CssComponentValue.Serialize(trimmed);
        Rules = rules;
        _condition = SupportsNode.Parse(trimmed, namespaces);
    }

    /// <summary>Gets the condition as CSS text.</summary>
    public string ConditionText { get; }

    /// <summary>Gets whether the condition parsed. A rule whose condition does not parse never matches.</summary>
    public bool IsValid => _condition is not null;

    /// <summary>Gets the rules inside.</summary>
    public IReadOnlyList<CssRule> Rules { get; }

    /// <summary>
    /// Evaluates the condition. <c>selector()</c> tests are answered by this package's selector parser; <c>font-tech()</c>,
    /// <c>font-format()</c> and unknown syntax are false.
    /// </summary>
    /// <param name="supports">Answers whether a declaration such as <c>(display: grid)</c> is supported.</param>
    /// <returns>True when the condition holds.</returns>
    public bool Matches(Func<CssDeclaration, bool> supports)
    {
        ArgumentNullException.ThrowIfNull(supports);
        return _condition is not null && _condition.Evaluate(supports);
    }
}

/// <summary>An <c>@font-face</c> rule.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Css.cs" region="parse"/>
/// </example>
public sealed class CssFontFaceRule : CssRule
{
    private CssFontFace? _fontFace;

    internal CssFontFaceRule(IReadOnlyList<CssDeclaration> declarations)
    {
        Declarations = declarations;
    }

    /// <summary>Gets the descriptors as declarations, in order.</summary>
    public IReadOnlyList<CssDeclaration> Declarations { get; }

    /// <summary>Gets the descriptors parsed: family, sources, weight, style, stretch, unicode ranges and so on.</summary>
    public CssFontFace FontFace => _fontFace ??= CssFontFace.FromDeclarations(Declarations);
}

/// <summary>An <c>@import</c> rule. Egami.Css does not fetch it: the consumer may load the sheet and pass it to the cascade.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Css.cs" region="parse"/>
/// </example>
public sealed class CssImportRule : CssRule
{
    internal CssImportRule(string url, CssMediaQueryList media, string? layer, string? supportsText)
    {
        Url = url;
        Media = media;
        Layer = layer;
        SupportsText = supportsText;
    }

    /// <summary>Gets the URL of the imported sheet, unresolved.</summary>
    public string Url { get; }

    /// <summary>Gets the media the import is for; an empty list matches everything.</summary>
    public CssMediaQueryList Media { get; }

    /// <summary>Gets the layer name of <c>layer(name)</c>, an empty string for a bare <c>layer</c>, or <see langword="null"/>.</summary>
    public string? Layer { get; }

    /// <summary>Gets the condition of <c>supports(...)</c> as CSS text, or <see langword="null"/>.</summary>
    public string? SupportsText { get; }
}

/// <summary>An <c>@namespace</c> rule: a prefix (or the default namespace) for selectors.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Css.cs" region="parse"/>
/// </example>
public sealed class CssNamespaceRule : CssRule
{
    internal CssNamespaceRule(string? prefix, string uri)
    {
        Prefix = prefix;
        Uri = uri;
    }

    /// <summary>Gets the prefix, or <see langword="null"/> for the default namespace.</summary>
    public string? Prefix { get; }

    /// <summary>Gets the namespace URI.</summary>
    public string Uri { get; }
}

/// <summary>
/// An <c>@layer</c> rule: a statement naming layers, or a block of rules. Cascade layers are not implemented: the cascade treats
/// the rules inside a layer block as ordinary rules in their place.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Css.cs" region="parse"/>
/// </example>
public sealed class CssLayerRule : CssRule
{
    internal CssLayerRule(IReadOnlyList<string> names, IReadOnlyList<CssRule>? rules)
    {
        Names = names;
        Rules = rules;
    }

    /// <summary>Gets the layer names: one for a block (empty for an anonymous layer), one or more for a statement.</summary>
    public IReadOnlyList<string> Names { get; }

    /// <summary>Gets the rules of a block, or <see langword="null"/> for a statement.</summary>
    public IReadOnlyList<CssRule>? Rules { get; }
}

/// <summary>An at-rule Egami.Css does not interpret, such as <c>@keyframes</c> or <c>@counter-style</c>, kept as component values.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Css.cs" region="parse"/>
/// </example>
public sealed class CssUnknownAtRule : CssRule
{
    internal CssUnknownAtRule(string name, IReadOnlyList<CssComponentValue> prelude, CssSimpleBlock? block)
    {
        Name = name;
        Prelude = prelude;
        Block = block;
    }

    /// <summary>Gets the at-rule's name in lowercase, without the <c>@</c>.</summary>
    public string Name { get; }

    /// <summary>Gets the prelude: the component values between the name and the block or semicolon.</summary>
    public IReadOnlyList<CssComponentValue> Prelude { get; }

    /// <summary>Gets the <c>{}</c> block, or <see langword="null"/> for a statement ending in a semicolon.</summary>
    public CssSimpleBlock? Block { get; }
}

/// <summary>An <c>@supports</c> condition, evaluated against a callback.</summary>
internal abstract class SupportsNode
{
    public abstract bool Evaluate(Func<CssDeclaration, bool> supports);

    public static SupportsNode? Parse(IReadOnlyList<CssComponentValue> items, CssNamespaces? namespaces)
    {
        int position = 0;
        SupportsNode? node = ParseCondition(items, ref position, namespaces);
        SkipWhitespace(items, ref position);
        return node is not null && position == items.Count ? node : null;
    }

    private static void SkipWhitespace(IReadOnlyList<CssComponentValue> items, ref int position)
    {
        while (position < items.Count && items[position].IsWhitespace)
        {
            position++;
        }
    }

    private static SupportsNode? ParseCondition(IReadOnlyList<CssComponentValue> items, ref int position, CssNamespaces? namespaces)
    {
        SkipWhitespace(items, ref position);
        if (position < items.Count && items[position].IsIdent("not"))
        {
            position++;
            int before = position;
            SkipWhitespace(items, ref position);
            if (position == before)
            {
                return null;
            }

            SupportsNode? operand = ParseInParens(items, ref position, namespaces);
            return operand is null ? null : new Not(operand);
        }

        SupportsNode? first = ParseInParens(items, ref position, namespaces);
        if (first is null)
        {
            return null;
        }

        List<SupportsNode> operands = [first];
        string? joiner = null;
        while (true)
        {
            int save = position;
            SkipWhitespace(items, ref position);
            if (position == save || position >= items.Count || items[position] is not CssTokenValue { Token: { Kind: CssTokenKind.Ident } word })
            {
                position = save;
                break;
            }

            string lower = CssText.ToAsciiLower(word.Value);
            if (lower is not ("and" or "or") || (joiner is not null && joiner != lower))
            {
                return null;
            }

            joiner = lower;
            position++;
            int beforeWhitespace = position;
            SkipWhitespace(items, ref position);
            if (position == beforeWhitespace)
            {
                return null;
            }

            SupportsNode? next = ParseInParens(items, ref position, namespaces);
            if (next is null)
            {
                return null;
            }

            operands.Add(next);
        }

        return operands.Count == 1 ? first : new Junction(joiner == "and", [.. operands]);
    }

    private static SupportsNode? ParseInParens(IReadOnlyList<CssComponentValue> items, ref int position, CssNamespaces? namespaces)
    {
        if (position >= items.Count)
        {
            return null;
        }

        CssComponentValue item = items[position];
        if (item is CssFunction function)
        {
            position++;
            if (function.NameIs("selector"))
            {
                CssSelectorList? list = CssSelectorList.TryParse(function.Arguments, namespaces);
                return new Constant(list is { Selectors.Count: 1 });
            }

            return new Constant(false);
        }

        if (item is not CssSimpleBlock { OpenToken: '(' } block)
        {
            return null;
        }

        position++;
        int inner = 0;
        SupportsNode? condition = ParseCondition(block.Content, ref inner, namespaces);
        SkipWhitespace(block.Content, ref inner);
        if (condition is not null && inner == block.Content.Count)
        {
            return condition;
        }

        // ( <declaration> ), or <general-enclosed> (false).
        List<CssComponentValue> content = CssSyntax.Trim(block.Content);
        if (content.Count > 0 && content[0] is CssTokenValue { Token.Kind: CssTokenKind.Ident })
        {
            CssSyntax.Parser parser = new(content, CssParseOptions.Default);
            if (parser.ConsumeDeclaration(nested: false, reportErrors: false) is { } declaration && parser.SkipWhitespaceToEnd())
            {
                return new Declaration(declaration);
            }
        }

        return new Constant(false);
    }

    private sealed class Not(SupportsNode operand) : SupportsNode
    {
        public override bool Evaluate(Func<CssDeclaration, bool> supports) => !operand.Evaluate(supports);
    }

    private sealed class Junction(bool isAnd, SupportsNode[] operands) : SupportsNode
    {
        public override bool Evaluate(Func<CssDeclaration, bool> supports)
        {
            foreach (SupportsNode operand in operands)
            {
                if (operand.Evaluate(supports) != isAnd)
                {
                    return !isAnd;
                }
            }

            return isAnd;
        }
    }

    private sealed class Constant(bool value) : SupportsNode
    {
        public override bool Evaluate(Func<CssDeclaration, bool> supports) => value;
    }

    private sealed class Declaration(CssDeclaration declaration) : SupportsNode
    {
        public override bool Evaluate(Func<CssDeclaration, bool> supports) => supports(declaration);
    }
}
