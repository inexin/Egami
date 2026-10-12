using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace Egami.Fonts.Tests;

/// <summary>
/// WOFF and WOFF2 (2.7.2 tasks 2 and 3): every decoded table equals fontTools' decoding (WOFF) or Google's woff2_decompress
/// (WOFF2, whose glyf and loca rebuild is the reference one; fontTools pads rebuilt glyphs differently), and faces read from a web
/// font draw like the original.
/// </summary>
public class WebFontTests
{
    public static TheoryData<string> WebFonts() =>
    [
        "NotoSans-Regular.woff", "SourceSans3-Regular.woff", "NotoSans-Regular.woff2", "NotoSans-Regular-null.woff2",
        "Roboto-Regular-hmtx.woff2", "SourceSans3-Regular.woff2", "RobotoFlex-subset.woff2", "NotoSansSerif.woff2",
    ];

    [Theory]
    [MemberData(nameof(WebFonts))]
    public void DecodedTablesEqualTheReference(string file)
    {
        IReadOnlyList<Typeface> faces = FontLoader.LoadAll(TestFonts.FontPath("derived/" + file));
        JsonElement r = TestFonts.Reference("web/" + file);
        bool woff2 = file.EndsWith(".woff2", StringComparison.Ordinal);
        JsonElement expectedFaces = woff2 ? r.GetProperty("woff2Decompress") : r.GetProperty("faces");
        Assert.Equal(expectedFaces.GetArrayLength(), faces.Count);
        for (int f = 0; f < faces.Count; f++)
        {
            Typeface face = faces[f];
            Assert.Equal(woff2 ? FontFileKind.Woff2 : FontFileKind.Woff, face.Container);
            JsonElement tables = expectedFaces[f];
            Assert.Equal(tables.EnumerateObject().Count(), face.Tables.Count);
            foreach (JsonProperty table in tables.EnumerateObject())
            {
                Assert.True(face.TryGetTable(new FontTag(table.Name), out ReadOnlyMemory<byte> data), table.Name);
                byte[] bytes = data.ToArray();
                if (woff2 && table.Name == "head")
                {
                    // The reference decoder recomputes checkSumAdjustment for its own table layout; Egami keeps the stored one.
                    BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 0);
                    Assert.Equal(table.Value[0].GetInt32(), bytes.Length);
                    continue;
                }

                if (table.Name == "glyf" && r.TryGetProperty("overlapGlyphs", out JsonElement overlaps) && overlaps.GetArrayLength() > 0)
                {
                    // Debian's woff2 1.0.2 drops the overlap flags, so this glyf differs from it by design: check those glyphs keep
                    // the flag and that every glyph reads as fontTools' decoding does (FacesDrawLikeTheOriginal).
                    var set = overlaps.EnumerateArray().Select(e => e.GetInt32()).ToHashSet();
                    for (int g = 0; g < face.GlyphCount; g++)
                    {
                        Assert.Equal(set.Contains(g), FirstFlag(face, g) is int flag && (flag & 0x40) != 0);
                    }

                    continue;
                }

                Assert.Equal(table.Value[0].GetInt32(), bytes.Length);
                Assert.True(table.Value[1].GetString() == Convert.ToHexStringLower(SHA256.HashData(bytes)), $"{file} face {f} table {table.Name} differs");
            }
        }

        if (woff2 && r.TryGetProperty("faces", out JsonElement fontTools) && fontTools.GetArrayLength() > 0)
        {
            // fontTools agrees on every table but the rebuilt glyf, loca and head.
            foreach (JsonProperty table in fontTools[0].EnumerateObject())
            {
                if (table.Name is "glyf" or "loca" or "head")
                {
                    continue;
                }

                Assert.True(faces[0].TryGetTable(new FontTag(table.Name), out ReadOnlyMemory<byte> data));
                Assert.Equal(table.Value[1].GetString(), Convert.ToHexStringLower(SHA256.HashData(data.Span)));
            }
        }
    }

    [Fact]
    public void WoffMetadataAndPrivateDataAreKept()
    {
        Typeface face = TestFonts.Load("derived/NotoSans-Regular.woff");
        JsonElement r = TestFonts.Reference("web/NotoSans-Regular.woff");
        Assert.Equal(r.GetProperty("metadata").GetString(), face.GetWoffMetadata());
        Assert.Equal(r.GetProperty("private").GetString(), Convert.ToHexStringLower(face.GetWoffPrivateData().Span));
        Assert.Null(TestFonts.Load("derived/SourceSans3-Regular.woff").GetWoffMetadata());
        Assert.True(TestFonts.Load("NotoSans-Regular.ttf").GetWoffPrivateData().IsEmpty);
    }

    [Theory]
    [InlineData("derived/NotoSans-Regular.woff", "NotoSans-Regular.ttf", 0)]
    [InlineData("derived/NotoSans-Regular.woff2", "NotoSans-Regular.ttf", 0)]
    [InlineData("derived/NotoSans-Regular-null.woff2", "NotoSans-Regular.ttf", 0)]
    [InlineData("derived/Roboto-Regular-hmtx.woff2", "Roboto-Regular.ttf", 0)]
    [InlineData("derived/SourceSans3-Regular.woff2", "SourceSans3-Regular.otf", 0)]
    [InlineData("derived/NotoSansSerif.woff2", "derived/NotoSansSerif.ttc", 1)]
    public void FacesDrawLikeTheOriginal(string web, string original, int index)
    {
        Typeface a = TestFonts.Load(web, index);
        Typeface b = TestFonts.Load(original, index);
        Assert.Equal(b.GlyphCount, a.GlyphCount);
        Assert.Equal(b.FullName, a.FullName);
        Assert.Equal(b.Extents, a.Extents);
        for (int g = 0; g < a.GlyphCount; g += Math.Max(1, a.GlyphCount / 400))
        {
            var sa = new TextSink();
            var sb = new TextSink();
            a.GetGlyphOutline((ushort)g, sa);
            b.GetGlyphOutline((ushort)g, sb);
            Assert.Equal(sb.Verbs, sa.Verbs);
            Assert.Equal(sb.Ops.SelectMany(o => o), sa.Ops.SelectMany(o => o));
            Assert.Equal(b.GetAdvance((ushort)g), a.GetAdvance((ushort)g));
            Assert.Equal(b.GetLeftSideBearing((ushort)g), a.GetLeftSideBearing((ushort)g));
            Assert.Equal(b.GetGlyphExtents((ushort)g), a.GetGlyphExtents((ushort)g));
        }
    }

    [Fact]
    public void ProbeReportsWebFonts()
    {
        Assert.Equal(new FontProbe(FontFileKind.Woff, 1, FontOutlines.TrueType), FontLoader.Probe(TestFonts.Bytes("derived/NotoSans-Regular.woff")));
        Assert.Equal(new FontProbe(FontFileKind.Woff, 1, FontOutlines.Cff), FontLoader.Probe(TestFonts.Bytes("derived/SourceSans3-Regular.woff")));
        Assert.Equal(new FontProbe(FontFileKind.Woff2, 1, FontOutlines.TrueType), FontLoader.Probe(TestFonts.Bytes("derived/NotoSans-Regular.woff2")));
        Assert.Equal(new FontProbe(FontFileKind.Woff2, 1, FontOutlines.Cff), FontLoader.Probe(TestFonts.Bytes("derived/SourceSans3-Regular.woff2")));
        Assert.Equal(new FontProbe(FontFileKind.Woff2, 2, FontOutlines.TrueType), FontLoader.Probe(TestFonts.Bytes("derived/NotoSansSerif.woff2")));
        Assert.Equal(FontFileKind.Sfnt, TestFonts.Load("NotoSans-Regular.ttf").Container);
        Assert.Equal(FontFileKind.Collection, TestFonts.Load("derived/NotoSansSerif.ttc", 1).Container);
    }

    [Fact]
    public void CollectionsScanWebFonts()
    {
        string dir = Path.Combine(Path.GetTempPath(), "egami-web-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.Copy(TestFonts.FontPath("derived/NotoSansSerif.woff2"), Path.Combine(dir, "a.woff2"));
            File.Copy(TestFonts.FontPath("derived/SourceSans3-Regular.woff"), Path.Combine(dir, "b.woff"));
            var collection = new FontCollection();
            Assert.Equal(3, collection.AddDirectory(dir));
            Assert.True(collection.TryGetFamily("Noto Serif", out FontFamily serif));
            Assert.Equal(FontFileKind.Woff2, serif.Match(FontStyle.Normal).Typeface.Container);
            Assert.NotNull(collection.MatchTypeface("Source Sans 3", FontStyle.Normal));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void LimitsApplyBeforeDecoding()
    {
        var options = new FontLoadOptions { Context = EgamiContext.Default with { Limits = SafetyLimits.Default with { MaxTotalBytes = 300_000 } } };
        Assert.Throws<LimitExceededException>(() => Typeface.FromMemory(TestFonts.Bytes("derived/NotoSans-Regular.woff2"), 0, options));
        Assert.Throws<LimitExceededException>(() => Typeface.FromMemory(TestFonts.Bytes("derived/SourceSans3-Regular.woff"), 0, options));
    }

    [Theory]
    [MemberData(nameof(WebFonts))]
    public void DamagedWebFontsFailOnlyWithDocumentedExceptions(string file)
    {
        byte[] font = TestFonts.Bytes("derived/" + file);
        var random = new Random(file.Length);
        for (int length = 0; length < font.Length; length += Math.Max(1, font.Length / 61))
        {
            Exercise(font.AsSpan(0, length).ToArray());
        }

        for (int round = 0; round < 60; round++)
        {
            byte[] copy = (byte[])font.Clone();
            int region = round < 30 ? Math.Min(copy.Length, 600) : copy.Length;
            for (int k = 0; k < 4; k++)
            {
                copy[random.Next(region)] = (byte)random.Next(256);
            }

            Exercise(copy);
        }
    }

    // The first point's flag of a simple glyph, or null.
    private static int? FirstFlag(Typeface face, int glyph)
    {
        ReadOnlySpan<byte> data = face.Glyf!.GetGlyphData(glyph, out _);
        if (data.Length < 10)
        {
            return null;
        }

        int contours = BinaryPrimitives.ReadInt16BigEndian(data);
        if (contours <= 0)
        {
            return null;
        }

        int at = 10 + (2 * contours);
        int instructions = BinaryPrimitives.ReadUInt16BigEndian(data[at..]);
        return data[at + 2 + instructions];
    }

    private static void Exercise(byte[] data)
    {
        try
        {
            foreach (Typeface face in FontLoader.LoadAll(data))
            {
                _ = face.FullName;
                _ = face.Extents;
                var sink = new TextSink();
                for (int g = 0; g < Math.Min(face.GlyphCount, 30); g++)
                {
                    face.GetGlyphOutline((ushort)g, sink);
                    _ = face.GetAdvance((ushort)g);
                }
            }
        }
        catch (EgamiException)
        {
        }
    }
}
