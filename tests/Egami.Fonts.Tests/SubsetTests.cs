namespace Egami.Fonts.Tests;

/// <summary>Subsets re-load with every kept glyph's outline and advance unchanged, in every format and numbering.</summary>
public class SubsetTests
{
    private const string Sample = "Hello, World! Ångström façade naïve 0123 ÆØÅ æøå ŒœŠš “quotes” — fi fl";

    public static TheoryData<string, SubsetFormat, bool> Cases => new()
    {
        { "NotoSans-Regular.ttf", SubsetFormat.TrueType, false },
        { "NotoSans-Regular.ttf", SubsetFormat.TrueType, true },
        { "DejaVuSans.ttf", SubsetFormat.TrueType, false },
        { "Roboto-Regular.ttf", SubsetFormat.Auto, false },
        { "SourceSans3-Regular.otf", SubsetFormat.OpenTypeCff, false },
        { "SourceSans3-Regular.otf", SubsetFormat.OpenTypeCff, true },
        { "SourceSans3-Regular.otf", SubsetFormat.BareCff, false },
        { "SourceSerif4-Regular.otf", SubsetFormat.Auto, false },
        { "derived/NotoSansCJK-subset.otf", SubsetFormat.OpenTypeCff, false },
        { "derived/NotoSansCJK-subset.otf", SubsetFormat.BareCff, false },
        { "derived/SourceSans3.cff", SubsetFormat.Auto, false },
        { "derived/NotoSansCJK-subset.cff", SubsetFormat.BareCff, true },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void SubsetsKeepOutlinesAndAdvances(string file, SubsetFormat format, bool retain)
    {
        Typeface face = file.EndsWith(".cff", StringComparison.Ordinal) ? Typeface.FromCff(TestFonts.Bytes(file)) : TestFonts.Load(file);
        ushort[] wanted = Wanted(face);
        var options = new SubsetOptions { Format = format, RetainGlyphIds = retain, FontNamePrefix = "ABCDEF+" };
        FontSubset subset = FontSubsetter.Subset(face, wanted, options);
        Typeface reloaded = subset.ToTypeface();
        WriteForExternalCheck(file, subset, retain);

        Assert.NotEqual(SubsetFormat.Auto, subset.Format);
        Assert.Equal(subset.Glyphs.Count, reloaded.GlyphCount);
        Assert.Contains((ushort)0, subset.GlyphMap.Keys);
        Assert.All(wanted, g => Assert.True(subset.GlyphMap.ContainsKey(g)));
        if (!retain)
        {
            Assert.Equal(subset.GlyphMap.Count, reloaded.GlyphCount);
            ushort previous = 0;
            foreach (ushort old in subset.Glyphs.Skip(1))
            {
                Assert.True(old > previous, "Dense ids follow the old order.");
                previous = old;
            }
        }
        else
        {
            Assert.All(subset.GlyphMap, p => Assert.Equal(p.Key, p.Value));
        }

        foreach ((ushort old, ushort now) in subset.GlyphMap)
        {
            var a = new TextSink();
            var b = new TextSink();
            Assert.True(face.GetGlyphOutline(old, a));
            Assert.True(reloaded.GetGlyphOutline(now, b));
            AssertSameOutline(a, b, old);
            Assert.Equal(Math.Round(face.GetAdvance(old)), reloaded.GetAdvance(now), 0.01);
        }

        if (subset.Format != SubsetFormat.BareCff && options.KeepCmap && face.HasCharacterMap)
        {
            foreach (char c in Sample)
            {
                ushort old = face.GetGlyph(c);
                Assert.Equal(subset.GlyphMap[old], reloaded.GetGlyph(c));
            }

            Assert.StartsWith("ABCDEF+", reloaded.PostScriptName, StringComparison.Ordinal);
            Assert.Equal(face.UnitsPerEm, reloaded.UnitsPerEm);
        }

        Assert.True(subset.Data.Length < (file.EndsWith(".cff", StringComparison.Ordinal) || file.Contains("CJK", StringComparison.Ordinal) ? 60_000 : face.FileData.Length / 3));
    }

    [Fact]
    public void CompositesAndSeacPullInTheirParts()
    {
        Typeface noto = TestFonts.Load("NotoSans-Regular.ttf");
        ushort aring = noto.GetGlyph('Å');
        var components = new List<int>();
        noto.Glyf!.GetComponents(aring, components);
        Assert.NotEmpty(components);
        FontSubset subset = FontSubsetter.Subset(noto, [aring]);
        Assert.All(components, c => Assert.Contains((ushort)c, subset.GlyphMap.Keys));
    }

    [Fact]
    public void TheCmapCanBeLeftOut()
    {
        Typeface noto = TestFonts.Load("NotoSans-Regular.ttf");
        FontSubset subset = FontSubsetter.Subset(noto, [noto.GetGlyph('A')], new SubsetOptions { KeepCmap = false });
        Typeface reloaded = subset.ToTypeface();
        Assert.False(reloaded.HasCharacterMap);
        Assert.False(reloaded.TryGetTable(new FontTag("cmap"), out _));
    }

    [Fact]
    public void KeptTablesAreCopied()
    {
        Typeface noto = TestFonts.Load("NotoSans-Regular.ttf");
        FontSubset subset = FontSubsetter.Subset(noto, [noto.GetGlyph('A')], new SubsetOptions { KeepTables = [new FontTag("GSUB")] });
        Assert.True(subset.ToTypeface().TryGetTable(new FontTag("GSUB"), out ReadOnlyMemory<byte> gsub));
        noto.TryGetTable(new FontTag("GSUB"), out ReadOnlyMemory<byte> original);
        Assert.True(gsub.Span.SequenceEqual(original.Span));
    }

    [Fact]
    public void WrongFormatsAndIdsThrow()
    {
        Typeface noto = TestFonts.Load("NotoSans-Regular.ttf");
        Typeface source = TestFonts.Load("SourceSans3-Regular.otf");
        Assert.Throws<ArgumentException>(() => FontSubsetter.Subset(noto, [1], new SubsetOptions { Format = SubsetFormat.BareCff }));
        Assert.Throws<ArgumentException>(() => FontSubsetter.Subset(source, [1], new SubsetOptions { Format = SubsetFormat.TrueType }));
        Assert.Throws<ArgumentException>(() => FontSubsetter.Subset(Typeface.FromCff(TestFonts.Bytes("derived/SourceSans3.cff")), [1], new SubsetOptions { Format = SubsetFormat.OpenTypeCff }));
        Assert.Throws<ArgumentOutOfRangeException>(() => FontSubsetter.Subset(noto, [(ushort)noto.GlyphCount]));
    }

    [Fact]
    public void AnEmptyRequestKeepsNotdef()
    {
        FontSubset subset = FontSubsetter.Subset(TestFonts.Load("SourceSans3-Regular.otf"), []);
        Assert.Equal(1, subset.ToTypeface().GlyphCount);
        Assert.Equal([(ushort)0], subset.Glyphs);
    }

    [Fact]
    public void ThePrefixGoesOnTheCffName()
    {
        FontSubset subset = FontSubsetter.Subset(TestFonts.Load("SourceSans3-Regular.otf"), [5], new SubsetOptions { Format = SubsetFormat.BareCff, FontNamePrefix = "XYZABC+" });
        Assert.Equal("XYZABC+SourceSans3-Regular", Typeface.FromCff(subset.Data).PostScriptName);
    }

    internal static ushort[] Wanted(Typeface face)
    {
        if (!face.HasCharacterMap)
        {
            // Bare CFF: a spread of glyph ids.
            return [.. Enumerable.Range(1, Math.Min(face.GlyphCount - 1, 400)).Where(g => g % 7 == 0).Select(g => (ushort)g)];
        }

        var glyphs = new HashSet<ushort>();
        foreach (char c in Sample + "的一是不了在人有我他")
        {
            glyphs.Add(face.GetGlyph(c));
        }

        return [.. glyphs];
    }

    private static void AssertSameOutline(TextSink a, TextSink b, int glyph)
    {
        Assert.True(a.Verbs.Count == b.Verbs.Count, $"glyph {glyph}: {a.Verbs.Count} verbs before, {b.Verbs.Count} after");
        for (int i = 0; i < a.Verbs.Count; i++)
        {
            Assert.Equal(a.Verbs[i], b.Verbs[i]);
            for (int k = 0; k < a.Ops[i].Length; k++)
            {
                Assert.True(Math.Abs(a.Ops[i][k] - b.Ops[i][k]) <= 1e-3, $"glyph {glyph} op {i}: {a.Ops[i][k]} before, {b.Ops[i][k]} after");
            }
        }
    }

    private static void WriteForExternalCheck(string file, FontSubset subset, bool retain)
    {
        string dir = Path.Combine(TestFonts.Root, ".cache", "font-subsets");
        Directory.CreateDirectory(dir);
        string ext = subset.Format switch
        {
            SubsetFormat.TrueType => ".ttf",
            SubsetFormat.OpenTypeCff => ".otf",
            _ => ".cff",
        };
        string name = Path.GetFileNameWithoutExtension(file) + "-" + subset.Format + (retain ? "-retain" : string.Empty) + ext;
        File.WriteAllBytes(Path.Combine(dir, name), subset.Data.ToArray());
    }
}
