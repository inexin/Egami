using Egami.Fonts.Layout;
using Egami.Fonts.Shaping;

namespace Egami.Fonts.Tests.Layout;

/// <summary>ParagraphStyle.SpaceShrink: justified lines that fit by shrinking their word spaces, as Word fills them.</summary>
public sealed class SpaceShrinkTests
{
    private const string Text =
        "In olden times when wishing still helped one, there lived a king whose daughters were all beautiful, but the youngest "
        + "was so beautiful that the sun itself, which has seen so much, was astonished whenever it shone in her face. Close by "
        + "the king's castle lay a great dark forest, and under an old lime tree in the forest was a well, and when the day was "
        + "very warm, the king's child went out into the forest and sat down by the side of the cool fountain.";

    private static readonly string Arabic = string.Join(' ', File.ReadAllLines(Path.Combine(TestFonts.Root, "tests", "reference", "shaping", "texts", "arabic.txt")).Skip(3).Take(3));

    private static ParagraphStyle Style(float shrink, bool hyphens = false) =>
        AutoHyphenTests.Style(hyphens: hyphens ? Hyphens.Auto : Hyphens.Manual) with { Align = TextAlign.Justify, SpaceShrink = shrink };

    private static ParagraphStyle ArabicStyle(float shrink, TextJustify justify = TextJustify.InterWord) =>
        ParagraphTests.Style("shaping/NotoSansArabic-Regular.ttf") with { Align = TextAlign.Justify, Justify = justify, SpaceShrink = shrink };

    private static float Measure(string text, ParagraphStyle style, int start, int end) =>
        Paragraph.Create(text, style).CreateFlow().Builder.Measure(start, end);

    /// <summary>The summed natural width of the line's shrinkable spaces (U+0020), its last character left out.</summary>
    private static float Spaces(Paragraph paragraph, int start, int contentEnd)
    {
        LineBuilder builder = paragraph.CreateFlow().Builder;
        float sum = 0;
        for (int i = start; i < contentEnd - 1; i++)
        {
            sum += paragraph.Text[i] == ' ' ? builder.Measure(i, i + 1) : 0;
        }

        return sum;
    }

    private static int ContentEnd(string text, TextLine line)
    {
        int end = line.End;
        while (end > line.Start && text[end - 1] is ' ' or '\n')
        {
            end--;
        }

        return end;
    }

    private static float HyphenWidth(ParagraphStyle style) =>
        Paragraph.Create("‐", style with { Align = TextAlign.Start }).Layout(float.PositiveInfinity).Lines[0].Width;

    /// <summary>Checks every line against the limit and returns how many shrank.</summary>
    private static int CheckLimit(TextLayout layout, float width, float shrink)
    {
        Paragraph paragraph = layout.Paragraph;
        LineBuilder builder = paragraph.CreateFlow().Builder;
        float hyphen = HyphenWidth(paragraph.Style);
        int shrunk = 0;
        foreach (TextLine line in layout.Lines)
        {
            int content = ContentEnd(paragraph.Text, line);
            float natural = builder.Measure(line.Start, content) + (line.AutoHyphen ? hyphen : 0);
            if (natural > width + 1e-3f && content > line.Start && paragraph.Text.AsSpan(line.Start, content - line.Start).Contains(' '))
            {
                Assert.True(natural - width <= (shrink * Spaces(paragraph, line.Start, content)) + 1e-3f, $"{natural} at {width}");
                Assert.Equal(width, line.Width, 3);
                shrunk++;
            }
        }

        return shrunk;
    }

    [Fact]
    public void ValuesOutsideZeroToOneThrow()
    {
        foreach (float bad in (float[])[-0.01f, 1.01f, float.NaN, float.PositiveInfinity, float.NegativeInfinity])
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new ParagraphStyle { SpaceShrink = bad });
        }

        foreach (float good in (float[])[0, 0.25f, 1])
        {
            Assert.Equal(good, new ParagraphStyle { SpaceShrink = good }.SpaceShrink);
        }

        Assert.Equal(0, ParagraphStyle.Default.SpaceShrink);
    }

    [Fact]
    public void ZeroKeepsEveryLineWithinTheWidth()
    {
        ParagraphStyle style = Style(0);
        for (float width = 100; width <= 320; width += 3)
        {
            TextLayout layout = Paragraph.Create(Text, style).Layout(width);
            Assert.Equal(0, CheckLimit(layout, width, 0));
        }
    }

    [Fact]
    public void ALineFitsWhenItsSpacesShrinkWithinTheShare()
    {
        // Rule A: the overflow is at most SpaceShrink times the line's word spaces.
        const string text = "alpha beta gamma delta epsilon zeta";
        ParagraphStyle style = Style(0.25f);
        int end = text.IndexOf(" epsilon", StringComparison.Ordinal);
        float natural = Measure(text, style, 0, end);
        float limit = 0.25f * Spaces(Paragraph.Create(text, style), 0, end);
        Assert.True(limit > 1);

        TextLayout fits = Paragraph.Create(text, style).Layout(natural - (0.9f * limit));
        Assert.Equal(end + 1, fits.Lines[0].End);
        Assert.Equal(natural - (0.9f * limit), fits.Lines[0].Width, 3);

        TextLayout over = Paragraph.Create(text, style).Layout(natural - (1.1f * limit));
        Assert.Equal(text.IndexOf(" delta", StringComparison.Ordinal) + 1, over.Lines[0].End);

        // Without SpaceShrink the word does not fit.
        TextLayout plain = Paragraph.Create(text, Style(0)).Layout(natural - (0.9f * limit));
        Assert.Equal(text.IndexOf(" delta", StringComparison.Ordinal) + 1, plain.Lines[0].End);
    }

    [Fact]
    public void EachSpaceShrinksAtMostHalfWhatItWouldStretch()
    {
        // Rule B: a short last word may not push the line's spaces to shrink more than half of what each would stretch had the
        // line ended before it, though the share would allow it.
        const string text = "alpha beta gamma delta a epsilon";
        ParagraphStyle style = Style(1);
        Paragraph paragraph = Paragraph.Create(text, style);
        int end = text.IndexOf(" epsilon", StringComparison.Ordinal);
        int previous = text.IndexOf(" a ", StringComparison.Ordinal);
        float natural = Measure(text, style, 0, end);
        float before = Measure(text, style, 0, previous);
        float limitA = Spaces(paragraph, 0, end);
        float limitB = (natural - before) * 4 / ((2 * 3) + 4);
        Assert.True(limitB < limitA - 1, $"{limitB} {limitA}");

        Assert.Equal(end + 1, paragraph.Layout(natural - limitB + 0.2f).Lines[0].End);
        Assert.Equal(previous + 1, paragraph.Layout(natural - limitB - 0.2f).Lines[0].End);
    }

    [Fact]
    public void ShrunkLinesStayWithinTheLimit()
    {
        foreach (float shrink in (float[])[0.1f, 0.25f, 0.5f])
        {
            int shrunk = 0;
            for (float width = 100; width <= 320; width += 1)
            {
                TextLayout layout = Paragraph.Create(Text, Style(shrink)).Layout(width);
                TextLayout plain = Paragraph.Create(Text, Style(0)).Layout(width);
                Assert.True(layout.Lines.Count <= plain.Lines.Count);
                shrunk += CheckLimit(layout, width, shrink);
            }

            Assert.True(shrunk > 20, $"Only {shrunk} shrunk lines at {shrink}.");
        }
    }

    [Fact]
    public void SpacesShrinkEvenlyAndGlyphsKeepTheirWidths()
    {
        const string text = "alpha beta gamma delta epsilon zeta";
        ParagraphStyle style = Style(0.25f);
        Paragraph paragraph = Paragraph.Create(text, style);
        int end = text.IndexOf(" epsilon", StringComparison.Ordinal);
        float natural = Measure(text, style, 0, end);
        float space = Measure(text, style, 5, 6);
        float over = 2;
        TextLine line = paragraph.Layout(natural - over).Lines[0];
        Assert.Equal(end + 1, line.End);

        List<(int Cluster, float Advance)> advances = Advances(line);
        foreach ((int cluster, float advance) in advances.Where(a => a.Cluster < end))
        {
            float expected = text[cluster] == ' ' ? space - (over / 3) : Measure(text, style, cluster, cluster + 1);
            Assert.Equal(expected, advance, 3);
        }
    }

    [Fact]
    public void NoBreakSpacesNeitherCountNorShrink()
    {
        const string spaces = "alpha beta gamma delta epsilon zeta";
        string nbsp = "alpha beta gamma delta epsilon zeta";
        ParagraphStyle style = Style(1);
        int end = spaces.IndexOf(" epsilon", StringComparison.Ordinal);
        float natural = Measure(spaces, style, 0, end);
        float space = Measure(spaces, style, 5, 6);
        Assert.Equal(natural, Measure(nbsp, style, 0, end), 3);

        // Three spaces may shrink by 2.5 spaces in all; one space and two no-break spaces only by one space.
        float width = natural - (2 * space);
        Assert.Equal(end + 1, Paragraph.Create(spaces, style).Layout(width).Lines[0].End);
        Assert.Equal(nbsp.IndexOf(" delta", StringComparison.Ordinal) + 1, Paragraph.Create(nbsp, style).Layout(width).Lines[0].End);

        // When it fits, the no-break spaces keep their width and the one space takes all the shrink.
        TextLine line = Paragraph.Create(nbsp, style).Layout(natural - 2).Lines[0];
        Assert.Equal(end + 1, line.End);
        foreach ((int cluster, float advance) in Advances(line).Where(a => a.Cluster < end))
        {
            float expected = nbsp[cluster] == ' ' ? space - 2 : Measure(nbsp, style, cluster, cluster + 1);
            Assert.Equal(expected, advance, 3);
        }
    }

    [Fact]
    public void TheLastLineShrinksToFitButNeverStretches()
    {
        const string text = "alpha beta gamma delta";
        ParagraphStyle style = Style(0.25f);
        float natural = Measure(text, style, 0, text.Length);
        float limit = 0.25f * Spaces(Paragraph.Create(text, style), 0, text.Length);

        TextLayout shrunk = Paragraph.Create(text, style).Layout(natural - (0.5f * limit));
        Assert.Single(shrunk.Lines);
        Assert.Equal(natural - (0.5f * limit), shrunk.Lines[0].Width, 3);

        TextLayout loose = Paragraph.Create(text, style).Layout(natural + 20);
        Assert.Single(loose.Lines);
        Assert.Equal(natural, loose.Lines[0].Width, 3);
        Assert.Equal(0, loose.Lines[0].Left, 3);

        Assert.Equal(2, Paragraph.Create(text, Style(0)).Layout(natural - (0.5f * limit)).Lines.Count);
    }

    [Fact]
    public void ALineBeforeAHardBreakShrinksToFit()
    {
        const string text = "alpha beta gamma delta\nepsilon zeta eta theta iota kappa lambda mu";
        ParagraphStyle style = Style(0.25f);
        int end = text.IndexOf('\n');
        float natural = Measure(text, style, 0, end);
        float limit = 0.25f * Spaces(Paragraph.Create(text, style), 0, end);

        TextLine line = Paragraph.Create(text, style).Layout(natural - (0.5f * limit)).Lines[0];
        Assert.True(line.HardBreak);
        Assert.Equal(end + 1, line.End);
        Assert.Equal(natural - (0.5f * limit), line.Width, 3);

        // AlignLast Start without Align Justify: no line justifies, so none shrinks.
        TextLayout ragged = Paragraph.Create(text, style with { Align = TextAlign.Start }).Layout(natural - (0.5f * limit));
        Assert.Equal(text.IndexOf(" delta", StringComparison.Ordinal) + 1, ragged.Lines[0].End);

        // AlignLast Justify alone justifies, and so shrinks, the line before the hard break.
        TextLayout last = Paragraph.Create(text, style with { Align = TextAlign.Start, AlignLast = TextAlignLast.Justify }).Layout(natural - (0.5f * limit));
        Assert.Equal(end + 1, last.Lines[0].End);
        Assert.Equal(natural - (0.5f * limit), last.Lines[0].Width, 3);
    }

    [Fact]
    public void JustifyNoneNeverShrinks()
    {
        const string text = "alpha beta gamma delta epsilon zeta";
        ParagraphStyle style = Style(1) with { Justify = TextJustify.None };
        int end = text.IndexOf(" epsilon", StringComparison.Ordinal);
        float natural = Measure(text, style, 0, end);
        Assert.Equal(text.IndexOf(" delta", StringComparison.Ordinal) + 1, Paragraph.Create(text, style).Layout(natural - 1).Lines[0].End);
    }

    [Fact]
    public void TheWholeWordIsKeptByShrinkingBeforeHyphenation()
    {
        const string text = "alpha beta gamma consideration and more words to follow";
        ParagraphStyle style = Style(0.25f, hyphens: true);
        int end = text.IndexOf(" and", StringComparison.Ordinal);
        float natural = Measure(text, style, 0, end);
        float limit = 0.25f * Spaces(Paragraph.Create(text, style), 0, end);
        float width = natural - (0.5f * limit);

        TextLine line = Paragraph.Create(text, style).Layout(width).Lines[0];
        Assert.Equal(end + 1, line.End);
        Assert.False(line.AutoHyphen);

        TextLine hyphenated = Paragraph.Create(text, style with { SpaceShrink = 0 }).Layout(width).Lines[0];
        Assert.True(hyphenated.AutoHyphen);
    }

    [Fact]
    public void AHyphenatedPieceFitsByShrinkingAThirdOfWhatItAdds()
    {
        // Between two hyphenation points of one word the spaces stay the same, so rule B lets the line shrink by a third of the
        // piece the later point adds.
        const string text = "alpha beta gamma consideration and more words to follow";
        ParagraphStyle style = Style(1, hyphens: true);
        Paragraph paragraph = Paragraph.Create(text, style);
        float hyphen = HyphenWidth(style);
        int word = text.IndexOf("consideration", StringComparison.Ordinal);
        List<int> points = [.. paragraph.CreateFlow().Builder.CandidateEnds(0).Where(e => e.AutoHyphen && e.End > word).Select(e => e.End)];
        Assert.True(points.Count >= 2, string.Join(",", points));

        int first = points[0], second = points[1];
        float natural = Measure(text, style, 0, second) + hyphen;
        float third = (Measure(text, style, 0, second) - Measure(text, style, 0, first)) / 3;
        Assert.True(third < Spaces(paragraph, 0, second) - 1);

        TextLine fits = paragraph.Layout(natural - third + 0.2f).Lines[0];
        Assert.True(fits.AutoHyphen);
        Assert.Equal(second, fits.End);
        Assert.Equal(natural - third + 0.2f, fits.Width, 3);

        TextLine earlier = paragraph.Layout(natural - third - 0.2f).Lines[0];
        Assert.Equal(first, earlier.End);
    }

    [Fact]
    public void RightToLeftLinesShrinkAndFillTheWidth()
    {
        int shrunk = 0;
        for (float width = 200; width <= 320; width += 2)
        {
            TextLayout layout = Paragraph.Create(Arabic, ArabicStyle(0.25f)).Layout(width);
            shrunk += CheckLimit(layout, width, 0.25f);
            foreach (TextLine line in layout.Lines.SkipLast(1))
            {
                Assert.Equal(0, line.Left, 3);
                Assert.Equal(width, line.Width, 3);
            }
        }

        Assert.True(shrunk > 5, $"Only {shrunk} shrunk lines.");
    }

    [Fact]
    public void KashidaLinesThatShrinkGetNoTatweels()
    {
        int shrunk = 0;
        int stretched = 0;
        for (float width = 200; width <= 320; width += 2)
        {
            TextLayout layout = Paragraph.Create(Arabic, ArabicStyle(0.25f, TextJustify.Kashida)).Layout(width);
            LineBuilder builder = layout.Paragraph.CreateFlow().Builder;
            foreach (TextLine line in layout.Lines.SkipLast(1))
            {
                float natural = builder.Measure(line.Start, ContentEnd(Arabic, line));
                int tatweels = Tatweels(line, Arabic);
                if (natural > width + 1e-3f)
                {
                    Assert.Equal(0, tatweels);
                    shrunk++;
                }
                else
                {
                    stretched += tatweels > 0 ? 1 : 0;
                }
            }

            CheckLimit(layout, width, 0.25f);
        }

        Assert.True(shrunk > 5, $"Only {shrunk} shrunk lines.");
        Assert.True(stretched > 5, $"Only {stretched} lines with tatweels.");
    }

    [Fact]
    public void OptimalFillingUsesSpaceShrinkAsItsLimit()
    {
        ParagraphStyle optimal = Style(0.25f) with { LineFill = LineFill.Optimal };
        ParagraphStyle tighter = Style(0.1f) with { LineFill = LineFill.Optimal, OptimalLines = new OptimalLineOptions { ShrinkSpaces = true } };
        int shrunk = 0, tight = 0;
        for (float width = 120; width <= 300; width += 4)
        {
            shrunk += CheckLimit(Paragraph.Create(Text, optimal).Layout(width), width, 0.25f);

            // SpaceShrink, not ShrinkSpaces' third, limits the shrink.
            tight += CheckLimit(Paragraph.Create(Text, tighter).Layout(width), width, 0.1f);
        }

        Assert.True(shrunk > 5, $"Only {shrunk} shrunk lines.");
        Assert.True(tight > 0, "No line shrank at 0.1.");
    }

    /// <summary>Each cluster's advance on a left-to-right line, from its glyph positions.</summary>
    private static List<(int Cluster, float Advance)> Advances(TextLine line)
    {
        List<(int, float)> advances = [];
        foreach (LayoutRun run in line.Runs)
        {
            if (run.Glyphs is not GlyphRun glyphs)
            {
                continue;
            }

            for (int g = 0; g < glyphs.Count; g++)
            {
                float next = g + 1 < glyphs.Count ? glyphs.Positions[g + 1].X : run.X + run.Advance;
                advances.Add((glyphs.Clusters[g], next - glyphs.Positions[g].X));
            }
        }

        return advances;
    }

    private static int Tatweels(TextLine line, string text)
    {
        int count = 0;
        foreach (LayoutRun run in line.Runs)
        {
            if (run.Glyphs is not GlyphRun glyphs)
            {
                continue;
            }

            ushort tatweel = glyphs.Font.Typeface.GetGlyph('ـ');
            for (int g = 0; g < glyphs.Count; g++)
            {
                count += glyphs.Glyphs[g] == tatweel && text[glyphs.Clusters[g]] != 'ـ' ? 1 : 0;
            }
        }

        return count;
    }
}
