namespace Egami.Css.Tests;

public sealed class MediaTests
{
    private static readonly CssMediaContext s_context = new() { Width = 800, Height = 600, Resolution = 2, PrefersColorScheme = "dark" };

    [Theory]
    [InlineData("", true)]
    [InlineData("all", true)]
    [InlineData("screen", true)]
    [InlineData("SCREEN", true)]
    [InlineData("print", false)]
    [InlineData("tv", false)]
    [InlineData("not print", true)]
    [InlineData("not screen", false)]
    [InlineData("only screen", true)]
    [InlineData("print, screen", true)]
    [InlineData("screen and (min-width: 800px)", true)]
    [InlineData("screen and (min-width: 801px)", false)]
    [InlineData("(max-width: 50em)", true)]
    [InlineData("(max-width: 49em)", false)]
    [InlineData("(width: 800px)", true)]
    [InlineData("(width)", true)]
    [InlineData("(400px <= width < 900px)", true)]
    [InlineData("(400px <= width < 800px)", false)]
    [InlineData("(width > 8.3333in)", true)]
    [InlineData("(height >= 600px) and (orientation: landscape)", true)]
    [InlineData("(orientation: portrait)", false)]
    [InlineData("(aspect-ratio: 4/3)", true)]
    [InlineData("(min-aspect-ratio: 16/9)", false)]
    [InlineData("(resolution >= 2dppx)", true)]
    [InlineData("(min-resolution: 192dpi)", true)]
    [InlineData("(prefers-color-scheme: dark)", true)]
    [InlineData("(prefers-reduced-motion: reduce)", false)]
    [InlineData("(prefers-reduced-motion)", false)]
    [InlineData("(color)", true)]
    [InlineData("(min-color: 8)", true)]
    [InlineData("(monochrome)", false)]
    [InlineData("(grid)", false)]
    [InlineData("(width: 25vw)", false)]
    [InlineData("(width: calc(700px + 100px))", true)]
    [InlineData("not (width < 100px)", true)]
    [InlineData("(width < 100px) or (height > 100px)", true)]
    [InlineData("(width < 100px) or (unknown-feature)", false)]
    [InlineData("not (unknown-feature)", false)]
    [InlineData("(unknown-feature), screen", true)]
    [InlineData("screen and (width < 100px) or (height > 100px)", false)]
    [InlineData("(width < 100px) and (height > 100px) or (color)", false)]
    [InlineData("(min-orientation: landscape)", false)]
    [InlineData("(min-width < 100px)", false)]
    [InlineData("foo(bar)", false)]
    [InlineData("and", false)]
    [InlineData("screen and", false)]
    [InlineData("(width: 800)", false)]
    [InlineData("(width: 0)", false)]
    [InlineData("(5px < width < 3000px < x)", false)]
    public void Evaluates(string query, bool expected)
    {
        Assert.Equal(expected, CssMediaQueryList.Parse(query).Matches(s_context));
    }

    [Fact]
    public void FeatureHook()
    {
        CssMediaContext context = s_context with
        {
            Feature = (name, value) => name switch
            {
                "hover" => value is null || value[0].ToString() == "hover",
                "pointer" => value?[0].ToString() == "fine",
                _ => null,
            },
        };
        Assert.True(CssMediaQueryList.Parse("(hover) and (pointer: fine)").Matches(context));
        Assert.False(CssMediaQueryList.Parse("(pointer: coarse)").Matches(context));
        Assert.False(CssMediaQueryList.Parse("(scan: progressive)").Matches(context));
    }

    [Fact]
    public void InvalidQueriesBecomeNotAll()
    {
        CssMediaQueryList list = CssMediaQueryList.Parse("screen and, print");
        Assert.Equal(2, list.Count);
        Assert.Equal("not all, print", list.MediaText);
        Assert.False(list.Matches(s_context));
        Assert.True(list.Matches(s_context with { MediaType = "print" }));
    }
}
