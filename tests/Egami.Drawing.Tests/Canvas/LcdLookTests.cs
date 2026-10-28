using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

using Egami.Fonts;
using Egami.Pixels;

namespace Egami.Drawing.Tests.Canvas;

/// <summary>
/// The look controls of 2.7.5 stage 2: LCD filter presets, GDI sampling, the ClearType level, Font.Embolden and stem darkening.
/// The defaults of each keep 1.2.0's output, which <see cref="TextGammaTests"/> pins.
/// </summary>
public class LcdLookTests
{
    private static Typeface Noto => TextTests.Noto.Value;

    private static readonly Font Lcd = new Font(Noto, 13) { Edging = FontEdging.Lcd, Subpixel = false };

    [Fact]
    public void PresetWeightsAddUpTo256()
    {
        foreach (LcdFilter f in new[] { LcdFilter.Default, LcdFilter.Balanced, LcdFilter.Light, LcdFilter.Gdi })
        {
            Assert.Equal(256, f.Weights.Sum());
        }

        Assert.Equal([16, 64, 96, 64, 16], LcdFilter.Default.Weights);
        Assert.Equal([8, 77, 86, 77, 8], LcdFilter.Balanced.Weights);
        Assert.Equal(LcdFilter.Light, LcdFilter.Gdi);
        Assert.Equal("Gdi", LcdFilter.Gdi.Name);
    }

    [Fact]
    public void CustomWeightsAreNormalised()
    {
        Assert.Equal([52, 51, 51, 51, 51], LcdFilter.Custom(1, 1, 1, 1, 1).Weights);
        Assert.Equal(LcdFilter.Default, LcdFilter.Custom(1, 4, 6, 4, 1));
        Assert.Equal("Custom", LcdFilter.Custom(1, 4, 6, 4, 1).Name);
        Assert.Throws<ArgumentOutOfRangeException>(() => LcdFilter.Custom(0, 0, 0, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => LcdFilter.Custom(1, -1, 1, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => LcdFilter.Custom(1, float.NaN, 1, 1, 1));
    }

    [Fact]
    public void OptionsValidate()
    {
        Assert.Throws<ArgumentNullException>(() => new CanvasOptions { LcdFilter = null! });
        Assert.Throws<ArgumentOutOfRangeException>(() => new CanvasOptions { LcdSampling = (LcdSampling)7 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new CanvasOptions { ClearTypeLevel = 1.01f });
        Assert.Throws<ArgumentOutOfRangeException>(() => new CanvasOptions { ClearTypeLevel = float.NaN });
        Assert.Equal(LcdFilter.Default, CanvasOptions.Default.LcdFilter);
        Assert.Equal(LcdSampling.Smooth, CanvasOptions.Default.LcdSampling);
        Assert.Equal(1f, CanvasOptions.Default.ClearTypeLevel);
    }

    [Fact]
    public void ExplicitDefaultsDrawAsTheDefaults()
    {
        var options = new CanvasOptions { LcdFilter = LcdFilter.Custom(1, 4, 6, 4, 1), LcdSampling = LcdSampling.Smooth, ClearTypeLevel = 1 };
        Assert.Equal(Draw(new CanvasOptions(), Lcd), Draw(options, Lcd));
        Assert.Equal(Draw(new CanvasOptions(), Lcd with { Embolden = 0, StemDarkening = false }), Draw(new CanvasOptions(), Lcd));
    }

    [Fact]
    public void EachFilterDrawsItsOwnMasks()
    {
        // One canvas after another in the same process: the filter is part of the glyph cache key.
        Rgba8[] standard = Draw(new CanvasOptions(), Lcd);
        Rgba8[] light = Draw(new CanvasOptions { LcdFilter = LcdFilter.Light }, Lcd);
        Rgba8[] balanced = Draw(new CanvasOptions { LcdFilter = LcdFilter.Balanced }, Lcd);
        Assert.NotEqual(standard, light);
        Assert.NotEqual(standard, balanced);
        Assert.NotEqual(light, balanced);
        Assert.Equal(light, Draw(new CanvasOptions { LcdFilter = LcdFilter.Gdi }, Lcd));
    }

    [Fact]
    public void ClearTypeLevelZeroIsGrayAndHalfIsBetween()
    {
        Rgba8[] gray = Draw(new CanvasOptions { ClearTypeLevel = 0 }, Lcd);
        Assert.All(gray, p => Assert.True(Math.Abs(p.R - p.G) <= 1 && Math.Abs(p.G - p.B) <= 1, $"{p} is not gray"));
        Rgba8[] full = Draw(new CanvasOptions(), Lcd), half = Draw(new CanvasOptions { ClearTypeLevel = 0.5f }, Lcd);
        Assert.True(Fringe(gray) < Fringe(half) && Fringe(half) < Fringe(full));
    }

    [Theory]
    [InlineData(PixelGeometry.RgbHorizontal)]
    [InlineData(PixelGeometry.BgrVertical)]
    public void GdiSamplingLeavesEdgesAlongTheStripesUnsmoothed(PixelGeometry geometry)
    {
        // An em dash's long edges run along horizontal stripes; a vertical bar's along vertical ones.
        bool vertical = geometry == PixelGeometry.BgrVertical;
        char bar = vertical ? '|' : '—';
        var options = new CanvasOptions { PixelGeometry = geometry };
        int smooth = PartialLines(GlyphBar(options, bar), vertical);
        int gdi = PartialLines(GlyphBar(options with { LcdSampling = LcdSampling.Gdi }, bar), vertical);
        Assert.True(smooth > 0);
        Assert.True(gdi < smooth, $"{gdi} partial lines with Gdi sampling, {smooth} with Smooth.");
    }

    [Fact]
    public void GdiSamplingDrawsText()
    {
        Rgba8[] smooth = Draw(new CanvasOptions(), Lcd), gdi = Draw(new CanvasOptions { LcdSampling = LcdSampling.Gdi }, Lcd);
        Assert.NotEqual(smooth, gdi);
        Assert.InRange(Ink(gdi), Ink(smooth) * 0.8, Ink(smooth) * 1.2);
    }

    [Fact]
    public void SyntheticBoldDrawsAsInEgami120()
    {
        // Synthetic bold is now the bold curve of the general outset: masks and paths must not change. Hash from main before 2.7.5 stage 2.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (FontEdging e in new[] { FontEdging.Antialias, FontEdging.Lcd })
        {
            using Raster<Rgba8> image = Raster.Create(240, 60, Rgba8.White);
            using (RasterCanvas<Rgba8> c = image.CreateCanvas(new CanvasOptions { PixelGeometry = PixelGeometry.RgbHorizontal }))
            {
                c.DrawText("Bold Hamburg", new Vector2(4, 24), new Font(Noto, 13) { Synthesis = FontSynthesis.Bold, Edging = e }, Rgba8.Black);
                c.FillPath(new Font(Noto, 20) { Synthesis = FontSynthesis.Bold }.Shape("Path").ToPath(new Vector2(4, 52)), new SolidPaint(Rgba8.Black));
            }

            hash.AppendData(MemoryMarshal.AsBytes(Pixels(image).AsSpan()));
        }

        Assert.Equal("38A0BAE221DC47C978CDBE0DFF99D1B35F1FB121640B165EC4B91721DD3A40F8", Convert.ToHexString(hash.GetHashAndReset()));
    }

    [Fact]
    public void EmboldenGrowsInkNotAdvances()
    {
        var font = new Font(Noto, 13);
        Font bolder = font with { Embolden = 0.3f };
        Assert.Equal(font.MeasureText("Hamburg").Advance, bolder.MeasureText("Hamburg").Advance);
        ushort h = Noto.GetGlyph('H');
        GlyphExtents a = font.GetGlyphExtents(h), b = bolder.GetGlyphExtents(h);
        Assert.Equal(a.XMin - 0.3f, b.XMin, 4);
        Assert.Equal(a.YMax + 0.3f, b.YMax, 4);
        Assert.True(Ink(Draw(new CanvasOptions(), bolder)) > Ink(Draw(new CanvasOptions(), font)) * 1.1);
        Assert.True(bolder.Shape("H").ToPath(Vector2.Zero).Bounds.Width > font.Shape("H").ToPath(Vector2.Zero).Bounds.Width + 0.5f);
        Assert.Throws<ArgumentOutOfRangeException>(() => font with { Embolden = -0.1f });
        Assert.Throws<ArgumentOutOfRangeException>(() => font with { Embolden = float.PositiveInfinity });
    }

    [Fact]
    public void EmboldenAddsToSyntheticBoldAndScalesWithTheCanvas()
    {
        var bold = new Font(Noto, 13) { Synthesis = FontSynthesis.Bold };
        ushort h = Noto.GetGlyph('H');
        Assert.Equal(bold.GetGlyphExtents(h).XMin - 0.2f, (bold with { Embolden = 0.2f }).GetGlyphExtents(h).XMin, 4);

        // Embolden is in the font's units: drawn at 2x, it grows twice as many pixels as the font at twice the size with the same amount.
        using Raster<Rgba8> scaled = Raster.Create(240, 60, Rgba8.White), big = Raster.Create(240, 60, Rgba8.White);
        using (RasterCanvas<Rgba8> c = scaled.CreateCanvas())
        {
            c.Scale(2, 2);
            c.DrawText("Hamburg", new Vector2(4, 20), new Font(Noto, 13) { Embolden = 0.25f, Subpixel = false }, Rgba8.Black);
        }

        using (RasterCanvas<Rgba8> c = big.CreateCanvas())
        {
            c.DrawText("Hamburg", new Vector2(8, 40), new Font(Noto, 26) { Embolden = 0.5f, Subpixel = false }, Rgba8.Black);
        }

        Assert.Equal(Pixels(big), Pixels(scaled));
    }

    [Fact]
    public void StemDarkeningDarkensSmallTextOnly()
    {
        var small = new Font(Noto, 8) { Subpixel = false };
        var large = new Font(Noto, 40) { Subpixel = false };
        Assert.True(Ink(Draw(new CanvasOptions(), small with { StemDarkening = true })) > Ink(Draw(new CanvasOptions(), small)));
        Assert.Equal(Draw(new CanvasOptions(), large), Draw(new CanvasOptions(), large with { StemDarkening = true }));
        ushort h = Noto.GetGlyph('H');
        Assert.Equal(small.GetGlyphExtents(h), (small with { StemDarkening = true }).GetGlyphExtents(h));
    }

    private static Rgba8[] Draw(CanvasOptions options, Font font)
    {
        using Raster<Rgba8> image = Raster.Create(240, 60, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = image.CreateCanvas(options with { PixelGeometry = PixelGeometry.RgbHorizontal }))
        {
            canvas.DrawText("Hamburgefonstiv", new Vector2(4, 40), font, Rgba8.Black);
        }

        return Pixels(image);
    }

    // One glyph at 37 pixels per em, where its edges fall between pixels, with its ink around (20, 20).
    private static Rgba8[] GlyphBar(CanvasOptions options, char bar)
    {
        var font = new Font(Noto, 37) { Edging = FontEdging.Lcd, Subpixel = false };
        GlyphExtents e = font.GetGlyphExtents(Noto.GetGlyph(bar));
        var at = new Vector2(MathF.Round(20 - ((e.XMin + e.XMax) / 2)), MathF.Round(20 + ((e.YMin + e.YMax) / 2)));
        using Raster<Rgba8> image = Raster.Create(48, 48, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = image.CreateCanvas(options))
        {
            canvas.DrawText(bar.ToString(), at, font, Rgba8.Black);
        }

        return Pixels(image);
    }

    // Rows (or columns) of the bar that are partly covered, along the middle of it.
    private static int PartialLines(Rgba8[] pixels, bool vertical)
    {
        int partial = 0;
        for (int i = 0; i < 48; i++)
        {
            Rgba8 p = vertical ? pixels[(20 * 48) + i] : pixels[(i * 48) + 20];
            partial += p.G is > 24 and < 231 ? 1 : 0;
        }

        return partial;
    }

    private static double Fringe(Rgba8[] pixels) => pixels.Sum(p => Math.Abs(p.R - p.G) + Math.Abs(p.G - p.B));

    private static double Ink(Rgba8[] pixels) => pixels.Sum(p => 765 - (p.R + p.G + p.B));

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
