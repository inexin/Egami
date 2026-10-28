using System.Numerics;

using Egami.Fonts;
using Egami.Pixels;

namespace Egami.Drawing.Tests.Canvas;

/// <summary>FontHinting.Full and FontMetrics.GridFitted on a raster (2.7.5 tasks 16 to 19).</summary>
public class FullHintingTests
{
    private static Typeface DejaVu => Typeface.FromFile(TextTests.FontPath("DejaVuSans.ttf"));

    private static Rgba8[] Draw(Font font, CanvasOptions? options = null, float scale = 1)
    {
        using Raster<Rgba8> image = Raster.Create(260, 40, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = image.CreateCanvas(options ?? new CanvasOptions { PixelGeometry = PixelGeometry.RgbHorizontal }))
        {
            canvas.Scale(scale, scale);
            canvas.DrawText("Hamburgefonstiv 0123", new Vector2(4.3f / scale, 26 / scale), font, Rgba8.Black);
        }

        var pixels = new Rgba8[image.Width * image.Height];
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                pixels[(y * image.Width) + x] = image.GetPixel(x, y);
            }
        }

        return pixels;
    }

    [Fact]
    public void FullHintingChangesMasksAndEachTargetHasItsOwn()
    {
        var font = new Font(DejaVu, 13) { Subpixel = false };
        Rgba8[] none = Draw(font), full = Draw(font with { Hinting = FontHinting.Full });
        Assert.NotEqual(none, full);
        Assert.Equal(full, Draw(font with { Hinting = FontHinting.Full }));
        Assert.Equal(none, Draw(font));

        // Another target is its own cache entry: drawing it in between leaves the first target's masks as they were.
        _ = Draw(font with { Hinting = FontHinting.Full, HintingTarget = TrueTypeTarget.Gdi });
        Assert.Equal(full, Draw(font with { Hinting = FontHinting.Full }));
    }

    [Fact]
    public void FacesThatCannotBeHintedFallBackToLight()
    {
        var cff = new Font(Typeface.FromFile(TextTests.FontPath("SourceSans3-Regular.otf")), 13) { Subpixel = false };
        Assert.Equal(Draw(cff with { Hinting = FontHinting.Light }), Draw(cff with { Hinting = FontHinting.Full }));
    }

    [Fact]
    public void GridFittedMetricsPutGlyphsOnWholePixels()
    {
        // With grid-fitted metrics, subpixel positioning does not apply: the same text with and without it draws the same.
        var font = new Font(DejaVu, 13) { Hinting = FontHinting.Full, Metrics = FontMetrics.GridFitted };
        Assert.Equal(Draw(font with { Subpixel = false }), Draw(font with { Subpixel = true }));
    }

    [Fact]
    public void NonSquareScalesAreNotHinted()
    {
        // A stretched canvas falls back to light hinting rather than hinting at one axis' ppem.
        using Raster<Rgba8> a = Raster.Create(200, 60, Rgba8.White), b = Raster.Create(200, 60, Rgba8.White);
        var font = new Font(DejaVu, 13) { Subpixel = false };
        foreach ((Raster<Rgba8> image, FontHinting h) in new[] { (a, FontHinting.Full), (b, FontHinting.Light) })
        {
            using RasterCanvas<Rgba8> canvas = image.CreateCanvas();
            canvas.Scale(1.5f, 2);
            canvas.DrawText("Hamburg", new Vector2(4, 20), font with { Hinting = h }, Rgba8.Black);
        }

        for (int y = 0; y < 60; y++)
        {
            for (int x = 0; x < 200; x++)
            {
                Assert.Equal(b.GetPixel(x, y), a.GetPixel(x, y));
            }
        }
    }
}
