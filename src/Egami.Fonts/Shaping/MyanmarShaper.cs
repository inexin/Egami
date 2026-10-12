// Translated from HarfBuzz's hb-ot-shaper-myanmar.cc (MIT); see THIRD-PARTY-NOTICES.md.
namespace Egami.Fonts.Shaping;

/// <summary>
/// The Myanmar shaper: syllables by the Myanmar machine, a dotted circle for each broken one, each syllable sorted into visual
/// order (kinzi after the base, medial ra and the pre-base vowels before it), then the basic features one at a time.
/// </summary>
internal sealed class MyanmarShaper : ComplexShaper
{
    public static readonly MyanmarShaper Instance = new();

    /// <summary>Zawgyi-encoded Myanmar, HarfBuzz's _hb_ot_shaper_myanmar_zawgyi.</summary>
    public static readonly ComplexShaper Zawgyi = MyanmarZawgyiShaper.Instance;

    // The basic features apply in order, one at a time, after reordering, per syllable.
    private static readonly string[] s_basicFeatures = ["rphf", "pref", "blwf", "pstf"];

    // The other features apply all at once, after clearing the syllables.
    private static readonly string[] s_otherFeatures = ["pres", "abvs", "blws", "psts"];

    // Visual positions in a syllable, from HarfBuzz's ot_position_t.
    private const byte PosPreM = 2;
    private const byte PosPreC = 3;
    private const byte PosBaseC = 4;
    private const byte PosAfterMain = 5;
    private const byte PosBeforeSub = 7;
    private const byte PosBelowC = 8;
    private const byte PosAfterSub = 9;

    private static readonly Comparison<GlyphInfo> s_compareOrder = (a, b) => a.ShaperPosition - b.ShaperPosition;

    public override void CollectFeatures(ShapePlanner planner)
    {
        ShapeMapBuilder map = planner.Map;

        // Before any lookups have been applied.
        map.AddGsubPause(SetupSyllables);

        map.EnableFeature("locl", FeatureFlags.PerSyllable);

        // The Indic specs do not require 'ccmp', but where a font uses it, it is typically at the start.
        map.EnableFeature("ccmp", FeatureFlags.PerSyllable);

        map.AddGsubPause(Reorder);

        foreach (string feature in s_basicFeatures)
        {
            map.EnableFeature(feature, FeatureFlags.ManualZwj | FeatureFlags.PerSyllable);
            map.AddGsubPause(null);
        }

        map.AddGsubPause(Syllabic.ClearSyllables);

        foreach (string feature in s_otherFeatures)
        {
            map.EnableFeature(feature, FeatureFlags.ManualZwj);
        }
    }

    public override void SetupMasks(ShapePlan plan, LayoutBuffer buffer, ShapeFont font)
    {
        // No masks: only the categories are recorded.
        GlyphInfo[] info = buffer.Info;
        for (int i = 0; i < buffer.Len; i++)
        {
            info[i].ShaperCategory = (byte)(IndicTables.GetCategories((int)info[i].Codepoint) & 0xFF);
        }
    }

    private static bool SetupSyllables(ShapePlan plan, ShapeFont font, LayoutBuffer buffer)
    {
        MyanmarMachine.FindSyllables(buffer);
        for (int start = 0, end = NextSyllable(buffer, 0); start < buffer.Len; start = end, end = NextSyllable(buffer, start))
        {
            buffer.UnsafeToBreak(start, end);
        }

        return false;
    }

    private static bool Reorder(ShapePlan plan, ShapeFont font, LayoutBuffer buffer)
    {
        bool ret = Syllabic.InsertDottedCircles(font, buffer, MyanmarMachine.BrokenCluster, MyanmarMachine.ExDOTTEDCIRCLE);

        for (int start = 0, end = NextSyllable(buffer, 0); start < buffer.Len; start = end, end = NextSyllable(buffer, start))
        {
            // Broken clusters have their dotted circle by now, so they reorder as consonant syllables.
            int syllableType = buffer.Info[start].Syllable & 0x0F;
            if (syllableType is MyanmarMachine.ConsonantSyllable or MyanmarMachine.BrokenCluster)
            {
                ReorderConsonantSyllable(buffer, start, end);
            }
        }

        return ret;
    }

    /// <summary>
    /// Whether a glyph counts as a consonant: vowels and placeholders do, which is safe as they cannot occur in a consonant
    /// syllable; a ligature never does.
    /// </summary>
    private static bool IsConsonant(in GlyphInfo info)
    {
        if ((info.GlyphProps & GlyphProps.Ligated) != 0)
        {
            return false;
        }

        return info.ShaperCategory is MyanmarMachine.ExC or MyanmarMachine.ExCS or MyanmarMachine.ExRa or MyanmarMachine.ExIV or
            MyanmarMachine.ExGB or MyanmarMachine.ExDOTTEDCIRCLE;
    }

    /// <summary>Microsoft's Myanmar rules: each glyph gets a visual position, and the syllable is sorted by it.</summary>
    private static void ReorderConsonantSyllable(LayoutBuffer buffer, int start, int end)
    {
        GlyphInfo[] info = buffer.Info;

        int baseIndex = end;
        bool hasReph = false;

        int limit = start;
        if (start + 3 <= end &&
            info[start].ShaperCategory == MyanmarMachine.ExRa &&
            info[start + 1].ShaperCategory == MyanmarMachine.ExAs &&
            info[start + 2].ShaperCategory == MyanmarMachine.ExH)
        {
            limit += 3;
            baseIndex = start;
            hasReph = true;
        }

        if (!hasReph)
        {
            baseIndex = limit;
        }

        for (int i = limit; i < end; i++)
        {
            if (IsConsonant(info[i]))
            {
                baseIndex = i;
                break;
            }
        }

        // Assign the positions; this loop is all of Myanmar reordering.
        {
            int i = start;
            for (; i < start + (hasReph ? 3 : 0); i++)
            {
                info[i].ShaperPosition = PosAfterMain;
            }

            for (; i < baseIndex; i++)
            {
                info[i].ShaperPosition = PosPreC;
            }

            if (i < end)
            {
                info[i].ShaperPosition = PosBaseC;
                i++;
            }

            byte pos = PosAfterMain;
            for (; i < end; i++)
            {
                byte category = info[i].ShaperCategory;
                if (category == MyanmarMachine.ExMR)
                {
                    // Pre-base medial ra.
                    info[i].ShaperPosition = PosPreC;
                    continue;
                }

                if (category == MyanmarMachine.ExVPre)
                {
                    // Left vowel.
                    info[i].ShaperPosition = PosPreM;
                    continue;
                }

                if (category == MyanmarMachine.ExVS)
                {
                    info[i].ShaperPosition = info[i - 1].ShaperPosition;
                    continue;
                }

                if (pos == PosAfterMain && category == MyanmarMachine.ExVBlw)
                {
                    pos = PosBelowC;
                    info[i].ShaperPosition = pos;
                    continue;
                }

                if (pos == PosBelowC && category == MyanmarMachine.ExA)
                {
                    info[i].ShaperPosition = PosBeforeSub;
                    continue;
                }

                if (pos == PosBelowC && category == MyanmarMachine.ExVBlw)
                {
                    info[i].ShaperPosition = pos;
                    continue;
                }

                if (pos == PosBelowC && category != MyanmarMachine.ExA)
                {
                    pos = PosAfterSub;
                    info[i].ShaperPosition = pos;
                    continue;
                }

                info[i].ShaperPosition = pos;
            }
        }

        buffer.Sort(start, end, s_compareOrder);

        // Flip the left vowel sequence (HarfBuzz issue 3863), then flip each vowel's variation selectors back after it.
        int firstLeftMatra = end;
        int lastLeftMatra = end;
        for (int i = start; i < end; i++)
        {
            if (info[i].ShaperPosition == PosPreM)
            {
                if (firstLeftMatra == end)
                {
                    firstLeftMatra = i;
                }

                lastLeftMatra = i;
            }
        }

        if (firstLeftMatra < lastLeftMatra)
        {
            // The sort merged the clusters already.
            buffer.ReverseRange(firstLeftMatra, lastLeftMatra + 1);
            int i = firstLeftMatra;
            for (int j = i; j <= lastLeftMatra; j++)
            {
                if (info[j].ShaperCategory == MyanmarMachine.ExVPre)
                {
                    buffer.ReverseRange(i, j + 1);
                    i = j + 1;
                }
            }
        }
    }

    /// <summary>The end of the syllable that starts at <paramref name="start"/>.</summary>
    private static int NextSyllable(LayoutBuffer buffer, int start)
    {
        if (start >= buffer.Len)
        {
            return start;
        }

        byte syllable = buffer.Info[start].Syllable;
        while (++start < buffer.Len && syllable == buffer.Info[start].Syllable)
        {
        }

        return start;
    }

    public override NormalizationMode NormalizationPreference => NormalizationMode.ComposedDiacriticsNoShortCircuit;

    public override ZeroWidthMarks ZeroWidthMarks => ZeroWidthMarks.ByGdefEarly;

    public override bool FallbackPosition => false;
}

/// <summary>
/// Zawgyi-encoded Myanmar (script Qaag): all automatic processing off, with no normalization, no zero-width marks and no fallback
/// positioning (HarfBuzz issue 1162).
/// </summary>
internal sealed class MyanmarZawgyiShaper : ComplexShaper
{
    public static readonly MyanmarZawgyiShaper Instance = new();

    public override NormalizationMode NormalizationPreference => NormalizationMode.None;

    public override ZeroWidthMarks ZeroWidthMarks => ZeroWidthMarks.None;

    public override bool FallbackPosition => false;
}
