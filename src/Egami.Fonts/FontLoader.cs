using Egami.Fonts.Parsing;

namespace Egami.Fonts;

/// <summary>Tells what font data holds and loads every face of a collection.</summary>
public static class FontLoader
{
    /// <summary>
    /// Says whether the bytes are a single TrueType or OpenType font, a collection (and how many faces) or a bare CFF stream, from
    /// the headers alone, without parsing glyphs.
    /// </summary>
    /// <param name="data">The start of the data; the first 4 KiB are enough for every header Egami reads.</param>
    /// <returns>What the data is.</returns>
    public static FontProbe Probe(ReadOnlySpan<byte> data) => SfntDirectory.Probe(data);

    /// <summary>Loads every face of a font file: one for a single font or CFF, all of them for a collection.</summary>
    /// <param name="path">The file.</param>
    /// <param name="options">How to load, or <see langword="null"/> for <see cref="FontLoadOptions.Default"/>.</param>
    /// <returns>The faces, sharing the file's memory.</returns>
    /// <exception cref="UnsupportedFormatException">The file is not a font Egami reads.</exception>
    /// <exception cref="CorruptDataException">A face has no readable glyph data, or is damaged and strict loading is set.</exception>
    /// <exception cref="LimitExceededException">The file is larger than the context's <see cref="SafetyLimits.MaxTotalBytes"/>.</exception>
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
    public static IReadOnlyList<Typeface> LoadAll(ReadOnlyMemory<byte> data, FontLoadOptions? options = null)
    {
        FontProbe probe = Probe(data.Span);
        int count = probe.Kind == FontFileKind.Collection ? probe.FaceCount : 1;
        var faces = new Typeface[count];
        for (int i = 0; i < count; i++)
        {
            faces[i] = Typeface.FromMemory(data, i, options);
        }

        return faces;
    }
}
