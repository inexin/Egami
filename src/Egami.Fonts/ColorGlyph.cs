using System.Numerics;

using Egami.Operations;
using Egami.Pixels;

namespace Egami.Fonts;

/// <summary>The kinds of color glyph data a face holds.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="color"/>
/// </example>
[Flags]
public enum ColorGlyphFormats
{
    /// <summary>No color data: every glyph is an outline.</summary>
    None = 0,

    /// <summary>COLR version 0: layers of outlines, each one palette color.</summary>
    ColrV0 = 1,

    /// <summary>COLR version 1: a graph of paints with gradients, transforms and compositing.</summary>
    ColrV1 = 2,

    /// <summary>Apple's sbix: PNG, JPEG or TIFF images per size.</summary>
    Sbix = 4,

    /// <summary>Google's CBDT and CBLC: PNG or BGRA images per size.</summary>
    Cbdt = 8,

    /// <summary>OpenType SVG documents, exposed by <see cref="Typeface.TryGetSvgDocument"/> but not drawn.</summary>
    Svg = 16,
}

/// <summary>Which palettes <see cref="FontPalette"/> prefers by the backgrounds CPAL says they suit.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="color"/>
/// </example>
public enum FontPaletteBackground
{
    /// <summary>The palette <see cref="FontPalette.Index"/> names.</summary>
    Any,

    /// <summary>The first palette flagged usable with a light background, else <see cref="FontPalette.Index"/>.</summary>
    Light,

    /// <summary>The first palette flagged usable with a dark background, else <see cref="FontPalette.Index"/>.</summary>
    Dark,
}

/// <summary>One palette entry replaced, as CSS's override-colors.</summary>
/// <param name="Index">The palette entry's index.</param>
/// <param name="Color">The color it gets.</param>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="color"/>
/// </example>
public readonly record struct FontPaletteEntry(int Index, RgbaF32 Color);

/// <summary>
/// The palette a color font draws with: a CPAL palette by index or by the background it suits, with entries overridden, as CSS's
/// font-palette and @font-palette-values.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="color"/>
/// </example>
public sealed record class FontPalette
{
    private readonly int _index;
    private readonly FontPaletteEntry[] _overrides = [];

    /// <summary>Initializes a new instance of the <see cref="FontPalette"/> class.</summary>
    /// <param name="index">The CPAL palette, 0 for the first.</param>
    /// <param name="overrides">Entries to replace, or none.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative.</exception>
    public FontPalette(int index = 0, params ReadOnlySpan<FontPaletteEntry> overrides)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        _index = index;
        _overrides = overrides.ToArray();
    }

    /// <summary>Gets the font's first palette, unchanged.</summary>
    public static FontPalette Default { get; } = new();

    /// <summary>Gets the first palette usable with a light background (dark ink), else the first.</summary>
    public static FontPalette Light { get; } = new() { Background = FontPaletteBackground.Light };

    /// <summary>Gets the first palette usable with a dark background (light ink), else the first.</summary>
    public static FontPalette Dark { get; } = new() { Background = FontPaletteBackground.Dark };

    /// <summary>Gets the CPAL palette's index, used when <see cref="Background"/> is Any or finds no palette; past the font's palettes, the first.</summary>
    public int Index => _index;

    /// <summary>Gets which background the palette is chosen for. Defaults to <see cref="FontPaletteBackground.Any"/>.</summary>
    public FontPaletteBackground Background { get; init; }

    /// <summary>Gets the entries replaced.</summary>
    public IReadOnlyList<FontPaletteEntry> Overrides => _overrides;

    /// <inheritdoc/>
    public bool Equals(FontPalette? other)
        => other is not null && _index == other._index && Background == other.Background && _overrides.AsSpan().SequenceEqual(other._overrides);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = default;
        hash.Add(_index);
        hash.Add(Background);
        foreach (FontPaletteEntry entry in _overrides)
        {
            hash.Add(entry);
        }

        return hash.ToHashCode();
    }
}

/// <summary>One CPAL palette of a face.</summary>
/// <param name="Name">The palette's English name from the name table, or <see langword="null"/>.</param>
/// <param name="UsableWithLightBackground">Whether CPAL flags it for light backgrounds.</param>
/// <param name="UsableWithDarkBackground">Whether CPAL flags it for dark backgrounds.</param>
/// <param name="Colors">The entries' colors, straight alpha.</param>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="color"/>
/// </example>
public sealed record class FontPaletteInfo(string? Name, bool UsableWithLightBackground, bool UsableWithDarkBackground, IReadOnlyList<Rgba8> Colors);

/// <summary>
/// A color in a color glyph: a resolved palette color, or the foreground (palette entry 0xFFFF), which takes the paint's color and
/// keeps only this color's alpha as a factor.
/// </summary>
/// <param name="Color">The color with straight alpha; for the foreground, the alpha to multiply the paint's by.</param>
/// <param name="IsForeground">Whether this is the foreground color.</param>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="color"/>
/// </example>
public readonly record struct GlyphColor(RgbaF32 Color, bool IsForeground)
{
    /// <summary>Returns the color to draw with a foreground color.</summary>
    /// <param name="foreground">The paint's color.</param>
    /// <returns>This color, or the foreground with this color's alpha multiplied in.</returns>
    public RgbaF32 Resolve(RgbaF32 foreground) => IsForeground ? foreground with { A = foreground.A * Color.A } : Color;
}

/// <summary>What a color gradient does past the ends of its color line.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="color"/>
/// </example>
public enum ColorExtend
{
    /// <summary>The end colors continue.</summary>
    Pad,

    /// <summary>The color line repeats.</summary>
    Repeat,

    /// <summary>The color line repeats, every other copy reflected.</summary>
    Reflect,
}

/// <summary>A stop of a color line.</summary>
/// <param name="Offset">The position along the line; COLR allows values outside 0 to 1.</param>
/// <param name="Color">The color.</param>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="color"/>
/// </example>
public readonly record struct ColorStop(float Offset, GlyphColor Color);

/// <summary>The stops of a color gradient and what happens past them.</summary>
/// <param name="Stops">The stops, in order of offset.</param>
/// <param name="Extend">What happens past the ends.</param>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="color"/>
/// </example>
public sealed record class ColorLine(IReadOnlyList<ColorStop> Stops, ColorExtend Extend)
{
    /// <inheritdoc/>
    public bool Equals(ColorLine? other) => other is not null && Extend == other.Extend && Stops.SequenceEqual(other.Stops);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Extend, Stops.Count);
}

/// <summary>
/// One node of a color glyph's paint graph, in font units with y up: COLR version 1 as OpenType 1.9.1 defines it (version 0 glyphs
/// become layers of solid glyph fills), with variable values already taken at the face's position and palette colors resolved.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="color"/>
/// </example>
public abstract record class ColorPaint
{
    private protected ColorPaint()
    {
    }

    /// <summary>Calls the visitor's method for this kind of node.</summary>
    /// <param name="visitor">The visitor.</param>
    public abstract void Accept(IColorPaintVisitor visitor);
}

/// <summary>Paints drawn one over another, in order.</summary>
/// <param name="Layers">The layers, bottom first.</param>
public sealed record class ColorLayersPaint(IReadOnlyList<ColorPaint> Layers) : ColorPaint
{
    /// <inheritdoc/>
    public override void Accept(IColorPaintVisitor visitor) => visitor.VisitLayers(this);

    /// <inheritdoc/>
    public bool Equals(ColorLayersPaint? other) => other is not null && Layers.SequenceEqual(other.Layers);

    /// <inheritdoc/>
    public override int GetHashCode() => Layers.Count;
}

/// <summary>One color filling the clip.</summary>
/// <param name="Color">The color.</param>
public sealed record class ColorSolidPaint(GlyphColor Color) : ColorPaint
{
    /// <inheritdoc/>
    public override void Accept(IColorPaintVisitor visitor) => visitor.VisitSolid(this);
}

/// <summary>
/// A linear gradient filling the clip: the color line runs from <see cref="P0"/> toward <see cref="P1"/>, with lines of equal
/// color parallel to <see cref="P0"/>–<see cref="P2"/>, as COLR defines it.
/// </summary>
/// <param name="P0">The start point.</param>
/// <param name="P1">The end point.</param>
/// <param name="P2">The point giving the direction of the lines of equal color.</param>
/// <param name="Line">The color line.</param>
public sealed record class ColorLinearGradientPaint(Vector2 P0, Vector2 P1, Vector2 P2, ColorLine Line) : ColorPaint
{
    /// <inheritdoc/>
    public override void Accept(IColorPaintVisitor visitor) => visitor.VisitLinearGradient(this);
}

/// <summary>A gradient between two circles filling the clip.</summary>
/// <param name="Center0">The start circle's center.</param>
/// <param name="Radius0">The start circle's radius.</param>
/// <param name="Center1">The end circle's center.</param>
/// <param name="Radius1">The end circle's radius.</param>
/// <param name="Line">The color line.</param>
public sealed record class ColorRadialGradientPaint(Vector2 Center0, float Radius0, Vector2 Center1, float Radius1, ColorLine Line) : ColorPaint
{
    /// <inheritdoc/>
    public override void Accept(IColorPaintVisitor visitor) => visitor.VisitRadialGradient(this);
}

/// <summary>A gradient around a center filling the clip, angles in degrees counter-clockwise from the x axis (y up).</summary>
/// <param name="Center">The center.</param>
/// <param name="StartAngle">Where the color line starts.</param>
/// <param name="EndAngle">Where the color line ends.</param>
/// <param name="Line">The color line.</param>
public sealed record class ColorSweepGradientPaint(Vector2 Center, float StartAngle, float EndAngle, ColorLine Line) : ColorPaint
{
    /// <inheritdoc/>
    public override void Accept(IColorPaintVisitor visitor) => visitor.VisitSweepGradient(this);
}

/// <summary>A paint clipped to a glyph's outline.</summary>
/// <param name="Glyph">The glyph whose outline clips.</param>
/// <param name="Paint">The paint drawn inside it.</param>
public sealed record class ColorGlyphPaint(ushort Glyph, ColorPaint Paint) : ColorPaint
{
    /// <inheritdoc/>
    public override void Accept(IColorPaintVisitor visitor) => visitor.VisitGlyph(this);
}

/// <summary>Another color glyph's paint graph drawn here, as COLR's PaintColrGlyph reuses it.</summary>
/// <param name="Glyph">The color glyph reused.</param>
/// <param name="Paint">Its paint graph.</param>
public sealed record class ColorGlyphReferencePaint(ushort Glyph, ColorPaint Paint) : ColorPaint
{
    /// <inheritdoc/>
    public override void Accept(IColorPaintVisitor visitor) => visitor.VisitColorGlyph(this);
}

/// <summary>
/// A paint under an affine transform in font units: COLR's transform, translate, scale, rotate and skew paints, each as its
/// matrix (row vectors, y up).
/// </summary>
/// <param name="Matrix">The transform.</param>
/// <param name="Paint">The paint transformed.</param>
public sealed record class ColorTransformPaint(Matrix3x2 Matrix, ColorPaint Paint) : ColorPaint
{
    /// <inheritdoc/>
    public override void Accept(IColorPaintVisitor visitor) => visitor.VisitTransform(this);
}

/// <summary>Two paints composited: the source onto the backdrop with a blend or Porter-Duff mode.</summary>
/// <param name="Mode">The mode; COLR's 28 modes map onto Egami's.</param>
/// <param name="Source">The source.</param>
/// <param name="Backdrop">The backdrop.</param>
public sealed record class ColorCompositePaint(BlendMode Mode, ColorPaint Source, ColorPaint Backdrop) : ColorPaint
{
    /// <inheritdoc/>
    public override void Accept(IColorPaintVisitor visitor) => visitor.VisitComposite(this);
}

/// <summary>Walks a color glyph's paint graph, one method per kind of node.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="color"/>
/// </example>
public interface IColorPaintVisitor
{
    /// <summary>Visits layers.</summary>
    /// <param name="paint">The node.</param>
    void VisitLayers(ColorLayersPaint paint);

    /// <summary>Visits a solid fill.</summary>
    /// <param name="paint">The node.</param>
    void VisitSolid(ColorSolidPaint paint);

    /// <summary>Visits a linear gradient.</summary>
    /// <param name="paint">The node.</param>
    void VisitLinearGradient(ColorLinearGradientPaint paint);

    /// <summary>Visits a two-circle gradient.</summary>
    /// <param name="paint">The node.</param>
    void VisitRadialGradient(ColorRadialGradientPaint paint);

    /// <summary>Visits a sweep gradient.</summary>
    /// <param name="paint">The node.</param>
    void VisitSweepGradient(ColorSweepGradientPaint paint);

    /// <summary>Visits a glyph outline clip.</summary>
    /// <param name="paint">The node.</param>
    void VisitGlyph(ColorGlyphPaint paint);

    /// <summary>Visits a reused color glyph.</summary>
    /// <param name="paint">The node.</param>
    void VisitColorGlyph(ColorGlyphReferencePaint paint);

    /// <summary>Visits a transform.</summary>
    /// <param name="paint">The node.</param>
    void VisitTransform(ColorTransformPaint paint);

    /// <summary>Visits a composite.</summary>
    /// <param name="paint">The node.</param>
    void VisitComposite(ColorCompositePaint paint);
}

/// <summary>
/// A glyph's color description from <see cref="Typeface.TryGetColorGlyph"/>: a <see cref="PaintColorGlyph"/> (COLR) or a
/// <see cref="BitmapColorGlyph"/> (sbix, CBDT).
/// </summary>
/// <param name="Glyph">The glyph.</param>
/// <param name="Format">The table it came from.</param>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="color"/>
/// </example>
public abstract record class ColorGlyph(ushort Glyph, ColorGlyphFormats Format);

/// <summary>A COLR glyph: its paint graph and, for version 1, the clip box the font gives.</summary>
/// <param name="Glyph">The glyph.</param>
/// <param name="Format"><see cref="ColorGlyphFormats.ColrV0"/> or <see cref="ColorGlyphFormats.ColrV1"/>.</param>
/// <param name="Paint">The paint graph's root.</param>
/// <param name="ClipBox">The ClipList box in font units, or <see langword="null"/>.</param>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="color"/>
/// </example>
public sealed record class PaintColorGlyph(ushort Glyph, ColorGlyphFormats Format, ColorPaint Paint, GlyphExtents? ClipBox) : ColorGlyph(Glyph, Format);

/// <summary>The encoding of a bitmap glyph's image.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="color"/>
/// </example>
public enum BitmapGlyphEncoding
{
    /// <summary>A PNG file.</summary>
    Png,

    /// <summary>A JPEG file.</summary>
    Jpeg,

    /// <summary>A TIFF file.</summary>
    Tiff,

    /// <summary>Raw premultiplied BGRA, four bytes per pixel, rows top first (CBDT's uncompressed formats).</summary>
    PremultipliedBgra,
}

/// <summary>
/// An sbix or CBDT glyph: its image, the strike's size and where the image goes, in font units with y up, so it scales with the
/// font; the drawing code decodes and resamples it.
/// </summary>
/// <param name="Glyph">The glyph.</param>
/// <param name="Format"><see cref="ColorGlyphFormats.Sbix"/> or <see cref="ColorGlyphFormats.Cbdt"/>.</param>
/// <param name="Data">The image bytes, a slice of the font's memory.</param>
/// <param name="Encoding">The image's encoding.</param>
/// <param name="PixelWidth">The image's width in pixels.</param>
/// <param name="PixelHeight">The image's height in pixels.</param>
/// <param name="PixelsPerEm">The strike's size.</param>
/// <param name="Bounds">Where the image's edges go, in font units, y up.</param>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="color"/>
/// </example>
public sealed record class BitmapColorGlyph(
    ushort Glyph,
    ColorGlyphFormats Format,
    ReadOnlyMemory<byte> Data,
    BitmapGlyphEncoding Encoding,
    int PixelWidth,
    int PixelHeight,
    int PixelsPerEm,
    GlyphExtents Bounds) : ColorGlyph(Glyph, Format);
