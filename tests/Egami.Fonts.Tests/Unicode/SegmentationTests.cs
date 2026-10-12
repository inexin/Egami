using System.Globalization;
using System.Text;

using Egami.Fonts.Unicode;

namespace Egami.Fonts.Tests.Unicode;

/// <summary>Unicode's segmentation conformance files pass in full; script runs follow UAX #24.</summary>
public sealed class SegmentationTests
{
    [Fact]
    public void GraphemeBreakTestPasses()
    {
        int cases = 0;
        List<string> failures = [];
        foreach ((string text, List<int> expected, string line) in Cases("GraphemeBreakTest.txt.gz"))
        {
            cases++;
            List<int> actual = [0];
            foreach (TextSegment g in TextSegmenter.Graphemes(text))
            {
                actual.Add(g.End);
            }

            List<int> queried = [.. Enumerable.Range(0, text.Length + 1).Where(i => TextSegmenter.IsGraphemeBoundary(text, i))];
            if (!actual.SequenceEqual(expected) || !queried.SequenceEqual(expected))
            {
                failures.Add($"{line}: got {string.Join(',', actual)} and {string.Join(',', queried)}, expected {string.Join(',', expected)}");
            }
        }

        Assert.True(failures.Count == 0, string.Join('\n', failures.Take(20)));
        Assert.True(cases > 700, $"Only {cases} cases ran.");
    }

    [Fact]
    public void WordBreakTestPasses()
    {
        int cases = 0;
        List<string> failures = [];
        foreach ((string text, List<int> expected, string line) in Cases("WordBreakTest.txt.gz"))
        {
            cases++;
            List<int> actual = [0];
            foreach (TextSegment w in TextSegmenter.Words(text))
            {
                actual.Add(w.End);
            }

            if (!actual.SequenceEqual(expected))
            {
                failures.Add($"{line}: got {string.Join(',', actual)}, expected {string.Join(',', expected)}");
            }
        }

        Assert.True(failures.Count == 0, string.Join('\n', failures.Take(20)));
        Assert.True(cases > 1800, $"Only {cases} cases ran.");
    }

    [Fact]
    public void EmptyTextHasNoSegments()
    {
        Assert.False(TextSegmenter.Graphemes("").MoveNext());
        Assert.False(TextSegmenter.Words("").MoveNext());
        Assert.True(TextSegmenter.IsGraphemeBoundary("", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextSegmenter.IsGraphemeBoundary("ab", 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => TextSegmenter.IsGraphemeBoundary("ab", -1));
    }

    [Fact]
    public void LoneSurrogatesAreOneClusterEach()
    {
        string text = "a\uD800b\uDC00";
        List<TextSegment> graphemes = [];
        foreach (TextSegment g in TextSegmenter.Graphemes(text))
        {
            graphemes.Add(g);
        }

        Assert.Equal([new(0, 1), new(1, 1), new(2, 1), new(3, 1)], graphemes);
        Assert.False(TextSegmenter.IsGraphemeBoundary("\uD83D\uDE00", 1));
    }

    [Theory]
    [InlineData("Hello, world", "Latn:0:12")]
    [InlineData("123 abc", "Latn:0:7")]
    [InlineData("123 ...", "Zyyy:0:7")]
    [InlineData("abc \u05D0\u05D1\u05D2 def", "Latn:0:4 Hebr:4:4 Latn:8:3")]
    [InlineData("abc (\u05D0\u05D1\u05D2) def", "Latn:0:5 Hebr:5:3 Latn:8:5")]
    [InlineData("\u0915\u093F\u0964 \u0915", "Deva:0:5")]
    [InlineData("\u0995\u0964", "Beng:0:2")]
    [InlineData("\u65E5\u672C\u3001\u3072\u3089\u304C\u306A", "Hani:0:3 Hira:3:4")]
    [InlineData("\u0627\u0644\u0639\u0631\u0628\u064A\u0629 123", "Arab:0:11")]
    [InlineData("a\u0301b", "Latn:0:3")]
    [InlineData("\u0301", "Zyyy:0:1")]
    public void ScriptRunsFollowUax24(string text, string expected)
    {
        List<ScriptRun> runs = [];
        ScriptItemizer.Itemize(text, runs);
        string actual = string.Join(' ', runs.Select(r => $"{r.Script.Code}:{r.Start}:{r.Length}"));
        Assert.Equal(expected, actual);
    }

    private static IEnumerable<(string Text, List<int> Boundaries, string Line)> Cases(string file)
    {
        foreach (string raw in UnicodeFiles.Lines(file))
        {
            string line = raw.Split('#')[0].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            StringBuilder text = new();
            List<int> boundaries = [];
            foreach (string token in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (token == "\u00F7")
                {
                    boundaries.Add(text.Length);
                }
                else if (token != "\u00D7")
                {
                    text.Append(char.ConvertFromUtf32(int.Parse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
                }
            }

            yield return (text.ToString(), boundaries, line);
        }
    }
}
