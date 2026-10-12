// Translated from HarfBuzz's hb-ot-shaper-arabic.cc and hb-ot-shaper-arabic-fallback.hh (MIT); see THIRD-PARTY-NOTICES.md.
using Egami.Fonts.Unicode;

namespace Egami.Fonts.Shaping;

/// <summary>
/// The Arabic shaper, for Arabic, Syriac, N'Ko, Mongolian and the other joining scripts: joining forms by the Unicode joining
/// state machine, the Arabic feature order with its pauses, stretched Syriac and subtending marks ('stch'), the modifier
/// combining mark order of UTR #53, and fallback shaping from the presentation forms when a font has no Arabic GSUB.
/// </summary>
internal sealed class ArabicShaper : ComplexShaper
{
    public static readonly ArabicShaper Instance = new();

    // Joining types, the state machine's columns.
    private const int JoiningTypeU = 0;
    private const int JoiningTypeL = 1;
    private const int JoiningTypeR = 2;
    private const int JoiningTypeD = 3;
    private const int JoiningGroupAlaph = 4;
    private const int JoiningGroupDalathRish = 5;
    private const int JoiningTypeT = 6;

    // Actions, in the order of the features; the shaping-action byte also records the 'stch' tiles.
    private const byte Isol = 0;
    private const byte Fina = 1;
    private const byte Fin2 = 2;
    private const byte Fin3 = 3;
    private const byte Medi = 4;
    private const byte Med2 = 5;
    private const byte Init = 6;
    private const byte None = 7;
    private const int NumFeatures = None;
    private const byte StchFixed = 8;
    private const byte StchRepeating = 9;

    private const int ScratchHasStch = LayoutBuffer.ScratchShaper0;
    private const int StchMaxGlyphs = 256;

    private static readonly string[] s_features = ["isol", "fina", "fin2", "fin3", "medi", "med2", "init"];

    // (previous action, current action, next state) for each state and joining type.
    private static readonly (byte Prev, byte Curr, byte Next)[,] s_stateTable =
    {
        // State 0: prev was U, not willing to join.
        { (None, None, 0), (None, Isol, 2), (None, Isol, 1), (None, Isol, 2), (None, Isol, 1), (None, Isol, 6) },
        // State 1: prev was R or ISOL/ALAPH, not willing to join.
        { (None, None, 0), (None, Isol, 2), (None, Isol, 1), (None, Isol, 2), (None, Fin2, 5), (None, Isol, 6) },
        // State 2: prev was D/L in ISOL form, willing to join.
        { (None, None, 0), (None, Isol, 2), (Init, Fina, 1), (Init, Fina, 3), (Init, Fina, 4), (Init, Fina, 6) },
        // State 3: prev was D in FINA form, willing to join.
        { (None, None, 0), (None, Isol, 2), (Medi, Fina, 1), (Medi, Fina, 3), (Medi, Fina, 4), (Medi, Fina, 6) },
        // State 4: prev was FINA ALAPH, not willing to join.
        { (None, None, 0), (None, Isol, 2), (Med2, Isol, 1), (Med2, Isol, 2), (Med2, Fin2, 5), (Med2, Isol, 6) },
        // State 5: prev was FIN2/FIN3 ALAPH, not willing to join.
        { (None, None, 0), (None, Isol, 2), (Isol, Isol, 1), (Isol, Isol, 2), (Isol, Fin2, 5), (Isol, Isol, 6) },
        // State 6: prev was DALATH/RISH, not willing to join.
        { (None, None, 0), (None, Isol, 2), (None, Isol, 1), (None, Isol, 2), (None, Fin3, 5), (None, Isol, 6) },
    };

    // The modifier combining marks of UTR #53, which move to the front of their combining class.
    private static readonly uint[] s_modifierCombiningMarks =
        [0x0654, 0x0655, 0x0658, 0x06DC, 0x06E3, 0x06E7, 0x06E8, 0x08CA, 0x08CB, 0x08CD, 0x08CE, 0x08CF, 0x08D3, 0x08F3];

    private sealed class PlanData
    {
        // One more than the features: the None action's mask is 0.
        public readonly uint[] MaskArray = new uint[NumFeatures + 1];
        public bool DoFallback;
        public bool HasStch;
        public FallbackPlan? Fallback;
    }

    private static bool IsSyriacFeature(string tag) => tag[3] is '2' or '3';

    /// <summary>The joining type, from the UCD's derived joining types (transparent for unlisted marks and format characters).</summary>
    private static int GetJoiningType(int u)
    {
        JoiningType type = UnicodeData.GetJoiningType(u);
        switch (type)
        {
            case JoiningType.NonJoining:
                return JoiningTypeU;
            case JoiningType.LeftJoining:
                return JoiningTypeL;
            case JoiningType.Transparent:
                return JoiningTypeT;
        }

        JoiningGroup group = UnicodeData.GetJoiningGroup(u);
        if (group == JoiningGroup.Alaph)
        {
            return JoiningGroupAlaph;
        }

        if (group == JoiningGroup.DalathRish)
        {
            return JoiningGroupDalathRish;
        }

        return type == JoiningType.RightJoining ? JoiningTypeR : JoiningTypeD;
    }

    public override void CollectFeatures(ShapePlanner planner)
    {
        ShapeMapBuilder map = planner.Map;

        // The Arabic feature order, with pauses between most; the pause before rlig is required, and Arabic (not Mongolian) has
        // one between rlig and calt as well.
        map.EnableFeature("stch");
        map.AddGsubPause(RecordStch);

        map.EnableFeature("ccmp", FeatureFlags.ManualZwj);
        map.EnableFeature("locl", FeatureFlags.ManualZwj);

        map.AddGsubPause(null);

        bool isArabic = planner.Script.Code == "Arab";
        foreach (string feature in s_features)
        {
            bool hasFallback = isArabic && !IsSyriacFeature(feature);
            map.AddFeature(feature, FeatureFlags.ManualZwj | (hasFallback ? FeatureFlags.HasFallback : FeatureFlags.None));
            map.AddGsubPause(null);
        }

        map.AddGsubPause(null);

        // In Arabic a ZWJ means "don't ligate" as well, so the ligating features run with manual ZWJ.
        map.EnableFeature("rlig", FeatureFlags.ManualZwj | FeatureFlags.HasFallback);

        if (isArabic)
        {
            map.AddGsubPause(FallbackShape);
        }

        map.EnableFeature("calt", FeatureFlags.ManualZwj);
        if (!map.HasFeature(ShapeMapBuilder.Tag("rclt")))
        {
            map.AddGsubPause(null);
        }

        map.EnableFeature("liga", FeatureFlags.ManualZwj);
        map.EnableFeature("clig", FeatureFlags.ManualZwj);

        // 'cswh' is off by default, as on Windows 8 and later.
        map.EnableFeature("mset", FeatureFlags.ManualZwj);
    }

    public override object? CreateData(ShapePlan plan)
    {
        PlanData data = new()
        {
            DoFallback = plan.Script.Code == "Arab",
            HasStch = plan.Map.Get1Mask(ShapeMapBuilder.Tag("stch")) != 0,
        };
        for (int i = 0; i < NumFeatures; i++)
        {
            uint tag = ShapeMapBuilder.Tag(s_features[i]);
            data.MaskArray[i] = plan.Map.Get1Mask(tag);
            data.DoFallback = data.DoFallback && (IsSyriacFeature(s_features[i]) || plan.Map.NeedsFallback(tag));
        }

        return data;
    }

    public override void SetupMasks(ShapePlan plan, LayoutBuffer buffer, ShapeFont font)
    {
        PlanData data = (PlanData)plan.ShaperData!;
        SetupMasks(data.MaskArray, buffer, plan.Script);
    }

    /// <summary>Sets each character's joining form mask; shared with the Universal Shaping Engine's joining scripts.</summary>
    public static void SetupMasks(uint[] maskArray, LayoutBuffer buffer, Script script)
    {
        Joining(buffer);
        if (script.Code == "Mong")
        {
            MongolianVariationSelectors(buffer);
        }

        GlyphInfo[] info = buffer.Info;
        for (int i = 0; i < buffer.Len; i++)
        {
            info[i].Mask |= maskArray[info[i].ShaperPosition];
        }
    }

    /// <summary>The joining-form masks of the Arabic features, for the Universal Shaping Engine.</summary>
    public static uint[] JoiningMasks(ShapeMap map)
    {
        uint[] masks = new uint[NumFeatures + 1];
        for (int i = 0; i < NumFeatures; i++)
        {
            masks[i] = map.Get1Mask(ShapeMapBuilder.Tag(s_features[i]));
        }

        return masks;
    }

    /// <summary>Whether the Universal Shaping Engine's script joins as Arabic does.</summary>
    public static bool HasArabicJoining(Script script) => script.Code switch
    {
        "Adlm" or "Arab" or "Chrs" or "Mand" or "Mani" or "Mong" or "Nkoo" or "Ougr" or "Phag" or "Phlp" or "Rohg" or "Sogd" or "Syrc" => true,
        _ => false,
    };

    private static void Joining(LayoutBuffer buffer)
    {
        int count = buffer.Len;
        GlyphInfo[] info = buffer.Info;
        int prev = -1;
        int state = 0;

        // The text before the run.
        foreach (int u in buffer.PreContext)
        {
            int thisType = GetJoiningType(u);
            if (thisType == JoiningTypeT)
            {
                continue;
            }

            state = s_stateTable[state, thisType].Next;
            break;
        }

        for (int i = 0; i < count; i++)
        {
            int thisType = GetJoiningType((int)info[i].Codepoint);
            if (thisType == JoiningTypeT)
            {
                info[i].ShaperPosition = None;
                continue;
            }

            (byte prevAction, byte currAction, byte next) = s_stateTable[state, thisType];
            if (prevAction != None && prev != -1)
            {
                info[prev].ShaperPosition = prevAction;
                buffer.SafeToInsertTatweel(prev, i + 1);
            }
            else if (prev == -1)
            {
                if (thisType >= JoiningTypeR)
                {
                    buffer.UnsafeToConcatFromOutbuffer(0, i + 1);
                }
            }
            else if (thisType >= JoiningTypeR || (state >= 2 && state <= 5))
            {
                // States 2 to 5 have a possible previous action.
                buffer.UnsafeToConcat(prev, i + 1);
            }

            info[i].ShaperPosition = currAction;
            prev = i;
            state = next;
        }

        // The text after the run.
        foreach (int u in buffer.PostContext)
        {
            int thisType = GetJoiningType(u);
            if (thisType == JoiningTypeT)
            {
                continue;
            }

            byte prevAction = s_stateTable[state, thisType].Prev;
            if (prevAction != None && prev != -1)
            {
                info[prev].ShaperPosition = prevAction;
                buffer.SafeToInsertTatweel(prev, buffer.Len);
            }
            else if (state >= 2 && state <= 5)
            {
                buffer.UnsafeToConcat(prev, buffer.Len);
            }

            break;
        }
    }

    private static void MongolianVariationSelectors(LayoutBuffer buffer)
    {
        // The Mongolian free variation selectors take the action of the character they follow.
        GlyphInfo[] info = buffer.Info;
        for (int i = 1; i < buffer.Len; i++)
        {
            if (info[i].Codepoint is (>= 0x180B and <= 0x180D) or 0x180F)
            {
                info[i].ShaperPosition = info[i - 1].ShaperPosition;
            }
        }
    }

    // ---- Fallback shaping ----

    private static bool FallbackShape(ShapePlan plan, ShapeFont font, LayoutBuffer buffer)
    {
        PlanData data = (PlanData)plan.ShaperData!;
        if (!data.DoFallback)
        {
            return false;
        }

        FallbackPlan fallback = data.Fallback ??= FallbackPlan.Create(plan, font);
        Interlocked.CompareExchange(ref data.Fallback, fallback, null);
        data.Fallback.Shape(font, buffer);
        return true;
    }

    /// <summary>
    /// GSUB lookups synthesized from the Arabic presentation forms the font maps (or, for a font laid out in Windows-1256
    /// order, hand-coded ones), applied in place of the font's when it has no Arabic GSUB.
    /// </summary>
    private sealed class FallbackPlan
    {
        private static readonly string[] s_fallbackFeatures = ["init", "medi", "fina", "isol", "rlig", "rlig", "rlig"];

        private readonly uint[] _masks;
        private readonly LayoutTable? _table;

        private FallbackPlan(uint[] masks, LayoutTable? table)
        {
            _masks = masks;
            _table = table;
        }

        public static FallbackPlan Create(ShapePlan plan, ShapeFont font)
        {
            return CreateUnicode(plan, font) ?? CreateWin1256(plan, font) ?? new FallbackPlan([], null);
        }

        private static FallbackPlan? CreateUnicode(ShapePlan plan, ShapeFont font)
        {
            List<uint> masks = [];
            GsubBuilder gsub = new();
            for (int i = 0; i < s_fallbackFeatures.Length; i++)
            {
                uint mask = plan.Map.Get1Mask(ShapeMapBuilder.Tag(s_fallbackFeatures[i]));
                if (mask == 0)
                {
                    continue;
                }

                bool added = i switch
                {
                    < 4 => SynthesizeSingle(gsub, font, i),
                    4 => SynthesizeLigature(gsub, font, ArabicTables.Ligatures3, LookupFlag.IgnoreMarks),
                    5 => SynthesizeLigature(gsub, font, ArabicTables.Ligatures, LookupFlag.IgnoreMarks),
                    _ => SynthesizeLigature(gsub, font, ArabicTables.MarkLigatures, 0),
                };
                if (added)
                {
                    masks.Add(mask);
                }
            }

            return masks.Count == 0 ? null : new FallbackPlan([.. masks], gsub.Build());
        }

        private static bool SynthesizeSingle(GsubBuilder gsub, ShapeFont font, int featureIndex)
        {
            List<(ushort Glyph, ushort Substitute)> pairs = [];
            ReadOnlySpan<ushort> table = ArabicTables.ShapingTable;
            for (int u = ArabicTables.ShapingTableFirst; u <= ArabicTables.ShapingTableLast; u++)
            {
                int s = table[((u - ArabicTables.ShapingTableFirst) * 4) + featureIndex];
                if (s == 0 || !font.TryGetNominalGlyph(u, out uint uGlyph) || !font.TryGetNominalGlyph(s, out uint sGlyph) ||
                    uGlyph == sGlyph || uGlyph > 0xFFFF || sGlyph > 0xFFFF)
                {
                    continue;
                }

                pairs.Add(((ushort)uGlyph, (ushort)sGlyph));
            }

            if (pairs.Count == 0)
            {
                return false;
            }

            // A stable sort by glyph, as HarfBuzz's.
            pairs = [.. pairs.OrderBy(p => p.Glyph)];
            gsub.AddLookup(1, LookupFlag.IgnoreMarks, [GsubBuilder.SingleFormat2([.. pairs.Select(p => p.Glyph)], [.. pairs.Select(p => p.Substitute)])]);
            return true;
        }

        private static bool SynthesizeLigature(GsubBuilder gsub, ShapeFont font,
            (ushort First, (ushort[] Components, ushort Ligature)[] Ligatures)[] table, int lookupFlag)
        {
            List<(ushort Glyph, int Index)> firsts = [];
            for (int i = 0; i < table.Length; i++)
            {
                if (font.TryGetNominalGlyph(table[i].First, out uint glyph))
                {
                    firsts.Add(((ushort)glyph, i));
                }
            }

            firsts = [.. firsts.OrderBy(f => f.Glyph)];
            List<(ushort Ligature, ushort[] Components)>[] sets = new List<(ushort, ushort[])>[firsts.Count];
            int count = 0;
            for (int i = 0; i < firsts.Count; i++)
            {
                sets[i] = [];
                foreach ((ushort[] components, ushort ligature) in table[firsts[i].Index].Ligatures)
                {
                    if (!font.TryGetNominalGlyph(ligature, out uint ligatureGlyph))
                    {
                        continue;
                    }

                    ushort[] componentGlyphs = new ushort[components.Length];
                    bool matched = true;
                    for (int j = 0; j < components.Length; j++)
                    {
                        if (components[j] == 0 || !font.TryGetNominalGlyph(components[j], out uint componentGlyph))
                        {
                            matched = false;
                            break;
                        }

                        componentGlyphs[j] = (ushort)componentGlyph;
                    }

                    if (matched)
                    {
                        sets[i].Add(((ushort)ligatureGlyph, componentGlyphs));
                        count++;
                    }
                }
            }

            if (count == 0)
            {
                return false;
            }

            gsub.AddLookup(4, lookupFlag, [GsubBuilder.LigatureFormat1([.. firsts.Select(f => f.Glyph)], sets)]);
            return true;
        }

        private static FallbackPlan? CreateWin1256(ShapePlan plan, ShapeFont font)
        {
            // Does the font look like it is laid out in Windows-1256 order?
            if (!(Is(0x0627, 199) && Is(0x0644, 225) && Is(0x0649, 236) && Is(0x064A, 237) && Is(0x0652, 250)))
            {
                return null;
            }

            static byte[] Single((ushort[] From, ushort[] To) s) => GsubBuilder.SingleFormat2(s.From, s.To);

            static byte[] Ligature((ushort First, (ushort Component, ushort Ligature)[] Ligatures) l) =>
                GsubBuilder.LigatureFormat1([l.First], [[.. l.Ligatures.Select(p => (p.Ligature, new[] { p.Component }))]]);

            byte[] initMedi = Single(ArabicTables.Win1256InitmediSubLookup);
            byte[] mediFinaLamAlef = Single(ArabicTables.Win1256MedifinaLamAlefSubLookup);
            (string Tag, int Type, int Flag, byte[][] Subtables)[] manifest =
            [
                ("rlig", 4, LookupFlag.IgnoreMarks, [Ligature(ArabicTables.Win1256LamInitLigatures)]),
                ("init", 1, LookupFlag.IgnoreMarks, [initMedi, Single(ArabicTables.Win1256InitSubLookup)]),
                ("medi", 1, LookupFlag.IgnoreMarks, [initMedi, Single(ArabicTables.Win1256MediSubLookup), mediFinaLamAlef]),
                ("fina", 1, LookupFlag.IgnoreMarks, [Single(ArabicTables.Win1256FinaSubLookup), mediFinaLamAlef]),
                ("rlig", 4, 0, [Ligature(ArabicTables.Win1256ShaddaLigatures)]),
            ];

            List<uint> masks = [];
            GsubBuilder gsub = new();
            foreach ((string tag, int type, int flag, byte[][] subtables) in manifest)
            {
                uint mask = plan.Map.Get1Mask(ShapeMapBuilder.Tag(tag));
                if (mask != 0)
                {
                    masks.Add(mask);
                    gsub.AddLookup(type, flag, subtables);
                }
            }

            return masks.Count == 0 ? null : new FallbackPlan([.. masks], gsub.Build());

            bool Is(int u, uint glyph) => font.TryGetNominalGlyph(u, out uint g) && g == glyph;
        }

        public void Shape(ShapeFont font, LayoutBuffer buffer)
        {
            if (_table is null)
            {
                return;
            }

            LayoutApplier c = new(_table, font.Layout.Gdef, buffer) { VariationDelta = font.VariationDelta };
            for (int i = 0; i < _masks.Length; i++)
            {
                LayoutLookup? lookup = _table.GetLookup(i);
                if (lookup is null)
                {
                    continue;
                }

                c.LookupIndex = i;
                c.SetLookupMask(_masks[i]);
                c.ApplyString(lookup);
            }
        }
    }

    // ---- Stretch ('stch') ----

    private static bool RecordStch(ShapePlan plan, ShapeFont font, LayoutBuffer buffer)
    {
        PlanData data = (PlanData)plan.ShaperData!;
        if (!data.HasStch)
        {
            return false;
        }

        // 'stch' was just applied: whatever multiplied is a stretch, its odd components the repeating tiles.
        GlyphInfo[] info = buffer.Info;
        for (int i = 0; i < buffer.Len; i++)
        {
            if (LayoutApplier.IsMultiplied(info[i]))
            {
                int comp = LayoutApplier.LigComp(info[i]);
                info[i].ShaperPosition = comp % 2 != 0 ? StchRepeating : StchFixed;
                buffer.ScratchFlags |= ScratchHasStch;
            }
        }

        return false;
    }

    private static bool IsStch(byte action) => action is StchFixed or StchRepeating;

    private static bool IsWordCategory(int genCat) => genCat is UnicodeProps.Unassigned or UnicodeProps.PrivateUse or UnicodeProps.ModifierLetter or
        UnicodeProps.OtherLetter or UnicodeProps.SpacingMark or UnicodeProps.EnclosingMark or UnicodeProps.NonspacingMark or
        UnicodeProps.DecimalNumber or UnicodeProps.LetterNumber or UnicodeProps.OtherNumber or UnicodeProps.CurrencySymbol or
        UnicodeProps.ModifierSymbol or UnicodeProps.MathSymbol or UnicodeProps.OtherSymbol;

    public override void PostprocessGlyphs(ShapePlan plan, LayoutBuffer buffer, ShapeFont font) => ApplyStch(buffer, font);

    private static void ApplyStch(LayoutBuffer buffer, ShapeFont font)
    {
        if ((buffer.ScratchFlags & ScratchHasStch) == 0)
        {
            return;
        }

        bool rtl = buffer.Direction == LayoutDirection.RightToLeft;
        if (!rtl)
        {
            buffer.Reverse();
        }

        // Two passes: measure how many glyphs the tiles add, make room, then cut them in, copying to the end of the buffer.
        long extraGlyphsNeeded = 0;
        for (int step = 0; step <= 1; step++)
        {
            bool cut = step == 1;
            int count = buffer.Len;
            GlyphInfo[] info = buffer.Info;
            GlyphPos[] pos = buffer.Pos;
            int newLen = count + (int)extraGlyphsNeeded;
            int j = newLen;
            for (int i = count; i > 0; i--)
            {
                if (!IsStch(info[i - 1].ShaperPosition))
                {
                    if (cut)
                    {
                        --j;
                        info[j] = info[i - 1];
                        pos[j] = pos[i - 1];
                    }

                    continue;
                }

                // Justification.
                int wTotal = 0, wFixed = 0, wRepeating = 0;
                int nFixed = 0, nRepeating = 0;
                int end = i;
                while (i > 0 && IsStch(info[i - 1].ShaperPosition))
                {
                    i--;
                    int width = font.GetHAdvance(info[i].Codepoint);
                    if (info[i].ShaperPosition == StchFixed)
                    {
                        wFixed = LayoutApplier.SaturateAdd(wFixed, width);
                        nFixed++;
                    }
                    else
                    {
                        wRepeating = LayoutApplier.SaturateAdd(wRepeating, width);
                        nRepeating++;
                    }
                }

                int start = i;
                int context = i;
                while (context > 0 && !IsStch(info[context - 1].ShaperPosition) &&
                    (UnicodeProps.IsDefaultIgnorable(info[context - 1]) || IsWordCategory(UnicodeProps.GeneralCategory(info[context - 1]))))
                {
                    context--;
                    wTotal = LayoutApplier.SaturateAdd(wTotal, pos[context].XAdvance);
                }

                i++;

                // How many more times to repeat each repeating tile.
                long nCopies = 0;
                long wRemainingSigned = (long)wTotal - wFixed;
                long wRepeatingSigned = wRepeating;
                int wRemaining = LayoutApplier.SaturateSub(wTotal, wFixed);
                if (wRemainingSigned > wRepeatingSigned && wRepeatingSigned > 0)
                {
                    nCopies = (wRemainingSigned / wRepeatingSigned) - 1;
                }

                // An extra repeat squeezed in may fit better.
                int extraRepeatOverlap = 0;
                long shortfall = wRemainingSigned - (wRepeatingSigned * (nCopies + 1));
                if (shortfall > 0 && nRepeating > 0)
                {
                    ++nCopies;
                    long excess = ((nCopies + 1) * wRepeatingSigned) - wRemainingSigned;
                    if (excess > 0)
                    {
                        extraRepeatOverlap = (int)Math.Clamp(excess / (nCopies * nRepeating), int.MinValue, int.MaxValue);
                        wRemaining = 0;
                    }
                }

                long maxCopies = 0;
                if (nRepeating > 0)
                {
                    int baseGlyphs = nFixed + nRepeating;
                    if (baseGlyphs < StchMaxGlyphs)
                    {
                        maxCopies = (StchMaxGlyphs - baseGlyphs) / nRepeating;
                    }
                }

                nCopies = Math.Min(nCopies, maxCopies);
                if (!cut)
                {
                    extraGlyphsNeeded += nCopies * nRepeating;
                }
                else
                {
                    buffer.UnsafeToBreak(context, end);
                    int xOffset = wRemaining / 2;
                    for (int k = end; k > start; k--)
                    {
                        int width = font.GetHAdvance(info[k - 1].Codepoint);
                        long repeat = 1;
                        if (info[k - 1].ShaperPosition == StchRepeating)
                        {
                            repeat += nCopies;
                        }

                        pos[k - 1].XAdvance = 0;
                        for (long n = 0; n < repeat; n++)
                        {
                            if (rtl)
                            {
                                xOffset = LayoutApplier.SaturateSub(xOffset, width);
                                if (n > 0)
                                {
                                    xOffset = LayoutApplier.SaturateAdd(xOffset, extraRepeatOverlap);
                                }
                            }

                            pos[k - 1].XOffset = xOffset;

                            // Append the copy.
                            --j;
                            info[j] = info[k - 1];
                            pos[j] = pos[k - 1];

                            if (!rtl)
                            {
                                xOffset = LayoutApplier.SaturateAdd(xOffset, width);
                                if (n > 0)
                                {
                                    xOffset = LayoutApplier.SaturateSub(xOffset, extraRepeatOverlap);
                                }
                            }
                        }
                    }
                }
            }

            if (!cut)
            {
                long total = count + extraGlyphsNeeded;
                if (total > buffer.MaxLen)
                {
                    break;
                }

                buffer.Ensure((int)total);
            }
            else
            {
                buffer.Len = newLen;
            }
        }

        if (!rtl)
        {
            buffer.Reverse();
        }
    }

    // ---- Mark reordering ----

    private static bool IsModifierCombiningMark(in GlyphInfo info) => Array.IndexOf(s_modifierCombiningMarks, info.Codepoint) >= 0;

    public override bool HasReorderMarks => true;

    public override void ReorderMarks(ShapePlan plan, LayoutBuffer buffer, int start, int end)
    {
        GlyphInfo[] info = buffer.Info;
        int i = start;
        for (int cc = 220; cc <= 230; cc += 10)
        {
            while (i < end && UnicodeProps.GetModifiedCombiningClass(info[i]) < cc)
            {
                i++;
            }

            if (i == end)
            {
                break;
            }

            if (UnicodeProps.GetModifiedCombiningClass(info[i]) > cc)
            {
                continue;
            }

            int j = i;
            while (j < end && UnicodeProps.GetModifiedCombiningClass(info[j]) == cc && IsModifierCombiningMark(info[j]))
            {
                j++;
            }

            if (i == j)
            {
                continue;
            }

            // Shift the modifier marks to the front.
            buffer.MergeClusters(start, j);
            GlyphInfo[] temp = info[i..j];
            Array.Copy(info, start, info, start + j - i, i - start);
            Array.Copy(temp, 0, info, start, j - i);

            // Renumber them 22 and 26 (folded back to 220 and 230 by fallback mark positioning) so the sequence stays sorted for
            // the normalizer's CGJ handling.
            int newStart = start + j - i;
            int newCc = cc == 220 ? 25 : 26;
            while (start < newStart)
            {
                UnicodeProps.SetModifiedCombiningClass(ref info[start], newCc);
                start++;
            }

            i = j;
        }
    }

    public override ZeroWidthMarks ZeroWidthMarks => ZeroWidthMarks.ByGdefLate;
}
