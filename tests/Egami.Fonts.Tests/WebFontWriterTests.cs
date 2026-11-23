using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

using Egami.Fonts.Woff;

namespace Egami.Fonts.Tests;

/// <summary>
/// WOFF and WOFF2 writing (2.7.7): a font written and read back through Egami's own reader has every table it had (glyf glyph
/// by glyph, as WOFF2 rebuilds it), and its faces draw the same; the containers' blocks, collections, options and damaged input.
/// </summary>
public class WebFontWriterTests
{
    public static TheoryData<string, WebFontFormat, bool, bool> RoundTrips()
    {
        var data = new TheoryData<string, WebFontFormat, bool, bool>();
        string[] fonts =
        [
            "NotoSans-Regular.ttf", "Roboto-Regular.ttf", "SourceSans3-Regular.otf", "DejaVuSans.ttf", "derived/RobotoFlex-subset.ttf",
            "derived/SourceSans3-VF-subset.otf", "derived/NotoSansSerif.ttc", "derived/NotoSans-Regular.woff", "derived/Roboto-Regular-hmtx.woff2",
        ];
        foreach (string font in fonts)
        {
            data.Add(font, WebFontFormat.Woff2, true, true);
            data.Add(font, WebFontFormat.Woff2, false, false);
            if (!font.EndsWith(".ttc", StringComparison.Ordinal))
            {
                data.Add(font, WebFontFormat.Woff, false, false);
            }
        }

        data.Add("NotoSans-Regular.ttf", WebFontFormat.Woff2, true, false);
        return data;
    }

    [Theory]
    [MemberData(nameof(RoundTrips))]
    public void WrittenFontsReadBackWithEveryTable(string file, WebFontFormat format, bool glyf, bool hmtx)
    {
        byte[] source = TestFonts.Bytes(file);
        var options = new WebFontOptions { Format = format, TransformGlyf = glyf, TransformHmtx = hmtx };
        byte[] web = WebFontWriter.Write(source, options);
        Assert.Equal(format == WebFontFormat.Woff ? FontFileKind.Woff : FontFileKind.Woff2, FontLoader.Probe(web).Kind);
        IReadOnlyList<Typeface> expected = FontLoader.LoadAll(source), actual = FontLoader.LoadAll(web);
        Assert.Equal(expected.Count, actual.Count);
        for (int f = 0; f < expected.Count; f++)
        {
            AssertSameTables(expected[f], actual[f], format == WebFontFormat.Woff2 && glyf, format == WebFontFormat.Woff2);
            AssertDrawsTheSame(expected[f], actual[f]);
        }
    }

    [Theory]
    [InlineData("NotoSans-Regular.ttf")]
    [InlineData("Roboto-Regular.ttf")]
    [InlineData("SourceSans3-Regular.otf")]
    [InlineData("DejaVuSans.ttf")]
    [InlineData("derived/RobotoFlex-subset.ttf")]
    [InlineData("derived/SourceSans3-VF-subset.otf")]
    [InlineData("derived/NotoSansSerif.ttc")]
    public void GlyfRebuildsAsTheReferenceEncodersFileDoes(string file)
    {
        // woff2_decompress of woff2_compress's file (tools/fontref/dump_277.py) gives the reference glyf and loca; Egami's file,
        // read back, must give the same bytes. Debian's woff2 1.0.2 drops overlap flags, so glyf is not compared where a glyph has one.
        JsonElement r = TestFonts.Reference("encode/" + Path.GetFileName(file));
        byte[] web = WebFontWriter.Write(TestFonts.Bytes(file));
        IReadOnlyList<Typeface> faces = FontLoader.LoadAll(web);
        JsonElement expected = r.GetProperty("woff2Decompress");
        Assert.Equal(expected.GetArrayLength(), faces.Count);
        for (int f = 0; f < faces.Count; f++)
        {
            bool overlaps = r.GetProperty("overlapGlyphs")[f].GetArrayLength() > 0;
            foreach (JsonProperty table in expected[f].EnumerateObject())
            {
                Assert.True(faces[f].TryGetTable(new FontTag(table.Name), out ReadOnlyMemory<byte> data), table.Name);
                Assert.Equal(table.Value[0].GetInt32(), data.Length);
                if (table.Name == "head" || (table.Name == "glyf" && overlaps))
                {
                    continue;
                }

                Assert.True(table.Value[1].GetString() == Convert.ToHexStringLower(SHA256.HashData(data.Span)), $"face {f} table {table.Name} differs");
            }
        }

        // Sizes: the gate (1 % of woff2_compress with hmtx off) is egami-compare's; this only catches a gross regression.
        Assert.True(web.Length < r.GetProperty("woff2Compress").GetInt32() * 1.02, $"{web.Length} bytes against woff2_compress's {r.GetProperty("woff2Compress").GetInt32()}");
    }

    [Theory]
    [InlineData("NotoSans-Regular.ttf")]
    [InlineData("SourceSans3-Regular.otf")]
    [InlineData("derived/RobotoFlex-subset.ttf")]
    public void FacesWriteAsTheyWereLoaded(string file)
    {
        Typeface face = TestFonts.Load(file);
        using var stream = new MemoryStream();
        WebFontWriter.Write(face, stream);
        Typeface back = Typeface.FromMemory(stream.ToArray());
        Assert.Equal(FontFileKind.Woff2, back.Container);
        AssertSameTables(face, back, glyfTransformed: face.Outlines == FontOutlines.TrueType);
        Assert.Equal(stream.ToArray(), WebFontWriter.Write(face));
    }

    [Fact]
    public void ACollectionFaceBecomesASingleFont()
    {
        Typeface serif = TestFonts.Load("derived/NotoSansSerif.ttc", 1);
        foreach (WebFontFormat format in Enum.GetValues<WebFontFormat>())
        {
            byte[] web = WebFontWriter.Write(serif, new WebFontOptions { Format = format });
            Assert.Equal(1, FontLoader.Probe(web).FaceCount);
            Typeface back = Typeface.FromMemory(web);
            Assert.Equal(serif.FullName, back.FullName);
            AssertDrawsTheSame(serif, back);
        }
    }

    [Fact]
    public void CollectionsShareTables()
    {
        byte[] ttc = TestFonts.Bytes("derived/NotoSansSerif.ttc");
        byte[] web = WebFontWriter.Write(ttc);
        Assert.Equal(new FontProbe(FontFileKind.Woff2, 2, FontOutlines.TrueType), FontLoader.Probe(web));
        int unique = FontLoader.LoadAll(ttc).SelectMany(f => f.Tables).Select(t => (t.Tag, t.Offset)).Distinct().Count();
        Assert.Equal(unique, BinaryPrimitives.ReadUInt16BigEndian(web.AsSpan(12)));
        Assert.Equal(0x74746366u, BinaryPrimitives.ReadUInt32BigEndian(web.AsSpan(4)));
        Assert.Throws<ArgumentException>(() => WebFontWriter.Write(ttc, new WebFontOptions { Format = WebFontFormat.Woff }));
    }

    [Fact]
    public void HeadersFollowTheSpecifications()
    {
        Typeface face = TestFonts.Load("NotoSans-Regular.ttf");
        Assert.True(face.TryGetTable(Tags.Head, out ReadOnlyMemory<byte> head));
        uint revision = BinaryPrimitives.ReadUInt32BigEndian(head.Span[4..]);

        byte[] woff = WebFontWriter.Write(face, new WebFontOptions { Format = WebFontFormat.Woff });
        Assert.Equal((uint)woff.Length, BinaryPrimitives.ReadUInt32BigEndian(woff.AsSpan(8)));
        Assert.Equal(revision, BinaryPrimitives.ReadUInt32BigEndian(woff.AsSpan(20)));
        int count = BinaryPrimitives.ReadUInt16BigEndian(woff.AsSpan(12));
        uint previous = 0;
        long sfntSize = 12 + (16 * count);
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> entry = woff.AsSpan(44 + (20 * i), 20);
            uint tag = BinaryPrimitives.ReadUInt32BigEndian(entry);
            uint offset = BinaryPrimitives.ReadUInt32BigEndian(entry[4..]), stored = BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);
            uint length = BinaryPrimitives.ReadUInt32BigEndian(entry[12..]), checksum = BinaryPrimitives.ReadUInt32BigEndian(entry[16..]);
            Assert.True(tag > previous, "the directory is sorted by tag");
            Assert.Equal(0u, offset % 4);
            Assert.True(stored <= length);
            Assert.True(face.TryGetTable(new FontTag(tag), out ReadOnlyMemory<byte> table));
            byte[] body = table.ToArray();
            if (tag == Tags.Head.Value)
            {
                BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(8), 0);
            }

            Assert.Equal(Subsetting.SfntWriter.Checksum(body), checksum);
            sfntSize += (length + 3) & ~3;
            previous = tag;
        }

        Assert.Equal((uint)sfntSize, BinaryPrimitives.ReadUInt32BigEndian(woff.AsSpan(16)));

        byte[] woff2 = WebFontWriter.Write(face);
        Assert.Equal((uint)woff2.Length, BinaryPrimitives.ReadUInt32BigEndian(woff2.AsSpan(8)));
        Assert.Equal(0, woff2.Length % 4);
        Assert.Equal(revision, BinaryPrimitives.ReadUInt32BigEndian(woff2.AsSpan(24)));
        Assert.True(woff2.Length < woff.Length);
        Assert.True(woff2.Length < WebFontWriter.Write(face, new WebFontOptions { TransformGlyf = false }).Length);
    }

    [Fact]
    public void MetadataAndPrivateDataAreKeptOrReplaced()
    {
        Typeface source = TestFonts.Load("derived/NotoSans-Regular.woff");
        byte[] sourceBytes = TestFonts.Bytes("derived/NotoSans-Regular.woff");
        string metadata = source.GetWoffMetadata()!;
        byte[] priv = source.GetWoffPrivateData().ToArray();
        foreach (WebFontFormat format in Enum.GetValues<WebFontFormat>())
        {
            var options = new WebFontOptions { Format = format };
            Typeface kept = Typeface.FromMemory(WebFontWriter.Write(source, options));
            Assert.Equal(metadata, kept.GetWoffMetadata());
            Assert.Equal(priv, kept.GetWoffPrivateData().ToArray());
            kept = Typeface.FromMemory(WebFontWriter.Write(sourceBytes, options));
            Assert.Equal(metadata, kept.GetWoffMetadata());

            string xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><metadata version=\"1.0\"><description><text>Ægami ✓</text></description></metadata>";
            Typeface replaced = Typeface.FromMemory(WebFontWriter.Write(source, options with { Metadata = xml, PrivateData = new byte[] { 1, 2, 3, 4, 5 } }));
            Assert.Equal(xml, replaced.GetWoffMetadata());
            Assert.Equal([1, 2, 3, 4, 5], replaced.GetWoffPrivateData().ToArray());

            Typeface removed = Typeface.FromMemory(WebFontWriter.Write(source, options with { Metadata = string.Empty, PrivateData = ReadOnlyMemory<byte>.Empty }));
            Assert.Null(removed.GetWoffMetadata());
            Assert.True(removed.GetWoffPrivateData().IsEmpty);

            Typeface onlyPrivate = Typeface.FromMemory(WebFontWriter.Write(TestFonts.Load("Roboto-Regular.ttf"), options with { PrivateData = new byte[] { 9 } }));
            Assert.Null(onlyPrivate.GetWoffMetadata());
            Assert.Equal([9], onlyPrivate.GetWoffPrivateData().ToArray());
        }
    }

    [Fact]
    public void SubsetsWriteAsWebFonts()
    {
        Typeface face = TestFonts.Load("NotoSans-Regular.ttf");
        GlyphRun run = new Font(face, 16).Shape("Web fonts, smaller.");
        FontSubset subset = FontSubsetter.Subset(face, run.Glyphs.ToArray());
        byte[] woff2 = WebFontWriter.Write(subset);
        Assert.True(woff2.Length < subset.Data.Length);
        AssertSameTables(subset.ToTypeface(), Typeface.FromMemory(woff2), glyfTransformed: true);

        FontSubset cff = FontSubsetter.Subset(TestFonts.Load("SourceSans3-Regular.otf"), [0, 5, 6], new SubsetOptions { Format = SubsetFormat.BareCff });
        Assert.Throws<ArgumentException>(() => WebFontWriter.Write(cff));
    }

    [Fact]
    public void InputThatIsNotAnSfntIsRejected()
    {
        Assert.Throws<ArgumentException>(() => WebFontWriter.Write(TestFonts.Load("derived/SourceSans3.cff")));
        Assert.Throws<ArgumentException>(() => WebFontWriter.Write(TestFonts.Bytes("derived/SourceSans3.cff")));
        Assert.Throws<ArgumentException>(() => WebFontWriter.Write(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
        Assert.Throws<ArgumentException>(() => WebFontWriter.Write(ReadOnlyMemory<byte>.Empty));
        Assert.Throws<ArgumentNullException>(() => WebFontWriter.Write((Typeface)null!));
        Assert.Throws<ArgumentNullException>(() => WebFontWriter.Write((FontSubset)null!));
        Assert.Throws<ArgumentNullException>(() => WebFontWriter.Write(TestFonts.Load("Roboto-Regular.ttf"), (Stream)null!));
    }

    [Fact]
    public void LimitsApply()
    {
        var options = new WebFontOptions { Context = EgamiContext.Default with { Limits = SafetyLimits.Default with { MaxTotalBytes = 300_000 } } };
        Assert.Throws<LimitExceededException>(() => WebFontWriter.Write(TestFonts.Bytes("NotoSans-Regular.ttf"), options));
        Assert.Throws<LimitExceededException>(() => WebFontWriter.Write(TestFonts.Load("NotoSans-Regular.ttf"), options));
        Assert.Throws<LimitExceededException>(() => WebFontWriter.Write(TestFonts.Load("Roboto-Regular.ttf"), options with { PrivateData = new byte[400_000] }));
    }

    [Fact]
    public void AnUnreadableGlyfIsStoredUntransformed()
    {
        byte[] font = TestFonts.Bytes("Roboto-Regular.ttf");
        Typeface face = Typeface.FromMemory(font);
        int glyf = face.Tables.First(t => t.Tag == Tags.Glyf).Offset;
        int glyph = face.Tables.First(t => t.Tag == Tags.Loca).Offset;
        int at = glyf + (BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(glyph + 2)) * 2);

        // Glyph 1's contour count becomes -2, which no glyph has.
        BinaryPrimitives.WriteInt16BigEndian(font.AsSpan(at), -2);
        var diagnostics = new List<EgamiDiagnostic>();
        var options = new WebFontOptions { Context = EgamiContext.Default with { Diagnostics = diagnostics.Add } };
        byte[] web = WebFontWriter.Write(font, options);
        Assert.Contains(diagnostics, d => d.Id == "Font.TransformSkipped");
        Typeface back = Typeface.FromMemory(web);
        Assert.True(back.TryGetTable(Tags.Glyf, out ReadOnlyMemory<byte> written));
        Assert.True(face.TryGetTable(Tags.Glyf, out ReadOnlyMemory<byte> original));
        Assert.True(Typeface.FromMemory(font).TryGetTable(Tags.Glyf, out original));
        Assert.Equal(original.ToArray(), written.ToArray());
    }

    [Theory]
    [InlineData("NotoSans-Regular.ttf", "AVo%é")]
    [InlineData("Roboto-Regular.ttf", "Ag%Å")]
    [InlineData("SourceSans3-Regular.otf", "AVo")]
    public void DamagedFontsFailOnlyWithDocumentedExceptions(string file, string text)
    {
        // A few glyphs, so each of the hundreds of writes is quick and the damage lands in structure, not bulk.
        Typeface face = TestFonts.Load(file);
        byte[] font = FontSubsetter.Subset(face, [.. new Font(face, 16).Shape(text).Glyphs]).Data.ToArray();
        Damage(font, 60, file.Length);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void DamagedCollectionsFailOnlyWithDocumentedExceptions() => Damage(TestFonts.Bytes("derived/NotoSansSerif.ttc"), 12, 7);

    private static void Damage(byte[] font, int rounds, int seed)
    {
        var random = new Random(seed);
        for (int round = 0; round < rounds; round++)
        {
            byte[] copy = (byte[])font.Clone();
            int region = round % 2 == 0 ? Math.Min(copy.Length, 600) : copy.Length;
            for (int k = 0; k < 4; k++)
            {
                copy[random.Next(region)] = (byte)random.Next(256);
            }

            foreach (WebFontFormat format in Enum.GetValues<WebFontFormat>())
            {
                try
                {
                    byte[] web = WebFontWriter.Write(copy, new WebFontOptions { Format = format });
                    foreach (Typeface back in FontLoader.LoadAll(web))
                    {
                        _ = back.FullName;
                        back.GetGlyphOutline(1, new TextSink());
                    }
                }
                catch (Exception e) when (e is EgamiException or ArgumentException)
                {
                }
            }
        }
    }

    [Fact]
    [Trait("Category", "Allocation")]
    public void TheGlyfTransformAllocatesLittleBeyondItsOutput()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("EGAMI_ALLOCATION_TESTS") == "1", "Allocation budgets run on demand: set EGAMI_ALLOCATION_TESTS=1.");
        Typeface face = TestFonts.Load("NotoSans-Regular.ttf");
        Assert.True(face.TryGetTable(Tags.Glyf, out ReadOnlyMemory<byte> glyf));
        Assert.True(face.TryGetTable(Tags.Loca, out ReadOnlyMemory<byte> loca));
        long fewest = long.MaxValue;
        int output = 0;
        for (int i = 0; i < 4; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            Woff2Glyf.Transformed t = Woff2Glyf.Transform(glyf.Span, loca.Span, face.GlyphCount, face.Head.IndexToLocFormat)!;
            fewest = Math.Min(fewest, GC.GetAllocatedBytesForCurrentThread() - before);
            output = t.Data.Length;
        }

        // The streams grow by doubling and are copied into the table once; instructions go straight from glyf: under three times the output.
        Assert.True(fewest < (3 * output) + (8 * face.GlyphCount), $"{fewest} B allocated for a {output}-byte transformed glyf ({glyf.Length} bytes in)");
    }

    internal static void AssertSameTables(Typeface expected, Typeface actual, bool glyfTransformed, bool woff2 = true)
    {
        Assert.Equal(expected.Tables.Select(t => t.Tag.Value).Order(), actual.Tables.Select(t => t.Tag.Value).Order());
        foreach (Parsing.TableRecord record in expected.Tables)
        {
            Assert.True(expected.TryGetTable(record.Tag, out ReadOnlyMemory<byte> a));
            Assert.True(actual.TryGetTable(record.Tag, out ReadOnlyMemory<byte> b));
            if (glyfTransformed && record.Tag == Tags.Glyf)
            {
                for (int g = 0; g < expected.GlyphCount; g++)
                {
                    Assert.True(CanonicalGlyph(expected, g).SequenceEqual(CanonicalGlyph(actual, g)), $"glyph {g} differs");
                }

                continue;
            }

            if (glyfTransformed && record.Tag == Tags.Loca)
            {
                continue;
            }

            byte[] bytes = a.ToArray();
            if (woff2 && record.Tag == Tags.Head)
            {
                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(16), (ushort)(BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(16)) | 0x0800));
            }

            Assert.True(bytes.AsSpan().SequenceEqual(b.Span), $"table {record.Tag} differs");
        }
    }

    internal static void AssertDrawsTheSame(Typeface expected, Typeface actual)
    {
        Assert.Equal(expected.GlyphCount, actual.GlyphCount);
        Assert.Equal(expected.FullName, actual.FullName);
        Assert.Equal(expected.Extents, actual.Extents);
        for (int g = 0; g < expected.GlyphCount; g++)
        {
            var sa = new TextSink();
            var sb = new TextSink();
            expected.GetGlyphOutline((ushort)g, sa);
            actual.GetGlyphOutline((ushort)g, sb);
            Assert.Equal(sa.Verbs, sb.Verbs);
            Assert.Equal(sa.Ops.SelectMany(o => o), sb.Ops.SelectMany(o => o));
            Assert.Equal(expected.GetAdvance((ushort)g), actual.GetAdvance((ushort)g));
            Assert.Equal(expected.GetLeftSideBearing((ushort)g), actual.GetLeftSideBearing((ushort)g));
            Assert.Equal(expected.GetGlyphExtents((ushort)g), actual.GetGlyphExtents((ushort)g));
            if (expected.Outlines == FontOutlines.TrueType && g % 7 == 0)
            {
                Egami.Fonts.Hinting.HintedGlyph? ha = expected.GetHintedGlyph((ushort)g, 13, TrueTypeTarget.GdiPlus), hb = actual.GetHintedGlyph((ushort)g, 13, TrueTypeTarget.GdiPlus);
                Assert.Equal(ha is null, hb is null);
                if (ha is not null)
                {
                    Assert.Equal(ha.X, hb!.X);
                    Assert.Equal(ha.Y, hb.Y);
                    Assert.Equal(ha.OnCurve, hb.OnCurve);
                    Assert.Equal(ha.ContourEnds, hb.ContourEnds);
                    Assert.Equal(ha.Advance, hb.Advance);
                }
            }
        }
    }

    // A glyph as WOFF2 keeps it: the header, contour ends, instructions, each point's on-curve and (first) overlap flags and its
    // coordinates; a composite's bytes up to its end. Flag encodings and padding may differ.
    private static byte[] CanonicalGlyph(Typeface face, int glyph)
    {
        ReadOnlySpan<byte> data = face.Glyf!.GetGlyphData(glyph, out _);
        if (data.Length < 10 || BinaryPrimitives.ReadInt16BigEndian(data) == 0)
        {
            return [];
        }

        int contours = BinaryPrimitives.ReadInt16BigEndian(data);
        var w = new List<byte>(data[..10].ToArray());
        if (contours < 0)
        {
            int at = 10, flags;
            bool instructions = false;
            do
            {
                flags = BinaryPrimitives.ReadUInt16BigEndian(data[at..]);
                instructions |= (flags & 0x100) != 0;
                at += 4 + ((flags & 1) != 0 ? 4 : 2) + ((flags & 8) != 0 ? 2 : (flags & 0x40) != 0 ? 4 : (flags & 0x80) != 0 ? 8 : 0);
            }
            while ((flags & 0x20) != 0);

            if (instructions)
            {
                at += 2 + BinaryPrimitives.ReadUInt16BigEndian(data[at..]);
            }

            w.AddRange(data[10..at].ToArray());
            return [.. w];
        }

        int p = 10 + (2 * contours);
        int count = BinaryPrimitives.ReadUInt16BigEndian(data[(p - 2)..]) + 1;
        int code = BinaryPrimitives.ReadUInt16BigEndian(data[p..]);
        w.AddRange(data[10..(p + 2 + code)].ToArray());
        p += 2 + code;
        var flagList = new byte[count];
        for (int i = 0; i < count;)
        {
            byte flag = data[p++];
            flagList[i++] = flag;
            if ((flag & 8) != 0)
            {
                for (int n = data[p++]; n > 0; n--)
                {
                    flagList[i++] = flag;
                }
            }
        }

        foreach (bool isX in new[] { true, false })
        {
            int value = 0;
            for (int i = 0; i < count; i++)
            {
                int flag = flagList[i], shortBit = isX ? 2 : 4, sameBit = isX ? 0x10 : 0x20;
                value += (flag & shortBit) != 0 ? ((flag & sameBit) != 0 ? data[p++] : -data[p++]) : (flag & sameBit) != 0 ? 0 : ReadShort(data, ref p);
                w.AddRange(BitConverter.GetBytes(value));
            }
        }

        for (int i = 0; i < count; i++)
        {
            w.Add((byte)((flagList[i] & 1) | (i == 0 ? flagList[i] & 0x40 : 0)));
        }

        return [.. w];
    }

    private static int ReadShort(ReadOnlySpan<byte> data, ref int at)
    {
        at += 2;
        return BinaryPrimitives.ReadInt16BigEndian(data[(at - 2)..]);
    }
}
