using System.Text.Json;

namespace Egami.Fonts.Tests;

/// <summary>Every test font read by Egami equals fontTools' reading: tables, names, character maps, metrics, outlines and kerning.</summary>
public class ReferenceTests
{
    public static TheoryData<string, int, string> Fonts => TestFonts.ReferencedData();

    [Theory]
    [MemberData(nameof(Fonts))]
    public void TablesEqualFontTools(string file, int face, string reference)
    {
        Typeface font = TestFonts.Load(file, face);
        JsonElement r = TestFonts.Reference(reference);
        Assert.Equal(r.GetProperty("glyphCount").GetInt32(), font.GlyphCount);

        JsonElement head = r.GetProperty("head");
        Assert.Equal(head.GetProperty("unitsPerEm").GetInt32(), font.UnitsPerEm);
        Assert.Equal(head.GetProperty("xMin").GetInt32(), font.Head.XMin);
        Assert.Equal(head.GetProperty("yMin").GetInt32(), font.Head.YMin);
        Assert.Equal(head.GetProperty("xMax").GetInt32(), font.Head.XMax);
        Assert.Equal(head.GetProperty("yMax").GetInt32(), font.Head.YMax);
        Assert.Equal(head.GetProperty("macStyle").GetInt32(), font.Head.MacStyle);
        Assert.Equal(head.GetProperty("indexToLocFormat").GetInt32(), font.Head.IndexToLocFormat);

        JsonElement hhea = r.GetProperty("hhea");
        Assert.Equal(new VerticalMetrics(hhea.GetProperty("ascent").GetInt32(), hhea.GetProperty("descent").GetInt32(), hhea.GetProperty("lineGap").GetInt32()), font.HheaMetrics);
        Assert.Equal(hhea.GetProperty("numberOfHMetrics").GetInt32(), font.Hhea!.NumberOfHMetrics);
        Assert.Equal(hhea.GetProperty("advanceWidthMax").GetInt32(), font.Hhea.AdvanceWidthMax);
        Assert.Equal(hhea.GetProperty("caretSlopeRise").GetInt32(), font.Hhea.CaretSlopeRise);

        JsonElement os2 = r.GetProperty("os2");
        var o = font.Os2;
        Assert.Equal(os2.GetProperty("version").GetInt32(), o.Version);
        Assert.Equal(os2.GetProperty("xAvgCharWidth").GetInt32(), o.XAvgCharWidth);
        Assert.Equal(os2.GetProperty("usWeightClass").GetInt32(), o.WeightClass);
        Assert.Equal(os2.GetProperty("usWidthClass").GetInt32(), o.WidthClass);
        Assert.Equal(os2.GetProperty("fsType").GetInt32(), o.FsType);
        Assert.Equal(os2.GetProperty("yStrikeoutSize").GetInt32(), o.StrikeoutSize);
        Assert.Equal(os2.GetProperty("yStrikeoutPosition").GetInt32(), o.StrikeoutPosition);
        Assert.Equal(os2.GetProperty("ySubscriptYOffset").GetInt32(), o.SubscriptYOffset);
        Assert.Equal(os2.GetProperty("ySuperscriptXSize").GetInt32(), o.SuperscriptXSize);
        Assert.Equal(os2.GetProperty("sFamilyClass").GetInt32(), o.FamilyClass);
        Assert.Equal(os2.GetProperty("ulUnicodeRange1").GetUInt32(), o.UnicodeRange1);
        Assert.Equal(os2.GetProperty("ulUnicodeRange4").GetUInt32(), o.UnicodeRange4);
        Assert.Equal(os2.GetProperty("achVendID").GetString(), o.VendorId.ToString().TrimEnd('\0'));
        Assert.Equal(os2.GetProperty("fsSelection").GetInt32(), o.FsSelection);
        Assert.Equal(os2.GetProperty("usFirstCharIndex").GetInt32(), o.FirstCharIndex);
        Assert.Equal(os2.GetProperty("usLastCharIndex").GetInt32(), o.LastCharIndex);
        Assert.Equal(os2.GetProperty("sTypoAscender").GetInt32(), o.TypoAscender);
        Assert.Equal(os2.GetProperty("sTypoDescender").GetInt32(), o.TypoDescender);
        Assert.Equal(os2.GetProperty("sTypoLineGap").GetInt32(), o.TypoLineGap);
        Assert.Equal(os2.GetProperty("usWinAscent").GetInt32(), o.WinAscent);
        Assert.Equal(os2.GetProperty("usWinDescent").GetInt32(), o.WinDescent);
        if (o.Version >= 1)
        {
            Assert.Equal(os2.GetProperty("ulCodePageRange1").GetUInt32(), o.CodePageRange1);
        }

        if (o.Version >= 2)
        {
            Assert.Equal(os2.GetProperty("sxHeight").GetInt32(), o.XHeight);
            Assert.Equal(os2.GetProperty("sCapHeight").GetInt32(), o.CapHeight);
            Assert.Equal(os2.GetProperty("usDefaultChar").GetInt32(), o.DefaultChar);
            Assert.Equal(os2.GetProperty("usMaxContext").GetInt32(), o.MaxContext);
        }

        Assert.Equal((FontEmbedding)(o.FsType & 0x30E), font.Embedding);

        JsonElement post = r.GetProperty("post");
        Assert.Equal(post.GetProperty("italicAngle").GetDouble(), font.Post.ItalicAngle, 4);
        Assert.Equal(post.GetProperty("underlinePosition").GetInt32(), font.Post.UnderlinePosition);
        Assert.Equal(post.GetProperty("underlineThickness").GetInt32(), font.Post.UnderlineThickness);
        Assert.Equal(post.GetProperty("isFixedPitch").GetInt32() != 0, font.IsFixedPitch);
    }

    [Theory]
    [MemberData(nameof(Fonts))]
    public void NamesEqualFontTools(string file, int face, string reference)
    {
        Typeface font = TestFonts.Load(file, face);
        JsonElement r = TestFonts.Reference(reference);
        var expected = new List<FontNameRecord>();
        foreach (JsonElement n in r.GetProperty("names").EnumerateArray())
        {
            var record = new FontNameRecord(n[0].GetInt32(), n[1].GetInt32(), n[2].GetInt32(), n[3].GetInt32(), n[4].GetString()!);
            if (record.PlatformId == 0 || (record.PlatformId == 3 && record.EncodingId is 0 or 1 or 10) || (record.PlatformId == 1 && record.EncodingId == 0))
            {
                expected.Add(record);
            }
        }

        Assert.Equal(expected, font.GetNames());
        Assert.False(string.IsNullOrEmpty(font.FamilyName));
        Assert.NotEqual("Unknown", font.FamilyName);
    }

    [Theory]
    [MemberData(nameof(Fonts))]
    public void CharacterMapEqualsFontTools(string file, int face, string reference)
    {
        Typeface font = TestFonts.Load(file, face);
        JsonElement r = TestFonts.Reference(reference);
        JsonElement? chosen = null;
        foreach (JsonElement table in r.GetProperty("cmap").EnumerateArray())
        {
            if (table.GetProperty("platform").GetInt32() == font.Cmap.PlatformId && table.GetProperty("encoding").GetInt32() == font.Cmap.EncodingId && table.TryGetProperty("map", out _))
            {
                chosen = table;
                break;
            }
        }

        Assert.True(chosen.HasValue, "The reference has the subtable Egami chose.");
        var expected = new SortedDictionary<int, int>();
        foreach (JsonElement pair in chosen.Value.GetProperty("map").EnumerateArray())
        {
            expected[pair[0].GetInt32()] = pair[1].GetInt32();
        }

        var actual = new SortedDictionary<int, int>();
        if (font.Cmap.Kind == Tables.CmapKind.MacRoman)
        {
            // fontTools keeps Mac Roman codes; compare through the translation.
            foreach ((int code, int glyph) in expected)
            {
                int cp = Tables.MacRoman.ToChar((byte)code);
                Assert.Equal(glyph, font.GetGlyph(cp));
            }

            return;
        }

        font.Cmap.Enumerate((cp, glyph) => actual[cp] = glyph);
        Assert.Equal(expected, actual);
        foreach ((int cp, int glyph) in expected)
        {
            Assert.Equal(glyph, font.GetGlyph(cp));
        }

        if (font.Cmap.Kind == Tables.CmapKind.Symbol)
        {
            Assert.Equal(font.GetGlyph(0xF041), font.GetGlyph('A'));
            Assert.NotEqual(0, font.GetGlyph('A'));
        }
    }

    [Theory]
    [MemberData(nameof(Fonts))]
    public void MetricsEqualFontTools(string file, int face, string reference)
    {
        Typeface font = TestFonts.Load(file, face);
        JsonElement r = TestFonts.Reference(reference);
        int g = 0;
        foreach (JsonElement glyph in r.GetProperty("glyphs").EnumerateArray())
        {
            Assert.Equal(glyph.GetProperty("advance").GetDouble(), font.GetAdvance((ushort)g));
            Assert.Equal(glyph.GetProperty("lsb").GetDouble(), font.GetLeftSideBearing((ushort)g));
            GlyphExtents e = font.GetGlyphExtents((ushort)g);
            JsonElement bounds = glyph.GetProperty("bounds");
            if (bounds.ValueKind == JsonValueKind.Null)
            {
                Assert.True(e.IsEmpty || (e.Width == 0 && e.Height == 0), $"glyph {g} should have no ink");
            }
            else
            {
                Assert.Equal(bounds[0].GetDouble(), e.XMin, 0.01);
                Assert.Equal(bounds[1].GetDouble(), e.YMin, 0.01);
                Assert.Equal(bounds[2].GetDouble(), e.XMax, 0.01);
                Assert.Equal(bounds[3].GetDouble(), e.YMax, 0.01);
            }

            g++;
        }
    }

    [Theory]
    [MemberData(nameof(Fonts))]
    public void OutlinesEqualFontTools(string file, int face, string reference)
    {
        Typeface font = TestFonts.Load(file, face);
        JsonElement r = TestFonts.Reference(reference);
        int g = 0;
        foreach (JsonElement glyph in r.GetProperty("glyphs").EnumerateArray())
        {
            var sink = new TextSink();
            Assert.True(font.GetGlyphOutline((ushort)g, sink));
            AssertOutline(glyph.GetProperty("outline"), sink, g);
            g++;
        }
    }

    [Theory]
    [MemberData(nameof(Fonts))]
    public void KerningEqualsFontTools(string file, int face, string reference)
    {
        Typeface font = TestFonts.Load(file, face);
        JsonElement r = TestFonts.Reference(reference);
        var expected = new Dictionary<(int, int), int>();
        foreach (JsonElement pair in r.GetProperty("kerning").EnumerateArray())
        {
            expected[(pair[0].GetInt32(), pair[1].GetInt32())] = pair[2].GetInt32();
        }

        var sample = r.GetProperty("kerningSample").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        foreach (int left in sample)
        {
            foreach (int right in sample)
            {
                int want = expected.GetValueOrDefault((left, right));
                Assert.True(want == (int)font.GetKerning((ushort)left, (ushort)right), $"pair {left},{right}: fontTools {want}, Egami {font.GetKerning((ushort)left, (ushort)right)}");
            }
        }
    }

    [Theory]
    [InlineData("derived/SourceSans3.cff", "SourceSans3")]
    [InlineData("derived/NotoSansCJK-subset.cff", "NotoSansCJK-subset")]
    public void BareCffEqualsFontTools(string file, string reference)
    {
        Typeface font = Typeface.FromCff(TestFonts.Bytes(file));
        JsonElement r = TestFonts.Reference(reference + ".cff");
        Assert.Equal(r.GetProperty("glyphCount").GetInt32(), font.GlyphCount);
        Assert.Equal(FontOutlines.Cff, font.Outlines);
        Assert.Equal(r.GetProperty("name").GetString(), font.PostScriptName);
        Assert.False(font.HasCharacterMap);
        int g = 0;
        foreach (JsonElement glyph in r.GetProperty("glyphs").EnumerateArray())
        {
            Assert.Equal(glyph.GetProperty("advance").GetDouble(), font.GetAdvance((ushort)g), 3);
            var sink = new TextSink();
            Assert.True(font.GetGlyphOutline((ushort)g, sink));
            AssertOutline(glyph.GetProperty("outline"), sink, g);
            g++;
        }
    }

    [Fact]
    public void StandardStringsEqualFontTools()
    {
        string[] expected = [.. TestFonts.Reference("cff-standard-strings").GetProperty("strings").EnumerateArray().Select(e => e.GetString()!)];
        Assert.Equal(Cff.CffStandard.StandardStringCount, expected.Length);
        Assert.Equal(expected.Length, Cff.CffStandard.StringTableLength);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], Cff.CffStandard.GetStandardString(i));
        }
    }

    private static void AssertOutline(JsonElement expected, TextSink actual, int glyph)
    {
        var ops = expected.EnumerateArray().ToArray();
        Assert.True(ops.Length == actual.Verbs.Count, $"glyph {glyph}: fontTools has {ops.Length} ops, Egami {actual.Verbs.Count}: {Describe(ops)} vs {Describe(actual)}");
        for (int i = 0; i < ops.Length; i++)
        {
            string verb = ops[i][0].GetString()!;
            Assert.True(verb[0] == actual.Verbs[i], $"glyph {glyph} op {i}: fontTools {verb}, Egami {actual.Verbs[i]}");
            for (int k = 1; k < ops[i].GetArrayLength(); k++)
            {
                double want = ops[i][k].GetDouble(), got = actual.Ops[i][k - 1];
                Assert.True(Math.Abs(want - got) <= 1e-3, $"glyph {glyph} op {i} value {k}: fontTools {want}, Egami {got}");
            }
        }
    }

    private static string Describe(JsonElement[] ops) => string.Join(" ", ops.Take(12).Select(o => o.ToString()));

    private static string Describe(TextSink sink) => string.Join(" ", sink.Verbs.Take(12).Zip(sink.Ops).Select(p => p.First + "[" + string.Join(",", p.Second) + "]"));
}
