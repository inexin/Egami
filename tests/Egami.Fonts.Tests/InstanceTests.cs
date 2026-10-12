using System.Text.Json;

namespace Egami.Fonts.Tests;

/// <summary>
/// Static instances (2.7.2 task 7): a variable face's subset is a static font whose integer outlines, advances and bearings equal
/// fontTools' instancer, whose OS/2 carries the position, and which reloads drawing like the varied face. Every instance is also
/// written to .cache/font-subsets for tools/fontref/check_subsets.py (fontTools and FreeType).
/// </summary>
public class InstanceTests
{
    public static TheoryData<string> GlyfFonts() => ["RobotoFlex-subset.ttf", "NotoSans-VF-subset.ttf", "NotoSerif-VF-subset.ttf"];

    [Theory]
    [MemberData(nameof(GlyfFonts))]
    public void InstancesEqualFontToolsInstancer(string file)
    {
        Typeface face = TestFonts.Load("derived/" + file);
        JsonElement r = TestFonts.Reference("var/" + Path.GetFileNameWithoutExtension(file) + ".instances");
        int n = 0;
        foreach (JsonElement location in r.GetProperty("locations").EnumerateArray())
        {
            FontVariation[] at = [.. location.GetProperty("user").EnumerateObject().Select(p => new FontVariation(p.Name, (float)p.Value.GetDouble()))];
            FontSubset subset = FontSubsetter.Subset(face, Enumerable.Range(0, face.GlyphCount).Select(g => (ushort)g), new SubsetOptions { RetainGlyphIds = true, Variations = at });
            Write($"{Path.GetFileNameWithoutExtension(file)}-instance{n++}.ttf", subset);
            Typeface instance = subset.ToTypeface();
            Assert.False(instance.IsVariable);
            Assert.False(instance.TryGetTable(new FontTag("gvar"), out _));
            Assert.Equal(location.GetProperty("weightClass").GetInt32(), instance.Os2.WeightClass);
            Assert.Equal(location.GetProperty("widthClass").GetInt32(), instance.Os2.WidthClass);
            Assert.Equal(location.GetProperty("typoAscender").GetInt32(), instance.Os2.TypoAscender);
            Assert.Equal(location.GetProperty("xHeight").GetInt32(), instance.Os2.XHeight);
            JsonElement glyphs = location.GetProperty("glyphs");
            for (int g = 0; g < instance.GlyphCount; g++)
            {
                string where = $"{file} glyph {g} at {location.GetProperty("user")}";
                Assert.True(glyphs[g].GetProperty("advance").GetInt32() == (int)instance.GetAdvance((ushort)g), $"{where}: advance {instance.GetAdvance((ushort)g)}, expected {glyphs[g].GetProperty("advance")}");
                Assert.True(glyphs[g].GetProperty("lsb").GetInt32() == (int)instance.GetLeftSideBearing((ushort)g), $"{where}: bearing {instance.GetLeftSideBearing((ushort)g)}, expected {glyphs[g].GetProperty("lsb")}");
                var sink = new TextSink();
                instance.GetGlyphOutline((ushort)g, sink);
                JsonElement outline = glyphs[g].GetProperty("outline");
                Assert.True(outline.GetArrayLength() == sink.Verbs.Count, $"{where}: {sink.Verbs.Count} verbs, expected {outline.GetArrayLength()}");
                for (int k = 0; k < sink.Verbs.Count; k++)
                {
                    for (int c = 0; c < sink.Ops[k].Length; c++)
                    {
                        Assert.True(Math.Abs(outline[k][c + 1].GetDouble() - sink.Ops[k][c]) < 1e-3, $"{where}: verb {k} value {c} is {sink.Ops[k][c]}, expected {outline[k][c + 1]}");
                    }
                }
            }
        }
    }

    [Theory]
    [InlineData("SourceSerif4-VF-subset.otf", SubsetFormat.OpenTypeCff)]
    [InlineData("SourceSans3-VF-subset.otf", SubsetFormat.OpenTypeCff)]
    [InlineData("SourceSans3-VF-subset.otf", SubsetFormat.BareCff)]
    public void Cff2InstancesAreCffAndDrawLikeThePosition(string file, SubsetFormat format)
    {
        Typeface face = TestFonts.Load("derived/" + file).WithVariations(FontVariation.Weight(650));
        FontSubset subset = FontSubsetter.Subset(face, Enumerable.Range(0, face.GlyphCount).Select(g => (ushort)g), new SubsetOptions { Format = format });
        Write($"{Path.GetFileNameWithoutExtension(file)}-650-{format}{(format == SubsetFormat.BareCff ? ".cff" : ".otf")}", subset);
        Typeface instance = subset.ToTypeface();
        Assert.Equal(FontOutlines.Cff, instance.Outlines);
        Assert.False(instance.IsCff2);
        Assert.False(instance.IsVariable);
        for (int g = 0; g < face.GlyphCount; g++)
        {
            ushort now = subset.GlyphMap[(ushort)g];
            var expected = new TextSink();
            var actual = new TextSink();
            face.GetGlyphOutline((ushort)g, expected);
            instance.GetGlyphOutline(now, actual);
            Assert.Equal(expected.Verbs, actual.Verbs);
            for (int k = 0; k < expected.Ops.Count; k++)
            {
                for (int c = 0; c < expected.Ops[k].Length; c++)
                {
                    Assert.True(Math.Abs(expected.Ops[k][c] - actual.Ops[k][c]) <= 1 / 64.0, $"glyph {g} verb {k}: {actual.Ops[k][c]}, expected {expected.Ops[k][c]}");
                }
            }

            Assert.Equal(MathF.Floor(face.GetAdvance((ushort)g) + 0.5f), instance.GetAdvance(now));
        }
    }

    [Fact]
    public void ReloadedInstancesDrawLikeTheVariedFace()
    {
        Typeface face = TestFonts.Load("derived/NotoSans-VF-subset.ttf").WithVariations(FontVariation.Weight(777), FontVariation.Width(81));
        ushort[] glyphs = [.. "Hamburgefonstiv Åé".Select(c => face.GetGlyph(c))];
        FontSubset subset = FontSubsetter.Subset(face, glyphs);
        Typeface instance = subset.ToTypeface();
        Assert.Equal(face.PostScriptName, instance.PostScriptName);
        foreach (ushort g in glyphs)
        {
            var expected = new TextSink();
            var actual = new TextSink();
            face.GetGlyphOutline(g, expected);
            instance.GetGlyphOutline(subset.GlyphMap[g], actual);
            Assert.Equal(expected.Verbs, actual.Verbs);
            for (int k = 0; k < expected.Ops.Count; k++)
            {
                for (int c = 0; c < expected.Ops[k].Length; c++)
                {
                    // Rounding to whole units moves a point by half a unit, and a component's offset by another half.
                    Assert.True(Math.Abs(expected.Ops[k][c] - actual.Ops[k][c]) <= 1 + 1e-3);
                }
            }

            Assert.True(Math.Abs(face.GetAdvance(g) - instance.GetAdvance(subset.GlyphMap[g])) <= 1);
        }

        // Variations in the options apply over the face's position; KeepTables never brings variation tables back.
        FontSubset light = FontSubsetter.Subset(face, glyphs, new SubsetOptions { Variations = [FontVariation.Weight(300)], KeepTables = [new FontTag("gvar"), new FontTag("GSUB")] });
        Typeface lightFace = light.ToTypeface();
        Assert.Equal(300, lightFace.Os2.WeightClass);
        Assert.Equal(3, lightFace.Os2.WidthClass);
        Assert.False(lightFace.TryGetTable(new FontTag("gvar"), out _));
        Assert.True(lightFace.TryGetTable(new FontTag("GSUB"), out _));
    }

    private static void Write(string name, FontSubset subset)
    {
        string dir = Path.Combine(TestFonts.Root, ".cache", "font-subsets");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name), subset.Data.ToArray());
    }
}
