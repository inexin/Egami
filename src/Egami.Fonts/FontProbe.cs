namespace Egami.Fonts;

/// <summary>What kind of font data some bytes hold, as <see cref="FontLoader.Probe(ReadOnlySpan{byte})"/> sees it.</summary>
public enum FontFileKind
{
    /// <summary>Not font data Egami recognizes.</summary>
    Unknown,

    /// <summary>A single TrueType or OpenType font (.ttf, .otf).</summary>
    Sfnt,

    /// <summary>A TrueType or OpenType collection (.ttc, .otc) of several faces.</summary>
    Collection,

    /// <summary>A bare Compact Font Format stream, as PDF embeds it in FontFile3.</summary>
    Cff,

    /// <summary>A WOFF file, which Egami reads from 2.7.2 on.</summary>
    Woff,

    /// <summary>A WOFF2 file, which Egami reads from 2.7.2 on.</summary>
    Woff2,
}

/// <summary>The result of <see cref="FontLoader.Probe(ReadOnlySpan{byte})"/>.</summary>
/// <param name="Kind">What the data is.</param>
/// <param name="FaceCount">The number of faces: 1 for a single font or CFF, the collection's count, 0 when unknown.</param>
/// <param name="Outlines">How the first face stores outlines, when that can be told from the header.</param>
public readonly record struct FontProbe(FontFileKind Kind, int FaceCount, FontOutlines? Outlines);
