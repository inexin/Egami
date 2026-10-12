using Egami.Fonts.Parsing;

namespace Egami.Fonts.Cff;

/// <summary>
/// A Compact Font Format font (version 1): name-keyed or CID-keyed, with its charstrings, subroutines, charset and per-font private
/// data. Only the first font of a FontSet is read, as OpenType and PDF require.
/// </summary>
internal sealed class CffFont
{
    public const int OpCharStrings = 17;
    public const int OpCharset = 15;
    public const int OpEncoding = 16;
    public const int OpPrivate = 18;
    public const int OpSubrs = 19;
    public const int OpDefaultWidthX = 20;
    public const int OpNominalWidthX = 21;
    public const int OpFontBBox = 5;
    public const int OpCharstringType = 1206;
    public const int OpFontMatrix = 1207;
    public const int OpRos = 1230;
    public const int OpCidCount = 1234;
    public const int OpFdArray = 1236;
    public const int OpFdSelect = 1237;
    public const int OpFontName = 1238;

    private readonly ReadOnlyMemory<byte> _data;
    private readonly ushort[] _charset;
    private readonly byte[]? _fdSelect;
    private Dictionary<int, int>? _sidToGlyph;

    private CffFont(
        ReadOnlyMemory<byte> data,
        int headerSize,
        string name,
        CffDict top,
        CffIndex strings,
        CffIndex globalSubrs,
        CffIndex charStrings,
        ushort[] charset,
        PrivateData[] privates,
        byte[]? fdSelect,
        int nameIndexStart,
        int topIndexStart,
        int stringIndexStart,
        int globalSubrStart)
    {
        _data = data;
        HeaderSize = headerSize;
        Name = name;
        Top = top;
        Strings = strings;
        GlobalSubrs = globalSubrs;
        CharStrings = charStrings;
        _charset = charset;
        Privates = privates;
        _fdSelect = fdSelect;
        NameIndexStart = nameIndexStart;
        TopIndexStart = topIndexStart;
        StringIndexStart = stringIndexStart;
        GlobalSubrStart = globalSubrStart;
    }

    public ReadOnlyMemory<byte> Data => _data;

    public int HeaderSize { get; }

    public string Name { get; }

    public CffDict Top { get; }

    public CffIndex Strings { get; }

    public CffIndex GlobalSubrs { get; }

    public CffIndex CharStrings { get; }

    /// <summary>Gets the private data of each font dict: one for a name-keyed font, one per FDArray entry for a CID-keyed font.</summary>
    public PrivateData[] Privates { get; }

    public int NameIndexStart { get; }

    public int TopIndexStart { get; }

    public int StringIndexStart { get; }

    public int GlobalSubrStart { get; }

    public bool IsCidKeyed => Top.Contains(OpRos);

    public int GlyphCount => CharStrings.Count;

    /// <summary>Gets the charset: the SID (name-keyed) or CID (CID-keyed) of each glyph.</summary>
    public ReadOnlySpan<ushort> Charset => _charset;

    /// <summary>Gets the units per em the FontMatrix implies, or 1000 when it has none.</summary>
    public int UnitsPerEm
    {
        get
        {
            double[]? matrix = Top.Get(OpFontMatrix);
            if (matrix is { Length: 6 } && matrix[0] > 0)
            {
                double units = Math.Round(1 / matrix[0]);
                if (units is >= 16 and <= 16384)
                {
                    return (int)units;
                }
            }

            return 1000;
        }
    }

    /// <summary>Gets the font dict index of a glyph: 0 for a name-keyed font.</summary>
    public int FontDictOf(int glyph) => _fdSelect is null || (uint)glyph >= (uint)_fdSelect.Length ? 0 : _fdSelect[glyph];

    public ReadOnlySpan<byte> FdSelect => _fdSelect;

    /// <summary>Gets the glyph whose charset SID is <paramref name="sid"/> (name-keyed fonts), or -1.</summary>
    public int GlyphOfSid(int sid)
    {
        Dictionary<int, int> map = _sidToGlyph ??= BuildSidMap();
        return map.TryGetValue(sid, out int glyph) ? glyph : -1;
    }

    /// <summary>Gets a string by SID: the standard strings below 391, else the String INDEX.</summary>
    public string GetString(int sid)
    {
        if (sid < CffStandard.StandardStringCount)
        {
            return CffStandard.GetStandardString(sid);
        }

        int index = sid - CffStandard.StandardStringCount;
        return (uint)index < (uint)Strings.Count ? System.Text.Encoding.Latin1.GetString(Strings[index]) : string.Empty;
    }

    public static CffFont Read(ReadOnlyMemory<byte> data, long fileOffset)
    {
        ReadOnlySpan<byte> span = data.Span;
        if (span.Length < 4)
        {
            throw FontErrors.Truncated(fileOffset);
        }

        int major = span[0], headerSize = span[2];
        if (major != 1)
        {
            throw new UnsupportedFormatException(major == 2 ? "CFF2 outlines are read from Egami.Fonts 2.7.2 on." : $"CFF version {major} is not supported.");
        }

        if (headerSize < 4 || headerSize > span.Length)
        {
            throw FontErrors.Corrupt(fileOffset, "The CFF header size is wrong.");
        }

        int nameStart = headerSize;
        CffIndex names = CffIndex.Read(data, nameStart);
        int topStart = names.End;
        CffIndex tops = CffIndex.Read(data, topStart);
        int stringStart = tops.End;
        CffIndex strings = CffIndex.Read(data, stringStart);
        int globalStart = strings.End;
        CffIndex globals = CffIndex.Read(data, globalStart);
        if (tops.Count < 1)
        {
            throw FontErrors.Corrupt(fileOffset, "The CFF data has no Top DICT.");
        }

        string name = names.Count > 0 ? System.Text.Encoding.Latin1.GetString(names[0]) : string.Empty;
        CffDict top = CffDict.Read(tops[0], fileOffset + topStart);
        if (top.GetInt(OpCharstringType, 2) != 2)
        {
            throw new UnsupportedFormatException("CFF fonts with Type 1 charstrings are not supported.");
        }

        int charStringsOffset = top.GetInt(OpCharStrings, -1);
        if (charStringsOffset <= 0 || charStringsOffset >= span.Length)
        {
            throw FontErrors.Corrupt(fileOffset, "The CFF Top DICT has no CharStrings.");
        }

        CffIndex charStrings = CffIndex.Read(data, charStringsOffset);
        int glyphCount = charStrings.Count;
        if (glyphCount == 0)
        {
            throw FontErrors.Corrupt(fileOffset + charStringsOffset, "The CFF data has no charstrings.");
        }
        ushort[] charset = ReadCharset(span, top.GetInt(OpCharset, 0), glyphCount, fileOffset);

        PrivateData[] privates;
        byte[]? fdSelect = null;
        if (top.Contains(OpRos))
        {
            int fdArrayOffset = top.GetInt(OpFdArray, -1);
            if (fdArrayOffset <= 0 || fdArrayOffset >= span.Length)
            {
                throw FontErrors.Corrupt(fileOffset, "The CID-keyed CFF font has no FDArray.");
            }

            CffIndex fdArray = CffIndex.Read(data, fdArrayOffset);
            privates = new PrivateData[fdArray.Count];
            for (int i = 0; i < fdArray.Count; i++)
            {
                CffDict fd = CffDict.Read(fdArray[i], fileOffset + fdArrayOffset);
                privates[i] = PrivateData.Read(data, fd, fileOffset, fdArrayOffset, i);
            }

            if (privates.Length == 0)
            {
                throw FontErrors.Corrupt(fileOffset, "The CID-keyed CFF font has an empty FDArray.");
            }

            fdSelect = ReadFdSelect(span, top.GetInt(OpFdSelect, -1), glyphCount, privates.Length, fileOffset);
        }
        else
        {
            privates = [PrivateData.Read(data, top, fileOffset, -1, 0)];
        }

        return new CffFont(data, headerSize, name, top, strings, globals, charStrings, charset, privates, fdSelect, nameStart, topStart, stringStart, globalStart);
    }

    /// <summary>Gets the subroutine bias for an INDEX of <paramref name="count"/> subroutines.</summary>
    public static int Bias(int count) => count < 1240 ? 107 : count < 33900 ? 1131 : 32768;

    private static ushort[] ReadCharset(ReadOnlySpan<byte> data, int offset, int glyphCount, long fileOffset)
    {
        var charset = new ushort[glyphCount];
        if (offset is 0 or 1 or 2)
        {
            // Predefined: ISOAdobe maps glyph i to SID i; the expert sets are treated the same, which only affects seac lookups.
            for (int i = 0; i < glyphCount; i++)
            {
                charset[i] = (ushort)Math.Min(i, CffStandard.IsoAdobeLastSid);
            }

            return charset;
        }

        if (offset < 0 || offset >= data.Length)
        {
            throw FontErrors.Corrupt(fileOffset, "The CFF charset offset is outside the data.");
        }

        var r = new FontReader(data[offset..], fileOffset + offset);
        int format = r.ReadByte();
        int glyph = 1;
        switch (format)
        {
            case 0:
                while (glyph < glyphCount)
                {
                    charset[glyph++] = r.ReadUInt16();
                }

                break;
            case 1:
            case 2:
                while (glyph < glyphCount)
                {
                    int first = r.ReadUInt16();
                    int left = format == 1 ? r.ReadByte() : r.ReadUInt16();
                    for (int k = 0; k <= left && glyph < glyphCount; k++)
                    {
                        charset[glyph++] = (ushort)(first + k);
                    }
                }

                break;
            default:
                throw FontErrors.Corrupt(fileOffset + offset, $"The CFF charset has format {format}.");
        }

        return charset;
    }

    private static byte[] ReadFdSelect(ReadOnlySpan<byte> data, int offset, int glyphCount, int fdCount, long fileOffset)
    {
        if (offset <= 0 || offset >= data.Length)
        {
            throw FontErrors.Corrupt(fileOffset, "The CID-keyed CFF font has no FDSelect.");
        }

        var select = new byte[glyphCount];
        var r = new FontReader(data[offset..], fileOffset + offset);
        int format = r.ReadByte();
        if (format == 0)
        {
            for (int i = 0; i < glyphCount; i++)
            {
                select[i] = r.ReadByte();
            }
        }
        else if (format == 3)
        {
            int ranges = r.ReadUInt16();
            int first = r.ReadUInt16();
            for (int k = 0; k < ranges; k++)
            {
                int fd = r.ReadByte();
                int next = r.ReadUInt16();
                for (int g = first; g < next && g < glyphCount; g++)
                {
                    select[g] = (byte)fd;
                }

                first = next;
            }
        }
        else
        {
            throw FontErrors.Corrupt(fileOffset + offset, $"The CFF FDSelect has format {format}.");
        }

        for (int i = 0; i < glyphCount; i++)
        {
            if (select[i] >= fdCount)
            {
                throw FontErrors.Corrupt(fileOffset + offset, $"The CFF FDSelect names font dict {select[i]} of {fdCount}.");
            }
        }

        return select;
    }

    private Dictionary<int, int> BuildSidMap()
    {
        var map = new Dictionary<int, int>(_charset.Length);
        for (int g = _charset.Length - 1; g >= 0; g--)
        {
            map[_charset[g]] = g;
        }

        return map;
    }
}

/// <summary>A Private DICT's subroutines and width defaults.</summary>
internal sealed class PrivateData
{
    public static readonly PrivateData Empty = new();

    public CffDict Dict { get; init; } = new();

    public CffDict? FontDict { get; init; }

    public CffIndex? LocalSubrs { get; init; }

    public int PrivateOffset { get; init; } = -1;

    public int PrivateSize { get; init; }

    public int LocalSubrsOffset { get; init; } = -1;

    public double DefaultWidthX { get; init; }

    public double NominalWidthX { get; init; }

    public static PrivateData Read(ReadOnlyMemory<byte> data, CffDict owner, long fileOffset, int fdArrayOffset, int index)
    {
        double[]? entry = owner.Get(CffFont.OpPrivate);
        if (entry is not { Length: 2 })
        {
            return new PrivateData { FontDict = fdArrayOffset >= 0 ? owner : null };
        }

        int size = (int)entry[0], offset = (int)entry[1];
        if (size < 0 || offset < 0 || (long)offset + size > data.Length)
        {
            throw FontErrors.Corrupt(fileOffset, $"The CFF Private DICT of font dict {index} is outside the data.");
        }

        CffDict dict = CffDict.Read(data.Span.Slice(offset, size), fileOffset + offset);
        int subrs = dict.GetInt(CffFont.OpSubrs, 0);
        CffIndex? local = null;
        if (subrs > 0)
        {
            local = CffIndex.Read(data, offset + subrs);
        }

        return new PrivateData
        {
            Dict = dict,
            FontDict = fdArrayOffset >= 0 ? owner : null,
            LocalSubrs = local,
            PrivateOffset = offset,
            PrivateSize = size,
            LocalSubrsOffset = subrs > 0 ? offset + subrs : -1,
            DefaultWidthX = dict.GetNumber(CffFont.OpDefaultWidthX, 0),
            NominalWidthX = dict.GetNumber(CffFont.OpNominalWidthX, 0),
        };
    }
}
