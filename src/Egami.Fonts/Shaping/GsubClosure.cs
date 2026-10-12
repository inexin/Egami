// Translated from HarfBuzz's hb-ot-layout.cc, hb-ot-layout-gsubgpos.hh, OT/Layout/GSUB/*.hh, hb-ot-layout-common.hh and hb-subset-input.cc (MIT); see THIRD-PARTY-NOTICES.md.
using static Egami.Fonts.Shaping.OtData;

namespace Egami.Fonts.Shaping;

/// <summary>
/// The GSUB glyph closure as hb-subset computes it: the lookups of the chosen features in every script and language system
/// (hb_ot_layout_collect_features and the feature variations' substitutes), then every glyph those lookups, and the lookups
/// their contextual rules call, can produce from the glyph set, repeated until the set stops growing
/// (hb_ot_layout_lookups_substitute_closure), with HarfBuzz's limits.
/// </summary>
internal sealed class GsubClosure
{
    private const int MaxNestingLevel = 64;
    private const int MaxStages = 12;
    private const int MaxLookupVisitCount = 35000;
    private const int MaxScripts = 500;
    private const int MaxLangSys = 2000;
    private const int MaxFeatureIndices = 8000;
    private const uint Invalid = GlyphBitSet.Invalid;

    private readonly LayoutTable _gsub;
    private readonly int _glyphCount;
    private readonly GlyphBitSet _glyphs;
    private readonly GlyphBitSet _output;
    private readonly List<GlyphBitSet> _stack = [];
    private readonly Dictionary<int, int> _doneCounts = [];
    private readonly Dictionary<int, GlyphBitSet> _doneSets = [];
    private int _depth;
    private int _lookupCount;
    private int _nestingLeft = MaxNestingLevel;

    private GsubClosure(LayoutTable gsub, GlyphBitSet glyphs)
    {
        _gsub = gsub;
        _glyphCount = glyphs.Bound;
        _glyphs = glyphs;
        _output = new GlyphBitSet(glyphs.Bound);
    }

    /// <summary>
    /// hb-subset's default layout features (hb-subset-input.cc, after fontTools' feature groups): the common, fraction,
    /// horizontal, vertical, direction, random, justification, East Asian spacing and private features, and those of the Arabic,
    /// Hangul, Tibetan and Indic shapers.
    /// </summary>
    public static readonly string[] DefaultFeatures =
    [
        "rvrn", "ccmp", "liga", "locl", "mark", "mkmk", "rlig",
        "frac", "numr", "dnom",
        "calt", "clig", "curs", "kern", "rclt",
        "valt", "vert", "vkrn", "vpal", "vrt2",
        "ltra", "ltrm",
        "rtla", "rtlm",
        "rand",
        "jalt",
        "chws", "vchw", "halt", "vhal", "palt",
        "Harf", "HARF", "Buzz", "BUZZ",
        "init", "medi", "fina", "isol", "med2", "fin2", "fin3", "cswh", "mset", "stch",
        "ljmo", "vjmo", "tjmo",
        "abvs", "blws", "abvm", "blwm",
        "nukt", "akhn", "rphf", "rkrf", "pref", "blwf", "half", "abvf", "pstf", "cfar", "vatu", "cjct", "pres", "psts", "haln",
        "dist",
    ];

    /// <summary>
    /// Adds to <paramref name="glyphs"/> every glyph the GSUB lookups of the features <paramref name="filter"/> keeps can produce
    /// from it.
    /// </summary>
    public static void Close(LayoutTable gsub, IReadOnlySet<uint> filter, GlyphBitSet glyphs)
    {
        if (!gsub.HasData)
        {
            return;
        }

        SortedSet<int> lookups = CollectLookups(gsub, filter);
        GsubClosure c = new(gsub, glyphs);
        int iteration = 0;
        int count;
        do
        {
            c._lookupCount = 0;
            count = glyphs.Count;
            foreach (int index in lookups)
            {
                c.CloseLookup(index);
            }
        }
        while (iteration++ <= MaxStages && count != glyphs.Count);
    }

    /// <summary>
    /// The lookups of the features hb-subset keeps (hb-subset-plan-layout.cc's _collect_layout_indices): every feature of every
    /// script and language system whose tag the filter keeps, or all of them, required features included, when it keeps every
    /// tag the table lists; then the lookups of their feature variation substitutes.
    /// </summary>
    private static SortedSet<int> CollectLookups(LayoutTable gsub, IReadOnlySet<uint> filter)
    {
        // _filter_tag_list: the filter only applies when it leaves out a tag the table lists.
        bool filtered = false;
        HashSet<int> kept = [];
        for (int i = 0; i < gsub.FeatureCount; i++)
        {
            uint tag = gsub.GetFeatureTag(i);
            if (tag == 0)
            {
                continue;
            }

            if (filter.Contains(tag))
            {
                kept.Add(i);
            }
            else
            {
                filtered = true;
            }
        }

        FeatureCollector collector = new(gsub, filtered ? kept : null);
        for (int i = 0; i < gsub.ScriptCount; i++)
        {
            collector.CollectScript(i);
        }

        SortedSet<int> lookups = [];
        foreach (int feature in collector.Features)
        {
            lookups.UnionWith(gsub.GetFeatureLookups(feature, -1));
        }

        gsub.CollectFeatureVariationLookups(collector.Features, lookups);
        return lookups;
    }

    /// <summary>hb_collect_features_context_t with every language: the feature indexes, with HarfBuzz's visit limits.</summary>
    private sealed class FeatureCollector(LayoutTable table, HashSet<int>? filter)
    {
        private readonly HashSet<int> _visitedScripts = [];
        private readonly HashSet<int> _visitedLangSys = [];
        private int _scriptCount;
        private int _langSysCount;
        private int _featureIndexCount;

        public SortedSet<int> Features { get; } = [];

        public void CollectScript(int scriptIndex)
        {
            int script = table.GetScript(scriptIndex);
            ReadOnlySpan<byte> d = table.Data.Span;
            int defaultLangSys = table.GetLangSys(scriptIndex, -1);
            int languages = script == 0 ? 0 : U16(d, script + 2);
            if ((defaultLangSys == 0 && languages == 0) || _scriptCount++ > MaxScripts || !_visitedScripts.Add(script))
            {
                return;
            }

            if (defaultLangSys != 0)
            {
                CollectLangSys(defaultLangSys);
            }

            for (int i = 0; i < languages; i++)
            {
                CollectLangSys(table.GetLangSys(scriptIndex, i));
            }
        }

        private void CollectLangSys(int langSys)
        {
            int required = table.GetRequiredFeature(langSys);
            int[] features = table.GetLangSysFeatures(langSys);
            if ((required < 0 && features.Length == 0) || _langSysCount++ > MaxLangSys || !_visitedLangSys.Add(langSys))
            {
                return;
            }

            if (filter is null)
            {
                if (required >= 0 && !VisitedFeatureIndices(1))
                {
                    Features.Add(required);
                }

                if (!VisitedFeatureIndices(features.Length))
                {
                    Features.UnionWith(features);
                }

                return;
            }

            // With a filter only the listed features count, each once; the required feature is not looked at.
            foreach (int index in features)
            {
                if (filter.Remove(index))
                {
                    Features.Add(index);
                }
            }
        }

        private bool VisitedFeatureIndices(int count)
        {
            _featureIndexCount += count;
            return _featureIndexCount > MaxFeatureIndices;
        }
    }

    private ReadOnlySpan<byte> D => _gsub.Data.Span;

    private GlyphBitSet ParentActive => _depth == 0 ? _glyphs : _stack[_depth - 1];

    private bool LookupLimitExceeded => _lookupCount > MaxLookupVisitCount;

    /// <summary>SubstLookup::closure: one lookup of the feature list, its output added to the glyphs after.</summary>
    private void CloseLookup(int index)
    {
        if (!ShouldVisitLookup(index))
        {
            return;
        }

        if (_gsub.GetLookup(index) is LayoutLookup lookup)
        {
            Dispatch(lookup);
        }

        Flush();
    }

    private void Flush()
    {
        _glyphs.UnionWith(_output);
        _output.Clear();
        _depth = 0;
    }

    private bool ShouldVisitLookup(int index) => _lookupCount++ <= MaxLookupVisitCount && !IsLookupDone(index);

    /// <summary>Whether the lookup was already visited with the current glyph set and the active glyphs.</summary>
    private bool IsLookupDone(int index)
    {
        int population = _glyphs.Count;
        if (!_doneCounts.TryGetValue(index, out int count) || count != population)
        {
            _doneCounts[index] = population;
            if (!_doneSets.TryGetValue(index, out GlyphBitSet? fresh))
            {
                _doneSets[index] = fresh = new GlyphBitSet(_glyphCount);
            }

            fresh.Clear();
        }

        GlyphBitSet covered = _doneSets[index];
        GlyphBitSet parent = ParentActive;
        if (parent.IsSubsetOf(covered))
        {
            return true;
        }

        covered.UnionWith(parent);
        return false;
    }

    private GlyphBitSet PushActive()
    {
        if (_stack.Count == _depth)
        {
            _stack.Add(new GlyphBitSet(_glyphCount));
        }

        GlyphBitSet set = _stack[_depth++];
        set.Clear();
        return set;
    }

    private void PopActive()
    {
        if (_depth > 0)
        {
            _depth--;
        }
    }

    /// <summary>A lookup a contextual rule calls at <paramref name="sequenceIndex"/>, as dispatch_closure_recurse_func.</summary>
    private void Recurse(int index, HashSet<int> coveredSequence, int sequenceIndex, int endIndex)
    {
        if (_nestingLeft == 0)
        {
            return;
        }

        _nestingLeft--;
        if (ShouldVisitLookup(index) && _gsub.GetLookup(index) is LayoutLookup lookup)
        {
            // After a lookup that may change the glyph count, later lookups of the rule see every glyph at those positions.
            if (MayHaveNon1To1(lookup))
            {
                for (int i = sequenceIndex; i <= endIndex; i++)
                {
                    coveredSequence.Add(i);
                }
            }

            Dispatch(lookup);
        }

        _nestingLeft++;
    }

    private bool MayHaveNon1To1(LayoutLookup lookup)
    {
        ReadOnlySpan<byte> d = D;
        for (int i = 0; i < lookup.Subtables.Length; i++)
        {
            int format = U16(d, lookup.Subtables[i]);
            bool non1To1 = lookup.SubtableTypes[i] switch
            {
                2 or 4 => format == 1,
                5 or 6 => format is >= 1 and <= 3,
                _ => false,
            };
            if (non1To1)
            {
                return true;
            }
        }

        return false;
    }

    private void Dispatch(LayoutLookup lookup)
    {
        for (int i = 0; i < lookup.Subtables.Length; i++)
        {
            int s = lookup.Subtables[i];
            int format = U16(D, s);
            switch (lookup.SubtableTypes[i], format)
            {
                case (1, 1):
                    SingleFormat1(s);
                    break;
                case (1, 2):
                    SingleFormat2(s);
                    break;
                case (2, 1):
                case (3, 1):
                    SequenceOrAlternates(s);
                    break;
                case (4, 1):
                    Ligatures(s);
                    break;
                case (5, 1):
                case (6, 1):
                    ContextFormat1(s, lookup.SubtableTypes[i] == 6);
                    break;
                case (5, 2):
                    ContextFormat2(s);
                    break;
                case (5, 3):
                    ContextFormat3(s);
                    break;
                case (6, 2):
                    ChainContextFormat2(s);
                    break;
                case (6, 3):
                    ChainContextFormat3(s);
                    break;
                case (8, 1):
                    ReverseChainSingle(s);
                    break;
            }
        }
    }

    private void SingleFormat1(int s)
    {
        ReadOnlySpan<byte> d = D;
        int coverage = Off16(d, s, s + 2);
        uint delta = (uint)S16(d, s + 4);
        const uint mask = 0xFFFF;

        // Help fuzzers avoid this function.
        if (CoveragePopulation(d, coverage) >= mask)
        {
            return;
        }

        GlyphBitSet intersection = new(_glyphCount);
        IntersectCoverage(d, coverage, ParentActive, intersection);

        // In degenerate fonts this can add new glyphs every round: no closure over a range mapped onto an overlapping range.
        uint minBefore = intersection.Min;
        uint maxBefore = intersection.Max;
        uint minAfter = (minBefore + delta) & mask;
        uint maxAfter = (maxBefore + delta) & mask;
        if ((uint)intersection.Count == maxBefore - minBefore + 1 &&
            ((minBefore <= minAfter && minAfter <= maxBefore) || (minBefore <= maxAfter && maxAfter <= maxBefore)))
        {
            return;
        }

        foreach (uint g in intersection.Glyphs())
        {
            _output.Add((g + delta) & mask);
        }
    }

    private void SingleFormat2(int s)
    {
        ReadOnlySpan<byte> d = D;
        int coverage = Off16(d, s, s + 2);
        int count = U16(d, s + 4);
        GlyphBitSet active = ParentActive;
        if (count > active.Count * 4)
        {
            for (uint g = Invalid; active.Next(ref g);)
            {
                int i = Coverage(d, coverage, g);
                if (i >= 0 && i < count)
                {
                    _output.Add(U16(d, s + 6 + (i * 2)));
                }
            }

            return;
        }

        List<uint> glyphs = CoverageGlyphs(d, coverage);
        for (int i = 0; i < glyphs.Count && i < count; i++)
        {
            if (active.Contains(glyphs[i]))
            {
                _output.Add(U16(d, s + 6 + (i * 2)));
            }
        }
    }

    /// <summary>Multiple (Sequence) and alternate (AlternateSet) substitutions: every glyph listed for an active glyph.</summary>
    private void SequenceOrAlternates(int s)
    {
        ReadOnlySpan<byte> d = D;
        List<uint> glyphs = CoverageGlyphs(d, Off16(d, s, s + 2));
        int count = U16(d, s + 4);
        GlyphBitSet active = ParentActive;
        for (int i = 0; i < glyphs.Count && i < count; i++)
        {
            if (!active.Contains(glyphs[i]))
            {
                continue;
            }

            int list = Off16(d, s, s + 6 + (i * 2));
            if (list == 0)
            {
                continue;
            }

            int n = U16(d, list);
            for (int j = 0; j < n; j++)
            {
                _output.Add(U16(d, list + 2 + (j * 2)));
            }
        }
    }

    private void Ligatures(int s)
    {
        ReadOnlySpan<byte> d = D;
        List<uint> glyphs = CoverageGlyphs(d, Off16(d, s, s + 2));
        int count = U16(d, s + 4);
        GlyphBitSet active = ParentActive;
        for (int i = 0; i < glyphs.Count && i < count; i++)
        {
            if (!active.Contains(glyphs[i]))
            {
                continue;
            }

            int set = Off16(d, s, s + 6 + (i * 2));
            if (set == 0)
            {
                continue;
            }

            int n = U16(d, set);
            for (int j = 0; j < n; j++)
            {
                // A null ligature reads as HarfBuzz's empty one: glyph 0 with no other components.
                int ligature = Off16(d, set, set + 2 + (j * 2));
                int components = ligature == 0 ? 0 : U16(d, ligature + 2);
                bool all = true;
                for (int k = 0; k < components - 1 && all; k++)
                {
                    all = _glyphs.Contains(U16(d, ligature + 4 + (k * 2)));
                }

                if (all)
                {
                    _output.Add(ligature == 0 ? 0u : U16(d, ligature));
                }
            }
        }
    }

    private void ReverseChainSingle(int s)
    {
        ReadOnlySpan<byte> d = D;
        int coverage = Off16(d, s, s + 2);
        int backtrack = U16(d, s + 4);
        int lookaheadAt = s + 6 + (backtrack * 2);
        int lookahead = U16(d, lookaheadAt);
        int substituteAt = lookaheadAt + 2 + (lookahead * 2);
        if (!CoverageIntersects(d, coverage, _glyphs))
        {
            return;
        }

        for (int i = 0; i < backtrack; i++)
        {
            if (!CoverageIntersects(d, Off16(d, s, s + 6 + (i * 2)), _glyphs))
            {
                return;
            }
        }

        for (int i = 0; i < lookahead; i++)
        {
            if (!CoverageIntersects(d, Off16(d, s, lookaheadAt + 2 + (i * 2)), _glyphs))
            {
                return;
            }
        }

        List<uint> glyphs = CoverageGlyphs(d, coverage);
        int count = U16(d, substituteAt);
        GlyphBitSet active = ParentActive;
        for (int i = 0; i < glyphs.Count && i < count; i++)
        {
            if (active.Contains(glyphs[i]))
            {
                _output.Add(U16(d, substituteAt + 2 + (i * 2)));
            }
        }
    }

    /// <summary>How a rule's values match: glyphs (format 1), classes (format 2) or coverages (format 3).</summary>
    private enum ContextFormat
    {
        Simple = 1,
        ClassBased = 2,
        CoverageBased = 3,
    }

    /// <summary>
    /// ContextClosureLookupContext: the format and, per backtrack, input and lookahead, the class definition or subtable the values
    /// refer to, with the per-subtable caches HarfBuzz keeps.
    /// </summary>
    private sealed class RuleContext(ContextFormat format, int backtrackData, int inputData, int lookaheadData)
    {
        public ContextFormat Format { get; } = format;

        public int BacktrackData { get; } = backtrackData;

        public int InputData { get; } = inputData;

        public int LookaheadData { get; } = lookaheadData;

        public Dictionary<int, bool>[] IntersectsCaches { get; } = [[], [], []];

        public Dictionary<int, GlyphBitSet> IntersectedCache { get; } = [];
    }

    /// <summary>Context and chain context format 1: a rule set per first glyph.</summary>
    private void ContextFormat1(int s, bool chain)
    {
        ReadOnlySpan<byte> d = D;
        int coverage = Off16(d, s, s + 2);
        GlyphBitSet previous = ParentActive;
        IntersectCoverage(d, coverage, previous, PushActive());
        RuleContext context = new(ContextFormat.Simple, 0, 0, 0);
        List<uint> glyphs = CoverageGlyphs(d, coverage);
        int count = U16(d, s + 4);
        for (int i = 0; i < glyphs.Count && i < count; i++)
        {
            if (previous.Contains(glyphs[i]))
            {
                RuleSet(Off16(d, s, s + 6 + (i * 2)), glyphs[i], context, chain);
            }
        }

        PopActive();
    }

    private void ContextFormat2(int s)
    {
        ReadOnlySpan<byte> d = D;
        int coverage = Off16(d, s, s + 2);
        if (!CoverageIntersects(d, coverage, _glyphs))
        {
            return;
        }

        GlyphBitSet previous = ParentActive;
        GlyphBitSet active = PushActive();
        IntersectCoverage(d, coverage, previous, active);
        int classDef = Off16(d, s, s + 4);
        RuleContext context = new(ContextFormat.ClassBased, classDef, classDef, classDef);
        int count = U16(d, s + 6);
        for (int i = 0; i < count; i++)
        {
            if (ClassIntersects(d, classDef, active, i))
            {
                RuleSet(Off16(d, s, s + 8 + (i * 2)), (uint)i, context, false);
            }
        }

        PopActive();
    }

    private void ContextFormat3(int s)
    {
        ReadOnlySpan<byte> d = D;
        int coverage = Off16(d, s, s + 6);
        if (!CoverageIntersects(d, coverage, _glyphs))
        {
            return;
        }

        GlyphBitSet previous = ParentActive;
        IntersectCoverage(d, coverage, previous, PushActive());
        int glyphCount = U16(d, s + 2);
        int lookupCount = U16(d, s + 4);
        RuleContext context = new(ContextFormat.CoverageBased, s, s, s);
        CloseRule(0, 0, glyphCount, s + 8, 0, 0, lookupCount, s + 6 + (glyphCount * 2), 0, context);
        PopActive();
    }

    private void ChainContextFormat2(int s)
    {
        ReadOnlySpan<byte> d = D;
        int coverage = Off16(d, s, s + 2);
        if (!CoverageIntersects(d, coverage, _glyphs))
        {
            return;
        }

        GlyphBitSet previous = ParentActive;
        GlyphBitSet active = PushActive();
        IntersectCoverage(d, coverage, previous, active);
        int input = Off16(d, s, s + 6);
        RuleContext context = new(ContextFormat.ClassBased, Off16(d, s, s + 4), input, Off16(d, s, s + 8));
        int count = U16(d, s + 10);
        for (int i = 0; i < count; i++)
        {
            if (ClassIntersects(d, input, active, i))
            {
                RuleSet(Off16(d, s, s + 12 + (i * 2)), (uint)i, context, true);
            }
        }

        PopActive();
    }

    private void ChainContextFormat3(int s)
    {
        ReadOnlySpan<byte> d = D;
        int backtrack = U16(d, s + 2);
        int inputAt = s + 4 + (backtrack * 2);
        int input = U16(d, inputAt);
        int lookaheadAt = inputAt + 2 + (input * 2);
        int lookahead = U16(d, lookaheadAt);
        int lookupAt = lookaheadAt + 2 + (lookahead * 2);
        int coverage = input == 0 ? 0 : Off16(d, s, inputAt + 2);
        if (!CoverageIntersects(d, coverage, _glyphs))
        {
            return;
        }

        GlyphBitSet previous = ParentActive;
        IntersectCoverage(d, coverage, previous, PushActive());
        RuleContext context = new(ContextFormat.CoverageBased, s, s, s);
        CloseRule(backtrack, s + 4, input, inputAt + 4, lookahead, lookaheadAt + 2, U16(d, lookupAt), lookupAt + 2, 0, context);
        PopActive();
    }

    /// <summary>RuleSet and ChainRuleSet closure: each rule of the set.</summary>
    private void RuleSet(int set, uint value, RuleContext context, bool chain)
    {
        if (set == 0 || LookupLimitExceeded)
        {
            return;
        }

        ReadOnlySpan<byte> d = D;
        int count = U16(d, set);
        for (int i = 0; i < count; i++)
        {
            int rule = Off16(d, set, set + 2 + (i * 2));
            if (rule == 0 || LookupLimitExceeded)
            {
                continue;
            }

            if (!chain)
            {
                // Rule: input count (with the first glyph), lookup count, the input after the first, the lookup records.
                int inputCount = U16(d, rule);
                int records = rule + 4 + (Math.Max(inputCount - 1, 0) * 2);
                CloseRule(0, 0, inputCount, rule + 4, 0, 0, U16(d, rule + 2), records, value, context);
                continue;
            }

            int backtrack = U16(d, rule);
            int inputAt = rule + 2 + (backtrack * 2);
            int input = U16(d, inputAt);
            int lookaheadAt = inputAt + 2 + (Math.Max(input - 1, 0) * 2);
            int lookahead = U16(d, lookaheadAt);
            int lookupAt = lookaheadAt + 2 + (lookahead * 2);
            CloseRule(backtrack, rule + 2, input, inputAt + 2, lookahead, lookaheadAt + 2, U16(d, lookupAt), lookupAt + 2, value, context);
        }
    }

    /// <summary>
    /// context_closure_lookup and chain_context_closure_lookup: when every value of the backtrack, the input after the first and
    /// the lookahead can match a glyph of the set, the rule's lookups are followed.
    /// </summary>
    private void CloseRule(
        int backtrackCount,
        int backtrack,
        int inputCount,
        int input,
        int lookaheadCount,
        int lookahead,
        int lookupCount,
        int records,
        uint value,
        RuleContext context)
    {
        ReadOnlySpan<byte> d = D;
        if (!AllIntersect(d, backtrackCount, backtrack, context.BacktrackData, context, 0) ||
            !AllIntersect(d, Math.Max(inputCount - 1, 0), input, context.InputData, context, 1) ||
            !AllIntersect(d, lookaheadCount, lookahead, context.LookaheadData, context, 2))
        {
            return;
        }

        RecurseLookups(inputCount, input, lookupCount, records, value, context);
    }

    private bool AllIntersect(ReadOnlySpan<byte> d, int count, int values, int data, RuleContext context, int which)
    {
        for (int i = 0; i < count; i++)
        {
            int value = U16(d, values + (i * 2));
            bool intersects;
            switch (context.Format)
            {
                case ContextFormat.Simple:
                    intersects = _glyphs.Contains((uint)value);
                    break;
                case ContextFormat.ClassBased:
                    Dictionary<int, bool> cache = context.IntersectsCaches[which];
                    if (!cache.TryGetValue(value, out intersects))
                    {
                        cache[value] = intersects = ClassIntersects(d, data, _glyphs, value);
                    }

                    break;
                default:
                    intersects = CoverageIntersects(d, At(d, data, value), _glyphs);
                    break;
            }

            if (!intersects)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// context_closure_recurse_lookups: each lookup record's lookup is followed with the glyphs that can be at its position
    /// active, or every glyph once an earlier lookup of the rule may have changed the glyph count there.
    /// </summary>
    private void RecurseLookups(int inputCount, int input, int lookupCount, int records, uint value, RuleContext context)
    {
        ReadOnlySpan<byte> d = D;
        HashSet<int> coveredSequence = [];
        for (int i = 0; i < lookupCount; i++)
        {
            int sequenceIndex = U16(d, records + (i * 4));
            int lookupIndex = U16(d, records + (i * 4) + 2);
            if (sequenceIndex >= inputCount)
            {
                continue;
            }

            bool hasPositionGlyphs = !coveredSequence.Contains(sequenceIndex);
            coveredSequence.Add(sequenceIndex);
            GlyphBitSet parent = ParentActive;
            GlyphBitSet active = PushActive();
            if (!hasPositionGlyphs)
            {
                active.CopyFrom(_glyphs);
            }
            else if (sequenceIndex == 0)
            {
                switch (context.Format)
                {
                    case ContextFormat.Simple:
                        active.Add(value);
                        break;
                    case ContextFormat.ClassBased:
                        IntersectedClassGlyphs(d, context, parent, (int)value, active);
                        break;
                    default:
                        active.CopyFrom(parent);
                        break;
                }
            }
            else
            {
                int inputValue = U16(d, input + ((sequenceIndex - 1) * 2));
                switch (context.Format)
                {
                    case ContextFormat.Simple:
                        active.Add((uint)inputValue);
                        break;
                    case ContextFormat.ClassBased:
                        IntersectedClassGlyphs(d, context, _glyphs, inputValue, active);
                        break;
                    default:
                        IntersectCoverage(d, At(d, context.InputData, inputValue), _glyphs, active);
                        break;
                }
            }

            int endIndex = inputCount + (context.Format == ContextFormat.CoverageBased ? 1 : 0);
            Recurse(lookupIndex, coveredSequence, sequenceIndex, endIndex);
            PopActive();
        }
    }

    /// <summary>intersected_class_glyphs, cached per class for the subtable as HarfBuzz caches it (whatever set came first).</summary>
    private void IntersectedClassGlyphs(ReadOnlySpan<byte> d, RuleContext context, GlyphBitSet glyphs, int klass, GlyphBitSet result)
    {
        if (!context.IntersectedCache.TryGetValue(klass, out GlyphBitSet? cached))
        {
            cached = new GlyphBitSet(_glyphCount);
            ClassGlyphs(d, context.InputData, glyphs, klass, cached);
            context.IntersectedCache[klass] = cached;
        }

        result.UnionWith(cached);
    }

    /// <summary>The table at a 16-bit offset from <paramref name="table"/>; 0 (null) for a zero offset or one past the end.</summary>
    private static int At(ReadOnlySpan<byte> d, int table, int offset) =>
        offset == 0 || table + offset >= d.Length ? 0 : table + offset;

    /// <summary>The glyphs of a coverage table in the order HarfBuzz's iterator gives them, stopping at a broken range.</summary>
    private static List<uint> CoverageGlyphs(ReadOnlySpan<byte> d, int t)
    {
        List<uint> glyphs = [];
        if (t == 0)
        {
            return glyphs;
        }

        int format = U16(d, t);
        int count = U16(d, t + 2);
        if (format == 1)
        {
            for (int i = 0; i < count; i++)
            {
                glyphs.Add(U16(d, t + 4 + (i * 2)));
            }
        }
        else if (format == 2 && count > 0 && U16(d, t + 4) <= U16(d, t + 6))
        {
            uint coverage = 0;
            int i = 0;
            uint j = U16(d, t + 4);
            while (i < count)
            {
                glyphs.Add(j);
                int r = t + 4 + (i * 6);
                if (j >= U16(d, r + 2))
                {
                    i++;
                    if (i < count)
                    {
                        uint old = coverage;
                        r += 6;
                        j = U16(d, r);
                        coverage = U16(d, r + 4);
                        if (coverage != old + 1)
                        {
                            break;
                        }
                    }
                }
                else
                {
                    coverage++;
                    j++;
                }
            }
        }

        return glyphs;
    }

    private static uint CoveragePopulation(ReadOnlySpan<byte> d, int t)
    {
        if (t == 0)
        {
            return 0;
        }

        int format = U16(d, t);
        int count = U16(d, t + 2);
        if (format == 1)
        {
            return (uint)count;
        }

        long total = 0;
        for (int i = 0; format == 2 && i < count; i++)
        {
            int r = t + 4 + (i * 6);
            int first = U16(d, r), last = U16(d, r + 2);
            total += last < first ? 0 : last - first + 1;
        }

        return (uint)total;
    }

    private static bool CoverageIntersects(ReadOnlySpan<byte> d, int t, GlyphBitSet glyphs)
    {
        if (t == 0)
        {
            return false;
        }

        int format = U16(d, t);
        int count = U16(d, t + 2);
        for (int i = 0; i < count; i++)
        {
            if (format == 1 && glyphs.Contains(U16(d, t + 4 + (i * 2))))
            {
                return true;
            }

            if (format == 2 && glyphs.IntersectsRange(U16(d, t + 4 + (i * 6)), U16(d, t + 6 + (i * 6))))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Adds the coverage's glyphs that are in <paramref name="glyphs"/>; ranges stop at one that overlaps the last.</summary>
    private static void IntersectCoverage(ReadOnlySpan<byte> d, int t, GlyphBitSet glyphs, GlyphBitSet result)
    {
        if (t == 0)
        {
            return;
        }

        int format = U16(d, t);
        int count = U16(d, t + 2);
        if (format == 1)
        {
            for (int i = 0; i < count; i++)
            {
                uint g = U16(d, t + 4 + (i * 2));
                if (glyphs.Contains(g))
                {
                    result.Add(g);
                }
            }
        }
        else if (format == 2)
        {
            uint last = 0;
            for (int i = 0; i < count; i++)
            {
                int r = t + 4 + (i * 6);
                uint first = U16(d, r);
                if (first < last)
                {
                    break;
                }

                last = U16(d, r + 2);
                result.AddRange(glyphs, first, last);
            }
        }
    }

    /// <summary>ClassDef::intersects_class: whether a glyph of the set is in the class (class 0: any glyph the table leaves out).</summary>
    private static bool ClassIntersects(ReadOnlySpan<byte> d, int t, GlyphBitSet glyphs, int klass)
    {
        if (t == 0)
        {
            return false;
        }

        int format = U16(d, t);
        if (format == 1)
        {
            uint start = U16(d, t + 2);
            int count = U16(d, t + 4);
            if (klass == 0)
            {
                uint g = Invalid;
                if (!glyphs.Next(ref g))
                {
                    return false;
                }

                if (g < start)
                {
                    return true;
                }

                g = start + (uint)count - 1;
                if (glyphs.Next(ref g))
                {
                    return true;
                }
            }

            for (int i = 0; i < count; i++)
            {
                if (U16(d, t + 6 + (i * 2)) == klass && glyphs.Contains(start + (uint)i))
                {
                    return true;
                }
            }

            return false;
        }

        if (format != 2)
        {
            return false;
        }

        int ranges = U16(d, t + 2);
        if (klass == 0)
        {
            if (ranges == 0)
            {
                return glyphs.Count != 0;
            }

            // A glyph before a range, or after the last, is unlisted; HarfBuzz's walk skips a range that starts right after the
            // one its separate iterator stands on.
            uint g = Invalid;
            uint last = Invalid;
            int it = 0;
            for (int i = 0; i < ranges; i++)
            {
                if (U16(d, t + 4 + (it * 6)) == last + 1)
                {
                    it++;
                    continue;
                }

                if (!glyphs.Next(ref g))
                {
                    break;
                }

                int r = t + 4 + (i * 6);
                if (g < U16(d, r))
                {
                    return true;
                }

                g = U16(d, r + 2);
                last = g;
            }

            if (g != Invalid && glyphs.Next(ref g))
            {
                return true;
            }
        }

        for (int i = 0; i < ranges; i++)
        {
            int r = t + 4 + (i * 6);
            if (U16(d, r + 4) == klass && glyphs.IntersectsRange(U16(d, r), U16(d, r + 2)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>ClassDef::intersected_class_glyphs: adds the set's glyphs that are in the class.</summary>
    private static void ClassGlyphs(ReadOnlySpan<byte> d, int t, GlyphBitSet glyphs, int klass, GlyphBitSet result)
    {
        if (t == 0)
        {
            return;
        }

        int format = U16(d, t);
        if (format == 1)
        {
            uint start = U16(d, t + 2);
            int count = U16(d, t + 4);
            if (klass == 0)
            {
                for (uint g = Invalid; glyphs.Next(ref g) && g < start;)
                {
                    result.Add(g);
                }

                for (uint g = start + (uint)count - 1; glyphs.Next(ref g);)
                {
                    result.Add(g);
                }

                return;
            }

            for (int i = 0; i < count; i++)
            {
                if (U16(d, t + 6 + (i * 2)) == klass && glyphs.Contains(start + (uint)i))
                {
                    result.Add(start + (uint)i);
                }
            }

            return;
        }

        if (format != 2)
        {
            return;
        }

        int ranges = U16(d, t + 2);
        if (klass == 0)
        {
            uint g = Invalid;
            for (int i = 0; i < ranges; i++)
            {
                int r = t + 4 + (i * 6);
                if (!glyphs.Next(ref g))
                {
                    return;
                }

                while (g < U16(d, r))
                {
                    result.Add(g);
                    if (!glyphs.Next(ref g))
                    {
                        return;
                    }
                }

                g = U16(d, r + 2);
            }

            while (glyphs.Next(ref g))
            {
                result.Add(g);
            }

            return;
        }

        for (int i = 0; i < ranges; i++)
        {
            int r = t + 4 + (i * 6);
            if (U16(d, r + 4) == klass)
            {
                result.AddRange(glyphs, U16(d, r), U16(d, r + 2));
            }
        }
    }
}
