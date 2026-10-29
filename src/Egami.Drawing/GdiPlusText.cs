using Egami.Fonts;
using Egami.Fonts.Layout;

namespace Egami.Drawing;

/// <summary>
/// Text placement as GDI+ (System.Drawing) does it, for callers that put text in a box themselves and want it where
/// Graphics.DrawString would put it. Measured from GDI+ on Windows; Egami's own layout (<see cref="Canvas.DrawText(string, RectF, ParagraphStyle, VerticalAlign)"/>)
/// is unchanged.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawing.cs" region="gdiplus-baseline"/>
/// </example>
public static class GdiPlusText
{
    /// <summary>
    /// Gets the baseline DrawString puts one line of text on in a layout box with a line alignment (StringFormat.LineAlignment):
    /// the line is a cell tall (ascent and descent), plus 1/8 em with the default format, placed at the box's top, middle or
    /// bottom, and the baseline is <see cref="Font.GdiPlusAscent"/> below the line's top. For a grid-fitted font (as
    /// <see cref="Font.WithGdiPlusLook"/> makes) the line's top is first rounded to a whole pixel, as ClearTypeGridFit does, so
    /// the baseline is whole; draw with an untransformed canvas, or one moved by whole pixels, to keep it on the pixel grid.
    /// </summary>
    /// <param name="font">The font.</param>
    /// <param name="box">The layout box, in canvas units, y down.</param>
    /// <param name="align">Where the line goes in the box: Near (<see cref="VerticalAlign.Top"/>), Center or Far
    /// (<see cref="VerticalAlign.Bottom"/>).</param>
    /// <param name="typographic">Whether the format is StringFormat.GenericTypographic, which adds no 1/8 em.</param>
    /// <returns>The baseline's y.</returns>
    /// <exception cref="InvalidOperationException">The font is <c>default(Font)</c>, which has no typeface.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="align"/> is not defined.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawing.cs" region="gdiplus-baseline"/>
    /// </example>
    public static float Baseline(Font font, RectF box, VerticalAlign align, bool typographic = false)
    {
        float block = font.GdiPlusCellHeight + (typographic ? 0 : font.Size / 8);
        float top = box.Y + align switch
        {
            VerticalAlign.Top => 0,
            VerticalAlign.Center => (box.Height - block) / 2,
            VerticalAlign.Bottom => box.Height - block,
            _ => throw new ArgumentOutOfRangeException(nameof(align), align, null),
        };
        bool grid = font.Metrics == FontMetrics.GridFitted && font.Hinting == FontHinting.Full;
        return (grid ? MathF.Floor(top + 0.5f) : top) + font.GdiPlusAscent;
    }
}
