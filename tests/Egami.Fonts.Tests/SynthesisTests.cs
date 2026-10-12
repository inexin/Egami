namespace Egami.Fonts.Tests;

/// <summary>Synthetic styles and rendering switches on <see cref="Font"/> (2.7.2 tasks 8 to 10): the numbers and the defaults.</summary>
public class SynthesisTests
{
    [Fact]
    public void BoldOutsetFollowsTheSizeCurve()
    {
        Assert.Equal(9 / 24f, Synthesis.BoldOutset(9), 5);
        Assert.Equal(6 / 24f, Synthesis.BoldOutset(6), 5);
        Assert.Equal(36 / 32f, Synthesis.BoldOutset(36), 5);
        Assert.Equal(100 / 32f, Synthesis.BoldOutset(100), 5);
        float mid = 22.5f * ((1 / 24f) + (((1 / 32f) - (1 / 24f)) * 0.5f));
        Assert.Equal(mid, Synthesis.BoldOutset(22.5f), 5);
        Assert.Equal(MathF.Tan(14 * MathF.PI / 180), Synthesis.Skew, 6);
    }

    [Fact]
    public void DefaultsKeep271Measuring()
    {
        Font font = new(TestFonts.Load("NotoSans-Regular.ttf"), 12);
        Assert.Equal(FontHinting.None, font.Hinting);
        Assert.Equal(FontEdging.Antialias, font.Edging);
        Assert.True(font.Subpixel);
        Assert.True(font.AllowSynthesis);
        Assert.Throws<ArgumentOutOfRangeException>(() => font with { Edging = (FontEdging)7 });
        Assert.Throws<ArgumentOutOfRangeException>(() => font with { Hinting = (FontHinting)7 });
        Font synthetic = font with { Synthesis = FontSynthesis.Bold, AllowSynthesis = false };
        ushort stem = font.Typeface.GetGlyph('I');
        Assert.Equal(font.GetGlyphExtents(stem), synthetic.GetGlyphExtents(stem));
        Assert.True((font with { Synthesis = FontSynthesis.Italic }).GetGlyphExtents(stem).XMax > font.GetGlyphExtents(stem).XMax);
    }

    [Fact]
    public void HintZonesComeFromBluesOrFromTheGlyphs()
    {
        HintZone[] cff = TestFonts.Load("SourceSans3-Regular.otf").HintZones;
        Assert.Contains(cff, z => z.Flat == 0 && z.Overshoot < 0);
        Assert.True(cff.Length >= 3);
        HintZone[] measured = TestFonts.Load("NotoSans-Regular.ttf").HintZones;
        Typeface noto = TestFonts.Load("NotoSans-Regular.ttf");
        Assert.Contains(measured, z => z.Flat == 0);
        Assert.Contains(measured, z => z.Flat == noto.GetGlyphExtents(noto.GetGlyph('x')).YMax);
        Assert.Contains(measured, z => z.Flat == noto.GetGlyphExtents(noto.GetGlyph('H')).YMax);
        Assert.Equal(measured.OrderBy(z => z.Flat), measured);
    }
}
