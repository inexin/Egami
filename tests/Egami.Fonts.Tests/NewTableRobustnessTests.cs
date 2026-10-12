using System.Buffers.Binary;

namespace Egami.Fonts.Tests;

/// <summary>
/// Robustness properties for 2.7.2's tables (task 15): fvar, avar, gvar, HVAR, MVAR, GDEF, CFF2, COLR, CPAL, sbix, CBDT, CBLC and
/// SVG cut short, scrambled, given huge deltas or cycles, never throw anything but the documented exceptions, leniently or
/// strictly, at the default and at other positions.
/// </summary>
public class NewTableRobustnessTests
{
    private static readonly string[] s_tables = ["fvar", "avar", "gvar", "HVAR", "MVAR", "GDEF", "GPOS", "CFF2", "COLR", "CPAL", "sbix", "CBDT", "CBLC", "SVG "];

    public static TheoryData<string> Seeds() =>
    [
        "derived/NotoSans-VF-subset.ttf", "derived/NotoSans-avar2.ttf", "derived/SourceSans3-VF-subset.otf", "test_glyphs-glyf_colr_1_variable.ttf",
        "derived/Twemoji-COLRv0-subset.ttf", "derived/Twemoji-sbix.ttf", "derived/Svg-test.ttf",
    ];

    [Theory]
    [MemberData(nameof(Seeds))]
    public void TablesCutShortFailOnlyWithDocumentedExceptions(string seed)
    {
        byte[] font = TestFonts.Bytes(seed);
        foreach ((int record, uint length) in Records(font))
        {
            foreach (uint cut in new uint[] { 0, 1, 3, 9, length / 3, length / 2, length - 1 })
            {
                byte[] copy = (byte[])font.Clone();
                BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(record + 12), Math.Min(cut, length));
                Exercise(copy);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void ScrambledTablesFailOnlyWithDocumentedExceptions(string seed)
    {
        byte[] font = TestFonts.Bytes(seed);
        var random = new Random(seed.Length * 17);
        foreach ((int record, uint length) in Records(font))
        {
            int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(record + 8));
            for (int round = 0; round < 8; round++)
            {
                byte[] copy = (byte[])font.Clone();
                int region = (int)Math.Min(length, 48 + (round * 300));
                for (int k = 0; k < 6 && region > 0; k++)
                {
                    copy[offset + random.Next(region)] = round % 2 == 0 ? (byte)random.Next(256) : (byte)0xFF;
                }

                Exercise(copy);
            }
        }
    }

    [Fact]
    public void ColrCyclesAreCut()
    {
        // test_glyphs' PaintColrGlyph cycle glyphs: drawn as far as the cycle, without stack overflow.
        Typeface face = TestFonts.Load("test_glyphs-glyf_colr_1.ttf");
        var seen = new List<EgamiDiagnostic>();
        Typeface reported = Typeface.FromMemory(TestFonts.Bytes("test_glyphs-glyf_colr_1.ttf"), 0, new FontLoadOptions { Context = EgamiContext.Default with { Diagnostics = seen.Add } });
        Assert.True(reported.TryGetColorGlyph(178, null, out ColorGlyph? glyph));
        Assert.Contains(seen, d => d.Id == "Font.BadColorGlyph");
        Assert.IsType<PaintColorGlyph>(glyph);
        _ = face;
    }

    private static IEnumerable<(int Record, uint Length)> Records(byte[] font)
    {
        int tables = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(4));
        for (int t = 0; t < tables; t++)
        {
            int record = 12 + (t * 16);
            string tag = System.Text.Encoding.ASCII.GetString(font, record, 4);
            if (s_tables.Contains(tag))
            {
                yield return (record, BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(record + 12)));
            }
        }
    }

    private static void Exercise(byte[] data)
    {
        foreach (bool strict in new[] { false, true })
        {
            try
            {
                Typeface face = Typeface.FromMemory(data, 0, new FontLoadOptions { Strict = strict });
                var positions = new List<Typeface> { face };
                if (face.IsVariable)
                {
                    positions.Add(face.WithVariations([.. face.VariationAxes.Select(a => new FontVariation(a.Tag, a.Max))]));
                    positions.Add(face.WithVariations([.. face.VariationAxes.Select(a => new FontVariation(a.Tag, a.Min))]));
                    if (face.NamedInstances.Count > 0)
                    {
                        positions.Add(face.WithNamedInstance(face.NamedInstances.Count - 1));
                    }
                }

                foreach (Typeface at in positions)
                {
                    _ = at.FullName + at.PostScriptName;
                    _ = at.Extents;
                    _ = at.Style;
                    _ = at.ColorFormats;
                    _ = at.Palettes.Count;
                    var sink = new TextSink();
                    for (int g = 0; g < Math.Min(at.GlyphCount, 30); g++)
                    {
                        at.GetGlyphOutline((ushort)g, sink);
                        _ = at.GetGlyphExtents((ushort)g);
                        _ = at.GetAdvance((ushort)g);
                        _ = at.GetLeftSideBearing((ushort)g);
                        _ = at.GetKerning((ushort)g, 3);
                        _ = at.TryGetColorGlyph((ushort)g, null, out _, 20);
                        _ = at.TryGetSvgDocument((ushort)g, out _, out _, out _);
                    }

                    if (at.Outlines != FontOutlines.None)
                    {
                        _ = FontSubsetter.Subset(at, [0, 1, 2]).ToTypeface().GlyphCount;
                    }
                }
            }
            catch (EgamiException)
            {
            }
            catch (ArgumentOutOfRangeException e) when (e.ParamName == "glyphs")
            {
                // A face with fewer than three glyphs.
            }
        }
    }
}
