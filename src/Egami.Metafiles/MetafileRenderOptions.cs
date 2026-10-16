using Egami.Drawing;
using Egami.Fonts;
using Egami.Pixels;

namespace Egami.Metafiles;

/// <summary>Which records of an EMF+ dual file play (design question 5).</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Metafiles.cs" region="metafile-gdi"/>
/// </example>
public enum MetafilePlayback
{
    /// <summary>As GDI+ plays the file: the EMF+ records, and the GDI records only where EmfPlusGetDC hands drawing to GDI. Plain WMF and EMF play their GDI records.</summary>
    Auto,

    /// <summary>As GDI plays the file: the GDI records only, EMF+ records ignored, as programs without GDI+ draw a dual file.</summary>
    Gdi,
}

/// <summary>How <see cref="Metafile.Draw(Canvas, RectF?, MetafileRenderOptions?)"/> and <see cref="Metafile.Rasterize{TPixel}(MetafileRenderOptions?)"/> draw a metafile.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Metafiles.cs" region="metafile-fonts"/>
/// </example>
public sealed record class MetafileRenderOptions
{
    private readonly int? _width;
    private readonly int? _height;

    /// <summary>Gets the defaults: 96 DPI, transparent, antialiased, the installed fonts, played as GDI+ plays it.</summary>
    public static MetafileRenderOptions Default { get; } = new();

    /// <summary>
    /// Gets the resolution <see cref="Metafile.Rasterize{TPixel}(MetafileRenderOptions?)"/> draws the picture's
    /// <see cref="Metafile.Size"/> at when neither <see cref="Width"/> nor <see cref="Height"/> is set; 96 by default.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive and finite.</exception>
    public float Dpi
    {
        get;
        init => field = value > 0 && float.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "Dpi must be positive and finite.");
    } = 96;

    /// <summary>Gets the raster's width in pixels, or <see langword="null"/> to take it from <see cref="Height"/> and the aspect ratio, or from <see cref="Dpi"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public int? Width
    {
        get => _width;
        init => _width = value is null or > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The width must be positive.");
    }

    /// <summary>
    /// Gets the raster's height in pixels, or <see langword="null"/> to take it from <see cref="Width"/> and the aspect ratio, or
    /// from <see cref="Dpi"/>. With both set, the picture is stretched to fill, as Word stretches it to its frame.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public int? Height
    {
        get => _height;
        init => _height = value is null or > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The height must be positive.");
    }

    /// <summary>Gets the color the raster starts with; transparent by default.</summary>
    public RgbaF32 Background { get; init; }

    /// <summary>
    /// Gets a value indicating whether GDI records are drawn antialiased; <see langword="true"/> by default (design question 6).
    /// <see langword="false"/> gives GDI's own aliased look, for comparing pixels with Windows. EMF+ records follow the file's
    /// own smoothing mode either way.
    /// </summary>
    public bool Antialias { get; init; } = true;

    /// <summary>Gets which records of an EMF+ dual file play; <see cref="MetafilePlayback.Auto"/> by default.</summary>
    public MetafilePlayback Playback { get; init; }

    /// <summary>Gets the fonts text is drawn with, or <see langword="null"/>, the default, for <see cref="FontCollection.System"/>.</summary>
    public FontCollection? Fonts { get; init; }

    /// <summary>
    /// Gets faces to use for faces the fonts lack, by name (case-insensitive), tried after the built-in table of
    /// metric-compatible open fonts (Arial to Liberation Sans, Calibri to Carlito and so on; design question 8).
    /// </summary>
    public IReadOnlyDictionary<string, string>? FontSubstitutions { get; init; }

    /// <summary>
    /// Gets a value indicating whether text is drawn as outlines instead of glyph runs, for callers that need paths. On a raster
    /// both look the same; a PDF or SVG backend then holds no selectable text.
    /// </summary>
    public bool TextAsPaths { get; init; }

    /// <summary>Gets the options of the raster canvas <see cref="Metafile.Rasterize{TPixel}(MetafileRenderOptions?)"/> draws on, or <see langword="null"/> for the defaults.</summary>
    public CanvasOptions? CanvasOptions { get; init; }

    /// <summary>Gets the context of the raster <see cref="Metafile.Rasterize{TPixel}(MetafileRenderOptions?)"/> creates, for its limits and memory, or <see langword="null"/> for the metafile's.</summary>
    public EgamiContext? Context { get; init; }
}
