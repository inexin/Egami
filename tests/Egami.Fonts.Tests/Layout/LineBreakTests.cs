using System.Globalization;
using System.Text;

using Egami.Fonts.Layout;
using Egami.Fonts.Tests.Unicode;

namespace Egami.Fonts.Tests.Layout;

/// <summary>UAX #14 line breaking: Unicode's LineBreakTest.txt in full, and the CSS switches.</summary>
public sealed class LineBreakTests
{
    private static List<int> Breaks(string text, LineBreakOptions options = default)
    {
        List<int> result = [];
        foreach (LineBreak b in LineBreaks.Enumerate(text, options))
        {
            result.Add(b.Index);
        }

        return result;
    }

    [Fact]
    public void LineBreakTestPasses()
    {
        int cases = 0;
        List<string> failures = [];
        foreach (string raw in UnicodeFiles.Lines("LineBreakTest.txt.gz"))
        {
            string line = raw.Split('#')[0].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            StringBuilder text = new();
            List<int> expected = [];
            foreach (string token in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (token == "\u00F7")
                {
                    expected.Add(text.Length);
                }
                else if (token != "\u00D7")
                {
                    text.Append(char.ConvertFromUtf32(int.Parse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
                }
            }

            cases++;
            // UAX #14's own rules are Strict; CSS's default also breaks before small kana.
            List<int> actual = Breaks(text.ToString(), new LineBreakOptions(Strictness: LineBreakStrictness.Strict));
            if (!actual.SequenceEqual(expected))
            {
                failures.Add($"{raw.Split('#')[0].Trim()}: got {string.Join(',', actual)}, expected {string.Join(',', expected)}");
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} of {cases} failed:\n" + string.Join('\n', failures.Take(30)));
        Assert.True(cases > 15000, $"Only {cases} cases ran.");
    }

    public static TheoryData<string> ChromiumCases()
    {
        var data = new TheoryData<string>();
        foreach (LayoutReferences.Case c in LayoutReferences.AllCases())
        {
            data.Add(c.Name);
        }

        return data;
    }

    /// <summary>The line break options a reference case's style asks for.</summary>
    internal static LineBreakOptions Options(LayoutReferences.Case c)
    {
        static T Enum<T>(System.Text.Json.JsonElement style, string name, T fallback)
            where T : struct, System.Enum =>
            style.TryGetProperty(name, out System.Text.Json.JsonElement v) ? System.Enum.Parse<T>(v.GetString()!) : fallback;

        string? language = c.Style.TryGetProperty("language", out System.Text.Json.JsonElement l) ? l.GetString() : null;
        return new LineBreakOptions(
            Enum(c.Style, "wordBreak", WordBreak.Normal),
            Enum(c.Style, "overflowWrap", OverflowWrap.Normal),
            Enum(c.Style, "lineBreak", LineBreakStrictness.Auto),
            language);
    }

    [Theory]
    [MemberData(nameof(ChromiumCases))]
    public void ChromiumLineStartsAreBreakOpportunities(string name)
    {
        // Every line Chromium starts must start at one of Egami's break opportunities, except where an overflowing piece breaks
        // between grapheme clusters (overflow-wrap) or Chromium breaks Thai, Lao, Khmer or Myanmar with its dictionary. Where
        // Chromium breaks among the opportunities is line filling's business (task 5).
        LayoutReferences.Case c = LayoutReferences.Find(name);
        LineBreakOptions options = Options(c);
        HashSet<int> opportunities = [.. Breaks(c.Text, options)];
        List<string> misses = [];
        foreach (LayoutReferences.Layout layout in c.Layouts)
        {
            foreach (LayoutReferences.Line line in layout.Lines.Skip(1))
            {
                int i = line.Start;
                if (opportunities.Contains(i))
                {
                    continue;
                }

                bool emergency = options.OverflowWrap != OverflowWrap.Normal && Egami.Fonts.Unicode.TextSegmenter.IsGraphemeBoundary(c.Text, i);
                bool dictionary = Egami.Fonts.Unicode.UnicodeData.GetLineBreakClass(char.ConvertToUtf32(c.Text, i)) == Egami.Fonts.Unicode.LineBreakClass.ComplexContext;
                if (!emergency && !dictionary)
                {
                    misses.Add($"{layout.Width}: {i} ({c.Text.Substring(Math.Max(0, i - 8), Math.Min(16, c.Text.Length - Math.Max(0, i - 8)))})");
                }
            }
        }

        Assert.True(misses.Count == 0, $"{name}: Chromium starts lines where Egami finds no break: {string.Join("; ", misses)}");
    }

    [Fact]
    public void HardBreaksAreMandatoryAndSoftHyphensAreFlagged()
    {
        List<LineBreak> breaks = [];
        foreach (LineBreak b in LineBreaks.Enumerate("ab\r\ncd\u00ADef\u2028g\n"))
        {
            breaks.Add(b);
        }

        Assert.Equal(
            [new LineBreak(4, true, false), new LineBreak(7, false, true), new LineBreak(10, true, false), new LineBreak(12, true, false)],
            breaks);
        Assert.Empty(Breaks(""));
        Assert.Equal([1], Breaks("a"));
    }

    [Fact]
    public void PlaceholdersBreakBeforeAndAfter()
    {
        Assert.Equal([5, 6, 11], Breaks("image\uFFFCtext."));
    }

    [Fact]
    public void ThaiBreaksOnlyAtSpacesAndZeroWidthSpaces()
    {
        Assert.Equal([9, 13], Breaks("\u0E2A\u0E27\u0E31\u0E2A\u0E14\u0E35\u0E04\u0E23 \u0E42\u0E25\u0E01\u0E01"[..13]));
        Assert.Equal([4, 6], Breaks("\u0E01\u0E02\u0E03\u200B\u0E04\u0E05"));
    }

    [Fact]
    public void WordBreakSwitches()
    {
        Assert.Equal([6, 11], Breaks("hello world"));
        Assert.Equal([1, 2, 3, 4, 6, 7, 8, 9, 10, 11], Breaks("hello world", new LineBreakOptions(WordBreak: WordBreak.BreakAll)));

        // Korean breaks between syllables by default and only between words with keep-all; Chinese punctuation still breaks.
        string korean = "\uBAA8\uB4E0 \uC778\uAC04\uC740";
        Assert.Equal([1, 3, 4, 5, 6], Breaks(korean));
        Assert.Equal([3, 6], Breaks(korean, new LineBreakOptions(WordBreak: WordBreak.KeepAll)));
        Assert.Equal([3, 5], Breaks("\u4EBA\u4EBA\u3002\u4EBA\u4EBA", new LineBreakOptions(WordBreak: WordBreak.KeepAll)));
    }

    [Fact]
    public void StrictnessFollowsCssText()
    {
        // Small kana: Auto and Normal break before it, Strict (UAX #14's own rules) does not.
        string kana = "\u304B\u3063\u3053";
        Assert.Equal([1, 2, 3], Breaks(kana));
        Assert.Equal([1, 2, 3], Breaks(kana, new LineBreakOptions(Strictness: LineBreakStrictness.Normal)));
        Assert.Equal([2, 3], Breaks(kana, new LineBreakOptions(Strictness: LineBreakStrictness.Strict)));

        // The wave dash starts a line under normal rules only in Chinese and Japanese.
        string dash = "\u3042\u301C\u3044";
        Assert.Equal([2, 3], Breaks(dash, new LineBreakOptions(Language: "en")));
        Assert.Equal([1, 2, 3], Breaks(dash, new LineBreakOptions(Language: "ja")));
        Assert.Equal([2, 3], Breaks(dash, new LineBreakOptions(Strictness: LineBreakStrictness.Strict, Language: "ja")));

        // Iteration marks break only under loose rules in Japanese.
        string iteration = "\u4EBA\u3005\u4EBA";
        Assert.Equal([2, 3], Breaks(iteration, new LineBreakOptions(Language: "ja")));
        Assert.Equal([1, 2, 3], Breaks(iteration, new LineBreakOptions(Strictness: LineBreakStrictness.Loose, Language: "ja-JP")));

        // Anywhere: every grapheme cluster, but never before a hard break.
        Assert.Equal([1, 2, 3, 4, 7, 9], Breaks("a b-c\r\nd\u0301", new LineBreakOptions(Strictness: LineBreakStrictness.Anywhere)));
    }
}
