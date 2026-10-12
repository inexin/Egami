namespace Egami.Svg;

/// <summary>How <see cref="SvgCanvas"/> writes text.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Svg.cs" region="svg-canvas-text"/>
/// </example>
public enum SvgTextMode
{
    /// <summary>
    /// Each glyph's outline, written once in <c>defs</c> and placed with <c>use</c>, so the file looks the same everywhere with no
    /// fonts installed. The default.
    /// </summary>
    Outlines,

    /// <summary>
    /// <c>text</c> elements with the font's family, weight, style and size and each cluster's position, so the text stays
    /// selectable and searchable; the viewer draws it with its own copy of the font, or with the subset
    /// <see cref="SvgWriteOptions.EmbedSubset"/> embeds.
    /// </summary>
    Text,
}

/// <summary>The format <see cref="SvgCanvas"/> embeds drawn images in.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Svg.cs" region="svg-canvas"/>
/// </example>
public enum SvgImageFormat
{
    /// <summary>PNG: lossless, with alpha. The default.</summary>
    Png,

    /// <summary>JPEG for opaque images (smaller for photos); images with transparency are still written as PNG.</summary>
    Jpeg,
}

/// <summary>How <see cref="SvgCanvas"/> writes SVG: text, number precision, layout, image format and fallback resolution.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Svg.cs" region="svg-canvas"/>
/// </example>
public sealed record class SvgWriteOptions
{
    /// <summary>Gets the defaults: glyph outlines, 3 decimals, indented, PNG images, fallback images at the device resolution.</summary>
    public static SvgWriteOptions Default { get; } = new();

    /// <summary>Gets how text is written. Defaults to <see cref="SvgTextMode.Outlines"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not defined.</exception>
    public SvgTextMode Text
    {
        get;
        init => field = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, null);
    }

    /// <summary>
    /// Gets a value indicating whether <see cref="SvgTextMode.Text"/> embeds a subset of each font, with the glyphs the file uses,
    /// as OpenType font data in an <c>@font-face</c> rule, so the text looks the same where the font is not installed. A face whose
    /// license forbids embedding or subsetting is not embedded. Outlines need no fonts and ignore it. Defaults to
    /// <see langword="false"/>.
    /// </summary>
    public bool EmbedSubset { get; init; }

    /// <summary>Gets the number of decimals written, from 0 to 9; trailing zeros are left out. Defaults to 3.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is below 0 or above 9.</exception>
    public int Precision
    {
        get;
        init => field = value is >= 0 and <= 9 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The precision must be from 0 to 9 decimals.");
    } = 3;

    /// <summary>
    /// Gets a value indicating whether to write the smallest file: the shortest path data the grammar allows and no indentation.
    /// Defaults to <see langword="false"/>.
    /// </summary>
    public bool Compact { get; init; }

    /// <summary>Gets the format drawn images are embedded in. Defaults to <see cref="SvgImageFormat.Png"/>; fallback images are always PNG.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not defined.</exception>
    public SvgImageFormat ImageFormat
    {
        get;
        init => field = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, null);
    }

    /// <summary>
    /// Gets the resolution of fallback images (calls SVG cannot express, drawn by Egami and embedded as PNG), in image pixels per
    /// device unit: 2 gives sharper fallbacks on high-density screens. Defaults to 1.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive and finite.</exception>
    public float FallbackScale
    {
        get;
        init => field = value > 0 && float.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The fallback scale must be positive and finite.");
    } = 1;

    /// <summary>
    /// Gets the context whose diagnostics receive what the writer had to rasterize or approximate (<c>Svg.FallbackImage</c>,
    /// <c>Svg.Approximated</c>, <c>Svg.FontNotEmbedded</c>) when the canvas is disposed, or <see langword="null"/> for
    /// <see cref="EgamiContext.Default"/>.
    /// </summary>
    public EgamiContext? Context { get; init; }
}
