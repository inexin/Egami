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

    /// <summary>Gets the options every canvas starts with.</summary>
    public static CanvasOptions Default { get; } = new();

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
}
