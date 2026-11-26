using System.Buffers.Binary;

using Egami.Fonts.Shaping;

namespace Egami.Fonts.Tests;

/// <summary>
/// SubsetOptions.LayoutClosure with GDEF, GSUB and GPOS cut down to the kept glyphs: text shapes with the subset exactly as with
/// the full face (glyph ids through the glyph map, positions and clusters), in many scripts, with dense and retained ids.
/// </summary>
public sealed class SubsetLayoutShapingTests
{
    public static TheoryData<string, string, bool> Texts()
    {
        var data = new TheoryData<string, string, bool>();
        (string Font, string Text)[] cases =
        [
            ("NotoSans-Regular.ttf", "office fjord Wave AVATAR Tiếng Việt ǅemal ﬁ 1/2 Ǻ́ q̃"),
            ("NotoSerif-Regular.ttf", "Typography ffl, To Yo P. Ŧ"),
            ("DejaVuSans.ttf", "ạ́ b̈ ffi ŋ́ Ω"),
            ("Roboto-Regular.ttf", "Kerning AWAY 0123 office"),
            ("SourceSans3-Regular.otf", "office fjord Typography 0123 TeX"),
            ("SourceSerif4-Regular.otf", "ὶ é Th ffi"),
            ("shaping/NotoSansArabic-Regular.ttf", "بِسْمِ اللّهِ لا كتاب"),
            ("shaping/Amiri-Regular.ttf", "بِسْمِ اللّهِ الرَّحْمَنِ لا محمد"),
            ("shaping/ScheherazadeNew-Regular.ttf", "كتاب لله في هذا"),
            ("shaping/NotoSansHebrew-Regular.ttf", "שָׁלוֹם בְּ"),
            ("shaping/NotoSansDevanagari-Regular.ttf", "नमस्ते क्षत्रिय हिन्दी र्क"),
            ("shaping/Mukta-Regular.ttf", "श्री द्वार कि"),
            ("shaping/NotoSansBengali-Regular.ttf", "বাংলা ক্ষ র্ক"),
            ("shaping/NotoSansTamil-Regular.ttf", "தமிழ் கொ"),
            ("shaping/NotoSansSinhala-Regular.ttf", "සිංහල ක්‍ර"),
            ("shaping/NotoSansThai-Regular.ttf", "ภาษาไทย ปู่ กำ"),
            ("shaping/NotoSansLao-Regular.ttf", "ພາສາລາວ ກຳ"),
            ("shaping/NotoSansKhmer-Regular.ttf", "ភាសាខ្មែរ ក្រ"),
            ("shaping/NotoSansMyanmar-Regular.ttf", "မြန်မာ ကျော်"),
        ];
        foreach ((string font, string text) in cases)
        {
            data.Add(font, text, false);
            data.Add(font, text, true);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Texts))]
    public void TheSubsetShapesAsTheFullFace(string file, string text, bool retain)
    {
        Typeface face = TestFonts.Load(file);
        AssertShapesAlike(face, text, new SubsetOptions { LayoutClosure = true, RetainGlyphIds = retain });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CjkAndEmojiSubsetsShapeAsTheFullFace(bool retain)
    {
        foreach (string file in (string[])["derived/NotoSansCJK-layout-subset.otf", "derived/Twemoji-COLRv0-subset.ttf"])
        {
            Typeface face = TestFonts.Load(file);
            List<int> codes = [];
            face.Cmap.Enumerate((cp, glyph) =>
            {
                if (glyph != 0 && cp > 0x2000 && codes.Count < 40)
                {
                    codes.Add(cp);
                }
            });
            Assert.NotEmpty(codes);
            string text = string.Concat(codes.Select(char.ConvertFromUtf32));
            AssertShapesAlike(face, text, new SubsetOptions { LayoutClosure = true, RetainGlyphIds = retain });
        }
    }

    [Fact]
    public void DenseIdsCutTheTablesDown()
    {
        Typeface face = TestFonts.Load("NotoSans-Regular.ttf");
        FontSubset subset = Subset(face, "office", new SubsetOptions { LayoutClosure = true });
        Typeface written = subset.ToTypeface();

        Assert.True(subset.Glyphs.Count < 20, $"Dense ids: {subset.Glyphs.Count} glyphs.");
        foreach (FontTag tag in (FontTag[])[new("GSUB"), new("GPOS"), new("GDEF")])
        {
            Assert.True(face.TryGetTable(tag, out ReadOnlyMemory<byte> full));
            Assert.True(written.TryGetTable(tag, out ReadOnlyMemory<byte> cut));
            Assert.True(cut.Length * 4 < full.Length, $"{tag}: {cut.Length} of {full.Length} bytes.");
        }

        Assert.Equal(face.Layout.Scripts, written.Layout.Scripts);
        Assert.False(written.TryGetTable(new FontTag("MATH"), out _));
    }

    [Fact]
    public void RetainedIdsAlsoCutTheTablesDownButKeepMath()
    {
        Typeface face = TestFonts.Load("DejaVuSans.ttf");
        FontSubset subset = Subset(face, "(office", new SubsetOptions { LayoutClosure = true, RetainGlyphIds = true });
        Typeface written = subset.ToTypeface();

        Assert.True(face.TryGetTable(new FontTag("GPOS"), out ReadOnlyMemory<byte> full));
        Assert.True(written.TryGetTable(new FontTag("GPOS"), out ReadOnlyMemory<byte> cut));
        Assert.True(cut.Length < full.Length);
        Assert.NotNull(written.Math);
    }

    [Fact]
    public void LayoutFeaturesKeepsMoreFeatures()
    {
        Typeface face = TestFonts.Load("SourceSans3-Regular.otf");
        const string text = "Small 0123";
        FontFeature[] features = [FontFeature.SmallCaps, new FontFeature(new FontTag("onum"))];

        // By default the closure and the tables leave smcp and onum out.
        Typeface plain = Subset(face, text, new SubsetOptions { LayoutClosure = true }).ToTypeface();
        Assert.DoesNotContain(new FontTag("smcp"), plain.Layout.Features);
        Assert.Contains(new FontTag("liga"), plain.Layout.Features);

        var options = new SubsetOptions
        {
            LayoutClosure = true,
            LayoutFeatures = [.. OpenTypeLayout.DefaultClosureFeatures, new FontTag("smcp"), new FontTag("onum")],
        };
        FontSubset subset = Subset(face, text, options);
        Typeface written = subset.ToTypeface();
        Assert.Contains(new FontTag("smcp"), written.Layout.Features);
        Assert.Contains(new FontTag("onum"), written.Layout.Features);
        AssertSame(Shape(face, text, features), Shape(written, text, features), subset);
    }

    [Theory]
    [InlineData("SourceSans3-Regular.otf", "office fjord AVATAR Typography 0123 Tiếng Việt")]
    [InlineData("shaping/Amiri-Regular.ttf", "بِسْمِ اللّهِ في كتاب")]
    public void EveryGlyphKeptShapesAsTheFullFace(string file, string text)
    {
        Typeface face = TestFonts.Load(file);
        FontSubset subset = FontSubsetter.Subset(face, Enumerable.Range(0, face.GlyphCount).Select(g => (ushort)g), new SubsetOptions { LayoutClosure = true });
        AssertSame(Shape(face, text), Shape(subset.ToTypeface(), text), subset);
    }

    [Theory]
    [InlineData("SourceSans3-Regular.otf", "office fjord AVATAR Typography 0123")]
    [InlineData("shaping/Amiri-Regular.ttf", "بِسْمِ اللّهِ في كتاب")]
    [InlineData("shaping/NotoSansDevanagari-Regular.ttf", "नमस्ते क्षत्रिय")]
    public void ExtensionLookupsShapeAlike(string file, string text)
    {
        // What the writer falls back to when 16-bit offsets overflow: every lookup an extension lookup, each subtable packed alone.
        Typeface face = TestFonts.Load(file);
        FontSubset subset = Subset(face, text, new SubsetOptions { LayoutClosure = true, ExtensionLookups = true });
        Typeface written = subset.ToTypeface();
        foreach ((string tag, int type) in (ReadOnlySpan<(string, int)>)[("GSUB", 7), ("GPOS", 9)])
        {
            Assert.True(written.TryGetTable(new FontTag(tag), out ReadOnlyMemory<byte> table));
            ReadOnlySpan<byte> d = table.Span;
            int lookupList = BinaryPrimitives.ReadUInt16BigEndian(d[8..]);
            int count = BinaryPrimitives.ReadUInt16BigEndian(d[lookupList..]);
            Assert.True(count > 0);
            for (int i = 0; i < count; i++)
            {
                int lookup = lookupList + BinaryPrimitives.ReadUInt16BigEndian(d[(lookupList + 2 + (i * 2))..]);
                Assert.Equal(type, BinaryPrimitives.ReadUInt16BigEndian(d[lookup..]));
            }
        }

        AssertSame(Shape(face, text), Shape(written, text), subset);
    }

    [Fact]
    public void ADamagedLayoutTableThrows()
    {
        byte[] data = TestFonts.Bytes("NotoSans-Regular.ttf");
        Typeface face = Typeface.FromMemory(data);
        Assert.True(face.TryGetTable(new FontTag("GSUB"), out _));
        face.GetTableSpan(new FontTag("GSUB"), out int gsub);

        // The script list claims 65535 scripts.
        int scriptList = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(gsub + 4));
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(gsub + scriptList), 0xFFFF);
        Typeface damaged = Typeface.FromMemory(data);
        ushort[] glyphs = [damaged.GetGlyph('o')];

        Assert.Throws<CorruptDataException>(() => FontSubsetter.Subset(damaged, glyphs, new SubsetOptions { LayoutClosure = true }));
        Assert.NotNull(FontSubsetter.Subset(damaged, glyphs));
    }

    private static void AssertShapesAlike(Typeface face, string text, SubsetOptions options)
    {
        FontSubset subset = Subset(face, text, options);
        AssertSame(Shape(face, text), Shape(subset.ToTypeface(), text), subset);
    }

    private static FontSubset Subset(Typeface face, string text, SubsetOptions options) =>
        FontSubsetter.Subset(face, FontSubsetter.GlyphsFor(face, text.EnumerateRunes().Select(r => r.Value)), options);

    private static GlyphBuffer Shape(Typeface face, string text, IReadOnlyList<FontFeature>? features = null)
    {
        GlyphBuffer buffer = new();
        TextShaper.ShapeRun(text, 0, text.Length, new Font(face, face.UnitsPerEm), new RunProperties { Features = features }, buffer);
        return buffer;
    }

    private static void AssertSame(GlyphBuffer full, GlyphBuffer cut, FontSubset subset)
    {
        ushort[] expected = [.. full.GlyphIds.ToArray().Select(g => subset.GlyphMap.TryGetValue(g, out ushort now) ? now : (ushort)0xFFFF)];
        Assert.DoesNotContain((ushort)0xFFFF, expected);
        Assert.Equal(expected, cut.GlyphIds.ToArray());
        Assert.Equal(full.Positions.ToArray(), cut.Positions.ToArray());
        Assert.Equal(full.Clusters.ToArray(), cut.Clusters.ToArray());
    }
}
