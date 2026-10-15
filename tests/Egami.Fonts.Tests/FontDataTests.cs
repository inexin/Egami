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
