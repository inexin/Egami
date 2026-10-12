using Egami.Fonts.Layout;
using Egami.Fonts.Shaping;

namespace Egami.Fonts.Tests.Layout;

/// <summary>Stage C line filling (task 5): Chromium's line breaks on the corpus, flows, and re-shaped lines.</summary>
public sealed class LineFillTests(ITestOutputHelper output)
{
    public static TheoryData<string> CorpusCases()
    {
        var data = new TheoryData<string>();
        // Chromium breaks the "thai" paragraph with its dictionary, which Egami has none of (2.8.2 question 5); "thai-words" is
        // the same text with a zero width space at each of Chromium's word boundaries.
        foreach (LayoutReferences.Case c in LayoutReferences.Load("corpus").Cases.Where(c => c.Name != "thai"))
        {
            data.Add(c.Name);
        }

        return data;
    }

    /// <summary>
    /// Widths where Chromium 129 breaks differently for a reason outside layout: its ICU applies UAX #14's rule LB19 from before
    /// Unicode 15.1, which never breaks next to a quotation mark, where Unicode 17's LB19 and LB19a break around quotation
    /// marks between Chinese characters (here before the U+201C after U+4E14 at index 69).
    /// </summary>
    internal static readonly HashSet<(string Case, float? Width)> KnownDifferences =
    [
        ("chinese", 80),
        ("justify-chinese", 150),

        // UAX #14 makes U+2028 a mandatory break (class BK); Chromium breaks lines only at CSS segment breaks (LF).
        ("hard-breaks", 300),

        // Blink's own table for ASCII line breaks never breaks after a slash before a letter, as in URLs; UAX #14 does.
        ("url", 120),
        ("url", 200),

        // Egami adds no letter spacing inside cursive scripts (2.8.2 question 11, CSS Text 3); Chromium spaces Arabic letters.
        ("letter-spacing-arabic", 200),

        // Chromium ends a line at the soft hyphen after "prehen" although "prehen=" (63.4 px with the custom hyphen) is wider
        // than the 60 px line; Egami counts the hyphen and breaks at the soft hyphen before.
        ("soft-hyphens-custom", 60),
    ];

    internal static int[] Starts(TextLayout layout) => [.. layout.Lines.Select(l => l.Start)];

    [Theory]
    [MemberData(nameof(CorpusCases))]
    public void LineBreaksEqualChromiumOnTheCorpus(string name)
    {
        LayoutReferences.Case c = LayoutReferences.Find(name);
        Paragraph paragraph = LayoutCases.Paragraph(c);
        List<string> failures = [];
        foreach (LayoutReferences.Layout reference in c.Layouts)
        {
            int[] expected = [.. reference.Lines.Select(l => l.Start)];
            int[] actual = Starts(paragraph.Layout(reference.Width ?? float.PositiveInfinity));
            if (KnownDifferences.Contains((name, reference.Width)))
            {
                Assert.False(expected.SequenceEqual(actual), $"{name} at {reference.Width} now equals Chromium: drop it from the known differences.");
                continue;
            }

            if (!expected.SequenceEqual(actual))
            {
                failures.Add($"{reference.Width}: Chromium {string.Join(',', expected)}; Egami {string.Join(',', actual)}");
            }
        }

        foreach (string f in failures)
        {
            output.WriteLine(f);
        }

        Assert.True(failures.Count == 0, $"{name}: {string.Join(" | ", failures)}");
    }

    public static TheoryData<string> FeatureCases()
    {
        // Vertical cases come with vertical lines (task 11).
        var data = new TheoryData<string>();
        foreach (LayoutReferences.Case c in LayoutReferences.Load("features").Cases)
        {
            if (!c.Style.TryGetProperty("writingMode", out _))
            {
                data.Add(c.Name);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(FeatureCases))]
    public void LineBreaksEqualChromiumOnTheFeatureCases(string name) => LineBreaksEqualChromiumOnTheCorpus(name);

    [Theory]
    [MemberData(nameof(CorpusCases))]
    public void LinePiecesEqualShapingThemFromScratch(string name)
    {
        // Every piece of every line, re-shaped where its break was unsafe or cut from the paragraph's shaping where it was
        // safe, has the glyphs of shaping the piece's text on its own in the same font, script and direction.
        LayoutReferences.Case c = LayoutReferences.Find(name);
        Paragraph paragraph = LayoutCases.Paragraph(c);
        int pieces = 0;
        foreach (LayoutReferences.Layout reference in c.Layouts)
        {
            foreach (TextLine line in paragraph.Layout(reference.Width ?? float.PositiveInfinity).Lines)
            {
                foreach (LayoutRun run in line.Runs.Where(r => r.Kind == LayoutRunKind.Text))
                {
                    ParagraphRun source = paragraph.Runs[paragraph.Measures.RunOf[run.Start]];
                    GlyphBuffer alone = new();
                    TextShaper.ShapeRun(c.Text, run.Start, run.Length, source.Font, source.Properties, alone);
                    ushort[] actual = [.. run.Glyphs!.Glyphs.ToArray()];
                    if (run.Start + run.Length == line.End && c.Text[line.End - 1] == '\u00AD')
                    {
                        // The taken soft hyphen's glyphs come after the piece's own.
                        actual = (run.BidiLevel & 1) != 0 ? actual[(actual.Length - alone.Count)..] : actual[..alone.Count];
                    }

                    if (!alone.GlyphIds.SequenceEqual(actual))
                    {
                        Assert.Fail($"{reference.Width}: piece {run.Start}+{run.Length}: alone {string.Join(",", alone.GlyphIds.ToArray())}, in line {string.Join(",", actual)}; line {Describe(line)}");
                    }

                    pieces++;
                }
            }
        }

        Assert.True(pieces > 0);
    }

    [Fact]
    public void UnsafeBreaksAreExercised()
    {
        // Some corpus line starts or ends where the paragraph's shaping is unsafe to break, so pieces are shaped again.
        int unsafeEnds = 0;
        foreach (LayoutReferences.Case c in LayoutReferences.Load("corpus").Cases)
        {
            Paragraph paragraph = LayoutCases.Paragraph(c);
            foreach (LayoutReferences.Layout reference in c.Layouts)
            {
                foreach (TextLine line in paragraph.Layout(reference.Width ?? float.PositiveInfinity).Lines)
                {
                    unsafeEnds += line.End < c.Text.Length && paragraph.Measures.Unsafe[line.End] ? 1 : 0;
                }
            }
        }

        Assert.True(unsafeEnds > 0);
    }

    private static string Describe(TextLine line) => string.Join(" / ", line.Runs.Select(r => $"{r.Kind}@{r.Start}+{r.Length} L{r.BidiLevel}: {(r.Glyphs is null ? "" : string.Join(",", r.Glyphs.Glyphs.ToArray()))}"));

    [Fact]
    public void FlowsTakeAWidthPerLine()
    {
        LayoutReferences.Case c = LayoutReferences.Find("latin");
        Paragraph paragraph = LayoutCases.Paragraph(c);

        // The same width every line equals Layout.
        LineFlow flow = paragraph.CreateFlow();
        List<int> starts = [];
        while (flow.NextLine(220) is TextLine line)
        {
            starts.Add(line.Start);
            Assert.Equal(0, line.Top);
        }

        Assert.True(flow.IsComplete);
        Assert.Null(flow.NextLine(220));
        Assert.Equal(Starts(paragraph.Layout(220)), starts);

        // Changing widths: every line fits its own width, or holds one unbreakable piece, and the lines cover the text in order.
        flow.Reset();
        float[] widths = [80, 300, 140, 500];
        int expected = 0;
        for (int i = 0; flow.NextLine(widths[i % widths.Length]) is TextLine line; i++)
        {
            Assert.Equal(expected, line.Start);
            // A line wider than its width holds one piece that cannot break.
            bool fits = line.Width <= widths[i % widths.Length] + 0.001f;
            Assert.True(fits || !paragraph.Breaks.Any(b => b.Index > line.Start && b.Index < line.End), $"line {i} is {line.Width} wide in {widths[i % widths.Length]}");
            expected = line.End;
        }

        Assert.Equal(c.Text.Length, expected);
        Assert.Throws<ArgumentOutOfRangeException>(() => paragraph.CreateFlow().NextLine(-1));
    }

    [Fact]
    public void EmptyAndUnboundedParagraphs()
    {
        ParagraphStyle style = ParagraphTests.Style("NotoSans-Regular.ttf");
        TextLayout empty = Egami.Fonts.Layout.Paragraph.Create("", style).Layout(100);
        Assert.Empty(empty.Lines);
        Assert.Equal(0, empty.Height);

        TextLayout unbounded = Egami.Fonts.Layout.Paragraph.Create("one line\nand a second, longer line", style).Layout(float.PositiveInfinity);
        Assert.Equal(2, unbounded.Lines.Count);
        Assert.True(unbounded.Lines[0].HardBreak);
        Assert.Equal(unbounded.LongestLine, unbounded.Width);
        Assert.Equal(unbounded.Lines[1].Width, unbounded.LongestLine);
    }
}
