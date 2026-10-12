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
    [InlineData("clamp-latin")]
    [InlineData("clamp-arabic")]
    [InlineData("clamp-chinese")]
    [InlineData("interaction-clamp")]
    public void ClampedLinesStartWhereChromiumsDo(string name)
    {
        LayoutReferences.Case c = LayoutReferences.Find(name);
        int max = c.Style.GetProperty("maxLines").GetInt32();
        foreach (LayoutReferences.Layout reference in c.Layouts)
        {
            TextLayout layout = LayoutCases.Paragraph(c).Layout(reference.Width!.Value);
            Assert.Equal(reference.Lines.Take(max).Select(l => l.Start), layout.Lines.Select(l => l.Start));
            Assert.Equal(reference.Height, layout.Height, 0.01f);
        }
    }
}
