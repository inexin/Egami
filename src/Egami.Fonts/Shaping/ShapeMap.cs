// Translated from HarfBuzz's hb-ot-map.hh and hb-ot-map.cc (MIT); see THIRD-PARTY-NOTICES.md.
namespace Egami.Fonts.Shaping;

/// <summary>How a feature is added to a shape plan.</summary>
[Flags]
internal enum FeatureFlags
{
    None = 0,
    Global = 0x0001,
    HasFallback = 0x0002,
    ManualZwnj = 0x0004,
    ManualZwj = 0x0008,
    ManualJoiners = ManualZwnj | ManualZwj,
    GlobalManualJoiners = Global | ManualJoiners,
    GlobalHasFallback = Global | HasFallback,
    GlobalSearch = 0x0010,
    Random = 0x0020,
    PerSyllable = 0x0040,
}

/// <summary>A pause between lookup stages, where a script shaper reorders or tags the buffer.</summary>
internal delegate bool PauseFunc(ShapePlan plan, ShapeFont font, LayoutBuffer buffer);

/// <summary>
/// The compiled feature map of a shape plan: each feature's mask bits and the lookups of GSUB and GPOS in application order,
/// grouped in stages separated by pauses.
/// </summary>
internal sealed class ShapeMap
{
    public const int MaxBits = 8;
    public const uint MaxValue = (1u << MaxBits) - 1;

    public uint GlobalMask;
    public readonly uint[] ChosenScript = new uint[2];
    public readonly bool[] FoundScript = new bool[2];
    public readonly List<FeatureMap> Features = [];
    public readonly List<LookupMap>[] Lookups = [[], []];
    public readonly List<StageMap>[] Stages = [[], []];

    internal sealed class FeatureMap
    {
        public uint Tag;
        public readonly int[] Index = new int[2];
        public readonly int[] Stage = new int[2];
        public int Shift;
        public uint Mask;
        public uint OneMask;
        public bool NeedsFallback;
        public bool AutoZwnj;
        public bool AutoZwj;
        public bool Random;
        public bool PerSyllable;
    }

    internal struct LookupMap
    {
        public int Index;
        public bool AutoZwnj;
        public bool AutoZwj;
        public bool Random;
        public bool PerSyllable;
        public uint Mask;
        public uint FeatureTag;
    }

    internal struct StageMap
    {
        public int LastLookup;
        public PauseFunc? PauseFunc;
    }

    private FeatureMap? Find(uint tag)
    {
        int lo = 0, hi = Features.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            uint t = Features[mid].Tag;
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
                return Features[mid];
            }
        }

        return null;
    }

    public uint GetMask(uint tag) => Find(tag)?.Mask ?? 0;

    public uint GetMask(uint tag, out int shift)
    {
        FeatureMap? map = Find(tag);
        shift = map?.Shift ?? 0;
        return map?.Mask ?? 0;
    }

    public uint Get1Mask(uint tag) => Find(tag)?.OneMask ?? 0;

    public bool NeedsFallback(uint tag) => Find(tag)?.NeedsFallback ?? false;

    public int GetFeatureIndex(int tableIndex, uint tag) => Find(tag)?.Index[tableIndex] ?? -1;

    public int GetFeatureStage(int tableIndex, uint tag) => Find(tag)?.Stage[tableIndex] ?? int.MaxValue;
}

/// <summary>Collects a shape plan's features and compiles them into a <see cref="ShapeMap"/>, as HarfBuzz's hb_ot_map_builder_t.</summary>
internal sealed class ShapeMapBuilder
{
    private readonly LayoutTable[] _tables;
    private readonly int[] _scriptIndex = new int[2];
    private readonly int[] _languageIndex = new int[2];
    private readonly int[] _currentStage = new int[2];
    private readonly List<FeatureInfo> _featureInfos = [];
    private readonly List<(int Index, PauseFunc? Func)>[] _stages = [[], []];

    public ShapeMapBuilder(LayoutTable gsub, LayoutTable gpos, ReadOnlySpan<uint> scriptTags, ReadOnlySpan<uint> languageTags)
    {
        _tables = [gsub, gpos];
        for (int t = 0; t < 2; t++)
        {
            FoundScript[t] = SelectScript(_tables[t], scriptTags, out _scriptIndex[t], out ChosenScript[t]);
            _languageIndex[t] = SelectLanguage(_tables[t], _scriptIndex[t], languageTags);
        }
    }

    public bool IsSimple { get; set; } = true;

    public uint[] ChosenScript { get; } = new uint[2];

    public bool[] FoundScript { get; } = new bool[2];

    private sealed class FeatureInfo
    {
        public uint Tag;
        public int Seq;
        public uint MaxValue;
        public FeatureFlags Flags;
        public uint DefaultValue;
        public readonly int[] Stage = new int[2];
    }

    private static bool SelectScript(LayoutTable table, ReadOnlySpan<uint> scriptTags, out int scriptIndex, out uint chosen)
    {
        foreach (uint tag in scriptTags)
        {
            int index = table.FindScript(tag);
            if (index >= 0)
            {
                scriptIndex = index;
                chosen = tag;
                return true;
            }
        }

        foreach (uint fallback in new[] { Tag("DFLT"), Tag("dflt"), Tag("latn") })
        {
            int index = table.FindScript(fallback);
            if (index >= 0)
            {
                scriptIndex = index;
                chosen = fallback;
                return false;
            }
        }

        scriptIndex = -1;
        chosen = 0;
        return false;
    }

    private static int SelectLanguage(LayoutTable table, int scriptIndex, ReadOnlySpan<uint> languageTags)
    {
        if (scriptIndex < 0)
        {
            return -1;
        }

        foreach (uint tag in languageTags)
        {
            int index = table.FindLanguage(scriptIndex, tag);
            if (index >= 0)
            {
                return index;
            }
        }

        // 'dflt', as some fonts list it as a language system.
        return table.FindLanguage(scriptIndex, Tag("dflt"));
    }

    public static uint Tag(string s) => ((uint)s[0] << 24) | ((uint)s[1] << 16) | ((uint)s[2] << 8) | s[3];

    public void AddFeature(uint tag, FeatureFlags flags = FeatureFlags.None, uint value = 1)
    {
        if (tag == 0)
        {
            return;
        }

        FeatureInfo info = new()
        {
            Tag = tag,
            Seq = _featureInfos.Count + 1,
            MaxValue = value,
            Flags = flags,
            DefaultValue = (flags & FeatureFlags.Global) != 0 ? value : 0,
        };
        info.Stage[0] = _currentStage[0];
        info.Stage[1] = _currentStage[1];
        _featureInfos.Add(info);
    }

    public void AddFeature(string tag, FeatureFlags flags = FeatureFlags.None, uint value = 1) => AddFeature(Tag(tag), flags, value);

    public void EnableFeature(string tag, FeatureFlags flags = FeatureFlags.None, uint value = 1) => AddFeature(Tag(tag), FeatureFlags.Global | flags, value);

    public void DisableFeature(string tag) => AddFeature(Tag(tag), FeatureFlags.Global, 0);

    public void AddGsubPause(PauseFunc? func) => AddPause(0, func);

    public void AddGposPause(PauseFunc? func) => AddPause(1, func);

    private void AddPause(int tableIndex, PauseFunc? func)
    {
        _stages[tableIndex].Add((_currentStage[tableIndex], func));
        _currentStage[tableIndex]++;
    }

    public bool HasFeature(uint tag)
    {
        for (int t = 0; t < 2; t++)
        {
            int langSys = _tables[t].GetLangSys(_scriptIndex[t], _languageIndex[t]);
            foreach (int index in _tables[t].GetLangSysFeatures(langSys))
            {
                if (_tables[t].GetFeatureTag(index) == tag)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Compiles the features into masks and lookup lists; <paramref name="variationsIndex"/> picks FeatureVariations records.</summary>
    public ShapeMap Compile(int[] variationsIndex)
    {
        ShapeMap m = new();
        const int globalBitShift = 31;
        const uint globalBitMask = 1u << globalBitShift;
        m.GlobalMask = globalBitMask;

        int[] requiredFeatureIndex = new int[2];
        uint[] requiredFeatureTag = new uint[2];
        int[] requiredFeatureStage = [0, 0];
        for (int t = 0; t < 2; t++)
        {
            m.ChosenScript[t] = ChosenScript[t];
            m.FoundScript[t] = FoundScript[t];
            int langSys = _tables[t].GetLangSys(_scriptIndex[t], _languageIndex[t]);
            requiredFeatureIndex[t] = _tables[t].GetRequiredFeature(langSys);
            requiredFeatureTag[t] = requiredFeatureIndex[t] >= 0 ? _tables[t].GetFeatureTag(requiredFeatureIndex[t]) : 0;
        }

        // Sort features and merge duplicates.
        List<FeatureInfo> infos = _featureInfos;
        if (infos.Count > 0)
        {
            if (!IsSimple)
            {
                infos.Sort((a, b) => a.Tag != b.Tag ? a.Tag.CompareTo(b.Tag) : a.Seq.CompareTo(b.Seq));
            }

            int j = 0;
            for (int i = 1; i < infos.Count; i++)
            {
                if (infos[i].Tag != infos[j].Tag)
                {
                    infos[++j] = infos[i];
                }
                else
                {
                    if ((infos[i].Flags & FeatureFlags.Global) != 0)
                    {
                        infos[j].Flags |= FeatureFlags.Global;
                        infos[j].MaxValue = infos[i].MaxValue;
                        infos[j].DefaultValue = infos[i].DefaultValue;
                    }
                    else
                    {
                        if ((infos[j].Flags & FeatureFlags.Global) != 0)
                        {
                            infos[j].Flags ^= FeatureFlags.Global;
                        }

                        infos[j].MaxValue = Math.Max(infos[j].MaxValue, infos[i].MaxValue);
                    }

                    infos[j].Flags |= infos[i].Flags & FeatureFlags.HasFallback;
                    infos[j].Stage[0] = Math.Min(infos[j].Stage[0], infos[i].Stage[0]);
                    infos[j].Stage[1] = Math.Min(infos[j].Stage[1], infos[i].Stage[1]);
                }
            }

            infos.RemoveRange(j + 1, infos.Count - j - 1);
        }

        // The feature indexes of the chosen language systems; earlier entries win.
        Dictionary<uint, int>[] featureIndices = [new(), new()];
        for (int t = 0; t < 2; t++)
        {
            int langSys = _tables[t].GetLangSys(_scriptIndex[t], _languageIndex[t]);
            int[] indexes = _tables[t].GetLangSysFeatures(langSys);
            for (int i = indexes.Length; i > 0; i--)
            {
                featureIndices[t][_tables[t].GetFeatureTag(indexes[i - 1])] = indexes[i - 1];
            }
        }

        int nextBit = 3 + 1;
        foreach (FeatureInfo info in infos)
        {
            int bitsNeeded = (info.Flags & FeatureFlags.Global) != 0 && info.MaxValue == 1 ? 0 : Math.Min(ShapeMap.MaxBits, BitStorage(info.MaxValue));
            if (info.MaxValue == 0 || nextBit + bitsNeeded >= globalBitShift)
            {
                continue;
            }

            bool found = false;
            int[] featureIndex = new int[2];
            for (int t = 0; t < 2; t++)
            {
                if (requiredFeatureTag[t] == info.Tag)
                {
                    requiredFeatureStage[t] = info.Stage[t];
                }

                if (featureIndices[t].TryGetValue(info.Tag, out int index))
                {
                    featureIndex[t] = index;
                    found = true;
                }
                else
                {
                    featureIndex[t] = -1;
                }
            }

            if (!found && (info.Flags & FeatureFlags.GlobalSearch) != 0)
            {
                for (int t = 0; t < 2; t++)
                {
                    int index = _tables[t].FindFeature(info.Tag);
                    featureIndex[t] = index;
                    found |= index >= 0;
                }
            }

            if (!found && (info.Flags & FeatureFlags.HasFallback) == 0)
            {
                continue;
            }

            ShapeMap.FeatureMap map = new()
            {
                Tag = info.Tag,
                AutoZwnj = (info.Flags & FeatureFlags.ManualZwnj) == 0,
                AutoZwj = (info.Flags & FeatureFlags.ManualZwj) == 0,
                Random = (info.Flags & FeatureFlags.Random) != 0,
                PerSyllable = (info.Flags & FeatureFlags.PerSyllable) != 0,
                NeedsFallback = !found,
            };
            map.Index[0] = featureIndex[0];
            map.Index[1] = featureIndex[1];
            map.Stage[0] = info.Stage[0];
            map.Stage[1] = info.Stage[1];
            if ((info.Flags & FeatureFlags.Global) != 0 && info.MaxValue == 1)
            {
                map.Shift = globalBitShift;
                map.Mask = globalBitMask;
            }
            else
            {
                map.Shift = nextBit;
                map.Mask = (uint)((1L << (nextBit + bitsNeeded)) - (1L << nextBit));
                nextBit += bitsNeeded;
                m.GlobalMask |= (info.DefaultValue << map.Shift) & map.Mask;
            }

            map.OneMask = (1u << map.Shift) & map.Mask;
            m.Features.Add(map);
        }

        m.Features.Sort((a, b) => a.Tag.CompareTo(b.Tag));

        AddGsubPause(null);
        AddGposPause(null);

        for (int t = 0; t < 2; t++)
        {
            List<ShapeMap.LookupMap> lookups = m.Lookups[t];
            int stageIndex = 0;
            int lastNumLookups = 0;
            for (int stage = 0; stage < _currentStage[t]; stage++)
            {
                if (requiredFeatureIndex[t] >= 0 && requiredFeatureStage[t] == stage)
                {
                    AddLookups(m, t, requiredFeatureIndex[t], variationsIndex[t], globalBitMask, true, true, false, false, 0);
                }

                foreach (ShapeMap.FeatureMap feature in m.Features)
                {
                    if (feature.Stage[t] == stage)
                    {
                        AddLookups(m, t, feature.Index[t], variationsIndex[t], feature.Mask, feature.AutoZwnj, feature.AutoZwj, feature.Random, feature.PerSyllable, feature.Tag);
                    }
                }

                // Sort lookups and merge duplicates.
                if (lastNumLookups + 1 < lookups.Count)
                {
                    lookups.Sort(lastNumLookups, lookups.Count - lastNumLookups, Comparer<ShapeMap.LookupMap>.Create((a, b) => a.Index.CompareTo(b.Index)));
                    int j = lastNumLookups;
                    for (int i = j + 1; i < lookups.Count; i++)
                    {
                        if (lookups[i].Index != lookups[j].Index)
                        {
                            lookups[++j] = lookups[i];
                        }
                        else
                        {
                            ShapeMap.LookupMap merged = lookups[j];
                            merged.Mask |= lookups[i].Mask;
                            merged.AutoZwnj &= lookups[i].AutoZwnj;
                            merged.AutoZwj &= lookups[i].AutoZwj;
                            lookups[j] = merged;
                        }
                    }

                    lookups.RemoveRange(j + 1, lookups.Count - j - 1);
                }

                lastNumLookups = lookups.Count;
                if (stageIndex < _stages[t].Count && _stages[t][stageIndex].Index == stage)
                {
                    m.Stages[t].Add(new ShapeMap.StageMap { LastLookup = lastNumLookups, PauseFunc = _stages[t][stageIndex].Func });
                    stageIndex++;
                }
            }
        }

        return m;
    }

    private void AddLookups(ShapeMap m, int t, int featureIndex, int variationsIndex, uint mask, bool autoZwnj, bool autoZwj, bool random, bool perSyllable, uint featureTag)
    {
        if (featureIndex < 0)
        {
            return;
        }

        int count = _tables[t].LookupCount;
        foreach (int index in _tables[t].GetFeatureLookups(featureIndex, variationsIndex))
        {
            if (index >= count)
            {
                continue;
            }

            m.Lookups[t].Add(new ShapeMap.LookupMap
            {
                Index = index,
                Mask = mask,
                AutoZwnj = autoZwnj,
                AutoZwj = autoZwj,
                Random = random,
                PerSyllable = perSyllable,
                FeatureTag = featureTag,
            });
        }
    }

    private static int BitStorage(uint v) => v == 0 ? 0 : 32 - System.Numerics.BitOperations.LeadingZeroCount(v);
}
