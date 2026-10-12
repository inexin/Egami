using System.Text.Json;

using Egami.Fonts.Unicode;

namespace Egami.Fonts.Tests.Shaping;

/// <summary>The shaping references load, their fonts load, and Egami's script runs agree with the script hb-shape chose.</summary>
public sealed class ShapingReferenceTests
{
    [Fact]
    public void HarfBuzzTestsLoadAndMatchHarfBuzzsOwnExpectations()
    {
        int cases = 0;
        HashSet<string> fonts = [];
        foreach (string suite in ShapingReferences.Suites)
        {
            JsonElement root = ShapingReferences.Load(Path.Combine("harfbuzz", suite + ".json.gz"));
            Assert.Equal("14.5.1", root.GetProperty("harfbuzz").GetString());
            foreach (JsonElement c in root.GetProperty("cases").EnumerateArray())
            {
                cases++;
                Assert.True(c.GetProperty("matches").GetBoolean(), $"{c.GetProperty("file")} line {c.GetProperty("line")}");
                Assert.NotEqual(JsonValueKind.Null, c.GetProperty("glyphs").ValueKind);
                fonts.Add(c.GetProperty("font").GetString()!);
            }
        }

        Assert.True(cases > 5000, $"Only {cases} cases.");
        foreach (string font in fonts)
        {
            Assert.True(File.Exists(Path.Combine(TestFonts.Root, font)), font);
        }
    }

    [Theory]
    [MemberData(nameof(ShapingReferences.CorpusData), MemberType = typeof(ShapingReferences))]
    public void CorpusLoadsAndItsGlyphsAreInTheFont(string name)
    {
        JsonElement root = ShapingReferences.Load(Path.Combine("corpus", name + ".json.gz"));
        Typeface face = Typeface.FromFile(Path.Combine(TestFonts.Root, root.GetProperty("font").GetString()!));
        int lines = 0;
        foreach (JsonElement line in root.GetProperty("lines").EnumerateArray())
        {
            lines++;
            ShapingReferences.Glyph[] glyphs = ShapingReferences.Glyphs(line.GetProperty("glyphs"));
            Assert.NotEmpty(glyphs);
            Assert.All(glyphs, g => Assert.InRange(g.Id, 0, face.GlyphCount - 1));
            // hb-shape's clusters count code points, not UTF-16 units.
            int length = line.GetProperty("text").GetString()!.EnumerateRunes().Count();
            Assert.All(glyphs, g => Assert.InRange(g.Cluster, 0, length - 1));
        }

        Assert.True(lines >= 20);
    }

    [Theory]
    [MemberData(nameof(ShapingReferences.CorpusData), MemberType = typeof(ShapingReferences))]
    public void ScriptRunsMatchHbShapesScriptChoice(string name)
    {
        // hb-shape gives a whole line the script of its first character that is not Common or Inherited; Egami's first run
        // with a script of its own must be that script.
        JsonElement root = ShapingReferences.Load(Path.Combine("corpus", name + ".json.gz"));
        List<ScriptRun> runs = [];
        foreach (JsonElement line in root.GetProperty("lines").EnumerateArray())
        {
            string text = line.GetProperty("text").GetString()!;
            runs.Clear();
            ScriptItemizer.Itemize(text, runs);
            Script first = runs.Select(r => r.Script).FirstOrDefault(s => !s.IsShared, Script.Common);
            Assert.True(first.Code == line.GetProperty("script").GetString(), $"{text}: {first.Code}, hb-shape {line.GetProperty("script")}");
        }
    }
}
