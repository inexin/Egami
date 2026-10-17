namespace Egami.Fonts.Tests;

/// <summary>The Font value: what default(Font) does.</summary>
public class FontTests
{
    [Fact]
    public void DefaultFontThrowsInvalidOperationWhereItNeedsAFace()
    {
        Font font = default;
        Assert.Null(font.Typeface);
        Assert.Equal(0, font.Scale);
        Assert.Equal(new Font(), font);

        Assert.Throws<InvalidOperationException>(() => font.EffectiveTypeface);
        Assert.Throws<InvalidOperationException>(() => font.Extents);
        Assert.Throws<InvalidOperationException>(() => font.LineHeight);
        Assert.Throws<InvalidOperationException>(() => font.GetAdvance(1));
        Assert.Throws<InvalidOperationException>(() => font.GetGlyphExtents(1));
        Assert.Throws<InvalidOperationException>(() => font.Shape("A"));
        Assert.Throws<InvalidOperationException>(() => font.MeasureText("A"));
    }
}
