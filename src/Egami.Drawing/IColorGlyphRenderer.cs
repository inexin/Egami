using System.Numerics;

using Egami.Fonts;

namespace Egami.Drawing;

/// <summary>
/// Draws color glyphs of a format Egami.Drawing does not draw itself, such as the SVG documents of an OpenType SVG table
/// (Egami.Svg's <c>SvgGlyphRenderer</c>). Set on <see cref="CanvasOptions.ColorGlyphs"/>, it is asked first for every glyph
/// whose font has that format, except where the font has a COLR version 1 glyph, so the order is COLR v1, the renderer, COLR v0,
/// sbix, CBDT, then the outline.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Svg.cs" region="svg-glyphs"/>
/// </example>
public interface IColorGlyphRenderer
{
    /// <summary>Gets the color formats the renderer draws, such as <see cref="ColorGlyphFormats.Svg"/>.</summary>
    ColorGlyphFormats Formats { get; }

    /// <summary>Draws one glyph, or returns false when the font has nothing of the renderer's formats for it.</summary>
    /// <param name="canvas">The canvas to draw on.</param>
    /// <param name="font">The font, at its size.</param>
    /// <param name="glyph">The glyph id.</param>
    /// <param name="transform">Maps the glyph's space (its origin on the baseline, the font's size, y down) into the canvas's local coordinates.</param>
    /// <param name="paint">The text's paint: its color is the foreground some glyphs draw in.</param>
    /// <returns>True when the glyph was drawn.</returns>
    bool TryDraw(Canvas canvas, Font font, ushort glyph, Matrix3x2 transform, Paint paint);
}
