using System.Collections.ObjectModel;

using Egami.Fonts.Cff;
using Egami.Fonts.Tables;

namespace Egami.Fonts;

// Phase 2.7.3: the font data PDF reaches glyphs by, as FreeType gives it: glyph names, CIDs, every cmap subtable and a CFF
// font's own encoding. Everything is read on first use, once per face (a variable font's instances share their font's).
public sealed partial class Typeface
{
    private Box<string?[]?>? _glyphNames;
    private Dictionary<string, ushort>? _glyphsByName;
    private Dictionary<int, ushort>? _glyphsByCid;
    private Box<CidSystemInfo?>? _cidSystemInfo;
    private ReadOnlyCollection<CharacterMap>? _characterMaps;
    private Box<CharacterMap?>? _builtInEncoding;

    /// <summary>
    /// Gets the character collection of a CID-keyed CFF font (bare, or the CFF table of an OpenType font), such as
    /// Adobe-Identity-0; null for every other font.
    /// </summary>
    public CidSystemInfo? CidSystemInfo => (Volatile.Read(ref Root._cidSystemInfo) ?? Publish(ref Root._cidSystemInfo, new Box<CidSystemInfo?>(Root.ReadCidSystemInfo()))).Value;

    /// <summary>
    /// Gets every cmap subtable of the face as stored, in table order, but the format 14 variation sequences (which
    /// <see cref="GetGlyph(int, int)"/> uses); empty for a bare CFF font or a face without a cmap table.
    /// </summary>
    /// <remarks>
    /// <see cref="GetGlyph(int)"/> keeps choosing its one Unicode subtable as before; these maps are for callers that need a
    /// particular one, such as PDF's (3,0) or (1,0) for a symbolic TrueType font and (3,1) otherwise.
    /// </remarks>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="cmaps"/>
    /// </example>
    public IReadOnlyList<CharacterMap> CharacterMaps => Volatile.Read(ref Root._characterMaps) ?? Publish(ref Root._characterMaps, Root.ReadCharacterMaps());

    /// <summary>
    /// Gets a name-keyed CFF font's own encoding (bare, or the CFF table of an OpenType font) as FreeType presents it: a
    /// <see cref="CharacterMap"/> with platform 7 and encoding 0 (the Standard Encoding), 1 (the Expert Encoding) or 2 (a custom
    /// encoding) that maps character codes 0 to 255 to glyphs. Null for CID-keyed, CFF2 and TrueType fonts.
    /// </summary>
    /// <remarks>PDF uses it for a Type1C font whose font dictionary gives no /Encoding.</remarks>
    public CharacterMap? BuiltInEncoding => (Volatile.Read(ref Root._builtInEncoding) ?? Publish(ref Root._builtInEncoding, new Box<CharacterMap?>(Root.ReadBuiltInEncoding()))).Value;

    /// <summary>Finds the first character map with a platform and encoding, as stored (without translating codes).</summary>
    /// <param name="platformId">The platform: 0 Unicode, 1 Macintosh, 3 Windows; 7 gives <see cref="BuiltInEncoding"/>.</param>
    /// <param name="encodingId">The encoding within the platform, such as 1 for (3,1) or 0 for (1,0).</param>
    /// <returns>The map, or null when the face has none for that pair.</returns>
    public CharacterMap? FindCharacterMap(int platformId, int encodingId)
    {
        if (platformId == CharacterMap.AdobePlatform)
        {
            return BuiltInEncoding is CharacterMap own && own.EncodingId == encodingId ? own : null;
        }

        foreach (CharacterMap map in CharacterMaps)
        {
            if (map.PlatformId == platformId && map.EncodingId == encodingId)
            {
                return map;
            }
        }

        return null;
    }

    /// <summary>
    /// Gets a glyph's name: from the charset of a name-keyed CFF font (bare, or the CFF table of an OpenType font), else from the
    /// post table (versions 1, 2 and 2.5), as FreeType's FT_Get_Glyph_Name gives it.
    /// </summary>
    /// <param name="glyph">The glyph id.</param>
    /// <returns>
    /// The name, such as "Aacute"; null for a glyph past <see cref="GlyphCount"/>, for a font without names (a CID-keyed CFF font,
    /// post versions 3 and 4) or for a damaged entry, which also reports a warning.
    /// </returns>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="names"/>
    /// </example>
    public string? GetGlyphName(ushort glyph)
    {
        string?[]? names = GlyphNames;
        return names is not null && glyph < names.Length ? names[glyph] : null;
    }

    /// <summary>Finds the glyph with a name; when several share it, the lowest glyph id, as FreeType's FT_Get_Name_Index does.</summary>
    /// <param name="name">The glyph name, compared ordinally.</param>
    /// <param name="glyph">The glyph id, or 0 when no glyph has the name.</param>
    /// <returns><see langword="true"/> if a glyph has the name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    public bool TryGetGlyphByName(string name, out ushort glyph)
    {
        ArgumentNullException.ThrowIfNull(name);
        Dictionary<string, ushort> map = Volatile.Read(ref Root._glyphsByName) ?? Publish(ref Root._glyphsByName, BuildNameMap(GlyphNames));
        return map.TryGetValue(name, out glyph);
    }

    /// <summary>Gets the CID of a glyph of a CID-keyed CFF font (its charset entry), as FreeType's FT_Get_CID_From_Glyph_Index does.</summary>
    /// <param name="glyph">The glyph id.</param>
    /// <returns>The CID; -1 for a font that is not CID-keyed or a glyph past <see cref="GlyphCount"/>.</returns>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="cids"/>
    /// </example>
    public int GetCid(ushort glyph)
    {
        CffFont? cff = Root._cff;
        return cff is { IsCidKeyed: true, IsCff2: false } && glyph < cff.Charset.Length ? cff.Charset[glyph] : -1;
    }

    /// <summary>Finds the glyph with a CID in a CID-keyed CFF font; when several share it, the lowest glyph id.</summary>
    /// <param name="cid">The CID.</param>
    /// <param name="glyph">The glyph id, or 0 when no glyph has the CID.</param>
    /// <returns><see langword="true"/> if a glyph has the CID; always <see langword="false"/> for a font that is not CID-keyed.</returns>
    public bool TryGetGlyphByCid(int cid, out ushort glyph)
    {
        Dictionary<int, ushort> map = Volatile.Read(ref Root._glyphsByCid) ?? Publish(ref Root._glyphsByCid, Root.BuildCidMap());
        return map.TryGetValue(cid, out glyph);
    }

    private string?[]? GlyphNames => (Volatile.Read(ref Root._glyphNames) ?? Publish(ref Root._glyphNames, new Box<string?[]?>(Root.ReadGlyphNames()))).Value;

    private static Dictionary<string, ushort> BuildNameMap(string?[]? names)
    {
        var map = new Dictionary<string, ushort>(names?.Length ?? 0, StringComparer.Ordinal);
        for (int g = (names?.Length ?? 0) - 1; g >= 0; g--)
        {
            if (names![g] is string name)
            {
                map[name] = (ushort)g;
            }
        }

        return map;
    }

    private Dictionary<int, ushort> BuildCidMap()
    {
        var map = new Dictionary<int, ushort>();
        if (_cff is { IsCidKeyed: true, IsCff2: false } cff)
        {
            ReadOnlySpan<ushort> charset = cff.Charset;
            for (int g = charset.Length - 1; g >= 0; g--)
            {
                map[charset[g]] = (ushort)g;
            }
        }

        return map;
    }

    private string?[]? ReadGlyphNames()
    {
        if (_cff is { IsCff2: false } cff)
        {
            if (cff.IsCidKeyed)
            {
                return null;
            }

            var names = new string?[GlyphCount];
            bool damaged = false;
            for (int g = 0; g < names.Length && g < cff.Charset.Length; g++)
            {
                names[g] = cff.TryGetString(cff.Charset[g]);
                damaged |= names[g] is null;
            }

            if (damaged)
            {
                Repair("Font.BadTable", "The CFF charset names strings the font does not have or cannot read; those glyphs have no name.", FaceOffset);
            }

            return names;
        }

        if (IsBareCff)
        {
            return null;
        }

        ReadOnlySpan<byte> post = GetTableSpan(Tags.Post, out int offset);
        if (offset < 0)
        {
            return null;
        }

        try
        {
            string?[]? names = PostNames.Read(post, offset, GlyphCount, out bool damaged);
            if (damaged)
            {
                Repair("Font.BadTable", "The post table's glyph names run past the table; the glyphs it cannot name have no name.", offset);
            }

            return names;
        }
        catch (CorruptDataException e) when (!_options.Strict)
        {
            Repair("Font.BadTable", $"The post table is damaged ({e.Message}); glyphs have no names.", offset);
            return null;
        }
    }

    private CidSystemInfo? ReadCidSystemInfo()
    {
        if (_cff is not { IsCidKeyed: true, IsCff2: false } cff || cff.Top.Get(CffFont.OpRos) is not { Length: 3 } ros)
        {
            return null;
        }

        string? registry = cff.TryGetString((int)ros[0]), ordering = cff.TryGetString((int)ros[1]);
        if (registry is null || ordering is null)
        {
            Repair("Font.BadTable", "The CFF ROS names strings the font does not have or cannot read; the CID system info is unknown.", FaceOffset);
            return null;
        }

        return new CidSystemInfo(registry, ordering, (int)ros[2]);
    }

    private ReadOnlyCollection<CharacterMap> ReadCharacterMaps()
    {
        if (IsBareCff || !TryGetTable(Tags.Cmap, out ReadOnlyMemory<byte> table))
        {
            return ReadOnlyCollection<CharacterMap>.Empty;
        }

        long tableOffset = TableOffset(Tags.Cmap);
        List<CmapRecord> records;
        try
        {
            records = CmapRecords.Read(table.Span, tableOffset);
        }
        catch (CorruptDataException e) when (!_options.Strict)
        {
            Repair("Font.BadTable", $"The cmap table is damaged ({e.Message}); it lists no character maps.", tableOffset);
            return ReadOnlyCollection<CharacterMap>.Empty;
        }

        var maps = new CharacterMap[records.Count];
        for (int i = 0; i < maps.Length; i++)
        {
            CmapRecord record = records[i];
            maps[i] = new CharacterMap(record.PlatformId, record.EncodingId, Math.Max(record.Format, 0), record.Language, GlyphCount, () => LoadSubtable(table, tableOffset, record));
        }

        return Array.AsReadOnly(maps);
    }

    private CmapSubtable? LoadSubtable(ReadOnlyMemory<byte> table, long tableOffset, CmapRecord record)
    {
        if (record.Offset < 0)
        {
            Repair("Font.BadTable", $"The ({record.PlatformId},{record.EncodingId}) cmap subtable lies outside the table; it maps nothing.", tableOffset);
            return null;
        }

        try
        {
            return CmapSubtable.Read(table.Span, record.Offset, tableOffset);
        }
        catch (CorruptDataException e)
        {
            Repair("Font.BadTable", $"The ({record.PlatformId},{record.EncodingId}) cmap subtable cannot be read ({e.Message}); it maps nothing.", tableOffset + record.Offset);
            return null;
        }
    }

    private CharacterMap? ReadBuiltInEncoding()
    {
        if (_cff is not { IsCidKeyed: false, IsCff2: false } cff)
        {
            return null;
        }

        // Read at once (256 codes): the Standard or Expert Encoding naming no glyph of the font gives no map, as FreeType adds no
        // charmap then; a custom encoding always gives one.
        int kind = Math.Clamp(cff.Top.GetInt(CffFont.OpEncoding, 0), 0, 2);
        TrimmedSubtable? table;
        try
        {
            table = cff.ReadEncoding() is (_, ushort[] glyphs) ? new TrimmedSubtable(0, glyphs) : null;
        }
        catch (CorruptDataException e)
        {
            Repair("Font.BadTable", $"The CFF encoding cannot be read ({e.Message}); the font's own encoding maps nothing.", FaceOffset);
            table = null;
        }

        if (kind < 2 && table is not null && !table.Any())
        {
            return null;
        }

        return new CharacterMap(CharacterMap.AdobePlatform, kind, 0, 0, GlyphCount, () => table);
    }
}
