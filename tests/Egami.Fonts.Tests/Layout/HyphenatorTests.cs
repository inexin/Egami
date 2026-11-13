using System.Text;

using Egami.Fonts.Layout;

namespace Egami.Fonts.Tests.Layout;

/// <summary>Hyphenation Build Plan tasks 2 and 3: Liang's pattern lookup, exceptions, minimums, the readers and the compiled form.</summary>
public sealed class HyphenatorTests
{
    // The patterns Liang's thesis and the TeXbook (appendix H) use to show the lookup on "hyphenation".
    private const string Liang = ".hy3ph he2n hena4 hen5at 1na n2at 1tio 2io o2n";

    private static readonly Lazy<Hyphenator> s_english = new(() => LoadFile("hyph-en-us.tex"));
    private static readonly Lazy<Hyphenator> s_norwegian = new(() => LoadFile("hyph-no.tex"));

    private static string Folder => Path.Combine(TestFonts.Root, "tests", "hyphenation");

    private static Hyphenator LoadFile(string name, HyphenationFormat format = HyphenationFormat.Auto)
    {
        using FileStream stream = File.OpenRead(Path.Combine(Folder, name));
        return Hyphenator.Load(stream, format);
    }

    /// <summary>The word with a hyphen at each point.</summary>
    internal static string Hyphenate(Hyphenator hyphenator, string word)
    {
        int[] points = new int[Math.Max(word.Length - 1, 0)];
        int count = hyphenator.GetPoints(word, points);
        StringBuilder text = new(word);
        for (int i = count - 1; i >= 0; i--)
        {
            text.Insert(points[i], '-');
        }

        return text.ToString();
    }

    private static Hyphenator FromText(string text, HyphenationFormat format = HyphenationFormat.Auto) =>
        FromBytes(Encoding.UTF8.GetBytes(text), format);

    private static Hyphenator FromBytes(byte[] data, HyphenationFormat format = HyphenationFormat.Auto) =>
        Hyphenator.Load(new MemoryStream(data), format);

    private static byte[] Compile(Hyphenator hyphenator)
    {
        MemoryStream output = new();
        hyphenator.Save(output);
        return output.ToArray();
    }

    // The words of a hyph-utf8 file's \hyphenation group.
    private static string[] ExceptionList(string file)
    {
        string text = File.ReadAllText(Path.Combine(Folder, file));
        int start = text.IndexOf("\\hyphenation{", StringComparison.Ordinal);
        start = text.IndexOf('\n', start) + 1;
        int end = text.IndexOf('}', start);
        return text[start..end].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    }

    // What an exception word should give: its own hyphens, less those the minimums rule out.
    private static string Expected(string exception, int minBefore, int minAfter)
    {
        string letters = exception.Replace("-", "", StringComparison.Ordinal);
        StringBuilder text = new();
        int at = 0;
        foreach (string part in exception.Split('-'))
        {
            if (at > 0)
            {
                if (at >= minBefore && letters.Length - at >= minAfter)
                {
                    text.Append('-');
                }
            }

            text.Append(part);
            at += part.Length;
        }

        return text.ToString();
    }

    [Fact]
    public void LiangsExampleFindsHyPhenAtion()
    {
        // .hy3ph, he2n, hena4, hen5at, 1na, n2at, 1tio, 2io and o2n give h y3p h e2n5a4t2i o2n: odd values after "hy" and
        // "hyphen" only.
        Assert.Equal("hy-phen-ation", Hyphenate(Hyphenator.FromPatterns(Liang), "hyphenation"));
        Assert.Equal("hyphen-ation", Hyphenate(Hyphenator.FromPatterns(Liang, minBefore: 3), "hyphenation"));
        Assert.Equal("HY-PHEN-ATION", Hyphenate(Hyphenator.FromPatterns(Liang), "HYPHENATION"));
        Assert.Equal("hyphenation", Hyphenate(Hyphenator.FromPatterns(Liang, minBefore: 7, minAfter: 5), "hyphenation"));
    }

    [Fact]
    public void PointsAreUtf16OffsetsAscending()
    {
        Hyphenator hyphenator = Hyphenator.FromPatterns(Liang);
        Span<int> points = stackalloc int[10];
        Assert.Equal(2, hyphenator.GetPoints("hyphenation", points));
        Assert.Equal([2, 6], points[..2].ToArray());
        Assert.Equal(0, hyphenator.GetPoints("", points));
        Assert.Equal(0, hyphenator.GetPoints("hy", points));
        Assert.Throws<ArgumentException>(() => hyphenator.GetPoints("hyphenation", new int[1]));
    }

    [Fact]
    public void EnglishExceptionsAreExact()
    {
        Hyphenator english = s_english.Value;
        Assert.Equal(2, english.MinBefore);
        Assert.Equal(3, english.MinAfter);
        string[] exceptions = ExceptionList("hyph-en-us.tex");
        Assert.Equal(14, exceptions.Length);
        foreach (string exception in exceptions)
        {
            Assert.Equal(Expected(exception, 2, 3), Hyphenate(english, exception.Replace("-", "", StringComparison.Ordinal)));
        }
    }

    [Fact]
    public void NorwegianExceptionsAreExact()
    {
        Hyphenator norwegian = s_norwegian.Value;
        Assert.Equal(2, norwegian.MinBefore);
        Assert.Equal(2, norwegian.MinAfter);
        string[] exceptions = ExceptionList("hyph-no.tex");
        Assert.True(exceptions.Length > 100);
        foreach (string exception in exceptions)
        {
            Assert.Equal(Expected(exception, 2, 2), Hyphenate(norwegian, exception.Replace("-", "", StringComparison.Ordinal)));
        }
    }

    [Theory]
    [InlineData("hy-phen-ation")]
    [InlineData("al-go-rithm")]
    [InlineData("com-puter")]
    [InlineData("type-set-ting")]
    [InlineData("pro-gram-ming")]
    [InlineData("Math-e-mat-ics")]
    public void EnglishPatternsBreakAsTeX(string expected) =>
        Assert.Equal(expected, Hyphenate(s_english.Value, expected.Replace("-", "", StringComparison.Ordinal)));

    [Theory]
    [InlineData("kom-mu-ni-ka-sjon")]
    [InlineData("sjo-ko-la-de")]
    [InlineData("fot-ball-kamp")]
    [InlineData("skjønn-lit-te-ra-tur")]
    [InlineData("ord-de-ling")]
    public void NorwegianPatternsBreakWords(string expected) =>
        Assert.Equal(expected, Hyphenate(s_norwegian.Value, expected.Replace("-", "", StringComparison.Ordinal)));

    [Fact]
    public void WithExceptionsAddsAndReplaces()
    {
        Hyphenator liang = Hyphenator.FromPatterns(Liang, "ta-ble");
        Hyphenator more = liang.WithExceptions(["hyphen-ation", "Present", "re-cord"]);
        Assert.Equal("hyphen-ation", Hyphenate(more, "hyphenation"));
        Assert.Equal("Hyphen-ation", Hyphenate(more, "Hyphenation"));
        Assert.Equal("present", Hyphenate(more, "present"));
        Assert.Equal("ta-ble", Hyphenate(more, "table"));
        Assert.Equal("hy-phen-ation", Hyphenate(liang, "hyphenation"));
        Assert.Equal("table", Hyphenate(more.WithExceptions(["table"]), "table"));

        // The minimums hold for exceptions too: re-cord leaves "re", two letters, before the break.
        Assert.Equal("table", Hyphenate(Hyphenator.FromPatterns(Liang, "ta-ble", minBefore: 3), "table"));
    }

    [Fact]
    public void BreaksKeepCharactersWhole()
    {
        // "1x" allows a break before every x; none falls before a combining mark or inside a surrogate pair.
        Hyphenator x = Hyphenator.FromPatterns("1x 1́", minBefore: 1, minAfter: 1);
        Assert.Equal("a-xá-x", Hyphenate(x, "axáx"));

        // 𝐱 (U+1D431) is one character of two code units: patterns match it whole and the minimums count it once.
        Hyphenator math = Hyphenator.FromPatterns("1\U0001D431", minBefore: 2, minAfter: 1);
        Assert.Equal("a\U0001D431-\U0001D431", Hyphenate(math, "a\U0001D431\U0001D431"));
        Assert.Equal("ab-\U0001D431", Hyphenate(math, "ab\U0001D431"));
    }

    [Fact]
    public void LongWordsWork()
    {
        Hyphenator hyphenator = Hyphenator.FromPatterns("a1a", minBefore: 1, minAfter: 1);
        string word = new('a', 300);
        int[] points = new int[word.Length];
        Assert.Equal(299, hyphenator.GetPoints(word, points));
        Assert.Equal(Enumerable.Range(1, 299), points[..299]);
    }

    [Fact]
    public void PatternListLoads()
    {
        // As hyph-utf8's .pat.txt: patterns on their own lines.
        Hyphenator fromText = FromText(Liang.Replace(' ', '\n'));
        Assert.Equal("hy-phen-ation", Hyphenate(fromText, "hyphenation"));
        Assert.Equal(2, fromText.MinBefore);
        Assert.Equal(3, fromText.MinAfter);
        Assert.Equal("hy-phen-ation", Hyphenate(FromText(Liang, HyphenationFormat.PatternList), "hyphenation"));
    }

    [Fact]
    public void TeXReaderHandlesEscapesCommentsAndMinimums()
    {
        string tex = """
            % hyphenmins:
            %     generation:
            %         left: 3
            %         right: 3
            %     typesetting:
            %         left: 1
            %         right: 2
            % texlive:
            %     message: test
            \message{Test patterns}
            \patterns{ % a comment
            1^^e6 1^^^^00f8 x1y%
            z
            }
            \hyphenation{ab-^^e6c}
            """;
        Hyphenator hyphenator = FromText(tex);
        Assert.Equal(1, hyphenator.MinBefore);
        Assert.Equal(2, hyphenator.MinAfter);
        Assert.Equal("a-æb-øc", Hyphenate(hyphenator, "aæbøc"));

        // The comment at the end of "x1y%" takes the line end, so the pattern is x1yz.
        Assert.Equal("ax-yz", Hyphenate(hyphenator, "axyz"));
        Assert.Equal("axyb", Hyphenate(hyphenator, "axyb"));
        Assert.Equal("ab-æc", Hyphenate(hyphenator, "abæc"));
    }

    [Fact]
    public void TeXInputIsNotFollowed()
    {
        // hyph-nb.tex is \input hyph-no.tex plus two exceptions.
        NotSupportedException error = Assert.Throws<NotSupportedException>(() => LoadFile("hyph-nb.tex"));
        Assert.Contains("hyph-no.tex", error.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => FromText("\\patterns{ab1c", HyphenationFormat.TeX));
        Assert.Throws<InvalidDataException>(() => FromText("% nothing here", HyphenationFormat.TeX));
    }

    [Fact]
    public void LibHyphenFilesLoad()
    {
        // ISO 8859-1 bytes, minimums from the file.
        byte[] latin1 = Encoding.Latin1.GetBytes("ISO8859-1\nLEFTHYPHENMIN 1\nRIGHTHYPHENMIN 1\n% a comment\n1æ\n");
        Hyphenator hyphenator = FromBytes(latin1);
        Assert.Equal(1, hyphenator.MinBefore);
        Assert.Equal(1, hyphenator.MinAfter);
        Assert.Equal("a-æb", Hyphenate(hyphenator, "aæb"));

        // libhyphen's default minimums are 2 and 2; the patterns after NEXTLEVEL are the ones for whole words.
        Hyphenator levels = FromText("UTF-8\n1b\nNEXTLEVEL\n1c\n");
        Assert.Equal(2, levels.MinBefore);
        Assert.Equal(2, levels.MinAfter);
        Assert.Equal("aab-cc", Hyphenate(levels, "aabcc"));

        // NOHYPHEN: no break next to these.
        Hyphenator apostrophe = FromText("UTF-8\nLEFTHYPHENMIN 1\nRIGHTHYPHENMIN 1\nNOHYPHEN ',-\n1b\n", HyphenationFormat.LibHyphen);
        Assert.Equal("a-ba'ba-b", Hyphenate(apostrophe, "aba'bab"));
        Assert.Equal("ka-b", Hyphenate(apostrophe, "kab"));
        Assert.Equal("k'b", Hyphenate(apostrophe, "k'b"));
    }

    [Fact]
    public void LibHyphenReplacementRulesThrow()
    {
        NotSupportedException error = Assert.Throws<NotSupportedException>(() => FromText("UTF-8\nc1k/k=k,1,1\n"));
        Assert.Contains("c1k/k=k,1,1", error.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => FromText("UTF-8\nLEFTHYPHENMIN x\n"));
        Assert.Throws<InvalidDataException>(() => FromText("1a", HyphenationFormat.LibHyphen));
    }

    [Fact]
    public void CompiledFormRoundTrips()
    {
        foreach (Hyphenator source in new[] { s_english.Value, s_norwegian.Value })
        {
            byte[] compiled = Compile(source);
            Hyphenator loaded = FromBytes(compiled);
            Assert.Equal(source.MinBefore, loaded.MinBefore);
            Assert.Equal(source.MinAfter, loaded.MinAfter);
            Assert.Equal(compiled, Compile(loaded));
            foreach (string word in Words())
            {
                Assert.Equal(Hyphenate(source, word), Hyphenate(loaded, word));
            }
        }

        Hyphenator withExtras = FromText("UTF-8\nLEFTHYPHENMIN 1\nNOHYPHEN '\n1b\n").WithExceptions(["ab-ba"])
            .WithMetadata([new("title", "Test"), new("licence", "MIT")]);
        Hyphenator back = FromBytes(Compile(withExtras), HyphenationFormat.Compiled);
        Assert.Equal("ab-ba", Hyphenate(back, "abba"));
        Assert.Equal("a'b-bab", Hyphenate(back, "a'bbab"));
        Assert.Equal(withExtras.Metadata, back.Metadata);
    }

    // The exception words of both files and the words the known-answer tests use.
    private static IEnumerable<string> Words() =>
        ExceptionList("hyph-en-us.tex").Concat(ExceptionList("hyph-no.tex")).Select(w => w.Replace("-", "", StringComparison.Ordinal))
            .Concat(["hyphenation", "algorithm", "computer", "typesetting", "kommunikasjon", "skjønnlitteratur", "Mathematics"]);

    [Fact]
    public void DamagedCompiledDataThrowsInvalidData()
    {
        byte[] compiled = Compile(Hyphenator.FromPatterns(Liang, "ta-ble").WithMetadata([new("title", "Liang")]));
        for (int length = 0; length < compiled.Length; length++)
        {
            Assert.Throws<InvalidDataException>(() => Hyphenator.Load(new MemoryStream(compiled[..length]), HyphenationFormat.Compiled));
        }

        // Any byte changed: the load either fails with InvalidDataException or gives a hyphenator that still answers.
        Random random = new(1983);
        for (int trial = 0; trial < 2000; trial++)
        {
            byte[] damaged = (byte[])compiled.Clone();
            damaged[random.Next(4, damaged.Length)] = (byte)random.Next(256);
            Hyphenator? loaded = null;
            try
            {
                loaded = FromBytes(damaged, HyphenationFormat.Compiled);
            }
            catch (InvalidDataException)
            {
            }

            if (loaded is not null)
            {
                Hyphenate(loaded, "hyphenation");
                Hyphenate(loaded, "table");
            }
        }
    }

    [Fact]
    public void CompiledFormIsCompactAndKeepsEveryAnswer()
    {
        // Version 2 stores no edge targets (breadth-first numbering makes them implicit) and packs the rest in varints.
        byte[] compiled = Compile(s_norwegian.Value);
        Assert.True(compiled.Length < 200_000, $"{compiled.Length} bytes");
        Hyphenator back = FromBytes(compiled, HyphenationFormat.Compiled);
        foreach (string word in Words())
        {
            Assert.Equal(Hyphenate(s_norwegian.Value, word), Hyphenate(back, word));
        }
    }

    [Fact]
    public void CompiledVersion1IsRejected()
    {
        byte[] compiled = Compile(s_english.Value);
        compiled[4] = 1;
        compiled[5] = 0;
        InvalidDataException e = Assert.Throws<InvalidDataException>(() => FromBytes(compiled, HyphenationFormat.Compiled));
        Assert.Contains("version 1", e.Message, StringComparison.Ordinal);
    }
}
