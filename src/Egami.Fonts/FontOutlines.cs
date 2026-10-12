namespace Egami.Fonts;

/// <summary>How a face stores its glyph outlines.</summary>
public enum FontOutlines
{
    /// <summary>TrueType quadratic outlines in the 'glyf' table.</summary>
    TrueType,

    /// <summary>Cubic Type 2 charstrings in a Compact Font Format table, in an OpenType font or as a bare CFF stream.</summary>
    Cff,
}
