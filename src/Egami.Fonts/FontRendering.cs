namespace Egami.Fonts;

/// <summary>How glyph outlines are fitted to the pixel grid before rasterizing.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="quality"/>
/// </example>
public enum FontHinting
{
    /// <summary>Outlines are drawn as designed, at their exact positions.</summary>
    None,

    /// <summary>
    /// Vertical only: the baseline, x-height and cap-height zones snap to whole pixels (from a CFF font's blue zones, else zones
    /// measured from the font's own flat glyphs), points between them following; x values and advances never move. Applies up to
    /// 48 pixels per em under scale-and-translate transforms.
    /// </summary>
    Light,
}

/// <summary>How a glyph's edges are turned into pixels.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="quality"/>
/// </example>
public enum FontEdging
{
    /// <summary>Grayscale antialiasing: each pixel covered by the share of its area inside the outline.</summary>
    Antialias,

    /// <summary>One bit per pixel: a pixel is covered when its centre is inside the outline (nonzero rule).</summary>
    Alias,

    /// <summary>
    /// Subpixel (LCD) antialiasing: coverage at three times the resolution along the canvas's pixel geometry, low-pass filtered
    /// and blended per color channel. Drawn only onto an opaque 8-bit RGB target with a known pixel geometry and a solid
    /// source-over paint; anything else falls back to <see cref="Antialias"/> with one diagnostic per canvas. Never for color
    /// glyphs.
    /// </summary>
    Lcd,
}

/// <summary>The numbers synthetic bold and italic use, shared by measuring and drawing.</summary>
internal static class Synthesis
{
    /// <summary>The synthetic oblique angle: CSS Fonts 4's default oblique, 14 degrees.</summary>
    public const float ItalicAngle = 14;

    /// <summary>tan(14°): x moves right by this times the height above the baseline.</summary>
    public static readonly float Skew = MathF.Tan(ItalicAngle * MathF.PI / 180);

    /// <summary>
    /// The synthetic bold outset in the same units as <paramref name="pixelsPerEm"/>: em/24 at 9 pixels per em and below, em/32
    /// at 36 and above, linear between.
    /// </summary>
    public static float BoldOutset(float pixelsPerEm)
    {
        float em = MathF.Abs(pixelsPerEm);
        float factor = em <= 9 ? 1 / 24f : em >= 36 ? 1 / 32f : (1 / 24f) + (((1 / 32f) - (1 / 24f)) * (em - 9) / 27);
        return em * factor;
    }
}

/// <summary>
/// An alignment zone for light hinting, in font units: the flat edge that snaps to a whole pixel and the overshoot beyond it (round
/// letters' tops and bottoms), which keeps its distance from the flat edge rounded to whole pixels.
/// </summary>
internal readonly record struct HintZone(float Flat, float Overshoot);
