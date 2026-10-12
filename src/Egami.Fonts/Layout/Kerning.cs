using Egami.Fonts.Parsing;

namespace Egami.Fonts.Layout;

/// <summary>
/// Pair kerning: GPOS lookup type 2 (formats 1 and 2, extension lookups included) under the kern feature of the DFLT and latn
/// scripts' default language systems, else the kern table's format 0 subtables. Only the first glyph's x advance is adjusted.
/// </summary>
internal sealed class Kerning
{
    public static readonly Kerning None = new([], null);

    private readonly PairLookup[] _lookups;
    private readonly KernPairs? _kern;

    private Kerning(PairLookup[] lookups, KernPairs? kern)
    {
        _lookups = lookups;
        _kern = kern;
    }

    public bool IsEmpty => _lookups.Length == 0 && _kern is null;

    /// <summary>Gets whether the adjustments come from GPOS rather than the kern table.</summary>
    public bool FromGpos => _lookups.Length > 0;

    public int Get(int left, int right)
    {
        if (_lookups.Length > 0)
        {
            int sum = 0;
            foreach (PairLookup lookup in _lookups)
            {
                sum += lookup.Get(left, right);
            }

            return sum;
        }

        return _kern?.Get(left, right) ?? 0;
    }

    public static Kerning Read(ReadOnlySpan<byte> gpos, long gposOffset, ReadOnlySpan<byte> kern, long kernOffset)
    {
        PairLookup[] lookups = gpos.IsEmpty ? [] : ReadGpos(gpos, gposOffset);
        if (lookups.Length > 0)
        {
            return new Kerning(lookups, null);
        }

        KernPairs? pairs = kern.IsEmpty ? null : KernPairs.Read(kern, kernOffset);
        return pairs is null ? None : new Kerning([], pairs);
    }

    private static PairLookup[] ReadGpos(ReadOnlySpan<byte> gpos, long offset)
    {
        var r = new FontReader(gpos, offset);
        int major = r.ReadUInt16();
        r.Skip(2);
        if (major != 1)
        {
            return [];
        }

        int scriptList = r.ReadUInt16();
        int featureList = r.ReadUInt16();
        int lookupList = r.ReadUInt16();

        // Feature indices of the default language systems of DFLT and latn.
        var features = new SortedSet<int>();
        int scripts = BigEndian.UInt16(gpos, scriptList);
        for (int s = 0; s < scripts; s++)
        {
            int record = scriptList + 2 + (s * 6);
            var tag = new FontTag(BigEndian.UInt32(gpos, record));
            if (tag != Tags.Dflt && tag != Tags.Latn)
            {
                continue;
            }

            int script = scriptList + BigEndian.UInt16(gpos, record + 4);
            int defaultLangSys = BigEndian.UInt16(gpos, script);
            if (defaultLangSys == 0)
            {
                continue;
            }

            int langSys = script + defaultLangSys;
            int required = BigEndian.UInt16(gpos, langSys + 2);
            if (required != 0xFFFF)
            {
                features.Add(required);
            }

            int count = BigEndian.UInt16(gpos, langSys + 4);
            for (int f = 0; f < count; f++)
            {
                features.Add(BigEndian.UInt16(gpos, langSys + 6 + (f * 2)));
            }
        }

        // Lookups of those features tagged kern.
        var lookupIndices = new SortedSet<int>();
        int featureCount = BigEndian.UInt16(gpos, featureList);
        foreach (int f in features)
        {
            if (f >= featureCount)
            {
                continue;
            }

            int record = featureList + 2 + (f * 6);
            if (new FontTag(BigEndian.UInt32(gpos, record)) != Tags.KernFeature)
            {
                continue;
            }

            int feature = featureList + BigEndian.UInt16(gpos, record + 4);
            int count = BigEndian.UInt16(gpos, feature + 2);
            for (int k = 0; k < count; k++)
            {
                lookupIndices.Add(BigEndian.UInt16(gpos, feature + 4 + (k * 2)));
            }
        }

        var lookups = new List<PairLookup>();
        int lookupCount = BigEndian.UInt16(gpos, lookupList);
        foreach (int index in lookupIndices)
        {
            if (index >= lookupCount)
            {
                continue;
            }

            int lookup = lookupList + BigEndian.UInt16(gpos, lookupList + 2 + (index * 2));
            int type = BigEndian.UInt16(gpos, lookup);
            int subtableCount = BigEndian.UInt16(gpos, lookup + 4);
            var subtables = new List<PairSubtable>();
            for (int k = 0; k < subtableCount; k++)
            {
                int subtable = lookup + BigEndian.UInt16(gpos, lookup + 6 + (k * 2));
                int subtableType = type;
                if (type == 9)
                {
                    subtableType = BigEndian.UInt16(gpos, subtable + 2);
                    subtable += (int)BigEndian.UInt32(gpos, subtable + 4);
                }

                if (subtableType == 2 && PairSubtable.Read(gpos, subtable) is PairSubtable pair)
                {
                    subtables.Add(pair);
                }
            }

            if (subtables.Count > 0)
            {
                lookups.Add(new PairLookup([.. subtables]));
            }
        }

        return [.. lookups];
    }
}

/// <summary>One GPOS lookup: its pair subtables, tried in order until one applies.</summary>
internal sealed class PairLookup(PairSubtable[] subtables)
{
    public int Get(int left, int right)
    {
        foreach (PairSubtable subtable in subtables)
        {
            if (subtable.TryGet(left, right, out int value))
            {
                return value;
            }
        }

        return 0;
    }
}

/// <summary>A GPOS pair adjustment subtable, format 1 (glyph pairs) or 2 (class pairs).</summary>
internal sealed class PairSubtable
{
    private readonly Coverage _coverage;
    private readonly int _format;

    // Format 1: per covered glyph, sorted second glyphs and their x advances.
    private readonly ushort[][]? _seconds;
    private readonly short[][]? _values;

    // Format 2: class definitions and the class1 x class2 matrix of x advances.
    private readonly ClassDef? _classes1;
    private readonly ClassDef? _classes2;
    private readonly short[]? _matrix;
    private readonly int _class2Count;

    private PairSubtable(Coverage coverage, ushort[][] seconds, short[][] values)
    {
        _format = 1;
        _coverage = coverage;
        _seconds = seconds;
        _values = values;
    }

    private PairSubtable(Coverage coverage, ClassDef classes1, ClassDef classes2, short[] matrix, int class2Count)
    {
        _format = 2;
        _coverage = coverage;
        _classes1 = classes1;
        _classes2 = classes2;
        _matrix = matrix;
        _class2Count = class2Count;
    }

    public bool TryGet(int left, int right, out int value)
    {
        value = 0;
        int coverageIndex = _coverage.IndexOf(left);
        if (coverageIndex < 0)
        {
            return false;
        }

        if (_format == 1)
        {
            if (coverageIndex >= _seconds!.Length)
            {
                return false;
            }

            int found = Array.BinarySearch(_seconds[coverageIndex], (ushort)right);
            if (found < 0)
            {
                return false;
            }

            value = _values![coverageIndex][found];
            return true;
        }

        int class1 = _classes1!.Get(left), class2 = _classes2!.Get(right);
        if (class2 >= _class2Count)
        {
            return false;
        }

        long index = ((long)class1 * _class2Count) + class2;
        if (index >= _matrix!.Length)
        {
            return false;
        }

        value = _matrix[index];
        return true;
    }

    public static PairSubtable? Read(ReadOnlySpan<byte> data, int offset)
    {
        int format = BigEndian.UInt16(data, offset);
        var coverage = Coverage.Read(data, offset + BigEndian.UInt16(data, offset + 2));
        int format1 = BigEndian.UInt16(data, offset + 4);
        int format2 = BigEndian.UInt16(data, offset + 6);
        int size1 = ValueSize(format1), size2 = ValueSize(format2);
        int advanceAt = AdvanceOffset(format1);
        if (format == 1)
        {
            int setCount = BigEndian.UInt16(data, offset + 8);
            var seconds = new ushort[setCount][];
            var values = new short[setCount][];
            for (int s = 0; s < setCount; s++)
            {
                int set = offset + BigEndian.UInt16(data, offset + 10 + (s * 2));
                int pairs = BigEndian.UInt16(data, set);
                int recordSize = 2 + size1 + size2;
                seconds[s] = new ushort[pairs];
                values[s] = new short[pairs];
                for (int p = 0; p < pairs; p++)
                {
                    int rec = set + 2 + (p * recordSize);
                    seconds[s][p] = BigEndian.UInt16(data, rec);
                    values[s][p] = advanceAt < 0 ? (short)0 : BigEndian.Int16(data, rec + 2 + advanceAt);
                }

                if (!IsSorted(seconds[s]))
                {
                    Array.Sort(seconds[s], values[s]);
                }
            }

            return new PairSubtable(coverage, seconds, values);
        }

        if (format == 2)
        {
            var classes1 = ClassDef.Read(data, offset + BigEndian.UInt16(data, offset + 8));
            var classes2 = ClassDef.Read(data, offset + BigEndian.UInt16(data, offset + 10));
            int class1Count = BigEndian.UInt16(data, offset + 12);
            int class2Count = BigEndian.UInt16(data, offset + 14);
            int recordSize = size1 + size2;
            long cells = (long)class1Count * class2Count;
            if (cells > (data.Length - offset) / Math.Max(1, recordSize) + 1)
            {
                throw FontErrors.Corrupt(offset, "A GPOS class pair table is larger than its data.");
            }

            var matrix = new short[cells];
            for (long i = 0; i < cells; i++)
            {
                int rec = offset + 16 + (int)(i * recordSize);
                matrix[i] = advanceAt < 0 ? (short)0 : BigEndian.Int16(data, rec + advanceAt);
            }

            return new PairSubtable(coverage, classes1, classes2, matrix, class2Count);
        }

        return null;
    }

    private static bool IsSorted(ushort[] values)
    {
        for (int i = 1; i < values.Length; i++)
        {
            if (values[i] < values[i - 1])
            {
                return false;
            }
        }

        return true;
    }

    // A ValueRecord holds one int16 per set bit of the low byte of its format.
    private static int ValueSize(int format) => 2 * System.Numerics.BitOperations.PopCount((uint)(format & 0xFF));

    // XAdvance is bit 2; it follows XPlacement and YPlacement when those are present.
    private static int AdvanceOffset(int format)
    {
        if ((format & 0x4) == 0)
        {
            return -1;
        }

        return 2 * System.Numerics.BitOperations.PopCount((uint)(format & 0x3));
    }
}

/// <summary>An OpenType coverage table, format 1 (sorted glyphs) or 2 (ranges).</summary>
internal sealed class Coverage
{
    private readonly ushort[] _starts;
    private readonly ushort[] _ends;
    private readonly int[] _indices;

    private Coverage(ushort[] starts, ushort[] ends, int[] indices)
    {
        _starts = starts;
        _ends = ends;
        _indices = indices;
    }

    public int IndexOf(int glyph)
    {
        int lo = 0, hi = _starts.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (_ends[mid] < glyph)
            {
                lo = mid + 1;
            }
            else if (_starts[mid] > glyph)
            {
                hi = mid - 1;
            }
            else
            {
                return _indices[mid] + (glyph - _starts[mid]);
            }
        }

        return -1;
    }

    public static Coverage Read(ReadOnlySpan<byte> data, int offset)
    {
        int format = BigEndian.UInt16(data, offset);
        int count = BigEndian.UInt16(data, offset + 2);
        if (format == 1)
        {
            var glyphs = new ushort[count];
            var indices = new int[count];
            for (int i = 0; i < count; i++)
            {
                glyphs[i] = BigEndian.UInt16(data, offset + 4 + (i * 2));
                indices[i] = i;
            }

            Array.Sort(glyphs, indices);
            return new Coverage(glyphs, (ushort[])glyphs.Clone(), indices);
        }

        if (format == 2)
        {
            var starts = new ushort[count];
            var ends = new ushort[count];
            var indices = new int[count];
            for (int i = 0; i < count; i++)
            {
                int rec = offset + 4 + (i * 6);
                starts[i] = BigEndian.UInt16(data, rec);
                ends[i] = BigEndian.UInt16(data, rec + 2);
                indices[i] = BigEndian.UInt16(data, rec + 4);
            }

            return new Coverage(starts, ends, indices);
        }

        return new Coverage([], [], []);
    }
}

/// <summary>An OpenType class definition table, format 1 or 2; glyphs not listed are class 0.</summary>
internal sealed class ClassDef
{
    private readonly ushort[] _starts;
    private readonly ushort[] _ends;
    private readonly ushort[] _classes;
    private readonly int _first;
    private readonly ushort[]? _array;

    private ClassDef(int first, ushort[] array)
    {
        _first = first;
        _array = array;
        _starts = _ends = _classes = [];
    }

    private ClassDef(ushort[] starts, ushort[] ends, ushort[] classes)
    {
        _starts = starts;
        _ends = ends;
        _classes = classes;
    }

    public int Get(int glyph)
    {
        if (_array is not null)
        {
            int i = glyph - _first;
            return (uint)i < (uint)_array.Length ? _array[i] : 0;
        }

        int lo = 0, hi = _starts.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (_ends[mid] < glyph)
            {
                lo = mid + 1;
            }
            else if (_starts[mid] > glyph)
            {
                hi = mid - 1;
            }
            else
            {
                return _classes[mid];
            }
        }

        return 0;
    }

    public static ClassDef Read(ReadOnlySpan<byte> data, int offset)
    {
        int format = BigEndian.UInt16(data, offset);
        if (format == 1)
        {
            int first = BigEndian.UInt16(data, offset + 2);
            int count = BigEndian.UInt16(data, offset + 4);
            var array = new ushort[count];
            for (int i = 0; i < count; i++)
            {
                array[i] = BigEndian.UInt16(data, offset + 6 + (i * 2));
            }

            return new ClassDef(first, array);
        }

        if (format == 2)
        {
            int count = BigEndian.UInt16(data, offset + 2);
            var starts = new ushort[count];
            var ends = new ushort[count];
            var classes = new ushort[count];
            for (int i = 0; i < count; i++)
            {
                int rec = offset + 4 + (i * 6);
                starts[i] = BigEndian.UInt16(data, rec);
                ends[i] = BigEndian.UInt16(data, rec + 2);
                classes[i] = BigEndian.UInt16(data, rec + 4);
            }

            return new ClassDef(starts, ends, classes);
        }

        return new ClassDef(0, []);
    }
}

/// <summary>The kern table's horizontal format 0 subtables (Microsoft version 0 and Apple version 1 headers).</summary>
internal sealed class KernPairs
{
    private readonly Dictionary<uint, int> _pairs;

    private KernPairs(Dictionary<uint, int> pairs)
    {
        _pairs = pairs;
    }

    public int Count => _pairs.Count;

    public int Get(int left, int right) => _pairs.TryGetValue(((uint)left << 16) | (uint)right, out int v) ? v : 0;

    public IEnumerable<KeyValuePair<uint, int>> Pairs => _pairs;

    public static KernPairs? Read(ReadOnlySpan<byte> data, long offset)
    {
        var pairs = new Dictionary<uint, int>();
        int version = BigEndian.UInt16(data, 0);
        bool apple = version == 1;
        int count = apple ? (int)Math.Min(BigEndian.UInt32(data, 4), 1024) : BigEndian.UInt16(data, 2);
        int at = apple ? 8 : 4;
        for (int t = 0; t < count && at + 6 <= data.Length; t++)
        {
            int length, format;
            bool horizontal, crossStream, overrideValues;
            int header;
            if (apple)
            {
                length = (int)Math.Min(BigEndian.UInt32(data, at), int.MaxValue);
                int coverage = BigEndian.UInt16(data, at + 4);
                format = coverage & 0xFF;
                horizontal = (coverage & 0x8000) == 0;
                crossStream = (coverage & 0x4000) != 0;
                overrideValues = false;
                header = 8;
            }
            else
            {
                length = BigEndian.UInt16(data, at + 2);
                int coverage = BigEndian.UInt16(data, at + 4);
                format = coverage >> 8;
                horizontal = (coverage & 1) != 0;
                crossStream = (coverage & 4) != 0;
                overrideValues = (coverage & 8) != 0;
                header = 6;
            }

            if (format == 0 && horizontal && !crossStream)
            {
                int body = at + header;
                int n = BigEndian.UInt16(data, body);
                for (int p = 0; p < n; p++)
                {
                    int rec = body + 8 + (p * 6);
                    if (rec + 6 > data.Length)
                    {
                        break;
                    }

                    uint key = ((uint)BigEndian.UInt16(data, rec) << 16) | BigEndian.UInt16(data, rec + 2);
                    int value = BigEndian.Int16(data, rec + 4);
                    pairs[key] = overrideValues || !pairs.TryGetValue(key, out int old) ? value : old + value;
                }
            }

            // Old Microsoft fonts may give a 16-bit length that wrapped; step by the pair count instead.
            if (!apple && format == 0)
            {
                int n = BigEndian.UInt16(data, at + header);
                at += header + 8 + (n * 6);
            }
            else
            {
                if (length <= 0)
                {
                    break;
                }

                at += length;
            }
        }

        _ = offset;
        return pairs.Count == 0 ? null : new KernPairs(pairs);
    }
}
