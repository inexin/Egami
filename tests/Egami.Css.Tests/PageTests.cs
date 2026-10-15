namespace Egami.Css.Tests;

public sealed class PageTests
{
    private static CssPageRule Page(string css, List<CssParseError>? errors = null)
    {
        CssStyleSheet sheet = CssStyleSheet.Parse(css, errors is null ? null : new CssParseOptions { Errors = errors.Add });
        return Assert.IsType<CssPageRule>(Assert.Single(sheet.Rules));
    }

    [Fact]
    public void ParsesSelectorsDeclarationsAndMarginRules()
    {
        List<CssParseError> errors = [];
        CssPageRule rule = Page("""
            @page Chapter:first:left, :blank {
              size: A4; margin: 2cm;
              @top-center { content: "Title" }
              @bottom-right-corner { content: counter(page) }
              @nonsense { color: red }
              @top-center x { color: red }
              p { color: red }
            }
            """, errors);
        Assert.Equal(2, rule.Selectors.Count);
        Assert.Equal("Chapter", rule.Selectors[0].Name);
        Assert.Equal(CssPagePseudoClasses.First | CssPagePseudoClasses.Left, rule.Selectors[0].PseudoClasses);
        Assert.Equal(new CssSpecificity(1, 1, 1), rule.Selectors[0].Specificity);
        Assert.Null(rule.Selectors[1].Name);
        Assert.Equal(CssPagePseudoClasses.Blank, rule.Selectors[1].PseudoClasses);
        Assert.Equal("Chapter:first:left, :blank", rule.SelectorText);
        Assert.Equal(["size", "margin"], rule.Declarations.Select(d => d.Name));
        Assert.Equal([CssMarginBox.TopCenter, CssMarginBox.BottomRightCorner], rule.MarginRules.Select(m => m.Box));
        Assert.Equal("\"Title\"", rule.MarginRules[0].Declarations[0].ValueText);
        Assert.Equal(3, errors.Count);
    }

    [Theory]
    [InlineData("@page chapter :first {}")]
    [InlineData("@page :middle {}")]
    [InlineData("@page a b {}")]
    [InlineData("@page a, {}")]
    [InlineData("@page ::first {}")]
    public void InvalidSelectorsDropTheRule(string css)
    {
        List<CssParseError> errors = [];
        Assert.Empty(CssStyleSheet.Parse(css, new CssParseOptions { Errors = errors.Add }).Rules);
        Assert.Single(errors);
    }

    [Fact]
    public void EmptyPreludeMatchesEveryPage()
    {
        CssPageRule rule = Page("@page { margin: 1in }");
        Assert.Empty(rule.Selectors);
        Assert.Equal("", rule.SelectorText);
        Assert.Empty(CssStyleSheet.Parse("@page;").Rules);
    }

    [Fact]
    public void SelectorMatching()
    {
        CssPageSelector selector = Page("@page chapter:right:first {}").Selectors[0];
        Assert.True(selector.Matches(new CssPage("chapter", IsFirst: true, IsLeft: false)));
        Assert.False(selector.Matches(new CssPage("chapter", IsFirst: true, IsLeft: true)));
        Assert.False(selector.Matches(new CssPage("Chapter", IsFirst: true, IsLeft: false)));
        Assert.False(selector.Matches(new CssPage(null, IsFirst: true, IsLeft: false)));
        Assert.False(selector.Matches(new CssPage("chapter", IsFirst: false, IsLeft: false)));
        Assert.True(Page("@page :blank {}").Selectors[0].Matches(new CssPage("x", false, true, IsBlank: true)));
        Assert.False(Page("@page :blank {}").Selectors[0].Matches(new CssPage("x", false, true)));
    }

    [Fact]
    public void CascadesBySpecificityOriginAndOrder()
    {
        CssCascade cascade = new(
        [
            (CssStyleSheet.Parse("@page { size: letter; margin: 1in } @page :first { margin-top: 3in !important }"), CssOrigin.UserAgent),
            (CssStyleSheet.Parse("""
                @page :first { margin-top: 2in }
                @page { margin-top: 1cm; margin-left: 5mm; color: red }
                @page :left { margin-left: 10mm }
                @page chapter { margin-left: 20mm }
                @page chapter:left { margin-left: 30mm }
                @media screen { @page { size: A3 } }
                @media print { @page { size: A5 } }
                """), CssOrigin.Author),
        ], new CssCascadeOptions { Media = new CssMediaContext { MediaType = "print" } });

        CssPageStyle first = cascade.CascadePage(new CssPage(null, IsFirst: true, IsLeft: false));
        Assert.Equal("3in", first.Page.GetValueText("margin-top"));
        Assert.Equal("5mm", first.Page.GetValueText("margin-left"));
        Assert.Equal("1in", first.Page.GetValueText("margin-bottom"));
        Assert.Equal("A5", first.Page.GetValueText("size"));
        Assert.Empty(first.MarginBoxes);

        CssPageStyle left = cascade.CascadePage(new CssPage(null, IsFirst: false, IsLeft: true));
        Assert.Equal("1cm", left.Page.GetValueText("margin-top"));
        Assert.Equal("10mm", left.Page.GetValueText("margin-left"));

        // A page type name outranks :left; both together outrank either.
        Assert.Equal("20mm", cascade.CascadePage(new CssPage("chapter", false, false)).Page.GetValueText("margin-left"));
        Assert.Equal("30mm", cascade.CascadePage(new CssPage("chapter", false, true)).Page.GetValueText("margin-left"));
    }

    [Fact]
    public void MarginBoxesCascadeAndInheritFromThePage()
    {
        CssCascade cascade = new([(CssStyleSheet.Parse("""
            @page { color: navy; font-size: 9pt; @bottom-center { content: counter(page); color: gray } }
            @page :first { @bottom-center { content: none } @top-left { content: "Draft" } }
            """), CssOrigin.Author)]);

        CssComputedStyle root = cascade.Compute(new CssCascadedStyle(), null);
        CssPageStyle second = cascade.CascadePage(new CssPage(null, false, false));
        CssComputedStyle page = cascade.Compute(second.Page, root);
        CssComputedStyle bottom = cascade.Compute(second.MarginBoxes[CssMarginBox.BottomCenter], page);
        Assert.Equal("counter(page)", bottom.GetValueText("content"));
        Assert.Equal("gray", bottom.GetValueText("color"));
        Assert.Equal("9pt", bottom.GetValueText("font-size"));

        CssPageStyle first = cascade.CascadePage(new CssPage(null, true, false));
        Assert.Equal("none", first.MarginBoxes[CssMarginBox.BottomCenter].GetValueText("content"));
        Assert.Equal("\"Draft\"", first.MarginBoxes[CssMarginBox.TopLeft].GetValueText("content"));
        Assert.Equal(2, first.MarginBoxes.Count);
    }

    [Theory]
    [InlineData("auto", null, null, null, CssPageOrientation.Auto)]
    [InlineData("landscape", null, null, null, CssPageOrientation.Landscape)]
    [InlineData("A4", "210mm", "297mm", "a4", CssPageOrientation.Auto)]
    [InlineData("a4 landscape", "297mm", "210mm", "a4", CssPageOrientation.Landscape)]
    [InlineData("portrait letter", "8.5in", "11in", "letter", CssPageOrientation.Portrait)]
    [InlineData("JIS-B5", "182mm", "257mm", "jis-b5", CssPageOrientation.Auto)]
    [InlineData("10cm", "10cm", "10cm", null, CssPageOrientation.Auto)]
    [InlineData("8.5in 11in", "8.5in", "11in", null, CssPageOrientation.Auto)]
    public void PageSize(string text, string? width, string? height, string? name, CssPageOrientation orientation)
    {
        Assert.True(CssPageSize.TryParse(text, out CssPageSize size));
        Assert.Equal(width, size.Width?.ToString());
        Assert.Equal(height, size.Height?.ToString());
        Assert.Equal(name, size.Name);
        Assert.Equal(orientation, size.Orientation);
        Assert.Equal(width is null, size.IsAuto);
        Assert.True(CssPageSize.TryParse(size.ToString(), out CssPageSize again));
        Assert.Equal(size, again);
    }

    [Theory]
    [InlineData("")]
    [InlineData("auto auto")]
    [InlineData("-1cm")]
    [InlineData("10%")]
    [InlineData("10")]
    [InlineData("1cm landscape")]
    [InlineData("landscape portrait")]
    [InlineData("A4 letter")]
    [InlineData("A6")]
    [InlineData("1cm 2cm 3cm")]
    public void InvalidPageSize(string text) => Assert.False(CssPageSize.TryParse(text, out _));

    [Fact]
    public void MarginBoxNames()
    {
        foreach (CssMarginBox box in Enum.GetValues<CssMarginBox>())
        {
            CssPageRule rule = Page("@page { @" + CssPageMarginRule.GetName(box) + " { content: 'x' } }");
            Assert.Equal(box, Assert.Single(rule.MarginRules).Box);
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => CssPageMarginRule.GetName((CssMarginBox)16));
        Assert.Contains("ledger", CssPageSize.Names);
    }
}
