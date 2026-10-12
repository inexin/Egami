// Translated from HarfBuzz's hb-ot-shaper-indic.cc (MIT); see THIRD-PARTY-NOTICES.md.
using static Egami.Fonts.Shaping.IndicMachine;

namespace Egami.Fonts.Shaping;

/// <summary>
/// The Indic shaper, for Devanagari, Bengali, Gurmukhi, Gujarati, Oriya, Tamil, Telugu, Kannada and Malayalam: syllables by the
/// Indic machine, the initial reordering that finds each syllable's base and sets the basic feature masks, the basic features
/// one at a time, the final reordering of pre-base matras, reph and pre-base-reordering consonants, then the presentation
/// features per syllable.
/// </summary>
internal sealed class IndicShaper : ComplexShaper
{
    public static readonly IndicShaper Instance = new();

    // Visual positions in a syllable, left to right.
    private const byte PosStart = 0;
    private const byte PosRaToBecomeReph = 1;
    private const byte PosPreM = 2;
    private const byte PosPreC = 3;
    private const byte PosBaseC = 4;
    private const byte PosAfterMain = 5;
    private const byte PosBeforeSub = 7;
    private const byte PosBelowC = 8;
    private const byte PosAfterSub = 9;
    private const byte PosBeforePost = 10;
    private const byte PosPostC = 11;
    private const byte PosAfterPost = 12;
    private const byte PosSmvd = 13;
    private const byte PosEnd = 14;

    private const int MaxSyllableLength = 64;

    private enum RephMode
    {
        // Reph formed out of an initial Ra,H sequence.
        Implicit,

        // Reph formed out of an initial Ra,H,ZWJ sequence.
        Explicit,

        // An encoded repha character, which needs reordering.
        LogRepha,
    }

    private enum BlwfMode
    {
        // 'blwf' applies to pre-base and post-base consonants.
        PreAndPost,

        // 'blwf' applies to post-base consonants only.
        PostOnly,
    }

    private sealed record IndicConfig(string? Script, bool HasOldSpec, int Virama, byte RephPos, RephMode RephMode, BlwfMode BlwfMode);

    private static readonly IndicConfig[] s_configs =
    [
        // The default; first.
        new(null, false, 0, PosBeforePost, RephMode.Implicit, BlwfMode.PreAndPost),
        new("Deva", true, 0x094D, PosBeforePost, RephMode.Implicit, BlwfMode.PreAndPost),
        new("Beng", true, 0x09CD, PosAfterSub, RephMode.Implicit, BlwfMode.PreAndPost),
        new("Guru", true, 0x0A4D, PosBeforeSub, RephMode.Implicit, BlwfMode.PreAndPost),
        new("Gujr", true, 0x0ACD, PosBeforePost, RephMode.Implicit, BlwfMode.PreAndPost),
        new("Orya", true, 0x0B4D, PosAfterMain, RephMode.Implicit, BlwfMode.PreAndPost),
        new("Taml", true, 0x0BCD, PosAfterPost, RephMode.Implicit, BlwfMode.PreAndPost),
        new("Telu", true, 0x0C4D, PosAfterPost, RephMode.Explicit, BlwfMode.PostOnly),
        new("Knda", true, 0x0CCD, PosAfterPost, RephMode.Implicit, BlwfMode.PostOnly),
        new("Mlym", true, 0x0D4D, PosAfterMain, RephMode.LogRepha, BlwfMode.PreAndPost),
    ];

    // The features: the basic ones apply one at a time after the initial reordering, the others all at once after the final
    // reordering (fonts such as Windows' Bengali intermix the lookups of 'init', 'pres', 'abvs' and 'blws'); all are
    // constrained to the syllable.
    private static readonly (string Tag, FeatureFlags Flags)[] s_features =
    [
        ("nukt", FeatureFlags.GlobalManualJoiners | FeatureFlags.PerSyllable),
        ("akhn", FeatureFlags.GlobalManualJoiners | FeatureFlags.PerSyllable),
        ("rphf", FeatureFlags.ManualJoiners | FeatureFlags.PerSyllable),
        ("rkrf", FeatureFlags.GlobalManualJoiners | FeatureFlags.PerSyllable),
        ("pref", FeatureFlags.ManualJoiners | FeatureFlags.PerSyllable),
        ("blwf", FeatureFlags.ManualJoiners | FeatureFlags.PerSyllable),
        ("abvf", FeatureFlags.ManualJoiners | FeatureFlags.PerSyllable),
        ("half", FeatureFlags.ManualJoiners | FeatureFlags.PerSyllable),
        ("pstf", FeatureFlags.ManualJoiners | FeatureFlags.PerSyllable),
        ("vatu", FeatureFlags.GlobalManualJoiners | FeatureFlags.PerSyllable),
        ("cjct", FeatureFlags.GlobalManualJoiners | FeatureFlags.PerSyllable),

        ("init", FeatureFlags.ManualJoiners | FeatureFlags.PerSyllable),
        ("pres", FeatureFlags.GlobalManualJoiners | FeatureFlags.PerSyllable),
        ("abvs", FeatureFlags.GlobalManualJoiners | FeatureFlags.PerSyllable),
        ("blws", FeatureFlags.GlobalManualJoiners | FeatureFlags.PerSyllable),
        ("psts", FeatureFlags.GlobalManualJoiners | FeatureFlags.PerSyllable),
        ("haln", FeatureFlags.GlobalManualJoiners | FeatureFlags.PerSyllable),
    ];

    // Indexes into s_features.
    private const int Rphf = 2;
    private const int Pref = 4;
    private const int Blwf = 5;
    private const int Abvf = 6;
    private const int Half = 7;
    private const int Pstf = 8;
    private const int Init = 11;
    private const int NumFeatures = 17;
    private const int BasicFeatures = Init;

    // Vowels and placeholders count as consonants: vowels cannot occur in a consonant syllable, so the consonant syllable logic
    // serves vowel syllables too.
    private static readonly uint s_consonantFlags =
        Flag(ExC) | Flag(ExCS) | Flag(ExRa) | Flag(ExCM) | Flag(ExV) | Flag(ExPLACEHOLDER) | Flag(ExDOTTEDCIRCLE);

    private static readonly uint s_joinerFlags = Flag(ExZWJ) | Flag(ExZWNJ);

    /// <summary>The lookups of one basic feature's stage, for testing whether the font would substitute a sequence.</summary>
    private sealed class WouldSubstituteFeature
    {
        private readonly int[] _lookups;
        private readonly bool _zeroContext;

        public WouldSubstituteFeature(ShapeMap map, string tag, bool zeroContext)
        {
            _zeroContext = zeroContext;
            _lookups = GetStageLookups(map, 0, map.GetFeatureStage(0, ShapeMapBuilder.Tag(tag)));
        }

        public bool WouldSubstitute(ReadOnlySpan<uint> glyphs, LayoutTable gsub)
        {
            foreach (int lookup in _lookups)
            {
                if (LayoutApplier.WouldSubstitute(gsub, lookup, glyphs, _zeroContext))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>The lookups of a stage, as HarfBuzz's hb_ot_map_t::get_stage_lookups.</summary>
        private static int[] GetStageLookups(ShapeMap map, int tableIndex, int stage)
        {
            List<ShapeMap.StageMap> stages = map.Stages[tableIndex];
            List<ShapeMap.LookupMap> lookups = map.Lookups[tableIndex];
            if (stage > stages.Count)
            {
                return [];
            }

            int start = stage > 0 ? stages[stage - 1].LastLookup : 0;
            int end = stage < stages.Count ? stages[stage].LastLookup : lookups.Count;
            int[] indexes = new int[Math.Max(end - start, 0)];
            for (int i = 0; i < indexes.Length; i++)
            {
                indexes[i] = lookups[start + i].Index;
            }

            return indexes;
        }
    }

    private sealed class PlanData
    {
        public required IndicConfig Config;
        public bool IsOldSpec;

        // The virama's glyph, looked up on first use as planning has no font; -1 until then.
        public int ViramaGlyph = -1;

        public required WouldSubstituteFeature Rphf;
        public required WouldSubstituteFeature Pref;
        public required WouldSubstituteFeature Blwf;
        public required WouldSubstituteFeature Pstf;
        public required WouldSubstituteFeature Vatu;
        public readonly uint[] MaskArray = new uint[NumFeatures];

        public bool LoadViramaGlyph(ShapeFont font, out uint glyph)
        {
            int cached = Volatile.Read(ref ViramaGlyph);
            if (cached == -1)
            {
                // The spec says 'locl' should apply to the virama too; it doesn't.
                if (Config.Virama == 0 || !font.TryGetNominalGlyph(Config.Virama, out uint g))
                {
                    g = 0;
                }

                cached = (int)g;
                Volatile.Write(ref ViramaGlyph, cached);
            }

            glyph = (uint)cached;
            return glyph != 0;
        }
    }

    private static uint Flag(int x) => x < 32 ? 1u << x : 0;

    private static uint FlagRange(int x, int y) => Flag(y + 1) - Flag(x);

    private static bool IsOneOf(in GlyphInfo info, uint flags)
    {
        // If it ligated, all bets are off.
        if ((info.GlyphProps & GlyphProps.Ligated) != 0)
        {
            return false;
        }

        return (Flag(info.ShaperCategory) & flags) != 0;
    }

    private static bool IsConsonant(in GlyphInfo info) => IsOneOf(info, s_consonantFlags);

    private static bool IsJoiner(in GlyphInfo info) => IsOneOf(info, s_joinerFlags);

    private static bool IsHalant(in GlyphInfo info) => IsOneOf(info, Flag(ExH));

    private static bool LigatedAndDidntMultiply(in GlyphInfo info) =>
        (info.GlyphProps & GlyphProps.Ligated) != 0 && (info.GlyphProps & GlyphProps.Multiplied) == 0;

    /// <summary>The end of the syllable that starts at <paramref name="start"/>, at most 64 glyphs on, as HarfBuzz's _hb_next_syllable.</summary>
    internal static int NextSyllable(LayoutBuffer buffer, int start)
    {
        if (start >= buffer.Len)
        {
            return start;
        }

        GlyphInfo[] info = buffer.Info;
        int count = start + Math.Min(buffer.Len - start, MaxSyllableLength);
        byte syllable = info[start].Syllable;
        while (++start < count && syllable == info[start].Syllable)
        {
        }

        return start;
    }

    public override void CollectFeatures(ShapePlanner planner)
    {
        ShapeMapBuilder map = planner.Map;

        // Before any lookups apply.
        map.AddGsubPause(SetupSyllables);

        map.EnableFeature("locl", FeatureFlags.PerSyllable);

        // The Indic specs do not require 'ccmp', but where fonts use it, it typically comes first.
        map.EnableFeature("ccmp", FeatureFlags.PerSyllable);

        int i = 0;
        map.AddGsubPause(InitialReordering);

        for (; i < BasicFeatures; i++)
        {
            map.AddFeature(s_features[i].Tag, s_features[i].Flags);
            map.AddGsubPause(null);
        }

        map.AddGsubPause(FinalReordering);

        for (; i < NumFeatures; i++)
        {
            map.AddFeature(s_features[i].Tag, s_features[i].Flags);
        }
    }

    public override void OverrideFeatures(ShapePlanner planner)
    {
        planner.Map.DisableFeature("liga");
        planner.Map.AddGsubPause(Syllabic.ClearSyllables);
    }

    public override object? CreateData(ShapePlan plan)
    {
        IndicConfig config = s_configs[0];
        for (int i = 1; i < s_configs.Length; i++)
        {
            if (plan.Script.Code == s_configs[i].Script)
            {
                config = s_configs[i];
                break;
            }
        }

        bool isOldSpec = config.HasOldSpec && (plan.Map.ChosenScript[0] & 0xFF) != '2';

        // Zero-context matching for the new spec of the main Indic scripts and for scripts with one spec, not for the old specs.
        // Windows allows context in both Malayalam specs, but not in new-spec Bengali; only change this to follow Windows.
        bool zeroContext = !isOldSpec && plan.Script.Code != "Mlym";
        PlanData data = new()
        {
            Config = config,
            IsOldSpec = isOldSpec,
            Rphf = new WouldSubstituteFeature(plan.Map, "rphf", zeroContext),
            Pref = new WouldSubstituteFeature(plan.Map, "pref", zeroContext),
            Blwf = new WouldSubstituteFeature(plan.Map, "blwf", zeroContext),
            Pstf = new WouldSubstituteFeature(plan.Map, "pstf", zeroContext),
            Vatu = new WouldSubstituteFeature(plan.Map, "vatu", zeroContext),
        };

        for (int i = 0; i < NumFeatures; i++)
        {
            data.MaskArray[i] = (s_features[i].Flags & FeatureFlags.Global) != 0 ? 0 : plan.Map.Get1Mask(ShapeMapBuilder.Tag(s_features[i].Tag));
        }

        return data;
    }

    public override void PreprocessText(ShapePlan plan, LayoutBuffer buffer, ShapeFont font) => VowelConstraints.Preprocess(buffer, plan.Script.Code);

    public override NormalizationMode NormalizationPreference => NormalizationMode.ComposedDiacriticsNoShortCircuit;

    public override bool HasDecompose => true;

    public override bool Decompose(NormalizeContext c, int ab, out int a, out int b)
    {
        switch (ab)
        {
            // Don't decompose these.
            case 0x0931: // Devanagari letter rra.
            case 0x09DC: // Bengali letter rra (https://github.com/harfbuzz/harfbuzz/issues/779).
            case 0x09DD: // Bengali letter rha.
            case 0x0B94: // Tamil letter au.
                a = 0;
                b = 0;
                return false;
        }

        return NormalizeContext.DecomposeUnicode(ab, out a, out b);
    }

    public override bool HasCompose => true;

    public override bool Compose(NormalizeContext c, int a, int b, out int ab)
    {
        // Avoid recomposing split matras.
        if (UnicodeProps.IsMarkCategory(UnicodeProps.Category(a)))
        {
            ab = 0;
            return false;
        }

        // A composition exclusion to recompose.
        if (a == 0x09AF && b == 0x09BC)
        {
            ab = 0x09DF;
            return true;
        }

        return NormalizeContext.ComposeUnicode(a, b, out ab);
    }

    public override void SetupMasks(ShapePlan plan, LayoutBuffer buffer, ShapeFont font)
    {
        // The masks are set up later, in a pause; here each character gets its category and position.
        GlyphInfo[] info = buffer.Info;
        for (int i = 0; i < buffer.Len; i++)
        {
            ushort type = IndicTables.GetCategories((int)info[i].Codepoint);
            info[i].ShaperCategory = (byte)(type & 0xFF);
            info[i].ShaperPosition = (byte)(type >> 8);
        }
    }

    public override ZeroWidthMarks ZeroWidthMarks => ZeroWidthMarks.None;

    public override bool FallbackPosition => false;

    private static bool SetupSyllables(ShapePlan plan, ShapeFont font, LayoutBuffer buffer)
    {
        FindSyllables(buffer);
        for (int start = 0, end = NextSyllable(buffer, 0); start < buffer.Len; start = end, end = NextSyllable(buffer, start))
        {
            buffer.UnsafeToBreak(start, end);
        }

        return false;
    }

    private static void UpdateConsonantPositions(PlanData data, ShapeFont font, LayoutBuffer buffer)
    {
        if (!data.LoadViramaGlyph(font, out uint virama))
        {
            return;
        }

        LayoutTable gsub = font.Layout.Gsub;
        GlyphInfo[] info = buffer.Info;
        for (int i = 0; i < buffer.Len; i++)
        {
            if (info[i].ShaperPosition == PosBaseC)
            {
                info[i].ShaperPosition = ConsonantPositionFromFace(data, info[i].Codepoint, virama, gsub);
            }
        }
    }

    private static byte ConsonantPositionFromFace(PlanData data, uint consonant, uint virama, LayoutTable gsub)
    {
        // The old spec orders the glyphs Consonant,Virama and the new spec Virama,Consonant, but some fonts (FreeSans) copied
        // old-spec lookups into the new-spec script and Uniscribe respects them (it finds the base at 0 in U+0924,U+094D,U+0930),
        // so both orders match; 'vatu' as well (https://github.com/harfbuzz/harfbuzz/issues/1587).
        ReadOnlySpan<uint> glyphs = [virama, consonant, virama];
        if (data.Blwf.WouldSubstitute(glyphs[..2], gsub) ||
            data.Blwf.WouldSubstitute(glyphs[1..], gsub) ||
            data.Vatu.WouldSubstitute(glyphs[..2], gsub) ||
            data.Vatu.WouldSubstitute(glyphs[1..], gsub))
        {
            return PosBelowC;
        }

        if (data.Pstf.WouldSubstitute(glyphs[..2], gsub) ||
            data.Pstf.WouldSubstitute(glyphs[1..], gsub))
        {
            return PosPostC;
        }

        if (data.Pref.WouldSubstitute(glyphs[..2], gsub) ||
            data.Pref.WouldSubstitute(glyphs[1..], gsub))
        {
            return PosPostC;
        }

        return PosBaseC;
    }

    // ---- Initial reordering ----

    private static bool InitialReordering(ShapePlan plan, ShapeFont font, LayoutBuffer buffer)
    {
        PlanData data = (PlanData)plan.ShaperData!;
        UpdateConsonantPositions(data, font, buffer);
        bool ret = Syllabic.InsertDottedCircles(font, buffer, BrokenCluster, ExDOTTEDCIRCLE, ExRepha, PosEnd);

        LayoutTable gsub = font.Layout.Gsub;
        for (int start = 0, end = NextSyllable(buffer, 0); start < buffer.Len; start = end, end = NextSyllable(buffer, start))
        {
            switch (buffer.Info[start].Syllable & 0x0F)
            {
                // Vowels look like consonants and the broken clusters have their dotted circles, so all four take the
                // consonant syllable logic.
                case VowelSyllable:
                case ConsonantSyllable:
                case BrokenCluster:
                case StandaloneCluster:
                    InitialReorderingConsonantSyllable(plan, data, gsub, buffer, start, end);
                    break;
            }
        }

        return ret;
    }

    // The rules of https://docs.microsoft.com/en-us/typography/script-development/devanagari.
    private static void InitialReorderingConsonantSyllable(ShapePlan plan, PlanData data, LayoutTable gsub, LayoutBuffer buffer, int start, int end)
    {
        GlyphInfo[] info = buffer.Info;
        string script = plan.Script.Code;

        // For compatibility with legacy usage in Kannada, Ra+H+ZWJ behaves as Ra+ZWJ+H
        // (https://github.com/harfbuzz/harfbuzz/issues/435#issuecomment-335560167).
        if (script == "Knda" &&
            start + 3 <= end &&
            IsOneOf(info[start], Flag(ExRa)) &&
            IsOneOf(info[start + 1], Flag(ExH)) &&
            IsOneOf(info[start + 2], Flag(ExZWJ)))
        {
            buffer.MergeClusters(start + 1, start + 3);
            (info[start + 1], info[start + 2]) = (info[start + 2], info[start + 1]);
        }

        // 1. Find the base consonant: from the end of the syllable, move back to a consonant that has no below-base or post-base
        // form (post-base forms follow below-base forms) and is not a pre-base-reordering Ra, or to the first consonant. If the
        // syllable starts with Ra + Halant (in a script with reph) and has more than one consonant, the Ra is no candidate.
        int baseIndex = end;
        bool hasReph = false;

        int limit = start;
        if (data.MaskArray[Rphf] != 0 &&
            start + 3 <= end &&
            ((data.Config.RephMode == RephMode.Implicit && !IsJoiner(info[start + 2])) ||
             (data.Config.RephMode == RephMode.Explicit && info[start + 2].ShaperCategory == ExZWJ)))
        {
            // See whether it matches 'rphf'.
            ReadOnlySpan<uint> glyphs =
            [
                info[start].Codepoint,
                info[start + 1].Codepoint,
                data.Config.RephMode == RephMode.Explicit ? info[start + 2].Codepoint : 0,
            ];
            if (data.Rphf.WouldSubstitute(glyphs[..2], gsub) ||
                (data.Config.RephMode == RephMode.Explicit && data.Rphf.WouldSubstitute(glyphs, gsub)))
            {
                limit += 2;
                while (limit < end && IsJoiner(info[limit]))
                {
                    limit++;
                }

                baseIndex = start;
                hasReph = true;
            }
        }
        else if (data.Config.RephMode == RephMode.LogRepha && info[start].ShaperCategory == ExRepha)
        {
            limit += 1;
            while (limit < end && IsJoiner(info[limit]))
            {
                limit++;
            }

            baseIndex = start;
            hasReph = true;
        }

        {
            int i = end;
            bool seenBelow = false;
            do
            {
                i--;
                if (IsConsonant(info[i]))
                {
                    // A consonant without a below-base or post-base form (post-base forms follow below-base forms).
                    if (info[i].ShaperPosition != PosBelowC &&
                        (info[i].ShaperPosition != PosPostC || seenBelow))
                    {
                        baseIndex = i;
                        break;
                    }

                    if (info[i].ShaperPosition == PosBelowC)
                    {
                        seenBelow = true;
                    }

                    // Pre-base-reordering Ra's are marked PosPostC, so the test above skips them already. Otherwise the first
                    // consonant reached is the base.
                    baseIndex = i;
                }
                else
                {
                    // A ZWJ after a Halant stops the base search and requests an explicit half form. A ZWJ before a Halant
                    // requests a subjoined form instead, so the search goes on; Bengali Ra,H,Ya forms Ya-phalaa that way.
                    if (start < i &&
                        info[i].ShaperCategory == ExZWJ &&
                        info[i - 1].ShaperCategory == ExH)
                    {
                        break;
                    }
                }
            }
            while (i > limit);
        }

        // Only an unforced reph (not Ra,H,ZWJ) needs another consonant: with none, there is no reph and the Ra is the base.
        if (hasReph && baseIndex == start && limit - baseIndex <= 2)
        {
            hasReph = false;
        }

        // 2. Decompose and reorder matras: normalization has decomposed them already.
        // 3. Reorder marks to canonical order: normalization has done that too.

        // Reorder characters.
        for (int i = start; i < baseIndex; i++)
        {
            info[i].ShaperPosition = Math.Min(PosPreC, info[i].ShaperPosition);
        }

        if (baseIndex < end)
        {
            info[baseIndex].ShaperPosition = PosBaseC;
        }

        // The beginning Ra.
        if (hasReph)
        {
            info[start].ShaperPosition = PosRaToBecomeReph;
        }

        // For the old-style script tags, move the first post-base Halant after the last consonant. Uniscribe does this only
        // without a Halant after the last consonant already in Kannada (U+0C9A,U+0CCD,U+0C9A,U+0CCD with Lohit Kannada), but
        // unconditionally in Malayalam, Bengali and Devanagari; other scripts are unknown, so only Kannada is blocked.
        if (data.IsOldSpec)
        {
            bool disallowDoubleHalants = script == "Knda";
            for (int i = baseIndex + 1; i < end; i++)
            {
                if (info[i].ShaperCategory == ExH)
                {
                    int j;
                    for (j = end - 1; j > i; j--)
                    {
                        if (IsConsonant(info[j]) ||
                            (disallowDoubleHalants && info[j].ShaperCategory == ExH))
                        {
                            break;
                        }
                    }

                    if (info[j].ShaperCategory != ExH && j > i)
                    {
                        // Move the Halant to after the last consonant.
                        GlyphInfo t = info[i];
                        Array.Copy(info, i + 1, info, i, j - i);
                        info[j] = t;
                    }

                    break;
                }
            }
        }

        // Attach miscellaneous marks to the previous character to move with it.
        {
            byte lastPos = PosStart;
            uint attached = s_joinerFlags | Flag(ExN) | Flag(ExRS) | Flag(ExCM) | Flag(ExH);
            for (int i = start; i < end; i++)
            {
                if ((Flag(info[i].ShaperCategory) & attached) != 0)
                {
                    info[i].ShaperPosition = lastPos;
                    if (info[i].ShaperCategory == ExH && info[i].ShaperPosition == PosPreM)
                    {
                        // Uniscribe doesn't move the Halant with a left matra (U+092B,U+093F,U+094D); neither do we.
                        for (int j = i; j > start; j--)
                        {
                            if (info[j - 1].ShaperPosition != PosPreM)
                            {
                                info[i].ShaperPosition = info[j - 1].ShaperPosition;
                                break;
                            }
                        }
                    }
                }
                else if (info[i].ShaperPosition != PosSmvd)
                {
                    if (info[i].ShaperCategory == ExMPst &&
                        i > start && info[i - 1].ShaperCategory == ExSM)
                    {
                        info[i - 1].ShaperPosition = info[i].ShaperPosition;
                    }

                    lastPos = info[i].ShaperPosition;
                }
            }
        }

        // Post-base consonants own what comes before them since the last consonant or matra.
        {
            int last = baseIndex;
            for (int i = baseIndex + 1; i < end; i++)
            {
                if (IsConsonant(info[i]))
                {
                    for (int j = last + 1; j < i; j++)
                    {
                        if (info[j].ShaperPosition < PosSmvd)
                        {
                            info[j].ShaperPosition = info[i].ShaperPosition;
                        }
                    }

                    last = i;
                }
                else if ((Flag(info[i].ShaperCategory) & (Flag(ExM) | Flag(ExMPst))) != 0)
                {
                    last = i;
                }
            }
        }

        {
            // The syllable field keeps each glyph's original place during the sort.
            byte syllable = info[start].Syllable;
            for (int i = start; i < end; i++)
            {
                info[i].Syllable = (byte)(i - start);
            }

            StableSort(info, start, end);

            // Find the base again; also flip a left-matra sequence.
            int firstLeftMatra = end;
            int lastLeftMatra = end;
            baseIndex = end;
            for (int i = start; i < end; i++)
            {
                if (info[i].ShaperPosition == PosBaseC)
                {
                    baseIndex = i;
                    break;
                }
                else if (info[i].ShaperPosition == PosPreM)
                {
                    if (firstLeftMatra == end)
                    {
                        firstLeftMatra = i;
                    }

                    lastLeftMatra = i;
                }
            }

            // https://github.com/harfbuzz/harfbuzz/issues/3863
            if (firstLeftMatra < lastLeftMatra)
            {
                // The clusters merge later.
                buffer.ReverseRange(firstLeftMatra, lastLeftMatra + 1);

                // Reverse the nuktas and such back.
                int i = firstLeftMatra;
                for (int j = i; j <= lastLeftMatra; j++)
                {
                    if ((Flag(info[j].ShaperCategory) & (Flag(ExM) | Flag(ExMPst))) != 0)
                    {
                        buffer.ReverseRange(i, j + 1);
                        i = j + 1;
                    }
                }
            }

            // Post-base glyphs may have shuffled around. The old spec moves halants, so it merges every cluster after the base;
            // otherwise the clusters merge as the sort order requires. Nothing before the base merges here, as the final
            // reordering still moves pre-base glyphs (a left matra goes closer to the base in C,H,ZWNJ,B,M) and merges their
            // clusters up to the base; the merges from both sides interlock (https://github.com/harfbuzz/harfbuzz/issues/2272).
            if (data.IsOldSpec || end - start > 127)
            {
                buffer.MergeClusters(baseIndex, end);
            }
            else
            {
                for (int i = baseIndex; i < end; i++)
                {
                    if (info[i].Syllable != 255)
                    {
                        int min = i;
                        int max = i;
                        int j = start + info[i].Syllable;
                        while (j != i)
                        {
                            min = Math.Min(min, j);
                            max = Math.Max(max, j);
                            int next = start + info[j].Syllable;
                            info[j].Syllable = 255; // So j isn't processed again.
                            j = next;
                        }

                        buffer.MergeClusters(Math.Max(baseIndex, min), max + 1);
                    }
                }
            }

            // Put the syllable back.
            for (int i = start; i < end; i++)
            {
                info[i].Syllable = syllable;
            }
        }

        // Set up the masks now.
        {
            // Reph.
            for (int i = start; i < end && info[i].ShaperPosition == PosRaToBecomeReph; i++)
            {
                info[i].Mask |= data.MaskArray[Rphf];
            }

            // Pre-base.
            uint mask = data.MaskArray[Half];
            if (!data.IsOldSpec && data.Config.BlwfMode == BlwfMode.PreAndPost)
            {
                mask |= data.MaskArray[Blwf];
            }

            for (int i = start; i < baseIndex; i++)
            {
                info[i].Mask |= mask;
            }

            // Post-base; the base itself gets nothing.
            mask = data.MaskArray[Blwf] | data.MaskArray[Abvf] | data.MaskArray[Pstf];
            for (int i = baseIndex + 1; i < end; i++)
            {
                info[i].Mask |= mask;
            }
        }

        if (data.IsOldSpec && script == "Deva")
        {
            // The old-spec eyelash Ra: 'blwf' applies to the vattu below half forms as well as below the base (U+0924,U+094D,
            // U+0930,U+094D,U+0915 with Sanskrit 2003). Ra,Halant,ZWJ is the way to request the eyelash form, so it is left
            // alone (U+0924,U+094D,U+0930,U+094D,U+200D,U+0915).
            for (int i = start; i + 1 < baseIndex; i++)
            {
                if (info[i].ShaperCategory == ExRa &&
                    info[i + 1].ShaperCategory == ExH &&
                    (i + 2 == baseIndex ||
                     info[i + 2].ShaperCategory != ExZWJ))
                {
                    info[i].Mask |= data.MaskArray[Blwf];
                    info[i + 1].Mask |= data.MaskArray[Blwf];
                }
            }
        }

        const int prefLen = 2;
        if (data.MaskArray[Pref] != 0 && baseIndex + prefLen < end)
        {
            // Find a Halant,Ra sequence and mark it for pre-base-reordering.
            Span<uint> glyphs = stackalloc uint[prefLen];
            for (int i = baseIndex + 1; i + prefLen - 1 < end; i++)
            {
                for (int j = 0; j < prefLen; j++)
                {
                    glyphs[j] = info[i + j].Codepoint;
                }

                if (data.Pref.WouldSubstitute(glyphs, gsub))
                {
                    for (int j = 0; j < prefLen; j++)
                    {
                        info[i++].Mask |= data.MaskArray[Pref];
                    }

                    break;
                }
            }
        }

        // The effects of ZWJ and ZWNJ.
        for (int i = start + 1; i < end; i++)
        {
            if (IsJoiner(info[i]))
            {
                bool nonJoiner = info[i].ShaperCategory == ExZWNJ;
                int j = i;
                do
                {
                    j--;

                    // ZWJ and ZWNJ disable 'cjct' just by being there, as it does not skip them; a ZWNJ disables 'half'.
                    if (nonJoiner)
                    {
                        info[j].Mask &= ~data.MaskArray[Half];
                    }
                }
                while (j > start && !IsConsonant(info[j]));
            }
        }
    }

    /// <summary>A stable sort by position, as HarfBuzz's hb_stable_sort; the clusters are left alone.</summary>
    private static void StableSort(GlyphInfo[] info, int start, int end)
    {
        for (int i = start + 1; i < end; i++)
        {
            int j = i;
            GlyphInfo t = info[i];
            while (j > start && info[j - 1].ShaperPosition > t.ShaperPosition)
            {
                j--;
            }

            if (i == j)
            {
                continue;
            }

            Array.Copy(info, j, info, j + 1, i - j);
            info[j] = t;
        }
    }

    // ---- Final reordering ----

    private static bool FinalReordering(ShapePlan plan, ShapeFont font, LayoutBuffer buffer)
    {
        if (buffer.Len == 0)
        {
            return false;
        }

        PlanData data = (PlanData)plan.ShaperData!;
        for (int start = 0, end = NextSyllable(buffer, 0); start < buffer.Len; start = end, end = NextSyllable(buffer, start))
        {
            FinalReorderingSyllable(plan, data, buffer, start, end);
        }

        return false;
    }

    private static void FinalReorderingSyllable(ShapePlan plan, PlanData data, LayoutBuffer buffer, int start, int end)
    {
        GlyphInfo[] info = buffer.Info;
        string script = plan.Script.Code;

        // This relies on halant glyphs, whose properties ligation and multiple substitution may have lost: recover the case
        // where a Halant is fairly surely meant. The virama glyph is loaded already.
        int viramaGlyph = Volatile.Read(ref data.ViramaGlyph);
        if (viramaGlyph != 0)
        {
            for (int i = start; i < end; i++)
            {
                if (info[i].Codepoint == (uint)viramaGlyph &&
                    (info[i].GlyphProps & GlyphProps.Ligated) != 0 &&
                    (info[i].GlyphProps & GlyphProps.Multiplied) != 0)
                {
                    // So it passes IsHalant.
                    info[i].ShaperCategory = ExH;
                    info[i].GlyphProps &= unchecked((ushort)~(GlyphProps.Ligated | GlyphProps.Multiplied));
                }
            }
        }

        // 4. Final reordering, after the localized and basic shaping features and before the rest apply to the whole syllable.
        bool tryPref = data.MaskArray[Pref] != 0;

        // Find the base again.
        int baseIndex;
        for (baseIndex = start; baseIndex < end; baseIndex++)
        {
            if (info[baseIndex].ShaperPosition >= PosBaseC)
            {
                if (tryPref && baseIndex + 1 < end)
                {
                    for (int i = baseIndex + 1; i < end; i++)
                    {
                        if ((info[i].Mask & data.MaskArray[Pref]) != 0)
                        {
                            if (!((info[i].GlyphProps & GlyphProps.Substituted) != 0 && LigatedAndDidntMultiply(info[i])))
                            {
                                // A 'pref' candidate that formed nothing: the base is around here.
                                baseIndex = i;
                                while (baseIndex < end && IsHalant(info[baseIndex]))
                                {
                                    baseIndex++;
                                }

                                if (baseIndex < end)
                                {
                                    info[baseIndex].ShaperPosition = PosBaseC;
                                }

                                tryPref = false;
                            }

                            break;
                        }
                    }

                    if (baseIndex == end)
                    {
                        break;
                    }
                }

                // Malayalam skips over unformed below (but not post) forms.
                if (script == "Mlym")
                {
                    for (int i = baseIndex + 1; i < end; i++)
                    {
                        while (i < end && IsJoiner(info[i]))
                        {
                            i++;
                        }

                        if (i == end || !IsHalant(info[i]))
                        {
                            break;
                        }

                        i++; // The halant.
                        while (i < end && IsJoiner(info[i]))
                        {
                            i++;
                        }

                        if (i < end && IsConsonant(info[i]) && info[i].ShaperPosition == PosBelowC)
                        {
                            baseIndex = i;
                            info[baseIndex].ShaperPosition = PosBaseC;
                        }
                    }
                }

                if (start < baseIndex && info[baseIndex].ShaperPosition > PosBaseC)
                {
                    baseIndex--;
                }

                break;
            }
        }

        if (baseIndex == end && start < baseIndex &&
            IsOneOf(info[baseIndex - 1], Flag(ExZWJ)))
        {
            baseIndex--;
        }

        if (baseIndex < end)
        {
            while (start < baseIndex &&
                IsOneOf(info[baseIndex], Flag(ExN) | Flag(ExH)))
            {
                baseIndex--;
            }
        }

        // Reorder matras: a pre-base matra reordered before the basic features moves closer to the main consonant, depending on
        // the half forms formed: after the last standalone halant glyph, after the initial matra position and before the main
        // consonant. Windows 7 shows that a ZWJ after the halant keeps the matra from moving after it and a ZWNJ lets it
        // (U+091F,U+094D,U+200C,U+092F,U+093F moves to the middle, U+091F,U+094D,U+200D,U+092F,U+093F does not;
        // https://github.com/harfbuzz/harfbuzz/issues/1070).
        if (start + 1 < end && start < baseIndex)
        {
            // Having lost track of the base, the position is before the last glyph.
            int newPos = baseIndex == end ? baseIndex - 2 : baseIndex - 1;

            // Malayalam and Tamil have no half forms or explicit virama forms: what 'half' forms there are chillus or ligated
            // explicit viramas, and the matra goes after them.
            if (script != "Mlym" && script != "Taml")
            {
                while (true)
                {
                    while (newPos > start &&
                        !IsOneOf(info[newPos], Flag(ExM) | Flag(ExMPst) | Flag(ExH)))
                    {
                        newPos--;
                    }

                    // Without a Halant, no move; otherwise only go on if the Halant does not belong to the matra itself.
                    if (IsHalant(info[newPos]) &&
                        info[newPos].ShaperPosition != PosPreM)
                    {
                        // A ZWJ after this halant keeps the matra from moving after it: keep searching. A ZWNJ ends the syllable
                        // in the machine, so a matra after it belongs to the next one.
                        if (newPos + 1 < end && info[newPos + 1].ShaperCategory == ExZWJ && newPos > start)
                        {
                            newPos--;
                            continue;
                        }
                    }
                    else
                    {
                        newPos = start;
                    }

                    break;
                }
            }

            if (start < newPos && info[newPos].ShaperPosition != PosPreM)
            {
                // See whether there are matras at all.
                for (int i = newPos; i > start; i--)
                {
                    if (info[i - 1].ShaperPosition == PosPreM)
                    {
                        int oldPos = i - 1;
                        if (oldPos < baseIndex && baseIndex <= newPos)
                        {
                            // Shouldn't actually happen.
                            baseIndex--;
                        }

                        GlyphInfo tmp = info[oldPos];
                        Array.Copy(info, oldPos + 1, info, oldPos, newPos - oldPos);
                        info[newPos] = tmp;

                        // The clusters merge after the reordering, as Indic matra reordering is special.
                        buffer.MergeClusters(newPos, Math.Min(end, baseIndex + 1));

                        newPos--;
                    }
                }
            }
            else
            {
                for (int i = start; i < baseIndex; i++)
                {
                    if (info[i].ShaperPosition == PosPreM)
                    {
                        buffer.MergeClusters(i, Math.Min(end, baseIndex + 1));
                        break;
                    }
                }
            }
        }

        // Reorder reph: it starts the syllable and moves by the results of the basic forms, after the main consonant, before
        // the post-base forms or after them, by script. A reph encoded as Ra,H or Ra,H,ZWJ moves only if it ligated into the
        // reph form; an encoded repha moves only if it did not ligate, as then the font probably handles it itself.
        if (start + 1 < end &&
            info[start].ShaperPosition == PosRaToBecomeReph &&
            ((info[start].ShaperCategory == ExRepha) ^ LigatedAndDidntMultiply(info[start])))
        {
            int newRephPos = FindRephPosition(data, info, start, end, baseIndex);

            buffer.MergeClusters(start, newRephPos + 1);
            GlyphInfo reph = info[start];
            Array.Copy(info, start + 1, info, start, newRephPos - start);
            info[newRephPos] = reph;

            if (start < baseIndex && baseIndex <= newRephPos)
            {
                baseIndex--;
            }
        }

        // Reorder pre-base-reordering consonants.
        if (tryPref && baseIndex + 1 < end)
        {
            for (int i = baseIndex + 1; i < end; i++)
            {
                if ((info[i].Mask & data.MaskArray[Pref]) != 0)
                {
                    // 1. Only a glyph 'pref' substituted reorders (a font may block the feature in some contexts); that it
                    // ligated is what is checked.
                    if (LigatedAndDidntMultiply(info[i]))
                    {
                        // 2. Find the target as for a pre-base matra; 3. without one, go before the main consonant.
                        int newPos = baseIndex;

                        // Malayalam and Tamil: after the chillus and ligated explicit viramas, as for matras.
                        if (script != "Mlym" && script != "Taml")
                        {
                            while (newPos > start &&
                                !IsOneOf(info[newPos - 1], Flag(ExM) | Flag(ExMPst) | Flag(ExH)))
                            {
                                newPos--;
                            }
                        }

                        if (newPos > start && IsHalant(info[newPos - 1]))
                        {
                            // After a ZWJ or ZWNJ following the halant.
                            if (newPos < end && IsJoiner(info[newPos]))
                            {
                                newPos++;
                            }
                        }

                        int oldPos = i;
                        buffer.MergeClusters(newPos, oldPos + 1);
                        GlyphInfo tmp = info[oldPos];
                        Array.Copy(info, newPos, info, newPos + 1, oldPos - newPos);
                        info[newPos] = tmp;

                        if (newPos <= baseIndex && baseIndex < oldPos)
                        {
                            baseIndex++;
                        }
                    }

                    break;
                }
            }
        }

        // 'init' applies to a left matra at a word start.
        if (info[start].ShaperPosition == PosPreM)
        {
            if (start == 0 ||
                (Flag(UnicodeProps.GeneralCategory(info[start - 1])) & FlagRange(UnicodeProps.Format, UnicodeProps.NonspacingMark)) == 0)
            {
                info[start].Mask |= data.MaskArray[Init];
            }
            else
            {
                buffer.UnsafeToBreak(start - 1, start + 1);
            }
        }
    }

    /// <summary>Where the reph goes, by the script's reph position class.</summary>
    private static int FindRephPosition(PlanData data, GlyphInfo[] info, int start, int end, int baseIndex)
    {
        byte rephPos = data.Config.RephPos;
        int newRephPos;

        // 1. A reph after the post-base consonant forms goes to step 5.
        if (rephPos != PosAfterPost)
        {
            // 2. Otherwise the target is after the first explicit halant glyph between the first post-reph consonant and the last
            // main consonant, or after a ZWJ or ZWNJ following it. (Old-implementation fonts never find a position here.)
            if (TryAfterHalant(info, start, baseIndex, out newRephPos))
            {
                return newRephPos;
            }

            // 3. After the main consonant: the first consonant not ligated with the main one, or not a potential
            // pre-base-reordering Ra.
            if (rephPos == PosAfterMain)
            {
                newRephPos = baseIndex;
                while (newRephPos + 1 < end && info[newRephPos + 1].ShaperPosition <= PosAfterMain)
                {
                    newRephPos++;
                }

                if (newRephPos < end)
                {
                    return newRephPos;
                }
            }

            // 4. Before the post-base consonants: the first post-base classified consonant not ligated with the main one, or
            // before the first matra, syllable modifier or vedic sign. This is HarfBuzz's take on what the step means.
            if (rephPos == PosAfterSub)
            {
                newRephPos = baseIndex;
                uint stop = Flag(PosPostC) | Flag(PosAfterPost) | Flag(PosSmvd);
                while (newRephPos + 1 < end &&
                    (Flag(info[newRephPos + 1].ShaperPosition) & stop) == 0)
                {
                    newRephPos++;
                }

                if (newRephPos < end)
                {
                    return newRephPos;
                }
            }
        }

        // 5. Without a consonant from steps 3 or 4, before the first post-base matra, syllable modifier or vedic sign with a
        // reordering class after the intended reph position; as step 2 (https://github.com/harfbuzz/harfbuzz/issues/2298).
        if (TryAfterHalant(info, start, baseIndex, out newRephPos))
        {
            return newRephPos;
        }

        // 6. Otherwise the end of the syllable.
        newRephPos = end - 1;
        while (newRephPos > start && info[newRephPos].ShaperPosition == PosSmvd)
        {
            newRephPos--;
        }

        // A reph that would end up after a Matra,Halant goes before the Halant, to interact with the matra; not after a plain
        // Consonant,Halant, as Uniscribe (U+0930,U+094D,U+0915,U+094B,U+094D).
        if (IsHalant(info[newRephPos]))
        {
            for (int i = baseIndex + 1; i < newRephPos; i++)
            {
                if ((Flag(info[i].ShaperCategory) & (Flag(ExM) | Flag(ExMPst))) != 0)
                {
                    newRephPos--;
                }
            }
        }

        return newRephPos;
    }

    private static bool TryAfterHalant(GlyphInfo[] info, int start, int baseIndex, out int newRephPos)
    {
        newRephPos = start + 1;
        while (newRephPos < baseIndex && !IsHalant(info[newRephPos]))
        {
            newRephPos++;
        }

        if (newRephPos < baseIndex && IsHalant(info[newRephPos]))
        {
            // After a ZWJ or ZWNJ following the halant.
            if (newRephPos + 1 < baseIndex && IsJoiner(info[newRephPos + 1]))
            {
                newRephPos++;
            }

            return true;
        }

        return false;
    }
}
