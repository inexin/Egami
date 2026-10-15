using System.Numerics;

using Egami.Fonts;
using Egami.Pixels;

namespace Egami.Drawing.Tests.Canvas;

/// <summary>
/// Rendering quality (2.7.2 tasks 8 to 10): light hinting moves only y, subpixel phases, alias and LCD edging, and synthetic bold
/// and italic, with cached and uncached pixels identical for every phase and edging.
/// </summary>
public class TextQualityTests
{
    private static Typeface Noto => TextTests.Noto.Value;

    private static Typeface Source => TextTests.Source.Value;

    public static TheoryData<FontEdging, bool, FontHinting, FontSynthesis> Styles() => new()
    {
        { FontEdging.Antialias, true, FontHinting.None, FontSynthesis.None },
        { FontEdging.Antialias, false, FontHinting.Light, FontSynthesis.None },
        { FontEdging.Alias, true, FontHinting.None, FontSynthesis.Bold },
        { FontEdging.Lcd, true, FontHinting.Light, FontSynthesis.Italic },
        { FontEdging.Lcd, false, FontHinting.None, FontSynthesis.Bold | FontSynthesis.Italic },
    };

    [Theory]
    [MemberData(nameof(Styles))]
    public void CachedAndUncachedPixelsAreIdentical(FontEdging edging, bool subpixel, FontHinting hinting, FontSynthesis synthesis)
    {
        long limit = GlyphCache.ByteLimit;
        try
        {
            foreach (Typeface face in new[] { Noto, Source })
            {
                Font font = new Font(face, 13) with { Edging = edging, Subpixel = subpixel, Hinting = hinting, Synthesis = synthesis };
                GlyphCache.ByteLimit = limit;
                GlyphCache.Clear();
                using Raster<Rgba8> cold = Draw(font);
                using Raster<Rgba8> warm = Draw(font);
                GlyphCache.ByteLimit = 0;
                using Raster<Rgba8> uncached = Draw(font);
                Assert.Equal(Pixels(cold), Pixels(warm));
                Assert.Equal(Pixels(cold), Pixels(uncached));
            }
        }
        finally
        {
            GlyphCache.ByteLimit = limit;
        }
    }

    [Fact]
    public void OverlappingContoursDrawAsTheirUnion()
    {
        // A variable font keeps overlapping contours (Source Serif's serifs overlap its stems); its glyphs draw as the union of
        // their contours, not with the overlaps' edges counted twice, so the glyph cache's mask is near filling the merged outline.
        Typeface face = Typeface.FromFile(TextTests.FontPath("derived/SourceSerif4-VF-subset.otf"));
        var font = new Font(face, 48) { Subpixel = false };
        GlyphRun shaped = font.Shape("hnx");
        GlyphRun run = GlyphRun.Create(font, shaped.Glyphs, [new Vector2(0, 0), new Vector2(40, 0), new Vector2(85, 0)]);
        using Raster<Rgba8> cached = Raster.Create(140, 60, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = cached.CreateCanvas())
        {
            canvas.DrawGlyphRun(run, new Vector2(5, 50), Rgba8.Black);
        }

        using Raster<Rgba8> merged = Raster.Create(140, 60, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = merged.CreateCanvas())
        {
            canvas.FillPath(run.ToPath(new Vector2(5, 50)).Simplify(), new SolidPaint(Rgba8.Black));
        }

        using Raster<Rgba8> unmerged = Raster.Create(140, 60, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = unmerged.CreateCanvas())
        {
            canvas.FillPath(run.ToPath(new Vector2(5, 50)), new SolidPaint(Rgba8.Black));
        }

        // Darker overlap edges are what the merge removes: the cached glyphs are near the merged fill and further from the raw one
        // (the cache and FillPath flatten curves at different points, so even the merged fill differs a little).
        (double toMerged, int worst) = Difference(cached, merged);
        (double toUnmerged, _) = Difference(cached, unmerged);
        Assert.True(toMerged < 0.15 && worst <= 32, $"Against the merged outline: mean {toMerged:F3}, largest {worst} levels.");
        Assert.True(toUnmerged > 1.5 * toMerged, $"Against the raw outline: mean {toUnmerged:F3}; merged {toMerged:F3}.");
    }

    private static (double Mean, int Max) Difference(Raster<Rgba8> a, Raster<Rgba8> b)
    {
        long sum = 0;
        int max = 0;
        for (int y = 0; y < a.Height; y++)
        {
            for (int x = 0; x < a.Width; x++)
            {
                int d = Math.Abs(a.GetPixel(x, y).R - b.GetPixel(x, y).R);
                sum += d;
                max = Math.Max(max, d);
            }
        }

        return (sum / (double)(a.Width * a.Height), max);
    }

    [Fact]
    public void SubpixelOriginsRoundToQuarterPixels()
    {
        Assert.Equal((10, 0), RasterCanvas<Rgba8>.Snap(10.1f, true));
        Assert.Equal((10, 1), RasterCanvas<Rgba8>.Snap(10.2f, true));
        Assert.Equal((10, 3), RasterCanvas<Rgba8>.Snap(10.8f, true));
        Assert.Equal((11, 0), RasterCanvas<Rgba8>.Snap(10.9f, true));
        Assert.Equal((-3, 2), RasterCanvas<Rgba8>.Snap(-2.5f, true));
        Assert.Equal((11, 0), RasterCanvas<Rgba8>.Snap(10.6f, false));

        // A quarter-pixel move changes the pixels; a whole-pixel move only shifts them.
        Font font = new(Noto, 14);
        using Raster<Rgba8> a = DrawAt(font, 10f);
        using Raster<Rgba8> b = DrawAt(font, 10.25f);
        using Raster<Rgba8> c = DrawAt(font, 11f);
        Assert.NotEqual(Pixels(a), Pixels(b));
        Assert.Equal(Pixels(a)[..^1], Pixels(c)[1..]);
        using Raster<Rgba8> whole = DrawAt(font with { Subpixel = false }, 10.25f);
        Assert.Equal(Pixels(a), Pixels(whole));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LightHintingMovesOnlyYAndKeepsAdvances(bool cff)
    {
        Typeface face = cff ? Source : Noto;
        var plain = new GlyphStyle(FontEdging.Antialias, false, FontSynthesis.None, PixelGeometry.Unknown);
        GlyphStyle hinted = plain with { Hint = true };
        float sy = 13f / face.UnitsPerEm;
        foreach (char ch in "xHoOpgAa&")
        {
            ushort glyph = face.GetGlyph(ch);
            Path a = RasterCanvas<Rgba8>.GlyphOutline(face, glyph, sy, sy, 0, plain, 13);
            Path b = RasterCanvas<Rgba8>.GlyphOutline(face, glyph, sy, sy, 0, hinted, 13);
            Assert.Equal(a.Verbs.ToArray(), b.Verbs.ToArray());
            Assert.Equal(a.Points.ToArray().Select(p => p.X), b.Points.ToArray().Select(p => p.X));
        }

        // The flat edges of x and H land on whole pixels: the baseline and the x-height or cap height.
        foreach (char ch in "xH")
        {
            Path b = RasterCanvas<Rgba8>.GlyphOutline(face, face.GetGlyph(ch), sy, sy, 0, hinted, 13);
            RectF bounds = b.Bounds;
            Assert.Equal(MathF.Round(bounds.Bottom), bounds.Bottom, 3);
            Assert.Equal(MathF.Round(bounds.Top), bounds.Top, 3);
        }

        Font font = new(face, 13);
        Assert.Equal(font.MeasureText("Hamburgefonstiv"), (font with { Hinting = FontHinting.Light, Edging = FontEdging.Alias, Subpixel = false }).MeasureText("Hamburgefonstiv"));
    }

    [Fact]
    public void AliasedTextHasNoPartialCoverage()
    {
        using Raster<Rgba8> image = Draw(new Font(Noto, 15) { Edging = FontEdging.Alias }, Rgba8.Black);
        Assert.All(Pixels(image), p => Assert.True(p.R is 0 or 255));
        using Raster<Rgba8> big = Raster.Create(300, 300, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = big.CreateCanvas())
        {
            canvas.DrawText("Ag", new Vector2(5, 260), new Font(Noto, 280) { Edging = FontEdging.Alias }, Rgba8.Black);
        }

        Assert.All(Pixels(big), p => Assert.True(p.R is 0 or 255));
    }

    [Fact]
    public void LcdEdgingColorsTheEdges()
    {
        var options = new CanvasOptions { PixelGeometry = PixelGeometry.RgbHorizontal };
        Font font = new(Noto, 14) { Edging = FontEdging.Lcd };
        using Raster<Rgba8> lcd = Draw(font, Rgba8.Black, options);
        Assert.Contains(Pixels(lcd), p => Math.Abs(p.R - p.B) > 40);

        // BGR mirrors the fringes.
        using Raster<Rgba8> bgr = Draw(font, Rgba8.Black, options with { PixelGeometry = PixelGeometry.BgrHorizontal });
        Rgba8[] a = Pixels(lcd), b = Pixels(bgr);
        Assert.Equal(a.Select(p => p.R), b.Select(p => p.B));

        // Grayscale where LCD cannot draw: no geometry, a gray canvas or a layer; the fallback is reported once per canvas.
        var seen = new List<EgamiDiagnostic>();
        var context = EgamiContext.Default with { Diagnostics = seen.Add };
        using Raster<Rgba8> unknown = Raster.Create(240, 40, Rgba8.White, context);
        using (RasterCanvas<Rgba8> canvas = unknown.CreateCanvas())
        {
            canvas.DrawText("Fallback", new Vector2(4, 30), font, Rgba8.Black);
            canvas.DrawText("Fallback", new Vector2(4, 30), font, Rgba8.Black);
        }

        Assert.Single(seen, d => d.Id == "Text.LcdFallback");
        Assert.All(Pixels(unknown), p => Assert.True(p.R == p.G && p.G == p.B));
        using Raster<Rgba8> layered = Raster.Create(240, 40, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = layered.CreateCanvas(options))
        {
            canvas.SaveLayer();
            canvas.DrawText("Fallback", new Vector2(4, 30), font, Rgba8.Black);
            canvas.Restore();
        }

        Assert.All(Pixels(layered), p => Assert.True(Math.Abs(p.R - p.B) <= 1));
    }

    [Fact]
    public void SyntheticOutlinesEqualOutsettingAndSkewingThePath()
    {
        Font font = new(Noto, 40);
        ushort glyph = Noto.GetGlyph('R');
        Path plain = font.GetGlyphPath(glyph);
        float k = MathF.Tan(14 * MathF.PI / 180);

        // Italic slants about the baseline: x moves right by the height above it (y down, so by -y).
        Path italic = (font with { Synthesis = FontSynthesis.Italic }).GetGlyphPath(glyph);
        Path expected = plain.Transform(new Matrix3x2(1, 0, -k, 1, 0, 0));
        Assert.Equal(expected.Points.ToArray().Select(p => (MathF.Round(p.X, 3), MathF.Round(p.Y, 3))), italic.Points.ToArray().Select(p => (MathF.Round(p.X, 3), MathF.Round(p.Y, 3))));

        // Bold outsets with round joins, by em/32 at 36 units and above.
        Path bold = (font with { Synthesis = FontSynthesis.Bold }).GetGlyphPath(glyph);
        Path outset = plain.Inflate(40 / 32f);
        Assert.Equal(outset.Points.ToArray(), bold.Points.ToArray());
        Assert.Equal(plain.Points.ToArray(), (font with { Synthesis = FontSynthesis.Bold, AllowSynthesis = false }).GetGlyphPath(glyph).Points.ToArray());

        // Measuring: advances stay, ink grows by the outset and leans with the slant.
        Font both = font with { Synthesis = FontSynthesis.Bold | FontSynthesis.Italic };
        Assert.Equal(font.MeasureText("R").Advance, both.MeasureText("R").Advance);
        TextBounds ink = (font with { Synthesis = FontSynthesis.Bold }).MeasureText("R").InkBounds, raw = font.MeasureText("R").InkBounds;
        Assert.Equal(raw.Left - 1.25f, ink.Left, 3);
        Assert.Equal(raw.Bottom + 1.25f, ink.Bottom, 3);
        RectF drawn = both.GetTextPath("R", Vector2.Zero).Bounds;
        TextBounds measured = both.MeasureText("R").InkBounds;
        Assert.Equal(drawn.Left, measured.Left, 1);
        Assert.Equal(drawn.Right, measured.Right, 1);
        Assert.Equal(drawn.Top, measured.Top, 1);
    }

    private static Raster<Rgba8> Draw(Font font, Rgba8? color = null, CanvasOptions? options = null)
    {
        Raster<Rgba8> image = Raster.Create(420, 50, Rgba8.White);
        using RasterCanvas<Rgba8> canvas = image.CreateCanvas(options);
        canvas.DrawText("Hamburgefonstiv 0123 Ågé &@", new Vector2(4.37f, 34.6f), font, color ?? new Rgba8(20, 40, 160, 255));
        return image;
    }

    private static Raster<Rgba8> DrawAt(Font font, float x)
    {
        Raster<Rgba8> image = Raster.Create(80, 30, Rgba8.White);
        using RasterCanvas<Rgba8> canvas = image.CreateCanvas();
        canvas.DrawText("ma", new Vector2(x, 20), font, Rgba8.Black);
        return image;
    }

    private static Rgba8[] Pixels(Raster<Rgba8> image)
    {
        var all = new Rgba8[image.Width * image.Height];
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                all[(y * image.Width) + x] = image.GetPixel(x, y);
            }
        }

        return all;
    }
}
