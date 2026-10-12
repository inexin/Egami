using System.Numerics;

using Egami.Fonts;
using Egami.Fonts.Shaping;
using Egami.Pixels;

namespace Egami.Drawing.Tests.Canvas;

/// <summary>Shaped text on the canvas: DrawText with fallback equals drawing its ShapedText, and complex scripts draw.</summary>
public class ShapedTextDrawingTests
{
    private static readonly Lazy<Typeface> Hebrew = new(() => Typeface.FromFile(TextTests.FontPath("shaping/NotoSansHebrew-Regular.ttf")));

    private static readonly Lazy<Typeface> Arabic = new(() => Typeface.FromFile(TextTests.FontPath("shaping/NotoSansArabic-Regular.ttf")));

    private static Raster<Rgba8> Draw(Action<RasterCanvas<Rgba8>> draw)
    {
        Raster<Rgba8> image = Raster.Create<Rgba8>(240, 90);
        using RasterCanvas<Rgba8> canvas = image.CreateCanvas();
        canvas.Clear(Rgba8.White);
        draw(canvas);
        return image;
    }

    [Fact]
    public void DrawTextWithFallbackEqualsItsShapedText()
    {
        FontCollection fonts = new();
        fonts.Add(Hebrew.Value);
        Font font = new(TextTests.Noto.Value, 20);
        ShapingOptions options = new() { Fallback = new FontFallback(fonts.Families) };
        string text = "Shalom \u05E9\u05DC\u05D5\u05DD!\nsecond line";
        using Raster<Rgba8> direct = Draw(c => c.DrawText(text, new Vector2(10, 30), font, Rgba8.Black, options));
        ShapedText shaped = TextShaper.Shape(text, font, options);
        using Raster<Rgba8> reused = Draw(c => c.DrawShapedText(shaped, new Vector2(10, 30), Rgba8.Black));
        Advanced.Pixels.Equal(direct, reused);
        Assert.Contains(shaped.Lines[0].Runs, r => r.Font.Typeface == Hebrew.Value);
    }

    [Fact]
    public void ArabicDrawsJoinedAndMeasuresAsDrawn()
    {
        Font font = new(Arabic.Value, 24);
        string text = "\u0645\u0631\u062D\u0628\u0627";
        GlyphRun run = font.Shape(text);
        Assert.Equal(1, run.BidiLevel);
        Assert.Equal(font.MeasureText(text).Advance, run.Advance);
        using Raster<Rgba8> image = Draw(c => c.DrawText(text, new Vector2(10, 40), font, Rgba8.Black));
        int dark = 0;
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                dark += image.GetPixel(x, y).R < 128 ? 1 : 0;
            }
        }

        Assert.True(dark > 50);
    }
}
