namespace Egami.Css.Tests;

public sealed class BoxPropertyTests
{
    private static readonly TestElement s_tree = TestElement.Parse("""
        <html id="root">
          <body id="body">
            <ul id="list"><li id="item"/></ul>
            <table id="table"><tr><td id="cell"/></tr></table>
          </body>
        </html>
        """);

    private static readonly CssPropertyRegistry s_registry = CssPropertyRegistry.CreateDefault();

    // The cascaded longhands of the element with id "item" under one rule, as text.
    private static Dictionary<string, string> Longhands(string declarations)
    {
        CssCascade cascade = new([(CssStyleSheet.Parse("#item { " + declarations + " }"), CssOrigin.Author)]);
        CssCascadedStyle style = cascade.Cascade(s_tree.FindById("item")!);
        return style.Properties.ToDictionary(p => p, p => style.GetValueText(p)!);
    }

    private static bool Valid(string property, string value)
    {
        IReadOnlyList<CssComponentValue> values = CssComponentValue.ParseList(value);
        if (s_registry.TryGetShorthand(property, out CssShorthandDefinition? shorthand))
        {
            return shorthand.Expand(values) is not null;
        }

        Assert.True(s_registry.TryGetProperty(property, out CssPropertyDefinition? definition), property);
        return definition.Validate!(values);
    }

    [Theory]
    [InlineData("1px", "1px", "1px", "1px", "1px")]
    [InlineData("1px 2px", "1px", "2px", "1px", "2px")]
    [InlineData("1px 2px 3px", "1px", "2px", "3px", "2px")]
    [InlineData("1px 2px 3px 4px", "1px", "2px", "3px", "4px")]
    [InlineData("auto -5% 0 calc(1em + 2px)", "auto", "-5%", "0", "calc(1em + 2px)")]
    public void MarginSides(string value, string top, string right, string bottom, string left)
    {
        Dictionary<string, string> longhands = Longhands("margin: " + value);
        Assert.Equal(top, longhands["margin-top"]);
        Assert.Equal(right, longhands["margin-right"]);
        Assert.Equal(bottom, longhands["margin-bottom"]);
        Assert.Equal(left, longhands["margin-left"]);
    }

    [Theory]
    [InlineData("margin", "1px 2px 3px 4px 5px", false)]
    [InlineData("margin", "red", false)]
    [InlineData("margin-top", "auto", true)]
    [InlineData("margin-top", "5", false)]
    [InlineData("padding", "-1px", false)]
    [InlineData("padding", "auto", false)]
    [InlineData("padding-left", "10%", true)]
    [InlineData("border-width", "thin medium thick 2px", true)]
    [InlineData("border-width", "10%", false)]
    [InlineData("border-style", "solid dotted", true)]
    [InlineData("border-style", "wavy", false)]
    [InlineData("border-color", "red #00f rgb(0 0 0)", true)]
    [InlineData("border-top", "1px solid red", true)]
    [InlineData("border-top", "solid solid", false)]
    [InlineData("border", "red 3px", true)]
    [InlineData("border", "1px solid red blue", false)]
    [InlineData("border-radius", "1px 2px / 3px", true)]
    [InlineData("border-radius", "1px / ", false)]
    [InlineData("border-radius", "-1px", false)]
    [InlineData("border-top-left-radius", "1px 50%", true)]
    [InlineData("width", "50%", true)]
    [InlineData("width", "fit-content(10em)", true)]
    [InlineData("width", "-1px", false)]
    [InlineData("width", "none", false)]
    [InlineData("max-width", "none", true)]
    [InlineData("min-height", "auto", true)]
    [InlineData("box-sizing", "border-box", true)]
    public void BoxGrammar(string property, string value, bool valid) => Assert.Equal(valid, Valid(property, value));

    [Fact]
    public void BorderShorthands()
    {
        Dictionary<string, string> border = Longhands("border: 2px dashed blue");
        foreach (string side in (string[])["top", "right", "bottom", "left"])
        {
            Assert.Equal("2px", border["border-" + side + "-width"]);
            Assert.Equal("dashed", border["border-" + side + "-style"]);
            Assert.Equal("blue", border["border-" + side + "-color"]);
        }

        // What the shorthand leaves out is reset; a later longhand wins over it.
        Dictionary<string, string> side2 = Longhands("border-left-color: red; border-left: thick; border-bottom: 1px solid; border-bottom-color: green");
        Assert.Equal("initial", side2["border-left-color"]);
        Assert.Equal("thick", side2["border-left-width"]);
        Assert.Equal("initial", side2["border-left-style"]);
        Assert.Equal("green", side2["border-bottom-color"]);

        Dictionary<string, string> radius = Longhands("border-radius: 1px 2px 3px / 4px 5px");
        Assert.Equal("1px 4px", radius["border-top-left-radius"]);
        Assert.Equal("2px 5px", radius["border-top-right-radius"]);
        Assert.Equal("3px 4px", radius["border-bottom-right-radius"]);
        Assert.Equal("2px 5px", radius["border-bottom-left-radius"]);
    }

    [Fact]
    public void ComputedInitialValues()
    {
        CssCascade cascade = new([(CssStyleSheet.Parse("#list { list-style: square inside; border-collapse: collapse }"), CssOrigin.Author)]);
        CssComputedStyle? style = null;
        foreach (string id in (string[])["root", "body", "list", "item"])
        {
            style = cascade.Compute(cascade.Cascade(s_tree.FindById(id)!), style);
        }

        // Inherited list and table properties reach the item; the box ones fall back to their initial values.
        Assert.Equal("square", style!.GetValueText("list-style-type"));
        Assert.Equal("inside", style.GetValueText("list-style-position"));
        Assert.Equal("none", style.GetValueText("list-style-image"));
        Assert.Equal("collapse", style.GetValueText("border-collapse"));
        Assert.Equal("0", style.GetValueText("margin-top"));
        Assert.Equal("medium", style.GetValueText("border-top-width"));
        Assert.Equal("none", style.GetValueText("border-top-style"));
        Assert.Equal("currentcolor", style.GetValueText("border-top-color"));
        Assert.Equal("auto", style.GetValueText("width"));
        Assert.Equal("transparent", style.GetValueText("background-color"));
        Assert.Equal("0% 0%", style.GetValueText("background-position"));
        Assert.Equal("visible", style.GetValueText("overflow-x"));
        Assert.Equal("2", style.GetValueText("orphans"));
        Assert.Equal("auto", style.GetValueText("break-before"));
        Assert.Equal("normal", style.GetValueText("content"));
    }

    [Fact]
    public void Background()
    {
        Dictionary<string, string> one = Longhands("background: url(a.png) no-repeat center / cover fixed content-box #fff");
        Assert.Equal("url(a.png)", one["background-image"]);
        Assert.Equal("center", one["background-position"]);
        Assert.Equal("cover", one["background-size"]);
        Assert.Equal("no-repeat", one["background-repeat"]);
        Assert.Equal("fixed", one["background-attachment"]);
        Assert.Equal("content-box", one["background-origin"]);
        Assert.Equal("content-box", one["background-clip"]);
        Assert.Equal("#fff", one["background-color"]);

        Dictionary<string, string> two = Longhands("background: linear-gradient(red, blue) left 10px top 5px repeat-x, padding-box border-box red");
        Assert.Equal("linear-gradient(red, blue), none", two["background-image"]);
        Assert.Equal("left 10px top 5px, 0% 0%", two["background-position"]);
        Assert.Equal("auto, auto", two["background-size"]);
        Assert.Equal("repeat-x, repeat", two["background-repeat"]);
        Assert.Equal("padding-box, padding-box", two["background-origin"]);
        Assert.Equal("border-box, border-box", two["background-clip"]);
        Assert.Equal("red", two["background-color"]);

        Assert.Equal("blue", Longhands("background: blue")["background-color"]);
        Assert.Equal("none", Longhands("background: none")["background-image"]);
    }

    [Theory]
    [InlineData("background", "red, blue", false)]
    [InlineData("background", "url(a) url(b)", false)]
    [InlineData("background", "center / ", false)]
    [InlineData("background", "/ cover", false)]
    [InlineData("background", "10px 20px / 50% auto space round", true)]
    [InlineData("background", ",", false)]
    [InlineData("background-position", "left top, 10% 20%", true)]
    [InlineData("background-position", "top left", true)]
    [InlineData("background-position", "10px left", false)]
    [InlineData("background-position", "left 10px top", true)]
    [InlineData("background-position", "left 10px left 5px", false)]
    [InlineData("background-position", "center center", true)]
    [InlineData("background-position", "right 5% bottom 6px", true)]
    [InlineData("background-size", "auto 50%", true)]
    [InlineData("background-size", "cover contain", false)]
    [InlineData("background-repeat", "repeat-x repeat", false)]
    [InlineData("background-image", "none, url(x), radial-gradient(red, blue)", true)]
    [InlineData("background-image", "red", false)]
    [InlineData("background-clip", "text", true)]
    [InlineData("background-origin", "text", false)]
    public void BackgroundGrammar(string property, string value, bool valid) => Assert.Equal(valid, Valid(property, value));

    [Theory]
    [InlineData("none", "none", "none", null)]
    [InlineData("square", "square", null, null)]
    [InlineData("none square", "square", "none", null)]
    [InlineData("url(a.png) none", "none", "url(a.png)", null)]
    [InlineData("none none", "none", "none", null)]
    [InlineData("inside \"-\"", "\"-\"", null, "inside")]
    [InlineData("lower-roman outside url(b.png)", "lower-roman", "url(b.png)", "outside")]
    public void ListStyle(string value, string type, string? image, string? position)
    {
        Dictionary<string, string> longhands = Longhands("list-style: " + value);
        Assert.Equal(type, longhands["list-style-type"]);
        Assert.Equal(image ?? "initial", longhands["list-style-image"]);
        Assert.Equal(position ?? "initial", longhands["list-style-position"]);
    }

    [Theory]
    [InlineData("list-style", "none none none", false)]
    [InlineData("list-style", "none square url(a)", false)]
    [InlineData("list-style", "square disc", false)]
    [InlineData("list-style-type", "symbols(cyclic \"*\")", true)]
    [InlineData("list-style-type", "5", false)]
    [InlineData("border-spacing", "2px 3px", true)]
    [InlineData("border-spacing", "10%", false)]
    [InlineData("caption-side", "bottom", true)]
    [InlineData("empty-cells", "hide", true)]
    [InlineData("table-layout", "fixed", true)]
    [InlineData("vertical-align", "-2px", true)]
    [InlineData("vertical-align", "middle", true)]
    [InlineData("vertical-align", "center", false)]
    [InlineData("float", "inline-start", true)]
    [InlineData("float", "both", false)]
    [InlineData("clear", "both", true)]
    [InlineData("overflow", "hidden auto", true)]
    [InlineData("overflow", "hidden auto clip", false)]
    [InlineData("overflow-x", "clip", true)]
    [InlineData("break-before", "page", true)]
    [InlineData("break-before", "recto", true)]
    [InlineData("break-inside", "page", false)]
    [InlineData("orphans", "3", true)]
    [InlineData("orphans", "0", false)]
    [InlineData("widows", "1.5", false)]
    [InlineData("page", "chapter", true)]
    [InlineData("page", "\"chapter\"", false)]
    [InlineData("page-break-before", "always", true)]
    [InlineData("page-break-inside", "always", false)]
    [InlineData("size", "A4 landscape", true)]
    [InlineData("size", "landscape A4", true)]
    [InlineData("size", "A4 A5", false)]
    [InlineData("size", "10%", false)]
    [InlineData("marks", "cross crop", true)]
    [InlineData("marks", "crop crop", false)]
    [InlineData("bleed", "6pt", true)]
    [InlineData("page-orientation", "rotate-left", true)]
    [InlineData("counter-reset", "item 1 reversed(x) y", true)]
    [InlineData("counter-reset", "1 item", false)]
    [InlineData("counter-increment", "reversed(x)", false)]
    [InlineData("counter-increment", "none", true)]
    public void OtherGrammar(string property, string value, bool valid) => Assert.Equal(valid, Valid(property, value));

    [Fact]
    public void LegacyPageBreaksAndOverflow()
    {
        Dictionary<string, string> longhands = Longhands("page-break-before: always; page-break-after: left; page-break-inside: avoid; overflow: hidden");
        Assert.Equal("page", longhands["break-before"]);
        Assert.Equal("left", longhands["break-after"]);
        Assert.Equal("avoid", longhands["break-inside"]);
        Assert.Equal("hidden", longhands["overflow-x"]);
        Assert.Equal("hidden", longhands["overflow-y"]);

        // A longhand after its legacy alias wins, and the other way round.
        Assert.Equal("avoid-page", Longhands("page-break-before: always; break-before: avoid-page")["break-before"]);
        Assert.Equal("page", Longhands("break-before: avoid-page; page-break-before: always")["break-before"]);
    }

    [Fact]
    public void InvalidDeclarationsFallBack()
    {
        // An invalid value is dropped at parse time, so the earlier valid one wins.
        Dictionary<string, string> longhands = Longhands("margin-top: 1px; margin-top: red; float: left; float: up; border-collapse: collapse; border-collapse: none");
        Assert.Equal("1px", longhands["margin-top"]);
        Assert.Equal("left", longhands["float"]);
        Assert.Equal("collapse", longhands["border-collapse"]);
    }

    [Fact]
    public void ShorthandWithVar()
    {
        CssCascade cascade = new([(CssStyleSheet.Parse("#body { --m: 1px 2px } #item { margin: var(--m); padding: var(--nope) }"), CssOrigin.Author)]);
        CssComputedStyle? style = null;
        foreach (string id in (string[])["root", "body", "list", "item"])
        {
            style = cascade.Compute(cascade.Cascade(s_tree.FindById(id)!), style);
        }

        Assert.Equal("1px", style!.GetValueText("margin-top"));
        Assert.Equal("2px", style.GetValueText("margin-right"));
        Assert.Equal("1px", style.GetValueText("margin-bottom"));
        Assert.Equal("0", style.GetValueText("padding-left"));
    }
}
