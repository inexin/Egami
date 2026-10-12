namespace Egami.Fonts.Shaping;

/// <summary>
/// Builds a small GSUB table in memory, a lookup list without scripts or features, for the lookups a shaper synthesizes (Arabic
/// fallback shaping), so the layout engine applies them as it applies a font's.
/// </summary>
internal sealed class GsubBuilder
{
    private readonly List<(int Type, int Flag, byte[][] Subtables)> _lookups = [];

    public void AddLookup(int type, int flag, byte[][] subtables) => _lookups.Add((type, flag, subtables));

    public LayoutTable Build()
    {
        List<byte> table = [];
        Add16(table, 1);
        Add16(table, 0);
        Add16(table, 10);
        Add16(table, 12);
        Add16(table, 14);
        Add16(table, 0); // Script list: no scripts.
        Add16(table, 0); // Feature list: no features.

        int lookupList = table.Count;
        Add16(table, _lookups.Count);
        int lookupOffsets = table.Count;
        table.AddRange(new byte[_lookups.Count * 2]);
        for (int i = 0; i < _lookups.Count; i++)
        {
            (int type, int flag, byte[][] subtables) = _lookups[i];
            int lookup = table.Count;
            Set16(table, lookupOffsets + (i * 2), lookup - lookupList);
            Add16(table, type);
            Add16(table, flag);
            Add16(table, subtables.Length);
            int subtableOffsets = table.Count;
            table.AddRange(new byte[subtables.Length * 2]);
            for (int s = 0; s < subtables.Length; s++)
            {
                Set16(table, subtableOffsets + (s * 2), table.Count - lookup);
                table.AddRange(subtables[s]);
            }
        }

        return new LayoutTable(table.ToArray(), false);
    }

    /// <summary>A single substitution, format 2, of glyphs sorted by id.</summary>
    public static byte[] SingleFormat2(ushort[] glyphs, ushort[] substitutes)
    {
        List<byte> s = [];
        Add16(s, 2);
        Add16(s, 6 + (substitutes.Length * 2));
        Add16(s, substitutes.Length);
        foreach (ushort g in substitutes)
        {
            Add16(s, g);
        }

        AddCoverage(s, glyphs);
        return [.. s];
    }

    /// <summary>A ligature substitution, format 1: for each first glyph (sorted by id), its ligatures and their other components.</summary>
    public static byte[] LigatureFormat1(ushort[] firstGlyphs, List<(ushort Ligature, ushort[] Components)>[] ligatureSets)
    {
        List<byte> s = [];
        Add16(s, 1);
        Add16(s, 0);
        Add16(s, firstGlyphs.Length);
        int setOffsets = s.Count;
        s.AddRange(new byte[firstGlyphs.Length * 2]);
        for (int i = 0; i < firstGlyphs.Length; i++)
        {
            int set = s.Count;
            Set16(s, setOffsets + (i * 2), set);
            Add16(s, ligatureSets[i].Count);
            int ligatureOffsets = s.Count;
            s.AddRange(new byte[ligatureSets[i].Count * 2]);
            for (int l = 0; l < ligatureSets[i].Count; l++)
            {
                (ushort ligature, ushort[] components) = ligatureSets[i][l];
                Set16(s, ligatureOffsets + (l * 2), s.Count - set);
                Add16(s, ligature);
                Add16(s, components.Length + 1);
                foreach (ushort c in components)
                {
                    Add16(s, c);
                }
            }
        }

        Set16(s, 2, s.Count);
        AddCoverage(s, firstGlyphs);
        return [.. s];
    }

    private static void AddCoverage(List<byte> s, ushort[] glyphs)
    {
        Add16(s, 1);
        Add16(s, glyphs.Length);
        foreach (ushort g in glyphs)
        {
            Add16(s, g);
        }
    }

    private static void Add16(List<byte> s, int value)
    {
        s.Add((byte)(value >> 8));
        s.Add((byte)value);
    }

    private static void Set16(List<byte> s, int at, int value)
    {
        s[at] = (byte)(value >> 8);
        s[at + 1] = (byte)value;
    }
}
