namespace Egami.Css.Tests;

public sealed class SelectorTests
{
    private const string Svg = "http://www.w3.org/2000/svg";

    private static readonly TestElement s_svg = TestElement.Parse("""
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" id="root" xml:lang="en-GB">
          <defs id="defs"><linearGradient id="grad"/></defs>
          <g id="g1" class="layer a">
            <rect id="r1" class="shape" width="10"/>
            <circle id="c1" class="shape round" r="5"/>
            <rect id="r2" class="shape" data-tags="one two"/>
            <text id="t1" lang="fr-Latn-CA">x<tspan id="ts1"/></text>
          </g>
          <g id="g2"><use id="u1" xlink:href="#r1"/></g>
          <foo xmlns="urn:other" id="o1"/>
        </svg>
        """);

    private static TestElement E(string id) => s_svg.FindById(id)!;

    private static string[] Select(string selector, CssNamespaces? namespaces = null)
    {
        CssSelectorList list = CssSelectorList.Parse(selector, namespaces);
        return [.. s_svg.DescendantsAndSelf().Where(e => list.Matches(e)).Select(e => e.Id!)];
    }

    [Theory]
    [InlineData("*", 0, 0, 0)]
    [InlineData("rect", 0, 0, 1)]
    [InlineData("#a.b[c]:hover", 1, 3, 0)]
    [InlineData("g > rect + circle ~ text::before", 0, 0, 5)]
    [InlineData(":is(#a, .b, c)", 1, 0, 0)]
    [InlineData(":where(#a, .b)", 0, 0, 0)]
    [InlineData(":not(.a, #b)", 1, 0, 0)]
    [InlineData(":has(> .a .b)", 0, 2, 0)]
    [InlineData(":nth-child(2n of .a, #b)", 1, 1, 0)]
    [InlineData(":nth-last-of-type(2)", 0, 1, 0)]
    [InlineData("a:before", 0, 0, 2)]
    [InlineData("svg|rect", 0, 0, 1)]
    [InlineData("*|*", 0, 0, 0)]
    public void Specificity(string selector, int a, int b, int c)
    {
        CssNamespaces namespaces = new();
        namespaces.Set("svg", Svg);
        CssSelector parsed = CssSelectorList.Parse(selector, namespaces).Selectors[0];
        Assert.Equal(new CssSpecificity(a, b, c), parsed.Specificity);
    }

    [Fact]
    public void SpecificityOrders()
    {
        Assert.True(new CssSpecificity(1, 0, 0) > new CssSpecificity(0, 99, 99));
        Assert.True(new CssSpecificity(0, 1, 0) > new CssSpecificity(0, 0, 99));
        Assert.Equal("(1, 2, 3)", new CssSpecificity(1, 2, 3).ToString());
    }

    [Theory]
    [InlineData("rect", "r1,r2")]
    [InlineData("g > .shape:first-child", "r1")]
    [InlineData(".shape + .shape", "c1,r2")]
    [InlineData("#r1 ~ *", "c1,r2,t1")]
    [InlineData("svg rect", "r1,r2")]
    [InlineData("linearGradient", "grad")]
    [InlineData("lineargradient", "")]
    [InlineData("[data-tags~=two]", "r2")]
    [InlineData("[id|=g1]", "g1")]
    [InlineData("[class^=sha][class$=pe]", "r1,r2")]
    [InlineData("[class*='ROUND' i]", "c1")]
    [InlineData("[class*='ROUND' s]", "")]
    [InlineData(":root", "root")]
    [InlineData(":scope", "root")]
    [InlineData(":empty", "grad,r1,c1,r2,ts1,u1,o1")]
    [InlineData("g :nth-child(odd)", "r1,r2,ts1,u1")]
    [InlineData(":nth-child(2 of .shape)", "c1")]
    [InlineData(":nth-last-child(-n+2)", "root,grad,r2,t1,ts1,g2,u1,o1")]
    [InlineData("rect:nth-of-type(2)", "r2")]
    [InlineData("rect:nth-last-of-type(1)", "r2")]
    [InlineData(":only-child", "root,grad,ts1,u1")]
    [InlineData(":only-of-type", "root,defs,grad,c1,t1,ts1,u1,o1")]
    [InlineData("g:has(> circle)", "g1")]
    [InlineData("g:has(use)", "g2")]
    [InlineData("rect:has(+ circle)", "r1")]
    [InlineData("rect:has(~ text tspan)", "r1,r2")]
    [InlineData(":is(g, defs) > :not(rect, circle)", "grad,t1,u1")]
    [InlineData(":where(.round, #r2)", "c1,r2")]
    [InlineData(":lang(fr)", "t1,ts1")]
    [InlineData(":lang(fr-CA)", "t1,ts1")]
    [InlineData(":lang('*-CA')", "t1,ts1")]
    [InlineData(":lang(en)", "root,defs,grad,g1,r1,c1,r2,g2,u1,o1")]
    [InlineData(":lang(en-US)", "")]
    [InlineData(":dir(ltr)", "root,defs,grad,g1,r1,c1,r2,t1,ts1,g2,u1,o1")]
    [InlineData(":dir(rtl)", "")]
    [InlineData(":defined", "root,defs,grad,g1,r1,c1,r2,t1,ts1,g2,u1,o1")]
    [InlineData(":hover", "")]
    public void Matching(string selector, string expected)
    {
        Assert.Equal(expected.Length == 0 ? [] : expected.Split(','), Select(selector));
    }

    [Fact]
    public void Namespaces()
    {
        CssNamespaces namespaces = new();
        namespaces.Set("svg", Svg);
        namespaces.Set("x", "http://www.w3.org/1999/xlink");
        namespaces.Set("o", "urn:other");
        Assert.Equal(["o1"], Select("o|*", namespaces));
        Assert.Equal(["r1", "r2"], Select("svg|rect", namespaces));
        Assert.Equal(["u1"], Select("[x|href='#r1']", namespaces));
        Assert.Equal(["u1"], Select("[*|href]", namespaces));
        Assert.Empty(Select("[href]", namespaces));
        Assert.Empty(Select("|rect", namespaces));

        CssNamespaces defaultOther = new();
        defaultOther.Set(null, "urn:other");
        Assert.Equal(["o1"], Select("*", defaultOther));
        Assert.Equal(["o1"], Select("[id]", defaultOther));
        Assert.Equal(["o1"], Select(":not(rect)", defaultOther));
        Assert.Empty(Select("*|*:is(rect)", defaultOther));
        Assert.False(CssSelectorList.TryParse("undeclared|rect", out _));
    }

    [Fact]
    public void PseudoElements()
    {
        CssSelector before = CssSelectorList.Parse("rect.shape::before").Selectors[0];
        Assert.Equal("before", before.PseudoElement);
        Assert.False(before.Matches(E("r1")));
        Assert.True(before.MatchesPseudoElement(E("r1"), "before"));
        Assert.False(before.MatchesPseudoElement(E("r1"), "after"));
        Assert.Equal("first-line", CssSelectorList.Parse("p:FIRST-LINE").Selectors[0].PseudoElement);
        Assert.Equal("marker", CssSelectorList.Parse("li::marker").Selectors[0].PseudoElement);
        Assert.True(CssSelectorList.TryParse("a::before:hover", out _));
        Assert.False(CssSelectorList.TryParse("a::before span", out _));
        Assert.False(CssSelectorList.TryParse("a::before.x", out _));
        Assert.True(CssSelectorList.TryParse(":is(::before)", out _), "A forgiving list drops the pseudo-element.");
        Assert.False(CssSelectorList.TryParse(":not(::before)", out _));
        Assert.False(CssSelectorList.TryParse("::nonsense", out _));
        Assert.True(CssSelectorList.TryParse("::-webkit-scrollbar", out _));
    }

    [Theory]
    [InlineData("a:unknown")]
    [InlineData(":nth-child(2n+)")]
    [InlineData(":nth-of-type(2 of .a)")]
    [InlineData(":has(:has(a))")]
    [InlineData(":not()")]
    [InlineData("a >")]
    [InlineData("a, ")]
    [InlineData("#1a")]
    [InlineData("[a=b c]")]
    [InlineData("[a=b x]")]
    [InlineData("a || b")]
    [InlineData(":lang()")]
    [InlineData(":dir(ltr rtl)")]
    public void InvalidSelectors(string selector)
    {
        Assert.False(CssSelectorList.TryParse(selector, out _));
        Assert.Throws<FormatException>(() => CssSelectorList.Parse(selector));
    }

    [Fact]
    public void ForgivingListsDropInvalidParts()
    {
        Assert.Equal(["c1"], Select(":is(.round, :unknown, 1px)"));
        Assert.Empty(Select(":where()"));
    }

    [Fact]
    public void ScopeAndStates()
    {
        CssSelectorList scoped = CssSelectorList.Parse(":scope > rect");
        Assert.True(scoped.Matches(E("r1"), E("g1")));
        Assert.False(scoped.Matches(E("r1"), E("root")));
        E("c1").States.Add("hover");
        try
        {
            Assert.Equal(["c1"], Select("circle:hover"));
            Assert.Equal(["g1"], Select("g:has(:hover)"));
        }
        finally
        {
            E("c1").States.Remove("hover");
        }
    }

    [Fact]
    public void HtmlCaseRules()
    {
        TestElement html = TestElement.Parse("""
            <html xmlns="http://www.w3.org/1999/xhtml"><body><INPUT TYPE="Checkbox" Name="x" id="i"/><svg xmlns="http://www.w3.org/2000/svg" id="s" viewBox="0 0 1 1"/></body></html>
            """, htmlDocument: true);
        TestElement input = html.FindById("i")!;
        TestElement svg = html.FindById("s")!;
        Assert.True(CssSelectorList.Parse("INPUT").Matches(input));
        Assert.True(CssSelectorList.Parse("[type=checkbox]").Matches(input));
        Assert.False(CssSelectorList.Parse("[type=checkbox s]").Matches(input));
        Assert.True(CssSelectorList.Parse("[NAME]").Matches(input));
        Assert.False(CssSelectorList.Parse("[name=X]").Matches(input));
        Assert.True(CssSelectorList.Parse("[viewBox]").Matches(svg));
        Assert.False(CssSelectorList.Parse("[viewbox]").Matches(svg));
        Assert.False(CssSelectorList.Parse("SVG").Matches(svg));
    }

    [Fact]
    public void DeepDescendantChainsStayFast()
    {
        // A selector that backtracks exponentially without the early exits: a long chain of descendant combinators.
        TestElement root = new("a", null);
        TestElement current = root;
        for (int i = 0; i < 200; i++)
        {
            TestElement child = new("a", null);
            current.Append(child);
            current = child;
        }

        CssSelectorList list = CssSelectorList.Parse("c " + string.Join(' ', Enumerable.Repeat("a", 40)));
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        foreach (TestElement element in root.DescendantsAndSelf())
        {
            Assert.False(list.Matches(element));
        }

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
    }
}
