using Egami.Fonts.Layout;

namespace Egami.Fonts.Tests.Layout;

/// <summary>Hyphenation Build Plan task 4: hyphens: auto in greedy line filling, with every limit.</summary>
public sealed class AutoHyphenTests
{
    private const string English =
        "Typographers traditionally hyphenate justified paragraphs, because otherwise extraordinarily long words "
        + "leave conspicuous rivers of whitespace. Automatic hyphenation considers international conventions, "
        + "dictionaries and exceptions, generating acceptable breakpoints in unbelievably complicated vocabulary.";

    private const float Width = 150;

    private static readonly float[] Widths = [90, 110, 130, 150];

    private static readonly Lazy<Hyphenator> s_english = new(() =>
    {
        using FileStream stream = File.OpenRead(Path.Combine(TestFonts.Root, "tests", "hyphenation", "hyph-en-us.tex"));
        return Hyphenator.Load(stream);
    });

    internal static HyphenationDictionaries Dictionaries(string language = "en")
    {
        HyphenationDictionaries dictionaries = new();
        dictionaries.Add(language, s_english.Value);
        return dictionaries;
    }

    internal static ParagraphStyle Style(string? language = "en-US", HyphenationOptions? options = null, Hyphens hyphens = Hyphens.Auto)
    {
        ParagraphStyle style = ParagraphTests.Style("NotoSans-Regular.ttf");
        return style with
        {
            DefaultStyle = style.DefaultStyle with { Size = 16, Language = language },
            Hyphens = hyphens,
            Hyphenation = options ?? new HyphenationOptions { Dictionaries = Dictionaries() },
        };
    }

    private static TextLayout Lay(ParagraphStyle style, string text = English, float width = Width) => Paragraph.Create(text, style).Layout(width);

    /// <summary>The lines that end inside a word: a letter on each side of the break.</summary>
    private static List<TextLine> Hyphenated(TextLayout layout)
    {
        string text = layout.Paragraph.Text;
        return [.. layout.Lines.Where(l => l.Start + l.Length is int end && end < text.Length && char.IsLetter(text[end - 1]) && char.IsLetter(text[end]))];
    }

    /// <summary>The word around a break and the break's offset in it.</summary>
    private static (string Word, int Offset) WordAt(string text, int index)
    {
        int start = index;
        while (start > 0 && char.IsLetter(text[start - 1]))
        {
            start--;
        }

        int end = index;
        while (end < text.Length && char.IsLetter(text[end]))
        {
            end++;
        }

        return (text[start..end], index - start);
    }

    private static int[] Points(string word, int minBefore = 2, int minAfter = 3)
    {
        int[] points = new int[word.Length];
        int count = s_english.Value.GetPoints(word, points, minBefore, minAfter);
        return points[..count];
    }

    [Fact]
    public void WordsCrossingTheEndBreakAtTheirPointsWithAHyphen()
    {
        TextLayout layout = Lay(Style());
        List<TextLine> hyphenated = Hyphenated(layout);
        Assert.NotEmpty(hyphenated);
        string text = layout.Paragraph.Text;
        foreach (TextLine line in hyphenated)
        {
            (string word, int offset) = WordAt(text, line.Start + line.Length);
            Assert.Contains(offset, Points(word));
            Assert.True(line.Width <= Width + 1e-3f, $"{line.Width} > {Width}");

            // The line's last glyph is the hyphen.
            Assert.True(line.AutoHyphen);
            LayoutRun last = line.Runs.Last(r => r.Glyphs is not null);
            Assert.Equal(1, layout.Paragraph.HyphenGlyphCount(last.Glyphs!.Font));
            Assert.Equal(last.Glyphs!.Font.Typeface.GetGlyph(0x2010), last.Glyphs.Glyphs[^1]);
        }

        // Every character still has its glyphs on its own line.
        foreach (TextLine line in layout.Lines)
        {
            foreach (LayoutRun run in line.Runs.Where(r => r.Glyphs is not null))
            {
                Assert.All(run.Glyphs!.Clusters.ToArray(), c => Assert.InRange(c, line.Start, line.Start + line.Length - 1));
            }
        }

        Assert.DoesNotContain(layout.Lines.Except(hyphenated), l => l.AutoHyphen);

        // Greedy filling with hyphens never takes more lines than without them.
        Assert.True(layout.Lines.Count <= Lay(Style(hyphens: Hyphens.Manual)).Lines.Count);
    }

    [Fact]
    public void ManualAndMissingDictionariesDoNotHyphenate()
    {
        Assert.Empty(Hyphenated(Lay(Style(hyphens: Hyphens.Manual))));
        Assert.Empty(Hyphenated(Lay(Style(language: null))));
        Assert.Empty(Hyphenated(Lay(Style(language: "de"))));
        Assert.Empty(Hyphenated(Lay(Style() with { Hyphenation = null })));

        // A language with a dictionary hyphenates even in a span of other text.
        RichText mixed = new RichTextBuilder().Push(new TextStyle { Language = "de" }).Append(English).Pop().ToRichText();
        Assert.Empty(Hyphenated(Paragraph.Create(mixed, Style()).Layout(Width)));
    }

    [Fact]
    public void LanguagesFallBackASubtagAtATime()
    {
        HyphenationDictionaries dictionaries = Dictionaries("de");
        Assert.True(dictionaries.TryGet("de-CH-1901", out Hyphenator? found));
        Assert.Same(s_english.Value, found);
        Assert.True(dictionaries.TryGet("DE_ch", out _));
        Assert.False(dictionaries.TryGet("en", out _));
        Assert.False(dictionaries.TryGet(null, out _));
        Assert.False(dictionaries.TryGet("", out _));
        Assert.NotEmpty(Hyphenated(Lay(Style(language: "en-US-x-test"))));
    }

    [Fact]
    public void ShortWordsAreLeftWhole()
    {
        HyphenationOptions options = new() { Dictionaries = Dictionaries(), MinWordLength = 13 };
        TextLayout layout = Lay(Style(options: options));
        foreach (TextLine line in Hyphenated(layout))
        {
            Assert.True(WordAt(layout.Paragraph.Text, line.Start + line.Length).Word.Length >= 13);
        }

        Assert.Empty(Hyphenated(Lay(Style(options: options with { MinWordLength = 100 }))));
    }

    [Fact]
    public void MinimumsBeforeAndAfterOverrideTheDictionary()
    {
        HyphenationOptions options = new() { Dictionaries = Dictionaries(), MinBefore = 5, MinAfter = 5 };
        TextLayout layout = Lay(Style(options: options));
        Assert.NotEmpty(Hyphenated(layout));
        foreach (TextLine line in Hyphenated(layout))
        {
            (string word, int offset) = WordAt(layout.Paragraph.Text, line.Start + line.Length);
            Assert.InRange(offset, 5, word.Length - 5);
            Assert.Contains(offset, Points(word, 5, 5));
        }
    }

    [Fact]
    public void ConsecutiveHyphenatedLinesAreLimited()
    {
        foreach (int limit in new[] { 1, 2 })
        {
            HyphenationOptions options = new() { Dictionaries = Dictionaries(), MaxConsecutiveLines = limit };
            foreach (float width in Widths)
            {
                TextLayout layout = Lay(Style(options: options), width: width);
                HashSet<TextLine> hyphenated = [.. Hyphenated(layout)];
                int run = 0;
                foreach (TextLine line in layout.Lines)
                {
                    run = hyphenated.Contains(line) ? run + 1 : 0;
                    Assert.True(run <= limit, $"{run} hyphenated lines in a row at width {width}, limit {limit}");
                }
            }
        }

        // Without a limit this text does hyphenate lines in a row at some width, so the limit is what holds it.
        Assert.Contains(Widths, width =>
        {
            TextLayout layout = Lay(Style(), width: width);
            HashSet<TextLine> hyphenated = [.. Hyphenated(layout)];
            return layout.Lines.Zip(layout.Lines.Skip(1)).Any(p => hyphenated.Contains(p.First) && hyphenated.Contains(p.Second));
        });
    }

    [Theory]
    [InlineData(TextAlign.Start)]
    [InlineData(TextAlign.Justify)]
    public void TheZoneKeepsWordsWholeWhenTheLineIsFullEnough(TextAlign align)
    {
        // A zone wider than the line: only a word that leaves the line empty is hyphenated, and this text has none.
        HyphenationOptions wide = new() { Dictionaries = Dictionaries(), Zone = Width + 1 };
        Assert.Empty(Hyphenated(Lay(Style(options: wide) with { Align = align })));

        // A moderate zone: every hyphenated line would have left more than the zone free without its piece of the word.
        HyphenationOptions zone = new() { Dictionaries = Dictionaries(), Zone = 30 };
        TextLayout layout = Lay(Style(options: zone) with { Align = align });
        Assert.True(Hyphenated(layout).Count <= Hyphenated(Lay(Style() with { Align = align })).Count);
        string text = layout.Paragraph.Text;
        foreach (TextLine line in Hyphenated(layout))
        {
            // The same line ended at the space before the word.
            int wordStart = line.Start + line.Length - WordAt(text, line.Start + line.Length).Offset;
            float before = Paragraph.Create(text[line.Start..wordStart].TrimEnd(), Style(hyphens: Hyphens.Manual)).Layout(float.PositiveInfinity).Lines[0].Width;
            Assert.True(Width - before > 30, $"{Width - before} <= 30");
        }

        // A word longer than the line is hyphenated whatever the zone.
        TextLayout single = Lay(Style(options: wide), "incomprehensibilities", 60);
        Assert.True(single.Lines.Count > 1);
        Assert.NotEmpty(Hyphenated(single));
    }

    [Fact]
    public void CapitalsCanBeLeftWhole()
    {
        string capitals = English.ToUpperInvariant();
        Assert.NotEmpty(Hyphenated(Lay(Style(), capitals)));
        HyphenationOptions options = new() { Dictionaries = Dictionaries(), HyphenateCapitalized = false };
        Assert.Empty(Hyphenated(Lay(Style(options: options), capitals)));

        // Capitalised words (a capital and lower-case letters) are still hyphenated.
        Assert.NotEmpty(Hyphenated(Lay(Style(options: options))));
    }

    [Fact]
    public void ManualHyphensWinAndWordsWithDigitsOrHyphensStayWhole()
    {
        // A word with a soft hyphen breaks only there.
        TextLayout soft = Lay(Style(), "Some text before extra\u00ADordinarily", 160);
        string text = soft.Paragraph.Text;
        Assert.All(soft.Lines.SkipLast(1), l => Assert.True(text[l.Start + l.Length - 1] is ' ' or '\u00AD'));

        foreach (string word in new[] { "extraordinarily2026", "well-extraordinarily", "extraordinarily-known" })
        {
            Assert.Empty(Hyphenated(Lay(Style(), "Some text before " + word, 160)));
        }
    }

    [Fact]
    public void HyphensNoneIgnoresSoftHyphens()
    {
        const string Text = "extra\u00ADordinarily";
        Assert.Contains(Lay(Style(hyphens: Hyphens.Manual), Text, 60).Lines, l => Text[l.Start + l.Length - 1] == '\u00AD');
        TextLayout none = Lay(Style(hyphens: Hyphens.None), Text, 60);
        Assert.Single(none.Lines);
        Assert.DoesNotContain(none.Lines, l => Text[l.Start + l.Length - 1] == '\u00AD');
        Assert.Empty(Hyphenated(none));
    }

    [Fact]
    public void LineFlowsAndRewindsHyphenateTheSame()
    {
        HyphenationOptions options = new() { Dictionaries = Dictionaries(), MaxConsecutiveLines = 1 };
        Paragraph paragraph = Paragraph.Create(English, Style(options: options));
        TextLayout layout = paragraph.Layout(Width);
        LineFlow flow = paragraph.CreateFlow();
        List<int> starts = [];
        bool rewound = false;
        while (flow.NextLine(Width) is TextLine line)
        {
            starts.Add(line.Start);
            if (starts.Count == 3 && !rewound)
            {
                // The third line goes back, as at the foot of a column, and is laid out again.
                rewound = true;
                flow.Rewind(line.Start);
                starts.RemoveAt(2);
            }
        }

        Assert.Equal(LineFillTests.Starts(layout), starts);
    }

    [Fact]
    public void OptionsCheckTheirValues()
    {
        HyphenationDictionaries dictionaries = new();
        Assert.Throws<ArgumentOutOfRangeException>(() => new HyphenationOptions { Dictionaries = dictionaries, MinWordLength = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new HyphenationOptions { Dictionaries = dictionaries, MinBefore = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new HyphenationOptions { Dictionaries = dictionaries, MinAfter = 256 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new HyphenationOptions { Dictionaries = dictionaries, MaxConsecutiveLines = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new HyphenationOptions { Dictionaries = dictionaries, Zone = float.NaN });
        Assert.Throws<ArgumentException>(() => dictionaries.Add(" ", s_english.Value));
    }
}
