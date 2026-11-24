using Egami.Fonts.Layout;
using Egami.Fonts.Unicode;

namespace Egami.Fonts.Tests.Layout;

/// <summary>ParagraphStyle.JustifyLineBreaks and StretchNoBreakSpaces, and justification after a line's last tab: justified
/// lines spread as Word spreads them.</summary>
public sealed class JustifyLineBreaksTests
{
    private const string Text = "In olden times when wishing still helped one,\nthere lived a king whose daughters were all beautiful.";

    private static ParagraphStyle Style(bool on = true, float shrink = 0) =>
        AutoHyphenTests.Style(hyphens: Hyphens.Manual) with { Align = TextAlign.Justify, JustifyLineBreaks = on, SpaceShrink = shrink };

    private static float Natural(string text, ParagraphStyle style, int start, int end) =>
        Paragraph.Create(text, style).CreateFlow().Builder.Measure(start, end);

    /// <summary>The pen x of the glyph for the character at <paramref name="index"/> on a line.</summary>
    private static float XOf(TextLine line, int index)
    {
        foreach (LayoutRun run in line.Runs)
        {
            if (run.Glyphs is GlyphRun glyphs)
            {
                for (int g = 0; g < glyphs.Count; g++)
                {
                    if (glyphs.Clusters[g] == index)
                    {
                        return glyphs.Positions[g].X;
                    }
                }
            }
        }

        throw new InvalidOperationException($"No glyph for {index}.");
    }

    [Fact]
    public void DefaultsKeepCssBehaviour()
    {
        Assert.False(ParagraphStyle.Default.JustifyLineBreaks);
        Assert.True(ParagraphStyle.Default.StretchNoBreakSpaces);
    }

    [Fact]
    public void OffLeavesTheLineBeforeABreakAtItsWidth()
    {
        TextLayout layout = Paragraph.Create(Text, Style(on: false)).Layout(400);
        TextLine first = layout.Lines[0];
        Assert.True(first.HardBreak);
        Assert.Equal(Natural(Text, Style(), 0, Text.IndexOf('\n') - 0), first.Width, 3);
        Assert.Equal(0, first.Left, 3);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    [InlineData("\u000B")]
    [InlineData("\u000C")]
    [InlineData("\u0085")]
    [InlineData("\u2028")]
    public void OnStretchesTheLineBeforeALineBreak(string brk)
    {
        string text = Text.Replace("\n", brk, StringComparison.Ordinal);
        TextLayout layout = Paragraph.Create(text, Style()).Layout(400);
        TextLine first = layout.Lines[0];
        Assert.True(first.HardBreak);
        Assert.Equal(400, first.Width, 3);
        Assert.Equal(0, first.Left, 3);

        // The paragraph's last line keeps AlignLast.
        TextLine last = layout.Lines[^1];
        Assert.True(last.Width < 399);
    }

    [Fact]
    public void ParagraphSeparatorAndTextEndKeepAlignLast()
    {
        string text = Text.Replace("\n", "\u2029", StringComparison.Ordinal);
        TextLayout layout = Paragraph.Create(text, Style()).Layout(400);
        Assert.True(layout.Lines[0].HardBreak);
        Assert.True(layout.Lines[0].Width < 399);

        // A break at the very end of the text adds no empty line: the line before it is the last line.
        const string end = "alpha beta gamma\n";
        TextLayout ended = Paragraph.Create(end, Style()).Layout(400);
        Assert.Single(ended.Lines);
        Assert.Equal(Natural(end, Style(), 0, end.Length - 1), ended.Lines[0].Width, 3);
    }

    [Fact]
    public void ShortTwoWordLineStretchesAndOneWordStaysAtTheStart()
    {
        const string text = "Dear Sir,\nThanks\nfor all.";
        TextLayout layout = Paragraph.Create(text, Style()).Layout(400);
        Assert.Equal(400, layout.Lines[0].Width, 3);
        Assert.Equal(0, XOf(layout.Lines[0], 0), 3);
        Assert.Equal(400 - Natural(text, Style(), 5, 9), XOf(layout.Lines[0], 5), 2);

        // One word: no opportunity, so it stays at the start, never letter-spaced.
        TextLine one = layout.Lines[1];
        Assert.Equal(Natural(text, Style(), 10, 16), one.Width, 3);
        Assert.Equal(0, one.Left, 3);
    }

    [Fact]
    public void TrailingSpacesBeforeTheBreakAreLeftOut()
    {
        const string text = "alpha beta gamma   \nend";
        TextLayout layout = Paragraph.Create(text, Style()).Layout(400);
        TextLine first = layout.Lines[0];
        Assert.Equal(400, first.Width, 3);

        // Two opportunities (the spaces inside), each taking half the room; the gamma ends at the width.
        float natural = Natural(text, Style(), 0, 16);
        float each = (400 - natural) / 2;
        Assert.Equal(Natural(text, Style(), 0, 11) + (2 * each), XOf(first, 11), 2);
    }

    [Fact]
    public void WithSpaceShrinkAnOverfullLineBeforeABreakShrinks()
    {
        const string text = "alpha beta gamma delta\nepsilon";
        int end = text.IndexOf('\n');
        float natural = Natural(text, Style(), 0, end);
        float spaces = 3 * Natural(text, Style(), 5, 6);
        float width = natural - (0.2f * spaces);

        TextLayout on = Paragraph.Create(text, Style(shrink: 0.25f)).Layout(width);
        Assert.Equal(end + 1, on.Lines[0].End);
        Assert.Equal(width, on.Lines[0].Width, 3);

        // Off, such a line shrinks too (as a last line does) but never stretches: the same here.
        TextLayout off = Paragraph.Create(text, Style(on: false, shrink: 0.25f)).Layout(width);
        Assert.Equal(end + 1, off.Lines[0].End);

        // On, it stretches with SpaceShrink as well.
        TextLayout wide = Paragraph.Create(text, Style(shrink: 0.25f)).Layout(natural + 30);
        Assert.Equal(natural + 30, wide.Lines[0].Width, 3);
        TextLayout narrowOff = Paragraph.Create(text, Style(on: false, shrink: 0.25f)).Layout(natural + 30);
        Assert.Equal(natural, narrowOff.Lines[0].Width, 3);
    }

    [Fact]
    public void RightToLeftLinesStretchTheSameWay()
    {
        const string text = "שלום עולם יפה\nסוף";
        ParagraphStyle style = ParagraphTests.Style("shaping/NotoSansHebrew-Regular.ttf") with
        {
            Align = TextAlign.Justify,
            JustifyLineBreaks = true,
            Direction = TextDirection.RightToLeft,
        };
        TextLayout layout = Paragraph.Create(text, style).Layout(300);
        Assert.Equal(300, layout.Lines[0].Width, 3);
        Assert.Equal(0, layout.Lines[0].Left, 3);

        // The last line keeps AlignLast: at the start, the right edge.
        TextLine last = layout.Lines[1];
        Assert.Equal(300, last.Left + last.Width, 3);
        Assert.True(last.Width < 299);

        TextLayout off = Paragraph.Create(text, style with { JustifyLineBreaks = false }).Layout(300);
        Assert.True(off.Lines[0].Width < 299);
    }

    [Fact]
    public void OptimalFillingScoresLinesBeforeABreak()
    {
        string text = Text;
        int brk = text.IndexOf('\n') + 1;
        ParagraphStyle on = Style() with { LineFill = LineFill.Optimal };
        LineBuilder builder = Paragraph.Create(text, on).CreateFlow().Builder;
        List<int> candidates = builder.CandidateIndexes(0);
        Assert.Equal(brk, candidates[^1]);
        LineBuilder.OptimalLine? line = builder.LineTable(0, 400, 400)[0, candidates.Count - 1];
        Assert.NotNull(line);
        Assert.True(line.Value.Forced);
        Assert.True(line.Value.Ratio > 0);

        LineBuilder offBuilder = Paragraph.Create(text, on with { JustifyLineBreaks = false }).CreateFlow().Builder;
        Assert.Equal(0, offBuilder.LineTable(0, 400, 400)[0, candidates.Count - 1]!.Value.Ratio);

        // U+2029 and the paragraph's end still score 0.
        LineBuilder separator = Paragraph.Create(text.Replace('\n', '\u2029'), on).CreateFlow().Builder;
        Assert.Equal(0, separator.LineTable(0, 400, 400)[0, candidates.Count - 1]!.Value.Ratio);
        List<int> tail = builder.CandidateIndexes(brk);
        Assert.Equal(0, builder.LineTable(brk, 1000, 1000)[0, tail.Count - 1]!.Value.Ratio);

        // Laid out, the line before the break is stretched.
        TextLayout layout = Paragraph.Create(text, on).Layout(400);
        Assert.Equal(400, layout.Lines[0].Width, 3);
    }

    [Fact]
    public void OptimalFillingAvoidsALooseLineBeforeABreak()
    {
        // Scored 0, the line before the break may be as short as the search likes; scored, it may not be very loose.
        string text = "aa bb cc dd ee ff gg hh ii jj kk ll mm nn oo pp\nzz";
        ParagraphStyle on = Style() with { LineFill = LineFill.Optimal };
        float width = Natural(text, on, 0, text.IndexOf(" mm", StringComparison.Ordinal));
        TextLayout layout = Paragraph.Create(text, on).Layout(width);
        foreach (TextLine line in layout.Lines)
        {
            if (line.End < text.Length)
            {
                Assert.Equal(width, line.Width, 3);
            }
        }
    }

    [Fact]
    public void BalancedBreaksAreUnchanged()
    {
        ParagraphStyle balanced = Style() with { LineFill = LineFill.Balanced };
        TextLayout on = Paragraph.Create(Text, balanced).Layout(260);
        TextLayout off = Paragraph.Create(Text, balanced with { JustifyLineBreaks = false }).Layout(260);
        Assert.Equal(off.Lines.Select(l => l.End), on.Lines.Select(l => l.End));
        TextLine beforeBreak = on.Lines.First(l => l.HardBreak);
        Assert.Equal(260, beforeBreak.Width, 3);
    }

    [Fact]
    public void GreedyBreaksAreUnchanged()
    {
        for (float width = 120; width < 500; width += 17)
        {
            TextLayout on = Paragraph.Create(Text, Style()).Layout(width);
            TextLayout off = Paragraph.Create(Text, Style(on: false)).Layout(width);
            Assert.Equal(off.Lines.Select(l => l.End), on.Lines.Select(l => l.End));
        }
    }

    [Fact]
    public void InterCharacterOpportunitiesBeforeTheLastTabAreSkipped()
    {
        const string text = "abcd\tefgh ijkl mnop qrst uvwx yzab cdef ghij klmn";
        ParagraphStyle style = Style(on: false) with { Justify = TextJustify.InterCharacter, TabStops = [new TabStop(100)] };
        TextLine first = Paragraph.Create(text, style).Layout(300).Lines[0];
        Assert.True(first.End < text.Length);
        Assert.Equal(300, first.Width, 3);
        Assert.Equal(Natural(text, style, 0, 3), XOf(first, 3), 2);
        Assert.Equal(100, XOf(first, 5), 2);
        Assert.True(XOf(first, 7) - XOf(first, 6) > Natural(text, style, 6, 7) + 0.1f);
    }

    [Fact]
    public void OnlySpacesAfterTheLastTabStretchAndTabStopsStay()
    {
        const string text = "ab cd\tef gh\tij kl mn op qr st uv wx yz";
        ParagraphStyle style = Style(on: false) with { TabStops = [new TabStop(120), new TabStop(240)] };
        TextLayout layout = Paragraph.Create(text, style).Layout(400);
        TextLine first = layout.Lines[0];
        Assert.True(first.End < text.Length);
        Assert.Equal(400, first.Width, 3);

        // Text after each tab starts on its stop; spaces before the last tab keep their width.
        int second = text.IndexOf("ef", StringComparison.Ordinal);
        int third = text.IndexOf("ij", StringComparison.Ordinal);
        Assert.Equal(120, XOf(first, second), 2);
        Assert.Equal(240, XOf(first, third), 2);
        Assert.Equal(Natural(text, style, 0, 3), XOf(first, 3), 2);
        Assert.Equal(120 + Natural(text, style, second, second + 3), XOf(first, second + 3), 2);

        // The spaces after the last tab take the room: the one after "ij" is wider than natural.
        float spaceAfter = XOf(first, third + 3) - XOf(first, third + 2);
        Assert.True(spaceAfter > Natural(text, style, third + 2, third + 3) + 1);
    }

    [Fact]
    public void ALineWhoseLastTabIsFollowedByOneWordStaysAtTheStart()
    {
        // Nothing to stretch after the last tab: the line is not justified, and its earlier spaces keep their width.
        const string text = "ab cd ef\tgh\nend";
        ParagraphStyle style = Style() with { TabStops = [new TabStop(150)] };
        TextLayout layout = Paragraph.Create(text, style).Layout(400);
        TextLine first = layout.Lines[0];
        Assert.Equal(0, first.Left, 3);
        Assert.Equal(150, XOf(first, 9), 2);
        Assert.Equal(Natural(text, style, 0, 3), XOf(first, 3), 2);
    }

    [Fact]
    public void WithSpaceShrinkOnlySpacesAfterTheLastTabShrink()
    {
        const string text = "ab cd\tef gh ij kl mn op qr st uv wx yz";
        ParagraphStyle style = Style(on: false, shrink: 1) with { TabStops = [new TabStop(100)] };
        int tab = text.IndexOf('\t');
        int end = text.IndexOf(" uv", StringComparison.Ordinal);
        float natural = Natural(text, style, 0, end);
        float space = Natural(text, style, 2, 3);
        float width = natural - (0.2f * space);
        TextLayout layout = Paragraph.Create(text, style).Layout(width);
        TextLine first = layout.Lines[0];
        Assert.Equal(end + 1, first.End);
        Assert.Equal(width, first.Width, 3);
        Assert.Equal(space, XOf(first, 3) - XOf(first, 2), 3);
        Assert.Equal(100, XOf(first, tab + 1), 2);
    }

    [Fact]
    public void NoBreakSpacesKeepTheirWidthWhenAsked()
    {
        const string text = "alpha\u00A0beta gamma delta epsilon zeta eta theta iota kappa lambda mu nu xi omicron pi";
        ParagraphStyle stretch = Style(on: false);
        ParagraphStyle keep = stretch with { StretchNoBreakSpaces = false };
        float nbsp = Natural(text, stretch, 5, 6);

        TextLine stretched = Paragraph.Create(text, stretch).Layout(300).Lines[0];
        Assert.True(XOf(stretched, 6) - XOf(stretched, 5) > nbsp + 0.5f);

        TextLine kept = Paragraph.Create(text, keep).Layout(300).Lines[0];
        Assert.Equal(300, kept.Width, 3);
        Assert.Equal(nbsp, XOf(kept, 6) - XOf(kept, 5), 3);
        Assert.True(XOf(kept, 11) - XOf(kept, 10) > Natural(text, keep, 10, 11) + 0.5f);
    }

    [Fact]
    public void NoBreakSpacesCountForNoStretchInOptimalFilling()
    {
        const string text = "alpha\u00A0beta gamma";
        ParagraphStyle on = Style(on: false) with { LineFill = LineFill.Optimal };
        float width = Natural(text, on, 0, text.Length) + 40;
        LineBuilder.OptimalLine? stretched = Paragraph.Create(text + " delta", on).CreateFlow().Builder.LineTable(0, width, width)[0, 1];
        LineBuilder.OptimalLine? kept = Paragraph.Create(text + " delta", on with { StretchNoBreakSpaces = false }).CreateFlow().Builder.LineTable(0, width, width)[0, 1];
        Assert.True(kept!.Value.Ratio > stretched!.Value.Ratio * 1.5);
    }
}
