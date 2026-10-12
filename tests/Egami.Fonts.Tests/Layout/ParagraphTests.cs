using Egami.Fonts.Layout;
using Egami.Fonts.Shaping;
using Egami.Fonts.Tests.Shaping;
using Egami.Pixels;

namespace Egami.Fonts.Tests.Layout;

/// <summary>Stage B: rich text, style resolution, itemization and shaping once (task 4).</summary>
public sealed class ParagraphTests
{
    private static readonly Dictionary<string, FontFamily> s_families = [];

    internal static FontFamily Family(string file)
    {
        lock (s_families)
        {
            if (!s_families.TryGetValue(file, out FontFamily? family))
            {
                FontCollection collection = new();
                collection.AddFile(Path.Combine(TestFonts.Root, "tests", "fonts", file));
                family = collection.Families[0];
                s_families[file] = family;
            }

            return family;
        }
    }

    internal static ParagraphStyle Style(params string[] files) =>
        new() { DefaultStyle = new TextStyle { FontFamilies = [.. files.Select(Family)] } };

    [Fact]
    public void BuilderOutputEqualsHandBuiltSpans()
    {
        TextStyle bold = new() { FontStyle = FontStyle.Bold };
        TextStyle red = new() { Color = new Rgba8(255, 0, 0, 255) };
        RichText built = new RichTextBuilder()
            .Append("plain ")
            .Push(bold).Append("bold ")
            .Push(red).Append("both").AddPlaceholder(new Placeholder(10, 12)).Pop()
            .Append(" bold").Pop()
            .Append(" plain")
            .ToRichText();

        TextStyle both = red.InheritFrom(bold);
        RichText hand = new(
            "plain bold both\uFFFC bold plain",
            [new StyleSpan(6, 5, bold), new StyleSpan(11, 5, both), new StyleSpan(16, 5, bold)],
            [new Placeholder(10, 12)]);

        Assert.Equal(hand.Text, built.Text);
        Assert.Equal(hand.Placeholders, built.Placeholders);
        Assert.Equal(hand.Spans.Count, built.Spans.Count);
        for (int i = 0; i < hand.Spans.Count; i++)
        {
            Assert.Equal(hand.Spans[i].Start, built.Spans[i].Start);
            Assert.Equal(hand.Spans[i].Length, built.Spans[i].Length);
            Assert.Equal(hand.Spans[i].Style, built.Spans[i].Style);
        }

        // The inner span inherits the outer one's weight and adds its color.
        Assert.Equal(FontStyle.Bold, built.Spans[1].Style.FontStyle);
        Assert.Equal(new Rgba8(255, 0, 0, 255), built.Spans[1].Style.Color);
        Assert.Throws<InvalidOperationException>(() => new RichTextBuilder().Pop());
        Assert.Throws<ArgumentException>(() => new RichTextBuilder().Append("a\uFFFCb"));
    }

    [Fact]
    public void RichTextRejectsBadSpansAndPlaceholders()
    {
        TextStyle s = new();
        Assert.Throws<ArgumentException>(() => new RichText("abc", [new StyleSpan(0, 2, s), new StyleSpan(1, 2, s)]));
        Assert.Throws<ArgumentException>(() => new RichText("abc", [new StyleSpan(2, 2, s)]));
        Assert.Throws<ArgumentException>(() => new RichText("abc", [new StyleSpan(1, 0, s)]));
        Assert.Throws<ArgumentException>(() => new RichText("a\uFFFC", null, []));
        Assert.Throws<ArgumentException>(() => new RichText("a", null, [new Placeholder(1, 1)]));
        Assert.Throws<ArgumentException>(() => new RichText("\uFFFC", null, [new Placeholder(-1, 1)]));
    }

    [Fact]
    public void PaintOnlyChangesNeverSplitAShapingRun()
    {
        ParagraphStyle style = Style("SourceSerif4-Regular.otf");
        Paragraph plain = Paragraph.Create("office affords", style);
        RichText colored = new("office affords",
        [
            new StyleSpan(2, 3, new TextStyle { Color = new Rgba8(200, 0, 0, 255), Background = new Rgba8(0, 0, 255, 255) }),
            new StyleSpan(8, 4, new TextStyle { Decoration = new TextDecoration(), Shadows = [new TextShadow(new(1, 1), 2, default)] }),
        ]);
        Paragraph painted = Paragraph.Create(colored, style);

        Assert.Single(plain.Runs);
        Assert.Single(painted.Runs);
        Assert.Equal(plain.Runs[0].Glyphs!.GlyphIds.ToArray(), painted.Runs[0].Glyphs!.GlyphIds.ToArray());

        // The ffi ligature survives the color change inside it.
        Assert.True(painted.Runs[0].Glyphs!.Count < "office affords".Length);
    }

    [Fact]
    public void ShapingFieldsSplitRuns()
    {
        ParagraphStyle style = Style("SourceSerif4-Regular.otf");
        RichText sized = new("office office", [new StyleSpan(7, 6, new TextStyle { Size = 24 })]);
        Paragraph paragraph = Paragraph.Create(sized, style);
        Assert.Equal(2, paragraph.Runs.Length);
        Assert.Equal(24, paragraph.Runs[1].Font.Size);

        // Letter spacing turns optional ligatures off.
        RichText spaced = new("office", [new StyleSpan(0, 6, new TextStyle { LetterSpacing = 2 })]);
        Assert.Equal(6, Paragraph.Create(spaced, style).Runs[0].Glyphs!.Count);
    }

    [Fact]
    public void FallbackChoosesTheFamilyThatMapsEachCluster()
    {
        Paragraph paragraph = Paragraph.Create("peace \u0633\u0644\u0627\u0645 now", Style("NotoSans-Regular.ttf", "shaping/NotoSansArabic-Regular.ttf"));
        // The space after the Arabic word is at the paragraph's level, so it is a run of its own.
        Assert.Equal([0, 6, 10, 11], paragraph.Runs.Select(r => r.Start));
        Assert.Equal("Noto Sans Arabic", paragraph.Runs[1].Font.Typeface.FamilyName);
        Assert.Equal(1, paragraph.Runs[1].Level);
        Assert.Equal(0, paragraph.Runs[2].Level);
        Assert.Equal("Noto Sans", paragraph.Runs[3].Font.Typeface.FamilyName);
    }

    [Fact]
    public void BreaksPlaceholdersAndControlsAreRunsOfTheirOwn()
    {
        RichText text = new("a\tb\nc\uFFFCd", null, [new Placeholder(8, 8)]);
        Paragraph paragraph = Paragraph.Create(text, Style("NotoSans-Regular.ttf"));
        Assert.Equal(
            [RunKind.Text, RunKind.Tab, RunKind.Text, RunKind.Control, RunKind.Text, RunKind.Placeholder, RunKind.Text],
            paragraph.Runs.Select(r => r.Kind));
        Assert.Equal(0, paragraph.Runs[5].Placeholder);
        Assert.Equal([2, 4, 5, 6, 7], paragraph.Breaks.Select(b => b.Index));
    }

    [Fact]
    public void FontFamiliesAreRequired()
    {
        Assert.Throws<ArgumentException>(() => Paragraph.Create("text", new ParagraphStyle()));
        Assert.Empty(Paragraph.Create("", new ParagraphStyle()).Runs);
    }

    [Theory]
    [InlineData("english", "NotoSans-Regular.ttf")]
    [InlineData("arabic", "shaping/NotoSansArabic-Regular.ttf")]
    [InlineData("hebrew", "shaping/NotoSansHebrew-Regular.ttf")]
    [InlineData("hindi", "shaping/NotoSansDevanagari-Regular.ttf")]
    [InlineData("thai", "shaping/NotoSansThai-Regular.ttf")]
    public void SingleStyleTextShapesAsTheShaperDoes(string text, string font)
    {
        Typeface face = Typeface.FromFile(Path.Combine(TestFonts.Root, "tests", "fonts", font));
        ParagraphStyle style = new() { DefaultStyle = new TextStyle { FontFamilies = [Family(font)], Size = 20 } };
        foreach (string line in File.ReadAllLines(Path.Combine(ShapingReferences.Folder, "texts", text + ".txt")).Take(12))
        {
            Paragraph paragraph = Paragraph.Create(line, style);
            ShapedText shaped = TextShaper.Shape(line, new Font(face, 20));
            ShapedRun[] expected = [.. shaped.Lines.SelectMany(l => l.Runs).OrderBy(r => r.Start)];
            ParagraphRun[] actual = [.. paragraph.Runs.Where(r => r.Kind == RunKind.Text)];

            // Run boundaries may differ (the shaper's single-run fast path), so glyphs are compared in logical order.
            Assert.Equal(Glyphs(expected.Select(r => r.Glyphs)), Glyphs(actual.Select(r => r.Glyphs!)));
        }
    }

    /// <summary>Glyph ids, clusters and positions of each run in logical (cluster) order, runs in logical order.</summary>
    private static List<(ushort, int, GlyphPosition)> Glyphs(IEnumerable<GlyphBuffer> runs)
    {
        List<(ushort, int, GlyphPosition)> all = [];
        foreach (GlyphBuffer buffer in runs)
        {
            List<(ushort, int, GlyphPosition)> run = [];
            for (int i = 0; i < buffer.Count; i++)
            {
                run.Add((buffer.GlyphIds[i], buffer.Clusters[i], buffer.Positions[i]));
            }

            all.AddRange(run.OrderBy(g => g.Item2));
        }

        return all;
    }
}
