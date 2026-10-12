namespace Egami.Fonts;

/// <summary>Whether a face is upright, italic or oblique.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="match"/>
/// </example>
public enum FontSlant
{
    /// <summary>Upright, the normal style.</summary>
    Upright,

    /// <summary>A true italic design.</summary>
    Italic,

    /// <summary>A slanted version of the upright design.</summary>
    Oblique,
}

/// <summary>
/// A face's style, or the style asked for when matching: weight from 1 to 1000 (400 normal, 700 bold), width from 1 to 9 (5
/// normal, as OS/2 usWidthClass), and slant.
/// </summary>
/// <param name="Weight">The weight, 1 to 1000; 400 is normal and 700 bold.</param>
/// <param name="Width">The width class, 1 (ultra-condensed) to 9 (ultra-expanded); 5 is normal.</param>
/// <param name="Slant">Upright, italic or oblique.</param>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="match"/>
/// </example>
public readonly record struct FontStyle(int Weight = 400, int Width = 5, FontSlant Slant = FontSlant.Upright)
{
    /// <summary>Gets weight 400, width 5, upright.</summary>
    public static FontStyle Normal => new(400, 5, FontSlant.Upright);

    /// <summary>Gets weight 700, width 5, upright.</summary>
    public static FontStyle Bold => new(700, 5, FontSlant.Upright);

    /// <summary>Gets weight 400, width 5, italic.</summary>
    public static FontStyle Italic => new(400, 5, FontSlant.Italic);

    /// <summary>Gets weight 700, width 5, italic.</summary>
    public static FontStyle BoldItalic => new(700, 5, FontSlant.Italic);

    /// <summary>Gets the weight clamped to 1 to 1000.</summary>
    internal int ClampedWeight => Math.Clamp(Weight == 0 ? 400 : Weight, 1, 1000);

    /// <summary>Gets the width clamped to 1 to 9.</summary>
    internal int ClampedWidth => Math.Clamp(Width == 0 ? 5 : Width, 1, 9);
}
