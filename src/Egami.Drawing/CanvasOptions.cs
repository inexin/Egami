using Egami.Operations;

namespace Egami.Drawing;

/// <summary>Whether a clip keeps what is inside a shape or what is outside it.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="clips"/>
/// </example>
public enum ClipOp
{
    /// <summary>Drawing stays inside the shape and the clip so far.</summary>
    Intersect = 0,

    /// <summary>Drawing stays inside the clip so far but outside the shape.</summary>
    Difference = 1,
}

/// <summary>How <see cref="Canvas.DrawPoints"/> reads its points.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="chart-axes"/>
/// </example>
public enum PointMode
{
    /// <summary>Each point is drawn as a dot: a square, or a circle with <see cref="LineCap.Round"/>, of the stroke's width.</summary>
    Points = 0,

    /// <summary>Each pair of points is a line; an odd last point is ignored.</summary>
    Lines = 1,

    /// <summary>The points are joined, in order, into one open polyline.</summary>
    Polygon = 2,
}

/// <summary>The order of a display's color subpixels, which LCD text edging needs to know.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="quality"/>
/// </example>
public enum PixelGeometry
{
    /// <summary>Not known: LCD edging falls back to grayscale.</summary>
    Unknown,

    /// <summary>Red, green and blue stripes from left to right, the common LCD layout.</summary>
    RgbHorizontal,

    /// <summary>Blue, green and red stripes from left to right.</summary>
    BgrHorizontal,

    /// <summary>Red, green and blue stripes from top to bottom, as on a rotated display.</summary>
    RgbVertical,

    /// <summary>Blue, green and red stripes from top to bottom.</summary>
    BgrVertical,
}

/// <summary>Where a canvas puts its pixel centres, for paths and unhinted text: GDI+'s <c>PixelOffsetMode</c>.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawing.cs" region="gdiplus-shapes"/>
/// </example>
public enum PixelOffset
{
    /// <summary>Whole coordinates fall on pixel edges and pixel centres at half coordinates: Egami's default, GDI+'s <c>Half</c>.</summary>
    Half,

    /// <summary>
    /// Whole coordinates fall on pixel centres, as GDI+ draws by default (<c>PixelOffsetMode.None</c>, <c>Default</c> and
    /// <c>HighSpeed</c>): paths, their paints and unhinted text move half a pixel right and down. Hinted text does not move,
    /// and images sit on GDI+'s grid, with source pixel centres on destination pixel centres: an image scaled by s moves (1 − s) / 2
    /// pixels, one drawn 1:1 not at all.
    /// </summary>
    None,
}

/// <summary>How a canvas antialiases shapes.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawing.cs" region="gdiplus-shapes"/>
/// </example>
public enum Antialiasing
{
    /// <summary>The exact area each pixel has inside the shape, in 256 levels: Egami's default.</summary>
    Exact,

    /// <summary>
    /// As measured from GDI+'s <c>SmoothingMode.AntiAlias</c> and <c>HighQuality</c>: 8 by 4 point samples a pixel, at eighths
    /// across and quarters down from its top-left corner, so coverage comes in 32 steps. A sample on a shape's left or top edge is
    /// inside and one on its right or bottom edge is outside. Pens whose device width is 1.5 pixels or less draw as GDI+'s thin
    /// lines: one pixel thick across the line's major axis, with pointed ends and no caps.
    /// </summary>
    GdiPlus,
}

/// <summary>Settings for a raster canvas.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="canvas"/>
/// </example>
public sealed record class CanvasOptions
{
    private readonly ColorSpace _colorSpace;
    /// <summary>The default <see cref="Tolerance"/>, in device pixels.</summary>
    internal const float DefaultTolerance = 0.1f;

    private readonly float _tolerance = DefaultTolerance;
    private readonly PixelGeometry _pixelGeometry;
    private readonly float _textGamma = 1;
    private readonly float _textContrast;
    private readonly LcdFilter _lcdFilter = LcdFilter.Default;
    private readonly LcdSampling _lcdSampling;
    private readonly float _clearTypeLevel = 1;
    private readonly PixelOffset _pixelOffset;
    private readonly Antialiasing _antialiasing;

    /// <summary>Gets the options every canvas starts with.</summary>
    public static CanvasOptions Default { get; } = new();

    /// <summary>
    /// Gets System.Drawing's canvas settings, as measured on Windows in 2.7.5 and 2.7.6. For ClearTypeGridFit text: an RGB stripe,
    /// <see cref="LcdFilter.Gdi"/>, <see cref="LcdSampling.Gdi"/> (coverage in sixths, blended through GDI+'s 8-bit gamma tables) and
    /// a <see cref="TextGamma"/> of 1.4 (GDI+'s 1 + Graphics.TextContrast / 10 at its default contrast of 4). For shapes and images:
    /// GDI+'s default <see cref="PixelOffset.None"/> and its <see cref="Antialiasing.GdiPlus"/>. Use it with fonts from
    /// <see cref="Fonts.Font.WithGdiPlusLook"/>; the resample filter is each draw call's, such as <c>ResampleFilter.GdiPlusBilinear</c>.
    /// </summary>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawing.cs" region="gdiplus"/>
    /// </example>
    public static CanvasOptions GdiPlus { get; } = new()
    {
        PixelGeometry = PixelGeometry.RgbHorizontal,
        LcdFilter = LcdFilter.Gdi,
        LcdSampling = LcdSampling.Gdi,
        TextGamma = 1.4f,
        PixelOffset = PixelOffset.None,
        Antialiasing = Antialiasing.GdiPlus,
    };

    /// <summary>
    /// Gets where edges and paints blend. <see cref="ColorSpace.Encoded"/> (the default) blends the stored values, as Egami's other
    /// operations and SkiaSharp do; <see cref="ColorSpace.Linear"/> blends in linear light, which keeps
    /// antialiased edges and translucent colors from darkening.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not defined.</exception>
    public ColorSpace ColorSpace
    {
        get => _colorSpace;
        init => _colorSpace = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, null);
    }

    /// <summary>
    /// Gets the subpixel order of the display the image is for, which <see cref="Fonts.FontEdging.Lcd"/> text needs. Defaults to
    /// <see cref="PixelGeometry.Unknown"/>, which draws LCD-edged text in grayscale.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not defined.</exception>
    public PixelGeometry PixelGeometry
    {
        get => _pixelGeometry;
        init => _pixelGeometry = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, null);
    }

    /// <summary>
    /// Gets a renderer for color glyphs of formats Egami.Drawing does not draw itself (OpenType SVG with Egami.Svg's
    /// <c>SvgGlyphRenderer.Instance</c>), or <see langword="null"/> (the default) to draw those glyphs as outlines.
    /// </summary>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Svg.cs" region="svg-glyphs"/>
    /// </example>
    public IColorGlyphRenderer? ColorGlyphRenderer { get; init; }

    /// <summary>
    /// Gets how far, in device pixels, a curve's straight-line approximation may stray from it. Defaults to 0.1; smaller values cost
    /// more lines.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not between 0.001 and 10.</exception>
    public float Tolerance
    {
        get => _tolerance;
        init => _tolerance = value is >= 0.001f and <= 10 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The tolerance must be from 0.001 to 10 pixels.");
    }

    /// <summary>
    /// Gets the gamma of the blend that draws glyph coverage, from 0.5 to 3. Defaults to 1, today's blend. Each channel blends as
    /// (a·src^γ + (1 − a)·dst^γ)^(1/γ) on the stored values, as GDI ClearType does (its default is 1.4); above 1 light text on a
    /// dark ground gets heavier and dark text on a light ground lighter. Shapes and images never pass through it.
    /// </summary>
    /// <remarks>
    /// The gamma applies to solid source-over text on an 8-bit RGB canvas outside a layer, the text LCD edging can draw; other text
    /// takes <see cref="TextContrast"/> only. With <see cref="ColorSpace.Linear"/> the gamma is ignored: that blend is already a
    /// gamma-correct one.
    /// </remarks>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="text-gamma"/>
    /// </example>
    /// <exception cref="ArgumentOutOfRangeException">The value is not between 0.5 and 3.</exception>
    public float TextGamma
    {
        get => _textGamma;
        init => _textGamma = value is >= 0.5f and <= 3 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The text gamma must be from 0.5 to 3.");
    }

    /// <summary>
    /// Gets how much thin glyph coverage is boosted before it blends, from 0 to 1. Defaults to 0, none. Coverage a becomes
    /// a + (1 − a)·c·a, the contrast boost of Skia and DirectWrite: it thickens antialiased edges and leaves full coverage alone.
    /// It applies to all cached glyphs, whatever the paint, canvas or <see cref="ColorSpace"/>.
    /// </summary>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="text-gamma"/>
    /// </example>
    /// <exception cref="ArgumentOutOfRangeException">The value is not between 0 and 1.</exception>
    public float TextContrast
    {
        get => _textContrast;
        init => _textContrast = value is >= 0 and <= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The text contrast must be from 0 to 1.");
    }

    /// <summary>
    /// Gets the filter <see cref="Fonts.FontEdging.Lcd"/> text spreads subpixel coverage with. Defaults to
    /// <see cref="LcdFilter.Default"/>; <see cref="LcdFilter.Gdi"/> is the one measured from GDI and GDI+.
    /// </summary>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="lcd-look"/>
    /// </example>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    public LcdFilter LcdFilter
    {
        get => _lcdFilter;
        init => _lcdFilter = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Gets how <see cref="Fonts.FontEdging.Lcd"/> text samples glyphs. Defaults to <see cref="Drawing.LcdSampling.Smooth"/>;
    /// <see cref="Drawing.LcdSampling.Gdi"/> leaves edges along the stripes unsmoothed, as GDI+ ClearType does.
    /// </summary>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="lcd-look"/>
    /// </example>
    /// <exception cref="ArgumentOutOfRangeException">The value is not defined.</exception>
    public LcdSampling LcdSampling
    {
        get => _lcdSampling;
        init => _lcdSampling = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, null);
    }

    /// <summary>
    /// Gets how much of the LCD mask <see cref="Fonts.FontEdging.Lcd"/> text uses, from 0 (the grayscale coverage of the same
    /// samples, no colour fringes) to 1 (the default, the filtered subpixel coverage), as Windows' ClearType level. Values between
    /// mix the two, trading fringes for sharpness.
    /// </summary>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="lcd-look"/>
    /// </example>
    /// <exception cref="ArgumentOutOfRangeException">The value is not between 0 and 1.</exception>
    public float ClearTypeLevel
    {
        get => _clearTypeLevel;
        init => _clearTypeLevel = value is >= 0 and <= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The ClearType level must be from 0 to 1.");
    }

    /// <summary>
    /// Gets where pixel centres are. Defaults to <see cref="Drawing.PixelOffset.Half"/>; <see cref="Drawing.PixelOffset.None"/> is
    /// GDI+'s default, which moves paths and unhinted text half a pixel.
    /// </summary>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawing.cs" region="gdiplus-shapes"/>
    /// </example>
    /// <exception cref="ArgumentOutOfRangeException">The value is not defined.</exception>
    public PixelOffset PixelOffset
    {
        get => _pixelOffset;
        init => _pixelOffset = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, null);
    }

    /// <summary>
    /// Gets how shapes are antialiased. Defaults to <see cref="Drawing.Antialiasing.Exact"/>; <see cref="Drawing.Antialiasing.GdiPlus"/>
    /// samples as GDI+ does and draws its thin pens. Text and images are not affected, nor are paints with
    /// <see cref="Paint.Antialias"/> off.
    /// </summary>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawing.cs" region="gdiplus-shapes"/>
    /// </example>
    /// <exception cref="ArgumentOutOfRangeException">The value is not defined.</exception>
    public Antialiasing Antialiasing
    {
        get => _antialiasing;
        init => _antialiasing = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, null);
    }
}
