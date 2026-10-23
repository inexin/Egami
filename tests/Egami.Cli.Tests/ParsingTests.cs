using Egami.Cli.Host;
using Egami.Cli.Pipeline;
using Egami.Cli.Steps;
using Egami.Pixels;

namespace Egami.Cli.Tests;

/// <summary>The size syntax, limits, output templates and option values.</summary>
public class ParsingTests
{
    [Theory]
    [InlineData("800", 4000, 3000, "contain", 800, 600)]
    [InlineData("x300", 4000, 3000, "contain", 400, 300)]
    [InlineData("800x800", 4000, 3000, "contain", 800, 600)]
    [InlineData("800x800^", 4000, 3000, "contain", 1067, 800)]
    [InlineData("800x800!", 4000, 3000, "contain", 800, 800)]
    [InlineData("800x800", 4000, 3000, "fill", 800, 800)]
    [InlineData("50%", 4000, 3000, "contain", 2000, 1500)]
    [InlineData("50%x25%", 4000, 3000, "contain", 2000, 750)]
    [InlineData("12.5%", 4000, 3000, "contain", 500, 375)]
    [InlineData("8000x8000>", 4000, 3000, "contain", 4000, 3000)]
    [InlineData("800x800>", 4000, 3000, "contain", 800, 600)]
    [InlineData("800x800<", 4000, 3000, "contain", 4000, 3000)]
    [InlineData("8000x8000<", 4000, 3000, "contain", 8000, 6000)]
    [InlineData("800x800", 4000, 3000, "cover", 800, 800)]
    public void SizeSyntaxGivesTheExpectedSize(string text, int width, int height, string fit, int expectedWidth, int expectedHeight)
    {
        Size target = SizeSpec.Parse(text).Target(new Size(width, height), ValueParsers.Enum<FitMode>(fit));
        Assert.Equal(new Size(expectedWidth, expectedHeight), target);
    }

    [Theory]
    [InlineData("")]
    [InlineData("x")]
    [InlineData("-5")]
    [InlineData("0x10")]
    [InlineData("10x10<>")]
    [InlineData("10x10!^")]
    [InlineData("abc")]
    [InlineData("10.5x10")]
    [InlineData("10x10@")]
    public void BadSizesAreUsageErrors(string text)
    {
        UsageException error = Assert.Throws<UsageException>(() => SizeSpec.Parse(text));
        Assert.Contains("not a valid size", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("800x600>")]
    [InlineData("50%")]
    [InlineData("x300")]
    [InlineData("100x100^")]
    public void SizeSyntaxRoundTrips(string text)
    {
        Assert.Equal(text, SizeSpec.Parse(text).ToString());
    }

    [Fact]
    public void LimitsParseEveryName()
    {
        SafetyLimits limits = LimitSpec.Apply(SafetyLimits.Default, "pixels=8000x8000, width=10k,height=2000,frames=50,memory=512MB,metadata=1M", "--limit");
        Assert.Equal(64_000_000, limits.MaxPixels);
        Assert.Equal(10_000, limits.MaxWidth);
        Assert.Equal(2_000, limits.MaxHeight);
        Assert.Equal(50, limits.MaxFrames);
        Assert.Equal(512L << 20, limits.MaxTotalBytes);
        Assert.Equal(1L << 20, limits.MaxMetadataBytes);
    }

    [Theory]
    [InlineData("pixels")]
    [InlineData("colors=5")]
    [InlineData("memory=lots")]
    [InlineData("frames=0")]
    public void BadLimitsAreUsageErrors(string text)
    {
        Assert.Throws<UsageException>(() => LimitSpec.Apply(SafetyLimits.Default, text, "--limit"));
    }

    [Fact]
    public void TemplatesResolveEveryPlaceholder()
    {
        InputFile input = new(Path.Combine("photos", "2026", "beach.jpg"), "photos/2026/beach.jpg", "2026", 7);
        Assert.Equal("web/2026/beach.webp", OutputTemplate.Parse("web/{dir}/{name}.{format}").Resolve(input, "webp"));
        Assert.Equal("beach-jpg-007.png", OutputTemplate.Parse("{name}-{ext}-{index:000}.png").Resolve(input, "png"));
        Assert.Equal("f/{x}-0004.png", OutputTemplate.Parse("f/{{x}}-{frame:0000}.png").Resolve(input, "png", part: 4));
        InputFile top = input with { Directory = string.Empty };
        Assert.Equal("web/beach.webp", OutputTemplate.Parse("web/{dir}/{name}.webp").Resolve(top, "webp"));
        Assert.Equal("beach.webp", OutputTemplate.Parse("{dir}/{name}.webp").Resolve(top, "webp"));
    }

    [Theory]
    [InlineData("out/{nam}.png")]
    [InlineData("out/{name.png")]
    [InlineData("out/name}.png")]
    [InlineData("out/{name:00}.png")]
    [InlineData("out/{index:0a}.png")]
    public void BadTemplatesAreUsageErrors(string text)
    {
        Assert.Throws<UsageException>(() => OutputTemplate.Parse(text));
    }

    [Fact]
    public void ColorsAreCss()
    {
        Assert.Equal(new Rgba8(255, 136, 0, 255), ValueParsers.ParseColor("#ff8800"));
        Assert.Equal(new Rgba8(255, 255, 255, 255), ValueParsers.ParseColor("white"));
        Assert.Equal(new Rgba8(0, 0, 0, 0), ValueParsers.ParseColor("transparent"));
        Assert.Equal(new Rgba8(0, 0, 255, 128), ValueParsers.ParseColor("rgb(0 0 255 / 50%)"));
        Assert.Throws<UsageException>(() => ValueParsers.ParseColor("blurple"));
    }

    [Fact]
    public void ChoicesIgnoreCaseAndDashes()
    {
        Assert.Equal(Operations.Anchor.TopLeft, ValueParsers.Enum<Operations.Anchor>("top-left"));
        Assert.Equal(Operations.Anchor.TopLeft, ValueParsers.Enum<Operations.Anchor>("TopLeft"));
        UsageException error = Assert.Throws<UsageException>(() => ValueParsers.Enum<Operations.Anchor>("middle"));
        Assert.Contains("top-left", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("srgb", "sRGB")]
    [InlineData("display-p3", "Display P3")]
    [InlineData("Rec2020", "Rec. 2020")]
    public void ProfilesHaveNames(string text, string fragment)
    {
        Assert.Contains(fragment, ValueParsers.ParseProfile(text).Name, StringComparison.OrdinalIgnoreCase);
    }
}
