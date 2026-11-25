using System.Text.Json;

using Egami.Fonts.Layout;
using Egami.Fonts.Layout.WordBreaking;
using Egami.Fonts.Unicode;

namespace Egami.Fonts.Tests.Layout;

/// <summary>2.8.5 task 7: word boundaries with word dictionaries, against Chromium's Intl.Segmenter.</summary>
public sealed class DictionaryWordTests
{
    /// <summary>
    /// Myanmar segment starts Chromium has and Egami does not: ICU splits ကမ္ဘာ ("world") after the U+1039 virama, inside a
    /// grapheme cluster (GB9c), which Egami's segmenter never does (as for line breaks, 2.8.5 A2).
    /// </summary>
    private static readonly int[] MyanmarGraphemeSplits = [82, 324, 1871, 2234, 2380];

    /// <summary>
    /// Lao and Khmer differences in number: Chromium's ICU keeps ICU 69's smaller Lao dictionary and an abridged Khmer one
    /// with changed engine parameters (README.chromium), so it splits compounds that the release-79.1 lists Egami ships keep
    /// whole, and splits some words differently; in Lao it also splits ໆ off the word before it, which Egami keeps (A2).
    /// </summary>
    private static readonly Dictionary<string, (int ChromiumOnly, int EgamiOnly)> OlderListDifferences = new()
    {
        ["lao"] = (87, 11),
        ["khmer"] = (129, 79),
    };

    /// <summary>
    /// Lao boundaries Chromium has around a full stop or colon between two Lao letters (ຄ.ສ, ວ່າ:ປະ). Current ICU (78.2 in Node)
    /// keeps these together under rules WB6 and WB7, since its word rules count complex-context letters as ALetter; Egami
    /// follows current ICU.
    /// </summary>
    private static readonly int[] LaoPunctuationSplits = [46, 47, 462, 463, 1409, 1410, 2648, 2649, 3237, 3238];

    public static TheoryData<string> Texts() => ["thai", "lao", "khmer", "burmese"];

    private static (string Text, int[] Starts) Reference(string name)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(LayoutReferences.Folder, "words.json")));
        JsonElement t = doc.RootElement.GetProperty("texts").EnumerateArray().Single(x => x.GetProperty("name").GetString() == name);
        return (t.GetProperty("text").GetString()!, [.. t.GetProperty("starts").EnumerateArray().Select(x => x.GetInt32())]);
    }

    private static List<int> Starts(string text, WordDictionaries? dictionaries)
    {
        List<int> starts = [];
        foreach (TextSegment w in TextSegmenter.Words(text, dictionaries))
        {
            starts.Add(w.Start);
        }

        return starts;
    }

    private static List<string> Segments(string text, WordDictionaries? dictionaries)
    {
        List<string> words = [];
        foreach (TextSegment w in TextSegmenter.Words(text, dictionaries))
        {
            words.Add(text.Substring(w.Start, w.Length));
        }

        return words;
    }

    [Theory]
    [MemberData(nameof(Texts))]
    public void WordsEqualChromium(string name)
    {
        (string text, int[] chromium) = Reference(name);
        HashSet<int> egami = [.. Starts(text, null)];
        List<int> chromiumOnly = [.. chromium.Where(i => !egami.Contains(i))];
        List<int> egamiOnly = [.. egami.Where(i => !chromium.Contains(i))];

        if (name == "burmese")
        {
            Assert.Equal(MyanmarGraphemeSplits, chromiumOnly);
            Assert.Empty(egamiOnly);
        }
        else if (OlderListDifferences.TryGetValue(name, out (int ChromiumOnly, int EgamiOnly) known))
        {
            // Every difference falls inside a run the dictionary splits; outside them the boundaries are exact.
            Assert.All(chromiumOnly.Concat(egamiOnly).Where(i => !(name == "lao" && LaoPunctuationSplits.Contains(i))),
                i => Assert.True(DictionarySegmenter.IsInsideRun(text, i), $"{name}: {i}"));
            if (name == "lao")
            {
                Assert.Subset(chromiumOnly.ToHashSet(), LaoPunctuationSplits.ToHashSet());
            }

            Assert.Equal(known, (chromiumOnly.Count, egamiOnly.Count));
        }
        else
        {
            Assert.Empty(chromiumOnly);
            Assert.Empty(egamiOnly);
        }
    }

    [Fact]
    public void TheOldOverloadKeepsUax29()
    {
        // UAX #29 alone breaks between Thai letters; the dictionary overload finds words and, as ICU, joins letters and digits.
        Assert.Equal(["ภ", "า", "ษ", "า", "ไ", "ท", "ย"], Segments2("ภาษาไทย"));
        Assert.Equal(["ภาษา", "ไทย"], Segments("ภาษาไทย", null));
        Assert.Equal(["ภาษา", "ไทย123"], Segments("ภาษาไทย123", null));
        Assert.Equal(["abcภาษา", "ไทย"], Segments("abcภาษาไทย", null));
        Assert.Equal(["ก.ข"], Segments("ก.ข", null));

        static List<string> Segments2(string text)
        {
            List<string> words = [];
            foreach (TextSegment w in TextSegmenter.Words(text))
            {
                words.Add(text.Substring(w.Start, w.Length));
            }

            return words;
        }
    }

    [Fact]
    public void RunsKeepTogetherWithoutADictionary()
    {
        Assert.Equal(["ภาษาไทย", " ", "ง่าย", "."], Segments("ภาษาไทย ง่าย.", WordDictionaries.None));

        WordDictionaries thaiOnly = new();
        thaiOnly.Add(Script.Thai, WordDictionary.FromWords(["ภาษา", "ไทย"]));
        Assert.Equal(["ภาษา", "ไทย", " ", "ພາສາລາວ"], Segments("ภาษาไทย ພາສາລາວ", thaiOnly));
    }

    [Fact]
    public void AStoredEnumeratorEnumeratesTwiceAlike()
    {
        // Each foreach works on its own copy, which takes and returns its own segmenter.
        string text = "ภาษาไทย ง่าย Hello ភាសាខ្មែរ";
        DictionaryWordEnumerator words = TextSegmenter.Words(text, null);
        List<int> first = [];
        foreach (TextSegment w in words)
        {
            first.Add(w.Start);
        }

        List<int> second = [];
        foreach (TextSegment w in words)
        {
            second.Add(w.Start);
            foreach (TextSegment inner in TextSegmenter.Words(text, null))
            {
                Assert.True(inner.Length > 0); // a nested walk takes another segmenter
            }
        }

        Assert.Equal(first, second);
        Assert.Equal(Starts(text, null), first);
    }

    [Fact]
    public void LatinIsUnchanged()
    {
        string text = "Hello, world! It's 3.14 o'clock — don't panic.";
        List<int> plain = [];
        foreach (TextSegment w in TextSegmenter.Words(text))
        {
            plain.Add(w.Start);
        }

        Assert.Equal(plain, Starts(text, null));
    }

    [Fact]
    public void ThaiWordsSelectAsWords()
    {
        ParagraphStyle style = new()
        {
            DefaultStyle = new TextStyle { FontFamilies = [ParagraphTests.Family("NotoSans-Regular.ttf"), ParagraphTests.Family("shaping/NotoSansThai-Regular.ttf")] },
        };
        string text = "ภาษาไทยง่ายนิดเดียว";
        TextLayout layout = Paragraph.Create(text, style).Layout(float.PositiveInfinity);
        Assert.Equal(new TextSegment(4, 3), layout.GetWordBoundary(5));
        Assert.Equal(7, layout.MoveCaret(new TextPosition(4), CaretMove.NextWord).Index);
        Assert.Equal(4, layout.MoveCaret(new TextPosition(7), CaretMove.PreviousWord).Index);

        // With no dictionaries the whole run is one word.
        TextLayout none = Paragraph.Create(text, style with { LineBreaking = new LineBreakOptions { Dictionaries = WordDictionaries.None } })
            .Layout(float.PositiveInfinity);
        Assert.Equal(new TextSegment(0, text.Length), none.GetWordBoundary(5));
    }

    [Fact]
    [Trait("Category", "Allocation")]
    public void WordsAllocateNothing()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("EGAMI_ALLOCATION_TESTS") == "1", "Allocation budgets run on demand: set EGAMI_ALLOCATION_TESTS=1.");
        string text = string.Concat(Enumerable.Repeat("ภาษาไทยง่ายนิดเดียว Hello, world! ភាសាខ្មែរងាយស្រួល ", 20));
        int Count()
        {
            int n = 0;
            foreach (TextSegment w in TextSegmenter.Words(text, null))
            {
                n += w.Length;
            }

            return n;
        }

        Count();
        long before = GC.GetAllocatedBytesForCurrentThread();
        int count = Count();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(text.Length, count);
    }
}
