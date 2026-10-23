using System.Numerics;

using Egami.Fonts.Layout;
using Egami.Fonts.Shaping;
using Egami.Fonts.Unicode;

namespace Egami.Fonts.Tests.Layout;

/// <summary>2.8.3 kashida justification: justified Arabic lines lengthen words with tatweels where the shaping allows.</summary>
public sealed class KashidaTests
{
    private const float Width = 300;

    private static readonly string Arabic = string.Join(' ', File.ReadAllLines(Path.Combine(TestFonts.Root, "tests", "reference", "shaping", "texts", "arabic.txt")).Skip(3).Take(3));

    private static ParagraphStyle Style(string font = "shaping/NotoSansArabic-Regular.ttf", KashidaLevel level = KashidaLevel.Medium) =>
        ParagraphTests.Style(font) with { Align = TextAlign.Justify, Justify = TextJustify.Kashida, Kashida = level };

    private static ushort TatweelGlyph(TextLayout layout) => layout.Lines[0].Runs.First(r => r.Glyphs is not null).Glyphs!.Font.Typeface.GetGlyph(Kashida.Tatweel);

    /// <summary>The tatweels layout put in (glyphs of U+0640 whose cluster's character is not one), by the cluster they join.</summary>
    private static Dictionary<int, int> Inserted(TextLayout layout)
    {
        ushort tatweel = TatweelGlyph(layout);
        string text = layout.Paragraph.Text;
        Dictionary<int, int> inserted = [];
        foreach (LayoutRun run in layout.Lines.SelectMany(l => l.Runs))
        {
            if (run.Glyphs is not GlyphRun glyphs)
            {
                continue;
            }

            for (int g = 0; g < glyphs.Count; g++)
            {
                int c = glyphs.Clusters[g];
                if (glyphs.Glyphs[g] == tatweel && text[c] != 'ـ')
                {
                    inserted[c] = inserted.GetValueOrDefault(c) + 1;
                }
            }
        }

        return inserted;
    }

    [Fact]
    public void JustifiedLinesFillTheWidthWithTatweels()
    {
        TextLayout layout = Paragraph.Create(Arabic, Style()).Layout(Width);
        Assert.True(layout.Lines.Count > 4);
        foreach (TextLine line in layout.Lines.SkipLast(1))
        {
            Assert.Equal(Width, line.Width, 3);

            // The runs' advances add up to the line, tatweels included.
            float sum = line.Runs.Where(r => r.Start < line.End).Sum(r => r.Advance);
            Assert.True(sum >= Width - 0.01f, $"{sum}");
        }

        Dictionary<int, int> inserted = Inserted(layout);
        Assert.NotEmpty(inserted);
        Assert.All(inserted.Values, count => Assert.InRange(count, 1, Kashida.Cap(KashidaLevel.Medium) + 1));

        // The last line is not justified, so it gets none.
        TextLine last = layout.Lines[^1];
        Assert.DoesNotContain(inserted.Keys, c => c >= last.Start);
    }

    [Fact]
    public void ShapingWithTheTatweelsInTheTextGivesTheSameGlyphs()
    {
        // Each tatweel sits where the shaping said one is safe: shaping the text with U+0640 there gives the same glyphs.
        Paragraph paragraph = Paragraph.Create(Arabic, Style());
        TextLayout layout = paragraph.Layout(Width);
        Dictionary<int, int> inserted = Inserted(layout);
        Font font = layout.Lines[0].Runs.First(r => r.Glyphs is not null).Glyphs!.Font;
        foreach (TextLine line in layout.Lines)
        {
            foreach (LayoutRun run in line.Runs.Where(r => r.Glyphs is not null && r.Length > 0))
            {
                // The run's text with the tatweels after the clusters they join, shaped in the paragraph's context.
                System.Text.StringBuilder builder = new(Arabic[..run.Start]);
                int start = builder.Length;
                for (int i = run.Start; i < run.Start + run.Length; i++)
                {
                    builder.Append(Arabic[i]);
                    bool clusterEnds = i + 1 == run.Start + run.Length || run.Glyphs!.Clusters.IndexOf(i + 1) >= 0;
                    if (clusterEnds)
                    {
                        int cluster = ClusterOf(run.Glyphs!, i);
                        builder.Append('ـ', inserted.GetValueOrDefault(cluster));
                    }
                }

                int length = builder.Length - start;
                builder.Append(Arabic.AsSpan(run.Start + run.Length));
                GlyphBuffer reshaped = new();
                RunProperties properties = new() { Direction = (run.BidiLevel & 1) != 0 ? TextDirection.RightToLeft : TextDirection.LeftToRight };
                TextShaper.ShapeRun(builder.ToString(), start, length, font, properties, reshaped);
                Assert.Equal(reshaped.GlyphIds.ToArray(), run.Glyphs!.Glyphs.ToArray());
            }
        }
    }

    private static int ClusterOf(GlyphRun glyphs, int index)
    {
        int best = -1;
        foreach (int c in glyphs.Clusters)
        {
            if (c <= index && c > best)
            {
                best = c;
            }
        }

        return best;
    }

    [Fact]
    public void HigherLevelsLengthenWordsMore()
    {
        int low = Inserted(Paragraph.Create(Arabic, Style(level: KashidaLevel.Low)).Layout(Width)).Values.Sum();
        int medium = Inserted(Paragraph.Create(Arabic, Style(level: KashidaLevel.Medium)).Layout(Width)).Values.Sum();
        int high = Inserted(Paragraph.Create(Arabic, Style(level: KashidaLevel.High)).Layout(Width)).Values.Sum();
        Assert.True(low < medium && medium <= high, $"{low} {medium} {high}");
    }

    [Fact]
    public void LinesBreakAsWithoutKashida()
    {
        // Kashida only finishes lines: they break where inter-word justification breaks them.
        TextLayout kashida = Paragraph.Create(Arabic, Style()).Layout(Width);
        TextLayout words = Paragraph.Create(Arabic, Style() with { Justify = TextJustify.InterWord }).Layout(Width);
        Assert.Equal(LineFillTests.Starts(words), LineFillTests.Starts(kashida));
    }

    [Fact]
    public void OtherScriptsAndFontsWithoutATatweelJustifyAtSpaces()
    {
        const string Latin = "Kashida only lengthens words in scripts that join with a tatweel; Latin text justifies at its spaces.";
        TextLayout kashida = Paragraph.Create(Latin, Style("NotoSans-Regular.ttf")).Layout(Width);
        TextLayout words = Paragraph.Create(Latin, Style("NotoSans-Regular.ttf") with { Justify = TextJustify.InterWord }).Layout(Width);
        Assert.Equal(Positions(words), Positions(kashida));

        // Noto Sans has no Arabic and no tatweel: its Arabic justifies at spaces too.
        TextLayout noTatweel = Paragraph.Create(Arabic, Style("NotoSans-Regular.ttf")).Layout(Width);
        TextLayout noTatweelWords = Paragraph.Create(Arabic, Style("NotoSans-Regular.ttf") with { Justify = TextJustify.InterWord }).Layout(Width);
        Assert.Equal(Positions(noTatweelWords), Positions(noTatweel));
    }

    private static Vector2[] Positions(TextLayout layout) =>
        [.. layout.Lines.SelectMany(l => l.Runs).Where(r => r.Glyphs is not null).SelectMany(r => r.Glyphs!.Positions.ToArray())];

    [Fact]
    public void VerticalLinesGetNoTatweels()
    {
        TextLayout layout = Paragraph.Create(Arabic, Style() with { WritingMode = WritingMode.VerticalRl }).Layout(Width);
        Assert.Empty(Inserted(layout));
    }

    [Fact]
    public void CaretsAndHitsStayOnCharacterBoundaries()
    {
        TextLayout layout = Paragraph.Create(Arabic, Style()).Layout(Width);
        Dictionary<int, int> inserted = Inserted(layout);
        TextLine line = layout.Lines[0];
        List<float> xs = [];
        for (int i = line.Start; i <= line.End; i = i == line.End ? i + 1 : TextSegmenter.NextGraphemeBoundary(Arabic, i))
        {
            CaretLine caret = layout.GetCaret(new TextPosition(i)).Primary;
            Assert.InRange(caret.X, line.Left - 0.01f, line.Left + line.Width + 0.01f);
            xs.Add(caret.X);
            if (i < line.End && i > line.Start)
            {
                // A hit just inside the caret on the side of the next character finds the same boundary.
                TextPosition hit = layout.HitTest(new Vector2(caret.X - 0.5f, line.Baseline));
                Assert.InRange(hit.Index, line.Start, line.End);
            }
        }

        // Right to left: carets move left as the index grows, and a cluster with tatweels is wider than without them.
        Assert.True(xs.Zip(xs.Skip(1)).Take(xs.Count - 2).All(p => p.Second <= p.First + 0.01f));
        int lengthened = inserted.Keys.First(c => c < line.End);
        TextLayout plain = Paragraph.Create(Arabic, Style() with { Justify = TextJustify.InterWord }).Layout(Width);
        float Wide(TextLayout l) => l.GetSelectionRects(lengthened, TextSegmenter.NextGraphemeBoundary(Arabic, lengthened)).Sum(r => r.Right - r.Left);
        Assert.True(Wide(layout) > Wide(plain) + 1);

        // The selection of the line's content spans its width.
        IReadOnlyList<TextBounds> rects = layout.GetSelectionRects(line.Start, line.End - 1);
        Assert.Equal(line.Left, rects.Min(r => r.Left), 1);
        Assert.Equal(line.Left + line.Width, rects.Max(r => r.Right), 1);
    }
}

/// <summary>2.8.3 task 2: the kashida point priorities, one case per rule.</summary>
public sealed class KashidaPriorityTests
{
    [Theory]
    [InlineData("كـتب", 1, 1)] // after a tatweel already in the text
    [InlineData("سلم", 0, 2)] // after Seen
    [InlineData("صبر", 0, 2)] // after Sad
    [InlineData("كله", 1, 3)] // before a final Heh
    [InlineData("كلة", 1, 3)] // before a final Teh Marbuta
    [InlineData("بد", 0, 3)] // before a final Dal
    [InlineData("كتا", 1, 4)] // before a final Alef
    [InlineData("مثل", 1, 4)] // before a final Lam
    [InlineData("بر", 0, 5)] // Beh before a final Reh
    [InlineData("بي", 0, 5)] // Beh before a final Yeh
    [InlineData("كتو", 1, 6)] // before a final Waw
    [InlineData("مع", 0, 6)] // before a final Ain
    [InlineData("كم", 0, 7)] // before another final letter
    [InlineData("كتب", 0, 8)] // before a medial letter
    [InlineData("كمَ", 0, 7)] // a mark after the letter does not stop it being final
    public void PointsRankAsArabicJustificationExpects(string word, int before, int priority) =>
        Assert.Equal(priority, Kashida.Priority(word, before, before + 1));

    [Fact]
    public void TheScriptsThatJoinWithATatweelTakeKashida()
    {
        Assert.True(Kashida.Joins(Script.Arabic));
        Assert.True(Kashida.Joins(Script.Syriac));
        Assert.False(Kashida.Joins(Script.Nko));
        Assert.False(Kashida.Joins(Script.Mongolian));
        Assert.False(Kashida.Joins(Script.Latin));
        Assert.Equal([1, 2, 4], [Kashida.Cap(KashidaLevel.Low), Kashida.Cap(KashidaLevel.Medium), Kashida.Cap(KashidaLevel.High)]);
    }
}
