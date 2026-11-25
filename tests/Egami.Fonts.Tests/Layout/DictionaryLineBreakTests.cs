using Egami.Fonts.Layout;
using Egami.Fonts.Unicode;

using WordBreak = Egami.Fonts.Layout.WordBreak;

namespace Egami.Fonts.Tests.Layout;

/// <summary>2.8.5 task 3 and 4: Thai, Lao, Khmer and Myanmar break between the words a dictionary finds.</summary>
public sealed class DictionaryLineBreakTests
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

    private static List<string> Words(string text, LineBreakOptions options = default)
    {
        List<string> words = [];
        int start = 0;
        foreach (int end in Breaks(text, options))
        {
            words.Add(text[start..end]);
            start = end;
        }

        return words;
    }

    // Expected words are ICU's (Intl.Segmenter in Node with ICU 78.2) for the same text.
    [Theory]
    [InlineData("ภาษาไทยง่ายนิดเดียว", "ภาษา|ไทย|ง่าย|นิด|เดียว")]
    [InlineData("ພາສາລາວງ່າຍດາຍ", "ພາສາ|ລາວ|ງ່າຍດາຍ")]
    [InlineData("ភាសាខ្មែរងាយស្រួល", "ភាសាខ្មែរ|ងាយស្រួល")]
    [InlineData("မြန်မာဘာသာစကား", "မြန်မာဘာသာ|စကား")]
    [InlineData("กรุงเทพฯเป็นเมืองหลวง", "กรุงเทพฯ|เป็น|เมือง|หลวง")]
    [InlineData("เด็กๆเล่นกัน", "เด็กๆ|เล่น|กัน")]
    public void BreaksBetweenDictionaryWords(string text, string expected)
    {
        Assert.Equal(expected.Split('|'), Words(text));
    }

    [Fact]
    public void LaoRepetitionMarkStaysWithTheWordBefore()
    {
        // ICU's word segmenter splits ທຸກ|ໆ|ຄົນ; a line must not start with ໆ.
        Assert.Equal(["ທຸກໆ", "ຄົນ"], Words("ທຸກໆຄົນ"));
    }

    [Fact]
    public void SpacesAndPunctuationStillBreakAsUax14Says()
    {
        Assert.Equal(["ภาษา", "ไทย ", "ง่าย"], Words("ภาษาไทย ง่าย"));
        Assert.Equal(["(ภาษา", "ไทย)"], Words("(ภาษาไทย)"));
    }

    [Fact]
    public void NoneBreaksOnlyAtSpaces()
    {
        LineBreakOptions none = new() { Dictionaries = WordDictionaries.None };
        Assert.Equal(["ภาษาไทยง่ายนิดเดียว"], Words("ภาษาไทยง่ายนิดเดียว", none));
        Assert.Equal(["ภาษาไทย ", "ง่าย"], Words("ภาษาไทย ง่าย", none));
    }

    [Fact]
    public void OwnDictionariesReplaceTheBuiltInOnes()
    {
        WordDictionaries own = new();
        own.Add(Script.Thai, WordDictionary.FromWords(["ภาษาไทย", "ง่าย", "นิดเดียว"]));
        LineBreakOptions options = new() { Dictionaries = own };
        Assert.Equal(["ภาษาไทย", "ง่าย", "นิดเดียว"], Words("ภาษาไทยง่ายนิดเดียว", options));

        // Lao has no dictionary in this set.
        Assert.Equal(["ພາສາລາວງ່າຍດາຍ"], Words("ພາສາລາວງ່າຍດາຍ", options));
    }

    [Fact]
    public void ShortRunsAreNotSplit()
    {
        // ICU needs four letters for two words, and in Thai more than four.
        WordDictionaries own = new();
        own.Add(Script.Thai, WordDictionary.FromWords(["กก", "ขข"]));
        own.Add(Script.Lao, WordDictionary.FromWords(["ກກ", "ຂຂ"]));
        LineBreakOptions options = new() { Dictionaries = own };
        Assert.Equal(["กกขข"], Words("กกขข", options));
        Assert.Equal(["ກກ", "ຂຂ"], Words("ກກຂຂ", options));
    }

    [Fact]
    public void TextNoWordFitsRunsToTheNextWord()
    {
        WordDictionaries own = new();
        own.Add(Script.Thai, WordDictionary.FromWords(["ภาษา", "ไทย"]));
        LineBreakOptions options = new() { Dictionaries = own };

        // The unknown letters after a long word start their own piece, which runs on to where the next word begins.
        Assert.Equal(["ภาษา", "กขค", "ไทย"], Words("ภาษากขคไทย", options));
    }

    [Fact]
    public void BreakAllAndAnywhereBreakBetweenEveryLetter()
    {
        string text = "ภาษาไทย";
        Assert.Equal(["ภ", "า", "ษ", "า", "ไ", "ท", "ย"], Words(text, new LineBreakOptions(WordBreak.BreakAll)));
        Assert.Equal(["ภ", "า", "ษ", "า", "ไ", "ท", "ย"], Words(text, new LineBreakOptions(Strictness: LineBreakStrictness.Anywhere)));
    }

    [Fact]
    public void KeepAllStillBreaksBetweenDictionaryWords()
    {
        // CSS Text 3's keep-all only keeps NU, AL, AI and ID letters together; SA is broken as normal.
        Assert.Equal(["ภาษา", "ไทย"], Words("ภาษาไทย", new LineBreakOptions(WordBreak.KeepAll)));
    }

    [Fact]
    public void ScriptChangesEndARunWithoutABreak()
    {
        // Each script's run is split on its own; where Thai meets Lao, UAX #14 keeps the two SA letters together, as ICU does.
        Assert.Equal([4, 11, 14], Breaks("ภาษาไทยພາສາລາວ"));
    }

    [Fact]
    public void NeverBreaksInsideAGraphemeCluster()
    {
        // Random Thai, Lao, Khmer and Myanmar letters and marks: breaks only fall on grapheme boundaries, and never before a
        // repetition or abbreviation mark.
        Random random = new(285);
        (int First, int Last)[] blocks = [(0x0E01, 0x0E4E), (0x0E81, 0x0EDF), (0x1780, 0x17DD), (0x1000, 0x109F)];
        for (int round = 0; round < 400; round++)
        {
            (int first, int last) = blocks[round % blocks.Length];
            char[] chars = new char[random.Next(1, 60)];
            for (int i = 0; i < chars.Length; i++)
            {
                chars[i] = (char)random.Next(first, last + 1);
            }

            string text = new(chars);
            int previous = 0;
            foreach (int b in Breaks(text))
            {
                Assert.True(b > previous);
                Assert.True(b == text.Length || TextSegmenter.IsGraphemeBoundary(text, b), $"{round}: break at {b} in {text}");
                Assert.True(b == text.Length || text[b] is not ((char)0x0E2F or (char)0x0E46 or (char)0x0EAF or (char)0x0EC6));
                previous = b;
            }
        }
    }

    [Fact]
    [Trait("Category", "Allocation")]
    public void BreakingAllocatesNothing()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("EGAMI_ALLOCATION_TESTS") == "1", "Allocation budgets run on demand: set EGAMI_ALLOCATION_TESTS=1.");
        string text = string.Concat(Enumerable.Repeat("ภาษาไทยง่ายนิดเดียว ພາສາລາວງ່າຍດາຍ ភាសាខ្មែរងាយស្រួល မြန်မာဘာသာစကား ", 20));
        int Count()
        {
            int n = 0;
            foreach (LineBreak b in LineBreaks.Enumerate(text))
            {
                n++;
            }

            return n;
        }

        int expected = Count();
        long before = GC.GetAllocatedBytesForCurrentThread();
        int count = Count();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(expected, count);
        Assert.Equal(0, allocated);
    }
}
