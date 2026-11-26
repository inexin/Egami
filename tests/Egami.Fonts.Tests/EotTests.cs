using System.Buffers.Binary;

using Egami.Fonts.Woff;

namespace Egami.Fonts.Tests;

/// <summary>
/// Embedded OpenType (W3C member submission): the header versions, XOR, MicroType Express (LZCOMP and the Compact Table
/// Format) read back to the same font, writing, probing and damaged input.
/// </summary>
public class EotTests
{
    [Theory]
    [InlineData("DejaVuSans.ttf", false, false)]
    [InlineData("NotoSans-Regular.ttf", true, false)]
    [InlineData("Roboto-Regular.ttf", false, true)]
    [InlineData("derived/RobotoFlex-subset.ttf", true, true)]
    public void MicroTypeExpressReadsBackToTheSameFont(string file, bool runLength, bool xor)
    {
        byte[] source = TestFonts.Bytes(file);
        byte[] eot = MtxTestEncoder.Eot(source, MtxTestEncoder.Encode(source, runLength), compressed: true, xor);
        Assert.Equal(new FontProbe(FontFileKind.Eot, 1, FontOutlines.TrueType), FontLoader.Probe(eot));
        Typeface expected = Typeface.FromMemory(source), actual = Typeface.FromMemory(eot);
        Assert.Equal(FontFileKind.Eot, actual.Container);

        // Every table comes back as it was but glyf and loca, rebuilt: each glyph says the same, though its flags, padding and
        // push instructions may be written differently (and an overlap flag, which the compact form has no room for, is gone).
        byte[] decoded = WebFontDecoder.Decode(eot, FontLoadOptions.Default).Data;
        Assert.Equal(expected.Tables.Select(t => t.Tag.Value).Order(), actual.Tables.Select(t => t.Tag.Value).Order());
        foreach (Parsing.TableRecord record in expected.Tables)
        {
            if (record.Tag != Tags.Glyf && record.Tag != Tags.Loca)
            {
                Assert.True(expected.TryGetTable(record.Tag, out ReadOnlyMemory<byte> a) && actual.TryGetTable(record.Tag, out ReadOnlyMemory<byte> b) && a.Span.SequenceEqual(b.Span), record.Tag.ToString());
            }
        }

        var (glyf, push, code) = MtxTestEncoder.GlyphStreams(source);
        var (glyf2, push2, code2) = MtxTestEncoder.GlyphStreams(decoded);
        Assert.Equal(glyf, glyf2);
        Assert.Equal(push, push2);
        Assert.Equal(code, code2);
        WebFontWriterTests.AssertDrawsTheSame(expected, actual);
    }

    [Theory]
    [InlineData("NotoSans-Regular.ttf", false, null)]
    [InlineData("SourceSans3-Regular.otf", true, "https://example.com/")]
    [InlineData("DejaVuSans.ttf", true, "https://a.example/\0https://b.example/")]
    public void WrittenFilesReadBack(string file, bool xor, string? root)
    {
        byte[] source = TestFonts.Bytes(file);
        byte[] eot = WebFontWriter.Write(source, new WebFontOptions { Format = WebFontFormat.Eot, XorFontData = xor, RootString = root });
        Typeface expected = Typeface.FromMemory(source);
        FontOutlines outlines = expected.Outlines;
        Assert.Equal(new FontProbe(FontFileKind.Eot, 1, outlines), FontLoader.Probe(eot));

        // The header: its size, the version, the flags, the magic number and the OS/2 summary, little-endian.
        Assert.Equal((uint)eot.Length, BinaryPrimitives.ReadUInt32LittleEndian(eot));
        Assert.Equal(0x00020001u, BinaryPrimitives.ReadUInt32LittleEndian(eot.AsSpan(8)));
        Assert.Equal(xor ? 0x10000000u : 0, BinaryPrimitives.ReadUInt32LittleEndian(eot.AsSpan(12)));
        Assert.Equal(0x504C, BinaryPrimitives.ReadUInt16LittleEndian(eot.AsSpan(34)));
        Assert.Equal(expected.Os2.WeightClass, BinaryPrimitives.ReadUInt32LittleEndian(eot.AsSpan(28)));
        int familySize = BinaryPrimitives.ReadUInt16LittleEndian(eot.AsSpan(82));
        Assert.Equal(expected.FamilyName, System.Text.Encoding.Unicode.GetString(eot, 84, familySize));
        Eot.Header header = Eot.ReadHeader(eot);
        Assert.Equal(root ?? "", System.Text.Encoding.Unicode.GetString(eot, header.RootStringOffset, header.RootStringSize));

        IReadOnlyList<Typeface> faces = FontLoader.LoadAll(eot);
        Typeface actual = Assert.Single(faces);
        Assert.Equal(FontFileKind.Eot, actual.Container);
        WebFontWriterTests.AssertSameTables(expected, actual, glyfTransformed: false, woff2: false);
        WebFontWriterTests.AssertDrawsTheSame(expected, actual);

        // An EOT file written again as WOFF2 and back gives the font too.
        byte[] woff2 = WebFontWriter.Write(eot);
        WebFontWriterTests.AssertDrawsTheSame(expected, Typeface.FromMemory(woff2));
    }

    [Fact]
    public void ReadsAFontPowerPointEmbedded()
    {
        // PowerPoint's own output: EOT 0x00020002, subset and MicroType Express compressed, with empty glyphs as a bare
        // contour count. Every glyph of the deck's text draws, hints and measures as the installed font's.
        byte[] data = TestFonts.Bytes("derived/DejaVuSans-powerpoint.fntdata");
        Eot.Header header = Eot.ReadHeader(data);
        Assert.Equal(Eot.Version22, header.Version);
        Assert.Equal(Eot.FlagSubset | Eot.FlagCompressed, header.Flags);
        var diagnostics = new List<EgamiDiagnostic>();
        Typeface embedded = Typeface.FromMemory(data, options: new FontLoadOptions { Context = EgamiContext.Default with { Diagnostics = diagnostics.Add } });
        Assert.Empty(diagnostics);
        Typeface installed = TestFonts.Load("DejaVuSans.ttf");
        Assert.Equal(installed.FullName, embedded.FullName);
        Assert.Equal(installed.GlyphCount, embedded.GlyphCount);
        Assert.Equal(installed.Extents, embedded.Extents);
        foreach (char c in "The quick brown fox jumps over the lazy dog 0123456789".Distinct())
        {
            ushort g = installed.GetGlyph(c);
            Assert.Equal(g, embedded.GetGlyph(c));
            var expected = new TextSink();
            var actual = new TextSink();
            installed.GetGlyphOutline(g, expected);
            embedded.GetGlyphOutline(g, actual);
            Assert.Equal(expected.Verbs, actual.Verbs);
            Assert.Equal(expected.Ops.SelectMany(o => o), actual.Ops.SelectMany(o => o));
            Assert.Equal(installed.GetAdvance(g), embedded.GetAdvance(g));
            Assert.Equal(installed.GetLeftSideBearing(g), embedded.GetLeftSideBearing(g));
            Egami.Fonts.Hinting.HintedGlyph? hinted = installed.GetHintedGlyph(g, 13, TrueTypeTarget.GdiPlus), embeddedHinted = embedded.GetHintedGlyph(g, 13, TrueTypeTarget.GdiPlus);
            Assert.Equal(hinted is null, embeddedHinted is null);
            if (hinted is not null)
            {
                Assert.Equal(hinted.X, embeddedHinted!.X);
                Assert.Equal(hinted.Y, embeddedHinted.Y);
            }
        }

        // PowerPoint keeps about the ASCII range; a glyph outside it, which the deck does not use, is left out, cmap and all.
        ushort zhe = installed.GetGlyph(0x416);
        var dropped = new TextSink();
        embedded.GetGlyphOutline(zhe, dropped);
        Assert.NotEqual(0, zhe);
        Assert.Empty(dropped.Verbs);
        Assert.Equal(0, embedded.GetGlyph(0x416));
    }

    [Fact]
    public void EveryHeaderVersionReads()
    {
        byte[] source = TestFonts.Bytes("NotoSans-Regular.ttf");
        byte[] v21 = WebFontWriter.Write(source, new WebFontOptions { Format = WebFontFormat.Eot, RootString = "https://example.com/" });
        Eot.Header header = Eot.ReadHeader(v21);
        Typeface expected = Typeface.FromMemory(source);

        // 0x00010000: the four names and the font data, no root string.
        int rootSizeAt = header.RootStringOffset - 2;
        byte[] v1 = [.. v21.AsSpan(0, rootSizeAt - 2), .. v21.AsSpan(header.FontDataOffset)];
        BinaryPrimitives.WriteUInt32LittleEndian(v1, (uint)v1.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(v1.AsSpan(8), 0x00010000);
        WebFontWriterTests.AssertDrawsTheSame(expected, Typeface.FromMemory(v1));

        // 0x00020002: the root string's checksum, an EUDC code page, an empty signature and a small EUDC font.
        byte[] root = v21.AsSpan(header.RootStringOffset, header.RootStringSize).ToArray();
        var tail = new List<byte>();
        tail.AddRange(BitConverter.GetBytes(Eot.RootStringChecksum(root)));
        tail.AddRange(BitConverter.GetBytes(1252u));
        tail.AddRange(new byte[4]); // Padding6, SignatureSize
        tail.AddRange(BitConverter.GetBytes(0u));
        tail.AddRange(BitConverter.GetBytes(3u));
        tail.AddRange(new byte[] { 1, 2, 3 });
        byte[] v22 = [.. v21.AsSpan(0, header.FontDataOffset), .. tail, .. v21.AsSpan(header.FontDataOffset)];
        BinaryPrimitives.WriteUInt32LittleEndian(v22, (uint)v22.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(v22.AsSpan(8), 0x00020002);
        var diagnostics = new List<EgamiDiagnostic>();
        var options = new FontLoadOptions { Context = EgamiContext.Default with { Diagnostics = diagnostics.Add } };
        WebFontWriterTests.AssertDrawsTheSame(expected, Typeface.FromMemory(v22, options: options));
        Assert.Empty(diagnostics);

        // A wrong checksum is reported, not enforced.
        v22[header.FontDataOffset] ^= 1;
        Typeface.FromMemory(v22, options: options);
        Assert.Contains(diagnostics, d => d.Id == "Font.BadContainer");
    }

    [Fact]
    public void LzcompHandlesEveryItemKind()
    {
        // Literals, DUP2/4/6, short and long copies, a copy from the preloaded window, long runs.
        var data = new List<byte>();
        var random = new Random(7);
        for (int i = 0; i < 3000; i++)
        {
            data.Add((byte)random.Next(4));
        }

        data.AddRange([1, 5, 1, 5, 9, 1, 7, 5]);
        data.AddRange(Enumerable.Repeat((byte)0xAB, 900));
        data.AddRange(data.GetRange(100, 700));
        data.AddRange([3, 40, 3, 41, 3, 42]);
        data.AddRange(Enumerable.Range(0, 256).Select(i => (byte)i));
        byte[] bytes = [.. data];
        foreach (bool runLength in new[] { false, true })
        {
            byte[] packed = MtxTestEncoder.Lzcomp(bytes, runLength);
            Assert.Equal(bytes, Lzcomp.Decompress(packed, readRunLengthFlag: true, long.MaxValue, 0));
        }

        Assert.Empty(Lzcomp.Decompress(MtxTestEncoder.Lzcomp([], false), true, long.MaxValue, 0));
        Assert.Equal([7, 7], Lzcomp.Decompress(MtxTestEncoder.Lzcomp([7, 7], false), true, long.MaxValue, 0));
    }

    [Fact]
    public void RunLengthLayerDecodes()
    {
        // Escape 0xEE: a plain byte, the escape itself, a run of five, a plain byte.
        Assert.Equal([1, 0xEE, 9, 9, 9, 9, 9, 2], Lzcomp.RunLengthDecode([0xEE, 1, 0xEE, 0, 0xEE, 5, 9, 2], long.MaxValue, 0));
        Assert.Throws<CorruptDataException>(() => Lzcomp.RunLengthDecode([0xEE, 1, 0xEE], long.MaxValue, 0));
        Assert.Throws<CorruptDataException>(() => Lzcomp.RunLengthDecode([0xEE, 0xEE, 4], long.MaxValue, 0));
        Assert.Throws<LimitExceededException>(() => Lzcomp.RunLengthDecode([0xEE, 0xEE, 255, 1], 100, 0));
    }

    [Fact]
    public void CompactTablesDecode()
    {
        // cvt: 5, then +300 (two bytes), -300, a large jump (a word), wrapping at 2^16.
        byte[] cvt = MtxDecoder.DecodeCvt([0, 4, 5, 248, 67, 240, 67, 238, 0x7F, 0x00], long.MaxValue);
        Assert.Equal([0, 5, 1, 54, 0, 5, 0x7F, 5], cvt);

        // The magnitude code of the submission's example: 0x90 0x48 0x84 read as 0 0 0 0 +1 +1 0 +1 +1 0 +1 0 0.
        byte[] head = new byte[54];
        BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(18), 1000);
        byte[] maxp = [0, 0, 0x50, 0, 0, 13];
        byte[] hhea = new byte[36];
        BinaryPrimitives.WriteUInt16BigEndian(hhea.AsSpan(34), 1);
        byte[] hmtx = [0x01, 0xF4, 0, 0]; // every glyph 500 units: 5 pixels at 10 ppem
        byte[] hdmx = [0, 0, 0, 1, 0, 0, 0, 16, 10, 6, 0x90, 0x48, 0x84];
        byte[] table = MtxDecoder.DecodeHdmx(hdmx, head, maxp, hhea, hmtx, long.MaxValue);
        Assert.Equal(8 + 16, table.Length);
        Assert.Equal([10, 6, 5, 5, 5, 5, 6, 6, 5, 6, 6, 5, 6, 5, 5, 0], table.AsSpan(8, 16).ToArray());

        // An hdmx stored as it is: the version says so and comes back.
        Assert.Equal([0, 0, 1, 2], MtxDecoder.DecodeHdmx([0xFF, 0xFF, 1, 2], [], [], [], [], long.MaxValue));
    }

    [Fact]
    public void CompactVdmxDecodes()
    {
        // One ratio, one group of two records: multipliers 1.0 (2048) for yMax and 0.25 (512) for yMin; ppem 8 then 10, with
        // errors +1 on the first yMax and -1 on the second yMin.
        var bits = new List<bool>();
        foreach (int value in new[] { 0, 1, 0, 1, 0, -1 })
        {
            MtxTestEncoder.Magnitude(bits, value);
        }

        var ctf = new List<byte> { 0, 1, 0, 1, 0, 1, 1, 1, 1, 1, 0, 12, 0, 2, 0x08, 0x00, 0x02, 0x00 };
        ctf.AddRange(MtxTestEncoder.PackLsbFirst(bits));
        byte[] vdmx = MtxDecoder.DecodeVdmx([.. ctf]);
        byte[] expected =
        [
            0, 1, 0, 1, 0, 1, 1, 1, 1, 1, 0, 12,
            0, 2, 8, 10,
            0, 8, 0, 9, 0xFF, 0xFE,
            0, 10, 0, 10, 0xFF, 0xFC,
        ];
        Assert.Equal(expected, vdmx);
    }

    [Fact]
    public void PushInstructionsAreWritten()
    {
        var w = new Subsetting.ByteWriter();
        MtxDecoder.WritePushes(w, [1, 2, 300, -1, .. Enumerable.Repeat(7, 10)]);
        Assert.Equal([0xB1, 1, 2, 0xB9, 0x01, 0x2C, 0xFF, 0xFF, 0x40, 10, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7], w.ToArray());
    }

    [Fact]
    public void DamagedFilesThrow()
    {
        byte[] source = TestFonts.Bytes("Roboto-Regular.ttf");
        byte[] eot = MtxTestEncoder.Eot(source, MtxTestEncoder.Encode(source), compressed: true, xor: false);

        // Cut short anywhere: the header, the strings, the compressed blocks.
        foreach (int length in new[] { 40, 90, 300, eot.Length / 2, eot.Length - 1 })
        {
            Assert.ThrowsAny<EgamiException>(() => Typeface.FromMemory(eot.AsMemory(0, length)));
        }

        // Block offsets that do not fit, and a font that is not a font.
        Eot.Header header = Eot.ReadHeader(eot);
        byte[] bad = (byte[])eot.Clone();
        bad[header.FontDataOffset + 4] = 0xFF;
        Assert.Throws<CorruptDataException>(() => Typeface.FromMemory(bad));
        byte[] plain = WebFontWriter.Write(source, new WebFontOptions { Format = WebFontFormat.Eot });
        header = Eot.ReadHeader(plain);
        plain[header.FontDataOffset] = 9;
        Assert.Throws<CorruptDataException>(() => Typeface.FromMemory(plain));

        // The output size counts against the context's limit.
        var small = new FontLoadOptions { Context = EgamiContext.Default with { Limits = SafetyLimits.Default with { MaxTotalBytes = 100_000 } } };
        Assert.Throws<LimitExceededException>(() => Typeface.FromMemory(eot, options: small));
    }

    [Fact]
    public void FontFoldersIndexEotFiles()
    {
        string folder = Path.Combine(Path.GetTempPath(), "egami-eot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            byte[] source = TestFonts.Bytes("Roboto-Regular.ttf");
            File.WriteAllBytes(Path.Combine(folder, "roboto.eot"), MtxTestEncoder.Eot(source, MtxTestEncoder.Encode(source), compressed: true, xor: true));
            var fonts = new FontCollection();
            Assert.Equal(1, fonts.AddDirectory(folder));
            FontMatch match = fonts.Match("Roboto", FontStyle.Normal)!.Value;
            Assert.Equal(FontFileKind.Eot, match.Typeface.Container);
            Assert.Equal(Typeface.FromMemory(source).GetGlyph('R'), match.Typeface.GetGlyph('R'));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void FuzzSeedsLoad()
    {
        // The eot fuzz target's seeds, from the small fuzz fonts. EGAMI_EOT_SEEDS names a folder to write them to again.
        string fonts = Path.Combine(TestFonts.Root, "tests", "images", "fuzz", "font-seeds");
        byte[] noto = File.ReadAllBytes(Path.Combine(fonts, "noto.ttf")), kern = File.ReadAllBytes(Path.Combine(fonts, "kern.ttf"));
        var seeds = new Dictionary<string, byte[]>
        {
            ["noto-mtx.eot"] = MtxTestEncoder.Eot(noto, MtxTestEncoder.Encode(noto), compressed: true, xor: false),
            ["noto-mtx-rle-xor.eot"] = MtxTestEncoder.Eot(noto, MtxTestEncoder.Encode(noto, runLength: true), compressed: true, xor: true),
            ["kern-root.eot"] = WebFontWriter.Write(kern, new WebFontOptions { Format = WebFontFormat.Eot, RootString = "https://example.com/" }),
        };
        string? write = Environment.GetEnvironmentVariable("EGAMI_EOT_SEEDS");
        string folder = write ?? Path.Combine(TestFonts.Root, "tests", "images", "fuzz", "eot-seeds");
        foreach ((string name, byte[] data) in seeds)
        {
            if (write is not null)
            {
                File.WriteAllBytes(Path.Combine(write, name), data);
            }

            Assert.Equal(data, File.ReadAllBytes(Path.Combine(folder, name)));
            Assert.Equal(FontFileKind.Eot, Typeface.FromMemory(data).Container);
        }
    }

    [Fact]
    public void CollectionsAreRefused()
    {
        Assert.Throws<ArgumentException>(() => WebFontWriter.Write(TestFonts.Bytes("derived/NotoSansSerif.ttc"), new WebFontOptions { Format = WebFontFormat.Eot }));
    }
}
