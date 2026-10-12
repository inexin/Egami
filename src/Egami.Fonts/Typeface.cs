using Egami.Fonts.Cff;
using Egami.Fonts.Layout;
using Egami.Fonts.Parsing;
using Egami.Fonts.Tables;

namespace Egami.Fonts;

/// <summary>
/// One font face: its names, style, metrics, character map, glyph outlines and kerning, read from a TrueType or OpenType font, a
/// collection, or a bare CFF stream. Immutable and safe to share across threads, canvases and backends.
/// </summary>
/// <remarks>
/// <para>
/// Loading reads the table directory, head and maxp, and checks that glyph data is there; every other table is parsed the first
/// time it is needed and kept. The font's bytes are held once, as the <see cref="ReadOnlyMemory{T}"/> given or read, and every table
/// is a slice of them. Memory given to <see cref="FromMemory(ReadOnlyMemory{byte}, int, FontLoadOptions?)"/> must not change
/// afterwards.
/// </para>
/// <para>
/// Loading is lenient by default: see <see cref="FontLoadOptions"/> for the defaults damaged or missing tables get and the
/// diagnostics they raise. All glyph metrics and outlines are in font units with y up; <see cref="Font"/> scales them to a size.
/// </para>
/// </remarks>
public sealed partial class Typeface
{
    private readonly ReadOnlyMemory<byte> _data;
    private readonly TableRecord[] _tables;
    private readonly FontLoadOptions _options;
    private readonly HeadTable _head;
    private readonly CffOptions? _cffOptions;
    private NameTable? _name;
    private Os2Table? _os2;
    private bool _os2Missing;
    private Box<HheaTable?>? _hhea;
    private PostTable? _post;
    private StyleAndNames? _style;

    private Typeface(ReadOnlyMemory<byte> data, int faceIndex, int faceOffset, TableRecord[] tables, uint sfntVersion, FontLoadOptions options)
    {
        _data = data;
        FaceIndex = faceIndex;
        FaceOffset = faceOffset;
        _tables = tables;
        SfntVersion = sfntVersion;
        _options = options;

        _head = LoadHead();
        Outlines = HasTable(Tags.Cff) && !HasTable(Tags.Glyf) ? FontOutlines.Cff : FontOutlines.TrueType;
        if (Outlines == FontOutlines.TrueType && !HasTable(Tags.Glyf))
        {
            throw HasTable(Tags.Cff2)
                ? new UnsupportedFormatException("CFF2 outlines are read from Egami.Fonts 2.7.2 on.")
                : FontErrors.Corrupt(faceOffset, "The font has no glyph data: neither glyf nor CFF is present.");
        }

        GlyphCount = LoadGlyphData();
        UnitsPerEm = _head.UnitsPerEm;
    }

    private Typeface(ReadOnlyMemory<byte> data, CffFont cff, CffOptions options)
    {
        _data = data;
        FaceIndex = 0;
        _tables = [];
        _options = options.LoadOptions;
        _cffOptions = options;
        _cff = cff;
        Outlines = FontOutlines.Cff;
        GlyphCount = cff.GlyphCount;
        int unitsPerEm = options.UnitsPerEm ?? cff.UnitsPerEm;
        if (unitsPerEm is < 16 or > 16384)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "UnitsPerEm must be from 16 to 16384.");
        }

        UnitsPerEm = unitsPerEm;
        double[]? box = cff.Top.Get(CffFont.OpFontBBox);
        _head = box is { Length: 4 }
            ? new HeadTable { UnitsPerEm = (ushort)unitsPerEm, XMin = Clamp16(box[0]), YMin = Clamp16(box[1]), XMax = Clamp16(box[2]), YMax = Clamp16(box[3]) }
            : new HeadTable { UnitsPerEm = (ushort)unitsPerEm };
        _cmap = CmapTable.Empty;
        _name = NameTable.Empty;
        IsBareCff = true;
    }

    /// <summary>Gets the face's index in its collection, 0 for a single font.</summary>
    public int FaceIndex { get; }

    /// <summary>Gets the typographic family name in English (name ID 16, else 1), such as "Noto Sans"; "Unknown" when the font has none.</summary>
    public string FamilyName => StyleInfo.Family;

    /// <summary>Gets the typographic subfamily name in English (name ID 17, else 2), such as "Bold Italic"; "Regular" when the font has none.</summary>
    public string SubfamilyName => StyleInfo.Subfamily;

    /// <summary>Gets the full name in English (name ID 4), such as "Noto Sans Bold Italic".</summary>
    public string FullName => StyleInfo.FullName;

    /// <summary>Gets the PostScript name (name ID 6, or the CFF font name), such as "NotoSans-BoldItalic", which PDF uses as BaseFont.</summary>
    public string PostScriptName => StyleInfo.PostScriptName;

    /// <summary>Gets the weight, width and slant the face declares in OS/2 (or head and post when OS/2 is missing).</summary>
    public FontStyle Style => StyleInfo.Style;

    /// <summary>Gets the font units per em, 16 to 16384.</summary>
    public int UnitsPerEm { get; }

    /// <summary>Gets the number of glyphs.</summary>
    public int GlyphCount { get; }

    /// <summary>Gets how the face stores its outlines.</summary>
    public FontOutlines Outlines { get; }

    /// <summary>Gets the embedding permissions the face declares (OS/2 fsType). Egami never enforces them.</summary>
    public FontEmbedding Embedding => (FontEmbedding)(Os2.FsType & 0x030E);

    /// <summary>Gets whether every glyph has the same advance (post isFixedPitch, or the CFF isFixedPitch entry).</summary>
    public bool IsFixedPitch => IsBareCff ? _cff!.Top.GetNumber(1201, 0) != 0 : Post.IsFixedPitch != 0;

    internal bool IsBareCff { get; }

    internal int FaceOffset { get; }

    internal uint SfntVersion { get; }

    internal ReadOnlyMemory<byte> FileData => _data;

    internal FontLoadOptions Options => _options;

    internal HeadTable Head => _head;

    internal IReadOnlyList<TableRecord> Tables => _tables;

    /// <summary>Loads one face from a font file, reading the whole file into memory and closing it.</summary>
    /// <param name="path">The file: .ttf, .otf, .ttc, .otc or a bare CFF stream.</param>
    /// <param name="index">The face to load from a collection; 0 for a single font.</param>
    /// <param name="options">How to load, or <see langword="null"/> for <see cref="FontLoadOptions.Default"/>.</param>
    /// <returns>The face.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not a face of the file.</exception>
    /// <exception cref="UnsupportedFormatException">The file is not a font Egami reads.</exception>
    /// <exception cref="CorruptDataException">The font has no readable glyph data, or is damaged and <see cref="FontLoadOptions.Strict"/> is set.</exception>
    /// <exception cref="LimitExceededException">The file is larger than the context's <see cref="SafetyLimits.MaxTotalBytes"/>.</exception>
    public static Typeface FromFile(string path, int index = 0, FontLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        options ??= FontLoadOptions.Default;
        return FromMemory(ReadFile(path, options), index, options);
    }

    /// <summary>Loads one face from a stream, reading it to the end.</summary>
    /// <param name="stream">The stream; it is read but not closed.</param>
    /// <param name="index">The face to load from a collection; 0 for a single font.</param>
    /// <param name="options">How to load, or <see langword="null"/> for <see cref="FontLoadOptions.Default"/>.</param>
    /// <returns>The face.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not a face of the data.</exception>
    /// <exception cref="UnsupportedFormatException">The data is not a font Egami reads.</exception>
    /// <exception cref="CorruptDataException">The font has no readable glyph data, or is damaged and <see cref="FontLoadOptions.Strict"/> is set.</exception>
    /// <exception cref="LimitExceededException">The data is larger than the context's <see cref="SafetyLimits.MaxTotalBytes"/>.</exception>
    public static Typeface FromStream(Stream stream, int index = 0, FontLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= FontLoadOptions.Default;
        return FromMemory(ReadStream(stream, options), index, options);
    }

    /// <summary>Loads one face from memory, which is kept, not copied, and must not change afterwards.</summary>
    /// <param name="data">The font data: an sfnt, a collection or a bare CFF stream.</param>
    /// <param name="index">The face to load from a collection; 0 for a single font.</param>
    /// <param name="options">How to load, or <see langword="null"/> for <see cref="FontLoadOptions.Default"/>.</param>
    /// <returns>The face.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not a face of the data.</exception>
    /// <exception cref="UnsupportedFormatException">The data is not a font Egami reads.</exception>
    /// <exception cref="CorruptDataException">The font has no readable glyph data, or is damaged and <see cref="FontLoadOptions.Strict"/> is set.</exception>
    /// <exception cref="LimitExceededException">The data is larger than the context's <see cref="SafetyLimits.MaxTotalBytes"/>.</exception>
    public static Typeface FromMemory(ReadOnlyMemory<byte> data, int index = 0, FontLoadOptions? options = null)
    {
        options ??= FontLoadOptions.Default;
        CheckSize(data.Length, options);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        FontProbe probe = SfntDirectory.Probe(data.Span);
        switch (probe.Kind)
        {
            case FontFileKind.Cff:
                if (index != 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(index), "A CFF stream holds one face.");
                }

                return FromCff(data, CffOptions.Default with { LoadOptions = options });
            case FontFileKind.Woff:
            case FontFileKind.Woff2:
                throw new UnsupportedFormatException("WOFF and WOFF2 fonts are read from Egami.Fonts 2.7.2 on.");
            case FontFileKind.Unknown:
                throw new UnsupportedFormatException("The data is not a TrueType, OpenType or CFF font.");
        }

        ReadOnlySpan<byte> span = data.Span;
        int faceOffset = SfntDirectory.FaceOffset(span, index, out _);
        List<string>? problems = null;
        TableRecord[] tables = SfntDirectory.Read(span, faceOffset, out uint version, problem => (problems ??= []).Add(problem));
        if (problems is not null)
        {
            Report(options, "Font.BadDirectory", problems[0], faceOffset);
        }

        return new Typeface(data, index, faceOffset, tables, version, options);
    }

    /// <summary>
    /// Loads a bare CFF stream as PDF embeds it (FontFile3 with subtype Type1C or CIDFontType0C). The face has no character map:
    /// glyphs are reached by id, or by CID through the charset for a CID-keyed font.
    /// </summary>
    /// <param name="data">The CFF data, kept and not copied.</param>
    /// <param name="options">Units per em and widths the CFF lacks, or <see langword="null"/> for <see cref="CffOptions.Default"/>.</param>
    /// <returns>The face.</returns>
    /// <exception cref="UnsupportedFormatException">The data is CFF2 or uses Type 1 charstrings.</exception>
    /// <exception cref="CorruptDataException">The CFF structures cannot be read.</exception>
    /// <exception cref="LimitExceededException">The data is larger than the context's <see cref="SafetyLimits.MaxTotalBytes"/>.</exception>
    public static Typeface FromCff(ReadOnlyMemory<byte> data, CffOptions? options = null)
    {
        options ??= CffOptions.Default;
        CheckSize(data.Length, options.LoadOptions);
        CffFont cff = CffFont.Read(data, 0);
        return new Typeface(data, cff, options);
    }

    /// <summary>Gets every decoded string of the naming table, in every language present.</summary>
    /// <returns>The records, in table order; empty when the font has no name table.</returns>
    public IReadOnlyList<FontNameRecord> GetNames() => Name.Records;

    /// <summary>Gets a table's bytes as stored in the font, without parsing them.</summary>
    /// <param name="tag">The table tag, such as <c>new FontTag("GSUB")</c>.</param>
    /// <param name="data">The table's bytes, a slice of the font's memory.</param>
    /// <returns><see langword="true"/> if the font has the table.</returns>
    public bool TryGetTable(FontTag tag, out ReadOnlyMemory<byte> data)
    {
        foreach (TableRecord record in _tables)
        {
            if (record.Tag == tag)
            {
                data = _data.Slice(record.Offset, record.Length);
                return true;
            }
        }

        data = default;
        return false;
    }

    /// <summary>Returns the full name, such as "Noto Sans Bold".</summary>
    /// <returns>The text.</returns>
    public override string ToString() => FullName;

    internal bool HasTable(FontTag tag)
    {
        foreach (TableRecord record in _tables)
        {
            if (record.Tag == tag)
            {
                return true;
            }
        }

        return false;
    }

    internal ReadOnlySpan<byte> GetTableSpan(FontTag tag, out int offset)
    {
        foreach (TableRecord record in _tables)
        {
            if (record.Tag == tag)
            {
                offset = record.Offset;
                return _data.Span.Slice(record.Offset, record.Length);
            }
        }

        offset = -1;
        return default;
    }

    internal NameTable Name => Volatile.Read(ref _name) ?? Publish(ref _name, LoadTable(Tags.Name, NameTable.Read, NameTable.Empty, "the family is \"Unknown\""));

    internal Os2Table Os2
    {
        get
        {
            Os2Table? os2 = Volatile.Read(ref _os2);
            if (os2 is not null)
            {
                return os2;
            }

            if (IsBareCff)
            {
                return Publish(ref _os2, new Os2Table());
            }

            ReadOnlySpan<byte> data = GetTableSpan(Tags.OS2, out int offset);
            if (offset < 0)
            {
                Repair("Font.MissingTable", "The OS/2 table is missing; weight 400, width 5 and no embedding restrictions are assumed.", FaceOffset);
                _os2Missing = true;
                return Publish(ref _os2, new Os2Table());
            }

            return Publish(ref _os2, Os2Table.Read(data));
        }
    }

    internal bool Os2Missing
    {
        get
        {
            _ = Os2;
            return _os2Missing || IsBareCff;
        }
    }

    internal HheaTable? Hhea
    {
        get
        {
            if (IsBareCff)
            {
                return null;
            }

            Box<HheaTable?>? box = Volatile.Read(ref _hhea);
            return (box ?? Publish(ref _hhea, new Box<HheaTable?>(LoadTable<HheaTable?>(Tags.Hhea, HheaTable.Read, null, "advances and line metrics come from other tables")))).Value;
        }
    }

    internal PostTable Post => Volatile.Read(ref _post) ?? Publish(ref _post, IsBareCff ? new PostTable() : LoadTable(Tags.Post, PostTable.Read, new PostTable(), "the italic angle is 0 and the underline is derived from the em"));

    private StyleAndNames StyleInfo => Volatile.Read(ref _style) ?? Publish(ref _style, StyleAndNames.Compute(this));

    internal static T Publish<T>(ref T? field, T value)
        where T : class
    {
        return Interlocked.CompareExchange(ref field, value, null) ?? value;
    }

    /// <summary>Repairs a problem: throws in strict mode, else reports a Warning diagnostic.</summary>
    internal void Repair(string id, string message, long offset)
    {
        if (_options.Strict)
        {
            throw FontErrors.Corrupt(offset, message);
        }

        Report(_options, id, message, offset);
    }

    internal static void Report(FontLoadOptions options, string id, string message, long offset)
    {
        if (options.Strict)
        {
            throw FontErrors.Corrupt(offset, message);
        }

        EgamiContext context = options.EffectiveContext;
        if (context.Diagnostics is null || context.DiagnosticLevel > DiagnosticLevel.Warning)
        {
            return;
        }

        using DiagnosticReporter reporter = context.StartDiagnostics();
        reporter.Report(DiagnosticLevel.Warning, id, message, offset >= 0 ? offset : null);
    }

    private T LoadTable<T>(FontTag tag, Func<ReadOnlySpan<byte>, long, T> parse, T fallback, string defaultText)
    {
        ReadOnlySpan<byte> data = GetTableSpan(tag, out int offset);
        if (offset < 0)
        {
            Repair("Font.MissingTable", $"The {tag} table is missing; {defaultText}.", FaceOffset);
            return fallback;
        }

        try
        {
            return parse(data, offset);
        }
        catch (CorruptDataException e) when (!_options.Strict)
        {
            Repair("Font.BadTable", $"The {tag} table is damaged ({e.Message}); {defaultText}.", offset);
            return fallback;
        }
    }

    private HeadTable LoadHead()
    {
        ReadOnlySpan<byte> data = GetTableSpan(Tags.Head, out int offset);
        HeadTable head;
        if (offset < 0)
        {
            Repair("Font.MissingTable", "The head table is missing; 1000 units per em are assumed.", FaceOffset);
            head = new HeadTable { UnitsPerEm = 1000, IndexToLocFormat = -1 };
        }
        else
        {
            try
            {
                head = HeadTable.Read(data, offset);
            }
            catch (CorruptDataException e) when (!_options.Strict)
            {
                Repair("Font.BadTable", $"The head table is damaged ({e.Message}); 1000 units per em are assumed.", offset);
                head = new HeadTable { UnitsPerEm = 1000, IndexToLocFormat = -1 };
            }
        }

        if (head.UnitsPerEm is < 16 or > 16384)
        {
            Repair("Font.BadTable", $"The head table gives {head.UnitsPerEm} units per em; 1000 are assumed.", offset);
            head = new HeadTable
            {
                UnitsPerEm = 1000,
                XMin = head.XMin,
                YMin = head.YMin,
                XMax = head.XMax,
                YMax = head.YMax,
                MacStyle = head.MacStyle,
                IndexToLocFormat = head.IndexToLocFormat,
                Flags = head.Flags,
            };
        }

        return head;
    }

    private int LoadGlyphData()
    {
        int declared = -1;
        ReadOnlySpan<byte> maxp = GetTableSpan(Tags.Maxp, out int maxpOffset);
        if (maxpOffset < 0)
        {
            Repair("Font.MissingTable", "The maxp table is missing; the glyph count comes from the glyph data.", FaceOffset);
        }
        else
        {
            try
            {
                _maxp = MaxpTable.Read(maxp, maxpOffset);
                declared = _maxp.NumGlyphs;
            }
            catch (CorruptDataException e) when (!_options.Strict)
            {
                Repair("Font.BadTable", $"The maxp table is damaged ({e.Message}); the glyph count comes from the glyph data.", maxpOffset);
            }
        }

        if (Outlines == FontOutlines.Cff)
        {
            TryGetTable(Tags.Cff, out ReadOnlyMemory<byte> cffData);
            int cffOffset = 0;
            GetTableSpan(Tags.Cff, out cffOffset);
            _cff = CffFont.Read(cffData, cffOffset);
            int count = _cff.GlyphCount;
            if (declared >= 0 && declared != count)
            {
                Repair("Font.BadTable", $"maxp gives {declared} glyphs but the CFF data has {count}; {Math.Min(declared, count)} are used.", maxpOffset);
                count = Math.Min(declared, count);
            }

            return count;
        }

        TryGetTable(Tags.Glyf, out ReadOnlyMemory<byte> glyf);
        GetTableSpan(Tags.Glyf, out int glyfOffset);
        ReadOnlySpan<byte> loca = GetTableSpan(Tags.Loca, out int locaOffset);
        if (locaOffset < 0)
        {
            throw FontErrors.Corrupt(FaceOffset, "The font has a glyf table but no loca table, so no glyph can be found.");
        }

        bool longFormat = _head.IndexToLocFormat switch
        {
            0 => false,
            1 => true,
            _ => declared >= 0 ? loca.Length >= (declared + 1) * 4 : loca.Length / 2 - 1 > 0xFFFF,
        };
        int available = (longFormat ? loca.Length / 4 : loca.Length / 2) - 1;
        int glyphs = declared >= 0 ? declared : Math.Min(available, 0xFFFF);
        if (available < glyphs)
        {
            Repair("Font.BadTable", $"The loca table has entries for {Math.Max(0, available)} glyphs of {glyphs}; the rest are left out.", locaOffset);
            glyphs = Math.Max(0, available);
        }

        if (glyphs <= 0)
        {
            throw FontErrors.Corrupt(locaOffset, "The font has no glyphs.");
        }

        _glyf = new GlyfTable(glyf, GlyfTable.ReadLoca(loca, glyphs, longFormat, locaOffset), glyfOffset);
        return glyphs;
    }

    private static short Clamp16(double value) => (short)Math.Clamp(Math.Round(value), short.MinValue, short.MaxValue);

    private static void CheckSize(long length, FontLoadOptions options)
    {
        long limit = options.EffectiveContext.Limits.MaxTotalBytes;
        if (length > limit)
        {
            throw new LimitExceededException(nameof(SafetyLimits.MaxTotalBytes), limit, length);
        }
    }

    internal static byte[] ReadFile(string path, FontLoadOptions options)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
        CheckSize(stream.Length, options);
        byte[] data = new byte[stream.Length];
        stream.ReadExactly(data);
        return data;
    }

    private static byte[] ReadStream(Stream stream, FontLoadOptions options)
    {
        long limit = options.EffectiveContext.Limits.MaxTotalBytes;
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[81920];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > limit)
            {
                throw new LimitExceededException(nameof(SafetyLimits.MaxTotalBytes), limit, buffer.Length);
            }
        }

        return buffer.ToArray();
    }

    /// <summary>The names and style, worked out once from name, OS/2, head and post (or the CFF Top DICT).</summary>
    private sealed record StyleAndNames(string Family, string Subfamily, string FullName, string PostScriptName, FontStyle Style)
    {
        public static StyleAndNames Compute(Typeface face)
        {
            NameTable name = face.Name;
            string? cffName = face._cff?.Name;
            string family = name.GetEnglish(16) ?? name.GetEnglish(1) ?? (face.IsBareCff ? StripSubsetTag(cffName) : null) ?? "Unknown";
            string subfamily = name.GetEnglish(17) ?? name.GetEnglish(2) ?? "Regular";
            string postScript = name.GetEnglish(6) ?? cffName ?? family.Replace(" ", string.Empty, StringComparison.Ordinal);
            string full = name.GetEnglish(4) ?? (subfamily == "Regular" ? family : $"{family} {subfamily}");

            Os2Table os2 = face.Os2;
            int weight = os2.WeightClass;
            if (weight is >= 1 and <= 9)
            {
                // Some old fonts give the class as 1 to 9.
                weight *= 100;
            }

            bool noOs2 = face.Os2Missing;
            if (noOs2 || weight == 0)
            {
                weight = (face._head.MacStyle & 1) != 0 ? 700 : 400;
            }

            int width = noOs2 || os2.WidthClass == 0 ? 5 : Math.Clamp((int)os2.WidthClass, 1, 9);
            FontSlant slant = FontSlant.Upright;
            if (!noOs2 && (os2.FsSelection & 0x0200) != 0)
            {
                slant = FontSlant.Oblique;
            }
            else if ((!noOs2 && (os2.FsSelection & 0x0001) != 0) || (face._head.MacStyle & 2) != 0)
            {
                slant = FontSlant.Italic;
            }

            return new StyleAndNames(family, subfamily, full, postScript, new FontStyle(Math.Clamp(weight, 1, 1000), width, slant));
        }

        // A PDF subset name such as ABCDEF+Garamond has a six-letter tag before the plus.
        private static string? StripSubsetTag(string? name)
        {
            if (name is { Length: > 7 } && name[6] == '+')
            {
                return name[7..];
            }

            return string.IsNullOrEmpty(name) ? null : name;
        }
    }
}

/// <summary>Holds a value that may be null, so a lazily computed null is published once.</summary>
internal sealed record Box<T>(T Value);
