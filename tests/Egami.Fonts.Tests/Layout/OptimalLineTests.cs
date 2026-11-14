using Egami.Fonts.Layout;

namespace Egami.Fonts.Tests.Layout;

/// <summary>Hyphenation Build Plan tasks 5 and 6: LineFill.Optimal (Knuth-Plass) and LineFlow.FollowingWidth.</summary>
public sealed class OptimalLineTests
{
    private const string Text =
        "In olden times when wishing still helped one, there lived a king whose daughters were all beautiful, but the youngest "
        + "was so beautiful that the sun itself, which has seen so much, was astonished whenever it shone in her face. Close by "
        + "the king's castle lay a great dark forest, and under an old lime tree in the forest was a well, and when the day was "
        + "very warm, the king's child went out into the forest and sat down by the side of the cool fountain.";

    private static readonly string[] Words =
    [
        "a", "an", "the", "of", "to", "in", "and", "or", "is", "was", "it", "on", "by", "we", "they", "should", "would", "could",
        "paragraph", "typography", "justified", "hyphenation", "dictionary", "between", "experience", "considerable",
        "information", "remarkably", "consequently", "breaking", "lines", "beautiful", "algorithm", "software", "practice",
        "optimal", "spacing", "rivers", "conspicuous", "whitespace", "international", "conventions", "unbelievably",
        "complicated", "vocabulary", "printing", "letters", "words", "measure", "column", "page", "book", "reader",
    ];

    private static ParagraphStyle Style(TextAlign align = TextAlign.Justify, bool hyphens = false, OptimalLineOptions? options = null, HyphenationOptions? hyphenation = null)
    {
        ParagraphStyle style = AutoHyphenTests.Style(hyphens: hyphens ? Hyphens.Auto : Hyphens.Manual, options: hyphenation);
        return style with { Align = align, LineFill = LineFill.Optimal, OptimalLines = options };
    }

    private static List<OptimalBreak> Ends(TextLayout layout) =>
        [.. layout.Lines.Select(l => new OptimalBreak(l.Start + l.Length, l.AutoHyphen))];

    private static double Cost(Paragraph paragraph, IReadOnlyList<OptimalBreak> ends, float width, double tolerance = double.PositiveInfinity, bool overfullFree = false) =>
        paragraph.CreateFlow().Builder.TotalDemerits(0, ends, width, width, tolerance, overfullFree);

    private static string Corpus(Random random, int words)
    {
        List<string> picked = [];
        for (int i = 0; i < words; i++)
        {
            picked.Add(Words[random.Next(Words.Length)]);
        }

        return string.Join(' ', picked) + ".";
    }

    /// <summary>
    /// The reference: a plain dynamic program over every pair of consecutive line ends, so every way to break the paragraph is
    /// weighed (a line's demerits depend only on it and the line before), with none of the search's pruning. It returns the
    /// least total demerits at the first tolerance (200, 2000, any) that some set of lines meets, as the passes go.
    /// </summary>
    private static double BruteForce(Paragraph paragraph, float width, OptimalLineOptions options)
    {
        LineBuilder builder = paragraph.CreateFlow().Builder;
        LineBuilder.OptimalLine?[,] table = builder.LineTable(0, width, width);
        int n = table.GetLength(1);
        foreach (double tolerance in (double[])[options.Tolerance, options.SecondTolerance, double.PositiveInfinity])
        {
            // best[i + 1, j]: the least total of lines ending with the line from i (-1 for the start) to j.
            double[,] best = new double[n + 1, n];
            for (int i = 0; i <= n; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    best[i, j] = double.PositiveInfinity;
                }
            }

            bool Fits(LineBuilder.OptimalLine? line) => line is { } l && l.Badness <= tolerance;
            for (int j = 0; j < n; j++)
            {
                if (Fits(table[0, j]))
                {
                    best[0, j] = builder.LineDemerits(null, table[0, j]!.Value);
                }
            }

            for (int j = 0; j < n - 1; j++)
            {
                for (int i = -1; i < j; i++)
                {
                    if (double.IsPositiveInfinity(best[i + 1, j]))
                    {
                        continue;
                    }

                    for (int k = j + 1; k < n; k++)
                    {
                        if (Fits(table[j + 1, k]))
                        {
                            double total = best[i + 1, j] + builder.LineDemerits(table[i + 1, j], table[j + 1, k]!.Value);
                            best[j + 1, k] = Math.Min(best[j + 1, k], total);
                        }
                    }
                }
            }

            double least = double.PositiveInfinity;
            for (int i = 0; i <= n; i++)
            {
                least = Math.Min(least, best[i, n - 1]);
            }

            if (!double.IsPositiveInfinity(least))
            {
                return least;
            }
        }

        return double.PositiveInfinity;
    }

    [Theory]
    [InlineData(TextAlign.Justify, false)]
    [InlineData(TextAlign.Justify, true)]
    [InlineData(TextAlign.Start, false)]
    [InlineData(TextAlign.Start, true)]
    public void MatchesBruteForceOnShortParagraphs(TextAlign align, bool hyphens)
    {
        Random random = new(5);
        int compared = 0;
        for (int i = 0; i < 12; i++)
        {
            string text = Corpus(random, 9 + (i % 5));
            foreach (float width in (float[])[110, 150, 190])
            {
                ParagraphStyle style = Style(align, hyphens);
                Paragraph paragraph = Paragraph.Create(text, style);
                double expected = BruteForce(paragraph, width, OptimalLineOptions.Default);
                if (double.IsPositiveInfinity(expected))
                {
                    continue;
                }

                List<OptimalBreak> ends = Ends(paragraph.Layout(width));
                double actual = Cost(paragraph, ends, width);
                Assert.True(Math.Abs(expected - actual) <= 1e-9 * expected, $"Paragraph {i} at {width}: {actual}, reference {expected}.");
                compared++;
            }
        }

        Assert.True(compared > 20, $"Only {compared} paragraphs compared.");
    }

    [Theory]
    [InlineData(TextAlign.Justify, false, 120)]
    [InlineData(TextAlign.Justify, true, 120)]
    [InlineData(TextAlign.Start, false, 120)]
    [InlineData(TextAlign.Start, true, 120)]
    public void NeverCostsMoreThanGreedy(TextAlign align, bool hyphens, int paragraphs) => CompareWithGreedy(align, hyphens, paragraphs);

    [Theory]
    [Trait("Category", "Slow")]
    [InlineData(TextAlign.Justify, false)]
    [InlineData(TextAlign.Justify, true)]
    [InlineData(TextAlign.Start, false)]
    [InlineData(TextAlign.Start, true)]
    public void NeverCostsMoreThanGreedyOn500Paragraphs(TextAlign align, bool hyphens) => CompareWithGreedy(align, hyphens, 500);

    private static void CompareWithGreedy(TextAlign align, bool hyphens, int paragraphs)
    {
        Random random = new(paragraphs + (int)align + (hyphens ? 1000 : 0));
        int better = 0;
        for (int i = 0; i < paragraphs; i++)
        {
            string text = Corpus(random, 20 + random.Next(80));
            float width = 90 + random.Next(300);
            ParagraphStyle style = Style(align, hyphens);
            Paragraph paragraph = Paragraph.Create(text, style);
            List<OptimalBreak> greedy = Ends(Paragraph.Create(text, style with { LineFill = LineFill.Greedy }).Layout(width));
            List<OptimalBreak> optimal = Ends(paragraph.Layout(width));
            double greedyCost = Cost(paragraph, greedy, width, overfullFree: true);
            double optimalCost = Cost(paragraph, optimal, width, overfullFree: true);
            Assert.True(optimalCost <= greedyCost + 1e-6, $"Paragraph {i} at {width}: optimal {optimalCost} > greedy {greedyCost}.");
            better += optimalCost < greedyCost - 1e-6 ? 1 : 0;
        }

        Assert.True(better > paragraphs / 4, $"Optimal beat greedy on only {better} of {paragraphs} paragraphs.");
    }

    [Fact]
    public void JustifiedLinesFitAndAreEvenerThanGreedy()
    {
        ParagraphStyle style = Style();
        TextLayout optimal = Paragraph.Create(Text, style).Layout(160);
        TextLayout greedy = Paragraph.Create(Text, style with { LineFill = LineFill.Greedy }).Layout(160);
        Assert.All(optimal.Lines, l => Assert.True(l.Width <= 160 + 1e-3f));
        Assert.Equal(Text, string.Concat(optimal.Lines.Select(l => Text.Substring(l.Start, l.Length))));
        double optimalCost = Cost(optimal.Paragraph, Ends(optimal), 160, overfullFree: true);
        double greedyCost = Cost(optimal.Paragraph, Ends(greedy), 160, overfullFree: true);
        Assert.True(optimalCost < greedyCost, $"{optimalCost} vs {greedyCost}");
    }

    [Fact]
    public void AutomaticHyphensBecomeCandidates()
    {
        string text = "Extraordinarily conspicuous international hyphenation conventions considerably complicate typography.";
        TextLayout layout = Paragraph.Create(text, Style(hyphens: true)).Layout(100);
        Assert.Contains(layout.Lines, l => l.AutoHyphen);
        TextLayout without = Paragraph.Create(text, Style()).Layout(100);
        Assert.DoesNotContain(without.Lines, l => l.AutoHyphen);
    }

    [Fact]
    public void ConsecutiveHyphenLimitHolds()
    {
        string text = string.Join(' ', Enumerable.Repeat("hyphenation international considerable conventions", 6));
        HyphenationOptions limit = new() { Dictionaries = AutoHyphenTests.Dictionaries(), MaxConsecutiveLines = 1 };
        foreach (float width in (float[])[70, 90, 110])
        {
            TextLayout layout = Paragraph.Create(text, Style(hyphens: true, hyphenation: limit)).Layout(width);
            for (int i = 1; i < layout.Lines.Count; i++)
            {
                Assert.False(layout.Lines[i - 1].AutoHyphen && layout.Lines[i].AutoHyphen, $"Lines {i - 1} and {i} at {width}.");
            }
        }
    }

    [Fact]
    public void HyphenPenaltyAndDemeritsSteerTheChoice()
    {
        string text = "Extraordinarily conspicuous international hyphenation conventions considerably complicate typography.";
        int Hyphens(OptimalLineOptions? options) => ((float[])[90, 100, 110, 120, 130]).Sum(
            width => Paragraph.Create(text, Style(hyphens: true, options: options)).Layout(width).Lines.Count(l => l.AutoHyphen));
        int cheap = Hyphens(new OptimalLineOptions { HyphenPenalty = 0 });
        int usual = Hyphens(null);
        int costly = Hyphens(new OptimalLineOptions { HyphenPenalty = 10000, DoubleHyphenDemerits = 100000 });
        Assert.True(cheap >= usual && usual >= costly && cheap > costly, $"{cheap}, {usual}, {costly}");
    }

    [Fact]
    public void ShrinkingSpacesFitsMoreAndStaysInTheWidth()
    {
        OptimalLineOptions shrink = new() { ShrinkSpaces = true };
        bool shrunk = false;
        foreach (float width in (float[])[120, 140, 160, 180, 200])
        {
            TextLayout plain = Paragraph.Create(Text, Style()).Layout(width);
            TextLayout tight = Paragraph.Create(Text, Style(options: shrink)).Layout(width);
            Assert.True(tight.Lines.Count <= plain.Lines.Count);
            foreach (TextLine line in tight.Lines)
            {
                Assert.True(line.Width <= width + 1e-3f, $"{line.Width} at {width}");
                float natural = tight.Paragraph.CreateFlow().Builder.Measure(line.Start, line.Start + line.Length);
                shrunk |= natural > width + 1;
            }
        }

        Assert.True(shrunk, "No line shrank its spaces.");
    }

    [Fact]
    public void PlanningFromAnAutoHyphenFindsOnlyTheWholeWordsPoints()
    {
        // A flow plans again from wherever it is, which can be an automatic hyphen inside a word: the rest of that word must
        // keep the points the whole word has, not be hyphenated as a word of its own.
        Paragraph paragraph = Paragraph.Create(string.Join(' ', Words) + ".", Style(hyphens: true));
        LineBuilder builder = paragraph.CreateFlow().Builder;
        List<int> all = builder.CandidateIndexes(0);
        int checkedPoints = 0;
        foreach (int start in all.Where(i => i < paragraph.Text.Length && char.IsLetter(paragraph.Text[i - 1]) && char.IsLetter(paragraph.Text[i])))
        {
            Assert.Equal(all.Where(i => i > start), builder.CandidateIndexes(start));
            checkedPoints++;
        }

        Assert.True(checkedPoints > 10, $"{checkedPoints} points");
    }

    [Fact]
    public void ShrinkingSpacesNeverOverfillsALastLineThatIsNotJustified()
    {
        // Lines before a mandatory break and the paragraph's last line align to the start (AlignLast Auto), so they cannot
        // shrink and must fit as they are.
        OptimalLineOptions shrink = new() { ShrinkSpaces = true };
        string text = Text + "\nA short line ends here, before the second part.\n" + Text;
        foreach (TextAlignLast last in (TextAlignLast[])[TextAlignLast.Auto, TextAlignLast.Justify])
        {
            ParagraphStyle style = Style(options: shrink) with { AlignLast = last };
            for (float width = 100; width <= 320; width += 1)
            {
                TextLayout layout = Paragraph.Create(text, style).Layout(width);
                Assert.All(layout.Lines, l => Assert.True(l.Width <= width + 1e-3f, $"{l.Width} at {width}, AlignLast {last}"));
            }
        }
    }

    [Fact]
    public void ShrunkLinesSurviveOverflowWrap()
    {
        // With OverflowWrap set, a planned line too wide for its box falls back to greedy filling, but one whose spaces shrink
        // to fit is not too wide.
        OptimalLineOptions shrink = new() { ShrinkSpaces = true };
        ParagraphStyle style = Style(options: shrink);
        ParagraphStyle wrapping = style with { LineBreaking = style.LineBreaking with { OverflowWrap = OverflowWrap.BreakWord } };
        foreach (float width in (float[])[120, 140, 160, 180, 200])
        {
            TextLayout plain = Paragraph.Create(Text, style).Layout(width);
            TextLayout wrapped = Paragraph.Create(Text, wrapping).Layout(width);
            Assert.Equal(Ends(plain), Ends(wrapped));
        }
    }

    [Fact]
    public void RaggedLinesLeaveEvenerRoomThanGreedy()
    {
        ParagraphStyle style = Style(TextAlign.Start);
        int evener = 0;
        foreach (float width in (float[])[110, 130, 150, 170, 190, 210])
        {
            double Spread(TextLayout layout) => layout.Lines.SkipLast(1).Sum(l => (width - l.Width) * (width - l.Width));
            TextLayout optimal = Paragraph.Create(Text, style).Layout(width);
            TextLayout greedy = Paragraph.Create(Text, style with { LineFill = LineFill.Greedy }).Layout(width);
            Assert.All(optimal.Lines, l => Assert.True(l.Width <= width + 1e-3f));
            Assert.True(optimal.Lines.Count <= greedy.Lines.Count + 1);
            evener += Spread(optimal) < Spread(greedy) - 1e-3 ? 1 : 0;
        }

        Assert.True(evener >= 3, $"Evener at only {evener} of 6 widths.");
    }

    [Fact]
    public void MandatoryBreaksSplitTheParagraph()
    {
        string text = "The first part has a few words in it.\nThe second part has some more words, and ends here.";
        TextLayout layout = Paragraph.Create(text, Style()).Layout(120);
        int newline = text.IndexOf('\n') + 1;
        Assert.Contains(layout.Lines, l => l.Start + l.Length == newline && l.HardBreak);
        Assert.Equal(text.Length, layout.Lines[^1].Start + layout.Lines[^1].Length);
    }

    [Fact]
    public void AWordWiderThanTheLineGetsALineOfItsOwn()
    {
        string text = "a b Pneumonoultramicroscopicsilicovolcanoconiosis c d";
        TextLayout optimal = Paragraph.Create(text, Style()).Layout(60);
        TextLayout greedy = Paragraph.Create(text, Style() with { LineFill = LineFill.Greedy }).Layout(60);
        Assert.Equal(greedy.Lines.Select(l => (l.Start, l.Length)), optimal.Lines.Select(l => (l.Start, l.Length)));
    }

    [Fact]
    public void MaxLinesStillCuts()
    {
        TextLayout layout = Paragraph.Create(Text, Style() with { MaxLines = 3 }).Layout(150);
        Assert.Equal(3, layout.Lines.Count);
    }

    [Fact]
    public void FirstLineWidthAndFollowingWidthPlanTogether()
    {
        Paragraph paragraph = Paragraph.Create(Text, Style());
        LineFlow flow = paragraph.CreateFlow();
        flow.FollowingWidth = 160;
        List<OptimalBreak> ends = [];
        for (float width = 130; flow.NextLine(width) is TextLine line; width = 160)
        {
            Assert.True(line.Width <= width + 1e-3f);
            ends.Add(new OptimalBreak(line.Start + line.Length, line.AutoHyphen));
        }

        Assert.Equal(paragraph.CreateFlow().Builder.OptimalEnds(0, 130, 160), ends);
    }

    [Fact]
    public void TwoColumnsWithRewindMatchOneLayout()
    {
        Paragraph paragraph = Paragraph.Create(Text, Style());
        TextLayout whole = paragraph.Layout(150);
        LineFlow flow = paragraph.CreateFlow();
        List<TextLine> lines = [];
        float height = 0;
        while (flow.NextLine(150) is TextLine line)
        {
            if (height + line.Height > 60 && height > 0)
            {
                // The column is full: the line goes back to start the next column, at the same width.
                flow.Rewind(line.Start);
                height = 0;
                lines.Add(flow.NextLine(150)!);
                height += lines[^1].Height;
                continue;
            }

            height += line.Height;
            lines.Add(line);
        }

        Assert.Equal(whole.Lines.Select(l => (l.Start, l.Length)), lines.Select(l => (l.Start, l.Length)));
    }

    [Fact]
    public void ANarrowerSecondColumnPlansAgain()
    {
        Paragraph paragraph = Paragraph.Create(Text, Style());
        LineFlow flow = paragraph.CreateFlow();
        for (int i = 0; i < 4; i++)
        {
            flow.NextLine(180);
        }

        int at = flow.Position;
        List<OptimalBreak> rest = [];
        while (flow.NextLine(120) is TextLine line)
        {
            Assert.True(line.Width <= 120 + 1e-3f);
            rest.Add(new OptimalBreak(line.Start + line.Length, line.AutoHyphen));
        }

        Assert.Equal(paragraph.CreateFlow().Builder.OptimalEnds(at, 120, 120), rest);
    }

    [Fact]
    public void LinesBesideAFloatThenFullWidth()
    {
        // A float takes 60 units beside the first three lines: those plan at 100, expecting 100 for the next ones too, and the
        // fourth line, at the full 160, plans the rest again.
        Paragraph paragraph = Paragraph.Create(Text, Style());
        LineFlow flow = paragraph.CreateFlow();
        flow.FollowingWidth = 100;
        List<OptimalBreak> beside = [];
        for (int i = 0; i < 3; i++)
        {
            TextLine line = flow.NextLine(100)!;
            Assert.True(line.Width <= 100 + 1e-3f);
            beside.Add(new OptimalBreak(line.Start + line.Length, line.AutoHyphen));
        }

        Assert.Equal(paragraph.CreateFlow().Builder.OptimalEnds(0, 100, 100).Take(3), beside);
        flow.FollowingWidth = null;
        int at = flow.Position;
        List<OptimalBreak> below = [];
        while (flow.NextLine(160) is TextLine line)
        {
            Assert.True(line.Width <= 160 + 1e-3f);
            below.Add(new OptimalBreak(line.Start + line.Length, line.AutoHyphen));
        }

        Assert.Equal(paragraph.CreateFlow().Builder.OptimalEnds(at, 160, 160), below);
    }

    [Fact]
    public void InfiniteWidthFillsGreedily()
    {
        TextLayout layout = Paragraph.Create(Text, Style()).Layout(float.PositiveInfinity);
        Assert.Single(layout.Lines);
    }

    [Fact]
    public void OptionsAreChecked()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OptimalLineOptions { LinePenalty = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new OptimalLineOptions { HyphenPenalty = 10001 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new OptimalLineOptions { Tolerance = -5 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new OptimalLineOptions { DoubleHyphenDemerits = -1 });
        LineFlow flow = Paragraph.Create(Text, Style()).CreateFlow();
        Assert.Throws<ArgumentOutOfRangeException>(() => flow.FollowingWidth = float.NaN);
        Assert.Throws<ArgumentOutOfRangeException>(() => flow.FollowingWidth = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => flow.FollowingWidth = float.PositiveInfinity);
        Assert.Equal(200, new OptimalLineOptions().Tolerance);
    }
}
