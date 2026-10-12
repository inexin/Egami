// Translated from HarfBuzz's hb-ot-shape.cc and hb-ot-shape-fallback.cc (MIT); see THIRD-PARTY-NOTICES.md.
using Egami.Fonts.Unicode;

namespace Egami.Fonts.Shaping;

/// <summary>
/// The OpenType shaping pipeline over one run in one font, as HarfBuzz's hb_ot_shape_internal: Unicode properties, clusters,
/// normalization, masks, GSUB, positioning and GPOS, and the fallbacks for fonts that lack them.
/// </summary>
internal static class OtShaper
{
    public static readonly HashSet<string> RightToLeftScripts =
    [
        "Arab", "Hebr", "Syrc", "Thaa", "Cprt", "Khar", "Phnx", "Nkoo", "Lydi", "Avst", "Armi", "Phli", "Prti", "Sarb", "Orkh", "Samr",
        "Mand", "Merc", "Mero", "Mani", "Mend", "Nbat", "Narb", "Palm", "Phlp", "Hatr", "Adlm", "Rohg", "Sogo", "Sogd", "Elym", "Chrs",
        "Yezi", "Ougr", "Gara", "Sidt",
    ];

    private static readonly HashSet<string> s_noHorizontalDirection = ["Hung", "Ital", "Runr", "Tfng"];

    /// <summary>The direction a script is written in horizontally; Invalid for scripts written either way.</summary>
    public static LayoutDirection HorizontalDirection(Script script)
    {
        string code = script.Code;
        if (RightToLeftScripts.Contains(code))
        {
            return LayoutDirection.RightToLeft;
        }

        return s_noHorizontalDirection.Contains(code) ? LayoutDirection.Invalid : LayoutDirection.LeftToRight;
    }

    /// <summary>
    /// The OpenType script tags HarfBuzz tries for a script: the Indic v3 and v2 tags before the old one, Myanmar's mym2 before mymr.
    /// </summary>
    public static uint[] ScriptTags(Script script)
    {
        List<uint> tags = [];
        string code = script.Code;
        uint? newTag = code switch
        {
            "Beng" => ShapeMapBuilder.Tag("bng2"),
            "Deva" => ShapeMapBuilder.Tag("dev2"),
            "Gujr" => ShapeMapBuilder.Tag("gjr2"),
            "Guru" => ShapeMapBuilder.Tag("gur2"),
            "Knda" => ShapeMapBuilder.Tag("knd2"),
            "Mlym" => ShapeMapBuilder.Tag("mlm2"),
            "Orya" => ShapeMapBuilder.Tag("ory2"),
            "Taml" => ShapeMapBuilder.Tag("tml2"),
            "Telu" => ShapeMapBuilder.Tag("tel2"),
            "Mymr" => ShapeMapBuilder.Tag("mym2"),
            _ => null,
        };
        if (newTag is uint tag)
        {
            if (code != "Mymr")
            {
                tags.Add((tag & 0xFFFFFF00) | '3');
            }

            tags.Add(tag);
        }

        if (script != Script.Unknown)
        {
            uint old = code switch
            {
                "Hira" or "Hrkt" => ShapeMapBuilder.Tag("kana"),
                "Laoo" => ShapeMapBuilder.Tag("lao "),
                "Yiii" => ShapeMapBuilder.Tag("yi  "),
                "Nkoo" => ShapeMapBuilder.Tag("nko "),
                "Vaii" => ShapeMapBuilder.Tag("vai "),
                _ => ShapeMapBuilder.Tag(code) | 0x20000000u,
            };
            tags.Add(old);
        }

        return [.. tags];
    }

    /// <summary>Shapes the characters in <paramref name="buffer"/> (code points with clusters) into positioned glyphs.</summary>
    public static void Shape(ShapePlan plan, ShapeFont font, LayoutBuffer buffer)
    {
        LayoutDirection targetDirection = buffer.Direction;
        buffer.Enter();
        try
        {
            SetUnicodeProps(buffer, plan.Map.GlobalMask);
            InsertDottedCircle(buffer, font);
            FormClusters(buffer);
            EnsureNativeDirection(buffer, plan.Script);
            plan.Shaper.PreprocessText(plan, buffer, font);
            SubstitutePre(plan, font, buffer, targetDirection);
            Position(plan, font, buffer);
            SubstitutePost(plan, font, buffer);
            PropagateFlags(buffer);
        }
        catch (LayoutLimitException)
        {
            // The font drove the buffer past its limits; what was shaped so far stands, as HarfBuzz leaves it.
            if (!buffer.HavePositions)
            {
                buffer.ClearPositions();
            }

            buffer.Successful = false;
        }

        if (!buffer.Successful)
        {
            Typeface.Report(font.Face.Options, "Font.ShapingLimit", "The font's layout tables exceeded the shaping limits; the text is shaped as far as the limits allow.", -1);
        }

        buffer.Direction = targetDirection;
    }

    // ---- Prepare ----

    private static bool IsRegionalIndicator(uint u) => u is >= 0x1F1E6 and <= 0x1F1FF;

    private static void SetUnicodeProps(LayoutBuffer buffer, uint globalMask)
    {
        int count = buffer.Len;
        GlyphInfo[] info = buffer.Info;
        for (int i = 0; i < count; i++)
        {
            info[i].Mask = globalMask;
            UnicodeProps.Set(ref info[i], buffer);
            if (info[i].Codepoint < 0x80)
            {
                continue;
            }

            int genCat = UnicodeProps.GeneralCategory(info[i]);
            if (genCat is UnicodeProps.LowercaseLetter or UnicodeProps.UppercaseLetter or UnicodeProps.TitlecaseLetter or UnicodeProps.OtherLetter or UnicodeProps.SpaceSeparator)
            {
                continue;
            }

            if (genCat == UnicodeProps.ModifierSymbol && info[i].Codepoint is >= 0x1F3FB and <= 0x1F3FF)
            {
                UnicodeProps.SetContinuation(ref info[i], buffer);
            }
            else if (i > 0 && IsRegionalIndicator(info[i].Codepoint))
            {
                if (IsRegionalIndicator(info[i - 1].Codepoint) && !UnicodeProps.IsContinuation(info[i - 1]))
                {
                    UnicodeProps.SetContinuation(ref info[i], buffer);
                }
            }
            else if (UnicodeProps.IsZwj(info[i]))
            {
                UnicodeProps.SetContinuation(ref info[i], buffer);
                if (i + 1 < count && UnicodeData.HasFlag((int)info[i + 1].Codepoint, UnicodeTables.ExtendedPictographicFlag))
                {
                    i++;
                    info[i].Mask = globalMask;
                    UnicodeProps.Set(ref info[i], buffer);
                    UnicodeProps.SetContinuation(ref info[i], buffer);
                }
            }
            else if (info[i].Codepoint is (>= 0xFF9E and <= 0xFF9F) or (>= 0xE0020 and <= 0xE007F))
            {
                UnicodeProps.SetContinuation(ref info[i], buffer);
            }
            else if (info[i].Codepoint == 0x2044)
            {
                buffer.ScratchFlags |= LayoutBuffer.ScratchHasFractionSlash;
            }
        }
    }

    private static void InsertDottedCircle(LayoutBuffer buffer, ShapeFont font)
    {
        if (buffer.DoNotInsertDottedCircle || !buffer.BeginningOfText || buffer.PreContext.Length > 0 || buffer.Len == 0 ||
            !UnicodeProps.IsUnicodeMark(buffer.Info[0]) || !font.HasGlyph(0x25CC))
        {
            return;
        }

        GlyphInfo dottedCircle = new() { Codepoint = 0x25CC };
        UnicodeProps.Set(ref dottedCircle, buffer);
        buffer.ClearOutput();
        buffer.Idx = 0;
        GlyphInfo info = dottedCircle;
        info.Cluster = buffer.Cur().Cluster;
        info.Mask = buffer.Cur().Mask;
        buffer.OutputInfo(info);
        buffer.Sync();
    }

    private static void FormClusters(LayoutBuffer buffer)
    {
        if ((buffer.ScratchFlags & LayoutBuffer.ScratchHasContinuations) == 0)
        {
            return;
        }

        int count = buffer.Len;
        int start = 0;
        for (int i = 1; i <= count; i++)
        {
            if (i == count || !UnicodeProps.IsContinuation(buffer.Info[i]))
            {
                buffer.MergeGraphemeClusters(start, i);
                start = i;
            }
        }
    }

    private static void EnsureNativeDirection(LayoutBuffer buffer, Script script)
    {
        LayoutDirection direction = buffer.Direction;
        LayoutDirection horizDir = HorizontalDirection(script);
        if (horizDir == LayoutDirection.RightToLeft && direction == LayoutDirection.LeftToRight)
        {
            bool foundNumber = false, foundLetter = false, foundRi = false;
            for (int i = 0; i < buffer.Len; i++)
            {
                int gc = UnicodeProps.GeneralCategory(buffer.Info[i]);
                if (UnicodeProps.IsLetterCategory(gc))
                {
                    foundLetter = true;
                    break;
                }

                if (gc == UnicodeProps.DecimalNumber)
                {
                    foundNumber = true;
                }
                else if (IsRegionalIndicator(buffer.Info[i].Codepoint))
                {
                    foundRi = true;
                }
            }

            if ((foundNumber || foundRi) && !foundLetter)
            {
                horizDir = LayoutDirection.LeftToRight;
            }
        }

        if ((LayoutBuffer.IsHorizontal(direction) && direction != horizDir && horizDir != LayoutDirection.Invalid) ||
            (LayoutBuffer.IsVertical(direction) && direction != LayoutDirection.TopToBottom))
        {
            ReverseGraphemes(buffer);
            buffer.Direction = LayoutBuffer.Reverse(buffer.Direction);
        }
    }

    private static void ReverseGraphemes(LayoutBuffer buffer)
    {
        bool merge = buffer.ClusterLevel == LayoutClusterLevel.MonotoneCharacters;
        int len = buffer.Len;
        if (len == 0)
        {
            return;
        }

        int start = 0;
        int i;
        for (i = 1; i < len; i++)
        {
            if (!UnicodeProps.IsContinuation(buffer.Info[i]))
            {
                if (merge)
                {
                    buffer.MergeClusters(start, i);
                }

                buffer.ReverseRange(start, i);
                start = i;
            }
        }

        if (merge)
        {
            buffer.MergeClusters(start, i);
        }

        buffer.ReverseRange(start, i);
        buffer.Reverse();
    }

    // ---- Substitute ----

    private static void RotateChars(ShapePlan plan, ShapeFont font, LayoutBuffer buffer, LayoutDirection targetDirection)
    {
        int count = buffer.Len;
        GlyphInfo[] info = buffer.Info;
        if (LayoutBuffer.IsBackward(targetDirection))
        {
            uint rtlmMask = plan.RtlmMask;
            for (int i = 0; i < count; i++)
            {
                if (UnicodeData.TryGetBidiMirroringGlyph((int)info[i].Codepoint, out int mirror) && font.HasGlyph(mirror))
                {
                    info[i].Codepoint = (uint)mirror;
                }
                else
                {
                    info[i].Mask |= rtlmMask;
                }
            }
        }

        if (LayoutBuffer.IsVertical(targetDirection) && !plan.HasVert)
        {
            for (int i = 0; i < count; i++)
            {
                int vertical = VerticalCharFor((int)info[i].Codepoint);
                if (vertical != info[i].Codepoint && font.HasGlyph(vertical))
                {
                    info[i].Codepoint = (uint)vertical;
                }
            }
        }
    }

    private static int VerticalCharFor(int u) => (u >> 8) switch
    {
        0x20 => u switch
        {
            0x2013 => 0xFE32,
            0x2014 => 0xFE31,
            0x2025 => 0xFE30,
            0x2026 => 0xFE19,
            _ => u,
        },
        0x30 => u switch
        {
            0x3001 => 0xFE11,
            0x3002 => 0xFE12,
            0x3008 => 0xFE3F,
            0x3009 => 0xFE40,
            0x300A => 0xFE3D,
            0x300B => 0xFE3E,
            0x300C => 0xFE41,
            0x300D => 0xFE42,
            0x300E => 0xFE43,
            0x300F => 0xFE44,
            0x3010 => 0xFE3B,
            0x3011 => 0xFE3C,
            0x3014 => 0xFE39,
            0x3015 => 0xFE3A,
            0x3016 => 0xFE17,
            0x3017 => 0xFE18,
            _ => u,
        },
        0xFE => u switch
        {
            0xFE4F => 0xFE34,
            _ => u,
        },
        0xFF => u switch
        {
            0xFF01 => 0xFE15,
            0xFF08 => 0xFE35,
            0xFF09 => 0xFE36,
            0xFF0C => 0xFE10,
            0xFF1A => 0xFE13,
            0xFF1B => 0xFE14,
            0xFF1F => 0xFE16,
            0xFF3B => 0xFE47,
            0xFF3D => 0xFE48,
            0xFF3F => 0xFE33,
            0xFF5B => 0xFE37,
            0xFF5D => 0xFE38,
            _ => u,
        },
        _ => u,
    };

    private static void SetupMasksFraction(ShapePlan plan, LayoutBuffer buffer)
    {
        if ((buffer.ScratchFlags & LayoutBuffer.ScratchHasFractionSlash) == 0 || !plan.HasFrac)
        {
            return;
        }

        uint preMask, postMask;
        if (LayoutBuffer.IsForward(buffer.Direction))
        {
            preMask = plan.NumrMask | plan.FracMask;
            postMask = plan.FracMask | plan.DnomMask;
        }
        else
        {
            preMask = plan.FracMask | plan.DnomMask;
            postMask = plan.NumrMask | plan.FracMask;
        }

        int count = buffer.Len;
        GlyphInfo[] info = buffer.Info;
        for (int i = 0; i < count; i++)
        {
            if (info[i].Codepoint != 0x2044)
            {
                continue;
            }

            int start = i, end = i + 1;
            while (start > 0 && UnicodeProps.GeneralCategory(info[start - 1]) == UnicodeProps.DecimalNumber)
            {
                start--;
            }

            while (end < count && UnicodeProps.GeneralCategory(info[end]) == UnicodeProps.DecimalNumber)
            {
                end++;
            }

            if (start == i || end == i + 1)
            {
                if (start == i)
                {
                    buffer.UnsafeToConcat(start, start + 1);
                }

                if (end == i + 1)
                {
                    buffer.UnsafeToConcat(end - 1, end);
                }

                continue;
            }

            buffer.UnsafeToBreak(start, end);
            for (int j = start; j < i; j++)
            {
                info[j].Mask |= preMask;
            }

            info[i].Mask |= plan.FracMask;
            for (int j = i + 1; j < end; j++)
            {
                info[j].Mask |= postMask;
            }

            i = end - 1;
        }
    }

    private static void SetupMasks(ShapePlan plan, ShapeFont font, LayoutBuffer buffer)
    {
        SetupMasksFraction(plan, buffer);
        plan.Shaper.SetupMasks(plan, buffer, font);
        foreach (FeatureSetting feature in plan.UserFeatures)
        {
            if (!feature.IsGlobal)
            {
                uint mask = plan.Map.GetMask(feature.Tag, out int shift);
                buffer.SetMasks(feature.Value << shift, mask, feature.Start, feature.End);
            }
        }
    }

    private static void SubstitutePre(ShapePlan plan, ShapeFont font, LayoutBuffer buffer, LayoutDirection targetDirection)
    {
        // Default substitution: mirroring, normalization, masks and glyph mapping.
        RotateChars(plan, font, buffer, targetDirection);
        Normalizer.Normalize(plan, buffer, font);
        SetupMasks(plan, font, buffer);
        if (plan.FallbackMarkPositioning)
        {
            Fallback.RecategorizeMarks(buffer);
        }

        for (int i = 0; i < buffer.Len; i++)
        {
            buffer.Info[i].Codepoint = buffer.Info[i].NormalizerGlyph;
        }

        // The plan's substitutions.
        GdefTable gdef = font.Layout.Gdef;
        for (int i = 0; i < buffer.Len; i++)
        {
            buffer.Info[i].GlyphProps = (ushort)gdef.GetGlyphProps(buffer.Info[i].Codepoint);
            buffer.Info[i].LigProps = 0;
        }

        if (plan.FallbackGlyphClasses)
        {
            SynthesizeGlyphClasses(buffer);
        }

        ApplyLookups(plan, font, buffer, 0);
    }

    private static void SynthesizeGlyphClasses(LayoutBuffer buffer)
    {
        for (int i = 0; i < buffer.Len; i++)
        {
            ref GlyphInfo info = ref buffer.Info[i];
            int klass = UnicodeProps.GeneralCategory(info) != UnicodeProps.NonspacingMark || UnicodeProps.IsDefaultIgnorable(info)
                ? GlyphProps.BaseGlyph
                : GlyphProps.Mark;
            info.GlyphProps = (ushort)klass;
        }
    }

    /// <summary>Applies the plan's GSUB (0) or GPOS (1) lookups stage by stage, as HarfBuzz's hb_ot_map_t::apply.</summary>
    private static void ApplyLookups(ShapePlan plan, ShapeFont font, LayoutBuffer buffer, int tableIndex)
    {
        LayoutTable table = tableIndex == 0 ? font.Layout.Gsub : font.Layout.Gpos;
        LayoutApplier c = new(table, font.Layout.Gdef, buffer)
        {
            VariationDelta = font.VariationDelta,
            XMult = font.XMult,
            YMult = font.YMult,
            XMultF = font.XMultF,
            YMultF = font.YMultF,
        };
        List<ShapeMap.LookupMap> lookups = plan.Map.Lookups[tableIndex];
        int i = 0;
        foreach (ShapeMap.StageMap stage in plan.Map.Stages[tableIndex])
        {
            for (; i < stage.LastLookup; i++)
            {
                ShapeMap.LookupMap lookup = lookups[i];
                LayoutLookup? l = table.GetLookup(lookup.Index);
                if (l is null)
                {
                    continue;
                }

                c.LookupIndex = lookup.Index;
                c.SetLookupMask(lookup.Mask);
                c.AutoZwj = lookup.AutoZwj;
                c.AutoZwnj = lookup.AutoZwnj;
                c.Random = lookup.Random;
                c.PerSyllable = lookup.PerSyllable;
                c.Direction = buffer.Direction;
                c.ApplyString(l);
            }

            stage.PauseFunc?.Invoke(plan, font, buffer);
        }
    }

    private static void SubstitutePost(ShapePlan plan, ShapeFont font, LayoutBuffer buffer)
    {
        DealWithVariationSelectors(buffer);
        HideDefaultIgnorables(buffer, font);
        plan.Shaper.PostprocessGlyphs(plan, buffer, font);
    }

    private static void DealWithVariationSelectors(LayoutBuffer buffer)
    {
        if ((buffer.ScratchFlags & LayoutBuffer.ScratchHasVariationSelectorFallback) == 0 || buffer.NotFoundVariationSelector == uint.MaxValue)
        {
            return;
        }

        for (int i = 0; i < buffer.Len; i++)
        {
            if (UnicodeProps.IsVariationSelector(buffer.Info[i]))
            {
                buffer.Info[i].Codepoint = buffer.NotFoundVariationSelector;
                buffer.Pos[i] = default;
                UnicodeProps.SetVariationSelector(ref buffer.Info[i], false);
            }
        }
    }

    private static void HideDefaultIgnorables(LayoutBuffer buffer, ShapeFont font)
    {
        if ((buffer.ScratchFlags & LayoutBuffer.ScratchHasDefaultIgnorables) == 0 || buffer.PreserveDefaultIgnorables)
        {
            return;
        }

        uint invisible = buffer.Invisible;
        if (!buffer.RemoveDefaultIgnorables && (invisible != 0 || font.TryGetNominalGlyph(' ', out invisible)))
        {
            for (int i = 0; i < buffer.Len; i++)
            {
                if (UnicodeProps.IsDefaultIgnorable(buffer.Info[i]))
                {
                    buffer.Info[i].Codepoint = invisible;
                }
            }
        }
        else
        {
            buffer.DeleteGlyphsInplace(static info => UnicodeProps.IsDefaultIgnorable(info));
        }
    }

    // ---- Position ----

    private static void Position(ShapePlan plan, ShapeFont font, LayoutBuffer buffer)
    {
        buffer.ClearPositions();
        PositionDefault(plan, font, buffer);
        PositionPlan(plan, font, buffer);
        if (LayoutBuffer.IsBackward(buffer.Direction))
        {
            buffer.Reverse();
        }
    }

    private static void PositionDefault(ShapePlan plan, ShapeFont font, LayoutBuffer buffer)
    {
        int count = buffer.Len;
        if (LayoutBuffer.IsHorizontal(buffer.Direction))
        {
            for (int i = 0; i < count; i++)
            {
                buffer.Pos[i].XAdvance = font.GetHAdvance(buffer.Info[i].Codepoint);
            }
        }
        else
        {
            // The v origin is subtracted even when it is only the fallback.
            for (int i = 0; i < count; i++)
            {
                uint glyph = buffer.Info[i].Codepoint;
                buffer.Pos[i].YAdvance = font.GetVAdvance(glyph);
                font.GetVOrigin(glyph, out int x, out int y);
                buffer.Pos[i].XOffset -= x;
                buffer.Pos[i].YOffset -= y;
            }
        }

        if ((buffer.ScratchFlags & LayoutBuffer.ScratchHasSpaceFallback) != 0)
        {
            Fallback.Spaces(font, buffer);
        }
    }

    private static void PositionPlan(ShapePlan plan, ShapeFont font, LayoutBuffer buffer)
    {
        bool adjustOffsetsWhenZeroing = plan.AdjustMarkPositioningWhenZeroing && LayoutBuffer.IsForward(buffer.Direction);
        for (int i = 0; i < buffer.Len; i++)
        {
            buffer.Pos[i].AttachChain = 0;
            buffer.Pos[i].AttachType = 0;
        }

        if (plan.ZeroMarks && plan.Shaper.ZeroWidthMarks == ZeroWidthMarks.ByGdefEarly)
        {
            ZeroMarkWidthsByGdef(buffer, adjustOffsetsWhenZeroing);
        }

        if (plan.ApplyGpos)
        {
            ApplyLookups(plan, font, buffer, 1);
        }

        if (plan.ApplyKern)
        {
            font.Layout.Kern.Apply(buffer, font.Layout.Gdef, plan.KernMask, font);
        }

        if (plan.ZeroMarks && plan.Shaper.ZeroWidthMarks == ZeroWidthMarks.ByGdefLate)
        {
            ZeroMarkWidthsByGdef(buffer, adjustOffsetsWhenZeroing);
        }

        ZeroWidthDefaultIgnorables(buffer);
        LayoutApplier.PropagateAttachmentOffsets(buffer);
        if (plan.FallbackMarkPositioning)
        {
            Fallback.MarkPosition(plan, font, buffer, adjustOffsetsWhenZeroing);
        }
    }

    private static void ZeroMarkWidthsByGdef(LayoutBuffer buffer, bool adjustOffsets)
    {
        for (int i = 0; i < buffer.Len; i++)
        {
            if (LayoutApplier.IsMark(buffer.Info[i]))
            {
                ref GlyphPos pos = ref buffer.Pos[i];
                if (adjustOffsets)
                {
                    pos.XOffset = LayoutApplier.SaturateSub(pos.XOffset, pos.XAdvance);
                    pos.YOffset = LayoutApplier.SaturateSub(pos.YOffset, pos.YAdvance);
                }

                pos.XAdvance = 0;
                pos.YAdvance = 0;
            }
        }
    }

    private static void ZeroWidthDefaultIgnorables(LayoutBuffer buffer)
    {
        if ((buffer.ScratchFlags & LayoutBuffer.ScratchHasDefaultIgnorables) == 0 || buffer.PreserveDefaultIgnorables || buffer.RemoveDefaultIgnorables)
        {
            return;
        }

        for (int i = 0; i < buffer.Len; i++)
        {
            if (UnicodeProps.IsDefaultIgnorable(buffer.Info[i]))
            {
                ref GlyphPos pos = ref buffer.Pos[i];
                pos.XAdvance = 0;
                pos.YAdvance = 0;
                if (LayoutBuffer.IsHorizontal(buffer.Direction))
                {
                    pos.XOffset = 0;
                }
                else
                {
                    pos.YOffset = 0;
                }
            }
        }
    }

    private static void PropagateFlags(LayoutBuffer buffer)
    {
        uint andMask = LayoutBuffer.FlagDefined;
        if (!buffer.ProduceUnsafeToConcat)
        {
            andMask &= ~LayoutBuffer.FlagUnsafeToConcat;
        }

        GlyphInfo[] info = buffer.Info;
        int count = buffer.Len;
        int start = 0;
        for (int end = 1; end <= count; end++)
        {
            if (end < count && info[end].Cluster == info[start].Cluster)
            {
                continue;
            }

            uint mask = 0;
            for (int i = start; i < end; i++)
            {
                mask |= info[i].Mask;
            }

            if (buffer.ProduceSafeToInsertTatweel)
            {
                if ((mask & LayoutBuffer.FlagUnsafeToBreak) != 0)
                {
                    mask &= ~LayoutBuffer.FlagSafeToInsertTatweel;
                }

                if ((mask & LayoutBuffer.FlagSafeToInsertTatweel) != 0)
                {
                    mask |= LayoutBuffer.FlagUnsafeToBreak | LayoutBuffer.FlagUnsafeToConcat;
                }
            }

            mask &= andMask;
            for (int i = start; i < end; i++)
            {
                info[i].Mask = mask;
            }

            start = end;
        }
    }
}
