namespace Egami.Fonts;

/// <summary>A box around ink, such as a glyph's outline, with y up in font units or in a <see cref="Font"/>'s size.</summary>
/// <param name="XMin">The left edge.</param>
/// <param name="YMin">The bottom edge.</param>
/// <param name="XMax">The right edge.</param>
/// <param name="YMax">The top edge.</param>
public readonly record struct GlyphExtents(float XMin, float YMin, float XMax, float YMax)
{
    /// <summary>Gets an empty box, the extents of a glyph with no outline such as the space.</summary>
    public static GlyphExtents Empty => default;

    /// <summary>Gets the width.</summary>
    public float Width => XMax - XMin;

    /// <summary>Gets the height.</summary>
    public float Height => YMax - YMin;

    /// <summary>Gets whether the box has no area.</summary>
    public bool IsEmpty => XMax <= XMin || YMax <= YMin;

    /// <summary>Returns the box scaled by <paramref name="scale"/>.</summary>
    /// <param name="scale">The factor, such as a font size over units per em.</param>
    /// <returns>The scaled box.</returns>
    public GlyphExtents Scale(float scale) => new(XMin * scale, YMin * scale, XMax * scale, YMax * scale);
}
