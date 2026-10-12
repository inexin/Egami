using System.Buffers.Binary;

namespace Egami.Fonts.Tests;

/// <summary>Probing, collections, lenient defaults with diagnostics, strict mode and the documented exceptions.</summary>
public class LoadingTests
{
    [Fact]
    public void ProbeTellsTheKinds()
    {
        Assert.Equal(new FontProbe(FontFileKind.Sfnt, 1, FontOutlines.TrueType), FontLoader.Probe(TestFonts.Bytes("NotoSans-Regular.ttf")));
        Assert.Equal(new FontProbe(FontFileKind.Sfnt, 1, FontOutlines.Cff), FontLoader.Probe(TestFonts.Bytes("SourceSans3-Regular.otf")));
        Assert.Equal(new FontProbe(FontFileKind.Collection, 2, FontOutlines.TrueType), FontLoader.Probe(TestFonts.Bytes("derived/NotoSansSerif.ttc")));
        Assert.Equal(FontFileKind.Cff, FontLoader.Probe(TestFonts.Bytes("derived/SourceSans3.cff")).Kind);
        Assert.Equal(FontFileKind.Woff2, FontLoader.Probe("wOF2\0\0\0\0"u8).Kind);
        Assert.Equal(FontFileKind.Unknown, FontLoader.Probe("\x89PNG\r\n"u8).Kind);
        Assert.Equal(FontFileKind.Unknown, FontLoader.Probe([]).Kind);
    }

    [Fact]
    public void CollectionsLoadEveryFaceSharingMemory()
    {
        IReadOnlyList<Typeface> faces = FontLoader.LoadAll(TestFonts.FontPath("derived/NotoSansSerif.ttc"));
        Assert.Equal(2, faces.Count);
        Assert.Equal("Noto Sans", faces[0].FamilyName);
        Assert.Equal("Noto Serif", faces[1].FamilyName);
        Assert.Equal(1, faces[1].FaceIndex);
        Assert.True(faces[0].FileData.Span == faces[1].FileData.Span);
        Assert.Throws<ArgumentOutOfRangeException>(() => TestFonts.Load("derived/NotoSansSerif.ttc", 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => TestFonts.Load("NotoSans-Regular.ttf", 1));
    }

    [Fact]
    public void NamesAndStyleComeFromTheTables()
    {
        Typeface bold = TestFonts.Load("NotoSans-Bold.ttf");
        Assert.Equal("Noto Sans", bold.FamilyName);
        Assert.Equal("Bold", bold.SubfamilyName);
        Assert.Equal("Noto Sans Bold", bold.FullName);
        Assert.Equal("NotoSans-Bold", bold.PostScriptName);
        Assert.Equal(FontStyle.Bold, bold.Style);
        Assert.Equal(FontSlant.Italic, TestFonts.Load("NotoSans-Italic.ttf").Style.Slant);
        Assert.Equal(FontStyle.Normal, TestFonts.Load("SourceSans3-Regular.otf").Style);
        Assert.True(TestFonts.Load("DejaVuSansMono.ttf").IsFixedPitch);
        Assert.Equal("Noto Sans Bold", bold.ToString());
    }

    [Fact]
    public void StreamsAndMemoryLoadTheSameFace()
    {
        byte[] bytes = TestFonts.Bytes("Roboto-Regular.ttf");
        using var stream = new MemoryStream(bytes);
        Typeface a = Typeface.FromStream(stream), b = Typeface.FromMemory(bytes);
        Assert.Equal(a.GlyphCount, b.GlyphCount);
        Assert.Equal(a.GetAdvance(a.GetGlyph('W')), b.GetAdvance(b.GetGlyph('W')));
    }

    [Fact]
    public void TryGetTableReturnsTheStoredBytes()
    {
        Typeface face = TestFonts.Load("NotoSans-Regular.ttf");
        Assert.True(face.TryGetTable(new FontTag("GSUB"), out ReadOnlyMemory<byte> gsub));
        Assert.Equal(0x0001, BinaryPrimitives.ReadUInt16BigEndian(gsub.Span));
        Assert.False(face.TryGetTable(new FontTag("zzzz"), out _));
        Assert.Equal("OS/2", new FontTag("OS/2").ToString());
        Assert.Equal("CFF ", new FontTag("CFF").ToString());
        Assert.Throws<ArgumentException>(() => new FontTag("toolong"));
    }

    [Fact]
    public void UnsupportedDataThrowsUnsupportedFormat()
    {
        Assert.Throws<UnsupportedFormatException>(() => Typeface.FromMemory("wOFF\0\0\0\0\0\0\0\0"u8.ToArray()));
        Assert.Throws<UnsupportedFormatException>(() => Typeface.FromMemory(new byte[64]));
        Assert.Throws<UnsupportedFormatException>(() => Typeface.FromMemory("typ1\0\0\0\0\0\0\0\0"u8.ToArray()));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("OS/2")]
    [InlineData("post")]
    [InlineData("hhea")]
    [InlineData("hmtx")]
    [InlineData("cmap")]
    [InlineData("GPOS")]
    public void MissingTablesGetDefaultsAndADiagnostic(string tag)
    {
        byte[] bytes = RenameTable(TestFonts.Bytes("NotoSans-Regular.ttf"), tag, "zzzz");
        var seen = new List<EgamiDiagnostic>();
        var options = new FontLoadOptions { Context = EgamiContext.Default with { Diagnostics = seen.Add } };
        Typeface face = Typeface.FromMemory(bytes, 0, options);
        _ = face.FamilyName;
        _ = face.Extents;
        _ = face.GetAdvance(face.GetGlyph('A'));
        _ = face.Embedding;
        _ = face.GetKerning(1, 2);
        var sink = new TextSink();
        Assert.True(face.GetGlyphOutline(36, sink));
        if (tag == "GPOS")
        {
            Assert.Empty(seen);
            return;
        }

        Assert.Contains(seen, d => d.Id == "Font.MissingTable" && d.Message.Contains(tag, StringComparison.Ordinal));
        Assert.All(seen, d => Assert.Equal("Font", d.Source));
        switch (tag)
        {
            case "name":
                Assert.Equal("Unknown", face.FamilyName);
                break;
            case "OS/2":
                Assert.Equal(FontStyle.Normal, face.Style);
                Assert.Equal(FontEmbedding.Installable, face.Embedding);
                break;
            case "hmtx":
            case "hhea":
                Assert.Equal(face.UnitsPerEm / 2f, face.GetAdvance(face.GetGlyph('A')));
                break;
            case "cmap":
                Assert.Equal(0, face.GetGlyph('A'));
                Assert.False(face.HasCharacterMap);
                break;
        }

        Assert.Throws<CorruptDataException>(() =>
        {
            Typeface strict = Typeface.FromMemory(bytes, 0, new FontLoadOptions { Strict = true });
            _ = strict.FamilyName;
            _ = strict.Extents;
            _ = strict.GetAdvance(1);
            _ = strict.GetGlyph('A');
        });
    }

    [Fact]
    public void NoGlyphDataThrowsCorrupt()
    {
        Assert.Throws<CorruptDataException>(() => Typeface.FromMemory(RenameTable(TestFonts.Bytes("NotoSans-Regular.ttf"), "glyf", "zzzz")));
        Assert.Throws<CorruptDataException>(() => Typeface.FromMemory(RenameTable(TestFonts.Bytes("NotoSans-Regular.ttf"), "loca", "zzzz")));
    }

    [Fact]
    public void ABrokenGlyphDrawsEmptyWithADiagnostic()
    {
        byte[] bytes = TestFonts.Bytes("NotoSans-Regular.ttf");
        Typeface clean = Typeface.FromMemory(bytes);
        ushort a = clean.GetGlyph('A');

        // Point A's loca entry past the glyf table.
        (int locaOffset, _) = FindTable(bytes, "loca");
        bool longLoca = clean.Head.IndexToLocFormat == 1;
        if (longLoca)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(locaOffset + ((a + 1) * 4)), 0x7FFFFFF0);
        }
        else
        {
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(locaOffset + ((a + 1) * 2)), 0xFFF0);
        }

        var seen = new List<EgamiDiagnostic>();
        Typeface face = Typeface.FromMemory(bytes, 0, new FontLoadOptions { Context = EgamiContext.Default with { Diagnostics = seen.Add } });
        var sink = new TextSink();
        Assert.False(face.GetGlyphOutline(a, sink));
        Assert.Empty(sink.Verbs);
        Assert.Contains(seen, d => d.Id == "Font.BadGlyph");
        Assert.True(face.GetGlyphOutline(face.GetGlyph('Z'), sink));
        Assert.Throws<CorruptDataException>(() => Typeface.FromMemory(bytes, 0, new FontLoadOptions { Strict = true }).GetGlyphOutline(a, sink));
    }

    [Fact]
    public void GlyphIdsPastTheEndAreEmpty()
    {
        Typeface face = TestFonts.Load("NotoSans-Regular.ttf");
        ushort past = (ushort)face.GlyphCount;
        Assert.False(face.GetGlyphOutline(past, new TextSink()));
        Assert.Equal(0, face.GetAdvance(past));
        Assert.True(face.GetGlyphExtents(past).IsEmpty);
    }

    [Fact]
    public void TheSizeLimitIsChecked()
    {
        var options = new FontLoadOptions { Context = EgamiContext.Default with { Limits = SafetyLimits.Default with { MaxTotalBytes = 1000 } } };
        Assert.Throws<LimitExceededException>(() => Typeface.FromMemory(TestFonts.Bytes("NotoSans-Regular.ttf"), 0, options));
    }

    [Fact]
    public void MapCodePointsHandlesSurrogatesAndSelectors()
    {
        Typeface face = TestFonts.Load("NotoSans-Regular.ttf");
        Span<ushort> glyphs = stackalloc ushort[8];
        int n = face.MapCodePoints("A\U0001F600B️\uD800", glyphs);
        Assert.Equal(4, n);
        Assert.Equal(face.GetGlyph('A'), glyphs[0]);
        Assert.Equal(0, glyphs[1]);
        Assert.Equal(face.GetGlyph('B'), glyphs[2]);
        Assert.Equal(face.GetGlyph(0xFFFD), glyphs[3]);
    }

    [Fact]
    public void ExtentsFollowTheVerticalMetricsRule()
    {
        Typeface noto = TestFonts.Load("NotoSans-Regular.ttf");
        FontExtents e = noto.Extents;
        bool typo = (noto.Os2.FsSelection & 0x80) != 0;
        VerticalMetrics set = typo ? noto.TypoMetrics : noto.HheaMetrics;
        Assert.Equal(set.Ascent, e.Ascent);
        Assert.Equal(set.Descent, e.Descent);
        Assert.Equal(set.LineGap, e.LineGap);
        Assert.Equal(noto.Os2.CapHeight, e.CapHeight);
        Assert.Equal(noto.Os2.XHeight, e.XHeight);
        Assert.Equal(e.Ascent - e.Descent + e.LineGap, e.LineHeight);
        Assert.Equal(new VerticalMetrics(noto.Os2.WinAscent, -noto.Os2.WinDescent, 0), noto.WinMetrics);
    }

    internal static byte[] RenameTable(byte[] font, string from, string to)
    {
        byte[] copy = (byte[])font.Clone();
        (int _, int record) = FindTable(copy, from);
        uint tag = new FontTag(to).Value;
        BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(record), tag);
        return copy;
    }

    internal static (int Offset, int Record) FindTable(byte[] font, string tag)
    {
        uint want = new FontTag(tag).Value;
        int count = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(4));
        for (int i = 0; i < count; i++)
        {
            int record = 12 + (i * 16);
            if (BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(record)) == want)
            {
                return ((int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(record + 8)), record);
            }
        }

        throw new InvalidOperationException($"No {tag} table.");
    }
}
