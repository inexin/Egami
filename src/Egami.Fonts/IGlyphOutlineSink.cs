namespace Egami.Fonts;

/// <summary>
/// Receives a glyph outline from <see cref="Typeface.GetGlyphOutline(ushort, IGlyphOutlineSink)"/>, in font units with y up.
/// Each contour starts with <see cref="MoveTo"/> and ends with <see cref="Close"/>; a closing line back to the start point is implied
/// by <see cref="Close"/> and not sent.
/// </summary>
/// <remarks>TrueType outlines arrive as lines and quadratic curves, CFF outlines as lines and cubic curves; neither is converted.</remarks>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="glyphs"/>
/// </example>
public interface IGlyphOutlineSink
{
    /// <summary>Starts a contour at a point.</summary>
    /// <param name="x">The x coordinate.</param>
    /// <param name="y">The y coordinate.</param>
    void MoveTo(float x, float y);

    /// <summary>Adds a straight line to a point.</summary>
    /// <param name="x">The x coordinate.</param>
    /// <param name="y">The y coordinate.</param>
    void LineTo(float x, float y);

    /// <summary>Adds a quadratic Bézier curve.</summary>
    /// <param name="cx">The control point's x.</param>
    /// <param name="cy">The control point's y.</param>
    /// <param name="x">The end point's x.</param>
    /// <param name="y">The end point's y.</param>
    void QuadTo(float cx, float cy, float x, float y);

    /// <summary>Adds a cubic Bézier curve.</summary>
    /// <param name="c1x">The first control point's x.</param>
    /// <param name="c1y">The first control point's y.</param>
    /// <param name="c2x">The second control point's x.</param>
    /// <param name="c2y">The second control point's y.</param>
    /// <param name="x">The end point's x.</param>
    /// <param name="y">The end point's y.</param>
    void CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y);

    /// <summary>Ends the contour, joining it back to its start point.</summary>
    void Close();
}
