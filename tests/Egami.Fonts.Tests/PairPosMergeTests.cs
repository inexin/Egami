using Egami.Fonts.Shaping;
using Egami.Fonts.Unicode;

namespace Egami.Fonts.Tests;

/// <summary>
/// A variable face's static subset writes the PairPos format 2 subtables of a lookup as one where their first glyphs don't
/// overlap (2.7.10): the kerning is that of the subtables written apart (as extension lookups, which are never merged).
/// </summary>
public class PairPosMergeTests
{
    [Theory]
    [InlineData("derived/SourceSerif4-VF-subset.otf", 800f)]
    [InlineData("derived/SourceSans3-VF-subset.otf", 300f)]
    public void MergedPairsKernAsTheInstance(string file, float weight)
    {
        Typeface face = TestFonts.Load(file).WithVariations(FontVariation.Weight(weight));
        const string text = "AVTWYafjkovwxy.,";
        int[] characters = [.. text.Select(c => (int)c)];
        FontSubset subset = FontSubsetter.Subset(face, characters.Select(c => face.GetGlyph(c)), new SubsetOptions { Characters = characters, LayoutClosure = true });
        Typeface written = subset.ToTypeface();
        Typeface apart = FontSubsetter.Subset(face, characters.Select(c => face.GetGlyph(c)), new SubsetOptions { Characters = characters, LayoutClosure = true, ExtensionLookups = true }).ToTypeface();
        Assert.True(written.TryGetTable(new FontTag("GPOS"), out ReadOnlyMemory<byte> gpos));
        var properties = new RunProperties { Script = Script.Latin, Direction = TextDirection.LeftToRight };
        GlyphPosition[] Shape(Typeface typeface, string run)
        {
            GlyphBuffer buffer = new();
            TextShaper.ShapeRun(run, 0, run.Length, new Font(typeface, typeface.UnitsPerEm), properties, buffer);
            return buffer.Positions.ToArray();
        }

        float[] Kern(Typeface typeface, char a, char b)
        {
            GlyphPosition[] pair = Shape(typeface, $"{a}{b}"), first = Shape(typeface, $"{a}"), second = Shape(typeface, $"{b}");
            return [pair[0].XAdvance - first[0].XAdvance, pair[0].XOffset, pair[1].XAdvance - second[0].XAdvance, pair[1].XOffset];
        }

        int kerned = 0;
        foreach (char a in text)
        {
            foreach (char b in text)
            {
                float[] expected = Kern(apart, a, b), actual = Kern(written, a, b);
                Assert.True(expected.SequenceEqual(actual), $"{file} '{a}{b}': {string.Join(",", actual)}, expected {string.Join(",", expected)}");
                kerned += expected.Any(v => v != 0) ? 1 : 0;
            }
        }

        Assert.True(kerned > 10, $"{kerned} kerned pairs");
        Assert.True(LookupSubtables(gpos.Span, 0) <= 2, $"{LookupSubtables(gpos.Span, 0)} subtables");
    }

    private static int LookupSubtables(ReadOnlySpan<byte> gpos, int lookup)
    {
        static int U16(ReadOnlySpan<byte> d, int at) => (d[at] << 8) | d[at + 1];
        int list = U16(gpos, 8);
        int at = list + U16(gpos, list + 2 + (lookup * 2));
        return U16(gpos, at + 4);
    }
}
