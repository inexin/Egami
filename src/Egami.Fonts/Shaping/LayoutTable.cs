using static Egami.Fonts.Shaping.OtData;

namespace Egami.Fonts.Shaping;

/// <summary>One lookup of GSUB or GPOS: its type, flags and subtables, with extension subtables resolved.</summary>
internal sealed class LayoutLookup
{
    public LayoutLookup(int type, int props, int[] subtableTypes, int[] subtables, HashSet<int>? coverage)
    {
        Type = type;
        Props = props;
        SubtableTypes = subtableTypes;
        Subtables = subtables;
        Coverage = coverage;
    }

    /// <summary>Gets the lookup type, or the type its extension subtables wrap.</summary>
    public int Type { get; }

    /// <summary>Gets the lookup flag, with the mark filtering set in the high 16 bits.</summary>
    public int Props { get; }

    public int[] SubtableTypes { get; }

    /// <summary>Gets each subtable's offset from the start of the table.</summary>
    public int[] Subtables { get; }

    /// <summary>Gets every glyph the subtables' first coverage lists, or null when the lookup may start anywhere.</summary>
    public HashSet<int>? Coverage { get; }
}

/// <summary>A GSUB or GPOS table: script, feature and lookup lists, and feature variations.</summary>
internal sealed class LayoutTable
{
    private readonly int _scriptList;
    private readonly int _featureList;
    private readonly int _lookupList;
    private readonly int _featureVariations;
    private readonly LayoutLookup?[] _lookups;

    public LayoutTable(ReadOnlyMemory<byte> data, bool isGpos)
    {
        Data = data;
        IsGpos = isGpos;
        ReadOnlySpan<byte> d = data.Span;
        if (d.Length < 10 || U16(d, 0) != 1)
        {
            _lookups = [];
            return;
        }

        _scriptList = Off16(d, 0, 4);
        _featureList = Off16(d, 0, 6);
        _lookupList = Off16(d, 0, 8);
        if (U16(d, 2) >= 1)
        {
            _featureVariations = Off32(d, 0, 10);
        }

        _lookups = new LayoutLookup?[_lookupList == 0 ? 0 : U16(d, _lookupList)];
    }

    public static LayoutTable EmptyGsub { get; } = new(ReadOnlyMemory<byte>.Empty, false);

    public static LayoutTable EmptyGpos { get; } = new(ReadOnlyMemory<byte>.Empty, true);

    public ReadOnlyMemory<byte> Data { get; }

    public bool IsGpos { get; }

    /// <summary>Gets whether the font has the table with a known version, as HarfBuzz's has_data.</summary>
    public bool HasData => Data.Length >= 10 && U16(Data.Span, 0) == 1;

    public int LookupCount => _lookups.Length;

    public int ScriptCount => _scriptList == 0 ? 0 : U16(Data.Span, _scriptList);

    public int FeatureCount => _featureList == 0 ? 0 : U16(Data.Span, _featureList);

    public uint GetScriptTag(int index) => Tag(Data.Span, _scriptList + 2 + (index * 6));

    public uint GetFeatureTag(int index) => (uint)index < (uint)FeatureCount ? Tag(Data.Span, _featureList + 2 + (index * 6)) : 0;

    /// <summary>Finds a script by tag with a binary search, as HarfBuzz does; -1 when absent.</summary>
    public int FindScript(uint tag) => FindRecord(_scriptList, tag);

    /// <summary>The offset of a script table, or 0.</summary>
    public int GetScript(int index) => (uint)index < (uint)ScriptCount ? Off16(Data.Span, _scriptList, _scriptList + 2 + (index * 6) + 4) : 0;

    /// <summary>Finds a language system in a script; -1 when absent (the default language system is used then).</summary>
    public int FindLanguage(int scriptIndex, uint tag)
    {
        int script = GetScript(scriptIndex);
        if (script == 0)
        {
            return -1;
        }

        ReadOnlySpan<byte> d = Data.Span;
        int count = U16(d, script + 2);
        int lo = 0, hi = count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            uint t = Tag(d, script + 4 + (mid * 6));
            if (tag < t)
            {
                hi = mid - 1;
            }
            else if (tag > t)
            {
                lo = mid + 1;
            }
            else
            {
                return mid;
            }
        }

        return -1;
    }

    /// <summary>The offset of a language system table: <paramref name="languageIndex"/> -1 is the script's default.</summary>
    public int GetLangSys(int scriptIndex, int languageIndex)
    {
        int script = GetScript(scriptIndex);
        if (script == 0)
        {
            return 0;
        }

        ReadOnlySpan<byte> d = Data.Span;
        if (languageIndex < 0)
        {
            return Off16(d, script, script);
        }

        return languageIndex < U16(d, script + 2) ? Off16(d, script, script + 4 + (languageIndex * 6) + 4) : 0;
    }

    /// <summary>The required feature index of a language system, or -1.</summary>
    public int GetRequiredFeature(int langSys)
    {
        if (langSys == 0)
        {
            return -1;
        }

        int index = U16(Data.Span, langSys + 2);
        return index == 0xFFFF ? -1 : index;
    }

    /// <summary>The feature indexes a language system lists.</summary>
    public int[] GetLangSysFeatures(int langSys)
    {
        if (langSys == 0)
        {
            return [];
        }

        ReadOnlySpan<byte> d = Data.Span;
        int count = U16(d, langSys + 4);
        int[] result = new int[count];
        for (int i = 0; i < count; i++)
        {
            result[i] = U16(d, langSys + 6 + (i * 2));
        }

        return result;
    }

    /// <summary>Finds a feature by tag in the whole feature list (the first), for features searched globally; -1 when absent.</summary>
    public int FindFeature(uint tag)
    {
        for (int i = 0; i < FeatureCount; i++)
        {
            if (GetFeatureTag(i) == tag)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The lookup indexes of a feature, with a feature variation record's substitute feature table when it has one.</summary>
    public int[] GetFeatureLookups(int featureIndex, int variationsIndex)
    {
        ReadOnlySpan<byte> d = Data.Span;
        if ((uint)featureIndex >= (uint)FeatureCount)
        {
            return [];
        }

        int feature = Off16(d, _featureList, _featureList + 2 + (featureIndex * 6) + 4);
        if (variationsIndex >= 0 && _featureVariations != 0)
        {
            int substitute = FindFeatureSubstitute(variationsIndex, featureIndex);
            if (substitute != 0)
            {
                feature = substitute;
            }
        }

        if (feature == 0)
        {
            return [];
        }

        int count = U16(d, feature + 2);
        int[] result = new int[count];
        for (int i = 0; i < count; i++)
        {
            result[i] = U16(d, feature + 4 + (i * 2));
        }

        return result;
    }

    /// <summary>
    /// The first FeatureVariations record whose conditions hold at the normalized coordinates (in 2.14 units), or -1, as
    /// HarfBuzz's find_index.
    /// </summary>
    public int FindFeatureVariations(ReadOnlySpan<int> coords)
    {
        if (_featureVariations == 0)
        {
            return -1;
        }

        ReadOnlySpan<byte> d = Data.Span;
        int baseOffset = _featureVariations;
        if (U16(d, baseOffset) != 1)
        {
            return -1;
        }

        uint count = U32(d, baseOffset + 4);
        for (int i = 0; i < count && i < 65536; i++)
        {
            int record = baseOffset + 8 + (i * 8);
            int conditionSet = Off32(d, baseOffset, record);
            if (ConditionSetHolds(d, conditionSet, coords))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool ConditionSetHolds(ReadOnlySpan<byte> d, int set, ReadOnlySpan<int> coords)
    {
        if (set == 0)
        {
            return true;
        }

        int count = U16(d, set);
        for (int i = 0; i < count; i++)
        {
            int condition = Off32(d, set, set + 2 + (i * 4));
            if (condition == 0 || U16(d, condition) != 1)
            {
                return false;
            }

            int axis = U16(d, condition + 2);
            int min = S16(d, condition + 4);
            int max = S16(d, condition + 6);
            int coord = axis < coords.Length ? coords[axis] : 0;
            if (coord < min || coord > max)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Adds the lookups of every FeatureVariations record's substitute for a feature in <paramref name="featureIndexes"/>, whatever
    /// the record's conditions, as HarfBuzz's feature_variation_collect_lookups.
    /// </summary>
    public void CollectFeatureVariationLookups(IReadOnlySet<int> featureIndexes, ISet<int> lookups)
    {
        ReadOnlySpan<byte> d = Data.Span;
        int baseOffset = _featureVariations;
        if (baseOffset == 0 || U16(d, baseOffset) != 1)
        {
            return;
        }

        uint count = U32(d, baseOffset + 4);
        for (int i = 0; i < count && i < 65536; i++)
        {
            int substitution = Off32(d, baseOffset, baseOffset + 8 + (i * 8) + 4);
            if (substitution == 0 || U16(d, substitution) != 1)
            {
                continue;
            }

            int records = U16(d, substitution + 4);
            for (int j = 0; j < records; j++)
            {
                int r = substitution + 6 + (j * 6);
                if (!featureIndexes.Contains(U16(d, r)))
                {
                    continue;
                }

                int feature = Off32(d, substitution, r + 2);
                int lookupCount = feature == 0 ? 0 : U16(d, feature + 2);
                for (int k = 0; k < lookupCount; k++)
                {
                    lookups.Add(U16(d, feature + 4 + (k * 2)));
                }
            }
        }
    }

    private int FindFeatureSubstitute(int variationsIndex, int featureIndex)
    {
        ReadOnlySpan<byte> d = Data.Span;
        int baseOffset = _featureVariations;
        if ((uint)variationsIndex >= U32(d, baseOffset + 4))
        {
            return 0;
        }

        int record = baseOffset + 8 + (variationsIndex * 8);
        int substitution = Off32(d, baseOffset, record + 4);
        if (substitution == 0 || U16(d, substitution) != 1)
        {
            return 0;
        }

        int count = U16(d, substitution + 4);
        int lo = 0, hi = count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            int r = substitution + 6 + (mid * 6);
            int index = U16(d, r);
            if (featureIndex < index)
            {
                hi = mid - 1;
            }
            else if (featureIndex > index)
            {
                lo = mid + 1;
            }
            else
            {
                return Off32(d, substitution, r + 2);
            }
        }

        return 0;
    }

    /// <summary>The lookup at <paramref name="index"/>, read once; null when absent or broken.</summary>
    public LayoutLookup? GetLookup(int index)
    {
        if ((uint)index >= (uint)_lookups.Length)
        {
            return null;
        }

        LayoutLookup? lookup = Volatile.Read(ref _lookups[index]);
        if (lookup is null)
        {
            lookup = ReadLookup(index);
            Volatile.Write(ref _lookups[index], lookup);
        }

        return lookup;
    }

    private LayoutLookup ReadLookup(int index)
    {
        ReadOnlySpan<byte> d = Data.Span;
        int offset = Off16(d, _lookupList, _lookupList + 2 + (index * 2));
        if (offset == 0)
        {
            return new LayoutLookup(0, 0, [], [], []);
        }

        int type = U16(d, offset);
        int flag = U16(d, offset + 2);
        int count = U16(d, offset + 4);
        int props = flag;
        if ((flag & LookupFlag.UseMarkFilteringSet) != 0)
        {
            props |= U16(d, offset + 6 + (count * 2)) << 16;
        }

        int extension = IsGpos ? 9 : 7;
        List<int> types = new(count);
        List<int> subtables = new(count);
        int lookupType = type;
        for (int i = 0; i < count; i++)
        {
            int sub = Off16(d, offset, offset + 6 + (i * 2));
            int subType = type;
            if (sub != 0 && type == extension)
            {
                if (U16(d, sub) != 1)
                {
                    continue;
                }

                subType = U16(d, sub + 2);
                sub = Off32(d, sub, sub + 4);
                if (i == 0)
                {
                    lookupType = subType;
                }
            }

            if (sub == 0)
            {
                continue;
            }

            types.Add(subType);
            subtables.Add(sub);
        }

        HashSet<int>? coverage = new();
        for (int i = 0; i < subtables.Count && coverage is not null; i++)
        {
            int first = FirstCoverage(d, types[i], subtables[i]);
            if (first < 0)
            {
                coverage = null;
            }
            else
            {
                CollectCoverage(d, first, coverage);
            }
        }

        return new LayoutLookup(lookupType, props, [.. types], [.. subtables], coverage);
    }

    /// <summary>The coverage that decides whether a subtable applies to the current glyph; -1 when it may apply to any glyph.</summary>
    private int FirstCoverage(ReadOnlySpan<byte> d, int type, int sub)
    {
        int format = U16(d, sub);
        bool context = IsGpos ? type is 7 or 8 : type is 5 or 6;
        if (context && format == 3)
        {
            // Format 3 lists its coverages after the counts: input coverages for context, after the backtrack for chain context.
            bool chain = IsGpos ? type == 8 : type == 6;
            if (!chain)
            {
                return U16(d, sub + 2) == 0 ? 0 : Off16(d, sub, sub + 6);
            }

            int backtrack = U16(d, sub + 2);
            int inputCount = sub + 4 + (backtrack * 2);
            return U16(d, inputCount) == 0 ? 0 : Off16(d, sub, inputCount + 2);
        }

        if (!IsGpos && type == 8)
        {
            return Off16(d, sub, sub + 2);
        }

        return Off16(d, sub, sub + 2);
    }

    private int FindRecord(int list, uint tag)
    {
        if (list == 0)
        {
            return -1;
        }

        ReadOnlySpan<byte> d = Data.Span;
        int count = U16(d, list);
        int lo = 0, hi = count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            uint t = Tag(d, list + 2 + (mid * 6));
            if (tag < t)
            {
                hi = mid - 1;
            }
            else if (tag > t)
            {
                lo = mid + 1;
            }
            else
            {
                return mid;
            }
        }

        return -1;
    }
}
