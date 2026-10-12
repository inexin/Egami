namespace Egami.Fonts;

/// <summary>
/// A face's vertical metrics and decoration positions, with y up: in font units from <see cref="Typeface.Extents"/>, or in canvas
/// units from <see cref="Font.Extents"/>.
/// </summary>
/// <remarks>
/// Ascent, Descent and LineGap follow the browsers' rule: the OS/2 typo metrics when the face sets USE_TYPO_METRICS, else the hhea
/// metrics, falling back to the typo and then the Windows metrics when those are zero. The line height is
/// <c>Ascent - Descent + LineGap</c>.
/// </remarks>
/// <param name="Ascent">The distance from the baseline up to the top of the line, positive.</param>
/// <param name="Descent">The distance from the baseline down to the bottom of the line, negative.</param>
/// <param name="LineGap">The extra space between lines.</param>
/// <param name="CapHeight">The height of capital letters.</param>
/// <param name="XHeight">The height of lowercase letters such as x.</param>
/// <param name="UnderlinePosition">The top of the underline, relative to the baseline (usually negative).</param>
/// <param name="UnderlineThickness">The underline's thickness.</param>
/// <param name="StrikeoutPosition">The top of the strikeout line, relative to the baseline.</param>
/// <param name="StrikeoutSize">The strikeout line's thickness.</param>
/// <param name="ItalicAngle">The slant in degrees counter-clockwise from vertical: negative for text leaning right.</param>
/// <param name="BoundingBox">The box around every glyph of the face.</param>
public readonly record struct FontExtents(
    float Ascent,
    float Descent,
    float LineGap,
    float CapHeight,
    float XHeight,
    float UnderlinePosition,
    float UnderlineThickness,
    float StrikeoutPosition,
    float StrikeoutSize,
    float ItalicAngle,
    GlyphExtents BoundingBox)
{
    /// <summary>Gets the distance from one baseline to the next: <c>Ascent - Descent + LineGap</c>.</summary>
    public float LineHeight => Ascent - Descent + LineGap;

    /// <summary>Returns these extents scaled by <paramref name="scale"/>; the italic angle stays.</summary>
    /// <param name="scale">The factor, such as a font size over units per em.</param>
    /// <returns>The scaled extents.</returns>
    public FontExtents Scale(float scale) => new(
        Ascent * scale,
        Descent * scale,
        LineGap * scale,
        CapHeight * scale,
        XHeight * scale,
        UnderlinePosition * scale,
        UnderlineThickness * scale,
        StrikeoutPosition * scale,
        StrikeoutSize * scale,
        ItalicAngle,
        BoundingBox.Scale(scale));
}
