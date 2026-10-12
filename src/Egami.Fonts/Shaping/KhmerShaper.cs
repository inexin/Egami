// Translated from HarfBuzz's hb-ot-shaper-khmer.cc (MIT); see THIRD-PARTY-NOTICES.md.
namespace Egami.Fonts.Shaping;

/// <summary>
/// The Khmer shaper: syllables by the Khmer machine, a dotted circle for each broken one, the coeng-ro and pre-base vowel moved
/// to the front of the syllable, the basic features masked per syllable, and the split vowels decomposed.
/// </summary>
internal sealed class KhmerShaper : ComplexShaper
{
    public static readonly KhmerShaper Instance = new();

    // The features, in khmer_features' order: the basic ones apply per syllable before clearing the syllables, the others after.
    private static readonly (string Tag, FeatureFlags Flags)[] s_features =
    [
        ("pref", FeatureFlags.ManualJoiners | FeatureFlags.PerSyllable),
        ("blwf", FeatureFlags.ManualJoiners | FeatureFlags.PerSyllable),
        ("abvf", FeatureFlags.ManualJoiners | FeatureFlags.PerSyllable),
        ("pstf", FeatureFlags.ManualJoiners | FeatureFlags.PerSyllable),
        ("cfar", FeatureFlags.ManualJoiners | FeatureFlags.PerSyllable),
        ("pres", FeatureFlags.GlobalManualJoiners),
        ("abvs", FeatureFlags.GlobalManualJoiners),
        ("blws", FeatureFlags.GlobalManualJoiners),
        ("psts", FeatureFlags.GlobalManualJoiners),
    ];

    private const int Pref = 0;
    private const int Blwf = 1;
    private const int Abvf = 2;
    private const int Pstf = 3;
    private const int Cfar = 4;
    private const int BasicFeatures = 5;

    private sealed class PlanData
    {
        public readonly uint[] MaskArray = new uint[s_features.Length];
    }

    public override void CollectFeatures(ShapePlanner planner)
    {
        ShapeMapBuilder map = planner.Map;

        // Before any lookups have been applied.
        map.AddGsubPause(SetupSyllables);
        map.AddGsubPause(Reorder);

        // Uniscribe does not pause between the basic features (HarfBuzz issue 974).
        map.EnableFeature("locl", FeatureFlags.PerSyllable);
        map.EnableFeature("ccmp", FeatureFlags.PerSyllable);

        int i = 0;
        for (; i < BasicFeatures; i++)
        {
            map.AddFeature(s_features[i].Tag, s_features[i].Flags);
        }

        // HarfBuzz issue 3531: the syllables are not needed after the basic features.
        map.AddGsubPause(Syllabic.ClearSyllables);

        for (; i < s_features.Length; i++)
        {
            map.AddFeature(s_features[i].Tag, s_features[i].Flags);
        }
    }

    public override void OverrideFeatures(ShapePlanner planner)
    {
        ShapeMapBuilder map = planner.Map;

        // The Khmer spec counts 'clig' among the required features, so it goes in the overrides.
        map.EnableFeature("clig");
        map.DisableFeature("liga");
    }

    public override object? CreateData(ShapePlan plan)
    {
        PlanData data = new();
        for (int i = 0; i < s_features.Length; i++)
        {
            // The global features need no mask of their own.
            bool global = (s_features[i].Flags & FeatureFlags.Global) != 0;
            data.MaskArray[i] = global ? 0 : plan.Map.Get1Mask(ShapeMapBuilder.Tag(s_features[i].Tag));
        }

        return data;
    }

    public override void SetupMasks(ShapePlan plan, LayoutBuffer buffer, ShapeFont font)
    {
        // The masks wait for the syllables; only the categories are recorded here.
        GlyphInfo[] info = buffer.Info;
        for (int i = 0; i < buffer.Len; i++)
        {
            info[i].ShaperCategory = (byte)(IndicTables.GetCategories((int)info[i].Codepoint) & 0xFF);
        }
    }

    private static bool SetupSyllables(ShapePlan plan, ShapeFont font, LayoutBuffer buffer)
    {
        KhmerMachine.FindSyllables(buffer);
        for (int start = 0, end = NextSyllable(buffer, 0); start < buffer.Len; start = end, end = NextSyllable(buffer, start))
        {
            buffer.UnsafeToBreak(start, end);
        }

        return false;
    }

    private static bool Reorder(ShapePlan plan, ShapeFont font, LayoutBuffer buffer)
    {
        bool ret = Syllabic.InsertDottedCircles(font, buffer, KhmerMachine.BrokenCluster, KhmerMachine.ExDOTTEDCIRCLE);

        PlanData data = (PlanData)plan.ShaperData!;
        for (int start = 0, end = NextSyllable(buffer, 0); start < buffer.Len; start = end, end = NextSyllable(buffer, start))
        {
            // Broken clusters have their dotted circle by now, so they reorder as consonant syllables.
            int syllableType = buffer.Info[start].Syllable & 0x0F;
            if (syllableType is KhmerMachine.ConsonantSyllable or KhmerMachine.BrokenCluster)
            {
                ReorderConsonantSyllable(data, buffer, start, end);
            }
        }

        return ret;
    }

    /// <summary>Microsoft's Khmer rules: the coeng-ro and the pre-base vowel move to the front of the syllable.</summary>
    private static void ReorderConsonantSyllable(PlanData data, LayoutBuffer buffer, int start, int end)
    {
        GlyphInfo[] info = buffer.Info;

        // Everything after the base may take the post-base features.
        uint mask = data.MaskArray[Blwf] | data.MaskArray[Abvf] | data.MaskArray[Pstf];
        for (int i = start + 1; i < end; i++)
        {
            info[i].Mask |= mask;
        }

        int numCoengs = 0;
        for (int i = start + 1; i < end; i++)
        {
            // A coeng and ro (subscript type 2) move to just before the base and take 'pref'.
            if (info[i].ShaperCategory == KhmerMachine.ExH && numCoengs <= 2 && i + 1 < end)
            {
                numCoengs++;

                if (info[i + 1].ShaperCategory == KhmerMachine.ExRa)
                {
                    for (int j = 0; j < 2; j++)
                    {
                        info[i + j].Mask |= data.MaskArray[Pref];
                    }

                    buffer.MergeClusters(start, i + 2);
                    GlyphInfo t0 = info[i];
                    GlyphInfo t1 = info[i + 1];
                    Array.Copy(info, start, info, start + 2, i - start);
                    info[start] = t0;
                    info[start + 1] = t1;

                    // What follows takes 'cfar', which tells U+1784,U+17D2,U+179A,U+17D2,U+1782 from
                    // U+1784,U+17D2,U+1782,U+17D2,U+179A in Microsoft's Khmer fonts.
                    if (data.MaskArray[Cfar] != 0)
                    {
                        for (int j = i + 2; j < end; j++)
                        {
                            info[j].Mask |= data.MaskArray[Cfar];
                        }
                    }

                    numCoengs = 2;
                }
            }
            else if (info[i].ShaperCategory == KhmerMachine.ExVPre)
            {
                // The left vowel piece moves to the start.
                buffer.MergeClusters(start, i + 1);
                GlyphInfo t = info[i];
                Array.Copy(info, start, info, start + 1, i - start);
                info[start] = t;
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

    public override bool HasDecompose => true;

    public override bool Decompose(NormalizeContext c, int ab, out int a, out int b)
    {
        // The split vowels, which have no Unicode decompositions.
        switch (ab)
        {
            case 0x17BE:
            case 0x17BF:
            case 0x17C0:
            case 0x17C4:
            case 0x17C5:
                a = 0x17C1;
                b = ab;
                return true;
        }

        return NormalizeContext.DecomposeUnicode(ab, out a, out b);
    }

    public override bool HasCompose => true;

    public override bool Compose(NormalizeContext c, int a, int b, out int ab)
    {
        // The split vowels stay split.
        if (UnicodeProps.IsMarkCategory(UnicodeProps.Category(a)))
        {
            ab = 0;
            return false;
        }

        return NormalizeContext.ComposeUnicode(a, b, out ab);
    }

    public override ZeroWidthMarks ZeroWidthMarks => ZeroWidthMarks.None;

    public override bool FallbackPosition => false;
}
