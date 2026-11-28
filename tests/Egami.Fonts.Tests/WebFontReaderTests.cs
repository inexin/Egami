using System.Buffers.Binary;

namespace Egami.Fonts.Tests;

/// <summary>
/// WOFF and WOFF2 back to sfnt (2.7.8): <see cref="WebFontReader.ToSfnt"/> gives the font the writer wrapped, table for table,
/// for single fonts and WOFF2 collections; and subsets without TrueType hinting (<see cref="SubsetOptions.DropHinting"/>).
/// </summary>
public class WebFontReaderTests
{
    [Theory]
    [InlineData("NotoSans-Regular.ttf", WebFontFormat.Woff, false)]
    [InlineData("NotoSans-Regular.ttf", WebFontFormat.Woff2, true)]
    [InlineData("NotoSans-Regular.ttf", WebFontFormat.Woff2, false)]
    [InlineData("SourceSans3-Regular.otf", WebFontFormat.Woff, false)]
    [InlineData("SourceSans3-Regular.otf", WebFontFormat.Woff2, true)]
    [InlineData("derived/RobotoFlex-subset.ttf", WebFontFormat.Woff2, true)]
    [InlineData("derived/NotoSansSerif.ttc", WebFontFormat.Woff2, true)]
    public void WrittenWebFontsComeBackAsTheSfnt(string file, WebFontFormat format, bool transform)
    {
        byte[] source = TestFonts.Bytes(file);
        byte[] web = WebFontWriter.Write(source, new WebFontOptions { Format = format, TransformGlyf = transform, TransformHmtx = transform });
        byte[] sfnt = WebFontReader.ToSfnt(web);

        FontProbe probe = FontLoader.Probe(sfnt);
        Assert.Equal(file.EndsWith(".ttc", StringComparison.Ordinal) ? FontFileKind.Collection : FontFileKind.Sfnt, probe.Kind);
        IReadOnlyList<Typeface> expected = FontLoader.LoadAll(source), actual = FontLoader.LoadAll(sfnt);
        Assert.Equal(expected.Count, actual.Count);
        for (int f = 0; f < expected.Count; f++)
        {
            bool glyfTransformed = format == WebFontFormat.Woff2 && transform && expected[f].Outlines == FontOutlines.TrueType;
            WebFontWriterTests.AssertSameTables(expected[f], actual[f], glyfTransformed, format == WebFontFormat.Woff2);
            WebFontWriterTests.AssertDrawsTheSame(expected[f], actual[f]);
        }

        // WOFF 1.0 keeps every table byte for byte, so the sfnt's tables are the source's.
        if (format == WebFontFormat.Woff)
        {
            Typeface a = expected[0], b = actual[0];
            foreach (Parsing.TableRecord record in a.Tables)
            {
                Assert.True(a.TryGetTable(record.Tag, out ReadOnlyMemory<byte> x));
                Assert.True(b.TryGetTable(record.Tag, out ReadOnlyMemory<byte> y));
                Assert.True(x.Span.SequenceEqual(y.Span), record.Tag.ToString());
            }
        }
    }

    [Theory]
    [InlineData("derived/NotoSans-Regular.woff", 1)]
    [InlineData("derived/NotoSans-Regular.woff2", 1)]
    [InlineData("derived/Roboto-Regular-hmtx.woff2", 1)]
    [InlineData("derived/SourceSans3-Regular.woff", 1)]
    [InlineData("derived/SourceSans3-Regular.woff2", 1)]
    [InlineData("derived/NotoSansSerif.woff2", 2)]
    public void ReferenceWebFontsUnwrapToFontsThatLoadTheSame(string file, int faces)
    {
        byte[] web = TestFonts.Bytes(file);
        byte[] sfnt = WebFontReader.ToSfnt(web);
        IReadOnlyList<Typeface> expected = FontLoader.LoadAll(web), actual = FontLoader.LoadAll(sfnt);
        Assert.Equal(faces, actual.Count);
        for (int f = 0; f < faces; f++)
        {
            Assert.NotEqual(FontFileKind.Woff, actual[f].Container);
            Assert.NotEqual(FontFileKind.Woff2, actual[f].Container);
            Assert.Equal(expected[f].Tables.Select(t => t.Tag.Value).Order(), actual[f].Tables.Select(t => t.Tag.Value).Order());
            foreach (Parsing.TableRecord record in expected[f].Tables)
            {
                Assert.True(expected[f].TryGetTable(record.Tag, out ReadOnlyMemory<byte> x));
                Assert.True(actual[f].TryGetTable(record.Tag, out ReadOnlyMemory<byte> y));
                Assert.True(x.Span.SequenceEqual(y.Span), record.Tag.ToString());
            }
        }

        // Written again, the sfnt makes a web font that unwraps to a font drawing the same (WOFF 1.0: the same bytes).
        bool woff = file.EndsWith(".woff", StringComparison.Ordinal);
        byte[] again = WebFontReader.ToSfnt(WebFontWriter.Write(sfnt, new WebFontOptions { Format = woff ? WebFontFormat.Woff : WebFontFormat.Woff2 }));
        if (woff)
        {
            Assert.Equal(sfnt, again);
        }

        IReadOnlyList<Typeface> rewritten = FontLoader.LoadAll(again);
        for (int f = 0; f < faces; f++)
        {
            WebFontWriterTests.AssertDrawsTheSame(actual[f], rewritten[f]);
        }
    }

    [Fact]
    public void OnlyWebFontsUnwrap()
    {
        Assert.Throws<ArgumentException>(() => WebFontReader.ToSfnt(TestFonts.Bytes("NotoSans-Regular.ttf")));
        Assert.Throws<ArgumentException>(() => WebFontReader.ToSfnt(TestFonts.Bytes("derived/NotoSansSerif.ttc")));
        Assert.Throws<ArgumentException>(() => WebFontReader.ToSfnt(TestFonts.Bytes("derived/SourceSans3.cff")));
        Assert.Throws<ArgumentException>(() => WebFontReader.ToSfnt(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
        Assert.Throws<ArgumentException>(() => WebFontReader.ToSfnt(ReadOnlyMemory<byte>.Empty));

        byte[] cut = TestFonts.Bytes("derived/NotoSans-Regular.woff2")[..2000];
        Assert.ThrowsAny<EgamiException>(() => WebFontReader.ToSfnt(cut));
    }

    [Fact]
    public void LimitsApply()
    {
        EgamiContext small = EgamiContext.Default with { Limits = SafetyLimits.Default with { MaxTotalBytes = 300_000 } };
        byte[] woff2 = TestFonts.Bytes("derived/NotoSans-Regular.woff2");
        Assert.True(woff2.Length < 300_000);
        Assert.Throws<LimitExceededException>(() => WebFontReader.ToSfnt(woff2, small));
        Assert.Throws<LimitExceededException>(() => WebFontReader.ToSfnt(woff2, EgamiContext.Default with { Limits = SafetyLimits.Default with { MaxTotalBytes = 1000 } }));
        Assert.NotEmpty(WebFontReader.ToSfnt(woff2));
    }

    [Theory]
    [InlineData("NotoSans-Regular.ttf", false)]
    [InlineData("NotoSans-Regular.ttf", true)]
    [InlineData("DejaVuSans.ttf", false)]
    [InlineData("Roboto-Regular.ttf", false)]
    [InlineData("derived/NotoSans-VF-subset.ttf", false)]
    public void DropHintingLeavesOutTheProgramsAndInstructions(string file, bool layout)
    {
        Typeface face = TestFonts.Load(file);
        ushort[] glyphs = [.. "Hinting: Åå fi ÿ 0123".Select(c => face.GetGlyph(c)).Where(g => g != 0)];
        var options = new SubsetOptions { RetainGlyphIds = layout, LayoutClosure = layout, KeepTables = [Tags.Fpgm] };
        FontSubset hinted = FontSubsetter.Subset(face, glyphs, options);
        FontSubset bare = FontSubsetter.Subset(face, glyphs, options with { DropHinting = true });
        Typeface a = hinted.ToTypeface(), b = bare.ToTypeface();

        Assert.True(bare.Data.Length < hinted.Data.Length, $"{bare.Data.Length} vs {hinted.Data.Length}");
        Assert.Equal(hinted.Glyphs, bare.Glyphs);
        foreach (FontTag tag in (FontTag[])[Tags.Fpgm, Tags.Prep, Tags.Cvt])
        {
            Assert.False(b.TryGetTable(tag, out _), tag.ToString());
        }

        Assert.Equal(a.TryGetTable(Tags.Gasp, out _), b.TryGetTable(Tags.Gasp, out _));
        Assert.True(b.TryGetTable(Tags.Maxp, out ReadOnlyMemory<byte> maxp));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(maxp.Span[14..]));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(maxp.Span[26..]));

        for (int g = 0; g < b.GlyphCount; g++)
        {
            ReadOnlySpan<byte> data = b.Glyf!.GetGlyphData(g, out _);
            if (data.IsEmpty)
            {
                continue;
            }

            int contours = BinaryPrimitives.ReadInt16BigEndian(data);
            if (contours >= 0)
            {
                Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(data[(10 + (2 * contours))..]));
            }
            else
            {
                AssertNoCompositeInstructions(data);
            }

            var sa = new TextSink();
            var sb = new TextSink();
            a.GetGlyphOutline((ushort)g, sa);
            b.GetGlyphOutline((ushort)g, sb);
            Assert.Equal(sa.Verbs, sb.Verbs);
            Assert.Equal(sa.Ops.SelectMany(o => o), sb.Ops.SelectMany(o => o));
            Assert.Equal(a.GetAdvance((ushort)g), b.GetAdvance((ushort)g));
        }
    }

    [Fact]
    public void DropHintingDropsCffHintsOnly()
    {
        // The outlines and advances stay; only the hints go (CffCharStringWriterTests checks every glyph).
        Typeface face = TestFonts.Load("SourceSans3-Regular.otf");
        ushort[] glyphs = [.. "CFF".Select(c => face.GetGlyph(c))];
        FontSubset hinted = FontSubsetter.Subset(face, glyphs), plain = FontSubsetter.Subset(face, glyphs, new SubsetOptions { DropHinting = true });
        Assert.True(plain.Data.Length < hinted.Data.Length);
        Typeface a = hinted.ToTypeface(), b = plain.ToTypeface();
        for (int g = 0; g < a.GlyphCount; g++)
        {
            var sa = new TextSink();
            var sb = new TextSink();
            a.GetGlyphOutline((ushort)g, sa);
            b.GetGlyphOutline((ushort)g, sb);
            Assert.Equal(sa.Verbs, sb.Verbs);
            Assert.Equal(a.GetAdvance((ushort)g), b.GetAdvance((ushort)g));
        }
    }

    private static void AssertNoCompositeInstructions(ReadOnlySpan<byte> data)
    {
        int at = 10, flags;
        do
        {
            flags = BinaryPrimitives.ReadUInt16BigEndian(data[at..]);
            Assert.Equal(0, flags & 0x0100);
            int size = (flags & 0x0001) != 0 ? 4 : 2;
            size += (flags & 0x0008) != 0 ? 2 : (flags & 0x0040) != 0 ? 4 : (flags & 0x0080) != 0 ? 8 : 0;
            at += 4 + size;
        }
        while ((flags & 0x0020) != 0);

        // Only padding may follow the last component.
        Assert.True(data.Length - at < 4);
    }
}
