namespace Egami.Css.Tests;

public sealed class TypedGetterTests
{
    private static readonly CssLengthContext s_context = new(20, 16, 800, 600);

    // Computes a chain of nested elements, each with the inline style given, and returns their computed styles, root first.
    private static CssComputedStyle[] Chain(params string[] inline)
    {
        string markup = string.Join("", inline.Select((_, i) => $"<e id=\"e{i}\">")) + string.Join("", inline.Select(_ => "</e>"));
        TestElement root = TestElement.Parse(markup);
        CssCascade cascade = new([]);
        CssComputedStyle[] styles = new CssComputedStyle[inline.Length];
        CssComputedStyle? parent = null;
        for (int i = 0; i < inline.Length; i++)
        {
            parent = styles[i] = cascade.Compute(cascade.Cascade(root.FindById("e" + i)!, null, CssDeclaration.ParseList(inline[i])), parent);
        }

        return styles;
    }

    private static CssComputedStyle One(string inline) => Chain(inline)[0];

    [Theory]
    [InlineData("display: BLOCK", "block")]
    [InlineData("display: flex ", "flex")]
    [InlineData("visibility: hidden", "hidden")]
    public void KeywordIsOneLowercasedIdentifier(string css, string expected)
    {
        Assert.True(One(css).TryGetKeyword(css.Split(':')[0], out string? keyword));
        Assert.Equal(expected, keyword);
    }

    [Theory]
    [InlineData("display: inline flex")]
    [InlineData("z-index: 3")]
    [InlineData("color: rgb(1 2 3)")]
    public void KeywordRefusesOtherValues(string css)
    {
        Assert.False(One(css).TryGetKeyword(css.Split(':')[0], out string? keyword));
        Assert.Null(keyword);
    }

    [Fact]
    public void KeywordOfInitialAndMissingValues()
    {
        CssComputedStyle style = One("");
        Assert.True(style.TryGetKeyword("display", out string? display));
        Assert.Equal("inline", display);
        Assert.False(style.TryGetKeyword("no-such-property", out _));
    }

    [Fact]
    public void KeywordsListEveryIdentifier()
    {
        CssComputedStyle style = One("text-decoration-line: Underline overline; display: inline flex; z-index: 2");
        Assert.Equal(["underline", "overline"], style.GetKeywords("text-decoration-line"));
        Assert.Equal(["inline", "flex"], style.GetKeywords("display"));
        Assert.Empty(style.GetKeywords("z-index"));
        Assert.Empty(style.GetKeywords("no-such-property"));
    }

    [Theory]
    [InlineData("flex-grow: 1.5", 1.5)]
    [InlineData("flex-grow: -2", -2)]
    [InlineData("flex-grow: calc(2 * 3 + 0.5)", 6.5)]
    [InlineData("flex-grow: min(4, 2)", 2)]
    public void Numbers(string css, double expected)
    {
        Assert.True(One(css).TryGetNumber("flex-grow", out double number));
        Assert.Equal(expected, number, 1e-12);
    }

    [Theory]
    [InlineData("flex-grow: 2px")]
    [InlineData("flex-grow: 50%")]
    [InlineData("flex-grow: auto")]
    [InlineData("flex-grow: calc(2px + 1px)")]
    [InlineData("flex-grow: 1 2")]
    public void NumbersRefuseOtherValues(string css)
    {
        Assert.False(One(css).TryGetNumber("flex-grow", out _));
    }

    [Theory]
    [InlineData("z-index: 7", 7)]
    [InlineData("z-index: -3", -3)]
    [InlineData("z-index: calc(5 / 2)", 3)]
    [InlineData("z-index: calc(-5 / 2)", -2)]
    [InlineData("z-index: calc(1.4)", 1)]
    [InlineData("z-index: 99999999999", int.MaxValue)]
    public void Integers(string css, int expected)
    {
        Assert.True(One(css).TryGetInteger("z-index", out int value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("z-index: 1.5")]
    [InlineData("z-index: 1.0")]
    [InlineData("z-index: auto")]
    [InlineData("z-index: 2px")]
    public void IntegersRefuseOtherValues(string css)
    {
        Assert.False(One(css).TryGetInteger("z-index", out _));
    }

    [Fact]
    public void IntegerOfInheritedAndInitialValues()
    {
        CssComputedStyle[] styles = Chain("orphans: 4; z-index: 5", "");
        Assert.True(styles[1].TryGetInteger("orphans", out int orphans));
        Assert.Equal(4, orphans); // inherited
        Assert.False(styles[1].TryGetInteger("z-index", out _)); // not registered, so not inherited and no initial value
        Assert.True(One("").TryGetInteger("widows", out int widows));
        Assert.Equal(2, widows); // the initial value
    }

    [Theory]
    [InlineData("width: 12px", 12, CssUnit.Px)]
    [InlineData("width: 1.5EM", 1.5, CssUnit.Em)]
    [InlineData("width: 50%", 50, CssUnit.Percent)]
    [InlineData("width: 0", 0, CssUnit.Px)]
    [InlineData("width: 3vw", 3, CssUnit.Vw)]
    public void Lengths(string css, double value, CssUnit unit)
    {
        Assert.True(One(css).TryGetLength("width", out CssLength length));
        Assert.Equal(new CssLength(value, unit), length);
    }

    [Theory]
    [InlineData("width: auto")]
    [InlineData("width: 3")]
    [InlineData("width: calc(10px + 1em)")]
    [InlineData("width: 1px 2px")]
    public void LengthsRefuseOtherValues(string css)
    {
        Assert.False(One(css).TryGetLength("width", out _));
    }

    [Theory]
    [InlineData("width: 12px", 12)]
    [InlineData("width: 1in", 96)]
    [InlineData("width: 6pt", 8)]
    [InlineData("width: 2em", 40)]
    [InlineData("width: 2rem", 32)]
    [InlineData("width: 2ex", 20)]
    [InlineData("width: 2ch", 20)]
    [InlineData("width: 1ic", 20)]
    [InlineData("width: 10vw", 80)]
    [InlineData("width: 10vh", 60)]
    [InlineData("width: 10vmin", 60)]
    [InlineData("width: 10vmax", 80)]
    [InlineData("width: 10dvw", 80)]
    [InlineData("width: 0", 0)]
    [InlineData("width: calc(100px - 2em)", 60)]
    [InlineData("width: max(1em, 30px)", 30)]
    [InlineData("width: clamp(10px, 50vw, 100px)", 100)]
    public void ResolvedLengths(string css, double expected)
    {
        Assert.True(One(css).TryResolveLength("width", s_context, out double pixels));
        Assert.Equal(expected, pixels, 1e-9);
    }

    [Fact]
    public void PercentagesNeedABase()
    {
        CssComputedStyle style = One("width: 25%; height: calc(50% + 10px)");
        Assert.False(style.TryResolveLength("width", s_context, out _));
        Assert.False(style.TryResolveLength("height", s_context, out _));

        CssLengthContext context = s_context with { PercentBase = 200 };
        Assert.True(style.TryResolveLength("width", context, out double width));
        Assert.Equal(50, width);
        Assert.True(style.TryResolveLength("height", context, out double height));
        Assert.Equal(110, height);
    }

    [Fact]
    public void FontMetricsAndLineHeightComeFromTheContext()
    {
        CssComputedStyle style = One("width: 2ex; height: 2ch; min-width: 2lh; max-width: 1cap");
        CssLengthContext context = s_context with { XHeight = 9, ChWidth = 11, LineHeight = 30 };
        Assert.True(style.TryResolveLength("width", context, out double ex));
        Assert.Equal(18, ex);
        Assert.True(style.TryResolveLength("height", context, out double ch));
        Assert.Equal(22, ch);
        Assert.True(style.TryResolveLength("min-width", context, out double lh));
        Assert.Equal(60, lh);
        Assert.False(style.TryResolveLength("min-width", s_context, out _));
        Assert.False(style.TryResolveLength("max-width", context, out _));
    }

    [Theory]
    [InlineData("width: auto")]
    [InlineData("width: 5")]
    [InlineData("width: calc(1 + 2)")]
    [InlineData("width: calc(1em + 1cap)")]
    public void ResolvedLengthsRefuseOtherValues(string css)
    {
        Assert.False(One(css).TryResolveLength("width", s_context, out _));
    }

    [Fact]
    public void ContextResolvesLengthsAndCalc()
    {
        Assert.True(s_context.TryResolve(new CssLength(3, CssUnit.Rem), out double rem));
        Assert.Equal(48, rem);
        Assert.True(s_context.TryResolve(new CssLength(0, CssUnit.None), out double zero));
        Assert.Equal(0, zero);
        Assert.False(s_context.TryResolve(new CssLength(2, CssUnit.None), out _));
        Assert.False(s_context.TryResolve(new CssLength(2, CssUnit.Rlh), out _));
        Assert.True(CssCalc.TryParse("calc(1em + 1px)", out CssCalc? calc));
        Assert.True(s_context.TryResolve(calc, out double sum));
        Assert.Equal(21, sum);
    }

    [Fact]
    public void FailedVarMakesTheGetterFail()
    {
        CssComputedStyle style = One("z-index: var(--missing); display: var(--missing); width: var(--w); --w: 4px; flex-grow: var(--n, 2)");
        Assert.False(style.TryGetInteger("z-index", out _)); // invalid at computed-value time, and no initial value
        Assert.True(style.TryGetKeyword("display", out string? display));
        Assert.Equal("inline", display); // invalid at computed-value time: the initial value
        Assert.True(style.TryResolveLength("width", s_context, out double width));
        Assert.Equal(4, width);
        Assert.True(style.TryGetNumber("flex-grow", out double grow));
        Assert.Equal(2, grow);
    }

    [Fact]
    public void Urls()
    {
        CssComputedStyle style = One("list-style-image: url(\"dot.png\"); background-image: url(a.png), url(b.png)");
        Assert.True(style.TryGetUrl("list-style-image", out CssUrl url));
        Assert.Equal("dot.png", url.Url);
        Assert.False(style.TryGetUrl("background-image", out _));
        Assert.False(One("").TryGetUrl("list-style-image", out _)); // none
    }

    [Fact]
    public void FontFamiliesInherit()
    {
        CssComputedStyle[] styles = Chain("font-family: 'Open Sans', Arial, sans-serif", "");
        Assert.True(styles[1].TryGetFontFamilies(out CssFontFamilyList? families));
        Assert.Equal(["Open Sans", "Arial", "sans-serif"], families.Families.Select(f => f.Name));
        Assert.True(families.Families[2].IsGeneric);

        Assert.True(One("").TryGetFontFamilies(out CssFontFamilyList? initial));
        Assert.Equal("serif", Assert.Single(initial.Families).Name);
    }

    [Fact]
    public void ColorsResolveCurrentColor()
    {
        CssComputedStyle[] styles = Chain("color: red", "text-decoration-color: currentcolor; outline-color: #00ff00", "color: blue");
        Assert.True(styles[1].TryGetColor("text-decoration-color", out CssColor decoration));
        Assert.Equal((255, 0, 0, 255), decoration.ToRgba8());
        Assert.False(decoration.IsCurrentColor);
        Assert.True(styles[1].TryGetColor("outline-color", out CssColor outline));
        Assert.Equal((0, 255, 0, 255), outline.ToRgba8());

        // text-decoration-color is not inherited: the initial currentcolor resolves against the element's own color.
        Assert.True(styles[2].TryGetColor("text-decoration-color", out CssColor initial));
        Assert.Equal((0, 0, 255, 255), initial.ToRgba8());

        // The raw keyword stays available.
        Assert.True(styles[1].TryGetValue("text-decoration-color", out IReadOnlyList<CssComponentValue>? raw));
        Assert.True(CssColor.TryParse(raw, out CssColor keyword) && keyword.IsCurrentColor);
    }

    [Fact]
    public void ColorCurrentColorTakesTheParentsColor()
    {
        CssComputedStyle[] styles = Chain("color: rgb(10 20 30)", "color: currentColor", "", "color: currentcolor");
        foreach (CssComputedStyle style in styles)
        {
            Assert.True(style.TryGetColor("color", out CssColor color));
            Assert.Equal((10, 20, 30, 255), color.ToRgba8());
        }

        // At the root, color: currentcolor is the initial color.
        Assert.True(One("color: currentcolor").TryGetColor("color", out CssColor root));
        Assert.Equal((0, 0, 0, 255), root.ToRgba8());
        Assert.False(One("width: 3px").TryGetColor("width", out _));
    }

    [Fact]
    public void ColorThroughVarFallsBackToInheritance()
    {
        CssComputedStyle[] styles = Chain("color: green", "color: var(--nope)");
        Assert.True(styles[1].TryGetColor("color", out CssColor color));
        Assert.Equal((0, 128, 0, 255), color.ToRgba8());
    }

    [Theory]
    [InlineData("normal", 400)]
    [InlineData("bold", 700)]
    [InlineData("550", 550)]
    [InlineData("calc(100 * 3)", 300)]
    public void FontWeights(string weight, double expected)
    {
        Assert.Equal(expected, One("font-weight: " + weight).GetFontWeight());
    }

    // CSS Fonts 4, "Determination of bolder and lighter": parent weight → bolder, lighter.
    [Theory]
    [InlineData(50, 400, 50)]
    [InlineData(100, 400, 100)]
    [InlineData(349, 400, 100)]
    [InlineData(350, 700, 100)]
    [InlineData(549, 700, 100)]
    [InlineData(550, 900, 400)]
    [InlineData(749, 900, 400)]
    [InlineData(750, 900, 700)]
    [InlineData(899, 900, 700)]
    [InlineData(900, 900, 700)]
    [InlineData(950, 950, 700)]
    public void BolderAndLighterTable(double parent, double bolder, double lighter)
    {
        string p = parent.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(bolder, Chain("font-weight: " + p, "font-weight: bolder")[1].GetFontWeight());
        Assert.Equal(lighter, Chain("font-weight: " + p, "font-weight: lighter")[1].GetFontWeight());
    }

    [Fact]
    public void RelativeWeightsInheritResolved()
    {
        // 'bolder' resolves once, on the element that sets it; children inherit the number.
        CssComputedStyle[] styles = Chain("", "font-weight: bolder", "", "font-weight: bolder", "");
        Assert.Equal([400, 700, 700, 900, 900], styles.Select(s => s.GetFontWeight()));
        Assert.Equal(700, One("font-weight: bolder").GetFontWeight()); // root: against 400
    }

    [Theory]
    [InlineData("xx-small", 9.6)]
    [InlineData("x-small", 12)]
    [InlineData("small", 128.0 / 9)]
    [InlineData("medium", 16)]
    [InlineData("large", 19.2)]
    [InlineData("x-large", 24)]
    [InlineData("xx-large", 32)]
    [InlineData("xxx-large", 48)]
    [InlineData("12px", 12)]
    [InlineData("12pt", 16)]
    [InlineData("2em", 32)]
    [InlineData("150%", 24)]
    [InlineData("2rem", 32)]
    [InlineData("2ex", 16)]
    [InlineData("10vw", 80)]
    [InlineData("calc(1em + 4px)", 20)]
    [InlineData("0", 0)]
    [InlineData("math", 16)]
    public void RootFontSizes(string size, double expected)
    {
        Assert.Equal(expected, One("font-size: " + size).GetFontSize(s_context), 1e-9);
    }

    [Fact]
    public void FontSizesRelativeToTheParent()
    {
        CssComputedStyle[] styles = Chain("font-size: 20px", "font-size: 1.5em", "", "font-size: 50%", "font-size: 2rem", "font-size: calc(1em + 10%)");
        Assert.Equal([20, 30, 30, 15, 40, 44], styles.Select(s => s.GetFontSize(s_context with { RootFontSize = 20 })));
    }

    [Fact]
    public void LargerAndSmaller()
    {
        Assert.Equal(19.2, Chain("", "font-size: larger")[1].GetFontSize(s_context), 1e-9);
        Assert.Equal(128.0 / 9, Chain("", "font-size: smaller")[1].GetFontSize(s_context), 1e-9);
        Assert.Equal(48, Chain("font-size: xx-large", "font-size: larger")[1].GetFontSize(s_context), 1e-9);
        Assert.Equal(57.6, Chain("font-size: xxx-large", "font-size: larger")[1].GetFontSize(s_context), 1e-9);
        Assert.Equal(8, Chain("font-size: xx-small", "font-size: smaller")[1].GetFontSize(s_context), 1e-9);
        Assert.Equal(12, Chain("font-size: 10px", "font-size: larger")[1].GetFontSize(s_context), 1e-9);
        Assert.Equal(25, Chain("font-size: 30px", "font-size: smaller")[1].GetFontSize(s_context), 1e-9);
    }

    [Fact]
    public void FontSizeFromTheShorthandAndRegistryDefaults()
    {
        CssComputedStyle[] styles = Chain("font: bold 18px/2 serif", "font-size: inherit");
        Assert.Equal(18, styles[1].GetFontSize(s_context));
        Assert.Equal(700, styles[1].GetFontWeight());
        Assert.Equal(16, One("").GetFontSize(s_context));
        Assert.Equal(400, One("").GetFontWeight());
    }

    [Fact]
    public void FontSizeCacheFollowsTheContext()
    {
        CssComputedStyle[] styles = Chain("font-size: 10vw", "font-size: 2rem");
        Assert.Equal(80, styles[0].GetFontSize(s_context));
        Assert.Equal(80, styles[0].GetFontSize(s_context));
        Assert.Equal(40, styles[0].GetFontSize(s_context with { ViewportWidth = 400 }));
        Assert.Equal(32, styles[1].GetFontSize(s_context));
        Assert.Equal(40, styles[1].GetFontSize(s_context with { RootFontSize = 20 }));
        Assert.Equal(40, styles[1].GetFontSize(s_context with { RootFontSize = 20, FontSize = 99, PercentBase = 5 }));
    }

    [Fact]
    public void FontSizeAcrossThreads()
    {
        CssComputedStyle[] styles = Chain("font-size: 10vw", "font-size: 50%");
        Parallel.For(0, 2000, i =>
        {
            double width = 100 + (i % 7 * 100);
            Assert.Equal(width / 20, styles[1].GetFontSize(s_context with { ViewportWidth = width }), 1e-9);
        });
    }

    [Fact]
    public void GettersDoNotAllocateOnPlainValues()
    {
        CssComputedStyle[] styles = Chain("color: red; font-size: 20px; font-weight: bold", "display: block; z-index: 3; width: 2em; flex-grow: 1.5; text-decoration-color: currentcolor; list-style-image: url(a.png)");
        CssComputedStyle style = styles[1];
        CssLengthContext context = s_context with { PercentBase = 100 };

        void Run()
        {
            style.TryGetKeyword("display", out _);
            style.TryGetInteger("z-index", out _);
            style.TryGetNumber("flex-grow", out _);
            style.TryGetLength("width", out _);
            style.TryResolveLength("width", context, out _);
            style.TryGetColor("text-decoration-color", out _);
            style.TryGetColor("color", out _);
            style.TryGetUrl("list-style-image", out _);
            style.GetFontWeight();
            style.GetFontSize(context);
        }

        Run();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            Run();
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
