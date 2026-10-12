using System.Security.Cryptography;
using System.Text.Json;

using Egami.Operations;
using Egami.Pixels;

namespace Egami.Fonts.Tests;

/// <summary>
/// Color fonts (2.7.2 tasks 12 to 14): CPAL palettes, every COLR version 0 and 1 paint graph and clip box equal fontTools' reading,
/// sbix and CBDT images and placements, and OpenType SVG documents.
/// </summary>
public class ColorFontTests
{
    public static TheoryData<string, string, ColorGlyphFormats> Fonts() => new()
    {
        { "derived/NotoColorEmoji-COLRv1-subset.ttf", "NotoColorEmoji-COLRv1-subset", ColorGlyphFormats.ColrV1 | ColorGlyphFormats.Svg },
        { "derived/NotoColorEmoji-CBDT-subset.ttf", "NotoColorEmoji-CBDT-subset", ColorGlyphFormats.Cbdt },
        { "derived/Twemoji-COLRv0-subset.ttf", "Twemoji-COLRv0-subset", ColorGlyphFormats.ColrV0 },
        { "derived/Twemoji-sbix.ttf", "Twemoji-sbix", ColorGlyphFormats.Sbix },
        { "derived/Svg-test.ttf", "Svg-test", ColorGlyphFormats.Svg },
        { "test_glyphs-glyf_colr_1.ttf", "test_glyphs-glyf_colr_1", ColorGlyphFormats.ColrV1 },
        { "test_glyphs-glyf_colr_1_variable.ttf", "test_glyphs-glyf_colr_1_variable", ColorGlyphFormats.ColrV1 },
    };

    [Theory]
    [MemberData(nameof(Fonts))]
    public void FormatsAndPalettesEqualFontTools(string file, string reference, ColorGlyphFormats formats)
    {
        Typeface face = TestFonts.Load(file);
        JsonElement r = TestFonts.Reference("color/" + reference);
        Assert.Equal(formats, face.ColorFormats & ~ColorGlyphFormats.ColrV0 | (formats & ColorGlyphFormats.ColrV0));
        JsonElement palettes = r.GetProperty("palettes");
        Assert.Equal(palettes.GetArrayLength(), face.Palettes.Count);
        for (int p = 0; p < face.Palettes.Count; p++)
        {
            FontPaletteInfo info = face.Palettes[p];
            int type = palettes[p].GetProperty("type").GetInt32();
            Assert.Equal((type & 1) != 0, info.UsableWithLightBackground);
            Assert.Equal((type & 2) != 0, info.UsableWithDarkBackground);
            Assert.Equal(
                palettes[p].GetProperty("colors").EnumerateArray().Select(c => new Rgba8((byte)c[0].GetInt32(), (byte)c[1].GetInt32(), (byte)c[2].GetInt32(), (byte)c[3].GetInt32())),
                info.Colors);
        }
    }

    [Theory]
    [MemberData(nameof(Fonts))]
    public void PaintGraphsEqualFontTools(string file, string reference, ColorGlyphFormats formats)
    {
        _ = formats;
        Typeface face = TestFonts.Load(file);
        JsonElement r = TestFonts.Reference("color/" + reference);
        JsonElement glyphs = r.GetProperty("glyphs");
        int count = 0;
        foreach (JsonProperty entry in glyphs.EnumerateObject())
        {
            ushort glyph = ushort.Parse(entry.Name, System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(face.TryGetColorGlyph(glyph, null, out ColorGlyph? color), $"glyph {glyph}");
            var paint = Assert.IsType<PaintColorGlyph>(color);
            Assert.Equal(entry.Value.GetProperty("v").GetInt32() == 1 ? ColorGlyphFormats.ColrV1 : ColorGlyphFormats.ColrV0, paint.Format);
            Compare(entry.Value.GetProperty("tree"), paint.Paint, $"glyph {glyph}");
            if (entry.Value.TryGetProperty("clip", out JsonElement clip))
            {
                Assert.Equal(new GlyphExtents(clip[0].GetInt32(), clip[1].GetInt32(), clip[2].GetInt32(), clip[3].GetInt32()), paint.ClipBox);
            }

            Assert.True(face.HasColorGlyph(glyph));
            count++;
        }

        Assert.Equal(count > 0, (face.ColorFormats & (ColorGlyphFormats.ColrV0 | ColorGlyphFormats.ColrV1)) != 0);
        Assert.False(face.HasColorGlyph(0) && count == 0);
    }

    [Fact]
    public void SbixImagesComeFromTheRightStrike()
    {
        Typeface face = TestFonts.Load("derived/Twemoji-sbix.ttf");
        JsonElement r = TestFonts.Reference("color/Twemoji-sbix").GetProperty("sbix");
        Assert.Equal(FontOutlines.TrueType, face.Outlines);
        ushort smile = face.GetGlyph(0x1F600), rocket = face.GetGlyph(0x1F680), star = face.GetGlyph(0x2B50);
        foreach ((float size, int strike) in new[] { (72f, 72), (100f, 72), (0f, 72), (30f, 36), (36f, 36), (50f, 72) })
        {
            Assert.True(face.TryGetColorGlyph(smile, null, out ColorGlyph? c, size));
            var bitmap = Assert.IsType<BitmapColorGlyph>(c);
            Assert.Equal(strike, bitmap.PixelsPerEm);
            JsonElement e = r.GetProperty(smile.ToString(System.Globalization.CultureInfo.InvariantCulture)).GetProperty(strike.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(e.GetProperty("sha256").GetString(), Sha(bitmap.Data));
            Assert.Equal(BitmapGlyphEncoding.Png, bitmap.Encoding);
            Assert.Equal(strike, bitmap.PixelWidth);
            Assert.Equal(new GlyphExtents(0, 0, 1000, 1000), bitmap.Bounds);
        }

        // The rocket's origin offset moves it, in pixels of its strike; the star is a 'dupe' of the smile.
        Assert.True(face.TryGetColorGlyph(rocket, null, out ColorGlyph? r72, 72));
        Assert.Equal(new GlyphExtents(10 * 1000 / 72f, -8 * 1000 / 72f, (10 + 72) * 1000 / 72f, (-8 + 72) * 1000 / 72f), ((BitmapColorGlyph)r72).Bounds);
        Assert.True(face.TryGetColorGlyph(star, null, out ColorGlyph? s72, 72));
        Assert.True(face.TryGetColorGlyph(smile, null, out ColorGlyph? m72, 72));
        Assert.Equal(Sha(((BitmapColorGlyph)m72).Data), Sha(((BitmapColorGlyph)s72).Data));
        Assert.Equal(star, s72.Glyph);
        Assert.False(face.TryGetColorGlyph(0, null, out _));
    }

    [Fact]
    public void CbdtImagesAndMetricsEqualFontTools()
    {
        Typeface face = TestFonts.Load("derived/NotoColorEmoji-CBDT-subset.ttf");
        Assert.Equal(FontOutlines.None, face.Outlines);
        JsonElement r = TestFonts.Reference("color/NotoColorEmoji-CBDT-subset").GetProperty("cbdt");
        int checkedGlyphs = 0;
        foreach (JsonProperty glyph in r.EnumerateObject())
        {
            ushort id = ushort.Parse(glyph.Name, System.Globalization.CultureInfo.InvariantCulture);
            foreach (JsonProperty strike in glyph.Value.EnumerateObject())
            {
                int ppem = int.Parse(strike.Name, System.Globalization.CultureInfo.InvariantCulture);
                Assert.True(face.TryGetColorGlyph(id, null, out ColorGlyph? c, ppem));
                var bitmap = Assert.IsType<BitmapColorGlyph>(c);
                Assert.Equal(ppem, bitmap.PixelsPerEm);
                JsonElement e = strike.Value;
                Assert.Equal(e.GetProperty("width").GetInt32(), bitmap.PixelWidth);
                Assert.Equal(e.GetProperty("height").GetInt32(), bitmap.PixelHeight);
                Assert.Equal(e.GetProperty("sha256").GetString(), Sha(bitmap.Data));
                float scale = face.UnitsPerEm / (float)ppem;
                Assert.Equal(e.GetProperty("bearingX").GetInt32() * scale, bitmap.Bounds.XMin, 3);
                Assert.Equal(e.GetProperty("bearingY").GetInt32() * scale, bitmap.Bounds.YMax, 3);
                checkedGlyphs++;
            }
        }

        Assert.True(checkedGlyphs > 20);
        var sink = new TextSink();
        Assert.True(face.GetGlyphOutline(face.GetGlyph(0x1F600), sink));
        Assert.Empty(sink.Verbs);
        Assert.Throws<ArgumentException>(() => FontSubsetter.Subset(face, [1]));
    }

    [Fact]
    public void SvgDocumentsAreExposedNotDrawn()
    {
        Typeface face = TestFonts.Load("derived/Svg-test.ttf");
        JsonElement r = TestFonts.Reference("color/Svg-test").GetProperty("svg");
        foreach (JsonElement doc in r.EnumerateArray())
        {
            ushort first = (ushort)doc.GetProperty("first").GetInt32(), last = (ushort)doc.GetProperty("last").GetInt32();
            for (ushort g = first; g <= last; g++)
            {
                Assert.True(face.TryGetSvgDocument(g, out ReadOnlyMemory<byte> document, out ushort a, out ushort b));
                Assert.Equal((first, last), (a, b));
                Assert.Equal(doc.GetProperty("sha256").GetString(), Sha(document));
            }
        }

        Assert.False(face.TryGetSvgDocument(0, out _, out _, out _));
        Assert.False(face.TryGetColorGlyph(1, null, out _));
        Assert.False(TestFonts.Load("NotoSans-Regular.ttf").TryGetSvgDocument(36, out _, out _, out _));
    }

    [Fact]
    public void PalettesPickByIndexBackgroundAndOverrides()
    {
        Typeface face = TestFonts.Load("test_glyphs-glyf_colr_1.ttf");
        ushort glyph = FirstSolidGlyph(face, out int entry);
        Assert.True(face.Palettes.Count >= 2);
        RgbaF32 Solid(FontPalette p)
        {
            face.TryGetColorGlyph(glyph, p, out ColorGlyph? c);
            return FindSolid(((PaintColorGlyph)c!).Paint)!.Color.Color;
        }

        Assert.Equal(RgbaF32.FromRgba8(face.Palettes[0].Colors[entry]), Solid(FontPalette.Default));
        Assert.Equal(RgbaF32.FromRgba8(face.Palettes[1].Colors[entry]), Solid(new FontPalette(1)));
        Assert.Equal(RgbaF32.FromRgba8(face.Palettes[0].Colors[entry]), Solid(new FontPalette(99)));
        var red = new RgbaF32(1, 0, 0, 1);
        Assert.Equal(red, Solid(new FontPalette(0, new FontPaletteEntry(entry, red))));
        int dark = face.Palettes.Select((p, i) => (p, i)).FirstOrDefault(x => x.p.UsableWithDarkBackground, (null!, 0)).Item2;
        Assert.Equal(RgbaF32.FromRgba8(face.Palettes[dark].Colors[entry]), Solid(FontPalette.Dark));
        Assert.NotEqual(new FontPalette(1), FontPalette.Default);
        Assert.Equal(new FontPalette(0, new FontPaletteEntry(entry, red)), new FontPalette(0, new FontPaletteEntry(entry, red)));
    }

    [Fact]
    public void VariableColorGlyphsVary()
    {
        Typeface face = TestFonts.Load("test_glyphs-glyf_colr_1_variable.ttf");
        Assert.True(face.IsVariable);
        Typeface moved = face.WithVariations([.. face.VariationAxes.Select(a => new FontVariation(a.Tag, a.Max))]);
        int differing = 0;
        foreach (JsonProperty entry in TestFonts.Reference("color/test_glyphs-glyf_colr_1_variable").GetProperty("glyphs").EnumerateObject())
        {
            ushort glyph = ushort.Parse(entry.Name, System.Globalization.CultureInfo.InvariantCulture);
            face.TryGetColorGlyph(glyph, null, out ColorGlyph? a);
            moved.TryGetColorGlyph(glyph, null, out ColorGlyph? b);
            differing += a == b ? 0 : 1;
        }

        Assert.True(differing > 5);
    }

    private static ushort FirstSolidGlyph(Typeface face, out int entry)
    {
        for (ushort g = 0; g < face.GlyphCount; g++)
        {
            if (face.TryGetColorGlyph(g, null, out ColorGlyph? c) && c is PaintColorGlyph p && FindSolid(p.Paint) is ColorSolidPaint s && !s.Color.IsForeground)
            {
                for (entry = 0; entry < face.Palettes[0].Colors.Count; entry++)
                {
                    if (RgbaF32.FromRgba8(face.Palettes[0].Colors[entry]) == s.Color.Color && face.Palettes[0].Colors[entry] != face.Palettes[1].Colors[entry])
                    {
                        return g;
                    }
                }
            }
        }

        throw new InvalidOperationException("No glyph with a palette color that differs between palettes.");
    }

    private static ColorSolidPaint? FindSolid(ColorPaint paint) => paint switch
    {
        ColorSolidPaint s => s,
        ColorGlyphPaint g => FindSolid(g.Paint),
        ColorTransformPaint t => FindSolid(t.Paint),
        ColorLayersPaint l => l.Layers.Select(FindSolid).FirstOrDefault(x => x is not null),
        ColorCompositePaint c => FindSolid(c.Source) ?? FindSolid(c.Backdrop),
        _ => null,
    };

    private static string Sha(ReadOnlyMemory<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data.Span));

    // Egami's paint against fontTools' canonical form, to 1e-3 (F2Dot14 and Fixed values) and exact for glyphs and modes.
    private static void Compare(JsonElement expected, ColorPaint actual, string where)
    {
        string kind = expected.GetProperty("t").GetString()!;
        switch (kind)
        {
            case "layers":
                var layers = Assert.IsType<ColorLayersPaint>(actual);
                JsonElement c = expected.GetProperty("c");
                Assert.True(c.GetArrayLength() == layers.Layers.Count, $"{where}: {layers.Layers.Count} layers, expected {c.GetArrayLength()}");
                for (int i = 0; i < layers.Layers.Count; i++)
                {
                    Compare(c[i], layers.Layers[i], $"{where}/{i}");
                }

                break;
            case "solid":
                Color(expected.GetProperty("color"), Assert.IsType<ColorSolidPaint>(actual).Color, where);
                break;
            case "linear":
                {
                    var p = Assert.IsType<ColorLinearGradientPaint>(actual);
                    Numbers(expected.GetProperty("p"), [p.P0.X, p.P0.Y, p.P1.X, p.P1.Y, p.P2.X, p.P2.Y], where);
                    Line(expected.GetProperty("line"), p.Line, where);
                    break;
                }

            case "radial":
                {
                    var p = Assert.IsType<ColorRadialGradientPaint>(actual);
                    Numbers(expected.GetProperty("p"), [p.Center0.X, p.Center0.Y, p.Radius0, p.Center1.X, p.Center1.Y, p.Radius1], where);
                    Line(expected.GetProperty("line"), p.Line, where);
                    break;
                }

            case "sweep":
                {
                    var p = Assert.IsType<ColorSweepGradientPaint>(actual);
                    Numbers(expected.GetProperty("p"), [p.Center.X, p.Center.Y, p.StartAngle, p.EndAngle], where);
                    Line(expected.GetProperty("line"), p.Line, where);
                    break;
                }

            case "glyph":
                {
                    var p = Assert.IsType<ColorGlyphPaint>(actual);
                    Assert.Equal(expected.GetProperty("g").GetInt32(), p.Glyph);
                    Compare(expected.GetProperty("c"), p.Paint, $"{where}/glyph");
                    break;
                }

            case "colr":
                Assert.Equal(expected.GetProperty("g").GetInt32(), Assert.IsType<ColorGlyphReferencePaint>(actual).Glyph);
                break;
            case "comp":
                {
                    var p = Assert.IsType<ColorCompositePaint>(actual);
                    BlendMode[] modes =
                    [
                        BlendMode.Clear, BlendMode.Source, BlendMode.Destination, BlendMode.SourceOver, BlendMode.DestinationOver, BlendMode.SourceIn,
                        BlendMode.DestinationIn, BlendMode.SourceOut, BlendMode.DestinationOut, BlendMode.SourceAtop, BlendMode.DestinationAtop,
                        BlendMode.Xor, BlendMode.Plus, BlendMode.Screen, BlendMode.Overlay, BlendMode.Darken, BlendMode.Lighten, BlendMode.ColorDodge,
                        BlendMode.ColorBurn, BlendMode.HardLight, BlendMode.SoftLight, BlendMode.Difference, BlendMode.Exclusion, BlendMode.Multiply,
                        BlendMode.Hue, BlendMode.Saturation, BlendMode.Color, BlendMode.Luminosity,
                    ];
                    Assert.Equal(modes[expected.GetProperty("m").GetInt32()], p.Mode);
                    Compare(expected.GetProperty("s"), p.Source, $"{where}/source");
                    Compare(expected.GetProperty("b"), p.Backdrop, $"{where}/backdrop");
                    break;
                }

            case "xf":
                {
                    var p = Assert.IsType<ColorTransformPaint>(actual);
                    Numbers(expected.GetProperty("m"), [p.Matrix.M11, p.Matrix.M12, p.Matrix.M21, p.Matrix.M22, p.Matrix.M31, p.Matrix.M32], where);
                    Compare(expected.GetProperty("c"), p.Paint, $"{where}/xf");
                    break;
                }

            default:
                Assert.Fail($"{where}: unknown kind {kind}");
                break;
        }
    }

    private static void Line(JsonElement expected, ColorLine line, string where)
    {
        Assert.Equal((ColorExtend)expected.GetProperty("extend").GetInt32(), line.Extend);
        JsonElement stops = expected.GetProperty("stops");
        Assert.Equal(stops.GetArrayLength(), line.Stops.Count);
        for (int i = 0; i < line.Stops.Count; i++)
        {
            Assert.True(Math.Abs(stops[i][0].GetDouble() - line.Stops[i].Offset) <= 1e-3, $"{where} stop {i} offset");
            Color(stops[i][1], line.Stops[i].Color, $"{where} stop {i}");
        }
    }

    private static void Color(JsonElement expected, GlyphColor actual, string where)
    {
        if (expected.TryGetProperty("fg", out JsonElement fg))
        {
            Assert.True(actual.IsForeground, $"{where}: expected the foreground");
            Assert.True(Math.Abs(fg.GetDouble() - actual.Color.A) <= 1e-3, $"{where}: foreground alpha");
            return;
        }

        Assert.False(actual.IsForeground, where);
        Numbers(expected.GetProperty("rgba"), [actual.Color.R, actual.Color.G, actual.Color.B, actual.Color.A], where);
    }

    private static void Numbers(JsonElement expected, float[] actual, string where)
    {
        Assert.Equal(expected.GetArrayLength(), actual.Length);
        for (int i = 0; i < actual.Length; i++)
        {
            Assert.True(Math.Abs(expected[i].GetDouble() - actual[i]) <= 1e-3, $"{where}: value {i} is {actual[i]}, expected {expected[i]}");
        }
    }
}
