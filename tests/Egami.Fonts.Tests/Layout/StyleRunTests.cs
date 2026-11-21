using Egami.Fonts.Layout;
using Egami.Fonts.Unicode;
using Egami.Pixels;

namespace Egami.Fonts.Tests.Layout;

/// <summary>
/// Style changes inside a shaped run (a color, baseline shift or line height that shapes like the text around it): each lays
/// out as a run of its own with its own style index, while kerning and ligatures across the change stay.
/// </summary>
public sealed class StyleRunTests
{
    private static readonly TextStyle Red = new() { Color = new Rgba8(255, 0, 0, 255) };

    private static RichText Spanned(string text, int start, int length, TextStyle style) => new(text, [new StyleSpan(start, length, style)]);

    private static List<LayoutRun> TextRuns(TextLayout layout) => [.. layout.Lines.SelectMany(l => l.Runs).Where(r => r.Kind == LayoutRunKind.Text)];

    /// <summary>The pen x of every glyph of a line by cluster, in visual order.</summary>
    private static List<(int Cluster, ushort Glyph, float X, float Y)> Glyphs(TextLine line) =>
        [.. line.Runs.Where(r => r.Glyphs is not null).SelectMany(r => Enumerable.Range(0, r.Glyphs!.Count).Select(g => (r.Glyphs.Clusters[g], r.Glyphs.Glyphs[g], r.Glyphs.Positions[g].X, r.Glyphs.Positions[g].Y)))];

    private static void AssertSameGlyphs(List<(int Cluster, ushort Glyph, float X, float Y)> expected, List<(int Cluster, ushort Glyph, float X, float Y)> actual)
    {
        Assert.Equal(expected.Select(g => (g.Cluster, g.Glyph)), actual.Select(g => (g.Cluster, g.Glyph)));
        for (int g = 0; g < expected.Count; g++)
        {
            Assert.Equal(expected[g].X, actual[g].X, 0.001f);
            Assert.Equal(expected[g].Y, actual[g].Y, 0.001f);
        }
    }

    [Fact]
    public void AColorSpanMidLineIsARunOfItsOwnStyle()
    {
        const string text = "black red black";
        Paragraph paragraph = Paragraph.Create(Spanned(text, 6, 3, Red), ParagraphTests.Style("NotoSans-Regular.ttf"));
        TextLayout layout = paragraph.Layout(float.PositiveInfinity);

        List<LayoutRun> runs = TextRuns(layout);
        Assert.Equal([(0, 6, 0), (6, 3, 1), (9, 6, 0)], runs.Select(r => (r.Start, r.Length, r.StyleIndex)));
        Assert.All(runs.Where(r => r.StyleIndex == 1).SelectMany(r => r.Glyphs!.Clusters.ToArray()), c => Assert.InRange(c, 6, 8));
        Assert.Equal(Red.Color, paragraph.GetStyle(runs[1].StyleIndex).Color);
    }

    [Fact]
    public void ColorSpansKeepKerningAndAdvances()
    {
        // "AVA" kerns in Noto Sans; coloring the V leaves every glyph where it was.
        const string text = "AVATAR WAVE";
        ParagraphStyle style = ParagraphTests.Style("NotoSans-Regular.ttf");
        TextLayout plain = Paragraph.Create(text, style).Layout(float.PositiveInfinity);
        TextLayout colored = Paragraph.Create(Spanned(text, 1, 1, Red), style).Layout(float.PositiveInfinity);

        Font font = new(ParagraphTests.Family("NotoSans-Regular.ttf").Match(FontStyle.Normal).Typeface, Paragraph.DefaultSize);
        float unkerned = font.GetAdvance(font.Typeface.GetGlyph('A')) + font.GetAdvance(font.Typeface.GetGlyph('V'));
        List<(int Cluster, ushort Glyph, float X, float Y)> expected = Glyphs(plain.Lines[0]);
        Assert.True(expected[2].X - expected[0].X < unkerned - 0.1f, "the font kerns A and V");

        AssertSameGlyphs(expected, Glyphs(colored.Lines[0]));
        Assert.Equal(plain.Lines[0].Width, colored.Lines[0].Width);
        Assert.Equal([0, 1, 0], TextRuns(colored).Select(r => r.StyleIndex));
    }

    [Fact]
    public void ALigatureAcrossAColorChangeTakesItsFirstCharactersStyle()
    {
        const string text = "office";
        ParagraphStyle style = ParagraphTests.Style("SourceSerif4-Regular.otf");
        TextLayout plain = Paragraph.Create(text, style).Layout(float.PositiveInfinity);
        Assert.True(plain.Lines[0].Runs[0].Glyphs!.Count < text.Length, "the font makes a ligature of ffi");

        // The red span starts on the second f, inside the ligature: the ligature stays and goes with the first f's style.
        TextLayout colored = Paragraph.Create(Spanned(text, 2, 4, Red), style).Layout(float.PositiveInfinity);
        AssertSameGlyphs(Glyphs(plain.Lines[0]), Glyphs(colored.Lines[0]));
        List<LayoutRun> runs = TextRuns(colored);
        Assert.All(runs, r => Assert.Equal(r.StyleIndex, colored.Paragraph.RichText.Spans.Any(s => r.Start >= s.Start && r.Start < s.Start + s.Length) ? 1 : 0));
        Assert.Equal(text.Length, runs.Sum(r => r.Length));
        Assert.Equal(1, runs[^1].StyleIndex);
    }

    [Fact]
    public void RightToLeftColorSpansAreRunsInVisualOrder()
    {
        const string text = "אבג דהו זחט";
        ParagraphStyle style = ParagraphTests.Style("NotoSans-Regular.ttf", "shaping/NotoSansHebrew-Regular.ttf") with { Direction = TextDirection.RightToLeft };
        TextLayout plain = Paragraph.Create(text, style).Layout(float.PositiveInfinity);
        TextLayout colored = Paragraph.Create(Spanned(text, 5, 1, Red), style).Layout(float.PositiveInfinity);

        // Left to right: the logically last word, then the middle word's last letter, its red middle one and its first, then
        // the first word (spaces are runs of their own, in Noto Sans).
        List<LayoutRun> runs = TextRuns(colored);
        Assert.Equal([(8, 3, 0), (7, 1, 0), (6, 1, 0), (5, 1, 1), (4, 1, 0), (3, 1, 0), (0, 3, 0)], runs.Select(r => (r.Start, r.Length, r.StyleIndex)));
        Assert.True(runs.Zip(runs.Skip(1)).All(p => p.First.X < p.Second.X));
        AssertSameGlyphs(Glyphs(plain.Lines[0]), Glyphs(colored.Lines[0]));
    }

    [Fact]
    public void ABaselineShiftMidLineMovesOnlyItsGlyphs()
    {
        const string text = "base raised base";
        ParagraphStyle style = ParagraphTests.Style("NotoSans-Regular.ttf");
        TextLayout plain = Paragraph.Create(text, style).Layout(float.PositiveInfinity);
        TextLayout shifted = Paragraph.Create(Spanned(text, 5, 6, new TextStyle { BaselineShift = -6 }), style).Layout(float.PositiveInfinity);

        List<(int Cluster, ushort Glyph, float X, float Y)> before = Glyphs(plain.Lines[0]);
        List<(int Cluster, ushort Glyph, float X, float Y)> after = Glyphs(shifted.Lines[0]);
        float baselineMove = shifted.Lines[0].Baseline - plain.Lines[0].Baseline;
        for (int g = 0; g < before.Count; g++)
        {
            Assert.Equal(before[g].X, after[g].X, 0.001f);
            float expected = before[g].Cluster is >= 5 and < 11 ? -6 : 0;
            Assert.Equal(expected + baselineMove, after[g].Y - before[g].Y, 0.001f);
        }

        // The raised run makes the line box taller above the baseline.
        Assert.True(shifted.Lines[0].Ascent > plain.Lines[0].Ascent);
    }

    [Fact]
    public void ALineHeightMidLineSetsTheLineBox()
    {
        const string text = "short tall short";
        ParagraphStyle style = ParagraphTests.Style("NotoSans-Regular.ttf");
        TextLayout plain = Paragraph.Create(text, style).Layout(float.PositiveInfinity);
        TextLayout tall = Paragraph.Create(Spanned(text, 6, 4, new TextStyle { LineHeight = LineHeight.Absolute(60) }), style).Layout(float.PositiveInfinity);
        Assert.Equal(60, tall.Lines[0].Height, 1f);
        Assert.True(tall.Lines[0].Height > plain.Lines[0].Height + 20);
    }

    [Fact]
    public void StyleRunsFollowLineBreaksAndJustification()
    {
        // A red span across a wrap: each line has its own red piece, and justified lines still fill the width.
        string text = string.Join(' ', Enumerable.Repeat("word", 12));
        ParagraphStyle style = ParagraphTests.Style("NotoSans-Regular.ttf") with { Align = TextAlign.Justify };
        Paragraph paragraph = Paragraph.Create(Spanned(text, 12, 20, Red), style);
        TextLayout layout = paragraph.Layout(120);
        TextLayout plain = Paragraph.Create(text, style).Layout(120);
        Assert.True(layout.Lines.Count >= 3);
        Assert.Equal(plain.Lines.Count, layout.Lines.Count);
        for (int li = 0; li < layout.Lines.Count; li++)
        {
            TextLine line = layout.Lines[li];
            AssertSameGlyphs(Glyphs(plain.Lines[li]), Glyphs(line));
            foreach (LayoutRun run in line.Runs.Where(r => r.Kind == LayoutRunKind.Text))
            {
                for (int i = run.Start; i < run.Start + run.Length; i++)
                {
                    Assert.Equal(paragraph.RichText.SpanAt(i) + 1, run.StyleIndex);
                }
            }

            Assert.Equal(line.Length, line.Runs.Sum(r => r.Length));
        }
    }
}
