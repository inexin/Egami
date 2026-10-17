using Egami.Fonts.Tables;

namespace Egami.Fonts;

/// <summary>A code and the glyph a <see cref="CharacterMap"/> gives it.</summary>
/// <param name="Code">The character code, in the map's own encoding.</param>
/// <param name="Glyph">The glyph id.</param>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="cmaps"/>
/// </example>
public readonly record struct CharacterMapping(int Code, ushort Glyph);

/// <summary>
/// One character map of a face, as stored: a cmap subtable for one (platform, encoding) pair, or a CFF font's own encoding as
/// FreeType presents it (platform 7). Codes are the subtable's own: bytes for a Macintosh (1,0) map or a symbol font's
/// (3,0) map, never translated to Unicode.
/// </summary>
/// <remarks>
/// The subtable is parsed on first use, once, and the map is safe to use from several threads. A subtable that cannot be read
/// maps nothing and reports a warning through the context's diagnostics.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="cmaps"/>
/// </example>
public sealed class CharacterMap
{
    /// <summary>The platform id of a CFF font's own encoding, as FreeType numbers it (TT_PLATFORM_ADOBE).</summary>
    public const int AdobePlatform = 7;

    private readonly Func<CmapSubtable?> _load;
    private readonly int _glyphCount;
    private CmapSubtable? _subtable;
    private int _loaded;

    internal CharacterMap(int platformId, int encodingId, int format, int language, int glyphCount, Func<CmapSubtable?> load)
    {
        _glyphCount = glyphCount;
        PlatformId = platformId;
        EncodingId = encodingId;
        Format = format;
        Language = language;
        _load = load;
    }

    /// <summary>Gets the platform id: 0 Unicode, 1 Macintosh, 3 Windows, 7 a CFF font's own encoding.</summary>
    public int PlatformId { get; }

    /// <summary>
    /// Gets the encoding id within the platform, such as 1 (Unicode BMP) or 0 (symbol) on Windows. For platform 7: 0 the Standard
    /// Encoding, 1 the Expert Encoding, 2 a custom encoding.
    /// </summary>
    public int EncodingId { get; }

    /// <summary>Gets the subtable format: 0, 2, 4, 6, 10, 12 or 13, or another the map cannot read (it maps nothing); 0 for platform 7.</summary>
    public int Format { get; }

    /// <summary>Gets the subtable's language field: nonzero only in some Macintosh subtables (the Mac language code plus one).</summary>
    public int Language { get; }

    /// <summary>Gets the glyph a code maps to.</summary>
    /// <param name="code">The character code, in this map's encoding.</param>
    /// <returns>The glyph id, 0 (.notdef) when the code is not mapped or maps past the face's glyphs (as FreeType's FT_Get_Char_Index).</returns>
    public ushort GetGlyph(int code)
    {
        int glyph = Subtable?.Map(code) ?? 0;
        return glyph < _glyphCount ? (ushort)glyph : (ushort)0;
    }

    /// <summary>Gets every code that maps to a glyph other than 0 within the face's glyphs, in ascending code order.</summary>
    /// <returns>The mappings.</returns>
    public IEnumerable<CharacterMapping> GetMappings()
    {
        if (Subtable is not CmapSubtable table)
        {
            return [];
        }

        var mappings = new List<CharacterMapping>();
        table.Enumerate((code, glyph) =>
        {
            if (glyph < _glyphCount)
            {
                mappings.Add(new CharacterMapping(code, (ushort)glyph));
            }
        });
        return mappings;
    }

    /// <summary>Returns the platform, encoding and format, such as "(3,1) format 4".</summary>
    /// <returns>The text.</returns>
    public override string ToString() => $"({PlatformId},{EncodingId}) format {Format}";

    private CmapSubtable? Subtable
    {
        get
        {
            if (Volatile.Read(ref _loaded) == 0)
            {
                lock (_load)
                {
                    if (_loaded == 0)
                    {
                        _subtable = _load();
                        Volatile.Write(ref _loaded, 1);
                    }
                }
            }

            return _subtable;
        }
    }
}
