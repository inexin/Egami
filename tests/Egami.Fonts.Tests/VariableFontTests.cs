using System.Text.Json;

namespace Egami.Fonts.Tests;

/// <summary>
/// Variable fonts (2.7.2 tasks 4 to 6): axes and instances as fvar lists them, and at 25 locations per font the normalized
/// coordinates, every glyph's outline and advance (within 1/64 unit), MVAR's metrics and the varied pair kerning equal fontTools.
/// </summary>
public class VariableFontTests
{
    public static TheoryData<string> Fonts() =>
    [
        "RobotoFlex-subset.ttf", "NotoSans-VF-subset.ttf", "NotoSerif-VF-subset.ttf", "SourceSerif4-VF-subset.otf",
        "SourceSans3-VF-subset.otf", "NotoSans-avar2.ttf",
    ];

    [Theory]
    [MemberData(nameof(Fonts))]
    public void AxesAndInstancesEqualFontTools(string file)
    {
        Typeface face = TestFonts.Load("derived/" + file);
        JsonElement r = TestFonts.Reference("var/" + Path.GetFileNameWithoutExtension(file));
        Assert.True(face.IsVariable);
        Assert.Empty(face.Variations);
        JsonElement axes = r.GetProperty("axes");
        Assert.Equal(axes.GetArrayLength(), face.VariationAxes.Count);
        for (int i = 0; i < face.VariationAxes.Count; i++)
        {
            VariationAxis a = face.VariationAxes[i];
            Assert.Equal(axes[i].GetProperty("tag").GetString(), a.Tag.ToString());
            Assert.Equal(axes[i].GetProperty("min").GetDouble(), a.Min, 4);
            Assert.Equal(axes[i].GetProperty("default").GetDouble(), a.Default, 4);
            Assert.Equal(axes[i].GetProperty("max").GetDouble(), a.Max, 4);
            Assert.Equal((axes[i].GetProperty("flags").GetInt32() & 1) != 0, a.IsHidden);
            Assert.NotEmpty(a.Name);
        }

        JsonElement instances = r.GetProperty("instances");
        Assert.Equal(instances.GetArrayLength(), face.NamedInstances.Count);
        for (int i = 0; i < face.NamedInstances.Count; i++)
        {
            NamedInstance n = face.NamedInstances[i];
            foreach (FontVariation v in n.Coordinates)
            {
                Assert.Equal(instances[i].GetProperty("coordinates").GetProperty(v.Axis.ToString()).GetDouble(), v.Value, 3);
            }

            Typeface at = face.WithNamedInstance(i);
            Assert.Equal(n.Name, at.SubfamilyName);
            if (n.PostScriptName is not null)
            {
                Assert.Equal(n.PostScriptName, at.PostScriptName);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Fonts))]
    public void OutlinesAndAdvancesEqualFontToolsAt25Locations(string file)
    {
        Typeface face = TestFonts.Load("derived/" + file);
        JsonElement r = TestFonts.Reference("var/" + Path.GetFileNameWithoutExtension(file));
        JsonElement locations = r.GetProperty("locations");
        Assert.Equal(25, locations.GetArrayLength());
        foreach (JsonElement location in locations.EnumerateArray())
        {
            Typeface at = face.WithVariations([.. location.GetProperty("user").EnumerateObject().Select(p => new FontVariation(p.Name, (float)p.Value.GetDouble()))]);
            float[] coords = at.Instance?.Coords ?? new float[face.VariationAxes.Count];
            for (int a = 0; a < face.VariationAxes.Count; a++)
            {
                double expected = location.GetProperty("normalized").TryGetProperty(face.VariationAxes[a].Tag.ToString(), out JsonElement v) ? v.GetDouble() : 0;
                Assert.Equal(expected, coords[a], 4);
            }

            JsonElement glyphs = location.GetProperty("glyphs");
            Assert.Equal(glyphs.GetArrayLength(), at.GlyphCount);
            for (int g = 0; g < at.GlyphCount; g++)
            {
                JsonElement expected = glyphs[g];
                string where = $"{file} glyph {g} at {location.GetProperty("user")}";
                Assert.True(Math.Abs(expected.GetProperty("advance").GetDouble() - at.GetAdvance((ushort)g)) <= 1 / 64.0, $"{where}: advance {at.GetAdvance((ushort)g)}, expected {expected.GetProperty("advance")}");
                var sink = new TextSink();
                at.GetGlyphOutline((ushort)g, sink);
                JsonElement outline = expected.GetProperty("outline");
                Assert.True(outline.GetArrayLength() == sink.Verbs.Count, $"{where}: {sink.Verbs.Count} verbs, expected {outline.GetArrayLength()}");
                for (int k = 0; k < sink.Verbs.Count; k++)
                {
                    JsonElement op = outline[k];
                    Assert.Equal(op[0].GetString()![0], sink.Verbs[k]);
                    for (int c = 0; c < sink.Ops[k].Length; c++)
                    {
                        double want = op[c + 1].GetDouble();
                        Assert.True(Math.Abs(want - sink.Ops[k][c]) <= 1 / 64.0, $"{where}: verb {k} value {c} is {sink.Ops[k][c]}, expected {want}");
                    }
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Fonts))]
    public void MetricsAndKerningEqualTheInstancer(string file)
    {
        Typeface face = TestFonts.Load("derived/" + file);
        JsonElement r = TestFonts.Reference("var/" + Path.GetFileNameWithoutExtension(file));
        int[] sample = [.. r.GetProperty("kerningSample").EnumerateArray().Select(e => e.GetInt32())];
        foreach (JsonElement location in r.GetProperty("locations").EnumerateArray())
        {
            if (!location.TryGetProperty("metrics", out JsonElement m))
            {
                continue;
            }

            Typeface at = face.WithVariations([.. location.GetProperty("user").EnumerateObject().Select(p => new FontVariation(p.Name, (float)p.Value.GetDouble()))]);
            int Mvar(int value, string tag) => at.Instance is null ? value : (int)MathF.Floor(value + at.Instance.Mvar(tag) + 0.5f);
            Assert.Equal(m.GetProperty("typoAscender").GetInt32(), Mvar(at.Os2.TypoAscender, "hasc"));
            Assert.Equal(m.GetProperty("typoDescender").GetInt32(), Mvar(at.Os2.TypoDescender, "hdsc"));
            Assert.Equal(m.GetProperty("typoLineGap").GetInt32(), Mvar(at.Os2.TypoLineGap, "hlgp"));
            Assert.Equal(m.GetProperty("winAscent").GetInt32(), Mvar(at.Os2.WinAscent, "hcla"));
            Assert.Equal(m.GetProperty("winDescent").GetInt32(), Mvar(at.Os2.WinDescent, "hcld"));
            Assert.Equal(m.GetProperty("xHeight").GetInt32(), Mvar(at.Os2.XHeight, "xhgt"));
            Assert.Equal(m.GetProperty("capHeight").GetInt32(), Mvar(at.Os2.CapHeight, "cpht"));
            Assert.Equal(m.GetProperty("strikeoutSize").GetInt32(), Mvar(at.Os2.StrikeoutSize, "strs"));
            Assert.Equal(m.GetProperty("strikeoutPosition").GetInt32(), Mvar(at.Os2.StrikeoutPosition, "stro"));
            Assert.Equal(m.GetProperty("underlinePosition").GetInt32(), Mvar(at.Post.UnderlinePosition, "undo"));
            Assert.Equal(m.GetProperty("underlineThickness").GetInt32(), Mvar(at.Post.UnderlineThickness, "unds"));
            Assert.Equal(new VerticalMetrics(m.GetProperty("hheaAscent").GetInt32(), m.GetProperty("hheaDescent").GetInt32(), m.GetProperty("hheaLineGap").GetInt32()), at.HheaMetrics);
            Assert.Equal(m.GetProperty("xHeight").GetInt32(), at.Extents.XHeight);
            Assert.Equal(m.GetProperty("capHeight").GetInt32(), at.Extents.CapHeight);

            var expected = new Dictionary<(int, int), int>();
            foreach (JsonElement pair in location.GetProperty("kerning").EnumerateArray())
            {
                expected[(pair[0].GetInt32(), pair[1].GetInt32())] = pair[2].GetInt32();
            }

            foreach (int left in sample)
            {
                foreach (int right in sample)
                {
                    int want = expected.TryGetValue((left, right), out int v) ? v : 0;
                    Assert.True(want == (int)at.GetKerning((ushort)left, (ushort)right), $"{file} kerning {left},{right} at {location.GetProperty("user")}: {at.GetKerning((ushort)left, (ushort)right)}, expected {want}");
                }
            }
        }
    }

    [Fact]
    public void PositionsAreSharedAndClamped()
    {
        Typeface face = TestFonts.Load("derived/NotoSans-VF-subset.ttf");
        Typeface bold = face.WithVariations(FontVariation.Weight(700));
        Assert.Same(bold, face.WithVariations(FontVariation.Weight(700)));
        Assert.Same(face, bold.WithVariations(FontVariation.Weight(400)));
        Assert.Same(face, face.WithVariations(new FontVariation("XXXX", 3)));
        Assert.Equal([FontVariation.Weight(900)], face.WithVariations(FontVariation.Weight(5000)).Variations);
        Assert.Equal([FontVariation.Weight(700), FontVariation.Width(75)], bold.WithVariations(FontVariation.Width(75)).Variations);
        Assert.Equal(700, bold.Style.Weight);
        Assert.Equal(3, bold.WithVariations(FontVariation.Width(75)).Style.Width);
        Assert.True(bold.TryGetTable(new FontTag("glyf"), out ReadOnlyMemory<byte> a) && face.TryGetTable(new FontTag("glyf"), out ReadOnlyMemory<byte> b) && a.Span == b.Span);

        Typeface plain = TestFonts.Load("NotoSans-Regular.ttf");
        Assert.False(plain.IsVariable);
        Assert.Empty(plain.VariationAxes);
        Assert.Same(plain, plain.WithVariations(FontVariation.Weight(700)));
        Assert.Throws<ArgumentOutOfRangeException>(() => plain.WithNamedInstance(0));
    }

    [Fact]
    public void UnnamedPositionsGetVariablePostScriptNames()
    {
        Typeface face = TestFonts.Load("derived/NotoSans-VF-subset.ttf");
        Typeface at = face.WithVariations(FontVariation.Weight(650), FontVariation.Width(80.5f));
        Assert.Equal("NotoSans_650wght_80.5wdth", at.PostScriptName);
        Assert.Equal(face.FullName, at.FullName);
        Assert.Equal(650, at.Style.Weight);
    }
}
