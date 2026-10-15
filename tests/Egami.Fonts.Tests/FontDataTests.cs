using System.IO.Compression;
using System.Text.Json;

namespace Egami.Fonts.Tests;

/// <summary>
/// Phase 2.7.3: the font data PDF needs, checked against FreeType and fontTools (tests/reference/fonts/fontdata, written by
/// tools/fontref/dump_273.py): CFF outlines through each Font DICT's FontMatrix, glyph names, CIDs, cmap subtables and built-in
/// encodings.
/// </summary>
public class FontDataTests
{
    public static TheoryData<string> BareCffFiles => new(
        "derived/SourceSans3.cff",
        "derived/SourceSans3-custom.cff",
        "derived/NotoSansCJK-subset.cff",
        "derived/NotoSansCJK-fd2048.cff",
        "derived/NotoSansCJK-fdmatrix.cff");

    public static TheoryData<string> AllFiles
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (string path in Directory.EnumerateFiles(Path.Combine(TestFonts.Root, "tests", "reference", "fonts", "fontdata")).Order(StringComparer.Ordinal))
            {
                string name = Path.GetFileName(path)[..^".json.gz".Length];
                if (name == "cff-standard")
                {
                    continue;
                }

                data.Add(name.StartsWith("derived_", StringComparison.Ordinal) ? "derived/" + name["derived_".Length..] : name);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(AllFiles))]
    public void FontDataEqualsFreeTypeAndFontTools(string file)
    {
        JsonElement faces = Reference(file);
        for (int index = 0; index < faces.GetArrayLength(); index++)
        {
            JsonElement expected = faces[index];
            Typeface face = file.EndsWith(".cff", StringComparison.Ordinal) ? Typeface.FromCff(TestFonts.Bytes(file)) : TestFonts.Load(file, index);

            // Names, both ways: FreeType's, or none.
            JsonElement names = expected.GetProperty("names");
            if (names.ValueKind == JsonValueKind.Null)
            {
                Assert.Null(face.GetGlyphName(0));
            }
            else
            {
                Assert.Equal(names.GetArrayLength(), face.GlyphCount);
                var firstByName = new Dictionary<string, ushort>(StringComparer.Ordinal);
                for (ushort g = 0; g < face.GlyphCount; g++)
                {
                    string name = names[g].GetString()!;
                    Assert.True(name == face.GetGlyphName(g), $"{file} glyph {g}: {face.GetGlyphName(g)}, FreeType {name}");
                    firstByName.TryAdd(name, g);
                }

                foreach ((string name, ushort g) in firstByName)
                {
                    Assert.True(face.TryGetGlyphByName(name, out ushort found) && found == g, $"{file} name {name}: {found}, expected {g}");
                }
            }

            Assert.Null(face.GetGlyphName((ushort)face.GlyphCount));
            Assert.False(face.TryGetGlyphByName("no such glyph", out _));

            // CIDs and the character collection.
            if (expected.TryGetProperty("cid", out JsonElement cid) && cid.ValueKind != JsonValueKind.Null)
            {
                Assert.Equal(new CidSystemInfo(cid.GetProperty("registry").GetString()!, cid.GetProperty("ordering").GetString()!, cid.GetProperty("supplement").GetInt32()), face.CidSystemInfo);
                JsonElement cids = cid.GetProperty("cids");
                Assert.Equal(cids.GetArrayLength(), face.GlyphCount);
                for (ushort g = 0; g < face.GlyphCount; g++)
                {
                    int expectedCid = cids[g].GetInt32();
                    Assert.Equal(expectedCid, face.GetCid(g));
                    Assert.True(face.TryGetGlyphByCid(expectedCid, out ushort back) && face.GetCid(back) == expectedCid);
                }
            }
            else
            {
                Assert.Null(face.CidSystemInfo);
                Assert.Equal(-1, face.GetCid(0));
                Assert.False(face.TryGetGlyphByCid(0, out _));
            }

            // Every cmap subtable but format 14, as stored.
            JsonElement cmaps = expected.GetProperty("cmaps");
            Assert.Equal(cmaps.GetArrayLength(), face.CharacterMaps.Count);
            for (int i = 0; i < cmaps.GetArrayLength(); i++)
            {
                JsonElement c = cmaps[i];
                CharacterMap map = face.CharacterMaps[i];
                Assert.Equal((c.GetProperty("platform").GetInt32(), c.GetProperty("encoding").GetInt32(), c.GetProperty("format").GetInt32(), c.GetProperty("language").GetInt32()), (map.PlatformId, map.EncodingId, map.Format, map.Language));
                AssertMappings(file, map, c.GetProperty("map"));
                Assert.Same(map, face.FindCharacterMap(map.PlatformId, map.EncodingId) == map ? map : face.CharacterMaps.First(m => m.PlatformId == map.PlatformId && m.EncodingId == map.EncodingId));
            }

            // A CFF font's own encoding, as FreeType's platform 7 charmap.
            JsonElement builtin = expected.GetProperty("builtin");
            if (builtin.GetArrayLength() == 0)
            {
                Assert.Null(face.BuiltInEncoding);
            }
            else
            {
                CharacterMap own = face.BuiltInEncoding!;
                Assert.Equal((CharacterMap.AdobePlatform, builtin[0].GetProperty("encoding").GetInt32()), (own.PlatformId, own.EncodingId));
                Assert.Same(own, face.FindCharacterMap(CharacterMap.AdobePlatform, own.EncodingId));
                // FreeType's Expert Encoding gives code 175 Dotaccentsmall, as 172; Egami gives it Macronsmall, as pdf.js does.
                AssertMappings(file, own, builtin[0].GetProperty("map"), own.EncodingId == 1 ? 175 : -1);
                if (own.EncodingId == 1 && face.TryGetGlyphByName("Macronsmall", out ushort macronGlyph))
                {
                    Assert.Equal(macronGlyph, own.GetGlyph(175));
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(BareCffFiles))]
    public void CffOutlinesAndAdvancesEqualFreeType(string file)
    {
        Typeface face = Typeface.FromCff(TestFonts.Bytes(file));
        JsonElement outlines = Reference(file)[0].GetProperty("outlines");
        Assert.Equal(outlines.GetArrayLength(), face.GlyphCount);
        float scale = 1000f / face.UnitsPerEm;

        // FreeType rounds a slanted glyph's points to whole font units; a plain scale it keeps to 1/64 of a pixel.
        float tolerance = file.EndsWith("fdmatrix.cff", StringComparison.Ordinal) ? 0.51f : 0.05f;
        for (ushort g = 0; g < face.GlyphCount; g++)
        {
            JsonElement expected = outlines[g];
            Assert.True(Math.Abs((face.GetAdvance(g) * scale) - expected.GetProperty("advance").GetSingle()) < 0.05f, $"{file} glyph {g} advance {face.GetAdvance(g) * scale}");
            var points = new PointSink();
            face.GetGlyphOutline(g, points);
            points.Close();
            float[][] reference = [.. expected.GetProperty("points").EnumerateArray().Select(p => new[] { p[0].GetSingle(), p[1].GetSingle() })];
            Assert.True(reference.Length == points.Points.Count, $"{file} glyph {g}: {points.Points.Count} points, FreeType {reference.Length}");
            for (int i = 0; i < reference.Length; i++)
            {
                (float x, float y) = points.Points[i];
                Assert.True(Math.Abs((x * scale) - reference[i][0]) < tolerance && Math.Abs((y * scale) - reference[i][1]) < tolerance, $"{file} glyph {g} point {i}: ({x * scale}, {y * scale}), FreeType ({reference[i][0]}, {reference[i][1]})");
            }
        }
    }

    [Fact]
    public void FontDictMatrixSetsUnitsPerEm()
    {
        // pdf.js's text_clip_cff_cid.pdf: every Font DICT scales by 1/2048 and the Top DICT gives no FontMatrix.
        Typeface original = Typeface.FromCff(TestFonts.Bytes("derived/NotoSansCJK-subset.cff"));
        Typeface scaled = Typeface.FromCff(TestFonts.Bytes("derived/NotoSansCJK-fd2048.cff"));
        Assert.Equal(1000, original.UnitsPerEm);
        Assert.Equal(2048, scaled.UnitsPerEm);
        Assert.Equal(original.GetAdvance(40), scaled.GetAdvance(40));
        Assert.Equal(original.GetGlyphExtents(40), scaled.GetGlyphExtents(40));

        // Font DICTs that differ: the Top DICT's default decides the units, and each glyph goes through its own matrix.
        Typeface mixed = Typeface.FromCff(TestFonts.Bytes("derived/NotoSansCJK-fdmatrix.cff"));
        Assert.Equal(1000, mixed.UnitsPerEm);
        Assert.Equal(original.GetAdvance(300) * 1000 / 2048, mixed.GetAdvance(300), 0.01f);
    }

    [Fact]
    public void FontDictMatrixSurvivesSubsetting()
    {
        Typeface mixed = Typeface.FromCff(TestFonts.Bytes("derived/NotoSansCJK-fdmatrix.cff"));
        FontSubset subset = FontSubsetter.Subset(mixed, [40, 300], new SubsetOptions { Format = SubsetFormat.BareCff });
        Typeface again = subset.ToTypeface();
        for (int i = 0; i < 2; i++)
        {
            ushort old = (ushort)(i == 0 ? 40 : 300), now = subset.GlyphMap[old];
            Assert.Equal(mixed.GetAdvance(old), again.GetAdvance(now), 0.01f);
            Assert.Equal(mixed.GetGlyphExtents(old), again.GetGlyphExtents(now));
        }
    }

    [Fact]
    public void CffStandardStringsEqualFontTools()
    {
        JsonElement r = Reference("cff-standard");
        JsonElement strings = r.GetProperty("strings");
        Assert.Equal(CffStandardStrings.Count, strings.GetArrayLength());
        for (int sid = 0; sid < CffStandardStrings.Count; sid++)
        {
            string name = strings[sid].GetString()!;
            Assert.Equal(name, CffStandardStrings.Get(sid));
            Assert.True(CffStandardStrings.TryGetSid(name, out int back) && back == sid);
        }

        Assert.Equal(r.GetProperty("standardEncoding").EnumerateArray().Select(e => e.GetString()), CffStandardStrings.StandardEncoding);
        Assert.Equal(256, CffStandardStrings.ExpertEncoding.Count);
        Assert.Equal("Macronsmall", CffStandardStrings.ExpertEncoding[175]);
        Assert.Equal("Ydieresissmall", CffStandardStrings.ExpertEncoding[255]);
        Assert.False(CffStandardStrings.TryGetSid("Macron", out int none));
        Assert.Equal(-1, none);
        Assert.Throws<ArgumentOutOfRangeException>(() => CffStandardStrings.Get(391));
        Assert.Throws<ArgumentOutOfRangeException>(() => CffStandardStrings.Get(-1));

        static string[] Names(ushort[] sids) => [.. sids.Select(sid => CffStandardStrings.Get(sid))];
        Assert.Equal(r.GetProperty("expertCharset").EnumerateArray().Select(e => e.GetString()), Names(Cff.CffStandard.PredefinedCharset(1)));
        Assert.Equal(r.GetProperty("expertSubsetCharset").EnumerateArray().Select(e => e.GetString()), Names(Cff.CffStandard.PredefinedCharset(2)));
    }

    [Fact]
    public void InstancesShareTheirFontsData()
    {
        Typeface font = TestFonts.Load("derived/SourceSans3-VF-subset.otf");
        Typeface bold = font.WithVariations([new FontVariation(new FontTag("wght"), 700)]);
        Assert.Same(font.CharacterMaps, bold.CharacterMaps);
        Assert.Equal(font.GetGlyphName(5), bold.GetGlyphName(5));
        Assert.Equal(font.FindCharacterMap(3, 1)!.GetGlyph('A'), bold.FindCharacterMap(3, 1)!.GetGlyph('A'));
        Assert.Equal(font.BuiltInEncoding, bold.BuiltInEncoding);
    }

    [Fact]
    public void LookupsAreThreadSafe()
    {
        byte[] data = TestFonts.Bytes("DejaVuSans.ttf");
        for (int round = 0; round < 4; round++)
        {
            Typeface face = Typeface.FromMemory(data);
            var found = new int[8];
            Parallel.For(0, 8, i =>
            {
                found[i] = face.TryGetGlyphByName("Aacute", out ushort g) ? g : -1;
                found[i] += face.FindCharacterMap(3, 1)!.GetGlyph(0xC1) - g;
            });
            Assert.All(found, f => Assert.Equal(found[0], f));
            Assert.True(found[0] > 0);
        }
    }

    [Fact]
    public void DamagedDataGivesWarningsNotExceptions()
    {
        // A post table version 2 whose name indices run past its strings, and a cmap with a format 8 subtable (which Egami does
        // not read) and a cut-off format 0 one.
        byte[] data = TestFonts.Bytes("derived/NotoSans-mac.ttf");
        byte[] font = WithTable(data, "post", PostVersion2(glyphCount: 3, indices: [0, 300, 3], strings: []));
        font = WithTable(font, "cmap", [0, 0, 0, 2, 0, 1, 0, 0, 0, 0, 0, 20, 0, 3, 0, 1, 0, 0, 0, 28, 0, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        var diagnostics = new List<string>();
        var options = new FontLoadOptions { Context = EgamiContext.Default with { Diagnostics = d => diagnostics.Add(d.Message) } };
        Typeface face = Typeface.FromMemory(font, 0, options);
        Assert.Equal(".notdef", face.GetGlyphName(0));
        Assert.Null(face.GetGlyphName(1));
        Assert.Equal("space", face.GetGlyphName(2));
        Assert.Null(face.GetGlyphName(3));
        Assert.Equal(2, face.CharacterMaps.Count);
        Assert.Equal((1, 0, 8), (face.CharacterMaps[0].PlatformId, face.CharacterMaps[0].EncodingId, face.CharacterMaps[0].Format));
        Assert.Equal(0, face.CharacterMaps[0].GetGlyph(32));
        Assert.Equal((3, 1, 0), (face.CharacterMaps[1].PlatformId, face.CharacterMaps[1].EncodingId, face.CharacterMaps[1].Format));
        Assert.Empty(face.CharacterMaps[1].GetMappings());
        Assert.Contains(diagnostics, m => m.Contains("post", StringComparison.Ordinal));
        Assert.Contains(diagnostics, m => m.Contains("(1,0) cmap subtable", StringComparison.Ordinal));
        Assert.Contains(diagnostics, m => m.Contains("(3,1) cmap subtable", StringComparison.Ordinal));

        Assert.Throws<CorruptDataException>(() => Typeface.FromMemory(font, 0, new FontLoadOptions { Strict = true }).GetGlyphName(1));
    }

    private static byte[] PostVersion2(int glyphCount, ushort[] indices, string[] strings)
    {
        var bytes = new List<byte> { 0, 2, 0, 0 };
        bytes.AddRange(new byte[28]);
        bytes.Add((byte)(glyphCount >> 8));
        bytes.Add((byte)glyphCount);
        foreach (ushort index in indices)
        {
            bytes.Add((byte)(index >> 8));
            bytes.Add((byte)index);
        }

        foreach (string s in strings)
        {
            bytes.Add((byte)s.Length);
            bytes.AddRange(System.Text.Encoding.Latin1.GetBytes(s));
        }

        return [.. bytes];
    }

    /// <summary>Replaces one table of an sfnt (checksums are not checked by Egami), appending the new bytes at the end.</summary>
    private static byte[] WithTable(byte[] font, string tag, byte[] table)
    {
        int count = (font[4] << 8) | font[5];
        byte[] result = [.. font, .. new byte[(4 - (font.Length % 4)) % 4], .. table];
        int start = font.Length + ((4 - (font.Length % 4)) % 4);
        for (int i = 0; i < count; i++)
        {
            int record = 12 + (16 * i);
            if (System.Text.Encoding.ASCII.GetString(font, record, 4) == tag)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(record + 8), start);
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(record + 12), table.Length);
                return result;
            }
        }

        throw new InvalidOperationException($"The font has no {tag} table.");
    }

    [Fact]
    public void ReservedDictOperatorsAreSkipped()
    {
        // pdf.js's bug1308536.pdf has a Type1C font whose DICT uses the reserved operator 25; FreeType, PDFium and pdf.js skip it.
        byte[] original = TestFonts.Bytes("derived/SourceSans3-custom.cff");
        byte[] patched = (byte[])original.Clone();
        int at = TopDictStart(patched, out int end);
        int op = -1;
        for (int i = at; i < end;)
        {
            int b0 = patched[i];
            if (b0 <= 21)
            {
                if (b0 is 0 or 1)
                {
                    op = i;
                    break;
                }

                i += b0 == 12 ? 2 : 1;
            }
            else
            {
                i += b0 switch { 28 => 3, 29 => 5, 30 => RealLength(patched, i), >= 247 => 2, _ => 1 };
            }
        }

        Assert.True(op > 0);
        patched[op] = 25;
        Typeface face = Typeface.FromCff(patched);
        Typeface reference = Typeface.FromCff(original);
        Assert.Equal(reference.GlyphCount, face.GlyphCount);
        Assert.Equal(reference.GetGlyphExtents(5), face.GetGlyphExtents(5));
        Assert.Equal(reference.GetGlyphName(5), face.GetGlyphName(5));

        Cff.CffDict dict = Cff.CffDict.Read([139, 140, 31, 141, 255, 142, 17], 0);
        Assert.Equal([3.0], dict.Get(17)!);
    }

    private static int TopDictStart(byte[] cff, out int end)
    {
        int at = SkipIndex(cff, cff[2]);
        int count = (cff[at] << 8) | cff[at + 1];
        int offSize = cff[at + 2];
        int Offset(int k)
        {
            int value = 0;
            for (int b = 0; b < offSize; b++)
            {
                value = (value << 8) | cff[at + 3 + (k * offSize) + b];
            }

            return value;
        }

        int dataStart = at + 3 + ((count + 1) * offSize) - 1;
        end = dataStart + Offset(1);
        return dataStart + Offset(0);
    }

    private static int SkipIndex(byte[] cff, int at)
    {
        int count = (cff[at] << 8) | cff[at + 1];
        if (count == 0)
        {
            return at + 2;
        }

        int offSize = cff[at + 2];
        int last = 0;
        for (int b = 0; b < offSize; b++)
        {
            last = (last << 8) | cff[at + 3 + (count * offSize) + b];
        }

        return at + 3 + ((count + 1) * offSize) - 1 + last;
    }

    private static int RealLength(byte[] data, int at)
    {
        int i = at + 1;
        while ((data[i] & 0xF) != 0xF && (data[i] >> 4) != 0xF)
        {
            i++;
        }

        return i - at + 1;
    }

    private static void AssertMappings(string file, CharacterMap map, JsonElement expected, int skipCode = -1)
    {
        CharacterMapping[] actual = [.. map.GetMappings().Where(m => m.Code != skipCode)];
        CharacterMapping[] reference = [.. expected.EnumerateArray().Select(p => new CharacterMapping(p[0].GetInt32(), (ushort)p[1].GetInt32())).Where(m => m.Code != skipCode)];
        Assert.True(reference.Length == actual.Length, $"{file} {map}: {actual.Length} mappings, reference {reference.Length}");
        for (int i = 0; i < actual.Length; i++)
        {
            Assert.True(reference[i] == actual[i], $"{file} {map}: {actual[i]}, reference {reference[i]}");
            Assert.Equal(reference[i].Glyph, map.GetGlyph(reference[i].Code));
        }
    }

    internal static JsonElement Reference(string file)
    {
        string path = Path.Combine(TestFonts.Root, "tests", "reference", "fonts", "fontdata", file.Replace('/', '_') + ".json.gz");
        using var gzip = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
        return JsonDocument.Parse(gzip).RootElement.Clone();
    }

    private sealed class PointSink : IGlyphOutlineSink
    {
        private int _start = -1;

        // FreeType leaves out a contour's last point when it repeats the first.
        public List<(float X, float Y)> Points { get; } = [];

        public void MoveTo(float x, float y)
        {
            Close();
            _start = Points.Count;
            Points.Add((x, y));
        }

        public void LineTo(float x, float y) => Points.Add((x, y));

        public void QuadTo(float cx, float cy, float x, float y)
        {
            Points.Add((cx, cy));
            Points.Add((x, y));
        }

        public void CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y)
        {
            Points.Add((c1x, c1y));
            Points.Add((c2x, c2y));
            Points.Add((x, y));
        }

        public void Close()
        {
            if (_start >= 0 && Points.Count - _start > 1 && Points[^1] == Points[_start])
            {
                Points.RemoveAt(Points.Count - 1);
            }

            _start = -1;
        }
    }
}
