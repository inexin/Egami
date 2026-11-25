using System.IO.Compression;
using System.Text;

using Egami.Fonts.Layout;
using Egami.Fonts.Unicode;

namespace Egami.Fonts.Tests.Layout;

/// <summary>Roadmap 2.8.5, Build Plan task 2: word dictionaries and the ICU word lists Egami.Fonts ships.</summary>
public sealed class WordDictionaryTests
{
    private const char Zwj = (char)0x200D;
    private const char Bom = (char)0xFEFF;

    [Fact]
    public void BuiltInHasTheFourScripts()
    {
        Assert.Equal(
            ["Khmr", "Laoo", "Mymr", "Thai"],
            WordDictionaries.BuiltIn.Scripts.Select(s => s.Code).Order(StringComparer.Ordinal));
        Assert.True(WordDictionaries.BuiltIn.IsReadOnly);
        Assert.False(WordDictionaries.BuiltIn.TryGet(Script.Latin, out _));
        Assert.False(WordDictionaries.BuiltIn.TryGet(Script.TaiTham, out _));
        Assert.Throws<InvalidOperationException>(() => WordDictionaries.BuiltIn.Add(Script.Latin, WordDictionary.FromWords(["a"])));
    }

    [Theory]
    [InlineData("th", 26383)]
    [InlineData("lo", 30550)]
    [InlineData("km", 81028)]
    [InlineData("my", 41120)]
    public void EveryBuiltInListLoadsAndPassesTheFullCheck(string name, int count)
    {
        // The built-in loader trusts its own resources; the public loader checks them all the way, as any user file.
        WordDictionary trusted = WordDictionaries.LoadBuiltIn(name);
        using Stream resource = typeof(WordDictionary).Assembly.GetManifestResourceStream($"Egami.Fonts.WordBreaking.{name}.edict")!;
        WordDictionary checkedCopy = WordDictionary.Load(resource);
        Assert.Equal(count, trusted.Count);
        Assert.Equal(count, checkedCopy.Count);
        Assert.InRange(trusted.Graph.GraphLength, 1, 1 << 20);
    }

    [Theory]
    [InlineData("Thai", "ภาษา")]
    [InlineData("Thai", "ไทย")]
    [InlineData("Thai", "สวัสดี")]
    [InlineData("Thai", "ประเทศไทย")]
    [InlineData("Lao", "ພາສາ")]
    [InlineData("Lao", "ລາວ")]
    [InlineData("Lao", "ສະບາຍດີ")]
    [InlineData("Khmer", "ភាសា")]
    [InlineData("Khmer", "ខ្មែរ")]
    [InlineData("Khmer", "កម្ពុជា")]
    [InlineData("Myanmar", "မြန်မာ")]
    [InlineData("Myanmar", "ဘာသာ")]
    [InlineData("Myanmar", "စကား")]
    public void BuiltInWordsAreFound(string script, string word)
    {
        Assert.True(WordDictionaries.BuiltIn.TryGet(ScriptByName(script), out WordDictionary? dictionary));
        Assert.True(dictionary.Contains(word));
        Assert.False(dictionary.Contains(word[..^1] + "x"));
    }

    [Fact]
    public void WordsOutsideTheMainBlockAreFound()
    {
        Assert.True(WordDictionaries.BuiltIn.TryGet(Script.Khmer, out WordDictionary? khmer));
        Assert.True(khmer.Contains("ម្ហ៊" + Zwj + "ិះ"));
        Assert.False(khmer.Contains("ម្ហ៊ិះ" + Zwj));
        Assert.True(WordDictionaries.BuiltIn.TryGet(Script.Myanmar, out WordDictionary? myanmar));
        Assert.True(myanmar.Contains("ကမ႓ာစစ်"));
    }

    [Fact]
    public void NonWordsAreNotFound()
    {
        Assert.True(WordDictionaries.BuiltIn.TryGet(Script.Thai, out WordDictionary? thai));
        Assert.False(thai.Contains(""));
        Assert.False(thai.Contains("ภาษาไทย"));
        Assert.False(thai.Contains("ภาษาไท"));
        Assert.False(thai.Contains("word"));
        Assert.False(thai.Contains(new string('ก', 200)));
    }

    [Fact]
    public void PrefixWalkFindsEveryWordEnd()
    {
        Assert.True(WordDictionaries.BuiltIn.TryGet(Script.Thai, out WordDictionary? thai));
        Span<int> lengths = stackalloc int[16];
        int count = thai.MatchPrefixes("ประเทศไทยมีภาษา", lengths);
        Assert.Equal([2, 3, 6, 9], lengths[..count].ToArray());

        // It stops when the span is full, and finds nothing where no word starts.
        Assert.Equal(2, thai.MatchPrefixes("ประเทศไทยมีภาษา", lengths[..2]));
        Assert.Equal([2, 3], lengths[..2].ToArray());
        Assert.Equal(0, thai.MatchPrefixes("xประเทศ", lengths));
        Assert.Equal(0, thai.MatchPrefixes("", lengths));
    }

    [Fact]
    public void FromWordsTakesAnyOrderAndDuplicates()
    {
        WordDictionary dictionary = WordDictionary.FromWords(["cat", "car", "cart", "a", "car", "dog", "do"]);
        Assert.Equal(6, dictionary.Count);
        foreach (string word in (string[])["a", "car", "cart", "cat", "do", "dog"])
        {
            Assert.True(dictionary.Contains(word), word);
        }

        foreach (string word in (string[])["", "c", "ca", "cars", "d", "dogs", "b"])
        {
            Assert.False(dictionary.Contains(word), word);
        }

        Assert.Equal(["a", "car", "cart", "cat", "do", "dog"], dictionary.Words());
        Span<int> lengths = stackalloc int[4];
        Assert.Equal(2, dictionary.MatchPrefixes("carts", lengths));
        Assert.Equal([3, 4], lengths[..2].ToArray());
    }

    [Fact]
    public void FromWordsRejectsBadInput()
    {
        Assert.Throws<ArgumentNullException>(() => WordDictionary.FromWords(null!));
        Assert.Throws<ArgumentException>(() => WordDictionary.FromWords(["a", ""]));
        Assert.Throws<ArgumentException>(() => WordDictionary.FromWords(["a", null!]));
        string[] tooMany = [.. Enumerable.Range(0, 256).Select(i => ((char)(0x4E00 + i)).ToString())];
        Assert.Throws<ArgumentException>(() => WordDictionary.FromWords(tooMany));
        Assert.Equal(255, WordDictionary.FromWords(tooMany[..255]).Count);
    }

    [Fact]
    public void EmptyDictionaryHasNoWords()
    {
        WordDictionary empty = WordDictionary.FromWords([]);
        Assert.Equal(0, empty.Count);
        Assert.False(empty.Contains("a"));
        Assert.Equal(0, empty.MatchPrefixes("abc", new int[4]));
        Assert.Empty(empty.Words());
        Assert.Equal(0, RoundTrip(empty).Count);
    }

    [Fact]
    public void SaveAndLoadRoundTrip()
    {
        Assert.True(WordDictionaries.BuiltIn.TryGet(Script.Lao, out WordDictionary? lao));
        WordDictionary back = RoundTrip(lao);
        Assert.Equal(lao.Count, back.Count);
        Assert.Equal(lao.Words(), back.Words());

        using MemoryStream first = new(), second = new();
        lao.Save(first);
        back.Save(second);
        Assert.Equal(first.ToArray(), second.ToArray());
    }

    [Fact]
    public void WithWordsAddsUserWordsAndKeepsTheOriginal()
    {
        Assert.True(WordDictionaries.BuiltIn.TryGet(Script.Thai, out WordDictionary? thai));
        string brand = "อีกามิ";
        Assert.False(thai.Contains(brand));
        WordDictionary more = thai.WithWords([brand, "ภาษา", "Egami"]);
        Assert.True(more.Contains(brand));
        Assert.True(more.Contains("Egami"));
        Assert.True(more.Contains("ประเทศไทย"));
        Assert.Equal(thai.Count + 2, more.Count);
        Assert.False(thai.Contains(brand));
        Assert.Throws<ArgumentNullException>(() => thai.WithWords(null!));
    }

    [Fact]
    public void LoadReadsAWordList()
    {
        string text = Bom + "# A list in ICU's form\n  # indented comment\nກາ\r\n\nກາງ  \nກາ\n";
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(text));
        WordDictionary dictionary = WordDictionary.Load(stream);
        Assert.Equal(2, dictionary.Count);
        Assert.True(dictionary.Contains("ກາ"));
        Assert.True(dictionary.Contains("ກາງ"));
        Assert.False(dictionary.Contains("#"));
    }

    [Fact]
    public void LoadRejectsDamagedFiles()
    {
        using MemoryStream saved = new();
        WordDictionary.FromWords(["cat", "car", "cart", "dog", "do", "ant", "antler"]).Save(saved);
        byte[] good = saved.ToArray();

        Assert.Throws<InvalidDataException>(() => WordDictionary.Load(new MemoryStream(good[..8])));
        byte[] version = (byte[])good.Clone();
        version[4] = 9;
        Assert.Throws<InvalidDataException>(() => WordDictionary.Load(new MemoryStream(version)));

        // Damage every bit of the inflated data: the file either loads into a dictionary that walks safely, or is refused.
        byte[] raw = Inflate(good);
        for (int i = 0; i < raw.Length; i++)
        {
            for (int bit = 0; bit < 8; bit++)
            {
                byte[] bad = (byte[])raw.Clone();
                bad[i] ^= (byte)(1 << bit);
                try
                {
                    WordDictionary loaded = WordDictionary.Load(new MemoryStream(Deflate(good, bad)));
                    _ = loaded.Contains("cart");
                    _ = loaded.MatchPrefixes("antlers", new int[8]);
                    Assert.Equal(loaded.Count, loaded.Words().Count());
                }
                catch (InvalidDataException)
                {
                }
            }
        }

        // Data after the graph.
        byte[] longer = [.. raw, 0];
        Assert.Throws<InvalidDataException>(() => WordDictionary.Load(new MemoryStream(Deflate(good, longer))));
    }

    [Fact]
    public void NoneIsEmptyAndReadOnly()
    {
        Assert.Empty(WordDictionaries.None.Scripts);
        Assert.True(WordDictionaries.None.IsReadOnly);
        Assert.False(WordDictionaries.None.TryGet(Script.Thai, out _));
        Assert.Throws<InvalidOperationException>(() => WordDictionaries.None.Add(Script.Thai, WordDictionary.FromWords(["ก"])));
    }

    [Fact]
    public void CopiesAddScriptsAndReplaceLists()
    {
        WordDictionary taiTham = WordDictionary.FromWords(["ᨠᩣ"]);
        WordDictionary thai = WordDictionary.FromWords(["ก"]);
        WordDictionaries copy = new(WordDictionaries.BuiltIn);
        Assert.False(copy.IsReadOnly);
        copy.Add(Script.TaiTham, taiTham);
        copy.Add(Script.Thai, thai);
        Assert.True(copy.TryGet(Script.TaiTham, out WordDictionary? found));
        Assert.Same(taiTham, found);
        Assert.True(copy.TryGet(Script.Thai, out found));
        Assert.Same(thai, found);
        Assert.True(copy.TryGet(Script.Khmer, out found));
        Assert.Equal(81028, found.Count);
        Assert.False(WordDictionaries.BuiltIn.TryGet(Script.TaiTham, out _));
        Assert.True(WordDictionaries.BuiltIn.TryGet(Script.Thai, out found));
        Assert.NotSame(thai, found);

        WordDictionaries own = new();
        Assert.Empty(own.Scripts);
        Assert.Throws<ArgumentNullException>(() => own.Add(Script.Thai, null!));
        Assert.Throws<ArgumentNullException>(() => new WordDictionaries(null!));
    }

    private static WordDictionary RoundTrip(WordDictionary dictionary)
    {
        using MemoryStream stream = new();
        dictionary.Save(stream);
        stream.Position = 0;
        return WordDictionary.Load(stream);
    }

    private static Script ScriptByName(string name) => name switch
    {
        "Thai" => Script.Thai,
        "Lao" => Script.Lao,
        "Khmer" => Script.Khmer,
        _ => Script.Myanmar,
    };

    private static byte[] Inflate(byte[] file)
    {
        using DeflateStream inflate = new(new MemoryStream(file, 5, file.Length - 5), CompressionMode.Decompress);
        using MemoryStream raw = new();
        inflate.CopyTo(raw);
        return raw.ToArray();
    }

    private static byte[] Deflate(byte[] file, byte[] raw)
    {
        using MemoryStream output = new();
        output.Write(file, 0, 5);
        using (DeflateStream deflate = new(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            deflate.Write(raw);
        }

        return output.ToArray();
    }
}
