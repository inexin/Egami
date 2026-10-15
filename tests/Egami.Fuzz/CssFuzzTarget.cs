using System.Text;

using Egami.Css;

namespace Egami.Fuzz;

/// <summary>
/// The css fuzz target (Phase 2.10): the input, read as UTF-8, is parsed as a style sheet and as a declaration list; every
/// selector is matched against a small fixed tree; the sheet is cascaded and computed over the tree with the declarations as
/// presentation hints and inline style; the @page rules are cascaded and computed for a few pages with their margin boxes; and
/// every declaration value goes through each value parser. Egami.Css never throws on
/// input, so any exception is a crash. Egami.Css.Tests compiles this file too and feeds it the seeds and random mutations.
/// </summary>
internal static class CssFuzzTarget
{
    public static void Run(byte[] data)
    {
        string text = Encoding.UTF8.GetString(data);
        CssParseOptions options = new() { Errors = CheckError };

        IReadOnlyList<CssToken> tokens = CssTokenizer.Tokenize(text, options);
        foreach (CssToken token in tokens)
        {
            _ = token.ToString();
        }

        IReadOnlyList<CssComponentValue> values = CssComponentValue.ParseList(text, options);
        _ = CssComponentValue.Serialize(values);

        CssStyleSheet sheet = CssStyleSheet.Parse(text, options);
        IReadOnlyList<CssDeclaration> declarations = CssDeclaration.ParseList(text, options);
        FuzzElement root = FuzzElement.CreateTree();
        List<FuzzElement> elements = [.. root.Descendants()];

        List<CssDeclaration> all = [.. declarations];
        foreach (CssStyleRule rule in sheet.GetAllStyleRules())
        {
            all.AddRange(rule.Declarations);
            foreach (CssSelector selector in rule.Selectors.Selectors)
            {
                _ = selector.Specificity;
                foreach (FuzzElement element in elements)
                {
                    _ = selector.Matches(element);
                    _ = selector.Matches(element, root);
                    if (selector.PseudoElement is { } pseudo)
                    {
                        _ = selector.MatchesPseudoElement(element, pseudo);
                    }
                }
            }
        }

        Walk(sheet.Rules, all);

        if (CssSelectorList.TryParse(text, out CssSelectorList? list, sheet.Namespaces))
        {
            foreach (FuzzElement element in elements)
            {
                _ = list.Matches(element);
            }
        }

        CssMediaContext media = new() { Feature = static (_, _) => null };
        _ = CssMediaQueryList.Parse(text).Matches(media);

        // Cascade and compute over the tree, with the declaration list as presentation hints and as inline style.
        CssCascade cascade = new([(sheet, CssOrigin.Author), (sheet, CssOrigin.User)], new CssCascadeOptions { Media = media });
        Dictionary<FuzzElement, CssComputedStyle> computed = [];
        foreach (FuzzElement element in elements)
        {
            CssComputedStyle? parent = element.ParentElement is { } p ? computed[p] : null;
            CssCascadedStyle cascaded = cascade.Cascade(element, declarations, element.Id == "b" ? declarations : null);
            CssComputedStyle style = cascade.Compute(cascaded, parent);
            computed[element] = style;
            foreach (KeyValuePair<string, IReadOnlyList<CssComponentValue>> pair in style)
            {
                _ = style.GetValueText(pair.Key);
                ParseValue(pair.Value);
            }

            _ = cascade.Compute(cascade.Cascade(element, pseudoElement: "before"), style).GetValueText("color");
        }

        // Pages: unnamed and named, first, left and blank, each margin box computed from the page context.
        foreach (CssPage page in (CssPage[])[new(null, true, false), new(null, false, true, true), new("a", false, false), new("A", true, true)])
        {
            CssPageStyle pageStyle = cascade.CascadePage(page);
            CssComputedStyle context = cascade.Compute(pageStyle.Page, computed[root]);
            _ = CssPageSize.TryParse(context.TryGetValue("size", out IReadOnlyList<CssComponentValue>? size) ? size : [], out CssPageSize pageSize);
            _ = pageSize.ToString();
            foreach (KeyValuePair<CssMarginBox, CssCascadedStyle> box in pageStyle.MarginBoxes)
            {
                _ = CssPageMarginRule.GetName(box.Key);
                foreach (KeyValuePair<string, IReadOnlyList<CssComponentValue>> pair in cascade.Compute(box.Value, context))
                {
                    ParseValue(pair.Value);
                }
            }
        }

        foreach (CssDeclaration declaration in all)
        {
            _ = declaration.ToString();
            ParseValue(declaration.Value);
            _ = CssTransformList.TryParseSvg(declaration.ValueText, out _);
        }

        ParseValue(values);
        _ = CssTransformList.TryParseSvg(text, out _);
        _ = CssFontFace.FromDeclarations(all);
    }

    private static void Walk(IReadOnlyList<CssRule> rules, List<CssDeclaration> all)
    {
        foreach (CssRule rule in rules)
        {
            switch (rule)
            {
                case CssMediaRule media:
                    _ = media.Media.Matches(CssMediaContext.Default);
                    Walk(media.Rules, all);
                    break;
                case CssSupportsRule supports:
                    _ = supports.Matches(static _ => true);
                    Walk(supports.Rules, all);
                    break;
                case CssLayerRule { Rules: { } layerRules }:
                    Walk(layerRules, all);
                    break;
                case CssFontFaceRule fontFace:
                    all.AddRange(fontFace.Declarations);
                    _ = fontFace.FontFace.UnicodeRanges.Count;
                    break;
                case CssImportRule import:
                    _ = import.Media.Matches(CssMediaContext.Default);
                    break;
                case CssPageRule page:
                    _ = page.SelectorText;
                    all.AddRange(page.Declarations);
                    foreach (CssPageSelector selector in page.Selectors)
                    {
                        _ = selector.Matches(new CssPage(selector.Name, true, false, true));
                    }

                    foreach (CssPageMarginRule margin in page.MarginRules)
                    {
                        all.AddRange(margin.Declarations);
                    }

                    break;
            }
        }
    }

    private static void ParseValue(IReadOnlyList<CssComponentValue> value)
    {
        _ = CssColor.TryParse(value, out CssColor color);
        _ = color.ToString();
        _ = CssLength.TryParse(value, out _);
        if (CssCalc.TryParse(value, out CssCalc? calc))
        {
            _ = calc.Resolve(static _ => 1.5);
        }

        if (CssTransformList.TryParse(value, out CssTransformList? transform))
        {
            _ = transform.ToMatrix(static (length, _) => length.Value);
        }

        _ = CssTransformOrigin.TryParse(value, out _);
        if (CssPageSize.TryParse(value, out CssPageSize size))
        {
            _ = size.ToString();
        }

        if (CssFont.TryParse(value, out CssFont? font))
        {
            _ = font.ToString();
        }

        if (CssFontFamilyList.TryParse(value, out CssFontFamilyList? families))
        {
            _ = families.ToString();
        }

        if (CssFontWeight.TryParse(value, out CssFontWeight weight))
        {
            _ = weight.Resolve(400);
        }

        foreach (CssComponentValue item in value)
        {
            _ = CssUrl.TryParse(item, out _);
            _ = CssAngle.TryParse(item, true, out _);
            _ = CssColor.TryParse(item, out _);
            _ = CssCalc.TryParse(item, out _);
        }

        _ = CssFontFace.FromDeclarations([new CssDeclaration("unicode-range", value), new CssDeclaration("src", value), new CssDeclaration("font-style", value)]);
    }

    private static void CheckError(CssParseError error)
    {
        if (error.Line < 0 || error.Column < 0 || string.IsNullOrWhiteSpace(error.Message))
        {
            throw new InvalidOperationException($"A malformed parse error: {error}.");
        }
    }

    /// <summary>A small fixed tree: an SVG root with groups, shapes, text and an HTML island.</summary>
    private sealed class FuzzElement(string localName, string? namespaceUri, params (string Name, string Value)[] attributes) : ICssElement
    {
        private const string Svg = "http://www.w3.org/2000/svg";
        private const string Html = "http://www.w3.org/1999/xhtml";

        private readonly List<FuzzElement> _children = [];

        public string LocalName { get; } = localName;

        public string? NamespaceUri { get; } = namespaceUri;

        public FuzzElement? ParentElement { get; private set; }

        public ICssElement? Parent => ParentElement;

        public ICssElement? PreviousSibling { get; private set; }

        public ICssElement? NextSibling { get; private set; }

        public ICssElement? FirstChild => _children.Count > 0 ? _children[0] : null;

        public bool IsEmpty => _children.Count == 0;

        public string? Id => GetAttribute("id", null);

        public bool IsHtml => NamespaceUri == Html;

        public IEnumerable<CssAttribute> Attributes => attributes.Select(a => new CssAttribute(a.Name == "lang" && LocalName == "text" ? ICssElement.XmlNamespace : null, a.Name, a.Value));

        public bool HasClass(string name)
        {
            string? value = GetAttribute("class", null);
            return value is not null && value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(name);
        }

        public string? GetAttribute(string localName, string? namespaceUri)
        {
            foreach (CssAttribute attribute in Attributes)
            {
                if (attribute.LocalName == localName && attribute.NamespaceUri == namespaceUri)
                {
                    return attribute.Value;
                }
            }

            return null;
        }

        public bool MatchesPseudoClass(string name) => name == "hover" && Id == "b";

        public FuzzElement Add(FuzzElement child)
        {
            child.ParentElement = this;
            if (_children.Count > 0)
            {
                _children[^1].NextSibling = child;
                child.PreviousSibling = _children[^1];
            }

            _children.Add(child);
            return this;
        }

        public IEnumerable<FuzzElement> Descendants()
        {
            yield return this;
            foreach (FuzzElement child in _children)
            {
                foreach (FuzzElement descendant in child.Descendants())
                {
                    yield return descendant;
                }
            }
        }

        public static FuzzElement CreateTree()
        {
            FuzzElement root = new("svg", Svg, ("id", "root"), ("lang", "en-US"), ("class", "a b"));
            FuzzElement group = new("g", Svg, ("id", "g1"), ("class", "group a"), ("transform", "rotate(45)"));
            group.Add(new FuzzElement("rect", Svg, ("id", "b"), ("class", "shape"), ("fill", "red"), ("width", "10")))
                .Add(new FuzzElement("circle", Svg, ("class", "shape round"), ("r", "5")))
                .Add(new FuzzElement("rect", Svg, ("id", "c"), ("data-x", "1 2 3")));
            FuzzElement text = new("text", Svg, ("lang", "fr-CA"), ("dir", "rtl"));
            text.Add(new FuzzElement("tspan", Svg, ("class", "x")));
            FuzzElement island = new("foreignObject", Svg);
            island.Add(new FuzzElement("div", Html, ("class", "Note"), ("type", "Button")).Add(new FuzzElement("p", Html, ("id", "p1"))));
            root.Add(new FuzzElement("defs", Svg)).Add(group).Add(text).Add(island).Add(new FuzzElement("unknown", null, ("id", "")));
            return root;
        }
    }
}
