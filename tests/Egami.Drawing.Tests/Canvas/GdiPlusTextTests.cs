using Egami.Fonts;
using Egami.Fonts.Layout;

namespace Egami.Drawing.Tests.Canvas;

/// <summary>GdiPlusText.Baseline: one line a cell tall (plus 1/8 em with the default format), GDI+'s ascent below its top.</summary>
public class GdiPlusTextTests
{
    // Noto Sans sets USE_TYPO_METRICS, so its cell is the typographic ascent and descent.
    private static float Cell(Font font) => (font.Typeface.TypoMetrics.Ascent - font.Typeface.TypoMetrics.Descent) * font.Scale;

    [Theory]
    [InlineData(VerticalAlign.Top, false)]
    [InlineData(VerticalAlign.Center, false)]
    [InlineData(VerticalAlign.Bottom, false)]
    [InlineData(VerticalAlign.Center, true)]
    public void DesignMetricsPlaceTheCellUnrounded(VerticalAlign align, bool typographic)
    {
        Font font = new(TextTests.Noto.Value, 16);
        var box = new RectF(0, 3.3f, 120, 40);
        float block = Cell(font) + (typographic ? 0 : 2);
        float top = 3.3f + (align switch { VerticalAlign.Center => (40 - block) / 2, VerticalAlign.Bottom => 40 - block, _ => 0 });
        Assert.Equal(top + (font.Typeface.TypoMetrics.Ascent * font.Scale), GdiPlusText.Baseline(font, box, align, typographic), 4);
        Assert.Equal(font.Typeface.TypoMetrics.Ascent * font.Scale, font.GdiPlusAscent, 4);
    }

    [Fact]
    public void GridFittedFontsRoundTheTopAndTheAscent()
    {
        Font font = new Font(TextTests.Noto.Value, 16).WithGdiPlusLook();
        float ascent = font.Typeface.TypoMetrics.Ascent * font.Scale;
        Assert.Equal(MathF.Floor(ascent + 0.5f), font.GdiPlusAscent);
        float top = 3.3f + ((40 - Cell(font) - 2) / 2);
        Assert.Equal(MathF.Floor(top + 0.5f) + font.GdiPlusAscent, GdiPlusText.Baseline(font, new RectF(0, 3.3f, 120, 40), VerticalAlign.Center));
    }

    [Fact]
    public void AnUndefinedAlignmentThrows()
        => Assert.Throws<ArgumentOutOfRangeException>(() => GdiPlusText.Baseline(new Font(TextTests.Noto.Value, 16), new RectF(0, 0, 10, 10), (VerticalAlign)7));
}
