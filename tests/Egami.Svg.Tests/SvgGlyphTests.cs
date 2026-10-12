using System.Numerics;

using Egami.Drawing;
using Egami.Fonts;
using Egami.Pixels;

namespace Egami.Svg.Tests;

/// <summary>Task 15: OpenType SVG glyphs drawn through CanvasOptions.ColorGlyphs.</summary>
public class SvgGlyphTests
{
    private static Font Emoji(float size)
    {
        FontCollection fonts = new();
        fonts.AddFile(System.IO.Path.Combine(TestSvg.Root, "tests", "svg", "resvg", "fonts", "TwitterColorEmoji.subset.ttf"));
        return fonts.Match("Twitter Color Emoji", default)!.Value.ToFont(size);
    }

    private static Raster<Rgba8> Draw(CanvasOptions options)
    {
        Raster<Rgba8> raster = Raster.Create<Rgba8>(80, 80);
        using RasterCanvas<Rgba8> canvas = raster.CreateCanvas(options);
        canvas.DrawText("\U0001F600", new Vector2(8, 66), Emoji(64), new SolidPaint(new RgbaF32(0, 0, 0, 1)));
        return raster;
    }

    [Fact]
    public void TheRendererDrawsTheGlyphsDocumentInColor()
    {
        using Raster<Rgba8> color = Draw(new CanvasOptions { ColorGlyphs = SvgGlyphRenderer.Instance });
        using Raster<Rgba8> plain = Draw(CanvasOptions.Default);

        // Twemoji's face is #FFCC4D; without the renderer the glyph is the font's black outline.
        Rgba8 face = RenderTreeTests.Pixel(color, 40, 45);
        Assert.True(face.R > 200 && face.G > 150 && face.B < 120, face.ToString());
        Rgba8 outline = RenderTreeTests.Pixel(plain, 40, 45);
        Assert.True(outline.R < 60 && outline.G < 60, outline.ToString());
    }

    [Fact]
    public void AGlyphWithoutADocumentIsLeftToTheOutline()
    {
        FontCollection fonts = new();
        fonts.AddFile(System.IO.Path.Combine(TestSvg.Root, "tests", "svg", "resvg", "fonts", "NotoSans-Regular.ttf"));
        Font font = fonts.Match("Noto Sans", default)!.Value.ToFont(20);
        using Raster<Rgba8> raster = Raster.Create<Rgba8>(10, 10);
        using RasterCanvas<Rgba8> canvas = raster.CreateCanvas();
        Assert.False(SvgGlyphRenderer.Instance.TryDraw(canvas, font, 1, Matrix3x2.Identity, new SolidPaint(new RgbaF32(0, 0, 0, 1))));
    }
}
