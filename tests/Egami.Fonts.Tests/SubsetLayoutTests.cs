using Egami.Fonts.Shaping;
using Egami.Fonts.Unicode;

namespace Egami.Fonts.Tests;

/// <summary>SubsetOptions.LayoutClosure: the subset keeps what GSUB can substitute, and its layout tables, so it shapes as the full face.</summary>
public sealed class SubsetLayoutTests
{
    private const string Arabic = "\u0628\u0650\u0633\u0652\u0645\u0650 \u0627\u0644\u0644\u0651\u0647\u0650 \u0644\u0627 \u0643\u062A\u0627\u0628";

    [Fact]
    public void LatinSubsetKeepsTheLigatureAndShapesAsTheFace()
    {
        Typeface face = TestFonts.Load("NotoSans-Regular.ttf");
        Typeface subset = Subset(face, "office", out FontSubset written);
        GlyphBuffer full = Shape(face, "office", Script.Latin, TextDirection.LeftToRight);
        GlyphBuffer sub = Shape(subset, "office", Script.Latin, TextDirection.LeftToRight);

        Assert.True(full.Count < 6, "The full face forms the ffi ligature.");
        Assert.All(full.GlyphIds.ToArray(), g => Assert.True(written.GlyphMap.ContainsKey(g)));
        AssertSame(full, sub);
        Assert.True(subset.TryGetTable(new FontTag("GSUB"), out _));
        Assert.True(subset.TryGetTable(new FontTag("GPOS"), out _));
        Assert.True(subset.TryGetTable(new FontTag("GDEF"), out _));
    }

    [Fact]
    public void WithoutItTheSubsetHasNoLayoutTables()
    {
        Typeface face = TestFonts.Load("NotoSans-Regular.ttf");
        ushort[] glyphs = [.. "office".Select(c => face.GetGlyph(c))];
        Typeface subset = FontSubsetter.Subset(face, glyphs, new SubsetOptions { RetainGlyphIds = true }).ToTypeface();

        Assert.False(subset.TryGetTable(new FontTag("GSUB"), out _));
        Assert.False(subset.TryGetTable(new FontTag("GPOS"), out _));
        Assert.Equal(6, Shape(subset, "office", Script.Latin, TextDirection.LeftToRight).Count);
    }

    [Fact]
    public void ArabicSubsetShapesAsTheFace()
    {
        Typeface face = TestFonts.Load("shaping/NotoSansArabic-Regular.ttf");
        Typeface subset = Subset(face, Arabic, out _);
        AssertSame(Shape(face, Arabic, Script.Arabic, TextDirection.RightToLeft), Shape(subset, Arabic, Script.Arabic, TextDirection.RightToLeft));
    }

    [Fact]
    public void CffSubsetShapesAsTheFace()
    {
        Typeface face = TestFonts.Load("SourceSans3-Regular.otf");
        Typeface subset = Subset(face, "office fjord", out _);
        AssertSame(Shape(face, "office fjord", Script.Latin, TextDirection.LeftToRight), Shape(subset, "office fjord", Script.Latin, TextDirection.LeftToRight));
    }

    [Fact]
    public void ItNeedsRetainedIdsAndSfntOutput()
    {
        Typeface face = TestFonts.Load("SourceSans3-Regular.otf");
        Assert.Throws<ArgumentException>(() => FontSubsetter.Subset(face, [5], new SubsetOptions { LayoutClosure = true }));
        Assert.Throws<ArgumentException>(() => FontSubsetter.Subset(face, [5], new SubsetOptions { LayoutClosure = true, RetainGlyphIds = true, Format = SubsetFormat.BareCff }));
    }

    private static Typeface Subset(Typeface face, string text, out FontSubset subset)
    {
        ushort[] glyphs = [.. text.Select(c => face.GetGlyph(c))];
        subset = FontSubsetter.Subset(face, glyphs, new SubsetOptions { RetainGlyphIds = true, LayoutClosure = true });
        return subset.ToTypeface();
    }

    private static GlyphBuffer Shape(Typeface face, string text, Script script, TextDirection direction)
    {
        GlyphBuffer buffer = new();
        TextShaper.ShapeRun(text, 0, text.Length, new Font(face, 20), new RunProperties { Script = script, Direction = direction }, buffer);
        return buffer;
    }

    private static void AssertSame(GlyphBuffer expected, GlyphBuffer actual)
    {
        Assert.Equal(expected.GlyphIds.ToArray(), actual.GlyphIds.ToArray());
        Assert.Equal(expected.Positions.ToArray(), actual.Positions.ToArray());
        Assert.Equal(expected.Clusters.ToArray(), actual.Clusters.ToArray());
    }
}
