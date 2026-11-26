using Egami.Fonts.Parsing;
using Egami.Fonts.Woff;

namespace Egami.Fonts;

/// <summary>
/// Unwraps WOFF 1.0 and WOFF 2.0 files into the TrueType or OpenType font they hold, as <see cref="WebFontWriter"/> wrapped it:
/// the reverse of the writer, for tools that want the sfnt back.
/// </summary>
/// <remarks>
/// WOFF 1.0 tables are inflated as they were stored. A WOFF2 file's glyf, loca and hmtx are rebuilt from their transformed
/// forms (WOFF 2.0, sections 5.1 and 5.4), so glyf's glyphs read the same as the original's but may be laid out more compactly;
/// a WOFF2 collection becomes a TrueType or OpenType collection (.ttc) with its shared tables stored once. The container's
/// extended metadata and private data are not part of the sfnt; <see cref="Typeface.GetWoffMetadata"/> and
/// <see cref="Typeface.GetWoffPrivateData"/> read them.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="webfont-unwrap"/>
/// </example>
public static class WebFontReader
{
    /// <summary>Turns a WOFF or WOFF2 file back into the sfnt, or the collection, it holds.</summary>
    /// <param name="data">The WOFF or WOFF2 file.</param>
    /// <param name="context">
    /// The context whose <see cref="SafetyLimits.MaxTotalBytes"/> bounds the font the file unpacks to and which receives
    /// diagnostics, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.
    /// </param>
    /// <returns>The TrueType or OpenType font (.ttf, .otf), or a collection (.ttc) for a WOFF2 collection.</returns>
    /// <exception cref="ArgumentException">The data is not a WOFF or WOFF2 file.</exception>
    /// <exception cref="CorruptDataException">A table does not decompress, or a transformed table cannot be rebuilt.</exception>
    /// <exception cref="LimitExceededException">The font would be larger than the context's <see cref="SafetyLimits.MaxTotalBytes"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="webfont-unwrap"/>
    /// </example>
    public static byte[] ToSfnt(ReadOnlyMemory<byte> data, EgamiContext? context = null)
    {
        FontFileKind kind = SfntDirectory.Probe(data.Span).Kind;
        if (kind is not (FontFileKind.Woff or FontFileKind.Woff2))
        {
            throw new ArgumentException($"The data is not a WOFF or WOFF2 file ({(kind == FontFileKind.Unknown ? "not a font" : kind.ToString())}).", nameof(data));
        }

        context ??= EgamiContext.Default;
        WebFontDecoder.CheckLimit(data.Length, context.Limits.MaxTotalBytes);
        return WebFontDecoder.Decode(data, new FontLoadOptions { Context = context }).Data;
    }
}
