using Egami.Fonts.Parsing;

namespace Egami.Fonts.Tables;

/// <summary>
/// The character to glyph map ('cmap'): the one Unicode subtable chosen by preference, the format 14 variation subtable, and the
/// symbol and Mac Roman translations.
/// </summary>
internal sealed class CmapTable
{
    public static readonly CmapTable Empty = new(null, CmapKind.Unicode, null, 0, 0);

    private readonly CmapSubtable? _primary;
    private readonly VariationSubtable? _variations;

    private readonly Func<int, int>? _symbolRemap;

    private CmapTable(CmapSubtable? primary, CmapKind kind, VariationSubtable? variations, int platform, int encoding, Func<int, int>? symbolRemap = null)
    {
        _primary = primary;
        Kind = kind;
        _variations = variations;
        PlatformId = platform;
        EncodingId = encoding;
        _symbolRemap = symbolRemap;
    }

    public CmapKind Kind { get; }

    public int PlatformId { get; }

    public int EncodingId { get; }

    public bool IsEmpty => _primary is null;

    /// <summary>Maps a Unicode code point to a glyph id, 0 when unmapped.</summary>
    public int Map(int codePoint)
    {
        if (_primary is null)
        {
            return 0;
        }

        switch (Kind)
        {
            case CmapKind.Symbol when _symbolRemap is not null:
                int mapped = _primary.Map(codePoint);
                if (mapped == 0 && _symbolRemap(codePoint) is int remapped and > 0)
                {
                    mapped = _primary.Map(remapped);
                }

                return mapped;
            case CmapKind.Symbol:
                int glyph = _primary.Map(codePoint);
                if (glyph == 0 && codePoint is >= 0 and <= 0xFF)
                {
                    glyph = _primary.Map(0xF000 + codePoint);
                }
                else if (glyph == 0 && codePoint is >= 0xF000 and <= 0xF0FF)
                {
                    glyph = _primary.Map(codePoint - 0xF000);
                }

                return glyph;
            case CmapKind.MacRoman:
                int b = MacRoman.FromCodePoint(codePoint);
                return b < 0 ? 0 : _primary.Map(b);
            default:
                return _primary.Map(codePoint);
        }
    }

    /// <summary>
    /// Maps a code point followed by a variation selector: the glyph the format 14 subtable names, the default glyph when it lists
    /// the pair as default, or -1 when the font says nothing about the pair.
    /// </summary>
    public int MapVariation(int codePoint, int selector)
    {
        if (_variations is null)
        {
            return -1;
        }

        return _variations.Map(codePoint, selector, out bool useDefault) is int glyph and >= 0 ? glyph : useDefault ? Map(codePoint) : -1;
    }

    /// <summary>Calls <paramref name="action"/> for every mapped code point in ascending order, translated to Unicode.</summary>
    public void Enumerate(Action<int, int> action)
    {
        if (_primary is null)
        {
            return;
        }

        if (Kind == CmapKind.MacRoman)
        {
            var pairs = new List<(int, int)>();
            _primary.Enumerate((code, glyph) =>
            {
                if (code <= 0xFF)
                {
                    pairs.Add((MacRoman.ToChar((byte)code), glyph));
                }
            });
            pairs.Sort();
            foreach ((int cp, int glyph) in pairs)
            {
                action(cp, glyph);
            }

            return;
        }

        _primary.Enumerate(action);
    }

    internal static CmapTable ReadChosen(ReadOnlySpan<byte> data, int subtableOffset, CmapKind kind, int platform, int encoding, Func<int, int>? symbolRemap)
    {
        var r = new FontReader(data, 0);
        r.Skip(2);
        int count = r.ReadUInt16();
        int variationOffset = -1;
        for (int i = 0; i < count; i++)
        {
            int p = r.ReadUInt16();
            int e = r.ReadUInt16();
            int offset = (int)Math.Min(r.ReadUInt32(), int.MaxValue);
            if (p == 0 && e == 5 && offset + 2 <= data.Length && BigEndian.UInt16(data, offset) == 14)
            {
                variationOffset = offset;
            }
        }

        CmapSubtable primary = CmapSubtable.Read(data, subtableOffset, 0);
        VariationSubtable? variations = variationOffset < 0 ? null : VariationSubtable.Read(data, variationOffset, 0);
        return new CmapTable(primary, kind, variations, platform, encoding, symbolRemap);
    }

    /// <summary>Reads the cmap, choosing (3,10), then (0,4) or (0,6), (3,1), (0,3), other Unicode, (3,0) symbol, then (1,0).</summary>
    public static CmapTable Read(ReadOnlySpan<byte> data, long offset)
    {
        var r = new FontReader(data, offset);
        r.Skip(2);
        int count = r.ReadUInt16();
        int bestRank = int.MaxValue, bestOffset = -1, bestPlatform = 0, bestEncoding = 0, variationOffset = -1;
        for (int i = 0; i < count; i++)
        {
            int platform = r.ReadUInt16();
            int encoding = r.ReadUInt16();
            int subtableOffset = (int)Math.Min(r.ReadUInt32(), int.MaxValue);
            if (subtableOffset + 2 > data.Length)
            {
                continue;
            }

            int format = BigEndian.UInt16(data, subtableOffset);
            if (format == 14)
            {
                if (platform == 0 && encoding == 5)
                {
                    variationOffset = subtableOffset;
                }

                continue;
            }

            int rank = (platform, encoding) switch
            {
                (3, 10) => 0,
                (0, 4) or (0, 6) => 1,
                (3, 1) => 2,
                (0, 3) => 3,
                (0, _) => 4,
                (3, 0) => 5,
                (1, 0) => 6,
                _ => int.MaxValue,
            };
            if (rank < bestRank && format is 0 or 4 or 6 or 10 or 12 or 13)
            {
                bestRank = rank;
                bestOffset = subtableOffset;
                bestPlatform = platform;
                bestEncoding = encoding;
            }
        }

        CmapSubtable? primary = bestOffset < 0 ? null : CmapSubtable.Read(data, bestOffset, offset);
        VariationSubtable? variations = variationOffset < 0 ? null : VariationSubtable.Read(data, variationOffset, offset);
        CmapKind kind = bestRank switch
        {
            5 => CmapKind.Symbol,
            6 => CmapKind.MacRoman,
            _ => CmapKind.Unicode,
        };
        return new CmapTable(primary, kind, variations, bestPlatform, bestEncoding);
    }
}

internal static class CmapForShaping
{
    /// <summary>
    /// The cmap as HarfBuzz reads it for shaping, which can differ from <see cref="CmapTable.Read"/>'s choice: a (3,0) symbol
    /// subtable first, then (3,10), (0,6), (0,4), (3,1), (0,3), (0,2), (0,1), (0,0) and Mac Roman; a symbol subtable also maps
    /// U+0000 to U+00FF to U+F000 to U+F0FF, or, in a Windows 3.1 Arabic font page, Unicode Arabic to the font's private use
    /// characters. Returns <paramref name="face"/>'s own cmap when the two choose the same subtable.
    /// </summary>
    public static CmapTable Read(ReadOnlySpan<byte> data, CmapTable face, int fontPage, Func<int, int> simplifiedArabic, Func<int, int> traditionalArabic)
    {
        if (data.Length < 4)
        {
            return face;
        }

        int count = BigEndian.UInt16(data, 2);
        int bestRank = int.MaxValue, bestOffset = -1, bestPlatform = 0, bestEncoding = 0;
        for (int i = 0; i < count && 4 + (i * 8) + 8 <= data.Length; i++)
        {
            int platform = BigEndian.UInt16(data, 4 + (i * 8));
            int encoding = BigEndian.UInt16(data, 6 + (i * 8));
            long subtableOffset = BigEndian.UInt32(data, 8 + (i * 8));
            if (subtableOffset + 2 > data.Length)
            {
                continue;
            }

            int format = BigEndian.UInt16(data, (int)subtableOffset);
            int rank = (platform, encoding) switch
            {
                (3, 0) => 0,
                (3, 10) => 1,
                (0, 6) => 2,
                (0, 4) => 3,
                (3, 1) => 4,
                (0, 3) => 5,
                (0, 2) => 6,
                (0, 1) => 7,
                (0, 0) => 8,
                (1, 0) => 9,
                _ => int.MaxValue,
            };
            if (rank < bestRank && format is 0 or 4 or 6 or 10 or 12 or 13)
            {
                bestRank = rank;
                bestOffset = (int)subtableOffset;
                bestPlatform = platform;
                bestEncoding = encoding;
            }
        }

        if (bestOffset < 0 || (bestRank != 0 && bestPlatform == face.PlatformId && bestEncoding == face.EncodingId))
        {
            return face;
        }

        Func<int, int>? remap = bestRank != 0 ? null : fontPage switch
        {
            0 => static u => u <= 0xFF ? 0xF000 + u : 0,
            0xB200 => simplifiedArabic,
            0xB300 => traditionalArabic,
            _ => static _ => 0,
        };
        return CmapTable.ReadChosen(data, bestOffset, bestRank switch { 0 => CmapKind.Symbol, 9 => CmapKind.MacRoman, _ => CmapKind.Unicode },
            bestPlatform, bestEncoding, remap);
    }
}

internal enum CmapKind
{
    Unicode,
    Symbol,
    MacRoman,
}

/// <summary>One cmap subtable, decoded into arrays so lookups never touch the bytes again.</summary>
internal abstract class CmapSubtable
{
    public abstract int Map(int code);

    public abstract void Enumerate(Action<int, int> action);

    public static CmapSubtable Read(ReadOnlySpan<byte> data, int offset, long tableOffset)
    {
        int format = BigEndian.UInt16(data, offset);
        var r = new FontReader(data[offset..], tableOffset + offset);
        r.Skip(2);
        switch (format)
        {
            case 0:
                {
                    r.Skip(4);
                    return new ByteSubtable(r.ReadBytes(256).ToArray());
                }

            case 4:
                return SegmentSubtable.Read(ref r);
            case 6:
                {
                    r.Skip(4);
                    int first = r.ReadUInt16();
                    int count = r.ReadUInt16();
                    return TrimmedSubtable.Read(ref r, first, count);
                }

            case 10:
                {
                    r.Skip(10);
                    int first = (int)Math.Min(r.ReadUInt32(), 0x10FFFF);
                    int count = (int)Math.Min(r.ReadUInt32(), (uint)r.Remaining / 2);
                    return TrimmedSubtable.Read(ref r, first, count);
                }

            default:
                {
                    // Formats 12 and 13: sequential or constant groups.
                    r.Skip(10);
                    int count = (int)Math.Min(r.ReadUInt32(), (uint)r.Remaining / 12);
                    return GroupSubtable.Read(ref r, count, format == 13);
                }
        }
    }
}

internal sealed class ByteSubtable(byte[] glyphs) : CmapSubtable
{
    public override int Map(int code) => (uint)code < 256 ? glyphs[code] : 0;

    public override void Enumerate(Action<int, int> action)
    {
        for (int i = 0; i < 256; i++)
        {
            if (glyphs[i] != 0)
            {
                action(i, glyphs[i]);
            }
        }
    }
}

internal sealed class TrimmedSubtable(int first, ushort[] glyphs) : CmapSubtable
{
    public static TrimmedSubtable Read(ref FontReader r, int first, int count)
    {
        var glyphs = new ushort[count];
        for (int i = 0; i < count; i++)
        {
            glyphs[i] = r.ReadUInt16();
        }

        return new TrimmedSubtable(first, glyphs);
    }

    public override int Map(int code)
    {
        int index = code - first;
        return (uint)index < (uint)glyphs.Length ? glyphs[index] : 0;
    }

    public override void Enumerate(Action<int, int> action)
    {
        for (int i = 0; i < glyphs.Length; i++)
        {
            if (glyphs[i] != 0)
            {
                action(first + i, glyphs[i]);
            }
        }
    }
}

/// <summary>Format 4: segments with deltas or glyph arrays, found by binary search over the end codes.</summary>
internal sealed class SegmentSubtable : CmapSubtable
{
    private readonly ushort[] _ends;
    private readonly ushort[] _starts;
    private readonly ushort[] _deltas;
    private readonly ushort[] _rangeOffsets;
    private readonly ushort[] _glyphArray;

    private SegmentSubtable(ushort[] ends, ushort[] starts, ushort[] deltas, ushort[] rangeOffsets, ushort[] glyphArray)
    {
        _ends = ends;
        _starts = starts;
        _deltas = deltas;
        _rangeOffsets = rangeOffsets;
        _glyphArray = glyphArray;
    }

    public static SegmentSubtable Read(ref FontReader r)
    {
        int length = r.ReadUInt16();
        r.Skip(2);
        int segments = r.ReadUInt16() / 2;
        r.Skip(6);
        var ends = new ushort[segments];
        var starts = new ushort[segments];
        var deltas = new ushort[segments];
        var rangeOffsets = new ushort[segments];
        for (int i = 0; i < segments; i++)
        {
            ends[i] = r.ReadUInt16();
        }

        r.Skip(2);
        for (int i = 0; i < segments; i++)
        {
            starts[i] = r.ReadUInt16();
        }

        for (int i = 0; i < segments; i++)
        {
            deltas[i] = r.ReadUInt16();
        }

        for (int i = 0; i < segments; i++)
        {
            rangeOffsets[i] = r.ReadUInt16();
        }

        // The glyph array runs to the subtable's length, or to the end of the data when the length is wrong (as in some old fonts).
        int arrayBytes = Math.Max(0, Math.Min(length - r.Position, r.Remaining));
        if (arrayBytes == 0 || length < r.Position)
        {
            arrayBytes = r.Remaining;
        }

        var glyphArray = new ushort[arrayBytes / 2];
        for (int i = 0; i < glyphArray.Length; i++)
        {
            glyphArray[i] = r.ReadUInt16();
        }

        return new SegmentSubtable(ends, starts, deltas, rangeOffsets, glyphArray);
    }

    public override int Map(int code)
    {
        if ((uint)code > 0xFFFF)
        {
            return 0;
        }

        int lo = 0, hi = _ends.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (_ends[mid] < code)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo < _ends.Length && code <= _ends[lo] ? MapInSegment(lo, code) : 0;
    }

    public override void Enumerate(Action<int, int> action)
    {
        int previous = -1;
        for (int s = 0; s < _ends.Length; s++)
        {
            for (int code = Math.Max(_starts[s], previous + 1); code <= _ends[s] && code < 0xFFFF; code++)
            {
                int glyph = MapInSegment(s, code);
                if (glyph != 0)
                {
                    action(code, glyph);
                }

                previous = code;
            }
        }
    }

    private int MapInSegment(int segment, int code)
    {
        if (code < _starts[segment])
        {
            return 0;
        }

        int rangeOffset = _rangeOffsets[segment];
        if (rangeOffset == 0)
        {
            return (code + _deltas[segment]) & 0xFFFF;
        }

        // The offset counts bytes from the segment's own idRangeOffset entry into the glyph array that follows the entries.
        int index = (rangeOffset / 2) + (code - _starts[segment]) - (_ends.Length - segment);
        if ((uint)index >= (uint)_glyphArray.Length)
        {
            return 0;
        }

        int glyph = _glyphArray[index];
        return glyph == 0 ? 0 : (glyph + _deltas[segment]) & 0xFFFF;
    }
}

/// <summary>Formats 12 (sequential groups) and 13 (constant groups).</summary>
internal sealed class GroupSubtable : CmapSubtable
{
    private readonly uint[] _starts;
    private readonly uint[] _ends;
    private readonly uint[] _glyphs;
    private readonly bool _constant;

    private GroupSubtable(uint[] starts, uint[] ends, uint[] glyphs, bool constant)
    {
        _starts = starts;
        _ends = ends;
        _glyphs = glyphs;
        _constant = constant;
    }

    public static GroupSubtable Read(ref FontReader r, int count, bool constant)
    {
        var starts = new uint[count];
        var ends = new uint[count];
        var glyphs = new uint[count];
        for (int i = 0; i < count; i++)
        {
            starts[i] = r.ReadUInt32();
            ends[i] = Math.Min(r.ReadUInt32(), 0x10FFFF);
            glyphs[i] = r.ReadUInt32();
        }

        return new GroupSubtable(starts, ends, glyphs, constant);
    }

    public override int Map(int code)
    {
        uint c = (uint)code;
        int lo = 0, hi = _ends.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (_ends[mid] < c)
            {
                lo = mid + 1;
            }
            else if (_starts[mid] > c)
            {
                hi = mid - 1;
            }
            else
            {
                long glyph = _constant ? _glyphs[mid] : _glyphs[mid] + (long)(c - _starts[mid]);
                return glyph > 0xFFFF ? 0 : (int)glyph;
            }
        }

        return 0;
    }

    public override void Enumerate(Action<int, int> action)
    {
        long previous = -1;
        for (int g = 0; g < _starts.Length; g++)
        {
            for (long c = Math.Max(_starts[g], previous + 1); c <= _ends[g]; c++)
            {
                long glyph = _constant ? _glyphs[g] : _glyphs[g] + (c - _starts[g]);
                if (glyph is > 0 and <= 0xFFFF)
                {
                    action((int)c, (int)glyph);
                }

                previous = c;
            }
        }
    }
}

/// <summary>Format 14: Unicode variation sequences.</summary>
internal sealed class VariationSubtable
{
    private readonly int[] _selectors;
    private readonly (int Start, int End)[][] _defaults;
    private readonly (int Code, int Glyph)[][] _mappings;

    private VariationSubtable(int[] selectors, (int, int)[][] defaults, (int, int)[][] mappings)
    {
        _selectors = selectors;
        _defaults = defaults;
        _mappings = mappings;
    }

    public static VariationSubtable Read(ReadOnlySpan<byte> data, int offset, long tableOffset)
    {
        var r = new FontReader(data[offset..], tableOffset + offset);
        r.Skip(6);
        int count = (int)Math.Min(r.ReadUInt32(), (uint)r.Remaining / 11);
        var selectors = new int[count];
        var defaults = new (int, int)[count][];
        var mappings = new (int, int)[count][];
        for (int i = 0; i < count; i++)
        {
            selectors[i] = (int)r.ReadUInt24();
            int defaultOffset = (int)Math.Min(r.ReadUInt32(), int.MaxValue);
            int mappingOffset = (int)Math.Min(r.ReadUInt32(), int.MaxValue);
            defaults[i] = [];
            mappings[i] = [];
            if (defaultOffset != 0)
            {
                var d = new FontReader(r.Data[defaultOffset..]);
                int n = (int)Math.Min(d.ReadUInt32(), (uint)d.Remaining / 4);
                defaults[i] = new (int, int)[n];
                for (int k = 0; k < n; k++)
                {
                    int start = (int)d.ReadUInt24();
                    defaults[i][k] = (start, start + d.ReadByte());
                }
            }

            if (mappingOffset != 0)
            {
                var m = new FontReader(r.Data[mappingOffset..]);
                int n = (int)Math.Min(m.ReadUInt32(), (uint)m.Remaining / 5);
                mappings[i] = new (int, int)[n];
                for (int k = 0; k < n; k++)
                {
                    mappings[i][k] = ((int)m.ReadUInt24(), m.ReadUInt16());
                }
            }
        }

        return new VariationSubtable(selectors, defaults, mappings);
    }

    /// <summary>Returns the glyph for the pair, or -1 with <paramref name="useDefault"/> telling whether the base glyph applies.</summary>
    public int Map(int code, int selector, out bool useDefault)
    {
        useDefault = false;
        int i = Array.IndexOf(_selectors, selector);
        if (i < 0)
        {
            return -1;
        }

        foreach ((int c, int glyph) in _mappings[i])
        {
            if (c == code)
            {
                return glyph;
            }
        }

        foreach ((int start, int end) in _defaults[i])
        {
            if (code >= start && code <= end)
            {
                useDefault = true;
                break;
            }
        }

        return -1;
    }
}
