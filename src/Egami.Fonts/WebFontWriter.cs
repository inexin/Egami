using Egami.Fonts.Parsing;
using Egami.Fonts.Woff;

namespace Egami.Fonts;

/// <summary>The web font container <see cref="WebFontWriter"/> writes.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="webfont"/>
/// </example>
public enum WebFontFormat
{
    /// <summary>WOFF 1.0 (W3C): each table compressed with zlib on its own. Holds a single font only.</summary>
    Woff,

    /// <summary>WOFF 2.0 (W3C): every table in one Brotli stream, with the glyf, loca and hmtx transforms; holds collections too.</summary>
    Woff2,
}

/// <summary>How <see cref="WebFontWriter"/> writes a web font.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="webfont"/>
/// </example>
public sealed record WebFontOptions
{
    /// <summary>Gets the default options: WOFF2 with the glyf, loca and hmtx transforms, keeping the source's metadata and private data.</summary>
    public static WebFontOptions Default { get; } = new();

    /// <summary>Gets the container to write. Defaults to <see cref="WebFontFormat.Woff2"/>.</summary>
    public WebFontFormat Format { get; init; } = WebFontFormat.Woff2;

    /// <summary>
    /// Gets whether WOFF2 stores glyf and loca in the transformed form (WOFF 2.0, section 5.1), which compresses TrueType outlines
    /// by about a third more. Defaults to true. A glyf table that cannot be read is stored as it is, with a
    /// <c>Font.TransformSkipped</c> diagnostic. Ignored for WOFF 1.0.
    /// </summary>
    public bool TransformGlyf { get; init; } = true;

    /// <summary>
    /// Gets whether WOFF2 leaves out the left side bearings of hmtx that equal each glyph's xMin (WOFF 2.0, section 5.4), when glyf
    /// is transformed too. Defaults to true. Ignored for WOFF 1.0.
    /// </summary>
    public bool TransformHmtx { get; init; } = true;

    /// <summary>
    /// Gets the extended metadata block (XML, written in UTF-8 and compressed), or <see langword="null"/> (the default) to keep the
    /// source's: the block of the WOFF or WOFF2 file the face or data came from, none for an sfnt. An empty string writes none.
    /// </summary>
    public string? Metadata { get; init; }

    /// <summary>
    /// Gets the private data block, or <see langword="null"/> (the default) to keep the source's, as for <see cref="Metadata"/>.
    /// Empty memory writes none.
    /// </summary>
    public ReadOnlyMemory<byte>? PrivateData { get; init; }

    /// <summary>
    /// Gets the context whose <see cref="SafetyLimits.MaxTotalBytes"/> bounds the input and output and which receives diagnostics,
    /// or <see langword="null"/> for the face's own context (<see cref="FontLoadOptions.Context"/>), else <see cref="EgamiContext.Default"/>.
    /// </summary>
    public EgamiContext? Context { get; init; }
}

/// <summary>
/// Writes fonts as WOFF 1.0 and WOFF 2.0 files for the web, losslessly: every table of the font is kept, and a WOFF2 file read
/// back gives the same tables but glyf and loca, whose glyphs read the same but may be laid out more compactly, and head, whose
/// flags gain bit 11 (the font has been through a lossless modifying transform), as the reference encoder sets it.
/// </summary>
/// <remarks>
/// Compression uses .NET's own zlib (<see cref="System.IO.Compression.ZLibStream"/>, the smallest of levels 9 and 8 with the
/// default and filtered strategies) and Brotli (<see cref="System.IO.Compression.BrotliEncoder"/> at quality 11 with a 4 MB
/// window). A table zlib cannot shrink is stored uncompressed in WOFF 1.0. The file's version is the head table's fontRevision.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="webfont"/>
/// </example>
public static class WebFontWriter
{
    /// <summary>Writes a face as a web font: its tables as loaded; a face of a collection becomes a single font.</summary>
    /// <param name="face">The face.</param>
    /// <param name="options">How to write it, or <see langword="null"/> for <see cref="WebFontOptions.Default"/>.</param>
    /// <returns>The WOFF or WOFF2 file.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="face"/> is null.</exception>
    /// <exception cref="ArgumentException">The face is a bare CFF font, which has no sfnt tables.</exception>
    /// <exception cref="LimitExceededException">The font is larger than the context's <see cref="SafetyLimits.MaxTotalBytes"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="webfont"/>
    /// </example>
    public static byte[] Write(Typeface face, WebFontOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(face);
        options ??= WebFontOptions.Default;
        if (face.IsBareCff)
        {
            throw new ArgumentException("A bare CFF font has no sfnt tables to write as a web font; subset it to OpenType CFF first.", nameof(face));
        }

        EgamiContext context = options.Context ?? face.Options.EffectiveContext;
        WebFontSource source = WebFontSource.FromFace(face, context.Limits.MaxTotalBytes);
        return WebFontEncoder.Encode(source, options, context);
    }

    /// <summary>Writes a face as a web font to a stream.</summary>
    /// <param name="face">The face.</param>
    /// <param name="stream">The stream; it is written but not closed.</param>
    /// <param name="options">How to write it, or <see langword="null"/> for <see cref="WebFontOptions.Default"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="face"/> or <paramref name="stream"/> is null.</exception>
    /// <exception cref="ArgumentException">The face is a bare CFF font, which has no sfnt tables.</exception>
    /// <exception cref="LimitExceededException">The font is larger than the context's <see cref="SafetyLimits.MaxTotalBytes"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="webfont"/>
    /// </example>
    public static void Write(Typeface face, Stream stream, WebFontOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        stream.Write(Write(face, options));
    }

    /// <summary>Writes a subset as a web font.</summary>
    /// <param name="subset">The subset, from <see cref="FontSubsetter"/>.</param>
    /// <param name="options">How to write it, or <see langword="null"/> for <see cref="WebFontOptions.Default"/>.</param>
    /// <returns>The WOFF or WOFF2 file.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="subset"/> is null.</exception>
    /// <exception cref="ArgumentException">The subset is a bare CFF stream (<see cref="SubsetFormat.BareCff"/>).</exception>
    /// <exception cref="LimitExceededException">The subset is larger than the context's <see cref="SafetyLimits.MaxTotalBytes"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="webfont-subset"/>
    /// </example>
    public static byte[] Write(FontSubset subset, WebFontOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(subset);
        if (subset.Format == SubsetFormat.BareCff)
        {
            throw new ArgumentException("A bare CFF subset has no sfnt tables to write as a web font; subset to OpenType CFF instead.", nameof(subset));
        }

        return Write(subset.Data, options);
    }

    /// <summary>
    /// Writes font data as a web font: an sfnt, or a TrueType or OpenType collection (WOFF2 only), whose tables are shared as in the
    /// collection. A WOFF or WOFF2 file is unwrapped and written again, keeping its metadata and private data unless the options
    /// replace them.
    /// </summary>
    /// <param name="data">The font data.</param>
    /// <param name="options">How to write it, or <see langword="null"/> for <see cref="WebFontOptions.Default"/>.</param>
    /// <returns>The WOFF or WOFF2 file.</returns>
    /// <exception cref="ArgumentException">
    /// The data is not an sfnt, collection or web font (a bare CFF stream, say), or is a collection and the format is WOFF 1.0.
    /// </exception>
    /// <exception cref="CorruptDataException">The table directory or a web font's data cannot be read.</exception>
    /// <exception cref="LimitExceededException">The data is larger than the context's <see cref="SafetyLimits.MaxTotalBytes"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="webfont"/>
    /// </example>
    public static byte[] Write(ReadOnlyMemory<byte> data, WebFontOptions? options = null)
    {
        options ??= WebFontOptions.Default;
        EgamiContext context = options.Context ?? EgamiContext.Default;
        WebFontSource source = WebFontSource.FromData(data, context);
        return WebFontEncoder.Encode(source, options, context);
    }
}
