using System.Numerics;

using Egami.Css;

namespace Egami.DocSamples;

[Collection("Samples")]
public class Css
{
    [Fact]
    public void ParseASheet()
    {
        #region parse
        // Parsing never throws on bad CSS: it recovers as CSS Syntax 3 says and reports each problem.
        var errors = new List<CssParseError>();
        var options = new CssParseOptions { Errors = errors.Add };
        CssStyleSheet sheet = CssStyleSheet.Parse("""
            @import url(print.css) print;
            @namespace svg url(http://www.w3.org/2000/svg);
            rect.note, svg|circle { fill: gold !important; stroke: #333 }
            @media (max-width: 600px) { rect { stroke-width: 2px } }
            @supports (display: grid) { g { display: grid } }
            @font-face { font-family: "Inter"; src: url(inter.woff2) format("woff2") }
            @layer base { text { fill: gray } }
            @keyframes spin { to { rotate: 1turn } }
            p:unknown { color: red }
            """, options);

        foreach (CssRule rule in sheet.Rules)
        {
            switch (rule)
            {
                case CssStyleRule style:
                    Console.WriteLine($"{style.SelectorText}: {style.Declarations.Count} declarations");
                    break;
                case CssMediaRule media:
                    Console.WriteLine($"@media {media.Media.MediaText}: {media.Rules.Count} rules");
                    break;
                case CssSupportsRule supports:
                    Console.WriteLine($"@supports {supports.ConditionText}");
                    break;
                case CssFontFaceRule fontFace:
                    Console.WriteLine($"@font-face {fontFace.FontFace.Family}");
                    break;
                case CssImportRule import:
                    Console.WriteLine($"@import {import.Url} for {import.Media.MediaText}");
                    break;
                case CssNamespaceRule ns:
                    Console.WriteLine($"@namespace {ns.Prefix} = {ns.Uri}");
                    break;
                case CssLayerRule layer:
                    Console.WriteLine($"@layer {string.Join(", ", layer.Names)}");
                    break;
                case CssUnknownAtRule other:
                    Console.WriteLine($"@{other.Name} (kept, not interpreted)");
                    break;
            }
        }

        // The rule with ':unknown' was dropped and reported.
        CssParseError dropped = errors[0];
        Console.WriteLine($"{dropped.Line}:{dropped.Column} {dropped.Message}");
        #endregion

        Assert.Equal(8, sheet.Rules.Count);
        Assert.Contains(errors, e => e.Message.Contains("p:unknown", StringComparison.Ordinal));
    }

    [Fact]
    public void Declarations()
    {
        #region declarations
        // A style attribute, and a declaration made from a presentation attribute such as SVG's fill="red".
        IReadOnlyList<CssDeclaration> inline = CssDeclaration.ParseList("color: Red; --Accent: #0af; font-size: 12px !important");
        CssDeclaration size = inline[2];
        Console.WriteLine($"{size.Name} = {size.ValueText}, important: {size.Important}"); // font-size = 12px, important: True

        CssDeclaration fill = CssDeclaration.Create("fill", "red");
        #endregion

        Assert.Equal("--Accent", inline[1].Name);
        Assert.Equal("red", fill.ValueText);
    }

    [Fact]
    public void Tokens()
    {
        #region tokens
        // CSS Syntax 3 tokens, and component values: tokens, functions and simple blocks.
        IReadOnlyList<CssToken> tokens = CssTokenizer.Tokenize("rgb(255 0 0 / 50%) 12px");
        CssToken first = tokens[0];
        bool isFunction = first.Kind == CssTokenKind.Function; // "rgb(" at line 1, column 1

        IReadOnlyList<CssComponentValue> values = CssComponentValue.ParseList("calc(100% - 2px) [a] x");
        var calc = (CssFunction)values[0];
        var block = (CssSimpleBlock)values[2];
        var word = (CssTokenValue)values[4];
        string text = CssComponentValue.Serialize(values); // "calc(100% - 2px) [a] x"
        #endregion

        Assert.True(isFunction);
        Assert.Equal("calc", calc.Name);
        Assert.Equal('[', block.OpenToken);
        Assert.Equal("x", word.Token.Value);
        Assert.Equal("calc(100% - 2px) [a] x", text);
    }

    #region element
    // A document tree implements ICssElement; Egami.Svg's elements do. The members must not allocate in the common case.
    public sealed class Element(string localName, string? namespaceUri = "http://www.w3.org/2000/svg") : ICssElement
    {
        private readonly List<Element> _children = [];
        private readonly List<CssAttribute> _attributes = [];

        public string LocalName { get; } = localName;
        public string? NamespaceUri { get; } = namespaceUri;
        public Element? ParentElement { get; private set; }
        public ICssElement? Parent => ParentElement;
        public ICssElement? PreviousSibling => Sibling(-1);
        public ICssElement? NextSibling => Sibling(1);
        public ICssElement? FirstChild => _children.Count > 0 ? _children[0] : null;
        public bool IsEmpty => _children.Count == 0;
        public string? Id => GetAttribute("id", null);
        public IEnumerable<CssAttribute> Attributes => _attributes;

        public bool HasClass(string name) => GetAttribute("class", null)?.Split(' ').Contains(name) == true;

        public string? GetAttribute(string localName, string? namespaceUri) =>
            _attributes.Find(a => a.LocalName == localName && a.NamespaceUri == namespaceUri) is { LocalName: not null } a ? a.Value : null;

        public Element Set(string name, string value)
        {
            _attributes.Add(new CssAttribute(null, name, value));
            return this;
        }

        public Element Add(Element child)
        {
            child.ParentElement = this;
            _children.Add(child);
            return this;
        }

        private Element? Sibling(int step)
        {
            if (ParentElement is null)
            {
                return null;
            }

            int index = ParentElement._children.IndexOf(this) + step;
            return index >= 0 && index < ParentElement._children.Count ? ParentElement._children[index] : null;
        }
    }
    #endregion

    private static (Element Root, Element Group, Element Rect) Tree()
    {
        var rect = new Element("rect").Set("id", "r").Set("class", "note");
        var group = new Element("g").Set("class", "layer").Add(rect).Add(new Element("circle"));
        var root = new Element("svg").Add(group);
        return (root, group, rect);
    }

    [Fact]
    public void Selectors()
    {
        (_, Element group, Element rect) = Tree();

        #region selectors
        // Selectors 4, matched right to left over ICssElement.
        CssSelectorList list = CssSelectorList.Parse("g.layer > rect:first-child, :is(circle, ellipse)");
        bool matches = list.Matches(rect);

        CssSelector selector = list.Selectors[0];
        CssSpecificity specificity = selector.Specificity; // (0, 2, 2)

        // Namespace prefixes, as @namespace declares them.
        var namespaces = new CssNamespaces();
        namespaces.Set("svg", "http://www.w3.org/2000/svg");
        bool inSvg = CssSelectorList.Parse("svg|rect", namespaces).Matches(rect);

        // A pseudo-element never matches the element itself; the cascade asks for it by name.
        CssSelector before = CssSelectorList.Parse("rect::before").Selectors[0];
        bool beforeMatches = before.MatchesPseudoElement(rect, "before");

        bool valid = CssSelectorList.TryParse("rect:nonsense", out _); // false: unknown pseudo-classes are invalid
        #endregion

        Assert.True(matches);
        Assert.Equal(new CssSpecificity(0, 2, 2), specificity);
        Assert.True(inSvg);
        Assert.True(beforeMatches);
        Assert.False(valid);
        Assert.True(list.Matches(group.FirstChild!));
    }

    [Fact]
    public void Cascade()
    {
        (Element root, Element group, Element rect) = Tree();

        #region cascade
        // The registry says which properties inherit and their initial values; consumers add their own.
        CssPropertyRegistry registry = CssPropertyRegistry.CreateDefault();
        registry.Register(new CssPropertyDefinition("fill", Inherited: true, InitialValue: "black")
        {
            Validate = value => CssColor.TryParse(value, out _),
        });
        registry.RegisterShorthand(new CssShorthandDefinition("marker", ["marker-start", "marker-end"],
            value => [new CssDeclaration("marker-start", value), new CssDeclaration("marker-end", value)]));

        CssStyleSheet userAgent = CssStyleSheet.Parse("rect { fill: gray }");
        CssStyleSheet author = CssStyleSheet.Parse("""
            svg { --accent: teal; font: 12px sans-serif }
            .layer { fill: var(--accent) }
            #r { fill: inherit; opacity: 0.5 }
            """);
        var cascade = new CssCascade(
            [(userAgent, CssOrigin.UserAgent), (author, CssOrigin.Author)],
            new CssCascadeOptions { Registry = registry, Media = new CssMediaContext { Width = 800 } });

        // Cascade each element (presentation attributes as hints, the style attribute as inline style), then compute it
        // from its parent's computed style.
        CssComputedStyle rootStyle = cascade.Compute(cascade.Cascade(root), null);
        CssComputedStyle groupStyle = cascade.Compute(cascade.Cascade(group), rootStyle);
        CssCascadedStyle cascaded = cascade.Cascade(rect,
            presentationHints: [CssDeclaration.Create("fill", "red")],
            inlineStyle: CssDeclaration.ParseList("stroke: blue"));
        CssComputedStyle style = cascade.Compute(cascaded, groupStyle);

        cascaded.TryGetDeclaration("fill", out CssDeclaration? winner, out CssOrigin origin); // fill: inherit, Author
        string? fill = style.GetValueText("fill");           // "teal": inherited, var() substituted
        string? fontSize = style.GetValueText("font-size");  // "12px": from the font shorthand, inherited
        if (style.TryGetValue("opacity", out IReadOnlyList<CssComponentValue>? opacity))
        {
            // The consumer parses each property by its type.
        }
        #endregion

        Assert.Equal("inherit", winner!.ValueText);
        Assert.Equal(CssOrigin.Author, origin);
        Assert.Equal("teal", fill);
        Assert.Equal("12px", fontSize);
        Assert.Equal("0.5", CssComponentValue.Serialize(opacity!));
        Assert.Equal("blue", style.GetValueText("stroke"));
    }

    [Fact]
    public void Media()
    {
        #region media
        // Media Queries 4: types, features, min-/max- and range syntax. Unknown features count as false.
        var phone = new CssMediaContext { Width = 390, Height = 844, Resolution = 3, PrefersColorScheme = "dark" };
        bool narrow = CssMediaQueryList.Parse("screen and (width < 600px)").Matches(phone);
        bool dark = CssMediaQueryList.Parse("(prefers-color-scheme: dark) and (min-resolution: 2dppx)").Matches(phone);

        // Features the context does not know go to a hook.
        CssMediaContext touch = phone with { Feature = (name, value) => name == "pointer" ? value?[0].ToString() == "coarse" : null };
        bool coarse = CssMediaQueryList.Parse("(pointer: coarse)").Matches(touch);
        #endregion

        Assert.True(narrow);
        Assert.True(dark);
        Assert.True(coarse);
    }

    [Fact]
    public void Colors()
    {
        #region colors
        // Every CSS Color 4 syntax, converted to sRGB (0 to 1) and clipped to its gamut.
        bool isColor = CssColor.TryParse("oklch(62.8% 0.258 29.2 / 50%)", out CssColor red);
        (byte r, byte g, byte b, byte a) = red.ToRgba8();         // about (255, 0, 0, 128)
        bool newer = red.Level == 4;                             // SVG 1.1 knows only level 3

        bool isCurrent = CssColor.TryParse("currentColor", out CssColor current);  // current.IsCurrentColor: use the 'color' property
        #endregion

        Assert.True(isColor && isCurrent);

        Assert.InRange(r, 254, 255);
        Assert.InRange(g + b, 0, 2);
        Assert.InRange(a, 127, 128);
        Assert.True(newer);
        Assert.True(current.IsCurrentColor);
    }

    [Fact]
    public void Lengths()
    {
        #region lengths
        bool isLength = CssLength.TryParse("1.5em", out CssLength length);       // (1.5, CssUnit.Em)
        new CssLength(1, CssUnit.In).TryToPixels(out double px);  // 96
        bool isAngle = CssAngle.TryParse("0.25turn", out CssAngle angle);        // 90 degrees
        bool isUrl = CssUrl.TryParse("url('pattern.svg#dots')", out CssUrl url);

        // calc(), min(), max() and clamp(): the consumer says what each unit is worth.
        bool isCalc = CssCalc.TryParse("calc(100% - 2em)", out CssCalc? calc);
        double width = calc!.Resolve(unit => unit switch
        {
            CssUnit.Percent => 3,   // a percent of a 300px box
            CssUnit.Em => 16,
            CssUnit.Px => 1,
            _ => double.NaN,
        });                                                       // 268
        #endregion

        Assert.True(isLength && isAngle && isUrl && isCalc);

        Assert.Equal(new CssLength(1.5, CssUnit.Em), length);
        Assert.Equal(96, px);
        Assert.Equal(90, angle.Degrees, 1e-9);
        Assert.Equal("pattern.svg#dots", url.Url);
        Assert.Equal(268, width);
    }

    [Fact]
    public void Transforms()
    {
        #region transforms
        // A CSS transform list, its matrix, and the functions it holds.
        bool isTransform = CssTransformList.TryParse("translate(10px, 50%) rotate(45deg) scale(2)", out CssTransformList? css);
        Matrix3x2 matrix = css!.ToMatrix((length, isX) => length.Unit == CssUnit.Percent ? length.Value * (isX ? 2 : 1) : length.Value);

        foreach (CssTransformFunction function in css.Functions)
        {
            string kind = function switch
            {
                CssTranslateTransform t => $"translate {t.X} {t.Y}",
                CssRotateTransform r => $"rotate {r.Degrees} about ({r.CenterX}, {r.CenterY})",
                CssScaleTransform s => $"scale {s.X} {s.Y}",
                CssSkewTransform k => $"skew {k.XDegrees} {k.YDegrees}",
                CssMatrixTransform m => $"matrix {m.A} {m.B} {m.C} {m.D} {m.E} {m.F}",
                CssUnsupportedTransform u => $"{u.Name} (3D, ignored)",
                _ => "",
            };
        }

        bool isOrigin = CssTransformOrigin.TryParse("left bottom", out CssTransformOrigin origin); // (0%, 100%, 0)
        #endregion

        Assert.True(isTransform && isOrigin);

        Assert.Equal(10, matrix.M31, 1e-4f);
        Assert.Equal(3, css.Functions.Count);
        Assert.Equal(new CssLength(100, CssUnit.Percent), origin.Y);
    }

    [Fact]
    public void Fonts()
    {
        #region fonts
        bool isFont = CssFont.TryParse("italic bold 14px/1.4 \"Source Sans 3\", sans-serif", out CssFont? font);
        CssFontWeight weight = font!.Weight;                     // 700
        foreach (CssFontFamily family in font.Families!.Families)
        {
            Console.WriteLine($"{family.Name} (generic: {family.IsGeneric})");
        }

        bool isFamilyList = CssFontFamilyList.TryParse("Georgia, serif", out CssFontFamilyList? families);
        bool isWeight = CssFontWeight.TryParse("bolder", out CssFontWeight bolder);
        double resolved = bolder.Kind == CssFontWeightKind.Bolder ? bolder.Resolve(400) : bolder.Value; // 700

        // @font-face descriptors.
        CssStyleSheet sheet = CssStyleSheet.Parse("""
            @font-face {
              font-family: "Inter";
              src: local("Inter"), url(inter.woff2) format("woff2") tech(variations);
              font-weight: 100 900;
              unicode-range: U+0000-00FF, U+20AC;
            }
            """);
        CssFontFace face = ((CssFontFaceRule)sheet.Rules[0]).FontFace;
        CssFontSource source = face.Sources[1];                  // inter.woff2, format woff2, tech variations
        CssRange weights = face.Weight!.Value;                   // 100 to 900
        CssUnicodeRange latin = face.UnicodeRanges[0];           // U+0-FF
        #endregion

        Assert.True(isFont && isFamilyList && isWeight);

        Assert.Equal(700, weight.Value);
        Assert.Equal(2, families!.Families.Count);
        Assert.Equal(700, resolved);
        Assert.Equal("woff2", source.Format);
        Assert.Equal(new CssRange(100, 900), weights);
        Assert.True(latin.Contains('é'));
    }
    [Fact]
    public void BoxProperties()
    {
        (Element root, Element group, Element rect) = Tree();

        #region box
        // The default registry knows the box, background, list, table, float, overflow and break properties: shorthands
        // expand into longhands, and an invalid value is dropped so an earlier one wins.
        CssStyleSheet sheet = CssStyleSheet.Parse("""
            .layer { list-style: square inside; border-collapse: collapse }
            rect {
              margin: 1em auto; padding: 2px 4px;
              border: 1px solid; border-left: thick double red;
              background: url(paper.png) no-repeat center / cover, ivory;
              float: left; overflow: hidden auto;
              page-break-before: always; break-inside: avoid;
              width: 50%; width: wide;
            }
            """);
        var cascade = new CssCascade([(sheet, CssOrigin.Author)]);
        CssComputedStyle groupStyle = cascade.Compute(cascade.Cascade(group), cascade.Compute(cascade.Cascade(root), null));
        CssComputedStyle style = cascade.Compute(cascade.Cascade(rect), groupStyle);

        string? marginLeft = style.GetValueText("margin-left");        // "auto"
        string? borderLeft = style.GetValueText("border-left-style");  // "double"
        string? borderTop = style.GetValueText("border-top-color");    // "currentcolor": left out, so initial
        string? images = style.GetValueText("background-image");       // "url(paper.png), none": one item per layer
        string? color = style.GetValueText("background-color");        // "ivory"
        string? breakBefore = style.GetValueText("break-before");      // "page": the legacy alias mapped
        string? width = style.GetValueText("width");                   // "50%": "wide" is not a width
        string? listType = style.GetValueText("list-style-type");      // "square": inherited
        #endregion

        Assert.Equal("auto", marginLeft);
        Assert.Equal("double", borderLeft);
        Assert.Equal("currentcolor", borderTop);
        Assert.Equal("url(paper.png), none", images);
        Assert.Equal("ivory", color);
        Assert.Equal("page", breakBefore);
        Assert.Equal("50%", width);
        Assert.Equal("square", listType);
        Assert.Equal("auto", style.GetValueText("overflow-y"));
    }

    [Fact]
    public void Pages()
    {
        #region page
        CssStyleSheet sheet = CssStyleSheet.Parse("""
            @page { size: A4; margin: 2cm; font: 9pt serif;
              @bottom-center { content: counter(page) } }
            @page :first { margin-top: 5cm; @bottom-center { content: none } }
            @page :left { margin-left: 3cm }
            @page appendix { size: A4 landscape }
            h1.appendix { page: appendix; break-before: right }
            """);
        CssPageRule first = (CssPageRule)sheet.Rules[1];
        CssPageSelector selector = first.Selectors[0];               // :first, specificity (0, 1, 0)
        CssPageMarginRule footer = ((CssPageRule)sheet.Rules[0]).MarginRules[0]; // CssMarginBox.BottomCenter

        // Cascade the page rules for each page the layout makes, then compute the page context from the root element's
        // computed style and each margin box from the page context's.
        var cascade = new CssCascade([(sheet, CssOrigin.Author)]);
        CssPageStyle pageStyle = cascade.CascadePage(new CssPage(Name: null, IsFirst: false, IsLeft: true));
        CssComputedStyle root = cascade.Compute(new CssCascadedStyle(), null);
        CssComputedStyle page = cascade.Compute(pageStyle.Page, root);
        string? marginLeft = page.GetValueText("margin-left");                // "3cm"
        bool isSize = CssPageSize.TryParse(page.GetValueText("size")!, out CssPageSize a4); // 210mm by 297mm
        CssComputedStyle box = cascade.Compute(pageStyle.MarginBoxes[CssMarginBox.BottomCenter], page);
        string? content = box.GetValueText("content");                        // "counter(page)"
        string? fontSize = box.GetValueText("font-size");                     // "9pt": inherited from the page context

        // A named page: the appendix pages turn A4 to landscape.
        CssPageStyle appendix = cascade.CascadePage(new CssPage("appendix", IsFirst: false, IsLeft: false));
        bool isTurned = CssPageSize.TryParse(appendix.Page.GetValueText("size")!, out CssPageSize turned);
        bool landscape = turned.Orientation == CssPageOrientation.Landscape;  // true: 297mm by 210mm
        #endregion

        Assert.True(isSize && isTurned);
        Assert.Equal(CssPagePseudoClasses.First, selector.PseudoClasses);
        Assert.Equal(CssMarginBox.BottomCenter, footer.Box);
        Assert.Equal("3cm", marginLeft);
        Assert.Equal(new CssLength(297, CssUnit.Mm), a4.Height);
        Assert.Equal("counter(page)", content);
        Assert.Equal("9pt", fontSize);
        Assert.True(landscape);
        Assert.Equal(new CssLength(297, CssUnit.Mm), turned.Width);
    }
}
