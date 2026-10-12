using Egami.Drawing;
using Egami.Pixels;

namespace Egami.Svg;

/// <summary>How <see cref="SvgDocument.Rasterize{TPixel}(SvgRenderOptions?)"/> and <see cref="SvgDocument.Draw(Canvas, RectF?, SvgRenderOptions?)"/> draw a document.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Svg.cs" region="rasterize"/>
/// </example>
public sealed record class SvgRenderOptions
{
    private readonly int? _width;
    private readonly int? _height;
    private readonly float _scale = 1;
    private readonly float _filterScale = 1;

    /// <summary>Gets the defaults: the document's own size, transparent, no text as paths.</summary>
    public static SvgRenderOptions Default { get; } = new();

    /// <summary>
    /// Gets the width of the raster in pixels, or <see langword="null"/> to take it from <see cref="Height"/> and the document's
    /// aspect ratio, or from its size times <see cref="Scale"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public int? Width
    {
        get => _width;
        init => _width = value is null or > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The width must be positive.");
    }

    /// <summary>
    /// Gets the height of the raster in pixels, or <see langword="null"/> to take it from <see cref="Width"/> and the document's
    /// aspect ratio, or from its size times <see cref="Scale"/>. With both set, the document is stretched to fill.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public int? Height
    {
        get => _height;
        init => _height = value is null or > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The height must be positive.");
    }

    /// <summary>Gets the scale applied to the document's size when neither <see cref="Width"/> nor <see cref="Height"/> is set; 1 by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive and finite.</exception>
    public float Scale
    {
        get => _scale;
        init => _scale = value > 0 && float.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The scale must be positive and finite.");
    }

    /// <summary>Gets the color the raster starts with; transparent by default.</summary>
    public RgbaF32 Background { get; init; }

    /// <summary>
    /// Gets the reader's languages, most preferred first, for <c>switch</c> and <c>systemLanguage</c> (BCP 47 tags such as
    /// <c>en-US</c>); English by default.
    /// </summary>
    public IReadOnlyList<string> Languages { get; init; } = ["en"];

    /// <summary>
    /// Gets a value indicating whether text is drawn as outlines instead of glyph runs, for callers that need paths, such as a
    /// plotter or a cutter. On a raster both look the same; a PDF or SVG backend then holds no selectable text.
    /// </summary>
    public bool TextAsPaths { get; init; }

    /// <summary>
    /// Gets the resolution factor a filtered group is rasterized at on a vector backend, times the device scale; 1 by default.
    /// On a raster canvas filters run at device scale.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive and finite.</exception>
    public float FilterScale
    {
        get => _filterScale;
        init => _filterScale = value > 0 && float.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The filter scale must be positive and finite.");
    }

    /// <summary>Gets the options of the raster canvas <see cref="SvgDocument.Rasterize{TPixel}(SvgRenderOptions?)"/> draws on, or <see langword="null"/> for the defaults.</summary>
    public CanvasOptions? CanvasOptions { get; init; }

    /// <summary>Gets the context of the raster <see cref="SvgDocument.Rasterize{TPixel}(SvgRenderOptions?)"/> creates, for its limits and memory; or <see langword="null"/> for the document's.</summary>
    public EgamiContext? Context { get; init; }
}
