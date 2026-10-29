using System.Numerics;

using Egami.Fonts;
using Egami.Fonts.Shaping;
using Egami.Fonts.Tests;
using Egami.Pixels;

namespace Egami.Drawing.Tests.Canvas;

/// <summary>
/// 2.7.6 task 22: <c>Font.EmbeddedBitmaps</c> draws a glyph an EBDT strike holds from its bitmap, aliased, at exactly the strike's
/// size; other sizes, other glyphs and fonts without the option draw outlines as before. The strikes come from
/// <see cref="EmbeddedStrikes"/>.
/// </summary>
public class EmbeddedBitmapDrawingTests
{
    private static readonly Lazy<Typeface> Face = new(() => Typeface.FromMemory(EmbeddedStrikes.WithStrikes(File.ReadAllBytes(TextTests.FontPath("DejaVuSans.ttf")))));

    [Theory]
    [InlineData(FontEdging.Antialias)]
    [InlineData(FontEdging.Lcd)]
    public void StrikeGlyphsDrawTheirBitmaps(FontEdging edging)
    {
        // 'A' at 13 ppem: 3 × 4 pixels, its top-left 1 right of the origin and 9 above the baseline.
        var font = new Font(Face.Value, 13) { EmbeddedBitmaps = true, Edging = edging, Subpixel = false };
        Rgba8[,] image = Draw(font, "A", new Vector2(10, 20), CanvasOptions.Default with { PixelGeometry = PixelGeometry.RgbHorizontal });
        for (int y = 0; y < 30; y++)
        {
            for (int x = 0; x < 30; x++)
            {
                int bx = x - 11, by = y - 11;
                bool ink = bx is >= 0 and < 3 && by is >= 0 and < 4 && EmbeddedStrikes.A1[by, bx] == 1;
                Assert.Equal(ink ? Rgba8.Black : Rgba8.White, image[y, x]);
            }
        }
    }

    [Fact]
    public void OtherSizesGlyphsAndTheDefaultDrawOutlines()
    {
        var plain = new Font(Face.Value, 13);
        Assert.Equal(Draw(plain, "AC", new Vector2(10, 20)), Draw(new Font(Typeface.FromFile(TextTests.FontPath("DejaVuSans.ttf")), 13), "AC", new Vector2(10, 20)));
        Assert.NotEqual(Draw(plain, "A", new Vector2(10, 20)), Draw(plain with { EmbeddedBitmaps = true }, "A", new Vector2(10, 20)));
        Assert.Equal(Draw(plain, "C", new Vector2(10, 20)), Draw(plain with { EmbeddedBitmaps = true }, "C", new Vector2(10, 20)));
        var twelve = new Font(Face.Value, 12);
        Assert.Equal(Draw(twelve, "A", new Vector2(10, 20)), Draw(twelve with { EmbeddedBitmaps = true }, "A", new Vector2(10, 20)));
    }

    /// <summary>
    /// 2.7.6 task 31: a fallback face takes the primary font's rendering settings, so a strike glyph found by fallback draws its
    /// bitmap, as GDI+ font linking does.
    /// </summary>
    [Fact]
    public void FallbackFacesDrawTheirStrikes()
    {
        Typeface hebrew = Typeface.FromFile(TextTests.FontPath("shaping/NotoSansHebrew-Regular.ttf"));
        Assert.False(hebrew.TryGetGlyph('A', out _));
        FontCollection collection = new();
        collection.Add(Face.Value);
        var options = new ShapingOptions { Fallback = new FontFallback([collection.Families[0]]) };
        var primary = new Font(hebrew, 13) { EmbeddedBitmaps = true, Subpixel = false };
        Rgba8[,] fallback = DrawShaped(TextShaper.Shape("A", primary, options), new Vector2(10, 20));
        var direct = new Font(Face.Value, 13) { EmbeddedBitmaps = true, Subpixel = false };
        Assert.Equal(Draw(direct, "A", new Vector2(10, 20)), fallback);
        Assert.NotEqual(Draw(direct with { EmbeddedBitmaps = false }, "A", new Vector2(10, 20)), fallback);
    }

    /// <summary>Every rendering setting of the primary font but its palette reaches the fallback run's font.</summary>
    [Fact]
    public void FallbackRunsKeepThePrimarySettings()
    {
        Typeface hebrew = Typeface.FromFile(TextTests.FontPath("shaping/NotoSansHebrew-Regular.ttf"));
        FontCollection collection = new();
        collection.Add(Face.Value);
        var primary = new Font(hebrew, 13).WithGdiPlusLook() with { Embolden = 0.2f, StemDarkening = true, ColorGlyphs = false, HintingTarget = TrueTypeTarget.FreeType };
        ShapedText shaped = TextShaper.Shape("A", primary, new ShapingOptions { Fallback = new FontFallback([collection.Families[0]]) });
        Font run = Assert.Single(Assert.Single(shaped.Lines).Runs).Font;
        Assert.Same(Face.Value, run.Typeface);
        Assert.Equal(
            (primary.Size, primary.Hinting, primary.HintingTarget, primary.Metrics, primary.EmbeddedBitmaps, primary.Edging, primary.Subpixel, primary.Embolden, primary.StemDarkening, primary.ColorGlyphs),
            (run.Size, run.Hinting, run.HintingTarget, run.Metrics, run.EmbeddedBitmaps, run.Edging, run.Subpixel, run.Embolden, run.StemDarkening, run.ColorGlyphs));
    }

    /// <summary>
    /// The task 31 report: SimSun found by fallback from Trebuchet MS with the GDI+ look draws as SimSun drawn directly (its 16 px
    /// bitmaps), on CanvasOptions.GdiPlus with smooth LCD sampling. Runs where both fonts are installed.
    /// </summary>
    [Fact]
    public void SimSunFallbackFromTrebuchetDrawsAsSimSunDirectly()
    {
        string fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        string simsunFile = System.IO.Path.Combine(fonts, "simsun.ttc"), trebuchetFile = System.IO.Path.Combine(fonts, "trebuc.ttf");
        if (!File.Exists(simsunFile) || !File.Exists(trebuchetFile))
        {
            return;
        }

        Typeface simsun = Typeface.FromFile(simsunFile, 0);
        FontCollection collection = new();
        collection.Add(simsun);
        var options = CanvasOptions.GdiPlus with { LcdSampling = LcdSampling.Smooth };
        Font direct = Font.FromPoints(simsun, 12, 96).WithGdiPlusLook();
        Font primary = Font.FromPoints(Typeface.FromFile(trebuchetFile), 12, 96).WithGdiPlusLook();
        ShapedText shaped = TextShaper.Shape("开始课程", primary, new ShapingOptions { Fallback = new FontFallback([collection.Families[0]], null) });
        Assert.All(Assert.Single(shaped.Lines).Runs, run => Assert.Same(simsun, run.Font.Typeface));
        Assert.Equal(Draw(direct, "开始课程", new Vector2(13, 17), options, 90, 22, Blue), DrawShaped(shaped, new Vector2(13, 17), options, 90, 22, Blue));
    }

    /// <summary>
    /// A fallback face's own tables still decide per face: a CFF or variable face asked for full hinting by the primary font draws
    /// as that face does when drawn directly with the same settings.
    /// </summary>
    [Theory]
    [InlineData("SourceSans3-Regular.otf")]
    [InlineData("derived/SourceSans3-VF-subset.otf")]
    [InlineData("derived/NotoSans-VF-subset.ttf")]
    public void CffAndVariableFallbackFacesDrawAsThemselves(string file)
    {
        Typeface face = Typeface.FromFile(TextTests.FontPath(file));
        Typeface hebrew = Typeface.FromFile(TextTests.FontPath("shaping/NotoSansHebrew-Regular.ttf"));
        FontCollection collection = new();
        collection.Add(face);
        Font primary = new Font(hebrew, 16).WithGdiPlusLook();
        ShapedText shaped = TextShaper.Shape("Ag", primary, new ShapingOptions { Fallback = new FontFallback([collection.Families[0]]) });
        // Matching may pick a variable face's default instance, so the direct drawing uses the face the run got.
        Typeface used = Assert.Single(Assert.Single(shaped.Lines).Runs).Font.Typeface;
        Assert.NotSame(hebrew, used);
        var options = CanvasOptions.GdiPlus with { LcdSampling = LcdSampling.Smooth };
        Rgba8[,] fallback = DrawShaped(shaped, new Vector2(4, 20), options, 40, 30, Blue);
        Assert.Equal(Draw(new Font(used, 16).WithGdiPlusLook(), "Ag", new Vector2(4, 20), options, 40, 30, Blue), fallback);
    }

    private static readonly Rgba8 Blue = new(0, 0x78, 0xd4, 255);

    private static Rgba8[,] Draw(Font font, string text, Vector2 at, CanvasOptions options, int width, int height, Rgba8 background)
    {
        using Raster<Rgba8> image = Raster.Create(width, height, background);
        using (RasterCanvas<Rgba8> canvas = image.CreateCanvas(options))
        {
            canvas.DrawText(text, at, font, Rgba8.White);
        }

        return Pixels(image);
    }

    private static Rgba8[,] DrawShaped(ShapedText text, Vector2 at, CanvasOptions options, int width, int height, Rgba8 background)
    {
        using Raster<Rgba8> image = Raster.Create(width, height, background);
        using (RasterCanvas<Rgba8> canvas = image.CreateCanvas(options))
        {
            canvas.DrawShapedText(text, at, new SolidPaint(Rgba8.White));
        }

        return Pixels(image);
    }

    private static Rgba8[,] Pixels(Raster<Rgba8> image)
    {
        var pixels = new Rgba8[image.Height, image.Width];
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                pixels[y, x] = image.GetPixel(x, y);
            }
        }

        return pixels;
    }

    private static Rgba8[,] DrawShaped(ShapedText text, Vector2 at)
    {
        using Raster<Rgba8> image = Raster.Create(30, 30, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = image.CreateCanvas())
        {
            canvas.DrawShapedText(text, at, new SolidPaint(Rgba8.Black));
        }

        var pixels = new Rgba8[30, 30];
        for (int y = 0; y < 30; y++)
        {
            for (int x = 0; x < 30; x++)
            {
                pixels[y, x] = image.GetPixel(x, y);
            }
        }

        return pixels;
    }

    private static Rgba8[,] Draw(Font font, string text, Vector2 at, CanvasOptions? options = null)
    {
        using Raster<Rgba8> image = Raster.Create(30, 30, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = image.CreateCanvas(options))
        {
            canvas.DrawText(text, at, font, Rgba8.Black);
        }

        var pixels = new Rgba8[30, 30];
        for (int y = 0; y < 30; y++)
        {
            for (int x = 0; x < 30; x++)
            {
                pixels[y, x] = image.GetPixel(x, y);
            }
        }

        return pixels;
    }
}
