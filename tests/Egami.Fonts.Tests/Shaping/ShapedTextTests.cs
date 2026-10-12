using System.Numerics;

using Egami.Fonts.Shaping;
using Egami.Fonts.Unicode;

namespace Egami.Fonts.Tests.Shaping;

/// <summary>TextShaper.Shape: lines, bidi order, script and span runs, and font fallback per grapheme cluster.</summary>
public sealed class ShapedTextTests
{
    private static readonly Typeface Noto = TestFonts.Load("NotoSans-Regular.ttf");

    private static readonly Typeface Hebrew = TestFonts.Load("shaping/NotoSansHebrew-Regular.ttf");

    private static readonly Typeface Arabic = TestFonts.Load("shaping/NotoSansArabic-Regular.ttf");

    private static FontFamily Family(Typeface face)
    {
        FontCollection collection = new();
        collection.Add(face);
        return collection.Families[0];
    }

    [Fact]
    public void OneRunEqualsShapeRun()
    {
        Font font = new(Noto, 20);
        ShapedText shaped = TextShaper.Shape("office AV", font);
        GlyphBuffer expected = new();
        TextShaper.ShapeRun("office AV", 0, 9, font, new RunProperties { Script = Script.Latin, Direction = TextDirection.LeftToRight }, expected);

        ShapedRun run = Assert.Single(Assert.Single(shaped.Lines).Runs);
        Assert.Equal(expected.GlyphIds.ToArray(), run.Glyphs.GlyphIds.ToArray());
        Assert.Equal(expected.Positions.ToArray(), run.Glyphs.Positions.ToArray());
        Assert.Equal(expected.Advance, shaped.Advance, 0.001f);
        Assert.Equal(Script.Latin, run.Script);
    }

    [Fact]
    public void HardBreaksSplitLines()
    {
        ShapedText shaped = TextShaper.Shape("ab\ncd\r\nef\u2029", new Font(Noto, 10));
        Assert.Equal([(0, 2), (3, 2), (7, 2), (10, 0)], shaped.Lines.Select(l => (l.Start, l.Length)));
        Assert.Empty(shaped.Lines[3].Runs);
        Assert.Single(TextShaper.Shape("", new Font(Noto, 10)).Lines);
    }

    [Fact]
    public void RightToLeftRunsAreOrderedForDisplay()
    {
        // "abc ", then Hebrew, then " def": the Hebrew run falls back to the Hebrew face and sits between the Latin runs; the space
        // after it takes the paragraph's level but stays in the Hebrew script and face, so it is a run of its own.
        string text = "abc \u05E9\u05DC\u05D5\u05DD \u05E2\u05D5\u05DC\u05DD def";
        ShapingOptions options = new() { Fallback = new FontFallback([Family(Hebrew)]) };
        ShapedLine line = Assert.Single(TextShaper.Shape(text, new Font(Noto, 10), options).Lines);
        Assert.Equal(0, line.ParagraphLevel);
        Assert.Equal(4, line.Runs.Count);
        Assert.Same(Noto, line.Runs[0].Font.Typeface);
        Assert.Same(Hebrew, line.Runs[2].Font.Typeface);
        Assert.Same(Noto, line.Runs[3].Font.Typeface);
        Assert.Same(Hebrew, line.Runs[1].Font.Typeface);
        Assert.Equal(1, line.Runs[1].BidiLevel);
        Assert.Equal(Script.Hebrew, line.Runs[1].Script);

        // The space between the Hebrew words stays in the Hebrew run, and the run's glyphs go right to left.
        Assert.Equal(4, line.Runs[1].Start);
        Assert.Equal(9, line.Runs[1].Length);
        ReadOnlySpan<int> clusters = line.Runs[1].Glyphs.Clusters;
        Assert.True(clusters[0] > clusters[^1]);
    }

    [Fact]
    public void AnArabicParagraphRunsRightToLeft()
    {
        string text = "\u0645\u0631\u062D\u0628\u0627 abc";
        ShapedLine line = Assert.Single(TextShaper.Shape(text, new Font(Arabic, 10)).Lines);
        Assert.Equal(1, line.ParagraphLevel);

        // Visual order: the Latin run (level 2) is left of the Arabic run.
        Assert.Equal(2, line.Runs.Count);
        Assert.Equal(Script.Latin, line.Runs[0].Script);
        Assert.Equal(Script.Arabic, line.Runs[1].Script);

        // Left to right: the Arabic word, the space (paragraph level, Arabic script), then the Latin word.
        ShapedLine ltr = TextShaper.Shape(text, new Font(Arabic, 10), new ShapingOptions { Direction = TextDirection.LeftToRight }).Lines[0];
        Assert.Equal([1, 0, 0], ltr.Runs.Select(r => r.BidiLevel));
    }

    [Fact]
    public void WithoutFallbackMissingCharactersStayInTheFont()
    {
        ShapedLine line = Assert.Single(TextShaper.Shape("a\u05E9b", new Font(Noto, 10)).Lines);
        Assert.All(line.Runs, r => Assert.Same(Noto, r.Font.Typeface));
        Assert.Contains(line.Runs.SelectMany(r => r.Glyphs.GlyphIds.ToArray()), g => g == 0);
    }

    [Fact]
    public void SpansSplitRunsAndCarryTheirSettings()
    {
        Font big = new(Noto, 20);
        ShapingOptions options = new()
        {
            Spans = [new TextSpan(2, 4) { Font = big, Features = [FontFeature.Ligatures(false)], Language = "de" }],
        };
        ShapedLine line = Assert.Single(TextShaper.Shape("a office b", new Font(Noto, 10), options).Lines);
        Assert.Equal([(0, 2), (2, 4), (6, 4)], line.Runs.Select(r => (r.Start, r.Length)));
        Assert.Equal(big, line.Runs[1].Font);
        Assert.Equal("de", line.Runs[1].Language);
        Assert.Equal(4, line.Runs[1].Glyphs.Count); // "offi" with ligatures off.

        Assert.Throws<ArgumentOutOfRangeException>(() => TextShaper.Shape("abc", big, new ShapingOptions { Spans = [new TextSpan(1, 5)] }));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextShaper.Shape("abcdef", big, new ShapingOptions { Spans = [new TextSpan(0, 3), new TextSpan(2, 2)] }));
    }

    [Fact]
    public void GlyphRunsFollowThePen()
    {
        Font font = new(Noto, 10);
        ShapedRun run = TextShaper.Shape("AV", font).Lines[0].Runs[0];
        GlyphRun glyphs = run.ToGlyphRun(new Vector2(5, 7), "AV");
        Assert.Equal(new Vector2(5, 7), glyphs.Positions[0]);
        Assert.Equal(5 + run.Glyphs.Positions[0].XAdvance, glyphs.Positions[1].X, 0.001f);
        Assert.Equal([0, 1], glyphs.Clusters.ToArray());
    }

    [Fact]
    public void AResolverGetsWholeEmojiSequences()
    {
        // A ZWJ family sequence is one grapheme cluster, so it falls back whole.
        Typeface color = TestFonts.Load("test_glyphs-glyf_colr_1.ttf");
        RecordingResolver resolver = new(color);
        string family = "\uD83D\uDC69\u200D\uD83D\uDC69\u200D\uD83D\uDC67";
        string text = "a" + family + "b";
        ShapedLine line = Assert.Single(TextShaper.Shape(text, new Font(Noto, 10), new ShapingOptions { Fallback = new FontFallback([], resolver) }).Lines);
        Assert.Equal([family], resolver.Clusters);
        Assert.Equal([(0, 1), (1, family.Length), (1 + family.Length, 1)], line.Runs.Select(r => (r.Start, r.Length)));
        Assert.Same(color, line.Runs[1].Font.Typeface);
    }

    private sealed class RecordingResolver(Typeface face) : IFontFallbackResolver
    {
        public List<string> Clusters { get; } = [];

        public bool TryResolve(ReadOnlySpan<char> cluster, Script script, string? language, FontStyle style, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Typeface? typeface)
        {
            Clusters.Add(cluster.ToString());
            typeface = face;
            return true;
        }
    }

    [Fact]
    public void PlainLatinShapesAs271()
    {
        // Text the font has no ligature or mark rules for: the character map and pair kerning, as 2.7.1's one-glyph step placed it.
        Font font = new(Noto, 17);
        foreach (string text in new[] { "Hello, World!", "AVATAR Toyota 1234", "Tj Wa ye" })
        {
            GlyphRun run = font.Shape(text);
            float x = 0;
            int previous = -1;
            for (int i = 0; i < text.Length; i++)
            {
                ushort glyph = Noto.GetGlyph(text[i]);
                if (previous >= 0)
                {
                    x += Noto.GetKerning((ushort)previous, glyph) * font.Scale;
                }

                Assert.Equal(glyph, run.Glyphs[i]);
                Assert.Equal(x, run.Positions[i].X, 0.01f);
                Assert.Equal(0, run.Positions[i].Y);
                x += font.GetAdvance(glyph);
                previous = glyph;
            }

            Assert.Equal(x, run.Advance, 0.01f);
        }
    }

    [Fact]
    public void FontShapeKeepsOneFont()
    {
        Font font = new(Noto, 10);
        GlyphRun run = font.Shape("\u05E9\u05DC\u05D5\u05DD abc", new ShapingOptions { Language = "he" });
        Assert.Equal(1, run.BidiLevel);
        Assert.Equal(Script.Common, run.Script);
        Assert.Equal("he", run.Language);
        Assert.Throws<ArgumentException>(() => font.Shape("a", new ShapingOptions { Fallback = new FontFallback([Family(Hebrew)]) }));
        Assert.Throws<ArgumentException>(() => font.Shape("abc", new ShapingOptions { Spans = [new TextSpan(0, 1) { Font = new Font(Hebrew, 10) }] }));
        Assert.Empty(font.Shape("\t\u0007").Glyphs.ToArray());
    }

    [Fact]
    public void VerticalTextGoesDown()
    {
        Font font = new(Noto, 10);
        GlyphRun run = font.Shape("ab\ncd", new ShapingOptions { Direction = TextDirection.TopToBottom });
        Assert.True(run.Positions[1].Y > run.Positions[0].Y);
        Assert.True(run.Positions[2].X < run.Positions[0].X);
    }

    [Fact]
    public void NullsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => TextShaper.Shape(null!, new Font(Noto, 10)));
        Assert.Throws<ArgumentNullException>(() => TextShaper.Shape("a", default));
        Assert.Throws<ArgumentNullException>(() => new FontFallback(null!));
    }
}
