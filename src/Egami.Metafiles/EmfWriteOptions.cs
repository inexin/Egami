namespace Egami.Metafiles;

/// <summary>How <see cref="EmfCanvas"/> writes text.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Metafiles.cs" region="emf-canvas"/>
/// </example>
public enum EmfText
{
    /// <summary>Each glyph run as filled outlines, so the picture looks the same where the font is not installed. The default.</summary>
    Outlines,

    /// <summary>
    /// The font's name with glyph indices and positions (EMF+ DrawDriverString, and GDI ExtTextOutW with glyph indices): smaller
    /// and kept as text, but drawn with wrong glyphs by a reader that lacks the exact font. A run is still written as outlines
    /// when its face has no family name GDI can hold, is synthesized bold or italic, emboldened or a variable instance, when its
    /// glyphs are turned or scaled one by one, when the transform mirrors it, or when its paint is not one color. Color glyphs
    /// are drawn as their layers and images in either mode.
    /// </summary>
    Glyphs,
}

/// <summary>How <see cref="EmfCanvas"/> writes an EMF file: the record kinds, resolution, text, fallback resolution and compression.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Metafiles.cs" region="emf-canvas"/>
/// </example>
public sealed record class EmfWriteOptions
{
    /// <summary>Gets the defaults: EMF+ dual at 96 DPI, text as outlines, fallback images at 2 pixels per unit, uncompressed.</summary>
    public static EmfWriteOptions Default { get; } = new();

    /// <summary>
    /// Gets which records are written. <see cref="MetafileKind.EmfPlusDual"/> (the default, as GDI+ writes) has EMF+ records,
    /// antialiased and with alpha in Office and GDI+, and beside them GDI records that draw the same picture with opaque colors
    /// for programs without GDI+; <see cref="MetafileKind.Emf"/> writes the GDI records alone and
    /// <see cref="MetafileKind.EmfPlusOnly"/> the EMF+ records alone.
    /// </summary>
    /// <exception cref="ArgumentException">The value is <see cref="MetafileKind.Wmf"/>, which Egami does not write, or not defined.</exception>
    public MetafileKind Kind
    {
        get;
        init => field = value is MetafileKind.Emf or MetafileKind.EmfPlusDual or MetafileKind.EmfPlusOnly
            ? value
            : throw new ArgumentException("EmfCanvas writes Emf, EmfPlusDual or EmfPlusOnly files; WMF is not written.", nameof(value));
    } = MetafileKind.EmfPlusDual;

    /// <summary>
    /// Gets the resolution of the reference device, in device units per inch: the canvas's units are its pixels, so a canvas
    /// 96 units wide at 96 DPI makes a picture one inch wide. Defaults to 96.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive and finite.</exception>
    public float Dpi
    {
        get;
        init => field = value > 0 && float.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The resolution must be positive and finite.");
    } = 96;

    /// <summary>Gets how text is written. Defaults to <see cref="EmfText.Outlines"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not defined.</exception>
    public EmfText Text
    {
        get;
        init => field = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, null);
    }

    /// <summary>
    /// Gets the resolution of fallback images (calls EMF cannot express, drawn by Egami and embedded as bitmaps), in image pixels
    /// per device unit. Defaults to 2.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive and finite.</exception>
    public float FallbackScale
    {
        get;
        init => field = value > 0 && float.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The fallback scale must be positive and finite.");
    } = 2;

    /// <summary>
    /// Gets the picture's name, written to the header's description after the application name "Egami", or
    /// <see langword="null"/> (the default) for no description.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>Gets a value indicating whether the file is gzip-compressed (EMZ). Defaults to <see langword="false"/>.</summary>
    public bool Compress { get; init; }
}
