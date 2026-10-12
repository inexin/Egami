// Translated from HarfBuzz's hb-ot-shaper-use.cc (MIT); see THIRD-PARTY-NOTICES.md.
using System.Globalization;

using Egami.Fonts.Unicode;

namespace Egami.Fonts.Shaping;

/// <summary>
/// The Universal Shaping Engine shaper, for the Brahmic and other complex scripts without a shaper of their own (and the Indic
/// scripts under their '*3' tags): USE categories, syllables by the USE machine, the per-syllable basic features, repha and
/// pre-base reordering, dotted circles in broken syllables, and joining forms (Arabic joining for the joining scripts,
/// otherwise per syllable).
/// </summary>
/// <remarks>See https://docs.microsoft.com/en-us/typography/script-development/use.</remarks>
internal sealed class UseShaper : ComplexShaper
{
    public static readonly UseShaper Instance = new();

    // The basic features, applied all at once before reordering, constrained to the syllable.
    private static readonly string[] s_basicFeatures = ["rkrf", "abvf", "blwf", "half", "pstf", "vatu", "cjct"];

    // The topographical features, in the order of the joining forms below.
    private static readonly string[] s_topographicalFeatures = ["isol", "init", "medi", "fina"];

    private const int JoiningFormIsol = 0;
    private const int JoiningFormInit = 1;
    private const int JoiningFormMedi = 2;
    private const int JoiningFormFina = 3;
    private const int JoiningFormNone = 4;

    // The other features, applied all at once after reordering and clearing syllables.
    private static readonly string[] s_otherFeatures = ["abvs", "blws", "haln", "pres", "psts"];

    // The categories a repha moves before.
    private const ulong PostBaseFlags =
        (1UL << UseMachine.FAbv) | (1UL << UseMachine.FBlw) | (1UL << UseMachine.FPst) |
        (1UL << UseMachine.FMAbv) | (1UL << UseMachine.FMBlw) | (1UL << UseMachine.FMPst) |
        (1UL << UseMachine.MAbv) | (1UL << UseMachine.MBlw) | (1UL << UseMachine.MPst) | (1UL << UseMachine.MPre) |
        (1UL << UseMachine.VAbv) | (1UL << UseMachine.VBlw) | (1UL << UseMachine.VPst) | (1UL << UseMachine.VPre) |
        (1UL << UseMachine.VMAbv) | (1UL << UseMachine.VMBlw) | (1UL << UseMachine.VMPst) | (1UL << UseMachine.VMPre);

    // The syllable types that reorder.
    private const int ReorderedSyllables =
        (1 << UseMachine.ViramaTerminatedCluster) | (1 << UseMachine.SakotTerminatedCluster) | (1 << UseMachine.StandardCluster) |
        (1 << UseMachine.SymbolCluster) | (1 << UseMachine.BrokenCluster);

    // HarfBuzz's HB_MAX_SYLLABLE_LENGTH.
    private const int MaxSyllableLength = 64;

    private sealed class PlanData
    {
        public uint RphfMask;

        // The Arabic joining-form masks, for the scripts that join as Arabic does; null for the others.
        public uint[]? ArabicMasks;
    }

    public override void CollectFeatures(ShapePlanner planner)
    {
        ShapeMapBuilder map = planner.Map;

        // Do this before any lookups have been applied.
        map.AddGsubPause(SetupSyllables);

        // "Default glyph pre-processing group".
        map.EnableFeature("locl", FeatureFlags.PerSyllable);
        map.EnableFeature("ccmp", FeatureFlags.PerSyllable);
        map.EnableFeature("nukt", FeatureFlags.PerSyllable);
        map.EnableFeature("akhn", FeatureFlags.ManualZwj | FeatureFlags.PerSyllable);

        // "Reordering group".
        map.AddGsubPause(ClearSubstitutionFlags);
        map.AddFeature("rphf", FeatureFlags.ManualZwj | FeatureFlags.PerSyllable);
        map.AddGsubPause(RecordRphf);
        map.AddGsubPause(ClearSubstitutionFlags);
        map.EnableFeature("pref", FeatureFlags.ManualZwj | FeatureFlags.PerSyllable);
        map.AddGsubPause(RecordPref);

        // "Orthographic unit shaping group".
        foreach (string feature in s_basicFeatures)
        {
            map.EnableFeature(feature, FeatureFlags.ManualZwj | FeatureFlags.PerSyllable);
        }

        map.AddGsubPause(Reorder);
        map.AddGsubPause(Syllabic.ClearSyllables);

        // "Topographical features".
        foreach (string feature in s_topographicalFeatures)
        {
            map.AddFeature(feature);
        }

        map.AddGsubPause(null);

        // "Standard typographic presentation".
        foreach (string feature in s_otherFeatures)
        {
            map.EnableFeature(feature, FeatureFlags.ManualZwj);
        }
    }

    public override object? CreateData(ShapePlan plan) => new PlanData
    {
        RphfMask = plan.Map.Get1Mask(ShapeMapBuilder.Tag("rphf")),
        ArabicMasks = ArabicShaper.HasArabicJoining(plan.Script) ? ArabicShaper.JoiningMasks(plan.Map) : null,
    };

    public override void PreprocessText(ShapePlan plan, LayoutBuffer buffer, ShapeFont font) =>
        VowelConstraints.Preprocess(buffer, plan.Script.Code);

    public override NormalizationMode NormalizationPreference => NormalizationMode.ComposedDiacriticsNoShortCircuit;

    public override bool HasCompose => true;

    public override bool Compose(NormalizeContext c, int a, int b, out int ab)
    {
        // Avoid recomposing split matras.
        if (UnicodeData.GetGeneralCategory(a) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
        {
            ab = 0;
            return false;
        }

        return NormalizeContext.ComposeUnicode(a, b, out ab);
    }

    public override void SetupMasks(ShapePlan plan, LayoutBuffer buffer, ShapeFont font)
    {
        PlanData data = (PlanData)plan.ShaperData!;

        // The Arabic joining action goes in the shaper position, before the USE category goes in the shaper category.
        if (data.ArabicMasks != null)
        {
            ArabicShaper.SetupMasks(data.ArabicMasks, buffer, plan.Script);
        }

        // Masks cannot be set up here: the categories are kept for the pause after the syllables are found.
        GlyphInfo[] info = buffer.Info;
        for (int i = 0; i < buffer.Len; i++)
        {
            info[i].ShaperCategory = UseTable.GetCategory((int)info[i].Codepoint);
        }
    }

    public override ZeroWidthMarks ZeroWidthMarks => ZeroWidthMarks.ByGdefEarly;

    public override bool FallbackPosition => false;

    /// <summary>The end of the syllable starting at <paramref name="start"/>, as HarfBuzz's _hb_next_syllable.</summary>
    private static int NextSyllable(LayoutBuffer buffer, int start)
    {
        if (start >= buffer.Len)
        {
            return start;
        }

        GlyphInfo[] info = buffer.Info;
        int count = start + Math.Min(buffer.Len - start, MaxSyllableLength);
        int syllable = info[start].Syllable;
        while (++start < count && syllable == info[start].Syllable)
        {
        }

        return start;
    }

    /// <summary>The first syllable's end, as HarfBuzz's foreach_syllable starts.</summary>
    private static int FirstSyllableEnd(LayoutBuffer buffer) => buffer.Len != 0 ? NextSyllable(buffer, 0) : 0;

    private static bool ClearSubstitutionFlags(ShapePlan plan, ShapeFont font, LayoutBuffer buffer)
    {
        GlyphInfo[] info = buffer.Info;
        for (int i = 0; i < buffer.Len; i++)
        {
            info[i].GlyphProps &= unchecked((ushort)~GlyphProps.Substituted);
        }

        return false;
    }

    private static bool SetupSyllables(ShapePlan plan, ShapeFont font, LayoutBuffer buffer)
    {
        UseMachine.FindSyllables(buffer);
        for (int start = 0, end = FirstSyllableEnd(buffer); start < buffer.Len; start = end, end = NextSyllable(buffer, start))
        {
            buffer.UnsafeToBreak(start, end);
        }

        SetupRphfMask(plan, buffer);
        SetupTopographicalMasks(plan, buffer);
        return false;
    }

    private static void SetupRphfMask(ShapePlan plan, LayoutBuffer buffer)
    {
        uint mask = ((PlanData)plan.ShaperData!).RphfMask;
        if (mask == 0)
        {
            return;
        }

        GlyphInfo[] info = buffer.Info;
        for (int start = 0, end = FirstSyllableEnd(buffer); start < buffer.Len; start = end, end = NextSyllable(buffer, start))
        {
            int limit = info[start].ShaperCategory == UseMachine.R ? 1 : Math.Min(3, end - start);
            for (int i = start; i < start + limit; i++)
            {
                info[i].Mask |= mask;
            }
        }
    }

    private static void SetupTopographicalMasks(ShapePlan plan, LayoutBuffer buffer)
    {
        if (((PlanData)plan.ShaperData!).ArabicMasks != null)
        {
            return;
        }

        uint[] masks = new uint[4];
        uint allMasks = 0;
        for (int i = 0; i < 4; i++)
        {
            masks[i] = plan.Map.Get1Mask(ShapeMapBuilder.Tag(s_topographicalFeatures[i]));
            if (masks[i] == plan.Map.GlobalMask)
            {
                masks[i] = 0;
            }

            allMasks |= masks[i];
        }

        if (allMasks == 0)
        {
            return;
        }

        uint otherMasks = ~allMasks;
        int lastStart = 0;
        int lastForm = JoiningFormNone;
        GlyphInfo[] info = buffer.Info;
        for (int start = 0, end = FirstSyllableEnd(buffer); start < buffer.Len; start = end, end = NextSyllable(buffer, start))
        {
            switch (info[start].Syllable & 0x0F)
            {
                case UseMachine.HieroglyphCluster:
                case UseMachine.NonCluster:
                    // These don't join.
                    lastForm = JoiningFormNone;
                    break;

                case UseMachine.ViramaTerminatedCluster:
                case UseMachine.SakotTerminatedCluster:
                case UseMachine.StandardCluster:
                case UseMachine.NumberJoinerTerminatedCluster:
                case UseMachine.NumeralCluster:
                case UseMachine.SymbolCluster:
                case UseMachine.BrokenCluster:
                    bool join = lastForm is JoiningFormFina or JoiningFormIsol;
                    if (join)
                    {
                        // Fix up the previous syllable's form.
                        lastForm = lastForm == JoiningFormFina ? JoiningFormMedi : JoiningFormInit;
                        for (int i = lastStart; i < start; i++)
                        {
                            info[i].Mask = (info[i].Mask & otherMasks) | masks[lastForm];
                        }
                    }

                    // The form of this syllable.
                    lastForm = join ? JoiningFormFina : JoiningFormIsol;
                    for (int i = start; i < end; i++)
                    {
                        info[i].Mask = (info[i].Mask & otherMasks) | masks[lastForm];
                    }

                    break;
            }

            lastStart = start;
        }
    }

    private static bool RecordRphf(ShapePlan plan, ShapeFont font, LayoutBuffer buffer)
    {
        uint mask = ((PlanData)plan.ShaperData!).RphfMask;
        if (mask == 0)
        {
            return false;
        }

        GlyphInfo[] info = buffer.Info;
        for (int start = 0, end = FirstSyllableEnd(buffer); start < buffer.Len; start = end, end = NextSyllable(buffer, start))
        {
            // Mark a substituted repha as R.
            for (int i = start; i < end && (info[i].Mask & mask) != 0; i++)
            {
                if ((info[i].GlyphProps & GlyphProps.Substituted) != 0)
                {
                    info[i].ShaperCategory = UseMachine.R;
                    break;
                }
            }
        }

        return false;
    }

    private static bool RecordPref(ShapePlan plan, ShapeFont font, LayoutBuffer buffer)
    {
        GlyphInfo[] info = buffer.Info;
        for (int start = 0, end = FirstSyllableEnd(buffer); start < buffer.Len; start = end, end = NextSyllable(buffer, start))
        {
            // Mark a substituted pref as VPre, as they behave the same way.
            for (int i = start; i < end; i++)
            {
                if ((info[i].GlyphProps & GlyphProps.Substituted) != 0)
                {
                    info[i].ShaperCategory = UseMachine.VPre;
                    break;
                }
            }
        }

        return false;
    }

    private static bool IsHalant(in GlyphInfo info) =>
        info.ShaperCategory is UseMachine.H or UseMachine.HVM or UseMachine.IS && (info.GlyphProps & GlyphProps.Ligated) == 0;

    private static void ReorderSyllable(LayoutBuffer buffer, int start, int end)
    {
        // Only a few syllable types need reordering.
        if (((1 << (buffer.Info[start].Syllable & 0x0F)) & ReorderedSyllables) == 0)
        {
            return;
        }

        GlyphInfo[] info = buffer.Info;

        // Move things forward: a repha goes towards the end, but before the first post-base glyph.
        if (info[start].ShaperCategory == UseMachine.R && end - start > 1)
        {
            for (int i = start + 1; i < end; i++)
            {
                byte category = info[i].ShaperCategory;
                bool isPostBaseGlyph = (category < 64 && ((1UL << category) & PostBaseFlags) != 0) || IsHalant(info[i]);
                if (isPostBaseGlyph || i == end - 1)
                {
                    // If we hit a post-base glyph, move before it; otherwise move to the end. Shift things in between backward.
                    if (isPostBaseGlyph)
                    {
                        i--;
                    }

                    buffer.MergeClusters(start, i + 1);
                    GlyphInfo t = info[start];
                    Array.Copy(info, start + 1, info, start, i - start);
                    info[i] = t;
                    break;
                }
            }
        }

        // Move things back: a pre-base vowel or vowel modifier goes after the last halant before it, or to the start.
        int j = start;
        for (int i = start; i < end; i++)
        {
            if (IsHalant(info[i]))
            {
                // If we hit a halant, move after it; otherwise move to the beginning, and shift things in between forward.
                j = i + 1;
            }
            else if (info[i].ShaperCategory is UseMachine.VPre or UseMachine.VMPre &&
                // Only move the first component of a MultipleSubst.
                LayoutApplier.LigComp(info[i]) == 0 && j < i)
            {
                buffer.MergeClusters(j, i + 1);
                GlyphInfo t = info[i];
                Array.Copy(info, j, info, j + 1, i - j);
                info[j] = t;
            }
        }
    }

    private static bool Reorder(ShapePlan plan, ShapeFont font, LayoutBuffer buffer)
    {
        bool ret = Syllabic.InsertDottedCircles(font, buffer, UseMachine.BrokenCluster, UseMachine.B, UseMachine.R);

        for (int start = 0, end = FirstSyllableEnd(buffer); start < buffer.Len; start = end, end = NextSyllable(buffer, start))
        {
            ReorderSyllable(buffer, start, end);
        }

        return ret;
    }
}
