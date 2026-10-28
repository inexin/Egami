using System.Numerics;

using Egami.Drawing;
using Egami.Fonts;
using Egami.Pixels;

namespace Egami.DocSamples;

[Collection("Samples")]
public class SystemDrawing
{
    private static string FontFile => System.IO.Path.Combine(AppContext.BaseDirectory, "fonts", "NotoSans-Regular.ttf");

    [Fact]
    public void GdiPlusLook()
    {
        Typeface face = Typeface.FromFile(FontFile);
        using Raster<Rgb8> button = Raster.Create(200, 40, new Rgb8(0x00, 0x78, 0xd4));

        #region gdiplus
        // Text that looks like System.Drawing's Graphics.DrawString with TextRenderingHint.ClearTypeGridFit: the font's own hints
        // and grid-fitted advances, GDI+'s ClearType filter, sampling and gamma.
        Font label = new Font(face, 16).WithGdiPlusLook();
        using (RasterCanvas<Rgb8> canvas = button.CreateCanvas(CanvasOptions.GdiPlusClearType))
        {
            float width = label.MeasureText("Start the course").Advance;
            canvas.DrawText("Start the course", new Vector2(MathF.Round((200 - width) / 2), 26), label, Rgba8.White);
        }

        // A different Graphics.TextContrast (0 to 12) is a different text gamma: 1 + contrast / 10.
        CanvasOptions contrast8 = CanvasOptions.GdiPlusClearType with { TextGamma = 1 + (8 / 10f) };
        #endregion

        Assert.Equal(1.8f, contrast8.TextGamma, 3);
        Assert.Equal(FontHinting.Full, label.Hinting);
        Assert.Contains(Enumerable.Range(0, button.Width), x => button.GetPixel(x, 22).R > 128);
    }

    #region drawstring
    // Graphics.DrawString(text, font, brush, x, y) with the default StringFormat starts the text 1/6 em right of the point (to a
    // whole pixel) with the first baseline the font's ascent below it; StringFormat.GenericTypographic drops that padding.
    // MeasureString with the default format adds about 0.275 em on each side and 1/8 em to the height (measured on Windows).
    public static Vector2 DrawStringOrigin(Font font, Vector2 point, bool typographic = false)
        => new(point.X + (typographic ? 0 : MathF.Round(font.Size / 6)), point.Y + font.Extents.Ascent);

    public static (float Width, float Height) MeasureStringSize(Font font, string text, bool typographic = false)
    {
        TextMeasure m = font.MeasureText(text);
        return typographic ? (m.Advance, m.Height) : (m.Advance + (0.55f * font.Size), m.Height + (font.Size / 8));
    }
    #endregion

    [Fact]
    public void DrawStringRules()
    {
        Typeface face = Typeface.FromFile(FontFile);
        var font = new Font(face, 24);
        Assert.Equal(new Vector2(14, 10 + font.Extents.Ascent), DrawStringOrigin(font, new Vector2(10, 10)));
        Assert.Equal(new Vector2(10, 10 + font.Extents.Ascent), DrawStringOrigin(font, new Vector2(10, 10), typographic: true));
        (float w, float h) = MeasureStringSize(font, "Label");
        (float tw, float th) = MeasureStringSize(font, "Label", typographic: true);
        Assert.Equal(0.55f * 24, w - tw, 3);
        Assert.Equal(3, h - th, 3);
    }
}
