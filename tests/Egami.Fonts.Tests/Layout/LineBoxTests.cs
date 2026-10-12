using Egami.Fonts.Layout;

namespace Egami.Fonts.Tests.Layout;

/// <summary>Stage C line boxes and spacing (task 6): line boxes and glyph positions against Chromium.</summary>
public sealed class LineBoxTests(ITestOutputHelper output)
{
    private const float Tolerance = 0.01f;

    /// <summary>Chromium's layout unit, 1/64 pixel.</summary>
    private const float Unit = 1f / 64;

    public static TheoryData<string> HorizontalCases()
    {
        var data = new TheoryData<string>();
        foreach (LayoutReferences.Case c in LayoutReferences.AllCases())
        {
            // Vertical lines (task 11), line limits (task 8) and alignment other than the start (task 7) have their own gates.
            if (!c.Style.TryGetProperty("writingMode", out _) && !c.Style.TryGetProperty("maxLines", out _) && !c.Style.TryGetProperty("align", out _)
                && c.Name != "thai")
            {
                data.Add(c.Name);
            }
        }

        return data;
    }

    /// <summary>
    /// The left and right x of each cluster's glyphs (or box), by the cluster's first UTF-16 index, and how many inline items
    /// (runs, and spans inside them) lie left of it on its line: Chromium snaps each item's width up to its 1/64 pixel layout
    /// unit, so its positions may run ahead of Egami's float ones by up to a unit per item.
    /// </summary>
    internal static Dictionary<int, (float Left, float Right, int Items)> Clusters(TextLayout layout)
    {
        Dictionary<int, (float, float, int)> result = [];
        RichText text = layout.Paragraph.RichText;
        foreach (TextLine line in layout.Lines)
        {
            // A right-to-left line is placed from the right edge by its snapped width, so every item on it counts.
            bool rtl = (layout.Paragraph.Bidi.ParagraphLevelAt(line.Start) & 1) != 0;
            int items = rtl ? line.Runs.Count + (2 * text.Spans.Count(s => s.Start > line.Start && s.Start < line.End)) + 1 : 0;
            foreach (LayoutRun run in line.Runs)
            {
                if (run.Glyphs is not GlyphRun glyphs)
                {
                    result[run.Start] = (run.X, run.X + run.Advance, items);
                    items++;
                    continue;
                }

                float pen = run.X;
                ReadOnlySpan<int> clusters = glyphs.Clusters;
                for (int g = 0; g < glyphs.Count; g++)
                {
                    float advance = glyphs.AdvanceAt(g);
                    int c = clusters[g];
                    int spans = text.Spans.Count(s => s.Start > run.Start && s.Start <= c) + text.Spans.Count(s => s.End > run.Start && s.End <= c);
                    result[c] = result.TryGetValue(c, out (float L, float R, int I) v) ? (Math.Min(v.L, pen), Math.Max(v.R, pen + advance), v.I) : (pen, pen + advance, items + spans);
                    pen += advance;
                }

                items += 1 + text.Spans.Count(s => s.Start > run.Start && s.Start < run.Start + run.Length) * 2;
            }
        }

        return result;
    }

    [Theory]
    [MemberData(nameof(HorizontalCases))]
    public void LineBoxesAndGlyphPositionsEqualChromium(string name)
    {
        LayoutReferences.Case c = LayoutReferences.Find(name);
        Paragraph paragraph = LayoutCases.Paragraph(c);
        List<string> failures = [];
        foreach (LayoutReferences.Layout reference in c.Layouts)
        {
            if (LineFillTests.KnownDifferences.Contains((name, reference.Width)))
            {
                continue;
            }

            TextLayout layout = paragraph.Layout(reference.Width ?? float.PositiveInfinity);
            if (layout.Lines.Count != reference.Lines.Count)
            {
                continue;
            }

            for (int i = 0; i < layout.Lines.Count; i++)
            {
                if (reference.Lines[i].Box is not float[] box)
                {
                    continue;
                }

                TextLine line = layout.Lines[i];
                if (Math.Abs(line.Top - box[0]) > Tolerance || Math.Abs(line.Baseline - box[1]) > Tolerance || Math.Abs(line.Bottom - box[2]) > Tolerance)
                {
                    failures.Add($"{reference.Width} line {i}: Chromium {box[0]}/{box[1]}/{box[2]}, Egami {line.Top}/{line.Baseline}/{line.Bottom}");
                }
            }

            Dictionary<int, (float Left, float Right, int Items)> clusters = Clusters(layout);
            foreach (LayoutReferences.Grapheme g in reference.Graphemes)
            {
                if (g.Rect is not float[] rect || !clusters.TryGetValue(g.Index, out (float Left, float Right, int Items) ours))
                {
                    continue;
                }

                // Chromium reports rectangles floored to its layout unit, after snapping each item before this one up to it; a
                // space or soft hyphen that ends a line is an item of its own there.
                int items = ours.Items + (c.Text[g.Index] is ' ' or '\u00AD' ? 1 : 0);
                // Right-to-left lines are placed from the right, so the snapping moves positions the other way.
                float low = rect[0] - (items * Unit) - Tolerance;
                float high = rect[0] + ((paragraph.Bidi.ParagraphLevelAt(g.Index) & 1) != 0 ? items + 1 : 1) * Unit + Tolerance;
                if ((paragraph.Bidi.ParagraphLevelAt(g.Index) & 1) != 0)
                {
                    low = rect[0] - Unit - Tolerance;
                }
                if (ours.Left < low || ours.Left > high)
                {
                    failures.Add($"{reference.Width} grapheme {g.Index} U+{(int)c.Text[g.Index]:X4}: Chromium x {rect[0]}, Egami {ours.Left}");
                }
            }
        }

        foreach (string f in failures.Take(40))
        {
            output.WriteLine(f);
        }

        Assert.True(failures.Count == 0, $"{name}: {failures.Count} differences, first: {string.Join(" | ", failures.Take(4))}");
    }
    private static readonly ParagraphStyle Sans = ParagraphTests.Style("NotoSans-Regular.ttf");

    [Fact]
    public void LineHeightsSplitTheirLeadingAsBrowsers()
    {
        // Noto Sans at 16 units: ascent 17 and descent 5 once rounded, as browsers keep them.
        TextLayout normal = Paragraph.Create("Line", Sans).Layout(200);
        Assert.Equal((17f, 5f), (normal.Lines[0].Ascent, normal.Lines[0].Descent));

        // 1.5 times 16 is 24: 2 units of leading, half above.
        TextLayout spaced = Paragraph.Create("Line", Sans with { DefaultStyle = Sans.DefaultStyle with { LineHeight = LineHeight.Multiple(1.5f) } }).Layout(200);
        Assert.Equal((18f, 6f), (spaced.Lines[0].Ascent, spaced.Lines[0].Descent));

        // A tight 0.8: 12.8 floored to the 1/64 unit, the negative half-leading above rounded down.
        TextLayout tight = Paragraph.Create("Line", Sans with { DefaultStyle = Sans.DefaultStyle with { LineHeight = LineHeight.Multiple(0.8f) } }).Layout(200);
        Assert.Equal(12f, tight.Lines[0].Ascent);
        Assert.Equal(12.796875f, tight.Lines[0].Height);
    }

    [Fact]
    public void StrutsSetAMinimumOrAFixedLineBox()
    {
        RichText text = new("small BIG", [new StyleSpan(6, 3, new TextStyle { Size = 40 })]);
        Strut strut = new() { Size = 16, LineHeight = LineHeight.Absolute(30) };
        TextLayout minimum = Paragraph.Create(text, Sans with { Strut = strut }).Layout(500);
        Assert.True(minimum.Lines[0].Height > 30);

        TextLayout forced = Paragraph.Create(text, Sans with { Strut = strut with { Force = true } }).Layout(500);
        Assert.Equal(30, forced.Lines[0].Height);
        Assert.Equal(21, forced.Lines[0].Ascent);

        TextLayout plain = Paragraph.Create("small", Sans with { Strut = strut }).Layout(500);
        Assert.Equal(30, plain.Lines[0].Height);
    }

    [Fact]
    public void TrimEdgesDropsTheOuterLeading()
    {
        ParagraphStyle style = Sans with { DefaultStyle = Sans.DefaultStyle with { LineHeight = LineHeight.Absolute(40) }, TrimEdges = true };
        TextLayout layout = Paragraph.Create("one" + "\n" + "two" + "\n" + "three", style).Layout(500);
        Assert.Equal(3, layout.Lines.Count);
        Assert.Equal(17, layout.Lines[0].Ascent);
        Assert.Equal(40, layout.Lines[1].Height);
        Assert.Equal(5, layout.Lines[2].Descent);
        // Three 40-unit lines less the 9 units of half-leading above the first and below the last.
        Assert.Equal((3 * 40) - 9 - 9, layout.Height, 3);
    }

    [Fact]
    public void TabStopsAlignTheTextAfterThem()
    {
        ParagraphStyle style = Sans with { TabStops = [new TabStop(100), new TabStop(200, TabAlignment.End), new TabStop(300, TabAlignment.Center), new TabStop(400, TabAlignment.Decimal)] };
        string text = "a" + "\t" + "start" + "\t" + "end" + "\t" + "mid" + "\t" + "12.50";
        TextLayout layout = Paragraph.Create(text, style).Layout(1000);
        Dictionary<int, (float Left, float Right, int Items)> clusters = Clusters(layout);
        Assert.Equal(100, clusters[text.IndexOf("start", StringComparison.Ordinal)].Left, 3);
        Assert.Equal(200, clusters[text.IndexOf("end", StringComparison.Ordinal) + 2].Right, 3);
        float mid = (clusters[text.IndexOf("mid", StringComparison.Ordinal)].Left + clusters[text.IndexOf("mid", StringComparison.Ordinal) + 2].Right) / 2;
        Assert.Equal(300, mid, 3);
        Assert.Equal(400, clusters[text.IndexOf('.', StringComparison.Ordinal)].Left, 3);
    }

    [Fact]
    public void SpacingSkipsCursiveScriptsAndWordSpacingHitsSeparators()
    {
        ParagraphStyle style = ParagraphTests.Style("NotoSans-Regular.ttf", "shaping/NotoSansArabic-Regular.ttf");
        string arabic = "\u0633\u0644\u0627\u0645";
        float plain = Paragraph.Create(arabic, style).Layout(float.PositiveInfinity).LongestLine;
        RichText spaced = new(arabic, [new StyleSpan(0, 4, new TextStyle { LetterSpacing = 5 })]);
        Assert.Equal(plain, Paragraph.Create(spaced, style).Layout(float.PositiveInfinity).LongestLine, 3);

        float latin = Paragraph.Create("a b c", style).Layout(float.PositiveInfinity).LongestLine;
        RichText words = new("a b c", [new StyleSpan(0, 5, new TextStyle { WordSpacing = 3 })]);
        Assert.Equal(latin + 6, Paragraph.Create(words, style).Layout(float.PositiveInfinity).LongestLine, 3);
        RichText letters = new("a b c", [new StyleSpan(0, 5, new TextStyle { LetterSpacing = 1 })]);
        Assert.Equal(latin + 5, Paragraph.Create(letters, style).Layout(float.PositiveInfinity).LongestLine, 3);
    }
}
