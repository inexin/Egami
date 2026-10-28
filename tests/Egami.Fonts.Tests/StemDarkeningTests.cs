namespace Egami.Fonts.Tests;

/// <summary>Stem darkening's curve (FreeType's documented default for its CFF engine) and the stem width it follows.</summary>
public class StemDarkeningTests
{
    [Fact]
    public void DarkeningFollowsTheDocumentedCurve()
    {
        // Half the amount, per side: 0.4 up to 0.5 px stems, 0.275 from 1 to 1.667, none from 2.333, linear between.
        Assert.Equal(0.2f, Synthesis.DarkeningOutset(0.3f), 4);
        Assert.Equal(0.16875f, Synthesis.DarkeningOutset(0.75f), 4);
        Assert.Equal(0.1375f, Synthesis.DarkeningOutset(1.3f), 4);
        Assert.Equal(0.06875f, Synthesis.DarkeningOutset(2f), 3);
        Assert.Equal(0f, Synthesis.DarkeningOutset(3f));
    }

    [Fact]
    public void StemWidthComesFromStdVWOrTheGlyphL()
    {
        // Noto Sans' stem across 'l', and Source Sans' CFF StdVW.
        Assert.InRange(Typeface.FromFile(TestFonts.FontPath("NotoSans-Regular.ttf")).StemWidth, 80, 110);
        Assert.InRange(Typeface.FromFile(TestFonts.FontPath("SourceSans3-Regular.otf")).StemWidth, 60, 110);
    }

    [Fact]
    public void DarkeningGrowsSmallTextAndLeavesLargeTextAlone()
    {
        Typeface face = Typeface.FromFile(TestFonts.FontPath("NotoSans-Regular.ttf"));
        var font = new Font(face, 10) { StemDarkening = true };
        Assert.True(font.Outset(10, device: true) > 0.1f);
        Assert.Equal(0f, font.Outset(48, device: true));
        Assert.Equal(0f, font.Outset(10, device: false));
    }
}
