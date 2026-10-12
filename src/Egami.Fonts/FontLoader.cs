using Egami.Fonts.Parsing;

namespace Egami.Fonts;

/// <summary>Tells what font data holds and loads every face of a collection.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="load"/>
/// </example>
public static class FontLoader
{
    /// <summary>
    /// Says whether the bytes are a single TrueType or OpenType font, a collection (and how many faces), a WOFF or WOFF2 file (and,
    /// for a WOFF2 collection, how many faces) or a bare CFF stream, from the headers alone, without parsing glyphs or decompressing.
    /// </summary>
    /// <param name="data">The start of the data; the first 4 KiB are enough for every header Egami reads, except a WOFF2 file with
    /// a long table directory, whose face count and outlines need the whole directory (0 and null without it).</param>
    /// <returns>What the data is.</returns>
    public static FontProbe Probe(ReadOnlySpan<byte> data) => SfntDirectory.Probe(data);

    /// <summary>Loads every face of a font file: one for a single font or CFF, all of them for a collection.</summary>
    /// <param name="path">The file.</param>
    /// <param name="options">How to load, or <see langword="null"/> for <see cref="FontLoadOptions.Default"/>.</param>
    /// <returns>The faces, sharing the file's memory.</returns>
    /// <exception cref="UnsupportedFormatException">The file is not a font Egami reads.</exception>
    /// <exception cref="CorruptDataException">A face has no readable glyph data, or is damaged and strict loading is set.</exception>
    /// <exception cref="LimitExceededException">The file is larger than the context's <see cref="SafetyLimits.MaxTotalBytes"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="load"/>
    /// </example>
    public static IReadOnlyList<Typeface> LoadAll(string path, FontLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        options ??= FontLoadOptions.Default;
        return LoadAll(Typeface.ReadFile(path, options), options);
    }

    /// <summary>Loads every face of font data held in memory, which is kept and must not change afterwards.</summary>
    /// <param name="data">The font data.</param>
    /// <param name="options">How to load, or <see langword="null"/> for <see cref="FontLoadOptions.Default"/>.</param>
    /// <returns>The faces, sharing the memory.</returns>
    /// <exception cref="UnsupportedFormatException">The data is not a font Egami reads.</exception>
    /// <exception cref="CorruptDataException">A face has no readable glyph data, or is damaged and strict loading is set.</exception>
    /// <exception cref="LimitExceededException">The data is larger than the context's <see cref="SafetyLimits.MaxTotalBytes"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="load"/>
    /// </example>
    public static IReadOnlyList<Typeface> LoadAll(ReadOnlyMemory<byte> data, FontLoadOptions? options = null)
    {
        FontProbe probe = Probe(data.Span);
        if (probe.Kind is FontFileKind.Woff or FontFileKind.Woff2)
        {
            options ??= FontLoadOptions.Default;
            Woff.DecodedFont web = Woff.WebFontDecoder.Decode(data, options);
            var decoded = new Typeface[web.FaceCount];
            for (int i = 0; i < decoded.Length; i++)
            {
                decoded[i] = Typeface.FromDecoded(web, i, options);
            }

            return decoded;
        }

        int count = probe.Kind == FontFileKind.Collection ? probe.FaceCount : 1;
        var faces = new Typeface[count];
        for (int i = 0; i < count; i++)
        {
            faces[i] = Typeface.FromMemory(data, i, options);
        }

        return faces;
    }
}
