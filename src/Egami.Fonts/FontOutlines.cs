namespace Egami.Fonts;

/// <summary>How a face stores its glyph outlines.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="load"/>
/// </example>
public enum FontOutlines
{
    /// <summary>TrueType quadratic outlines in the 'glyf' table.</summary>
    TrueType,

    /// <summary>Cubic Type 2 charstrings in a Compact Font Format table (CFF, or CFF2 in a variable font), in an OpenType font or as a bare CFF stream.</summary>
    Cff,

    /// <summary>No outlines: a color font of bitmaps only (CBDT or sbix), whose glyphs draw from their images and have no paths, or a
    /// font with no glyph data at all (a subset made for shaping), whose glyphs are empty.</summary>
    None,
}
