using static Egami.Fonts.Shaping.OtData;

namespace Egami.Fonts.Subsetting;

/// <summary>The kept glyphs of a subset: old id to new id, and the kept old ids in order for range queries.</summary>
internal sealed class GlyphRemap
{
    private readonly Dictionary<ushort, ushort> _map;
    private readonly int[] _kept;

    public GlyphRemap(Dictionary<ushort, ushort> map)
    {
        _map = map;
        _kept = [.. map.Keys.Select(k => (int)k).Order()];
    }

    public bool Has(int old) => (uint)old <= ushort.MaxValue && _map.ContainsKey((ushort)old);

    public bool TryMap(int old, out int now)
    {
        if ((uint)old <= ushort.MaxValue && _map.TryGetValue((ushort)old, out ushort mapped))
        {
            now = mapped;
            return true;
        }

        now = -1;
        return false;
    }

    public int Map(int old) => _map[(ushort)old];

    /// <summary>The kept old ids from <paramref name="first"/> to <paramref name="last"/>, ascending.</summary>
    public ReadOnlySpan<int> InRange(int first, int last)
    {
        if (last < first)
        {
            return [];
        }

        int lo = LowerBound(first);
        int hi = LowerBound(last + 1);
        return _kept.AsSpan(lo, hi - lo);
    }

    private int LowerBound(int value)
    {
        int lo = 0, hi = _kept.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_kept[mid] < value)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }
}

/// <summary>
/// The classes of a class definition that a subset keeps, renumbered densely in their old order (class 0 first); classes at or
/// past a limit (a PairPos matrix's or a mark array's class count) all map to one class past the kept ones, so they stay out of
/// range as before.
/// </summary>
internal sealed class ClassRemap
{
    private readonly Dictionary<int, int> _map = [];
    private readonly int _limit;

    /// <summary>Initializes a new instance of the <see cref="ClassRemap"/> class.</summary>
    /// <param name="present">The classes kept glyphs have.</param>
    /// <param name="limit">The first class that is out of range, or <see cref="int.MaxValue"/>.</param>
    /// <param name="keepZero">Whether class 0 is kept whether or not a glyph names it (it holds every unlisted glyph).</param>
    public ClassRemap(IEnumerable<int> present, int limit = int.MaxValue, bool keepZero = true)
    {
        _limit = limit;
        if (keepZero)
        {
            _map[0] = 0;
        }

        foreach (int c in present.Where(c => c >= (keepZero ? 1 : 0) && c < limit).Distinct().Order())
        {
            _map[c] = _map.Count;
        }
    }

    /// <summary>Gets the number of kept classes, class 0 included.</summary>
    public int Count => _map.Count;

    /// <summary>The new class, or -1 for a class no kept glyph has.</summary>
    public int Map(int old) => old >= _limit ? Count : _map.TryGetValue(old, out int now) ? now : -1;

    /// <summary>The old class of each new one below <see cref="Count"/>.</summary>
    public int[] OldClasses()
    {
        int[] old = new int[Count];
        foreach ((int o, int n) in _map)
        {
            old[n] = o;
        }

        return old;
    }
}

/// <summary>Reads coverages and class definitions for a subset and writes them back in their smallest format.</summary>
internal static class LayoutCommon
{
    /// <summary>
    /// The kept glyphs a coverage table covers, ascending, with the coverage index the shaper finds for each (its binary search,
    /// so a damaged table keeps the shaper's reading).
    /// </summary>
    public static List<(int Old, int Index)> Covered(ReadOnlySpan<byte> d, int t, GlyphRemap glyphs)
    {
        List<(int, int)> result = [];
        if (t == 0)
        {
            return result;
        }

        var candidates = new SortedSet<int>();
        int format = U16(d, t);
        int count = U16(d, t + 2);
        if (format == 1)
        {
            for (int i = 0; i < count; i++)
            {
                int g = U16(d, t + 4 + (i * 2));
                if (glyphs.Has(g))
                {
                    candidates.Add(g);
                }
            }
        }
        else if (format == 2)
        {
            for (int i = 0; i < count; i++)
            {
                int r = t + 4 + (i * 6);
                foreach (int g in glyphs.InRange(U16(d, r), U16(d, r + 2)))
                {
                    candidates.Add(g);
                }
            }
        }

        foreach (int g in candidates)
        {
            int index = Coverage(d, t, (uint)g);
            if (index >= 0)
            {
                result.Add((g, index));
            }
        }

        return result;
    }

    /// <summary>The kept glyphs a class definition gives a class other than 0, ascending, with the class the shaper reads.</summary>
    public static List<(int Old, int Class)> Classed(ReadOnlySpan<byte> d, int t, GlyphRemap glyphs)
    {
        List<(int, int)> result = [];
        if (t == 0)
        {
            return result;
        }

        var candidates = new SortedSet<int>();
        int format = U16(d, t);
        if (format == 1)
        {
            int start = U16(d, t + 2);
            int count = U16(d, t + 4);
            foreach (int g in glyphs.InRange(start, Math.Min(start + count - 1, ushort.MaxValue)))
            {
                candidates.Add(g);
            }
        }
        else if (format == 2)
        {
            int count = U16(d, t + 2);
            for (int i = 0; i < count; i++)
            {
                int r = t + 4 + (i * 6);
                foreach (int g in glyphs.InRange(U16(d, r), U16(d, r + 2)))
                {
                    candidates.Add(g);
                }
            }
        }

        foreach (int g in candidates)
        {
            int klass = ClassOf(d, t, (uint)g);
            if (klass != 0)
            {
                result.Add((g, klass));
            }
        }

        return result;
    }

    /// <summary>The class of every kept glyph a class definition lists, for <see cref="ClassRemap"/>.</summary>
    public static IEnumerable<int> Classes(ReadOnlySpan<byte> d, int t, GlyphRemap glyphs) => Classed(d, t, glyphs).Select(c => c.Class);

    /// <summary>A coverage table of new glyph ids (ascending, no repeats) in format 1 or 2, whichever is smaller.</summary>
    public static OtObject WriteCoverage(OtGraph graph, IReadOnlyList<int> glyphs)
    {
        int ranges = 0;
        for (int i = 0; i < glyphs.Count; i++)
        {
            if (i == 0 || glyphs[i] != glyphs[i - 1] + 1)
            {
                ranges++;
            }
        }

        OtWriter w = OtGraph.Writer();
        if (glyphs.Count == 0 || 2 * glyphs.Count <= 6 * ranges)
        {
            w.U16(1);
            w.U16(glyphs.Count);
            foreach (int g in glyphs)
            {
                w.U16(g);
            }
        }
        else
        {
            w.U16(2);
            w.U16(ranges);
            int start = 0;
            for (int i = 1; i <= glyphs.Count; i++)
            {
                if (i == glyphs.Count || glyphs[i] != glyphs[i - 1] + 1)
                {
                    w.U16(glyphs[start]);
                    w.U16(glyphs[i - 1]);
                    w.U16(start);
                    start = i;
                }
            }
        }

        return graph.Add(w);
    }

    /// <summary>A class definition of new glyph ids (ascending) and their classes (not 0), in format 1 or 2, whichever is smaller.</summary>
    public static OtObject WriteClassDef(OtGraph graph, IReadOnlyList<(int Glyph, int Class)> entries)
    {
        int ranges = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            if (i == 0 || entries[i].Glyph != entries[i - 1].Glyph + 1 || entries[i].Class != entries[i - 1].Class)
            {
                ranges++;
            }
        }

        OtWriter w = OtGraph.Writer();
        int span = entries.Count == 0 ? 0 : entries[^1].Glyph - entries[0].Glyph + 1;
        if (entries.Count == 0)
        {
            w.U16(2);
            w.U16(0);
        }
        else if (6 + (2 * span) <= 4 + (6 * ranges))
        {
            w.U16(1);
            w.U16(entries[0].Glyph);
            w.U16(span);
            int next = 0;
            for (int g = entries[0].Glyph; g <= entries[^1].Glyph; g++)
            {
                if (entries[next].Glyph == g)
                {
                    w.U16(entries[next++].Class);
                }
                else
                {
                    w.U16(0);
                }
            }
        }
        else
        {
            w.U16(2);
            w.U16(ranges);
            int start = 0;
            for (int i = 1; i <= entries.Count; i++)
            {
                if (i == entries.Count || entries[i].Glyph != entries[i - 1].Glyph + 1 || entries[i].Class != entries[i - 1].Class)
                {
                    w.U16(entries[start].Glyph);
                    w.U16(entries[i - 1].Glyph);
                    w.U16(entries[start].Class);
                    start = i;
                }
            }
        }

        return graph.Add(w);
    }

    /// <summary>A class definition's kept glyphs with new ids and classes renumbered by <paramref name="remap"/>.</summary>
    public static OtObject WriteClassDef(OtGraph graph, ReadOnlySpan<byte> d, int t, GlyphRemap glyphs, ClassRemap? remap)
    {
        List<(int, int)> entries = [];
        foreach ((int old, int klass) in Classed(d, t, glyphs))
        {
            int now = remap is null ? klass : remap.Map(klass);
            if (now > 0)
            {
                entries.Add((glyphs.Map(old), now));
            }
        }

        return WriteClassDef(graph, entries);
    }

    /// <summary>
    /// A copy of <paramref name="length"/> bytes at <paramref name="at"/>, zero past the end of the data as the shaper reads them;
    /// null for a null offset.
    /// </summary>
    public static OtObject? Blob(OtGraph graph, ReadOnlySpan<byte> d, int at, int length)
    {
        if (at == 0)
        {
            return null;
        }

        OtWriter w = OtGraph.Writer();
        int available = Math.Clamp(d.Length - at, 0, length);
        w.Bytes(d.Slice(at, available));
        for (int i = available; i < length; i++)
        {
            w.U8(0);
        }

        return graph.Add(w);
    }

    /// <summary>A device or variation index table (formats 1 to 3, or 0x8000), as HarfBuzz sizes it; null for a null offset.</summary>
    public static OtObject? Device(OtGraph graph, ReadOnlySpan<byte> d, int t)
    {
        if (t == 0)
        {
            return null;
        }

        int format = U16(d, t + 4);
        int size = 6;
        if (format is >= 1 and <= 3)
        {
            int start = U16(d, t), end = U16(d, t + 2);
            size = start > end ? 6 : 2 * (4 + ((end - start) >> (4 - format)));
        }

        return Blob(graph, d, t, size);
    }
}
