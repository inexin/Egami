namespace Egami.Css.Tests;

public sealed class CascadeTests
{
    private static readonly TestElement s_tree = TestElement.Parse("""
        <svg xmlns="http://www.w3.org/2000/svg" id="root">
          <g id="g" class="group">
            <rect id="r" class="shape"/>
            <text id="t"><tspan id="ts"/></text>
          </g>
        </svg>
        """);

    private static TestElement E(string id) => s_tree.FindById(id)!;

    private static CssCascade Cascade(params (string Css, CssOrigin Origin)[] sheets) => Cascade(null, sheets);

    private static CssCascade Cascade(CssCascadeOptions? options, params (string Css, CssOrigin Origin)[] sheets)
    {
        return new CssCascade(sheets.Select(s => (CssStyleSheet.Parse(s.Css), s.Origin)), options);
    }

    private static string? Winner(CssCascade cascade, string id, string property, IReadOnlyList<CssDeclaration>? hints = null, IReadOnlyList<CssDeclaration>? inline = null)
    {
        return cascade.Cascade(E(id), hints, inline).GetValueText(property);
    }

    // Computes the style of every element from the root down to the one with the id.
    private static CssComputedStyle Computed(CssCascade cascade, string id, Func<TestElement, IReadOnlyList<CssDeclaration>?>? inline = null)
    {
        List<TestElement> path = [];
        for (TestElement? e = E(id); e is not null; e = e.ParentElement)
        {
            path.Insert(0, e);
        }

        CssComputedStyle? style = null;
        foreach (TestElement element in path)
        {
            style = cascade.Compute(cascade.Cascade(element, null, inline?.Invoke(element)), style);
        }

        return style!;
    }

    [Fact]
    public void OriginsAndImportance()
    {
        CssCascade cascade = Cascade(
            ("rect { color: red; opacity: 0.1 !important; display: block !important }", CssOrigin.UserAgent),
            ("rect { color: green; opacity: 0.2 !important; visibility: hidden }", CssOrigin.User),
            ("rect { color: blue; opacity: 0.3 !important; display: inline !important; visibility: visible !important }", CssOrigin.Author));
        Assert.Equal("blue", Winner(cascade, "r", "color"));
        Assert.Equal("0.1", Winner(cascade, "r", "opacity"));
        Assert.Equal("block", Winner(cascade, "r", "display"));
        Assert.Equal("visible", Winner(cascade, "r", "visibility"));
        Assert.True(cascade.Cascade(E("r")).TryGetDeclaration("opacity", out _, out CssOrigin origin));
        Assert.Equal(CssOrigin.UserAgent, origin);
    }

    [Fact]
    public void SpecificityThenOrder()
    {
        CssCascade cascade = Cascade(("#r { color: red } .shape.shape { color: green } rect { color: blue } .shape { color: gray } .shape { opacity: 1; opacity: 0.5 }", CssOrigin.Author));
        Assert.Equal("red", Winner(cascade, "r", "color"));
        Assert.Equal("0.5", Winner(cascade, "r", "opacity"));
        CssCascade later = Cascade(("rect { color: red }", CssOrigin.Author), ("rect { color: blue }", CssOrigin.Author));
        Assert.Equal("blue", Winner(later, "r", "color"));
    }

    [Fact]
    public void PresentationHintsAndInlineStyle()
    {
        CssCascade cascade = Cascade(("* { opacity: 0.5 } #r { visibility: hidden } rect { color: blue !important }", CssOrigin.Author), ("rect { display: block }", CssOrigin.UserAgent));
        CssDeclaration[] hints = [CssDeclaration.Create("opacity", "0.9"), CssDeclaration.Create("display", "none"), CssDeclaration.Create("font-size", "10px")];
        IReadOnlyList<CssDeclaration> inline = CssDeclaration.ParseList("visibility: collapse; color: red; font-size: 20px");
        Assert.Equal("0.5", Winner(cascade, "r", "opacity", hints));
        Assert.Equal("none", Winner(cascade, "r", "display", hints));
        Assert.Equal("collapse", Winner(cascade, "r", "visibility", hints, inline));
        Assert.Equal("blue", Winner(cascade, "r", "color", hints, inline));
        Assert.Equal("20px", Winner(cascade, "r", "font-size", hints, inline));
        Assert.Equal("red", Winner(cascade, "r", "color", hints, CssDeclaration.ParseList("color: red !important")));
    }

    [Fact]
    public void ShorthandsCompetePerLonghand()
    {
        CssCascade cascade = Cascade(("rect { font: italic bold 12px/1.5 Georgia, serif } #r { font-size: 20px } g { font-family: x }", CssOrigin.Author));
        CssCascadedStyle style = cascade.Cascade(E("r"), [CssDeclaration.Create("font-weight", "100")]);
        Assert.Equal("20px", style.GetValueText("font-size"));
        Assert.Equal("italic", style.GetValueText("font-style"));
        Assert.Equal("700", style.GetValueText("font-weight"));
        Assert.Equal("1.5", style.GetValueText("line-height"));
        Assert.Equal("\"Georgia\", serif", style.GetValueText("font-family"));
        Assert.Equal("initial", style.GetValueText("font-variant-numeric"));
        Assert.False(style.TryGetDeclaration("font", out _));

        CssCascadedStyle decoration = Cascade(("rect { text-decoration: underline overline dotted red }", CssOrigin.Author)).Cascade(E("r"));
        Assert.Equal("underline overline", decoration.GetValueText("text-decoration-line"));
        Assert.Equal("dotted", decoration.GetValueText("text-decoration-style"));
        Assert.Equal("red", decoration.GetValueText("text-decoration-color"));
        Assert.Equal("initial", decoration.GetValueText("text-decoration-thickness"));
    }

    [Fact]
    public void InvalidDeclarationsAreDropped()
    {
        CssCascade cascade = Cascade(("rect { color: red; color: notacolor; font: 12px; display: 5 }", CssOrigin.Author));
        CssCascadedStyle style = cascade.Cascade(E("r"));
        Assert.Equal("red", style.GetValueText("color"));
        Assert.Null(style.GetValueText("font-size"));
        Assert.Null(style.GetValueText("display"));
    }

    [Fact]
    public void InheritanceAndKeywords()
    {
        CssCascade cascade = Cascade(
            ("svg { color: red; opacity: 0.5; font-size: 10px } rect { color: green }", CssOrigin.UserAgent),
            ("g { color: blue; opacity: 0.25; display: block } rect { color: unset; opacity: inherit; font-size: initial } text { display: inherit; color: revert; } tspan { color: initial; opacity: unset }", CssOrigin.Author));
        CssComputedStyle r = Computed(cascade, "r");
        Assert.Equal("blue", r.GetValueText("color"));
        Assert.Equal("0.25", r.GetValueText("opacity"));
        Assert.Equal("medium", r.GetValueText("font-size"));
        Assert.Equal("inline", r.GetValueText("display"));
        Assert.Equal("normal", r.GetValueText("font-style"));

        CssComputedStyle t = Computed(cascade, "t");
        Assert.Equal("block", t.GetValueText("display"));
        Assert.Equal("blue", t.GetValueText("color"));
        Assert.Equal("10px", t.GetValueText("font-size"));
        Assert.False(t.IsSetOnElement("font-size"));

        CssComputedStyle ts = Computed(cascade, "ts");
        Assert.Equal("black", ts.GetValueText("color"));
        Assert.Equal("1", ts.GetValueText("opacity"));
        Assert.Contains(ts, p => p.Key == "font-size");
        Assert.DoesNotContain(ts, p => p.Key == "opacity");
    }

    [Fact]
    public void RevertRollsBackOrigins()
    {
        CssCascade cascade = Cascade(
            ("rect { color: red; display: block }", CssOrigin.UserAgent),
            ("rect { color: green }", CssOrigin.User),
            ("rect { color: revert; display: revert-layer; opacity: revert }", CssOrigin.Author));
        CssComputedStyle style = Computed(cascade, "r");
        Assert.Equal("green", style.GetValueText("color"));
        Assert.Equal("block", style.GetValueText("display"));
        Assert.Equal("1", style.GetValueText("opacity"));

        CssCascade userRevert = Cascade(("rect { color: red }", CssOrigin.UserAgent), ("rect { color: revert }", CssOrigin.User), ("g { color: blue }", CssOrigin.Author));
        Assert.Equal("red", Computed(userRevert, "r").GetValueText("color"));
    }

    [Fact]
    public void AllShorthand()
    {
        CssCascade cascade = Cascade(("g { color: blue; display: block; direction: rtl } rect { all: unset }", CssOrigin.Author), ("rect { display: block; opacity: 0.5 }", CssOrigin.UserAgent));
        CssComputedStyle style = Computed(cascade, "r");
        Assert.Equal("blue", style.GetValueText("color"));
        Assert.Equal("inline", style.GetValueText("display"));
        Assert.Equal("1", style.GetValueText("opacity"));
        Assert.Equal("rtl", style.GetValueText("direction"));
    }

    [Fact]
    public void CustomPropertiesAndVar()
    {
        CssCascade cascade = Cascade(("""
            svg { --main: #336699; --Size: 12px; --empty:; --a: var(--b); --b: var(--a); --c: var(--a, 1px) }
            g { --main: red; --nested: var(--missing, var(--Size)); --self: var(--self) 1px }
            rect {
              color: var(--main);
              text-indent: var(--nested);
              opacity: var(--a, 0.3);
              letter-spacing: var(--c, 2px);
              word-spacing: var(--undefined);
              font: var(--missing, bold 20px serif);
              fill: rgb(var(--empty) 1 2 3);
              visibility: var(--self, hidden);
            }
            """, CssOrigin.Author));
        CssComputedStyle style = Computed(cascade, "r");
        Assert.Equal("red", style.GetValueText("color"));
        Assert.Equal("12px", style.GetValueText("text-indent"));
        Assert.Equal("20px", style.GetValueText("font-size"));
        Assert.Equal("0.3", style.GetValueText("opacity"));
        Assert.Equal("1px", style.GetValueText("letter-spacing"));
        Assert.Equal("normal", style.GetValueText("word-spacing"));
        Assert.Equal("700", style.GetValueText("font-weight"));
        Assert.Equal("serif", style.GetValueText("font-family"));
        Assert.Equal("rgb( 1 2 3)", style.GetValueText("fill"));
        Assert.Equal("hidden", style.GetValueText("visibility"));
        Assert.Equal("#336699", Computed(cascade, "root").GetValueText("--main"));
        Assert.Null(style.GetValueText("--a"));
        Assert.Equal("1px", style.GetValueText("--c"));
        Assert.Null(style.GetValueText("--self"));
        Assert.Equal("12px", style.GetValueText("--Size"));
        Assert.Null(style.GetValueText("--size"));
    }

    [Fact]
    public void VarExpansionIsBounded()
    {
        string css = "svg { --l0: x x x x x x x x x x; " + string.Join("; ", Enumerable.Range(1, 12).Select(i => $"--l{i}: var(--l{i - 1}) var(--l{i - 1}) var(--l{i - 1}) var(--l{i - 1})")) + " } rect { color: var(--l12) }";
        CssComputedStyle style = Computed(Cascade((css, CssOrigin.Author)), "r");
        Assert.Equal("black", style.GetValueText("color"));
        Assert.Null(style.GetValueText("--l12"));
        Assert.NotNull(style.GetValueText("--l3"));
    }

    [Fact]
    public void MediaAndSupports()
    {
        const string Css = """
            @media (min-width: 800px) { rect { color: red } }
            @media (max-width: 799px) { rect { color: blue } }
            @supports (display: grid) { rect { opacity: 0.5 } }
            @supports (display: 5) { rect { display: block } }
            @supports not (fill: red) { rect { visibility: hidden } }
            @layer base { rect { font-size: 9px } }
            """;
        CssCascade wide = Cascade(new CssCascadeOptions { Media = new CssMediaContext { Width = 1000 } }, (Css, CssOrigin.Author));
        CssCascade narrow = Cascade(new CssCascadeOptions { Media = new CssMediaContext { Width = 500 }, Supports = _ => false }, (Css, CssOrigin.Author));
        Assert.Equal("red", Winner(wide, "r", "color"));
        Assert.Equal("blue", Winner(narrow, "r", "color"));
        Assert.Equal("0.5", Winner(wide, "r", "opacity"));
        Assert.Null(Winner(narrow, "r", "opacity"));
        Assert.Null(Winner(wide, "r", "display"));
        Assert.Equal("hidden", Winner(wide, "r", "visibility"));
        Assert.Equal("9px", Winner(wide, "r", "font-size"));
    }

    [Fact]
    public void PseudoElementsCascadeSeparately()
    {
        CssCascade cascade = Cascade(("rect::before { color: red } rect { color: blue } rect:hover::after { color: green }", CssOrigin.Author));
        Assert.Equal("blue", cascade.Cascade(E("r")).GetValueText("color"));
        Assert.Equal("red", cascade.Cascade(E("r"), pseudoElement: "before").GetValueText("color"));
        Assert.Null(cascade.Cascade(E("r"), pseudoElement: "after").GetValueText("color"));
    }

    [Fact]
    public void ConsumersRegisterProperties()
    {
        CssPropertyRegistry registry = CssPropertyRegistry.CreateDefault();
        registry.Register(new CssPropertyDefinition("fill", true, "black") { Validate = v => CssColor.TryParse(v, out _) || (v.Count == 1 && v[0].ToString() == "none") });
        registry.Register(new CssPropertyDefinition("stroke-width", true, "1"));
        registry.RegisterShorthand(new CssShorthandDefinition("marker", ["marker-start", "marker-end"], v => [new CssDeclaration("marker-start", v), new CssDeclaration("marker-end", v)]));
        CssCascade cascade = Cascade(new CssCascadeOptions { Registry = registry }, ("g { fill: red; stroke-width: 2; marker: url(#m) } rect { fill: bogus }", CssOrigin.Author));
        CssComputedStyle style = Computed(cascade, "r");
        Assert.Equal("red", style.GetValueText("fill"));
        Assert.Equal("2", style.GetValueText("stroke-width"));
        Assert.Null(style.GetValueText("marker-start"));
        Assert.Equal("url(#m)", Computed(cascade, "g").GetValueText("marker-end"));
        Assert.Equal("black", Computed(cascade, "root").GetValueText("fill"));
    }

    [Fact]
    public void ClassIndexHandlesRepeatsAndManyRules()
    {
        string css = string.Join('\n', Enumerable.Range(0, 2000).Select(i => $".c{i} {{ opacity: {i % 10} }}")) + " .shape { color: red }";
        CssCascade cascade = Cascade((css, CssOrigin.Author));
        TestElement element = new("rect", null);
        element.SetAttribute("class", "shape  c5 shape c7");
        CssCascadedStyle style = cascade.Cascade(element);
        Assert.Equal("red", style.GetValueText("color"));
        Assert.Equal("7", style.GetValueText("opacity"));
    }
}
