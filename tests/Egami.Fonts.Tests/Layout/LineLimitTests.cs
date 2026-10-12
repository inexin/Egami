using Egami.Fonts.Layout;

namespace Egami.Fonts.Tests.Layout;

/// <summary>Max lines, max height and the ellipsis (task 8).</summary>
public sealed class LineLimitTests
{
    private static readonly string English = LayoutReferences.Find("latin").Text;

    [Fact]
    public void MaxLinesCutsAndEllipsizes()
    {
        ParagraphStyle style = ParagraphTests.Style("NotoSans-Regular.ttf") with { MaxLines = 3 };
        Paragraph paragraph = Paragraph.Create(English, style);
        TextLayout full = Paragraph.Create(English, style with { MaxLines = null }).Layout(200);
        TextLayout cut = paragraph.Layout(200);

        Assert.Equal(3, cut.Lines.Count);
        Assert.True(cut.DidExceed);
        Assert.Equal([.. full.Lines.Take(3).Select(l => l.Start)], cut.Lines.Select(l => l.Start));
        TextLine last = cut.Lines[2];
        Assert.True(last.Ellipsized);
        Assert.False(cut.Lines[1].Ellipsized);
        Assert.True(last.Width <= 200);

        // The ellipsis follows the kept text, and everything after the kept text is hidden.
        LayoutRun ellipsis = last.Runs[^1];
        Assert.Equal(0, ellipsis.Length);
        Assert.Equal(ellipsis.Start, cut.HiddenRange!.Value.Start);
        Assert.Equal(English.Length, cut.HiddenRange.Value.End);
        Assert.True(ellipsis.Start > last.Start && ellipsis.Start <= full.Lines[2].End);
        Assert.Equal(cut.Lines[2].Bottom, cut.Height);

        // Text that fits is not cut.
        TextLayout wide = paragraph.Layout(float.PositiveInfinity);
        Assert.False(wide.DidExceed);
        Assert.Null(wide.HiddenRange);
    }

    [Fact]
    public void MaxHeightStopsBeforeTheLineThatPassesIt()
    {
        ParagraphStyle style = ParagraphTests.Style("NotoSans-Regular.ttf") with { MaxHeight = 50 };
        TextLayout layout = Paragraph.Create(English, style).Layout(200);
        Assert.Equal(2, layout.Lines.Count);
        Assert.True(layout.Height <= 50);
        Assert.True(layout.DidExceed);
        Assert.True(layout.Lines[1].Ellipsized);
    }

    [Fact]
    public void NoEllipsisClipsAtTheLineEnd()
    {
        ParagraphStyle style = ParagraphTests.Style("NotoSans-Regular.ttf") with { MaxLines = 2, Ellipsis = null };
        TextLayout layout = Paragraph.Create(English, style).Layout(200);
        Assert.Equal(2, layout.Lines.Count);
        Assert.False(layout.Lines[1].Ellipsized);
        Assert.Equal(layout.Lines[1].End, layout.HiddenRange!.Value.Start);
    }

    [Fact]
    public void RightToLeftParagraphsEndWithTheEllipsisOnTheLeft()
    {
        LayoutReferences.Case c = LayoutReferences.Find("clamp-arabic");
        TextLayout layout = LayoutCases.Paragraph(c).Layout(200);
        Assert.Equal(2, layout.Lines.Count);
        TextLine last = layout.Lines[1];
        Assert.True(last.Ellipsized);
        Assert.Equal(0, last.Runs[0].Length);
        Assert.Equal(last.Runs.Min(r => r.X), last.Runs[0].X);
    }

    [Theory]
    [InlineData("clamp-latin", 0)]
    [InlineData("clamp-arabic", 0)]
    [InlineData("clamp-chinese", 16)]
    [InlineData("interaction-clamp", 0)]
    public void EllipsesSitWhereChromiumsDo(string name, float shift)
    {
        // Chromium's ellipsis ink, measured at 4 device pixels per unit, against the ink of Egami's ellipsis glyphs. The Chinese
        // line is exactly full: Chromium's advances, each snapped up to its 1/64 layout unit, leave the ellipsis no room after
        // dropping one character, so it drops two; Egami's exact advances fit after one, so its ellipsis sits one character on.
        LayoutReferences.Case c = LayoutReferences.Find(name);
        foreach (LayoutReferences.Layout reference in c.Layouts)
        {
            float[] ink = reference.Ellipsis!;
            TextLayout layout = LayoutCases.Paragraph(c).Layout(reference.Width!.Value);
            TextLine last = layout.Lines[^1];
            LayoutRun ellipsis = last.Runs.Single(r => r.Length == 0);
            TextBounds bounds = ellipsis.Glyphs!.Bounds;
            Assert.Equal(ink[0] + shift, bounds.Left, 0.5f);
            Assert.Equal(ink[1] + shift, bounds.Right, 0.5f);
        }
    }
}
