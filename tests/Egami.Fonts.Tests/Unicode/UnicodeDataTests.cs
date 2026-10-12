using System.Globalization;
using System.IO.Compression;

using Egami.Fonts.Unicode;

namespace Egami.Fonts.Tests.Unicode;

/// <summary>Every property of every code point equals the Unicode Character Database (tests/reference/unicode/properties.txt.gz).</summary>
public sealed class UnicodeDataTests
{
    private static readonly Lazy<Dictionary<string, List<string>>> s_expected = new(Load);

    [Theory]
    [InlineData("gc")]
    [InlineData("sc")]
    [InlineData("bc")]
    [InlineData("jt")]
    [InlineData("jg")]
    [InlineData("ccc")]
    [InlineData("InSC")]
    [InlineData("InPC")]
    [InlineData("vo")]
    [InlineData("ea")]
    [InlineData("GCB")]
    [InlineData("WB")]
    [InlineData("InCB")]
    [InlineData("lb")]
    [InlineData("Emoji")]
    [InlineData("EmojiPresentation")]
    [InlineData("EmojiModifier")]
    [InlineData("EmojiModifierBase")]
    [InlineData("EmojiComponent")]
    [InlineData("ExtendedPictographic")]
    [InlineData("DefaultIgnorable")]
    public void PropertyMatchesTheDatabaseForEveryCodePoint(string property)
    {
        Func<int, string> actual = property switch
        {
            "gc" => cp => UnicodeData.GetGeneralCategory(cp).ToString(),
            "sc" => cp => UnicodeData.GetScript(cp).Code,
            "bc" => cp => UnicodeData.GetBidiClass(cp).ToString(),
            "jt" => cp => UnicodeData.GetJoiningType(cp).ToString(),
            "jg" => cp => UnicodeData.GetJoiningGroup(cp).ToString(),
            "ccc" => cp => UnicodeData.GetCanonicalCombiningClass(cp).ToString(CultureInfo.InvariantCulture),
            "InSC" => cp => UnicodeData.GetIndicSyllabicCategory(cp).ToString(),
            "InPC" => cp => UnicodeData.GetIndicPositionalCategory(cp).ToString(),
            "vo" => cp => UnicodeData.GetVerticalOrientation(cp).ToString(),
            "ea" => cp => UnicodeData.GetEastAsianWidth(cp).ToString(),
            "GCB" => cp => UnicodeData.GraphemeBreakOf(cp).ToString(),
            "WB" => cp => UnicodeData.WordBreakOf(cp).ToString(),
            "InCB" => cp => UnicodeData.IndicConjunctBreakOf(cp).ToString(),
            "lb" => cp => UnicodeData.GetLineBreakClass(cp).ToString(),
            "Emoji" => cp => Bit(UnicodeData.IsEmoji(cp)),
            "EmojiPresentation" => cp => Bit(UnicodeData.IsEmojiPresentation(cp)),
            "EmojiModifier" => cp => Bit(UnicodeData.IsEmojiModifier(cp)),
            "EmojiModifierBase" => cp => Bit(UnicodeData.IsEmojiModifierBase(cp)),
            "EmojiComponent" => cp => Bit(UnicodeData.IsEmojiComponent(cp)),
            "ExtendedPictographic" => cp => Bit(UnicodeData.IsExtendedPictographic(cp)),
            "DefaultIgnorable" => cp => Bit(UnicodeData.IsDefaultIgnorable(cp)),
            _ => throw new ArgumentException(property),
        };

        int covered = 0;
        foreach (string line in s_expected.Value[property])
        {
            string[] f = line.Split(' ');
            int first = int.Parse(f[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            int last = int.Parse(f[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            Assert.Equal(covered, first);
            for (int cp = first; cp <= last; cp++)
            {
                string value = actual(cp);
                if (value != f[2])
                {
                    Assert.Fail($"U+{cp:X4} {property}: expected {f[2]}, got {value}.");
                }
            }

            covered = last + 1;
        }

        Assert.Equal(0x110000, covered);
    }

    [Fact]
    public void ScriptExtensionsMatchTheDatabase()
    {
        HashSet<int> listed = [];
        foreach (string line in s_expected.Value["scx"])
        {
            string[] f = line.Split(' ');
            int first = int.Parse(f[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            int last = int.Parse(f[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            for (int cp = first; cp <= last; cp++)
            {
                listed.Add(cp);
                Assert.Equal(f[2..], UnicodeData.GetScriptExtensions(cp).ToArray().Select(s => s.Code));
            }
        }

        foreach (int cp in new[] { 'A', 0x0627, 0x0915, 0x4E00, 0x10FFFF })
        {
            Assert.DoesNotContain(cp, listed);
            Assert.Equal([UnicodeData.GetScript(cp)], UnicodeData.GetScriptExtensions(cp).ToArray());
        }
    }

    [Fact]
    public void MirroringAndBracketsMatchTheDatabase()
    {
        Dictionary<int, int> mirrors = [];
        foreach (string line in s_expected.Value["bmg"])
        {
            string[] f = line.Split(' ');
            mirrors[Hex(f[0])] = Hex(f[1]);
        }

        Dictionary<int, (int Pair, string Type)> brackets = [];
        foreach (string line in s_expected.Value["bpb"])
        {
            string[] f = line.Split(' ');
            brackets[Hex(f[0])] = (Hex(f[1]), f[2]);
        }

        for (int cp = 0; cp <= 0x10FFFF; cp++)
        {
            bool mirrored = UnicodeData.TryGetBidiMirroringGlyph(cp, out int mirror);
            Assert.Equal(mirrors.ContainsKey(cp), mirrored);
            Assert.Equal(mirrored ? mirrors[cp] : cp, mirror);

            BidiPairedBracketType type = UnicodeData.GetBidiPairedBracketType(cp);
            if (brackets.TryGetValue(cp, out (int Pair, string Type) bracket))
            {
                Assert.Equal(bracket.Type == "o" ? BidiPairedBracketType.Open : BidiPairedBracketType.Close, type);
                Assert.Equal(bracket.Pair, UnicodeData.GetBidiPairedBracket(cp));
            }
            else
            {
                Assert.Equal(BidiPairedBracketType.None, type);
                Assert.Equal(cp, UnicodeData.GetBidiPairedBracket(cp));
            }
        }
    }

    [Fact]
    public void GeneralCategoryAgreesWithTheRuntimeWhereBothAssign()
    {
        // An independent check of the generator: the runtime's own tables (an older Unicode version) agree on every code point
        // they assign, except the few whose category Unicode changed since.
        int differences = 0;
        for (int cp = 0; cp <= 0x10FFFF; cp++)
        {
            UnicodeCategory runtime = CharUnicodeInfo.GetUnicodeCategory(cp);
            if (runtime != UnicodeCategory.OtherNotAssigned && runtime != UnicodeData.GetGeneralCategory(cp))
            {
                differences++;
            }
        }

        Assert.InRange(differences, 0, 16);
    }

    [Fact]
    public void ScriptsHaveCodesNamesAndOpenTypeTags()
    {
        Assert.Equal(Script.Unknown, default);
        Assert.Equal("Latn", Script.Latin.Code);
        Assert.Equal("Latin", Script.Latin.Name);
        Assert.Equal(["latn"], Script.Latin.OpenTypeTags.ToArray().Select(t => t.ToString()));
        Assert.Equal(["dev2", "deva"], Script.Devanagari.OpenTypeTags.ToArray().Select(t => t.ToString()));
        Assert.Equal(["mym2", "mymr"], Script.Myanmar.OpenTypeTags.ToArray().Select(t => t.ToString()));
        Assert.Equal(["kana"], Script.Hiragana.OpenTypeTags.ToArray().Select(t => t.ToString()));
        Assert.Equal(["lao "], Script.Lao.OpenTypeTags.ToArray().Select(t => t.ToString()));
        Assert.Equal(["DFLT"], Script.Common.OpenTypeTags.ToArray().Select(t => t.ToString()));
        Assert.True(Script.Inherited.IsShared);
        Assert.False(Script.Arabic.IsShared);
        Assert.True(Script.TryFromCode("arab", out Script arabic));
        Assert.Equal(Script.Arabic, arabic);
        Assert.False(Script.TryFromCode("Qqqq", out Script none));
        Assert.Equal(Script.Unknown, none);
        Assert.Equal("Deva", Script.Devanagari.ToString());
        Assert.Equal(Script.Devanagari, UnicodeData.GetScript('\u0915'));
        Assert.Equal(new Version(17, 0, 0), UnicodeData.UnicodeVersion);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0x110000)]
    [InlineData(int.MaxValue)]
    public void OutOfRangeCodePointsThrow(int codePoint)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => UnicodeData.GetScript(codePoint));
        Assert.Throws<ArgumentOutOfRangeException>(() => UnicodeData.GetBidiClass(codePoint));
        Assert.Throws<ArgumentOutOfRangeException>(() => UnicodeData.TryGetBidiMirroringGlyph(codePoint, out _));
    }

    private static string Bit(bool value) => value ? "1" : "0";

    private static int Hex(string text) => int.Parse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static Dictionary<string, List<string>> Load()
    {
        Dictionary<string, List<string>> sections = [];
        List<string>? current = null;
        foreach (string line in UnicodeFiles.Lines("properties.txt.gz"))
        {
            if (line.StartsWith('@'))
            {
                current = sections[line[1..]] = [];
            }
            else if (line.Length > 0 && !line.StartsWith('#'))
            {
                current!.Add(line);
            }
        }

        return sections;
    }
}

/// <summary>The files under tests/reference/unicode.</summary>
internal static class UnicodeFiles
{
    public static IEnumerable<string> Lines(string name)
    {
        using FileStream file = File.OpenRead(Path.Combine(TestFonts.Root, "tests", "reference", "unicode", name));
        using GZipStream gzip = new(file, CompressionMode.Decompress);
        using StreamReader reader = new(gzip);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }
}
