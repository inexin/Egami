using System.Buffers.Binary;

using Egami.Fonts.Shaping;
using Egami.Fonts.Unicode;

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
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MathIsCutAndRenumbered(bool retain)
    {
        Typeface face = TestFonts.Load("DejaVuSans.ttf");
        FontSubset subset = Subset(face, "(office\u2211\u221A)[]{}|", new SubsetOptions { LayoutClosure = true, RetainGlyphIds = retain });
        Typeface written = subset.ToTypeface();

        Assert.True(face.TryGetTable(new FontTag("GPOS"), out ReadOnlyMemory<byte> full));
        Assert.True(written.TryGetTable(new FontTag("GPOS"), out ReadOnlyMemory<byte> cut));
        Assert.True(cut.Length < full.Length);
        Assert.True(face.TryGetTable(new FontTag("MATH"), out ReadOnlyMemory<byte> fullMath));
        Assert.True(written.TryGetTable(new FontTag("MATH"), out ReadOnlyMemory<byte> cutMath));
        Assert.True(cutMath.Length * 2 < fullMath.Length, $"MATH: {cutMath.Length} of {fullMath.Length} bytes.");

        TypefaceMath before = face.Math!;
        TypefaceMath after = written.Math!;
        Assert.Equal(before.Constants, after.Constants);
        Assert.Equal(before.MinConnectorOverlap, after.MinConnectorOverlap);
        int constructions = 0;
        foreach ((ushort old, ushort now) in subset.GlyphMap)
        {
            Assert.Equal(before.GetItalicsCorrection(old), after.GetItalicsCorrection(now));
            Assert.Equal(before.GetTopAccentAttachment(old), after.GetTopAccentAttachment(now));
            Assert.Equal(before.IsExtendedShape(old), after.IsExtendedShape(now));
            foreach (MathKernCorner corner in Enum.GetValues<MathKernCorner>())
            {
                Assert.Equal(before.GetKerning(old, corner, 300), after.GetKerning(now, corner, 300));
            }

            foreach (MathDirection direction in Enum.GetValues<MathDirection>())
            {
                MathGlyphVariant[] kept = [.. before.GetVariants(old, direction).Where(v => subset.GlyphMap.ContainsKey(v.Glyph)).Select(v => v with { Glyph = subset.GlyphMap[v.Glyph] })];
                Assert.Equal(kept, after.GetVariants(now, direction));
                MathGlyphAssembly? a = before.GetAssembly(old, direction);
                MathGlyphAssembly? b = after.GetAssembly(now, direction);
                if (a is not null && a.Parts.All(part => subset.GlyphMap.ContainsKey(part.Glyph)))
                {
                    Assert.NotNull(b);
                    Assert.Equal(a.ItalicsCorrection, b.ItalicsCorrection);
                    Assert.Equal(a.Parts.Select(part => part with { Glyph = subset.GlyphMap[part.Glyph] }), b.Parts);
                }
                else
                {
                    Assert.Null(b);
                }

                constructions += kept.Length + (b is null ? 0 : 1);
            }
        }

        Assert.True(constructions > 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyKernIsCutAndRenumbered(bool retain)
    {
        Typeface face = TestFonts.Load("derived/NotoSans-kern.ttf");
        Assert.True(face.TryGetTable(new FontTag("kern"), out ReadOnlyMemory<byte> full));
        const string text = "AVATAR Wave To. LT";
        FontSubset subset = Subset(face, text, new SubsetOptions { LayoutClosure = true, RetainGlyphIds = retain });
        Typeface written = subset.ToTypeface();
        Assert.True(written.TryGetTable(new FontTag("kern"), out ReadOnlyMemory<byte> cut));
        Assert.True(cut.Length * 4 < full.Length, $"kern: {cut.Length} of {full.Length} bytes.");
        AssertSame(Shape(face, text), Shape(written, text), subset);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VerticalMetricsAreKept(bool retain)
    {
        Typeface face = TestFonts.Load("derived/NotoSansCJK-layout-subset.otf");
        Assert.True(face.TryGetTable(new FontTag("vmtx"), out _));
        string text = Text(face, 0x3000, 30);
        FontSubset subset = Subset(face, text, new SubsetOptions { LayoutClosure = true, RetainGlyphIds = retain });
        Typeface written = subset.ToTypeface();
        foreach (string tag in (string[])["vhea", "vmtx"])
        {
            Assert.True(written.TryGetTable(new FontTag(tag), out _), tag);
        }

        AssertSame(Shape(face, text, direction: TextDirection.TopToBottom), Shape(written, text, direction: TextDirection.TopToBottom), subset);
    }

    [Theory]
    [InlineData("derived/NotoSans-VF-subset.ttf", false)]
    [InlineData("derived/NotoSans-VF-subset.ttf", true)]
    [InlineData("derived/SourceSans3-VF-subset.otf", false)]
    [InlineData("derived/NotoSerif-VF-subset.ttf", false)]
    public void AVariableFaceSubsetShapesAsTheFaceAtItsPosition(string file, bool retain)
    {
        Typeface face = TestFonts.Load(file).WithVariations(new FontVariation("wght", 730));
        string text = Text(face, 0x20, 80);
        FontSubset subset = Subset(face, text, new SubsetOptions { LayoutClosure = true, RetainGlyphIds = retain });
        Typeface written = subset.ToTypeface();
        Assert.False(written.IsVariable);
        if (written.TryGetTable(new FontTag("GDEF"), out ReadOnlyMemory<byte> gdef))
        {
            Assert.True(BinaryPrimitives.ReadUInt16BigEndian(gdef.Span[2..]) <= 2, "GDEF keeps no variation store.");
        }

        AssertSame(Shape(face, text), Shape(written, text), subset);
    }

    [Fact]
    public void VariationSequencesAreKept()
    {
        Typeface face = TestFonts.Load("harfbuzz/in-house/fonts/6991b13ce889466be6de3f66e891de2bc0f117ee.ttf");
        Assert.NotNull(face.Cmap.Variations);
        (int code, int selector, int glyph) = face.Cmap.Variations.Records
            .SelectMany(r => r.Mappings.Select(m => (m.Code, r.Selector, m.Glyph)))
            .First(m => m.Glyph != face.GetGlyph(m.Code));
        FontSubset subset = FontSubsetter.Subset(face, FontSubsetter.GlyphsFor(face, [code, selector]), new SubsetOptions { LayoutClosure = true });
        Typeface written = subset.ToTypeface();
        Assert.Equal(subset.GlyphMap[(ushort)glyph], written.GetGlyph(code, selector));
        Assert.Equal(subset.GlyphMap.GetValueOrDefault(face.GetGlyph(code)), written.GetGlyph(code));
    }

    [Fact]
    public void FeaturesAndLanguageSystemsLeftEmptyAreLeftOut()
    {
        Typeface face = TestFonts.Load("NotoSans-Regular.ttf");
        FontSubset subset = Subset(face, "Hello", new SubsetOptions { LayoutClosure = true });
        Typeface written = subset.ToTypeface();

        // Each feature left keeps a lookup, or is one a shaper asks for by name.
        string[] presence = ["frac", "numr", "dnom", "rtlm", "vert", "kern", "vkrn", "mark", "isol", "fina", "fin2", "fin3", "medi", "med2", "init", "rlig", "rclt", "stch", "pref"];
        foreach (FontTag tag in (ReadOnlySpan<FontTag>)[new("GSUB"), new("GPOS")])
        {
            Assert.True(written.TryGetTable(tag, out ReadOnlyMemory<byte> data));
            var table = new LayoutTable(data, tag == new FontTag("GPOS"));
            for (int i = 0; i < table.FeatureCount; i++)
            {
                string feature = new FontTag(table.GetFeatureTag(i)).ToString();
                Assert.True(table.GetFeatureLookups(i, -1).Length > 0 || presence.Contains(feature), $"{tag} {feature}");
            }
        }

        // A language system the same as its script's default after the cut is left out: the shaper falls back to the default.
        Assert.True(written.Layout.Features.Count < face.Layout.Features.Count);
        int Languages(OpenTypeLayout layout) => layout.Scripts.Sum(s => layout.GetLanguageSystems(s).Count);
        Assert.True(Languages(written.Layout) < Languages(face.Layout));
        AssertSame(Shape(face, "Hello"), Shape(written, "Hello"), subset);
    }

    [Fact]
    public void ArabicPresentationFormsAreAddedOnlyForFallbackShaping()
    {
        // The face shapes Arabic through GSUB (it has init, medi and fina but no isol), so the shaper never draws a presentation
        // form in place of lam and alef.
        Typeface face = TestFonts.Load("shaping/NotoSansArabic-Regular.ttf");
        Assert.True(face.TryGetGlyph(0xFEFB, out ushort ligature) && ligature != 0);
        IReadOnlyList<ushort> glyphs = FontSubsetter.GlyphsFor(face, [0x0644, 0x0627]);
        Assert.Equal([.. new[] { face.GetGlyph(0x0644), face.GetGlyph(0x0627) }.Order()], glyphs);
    }

    [Fact]
    public void CharactersLimitTheCharacterMap()
    {
        Typeface face = TestFonts.Load("NotoSans-Regular.ttf");
        int[] text = [.. "(office)".Select(c => (int)c)];
        var options = new SubsetOptions { LayoutClosure = true, Characters = text };
        FontSubset subset = FontSubsetter.Subset(face, FontSubsetter.GlyphsFor(face, text), options);
        Typeface written = subset.ToTypeface();
        Typeface everything = FontSubsetter.Subset(face, FontSubsetter.GlyphsFor(face, text), options with { Characters = null }).ToTypeface();

        // The fi and ffi ligature characters are left out, though their glyphs are kept; the text's characters stay.
        Assert.True(everything.TryGetGlyph(0xFB01, out ushort fi) && fi != 0);
        Assert.False(written.TryGetGlyph(0xFB01, out fi) && fi != 0);
        Assert.All(text, c => Assert.True(written.TryGetGlyph(c, out ushort g) && g != 0));
        AssertSame(Shape(face, "(office)"), Shape(written, "(office)"), subset);
        AssertSame(Shape(face, "(office)", direction: TextDirection.RightToLeft), Shape(written, "(office)", direction: TextDirection.RightToLeft), subset);
    }

    [Fact]
    public void LayoutFeaturesKeepsMoreFeatures()
    {
        Typeface face = TestFonts.Load("SourceSans3-Regular.otf");
        const string text = "Small 0123";
        FontFeature[] features = [FontFeature.SmallCaps, new FontFeature(new FontTag("onum"))];

        // By default the closure and the tables leave smcp and onum out, and liga too, as no ligature of the text is left.
        Typeface plain = Subset(face, text, new SubsetOptions { LayoutClosure = true }).ToTypeface();
        Assert.DoesNotContain(new FontTag("smcp"), plain.Layout.Features);
        Assert.DoesNotContain(new FontTag("liga"), plain.Layout.Features);
        Assert.Contains(new FontTag("kern"), plain.Layout.Features);

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

    [Fact]
    public void ATableOfAnotherMajorVersionIsLeftOut()
    {
        // The shaper reads a GDEF of major version 2 as empty, so the subset has none.
        byte[] data = TestFonts.Bytes("NotoSans-Regular.ttf");
        Typeface.FromMemory(data).GetTableSpan(new FontTag("GDEF"), out int gdef);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(gdef), 2);
        Typeface face = Typeface.FromMemory(data);
        FontSubset subset = Subset(face, "office", new SubsetOptions { LayoutClosure = true });
        Assert.False(subset.ToTypeface().TryGetTable(new FontTag("GDEF"), out _));
        AssertSame(Shape(face, "office"), Shape(subset.ToTypeface(), "office"), subset);
    }

    private static void AssertShapesAlike(Typeface face, string text, SubsetOptions options)
    {
        FontSubset subset = Subset(face, text, options);
        AssertSame(Shape(face, text), Shape(subset.ToTypeface(), text), subset);
    }

    private static FontSubset Subset(Typeface face, string text, SubsetOptions options) =>
        FontSubsetter.Subset(face, FontSubsetter.GlyphsFor(face, text.EnumerateRunes().Select(r => r.Value)), options);

    private static GlyphBuffer Shape(Typeface face, string text, IReadOnlyList<FontFeature>? features = null, TextDirection direction = TextDirection.Auto)
    {
        GlyphBuffer buffer = new();
        TextShaper.ShapeRun(text, 0, text.Length, new Font(face, face.UnitsPerEm), new RunProperties { Features = features, Direction = direction }, buffer);
        return buffer;
    }

    /// <summary>Up to <paramref name="count"/> characters the face maps, from <paramref name="first"/> on.</summary>
    private static string Text(Typeface face, int first, int count)
    {
        List<int> codes = [];
        face.Cmap.Enumerate((cp, glyph) =>
        {
            if (glyph != 0 && cp >= first && codes.Count < count)
            {
                codes.Add(cp);
            }
        });
        Assert.NotEmpty(codes);
        return string.Concat(codes.Select(char.ConvertFromUtf32));
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
