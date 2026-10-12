namespace Egami.Css.Tests;

public sealed class SyntaxTests
{
    [Fact]
    public void TokensCarryPositions()
    {
        IReadOnlyList<CssToken> tokens = CssTokenizer.Tokenize("a {\r\n  color: red;\n}");
        CssToken color = tokens.First(t => t.Kind == CssTokenKind.Ident && t.Value == "color");
        Assert.Equal(2, color.Line);
        Assert.Equal(3, color.Column);
        CssToken close = tokens[^1];
        Assert.Equal(CssTokenKind.RightCurly, close.Kind);
        Assert.Equal(3, close.Line);
        Assert.Equal(1, close.Column);
    }

    [Fact]
    public void NumbersKeepTheirRepresentation()
    {
        IReadOnlyList<CssToken> tokens = CssTokenizer.Tokenize("+1.50e2px 7% -.5 12");
        Assert.Equal(CssTokenKind.Dimension, tokens[0].Kind);
        Assert.Equal("+1.50e2", tokens[0].Value);
        Assert.Equal(150, tokens[0].Number);
        Assert.Equal("px", tokens[0].Unit);
        Assert.False(tokens[0].IsInteger);
        Assert.Equal(CssTokenKind.Percentage, tokens[2].Kind);
        Assert.Equal(-0.5, tokens[4].Number);
        Assert.True(tokens[6].IsInteger);
    }

    [Fact]
    public void HugeNumbersAreClamped()
    {
        Assert.Equal(double.MaxValue, CssTokenizer.Tokenize("1e999")[0].Number);
    }

    [Theory]
    [InlineData("rgb(1 2 3 / 50%)")]
    [InlineData("url(\"a b.png\")")]
    [InlineData("calc(100% - 2px)")]
    [InlineData("a/**/b")]
    [InlineData("#a\\ b .c")]
    [InlineData("1e3px \\65 3")]
    [InlineData("'it''s' \"q\\\"uote\"")]
    [InlineData("url(a\\(b)")]
    [InlineData("[a|=\"b\" i] > {x:y}")]
    [InlineData("--x -->")]
    [InlineData("10\\65 3")]
    public void SerializationRoundTrips(string css)
    {
        IReadOnlyList<CssComponentValue> values = CssComponentValue.ParseList(css);
        string text = CssComponentValue.Serialize(values);
        Assert.Equal(values, CssComponentValue.ParseList(text));
    }

    [Fact]
    public void SerializationKeepsSimpleValues()
    {
        Assert.Equal("rgb(1 2 3 / 50%)", CssComponentValue.Serialize(CssComponentValue.ParseList("rgb(1 2 3 / 50%)")));
        Assert.Equal("url(\"a b.png\")", CssComponentValue.Serialize(CssComponentValue.ParseList("url('a b.png')")));
        Assert.Equal("calc(100% - 2px)", CssComponentValue.Serialize(CssComponentValue.ParseList("calc(100% - 2px)")));
        Assert.Equal("a/**/b", CssComponentValue.Serialize(CssComponentValue.ParseList("a/* c */b")));
    }

    [Fact]
    public void ComponentValuesCompareByContent()
    {
        IReadOnlyList<CssComponentValue> a = CssComponentValue.ParseList("rgb(1, 2, 3)");
        IReadOnlyList<CssComponentValue> b = CssComponentValue.ParseList("\n\nrgb(1, 2, 3)");
        Assert.Equal(a[0], b[1]);
        Assert.Equal(a[0].GetHashCode(), b[1].GetHashCode());
        Assert.NotEqual(a[0].Line, b[1].Line);
    }

    [Fact]
    public void DeepNestingIsCut()
    {
        List<CssParseError> errors = [];
        string css = new string('(', 100_000) + "x";
        IReadOnlyList<CssComponentValue> values = CssComponentValue.ParseList(css, new CssParseOptions { Errors = errors.Add });
        Assert.Single(values);
        Assert.Contains(errors, e => e.Message.Contains("nested too deeply", StringComparison.Ordinal));
        _ = CssComponentValue.Serialize(values);
    }

    [Fact]
    public void ParseErrorsAreReported()
    {
        List<CssParseError> errors = [];
        CssStyleSheet sheet = CssStyleSheet.Parse("a { color: red; \"x\n } b%%c { x: y } /* end", new CssParseOptions { Errors = errors.Add });
        Assert.Single(sheet.Rules);
        Assert.Contains(errors, e => e.Message.Contains("newline in a string", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Message.Contains("selector", StringComparison.Ordinal) && e.Line == 2);
        Assert.Contains(errors, e => e.Message.Contains("comment", StringComparison.Ordinal));
    }

    [Fact]
    public void StyleSheetRules()
    {
        CssStyleSheet sheet = CssStyleSheet.Parse("""
            @charset "utf-8";
            @import url(base.css) layer(reset) supports(display: grid) screen and (min-width: 10px);
            @namespace svg url(http://www.w3.org/2000/svg);
            @namespace "http://www.w3.org/1999/xhtml";
            svg|rect, p { fill: red !important; --Main-Color: Blue }
            @media print { p { color: black } }
            @supports (display: grid) { div { display: grid } }
            @font-face { font-family: Foo; src: url(foo.woff2) format("woff2") }
            @layer base, theme;
            @layer base { p { color: gray } }
            @keyframes spin { from { opacity: 0 } }
            @import "late.css";
            """);
        Assert.Collection(
            sheet.Rules,
            r => Assert.Equal("base.css", Assert.IsType<CssImportRule>(r).Url),
            r => Assert.Equal("svg", Assert.IsType<CssNamespaceRule>(r).Prefix),
            r => Assert.Null(Assert.IsType<CssNamespaceRule>(r).Prefix),
            r =>
            {
                CssStyleRule style = Assert.IsType<CssStyleRule>(r);
                Assert.Equal("svg|rect, p", style.SelectorText);
                Assert.Equal("fill", style.Declarations[0].Name);
                Assert.True(style.Declarations[0].Important);
                Assert.Equal("--Main-Color", style.Declarations[1].Name);
                Assert.Equal("Blue", style.Declarations[1].ValueText);
            },
            r => Assert.Equal("print", Assert.IsType<CssMediaRule>(r).Media.MediaText),
            r => Assert.True(Assert.IsType<CssSupportsRule>(r).Matches(d => d.Name == "display")),
            r => Assert.Equal("Foo", Assert.IsType<CssFontFaceRule>(r).FontFace.Family),
            r => Assert.Equal(["base", "theme"], Assert.IsType<CssLayerRule>(r).Names),
            r => Assert.Single(Assert.IsType<CssLayerRule>(r).Rules!),
            r => Assert.Equal("keyframes", Assert.IsType<CssUnknownAtRule>(r).Name));
        CssImportRule import = (CssImportRule)sheet.Rules[0];
        Assert.Equal("reset", import.Layer);
        Assert.Equal("display: grid", import.SupportsText);
        Assert.Equal("screen and (min-width: 10px)", import.Media.MediaText);
        Assert.Equal("http://www.w3.org/2000/svg", sheet.Namespaces.Prefixes["svg"]);
        Assert.Equal("http://www.w3.org/1999/xhtml", sheet.Namespaces.DefaultNamespace);
        Assert.Equal(4, sheet.GetAllStyleRules().Count());
    }

    [Fact]
    public void NestedRulesAreDroppedAndReported()
    {
        List<CssParseError> errors = [];
        CssStyleSheet sheet = CssStyleSheet.Parse("a { color: red; &:hover { color: blue } b { x: y } @media print { z: w } font-size: 2px }", new CssParseOptions { Errors = errors.Add });
        CssStyleRule rule = Assert.IsType<CssStyleRule>(Assert.Single(sheet.Rules));
        Assert.Equal(["color", "font-size"], rule.Declarations.Select(d => d.Name));
        Assert.Equal(2, errors.Count(e => e.Message.Contains("Nesting", StringComparison.Ordinal)));
        Assert.Contains(errors, e => e.Message.Contains("@media", StringComparison.Ordinal));
    }

    [Fact]
    public void DeclarationLists()
    {
        IReadOnlyList<CssDeclaration> list = CssDeclaration.ParseList("COLOR: Red ; ; bad; --X : 1 2 ;font-size:12px!IMPORTANT");
        Assert.Equal(["color", "--X", "font-size"], list.Select(d => d.Name));
        Assert.Equal("Red", list[0].ValueText);
        Assert.Equal("1 2", list[1].ValueText);
        Assert.True(list[2].Important);
        Assert.Equal("font-size: 12px !important", list[2].ToString());
        Assert.Equal("fill", CssDeclaration.Create("FILL", " red ").Name);
        Assert.Equal("red", CssDeclaration.Create("fill", " red ").ValueText);
    }
}
