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

    /// <summary>
    /// The font's own TrueType instructions, run by Egami's interpreter for <see cref="Font.HintingTarget"/> (in ClearType
    /// compatibility mode, as Windows runs them for ClearType), at a whole number of pixels per em, up to 256, under
    /// scale-and-translate transforms with equal x and y scales. Faces without instructions, CFF faces, variable faces and sizes the
    /// gasp table leaves unhinted fall back to <see cref="Light"/>.
    /// </summary>
    Full,
}

/// <summary>Which rasterizer the TrueType interpreter answers for: what GETINFO tells the font and how instructions are softened.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="full-hinting"/>
/// </example>
public enum TrueTypeTarget
{
    /// <summary>GDI+ with ClearTypeGridFit, as measured on Windows: rasterizer version 42 with ClearType (GETINFO 0x2C2A).</summary>
    GdiPlus,

    /// <summary>GDI ClearType, as measured: version 42 with ClearType, compatible widths and symmetric rendering (GETINFO 0x46C2A).</summary>
    Gdi,

    /// <summary>A FreeType-like target: version 40 with ClearType and subpixel positioning.</summary>
    FreeType,
}

/// <summary>Where a font's advances come from.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="full-hinting"/>
/// </example>
public enum FontMetrics
{
    /// <summary>The design advances, scaled: fractional pixels, the same at every size.</summary>
    Design,

    /// <summary>
    /// The hinted advances at a whole number of pixels per em: each glyph's right phantom point less its left after the font's
    /// instructions run for <see cref="Font.HintingTarget"/>. Measuring and drawing both use them, and glyph origins snap to whole
    /// pixels. Faces that cannot be hinted keep their design advances, rounded.
    /// </summary>
    GridFitted,

    /// <summary>
    /// GDI's compatible widths: the advances black-and-white hinting gives, from the hdmx table when it has the size, else the
    /// rounded design advance where the LTSH table says the glyph scales linearly, else the glyph's instructions run for a
    /// monochrome rasterizer. Whole pixels, cached per face and size; faces that cannot be hinted keep their design advances,
    /// rounded. Glyph origins snap to whole pixels.
    /// </summary>
    Compatible,
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

    /// <summary>Stem darkening's outset in device pixels: 0.15 at 8 pixels per em and below, none at 32 and above, linear between.</summary>
    public static float DarkeningOutset(float pixelsPerEm)
    {
        float em = MathF.Abs(pixelsPerEm);
        return em <= 8 ? 0.15f : em >= 32 ? 0 : 0.15f * (32 - em) / 24;
    }
}

/// <summary>
/// An alignment zone for light hinting, in font units: the flat edge that snaps to a whole pixel and the overshoot beyond it (round
/// letters' tops and bottoms), which keeps its distance from the flat edge rounded to whole pixels.
/// </summary>
internal readonly record struct HintZone(float Flat, float Overshoot);
