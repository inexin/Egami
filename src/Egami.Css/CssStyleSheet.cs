namespace Egami.Css;

/// <summary>A parsed CSS style sheet (CSS Syntax 3 "parse a stylesheet", with the rules CSS defines interpreted).</summary>
/// <remarks>
/// Parsing never throws on bad CSS: it recovers as CSS Syntax 3 says and reports each problem to
/// <see cref="CssParseOptions.Errors"/>. Style rules whose selector does not parse are dropped; <c>@import</c> and
/// <c>@namespace</c> rules after other rules are dropped; rules nested in style rules (CSS Nesting) are dropped.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Css.cs" region="parse"/>
/// </example>
public sealed class CssStyleSheet
{
    private CssStyleSheet(IReadOnlyList<CssRule> rules, CssNamespaces namespaces)
    {
        Rules = rules;
        Namespaces = namespaces;
    }

    /// <summary>Gets the top-level rules, in order.</summary>
    public IReadOnlyList<CssRule> Rules { get; }

    /// <summary>Gets the namespaces the sheet's <c>@namespace</c> rules declare.</summary>
    public CssNamespaces Namespaces { get; }

    /// <summary>Parses a style sheet.</summary>
    /// <param name="css">The CSS text.</param>
    /// <param name="options">Options for error reporting.</param>
    /// <returns>The style sheet.</returns>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Css.cs" region="parse"/>
    /// </example>
    public static CssStyleSheet Parse(string css, CssParseOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(css);
        options ??= CssParseOptions.Default;
        List<CssComponentValue> items = CssSyntax.ParseComponentValues(css, options);
        CssSyntax.Parser parser = new(items, options);
        List<object> raw = parser.ConsumeRuleList(topLevel: true);
        CssNamespaces namespaces = new();
        Builder builder = new(options, namespaces);
        List<CssRule> rules = builder.Build(raw, topLevel: true);
        return new CssStyleSheet(rules, namespaces);
    }

    /// <summary>Enumerates every style rule, including those inside <c>@media</c>, <c>@supports</c> and <c>@layer</c> blocks, whether or not their conditions hold.</summary>
    /// <returns>The style rules in order.</returns>
    public IEnumerable<CssStyleRule> GetAllStyleRules()
    {
        Stack<IEnumerator<CssRule>> stack = new();
        stack.Push(Rules.GetEnumerator());
        while (stack.Count > 0)
        {
            IEnumerator<CssRule> enumerator = stack.Peek();
            if (!enumerator.MoveNext())
            {
                stack.Pop().Dispose();
                continue;
            }

            switch (enumerator.Current)
            {
                case CssStyleRule style:
                    yield return style;
                    break;
                case CssMediaRule media:
                    stack.Push(media.Rules.GetEnumerator());
                    break;
                case CssSupportsRule supports:
                    stack.Push(supports.Rules.GetEnumerator());
                    break;
                case CssLayerRule { Rules: { } layerRules }:
                    stack.Push(layerRules.GetEnumerator());
                    break;
            }
        }
    }

    private sealed class Builder(CssParseOptions options, CssNamespaces namespaces)
    {
        // The import window closes at the first rule other than @charset, @import or an @layer statement; the namespace
        // window at the first rule other than those and @namespace.
        private bool _importAllowed = true;
        private bool _namespaceAllowed = true;

        public List<CssRule> Build(List<object> raw, bool topLevel)
        {
            List<CssRule> rules = [];
            foreach (object item in raw)
            {
                CssRule? rule = item switch
                {
                    RawAtRule atRule => BuildAtRule(atRule, topLevel),
                    RawQualifiedRule qualified => BuildStyleRule(qualified, topLevel),
                    _ => null,
                };
                if (rule is not null)
                {
                    rules.Add(rule);
                }
            }

            return rules;
        }

        private void CloseWindows(bool topLevel)
        {
            if (topLevel)
            {
                _importAllowed = false;
                _namespaceAllowed = false;
            }
        }

        private CssStyleRule? BuildStyleRule(RawQualifiedRule raw, bool topLevel)
        {
            CloseWindows(topLevel);
            CssSelectorList? selectors = CssSelectorList.TryParse(raw.Prelude, namespaces);
            if (selectors is null)
            {
                options.Report(raw.Line, raw.Column, $"The selector '{CssComponentValue.Serialize(CssSyntax.Trim(raw.Prelude))}' is invalid; the rule was dropped.");
                return null;
            }

            List<CssDeclaration> declarations = CssDeclaration.ParseList(raw.Block.Content, options, "a style rule");
            return new CssStyleRule(selectors, declarations) { Line = raw.Line, Column = raw.Column };
        }

        private List<CssRule> BuildGroup(CssSimpleBlock block)
        {
            CssSyntax.Parser parser = new(block.Content, options);
            return Build(parser.ConsumeRuleList(topLevel: false), topLevel: false);
        }

        private CssRule? BuildAtRule(RawAtRule raw, bool topLevel)
        {
            string name = CssText.ToAsciiLower(raw.Name);
            List<CssComponentValue> prelude = CssSyntax.Trim(raw.Prelude);
            switch (name)
            {
                case "charset":
                    return null;
                case "import":
                    if (!topLevel || !_importAllowed || raw.Block is not null)
                    {
                        Drop(raw, "is not allowed here");
                        return null;
                    }

                    return BuildImport(raw, prelude);
                case "namespace":
                    if (!topLevel || !_namespaceAllowed || raw.Block is not null)
                    {
                        Drop(raw, "is not allowed here");
                        return null;
                    }

                    _importAllowed = false;
                    return BuildNamespace(raw, prelude);
                case "media":
                    CloseWindows(topLevel);
                    if (raw.Block is null)
                    {
                        Drop(raw, "has no block");
                        return null;
                    }

                    return new CssMediaRule(CssMediaQueryList.Parse(prelude), BuildGroup(raw.Block)) { Line = raw.Line, Column = raw.Column };
                case "supports":
                    CloseWindows(topLevel);
                    if (raw.Block is null)
                    {
                        Drop(raw, "has no block");
                        return null;
                    }

                    CssSupportsRule supports = new(prelude, BuildGroup(raw.Block), namespaces) { Line = raw.Line, Column = raw.Column };
                    if (!supports.IsValid)
                    {
                        options.Report(raw.Line, raw.Column, "The @supports condition is invalid; the rule never applies.");
                    }

                    return supports;
                case "layer":
                    return BuildLayer(raw, prelude, topLevel);
                case "font-face":
                    CloseWindows(topLevel);
                    if (raw.Block is null)
                    {
                        Drop(raw, "has no block");
                        return null;
                    }

                    return new CssFontFaceRule(CssDeclaration.ParseList(raw.Block.Content, options, "@font-face")) { Line = raw.Line, Column = raw.Column };
                case "page":
                    CloseWindows(topLevel);
                    return BuildPage(raw, prelude);
                default:
                    CloseWindows(topLevel);
                    return new CssUnknownAtRule(name, raw.Prelude, raw.Block) { Line = raw.Line, Column = raw.Column };
            }
        }

        private CssPageRule? BuildPage(RawAtRule raw, List<CssComponentValue> prelude)
        {
            if (raw.Block is null)
            {
                Drop(raw, "has no block");
                return null;
            }

            List<CssPageSelector>? selectors = CssPageSelector.TryParseList(prelude);
            if (selectors is null)
            {
                options.Report(raw.Line, raw.Column, $"The @page selector '{CssComponentValue.Serialize(prelude)}' is invalid; the rule was dropped.");
                return null;
            }

            // Declarations for the page context and page-margin rules (CSS Paged Media 3 section 9.1).
            List<CssDeclaration> declarations = [];
            List<CssPageMarginRule> marginRules = [];
            List<object> parsed = [];
            new CssSyntax.Parser(raw.Block.Content, options).ConsumeBlockContents(parsed);
            foreach (object item in parsed)
            {
                switch (item)
                {
                    case CssDeclaration declaration:
                        declarations.Add(declaration);
                        break;
                    case RawAtRule margin when CssPageMarginRule.TryGetBox(CssText.ToAsciiLower(margin.Name), out CssMarginBox box) && margin.Block is not null && CssSyntax.Trim(margin.Prelude).Count == 0:
                        marginRules.Add(new CssPageMarginRule(box, CssDeclaration.ParseList(margin.Block.Content, options, "a page-margin rule")) { Line = margin.Line, Column = margin.Column });
                        break;
                    case RawAtRule other:
                        options.Report(other.Line, other.Column, $"The at-rule @{other.Name} is not allowed in @page; it was dropped.");
                        break;
                    case RawQualifiedRule qualified:
                        options.Report(qualified.Line, qualified.Column, "A nested rule in @page was dropped.");
                        break;
                }
            }

            return new CssPageRule(selectors, declarations, marginRules) { Line = raw.Line, Column = raw.Column };
        }

        private void Drop(RawAtRule raw, string reason) => options.Report(raw.Line, raw.Column, $"The @{raw.Name} rule {reason}; it was dropped.");

        private CssImportRule? BuildImport(RawAtRule raw, List<CssComponentValue> prelude)
        {
            if (prelude.Count == 0 || !TryUrlOrString(prelude[0], out string? url))
            {
                Drop(raw, "has no URL");
                return null;
            }

            int i = 1;
            SkipWhitespace(prelude, ref i);
            string? layer = null;
            if (i < prelude.Count && prelude[i].IsIdent("layer"))
            {
                layer = "";
                i++;
            }
            else if (i < prelude.Count && prelude[i] is CssFunction { } layerFunction && layerFunction.NameIs("layer"))
            {
                layer = CssComponentValue.Serialize(CssSyntax.Trim(layerFunction.Arguments));
                i++;
            }

            SkipWhitespace(prelude, ref i);
            string? supports = null;
            if (i < prelude.Count && prelude[i] is CssFunction { } supportsFunction && supportsFunction.NameIs("supports"))
            {
                supports = CssComponentValue.Serialize(CssSyntax.Trim(supportsFunction.Arguments));
                i++;
            }

            CssMediaQueryList media = CssMediaQueryList.Parse(prelude.GetRange(i, prelude.Count - i));
            return new CssImportRule(url, media, layer, supports) { Line = raw.Line, Column = raw.Column };
        }

        private CssNamespaceRule? BuildNamespace(RawAtRule raw, List<CssComponentValue> prelude)
        {
            string? prefix = null;
            int i = 0;
            if (prelude.Count > 0 && prelude[0] is CssTokenValue { Token: { Kind: CssTokenKind.Ident } ident })
            {
                prefix = ident.Value;
                i = 1;
                SkipWhitespace(prelude, ref i);
            }

            if (i != prelude.Count - 1 || !TryUrlOrString(prelude[i], out string? uri))
            {
                Drop(raw, "is malformed");
                return null;
            }

            namespaces.Set(prefix, uri);
            return new CssNamespaceRule(prefix, uri) { Line = raw.Line, Column = raw.Column };
        }

        private CssLayerRule? BuildLayer(RawAtRule raw, List<CssComponentValue> prelude, bool topLevel)
        {
            List<string> names = [];
            List<CssComponentValue> current = [];
            for (int i = 0; i <= prelude.Count; i++)
            {
                if (i < prelude.Count && !prelude[i].IsToken(CssTokenKind.Comma))
                {
                    current.Add(prelude[i]);
                    continue;
                }

                List<CssComponentValue> trimmed = CssSyntax.Trim(current);
                if (trimmed.Count > 0 || i < prelude.Count)
                {
                    if (!IsLayerName(trimmed))
                    {
                        Drop(raw, "has an invalid layer name");
                        CloseWindows(topLevel);
                        return null;
                    }

                    names.Add(CssComponentValue.Serialize(trimmed));
                }

                current.Clear();
            }

            if (raw.Block is null)
            {
                if (names.Count == 0)
                {
                    Drop(raw, "names no layer");
                    return null;
                }

                return new CssLayerRule(names, null) { Line = raw.Line, Column = raw.Column };
            }

            CloseWindows(topLevel);
            if (names.Count > 1)
            {
                Drop(raw, "block names more than one layer");
                return null;
            }

            return new CssLayerRule(names.Count == 0 ? [""] : names, BuildGroup(raw.Block)) { Line = raw.Line, Column = raw.Column };

            static bool IsLayerName(List<CssComponentValue> name)
            {
                // <ident> [ '.' <ident> ]*, with nothing between.
                for (int i = 0; i < name.Count; i++)
                {
                    bool ok = i % 2 == 0 ? name[i] is CssTokenValue { Token.Kind: CssTokenKind.Ident } : name[i].IsDelim('.');
                    if (!ok)
                    {
                        return false;
                    }
                }

                return name.Count % 2 == 1;
            }
        }

        private static void SkipWhitespace(List<CssComponentValue> items, ref int i)
        {
            while (i < items.Count && items[i].IsWhitespace)
            {
                i++;
            }
        }

        private static bool TryUrlOrString(CssComponentValue value, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? url)
        {
            if (value is CssTokenValue { Token: { Kind: CssTokenKind.String } text })
            {
                url = text.Value;
                return true;
            }

            if (CssUrl.TryParse(value, out CssUrl parsed))
            {
                url = parsed.Url;
                return true;
            }

            url = null;
            return false;
        }
    }
}
