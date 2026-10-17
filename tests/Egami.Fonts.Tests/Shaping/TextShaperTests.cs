using Egami.Fonts.Shaping;
using Egami.Fonts.Unicode;

namespace Egami.Fonts.Tests.Shaping;

/// <summary>The public shaping API: runs, features, layout tables and damaged fonts.</summary>
public sealed class TextShaperTests
{
    private static Typeface Noto => TestFonts.Load("NotoSans-Regular.ttf");

    [Fact]
    public void ShapeRunScalesTheEngineAndKeepsUtf16Clusters()
    {
        string text = "A" + char.ConvertFromUtf32(0x1D400) + " office";
        GlyphBuffer glyphs = new();
        Font font = new(Noto, 20);
        TextShaper.ShapeRun(text, 0, text.Length, font, default, glyphs);

        LayoutBuffer layout = new();
        TextShaper.Shape(text, 0, text.Length, Noto, default, kerning: true, layout);
        Assert.Equal(layout.Len, glyphs.Count);
        float scale = 20f / Noto.UnitsPerEm;
        for (int i = 0; i < glyphs.Count; i++)
        {
            Assert.Equal(layout.Info[i].Codepoint, glyphs.GlyphIds[i]);
            Assert.Equal(layout.Pos[i].XAdvance * scale, glyphs.Positions[i].XAdvance, 0.0001f);
        }

        Assert.Equal([0, 1, 3, 4, 5, 8, 9], glyphs.Clusters.ToArray());
        Assert.Equal(glyphs.Positions.ToArray().Sum(p => p.XAdvance), glyphs.Advance, 0.001f);
    }

    [Fact]
    public void FeaturesTurnLigaturesAndKerningOff()
    {
        GlyphBuffer with = new(), without = new();
        Font font = new(Noto, 20);
        TextShaper.ShapeRun("office", 0, 6, font, default, with);
        TextShaper.ShapeRun("office", 0, 6, font, new RunProperties { Features = [FontFeature.Ligatures(false)] }, without);
        Assert.True(with.Count < without.Count);
        Assert.Equal(6, without.Count);

        GlyphBuffer kerned = new(), plain = new();
        TextShaper.ShapeRun("AV", 0, 2, font, default, kerned);
        TextShaper.ShapeRun("AV", 0, 2, font with { Kerning = false }, default, plain);
        Assert.True(kerned.Advance < plain.Advance);
        Assert.Equal(GlyphFlags.UnsafeToBreak, kerned.Flags[1]);
    }

    [Fact]
    public void TopToBottomRunsUseVerticalMetricsAtTheFacesPosition()
    {
        Typeface face = Typeface.FromFile(Path.Combine(TestFonts.Root, "tests", "fonts", "harfbuzz", "in-house", "fonts", "NotoSansCJK-VF.abc.otf"));
        RunProperties vertical = new() { Direction = TextDirection.TopToBottom };
        GlyphBuffer regular = new(), bold = new();
        TextShaper.ShapeRun("AB", 0, 2, new Font(face, face.UnitsPerEm), vertical, regular);
        TextShaper.ShapeRun("AB", 0, 2, new Font(face.WithVariations(FontVariation.Weight(700)), face.UnitsPerEm), vertical, bold);

        // hb-shape --direction=t (with --variations=wght=700): VORG origins, vmtx advances, half the varied h advance.
        Assert.Equal([new GlyphPosition(0, -1000, -287, -880), new GlyphPosition(0, -1000, -316, -880)], regular.Positions.ToArray());
        Assert.Equal([new GlyphPosition(0, -1000, -320, -880), new GlyphPosition(0, -1000, -340, -880)], bold.Positions.ToArray());
    }

    [Fact]
    public void RunsArePartOfTheirText()
    {
        GlyphBuffer glyphs = new();
        TextShaper.ShapeRun("xx office xx", 3, 6, new Font(Noto, 10), default, glyphs);
        Assert.Equal(3, glyphs.Clusters[0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => TextShaper.ShapeRun("abc", 2, 2, new Font(Noto, 10), default, glyphs));
        TextShaper.ShapeRun("", 0, 0, new Font(Noto, 10), default, glyphs);
        Assert.Equal(0, glyphs.Count);
    }

    [Theory]
    [InlineData("liga", "liga", 1u, 0, int.MaxValue)]
    [InlineData("-liga", "liga", 0u, 0, int.MaxValue)]
    [InlineData("+kern", "kern", 1u, 0, int.MaxValue)]
    [InlineData("aalt=2", "aalt", 2u, 0, int.MaxValue)]
    [InlineData("ss01=on", "ss01", 1u, 0, int.MaxValue)]
    [InlineData("kern[3:5]=0", "kern", 0u, 3, 5)]
    [InlineData("kern[3:]", "kern", 1u, 3, int.MaxValue)]
    [InlineData("kern[:5]", "kern", 1u, 0, 5)]
    [InlineData("kern[4]", "kern", 1u, 4, 5)]
    [InlineData("'cv01'=3", "cv01", 3u, 0, int.MaxValue)]
    [InlineData("a=off", "a   ", 0u, 0, int.MaxValue)]
    public void FeatureSyntaxParsesAsHarfBuzz(string text, string tag, uint value, int start, int end)
    {
        FontFeature feature = FontFeature.Parse(text);
        Assert.Equal(new FontFeature(new FontTag(tag), value, start, end), feature);
        Assert.Equal(feature, FontFeature.Parse(feature.ToString()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("toolong")]
    [InlineData("kern[")]
    [InlineData("kern=x")]
    [InlineData("kern=1 junk")]
    public void BadFeatureSyntaxIsRejected(string text)
    {
        Assert.False(FontFeature.TryParse(text, out _));
        Assert.Throws<FormatException>(() => FontFeature.Parse(text));
        Assert.Throws<ArgumentOutOfRangeException>(() => FontFeature.StylisticSet(21));
    }

    [Fact]
    public void LayoutListsScriptsLanguagesAndFeatures()
    {
        OpenTypeLayout layout = Noto.Layout;
        Assert.True(layout.HasGsub);
        Assert.True(layout.HasGpos);
        Assert.Contains(new FontTag("latn"), layout.Scripts);
        Assert.Contains(new FontTag("kern"), layout.GetFeatures(new FontTag("latn")));
        Assert.Empty(layout.GetFeatures(new FontTag("zzzz")));
    }

    [Fact]
    public void ADamagedLayoutTableIsIgnoredWithADiagnostic()
    {
        List<EgamiDiagnostic> seen = [];
        FontLoadOptions options = new() { Context = EgamiContext.Default with { Diagnostics = seen.Add } };
        Typeface face = Typeface.FromFile(Path.Combine(TestFonts.Root, "tests", "fonts", "harfbuzz", "in-house", "fonts", "HarfBust.ttf"), 0, options);
        Assert.False(face.Layout.HasGsub);
        Assert.Contains(seen, d => d.Id == "Font.BadTable" && d.Message.Contains("GSUB", StringComparison.Ordinal));
    }

    [Fact]
    public void ALongRunAfterAShortOneOnTheSameThreadIsShaped()
    {
        Font font = new(Noto, 20);
        GlyphBuffer glyphs = new();
        TextShaper.ShapeRun("office", 0, 6, font, default, glyphs);

        // The short run's limits must not carry over to the next run in the thread's reused buffer.
        string text = new('a', 100_000);
        TextShaper.ShapeRun(text, 0, text.Length, font, default, glyphs);
        Assert.Equal(100_000, glyphs.Count);
    }

    [Fact]
    public void LanguageTagsMapAsHarfBuzz()
    {
        Assert.Equal([ShapeMapBuilder.Tag("URD ")], LanguageTags.FromBcp47("ur"));
        Assert.Equal([ShapeMapBuilder.Tag("ZHH ")], LanguageTags.FromBcp47("zh-Hant-HK"));
        Assert.Equal([ShapeMapBuilder.Tag("PGR ")], LanguageTags.FromBcp47("el-polyton"));
        Assert.Equal([ShapeMapBuilder.Tag("ARA ")], LanguageTags.FromBcp47("x-hbotara"));
        Assert.Equal([ShapeMapBuilder.Tag("ABC ")], LanguageTags.FromBcp47("abc"));
        Assert.Empty(LanguageTags.FromBcp47(null));
        Assert.Equal(Script.Latin, RunShaper.GuessScript(Buffer("123 abc")));
    }

    private static LayoutBuffer Buffer(string text)
    {
        LayoutBuffer buffer = new();
        foreach (char c in text)
        {
            buffer.Add(c, 0);
        }

        return buffer;
    }
}
