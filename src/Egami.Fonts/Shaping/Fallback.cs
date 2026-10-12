// Translated from HarfBuzz's hb-ot-shape-fallback.cc (MIT); see THIRD-PARTY-NOTICES.md.
using Egami.Fonts.Unicode;

namespace Egami.Fonts.Shaping;

/// <summary>Positioning for fonts that lack GPOS marks or spaces: marks placed by combining class, spaces sized from the em.</summary>
internal static class Fallback
{
    private const int AttachedBelowLeft = 200;
    private const int AttachedBelow = 202;
    private const int AttachedAbove = 214;
    private const int AttachedAboveRight = 216;
    private const int BelowLeft = 218;
    private const int Below = 220;
    private const int BelowRight = 222;
    private const int AboveLeft = 228;
    private const int Above = 230;
    private const int AboveRight = 232;
    private const int DoubleBelow = 233;
    private const int DoubleAbove = 234;

    // HarfBuzz's modified combining classes for the Hebrew, Arabic, Syriac, Thai, Lao and Tibetan classes.
    private const int Ccc10 = 22, Ccc11 = 15, Ccc12 = 16, Ccc13 = 17, Ccc14 = 23, Ccc15 = 18, Ccc16 = 19, Ccc17 = 20, Ccc18 = 21;
    private const int Ccc19 = 14, Ccc20 = 24, Ccc21 = 12, Ccc22 = 25, Ccc23 = 13, Ccc24 = 10, Ccc25 = 11, Ccc26 = 26;
    private const int Ccc27 = 28, Ccc28 = 29, Ccc29 = 30, Ccc30 = 31, Ccc31 = 32, Ccc32 = 33, Ccc33 = 27, Ccc34 = 34, Ccc35 = 35;
    private const int Ccc36 = 36, Ccc103 = 3, Ccc107 = 107, Ccc118 = 118, Ccc122 = 122, Ccc129 = 129, Ccc130 = 132, Ccc132 = 131;

    private static int RecategorizeCombiningClass(int u, int klass)
    {
        if (klass >= 200)
        {
            return klass;
        }

        // Thai and Lao need some per-character work.
        if ((u & ~0xFF) == 0x0E00)
        {
            if (klass == 0)
            {
                switch (u)
                {
                    case 0x0E31:
                    case 0x0E34:
                    case 0x0E35:
                    case 0x0E36:
                    case 0x0E37:
                    case 0x0E47:
                    case 0x0E4C:
                    case 0x0E4D:
                    case 0x0E4E:
                        klass = AboveRight;
                        break;
                    case 0x0EB1:
                    case 0x0EB4:
                    case 0x0EB5:
                    case 0x0EB6:
                    case 0x0EB7:
                    case 0x0EBB:
                    case 0x0ECC:
                    case 0x0ECD:
                        klass = Above;
                        break;
                    case 0x0EBC:
                        klass = Below;
                        break;
                }
            }
            else if (u == 0x0E3A)
            {
                // Thai virama is below-right.
                klass = BelowRight;
            }
        }

        switch (klass)
        {
            // Hebrew.
            case Ccc10:
            case Ccc11:
            case Ccc12:
            case Ccc13:
            case Ccc14:
            case Ccc15:
            case Ccc16:
            case Ccc17:
            case Ccc18:
            case Ccc20:
            case Ccc22:
                return Below;
            case Ccc23:
                return AttachedAbove;
            case Ccc24:
                return AboveRight;
            case Ccc25:
            case Ccc19:
                return AboveLeft;
            case Ccc26:
                return Above;
            case Ccc21:
                break;

            // Arabic and Syriac.
            case Ccc27:
            case Ccc28:
            case Ccc30:
            case Ccc31:
            case Ccc33:
            case Ccc34:
            case Ccc35:
            case Ccc36:
                return Above;
            case Ccc29:
            case Ccc32:
                return Below;

            // Thai.
            case Ccc103:
                return BelowRight;
            case Ccc107:
                return AboveRight;

            // Lao.
            case Ccc118:
                return Below;
            case Ccc122:
                return Above;

            // Tibetan.
            case Ccc129:
                return Below;
            case Ccc130:
                return Above;
            case Ccc132:
                return Below;
        }

        return klass;
    }

    public static void RecategorizeMarks(LayoutBuffer buffer)
    {
        for (int i = 0; i < buffer.Len; i++)
        {
            ref GlyphInfo info = ref buffer.Info[i];
            if (UnicodeProps.GeneralCategory(info) == UnicodeProps.NonspacingMark)
            {
                int combiningClass = UnicodeProps.GetModifiedCombiningClass(info);
                combiningClass = RecategorizeCombiningClass((int)info.Codepoint, combiningClass);
                UnicodeProps.SetModifiedCombiningClass(ref info, combiningClass);
            }
        }
    }

    private static void ZeroMarkAdvances(LayoutBuffer buffer, int start, int end, bool adjustOffsetsWhenZeroing)
    {
        for (int i = start; i < end; i++)
        {
            if (UnicodeProps.GeneralCategory(buffer.Info[i]) == UnicodeProps.NonspacingMark)
            {
                ref GlyphPos pos = ref buffer.Pos[i];
                if (adjustOffsetsWhenZeroing)
                {
                    pos.XOffset = LayoutApplier.SaturateSub(pos.XOffset, pos.XAdvance);
                    pos.YOffset = LayoutApplier.SaturateSub(pos.YOffset, pos.YAdvance);
                }

                pos.XAdvance = 0;
                pos.YAdvance = 0;
            }
        }
    }

    private static int Clamp(long v) => (int)Math.Clamp(v, int.MinValue, int.MaxValue);

    private static void PositionMark(ShapeFont font, LayoutBuffer buffer, ref ShapeExtents baseExtents, int i, int combiningClass)
    {
        if (!font.TryGetExtents(buffer.Info[i].Codepoint, out ShapeExtents markExtents))
        {
            return;
        }

        int yGap = font.UnitsPerEm / 16;
        ref GlyphPos pos = ref buffer.Pos[i];
        pos.XOffset = 0;
        pos.YOffset = 0;

        // X positioning; left and right marks are not positioned.
        switch (combiningClass)
        {
            case DoubleBelow or DoubleAbove when buffer.Direction == LayoutDirection.LeftToRight:
                pos.XOffset = Clamp((long)baseExtents.XBearing + baseExtents.Width - (markExtents.Width / 2) - markExtents.XBearing);
                break;
            case DoubleBelow or DoubleAbove when buffer.Direction == LayoutDirection.RightToLeft:
                pos.XOffset = Clamp((long)baseExtents.XBearing - (markExtents.Width / 2) - markExtents.XBearing);
                break;
            case AttachedBelowLeft:
            case BelowLeft:
            case AboveLeft:
                pos.XOffset = LayoutApplier.SaturateSub(baseExtents.XBearing, markExtents.XBearing);
                break;
            case AttachedAboveRight:
            case BelowRight:
            case AboveRight:
                pos.XOffset = Clamp((long)baseExtents.XBearing + baseExtents.Width - markExtents.Width - markExtents.XBearing);
                break;
            default:
                // Center align.
                pos.XOffset = Clamp(baseExtents.XBearing + (((long)baseExtents.Width - markExtents.Width) / 2) - markExtents.XBearing);
                break;
        }

        // Y positioning.
        switch (combiningClass)
        {
            case DoubleBelow:
            case BelowLeft:
            case Below:
            case BelowRight:
            case AttachedBelowLeft:
            case AttachedBelow:
                if (combiningClass is not (AttachedBelowLeft or AttachedBelow))
                {
                    baseExtents.Height = LayoutApplier.SaturateSub(baseExtents.Height, yGap);
                }

                pos.YOffset = Clamp((long)baseExtents.YBearing + baseExtents.Height - markExtents.YBearing);
                if ((yGap > 0) == (pos.YOffset > 0))
                {
                    baseExtents.Height = LayoutApplier.SaturateSub(baseExtents.Height, pos.YOffset);
                    pos.YOffset = 0;
                }

                baseExtents.Height = LayoutApplier.SaturateAdd(baseExtents.Height, markExtents.Height);
                break;

            case DoubleAbove:
            case AboveLeft:
            case Above:
            case AboveRight:
            case AttachedAbove:
            case AttachedAboveRight:
                if (combiningClass is not (AttachedAbove or AttachedAboveRight))
                {
                    baseExtents.YBearing = LayoutApplier.SaturateAdd(baseExtents.YBearing, yGap);
                    baseExtents.Height = LayoutApplier.SaturateSub(baseExtents.Height, yGap);
                }

                pos.YOffset = Clamp((long)baseExtents.YBearing - markExtents.YBearing - markExtents.Height);
                if ((yGap > 0) != (pos.YOffset > 0))
                {
                    int correction = -(pos.YOffset / 2);
                    baseExtents.YBearing = LayoutApplier.SaturateAdd(baseExtents.YBearing, correction);
                    baseExtents.Height = LayoutApplier.SaturateSub(baseExtents.Height, correction);
                    pos.YOffset = LayoutApplier.SaturateAdd(pos.YOffset, correction);
                }

                baseExtents.YBearing = LayoutApplier.SaturateSub(baseExtents.YBearing, markExtents.Height);
                baseExtents.Height = LayoutApplier.SaturateAdd(baseExtents.Height, markExtents.Height);
                break;
        }
    }

    private static void PositionAroundBase(ShapePlan plan, ShapeFont font, LayoutBuffer buffer, int baseIndex, int end, bool adjustOffsetsWhenZeroing)
    {
        LayoutDirection horizDir = LayoutDirection.Invalid;
        buffer.UnsafeToBreak(baseIndex, end);
        if (!font.TryGetExtents(buffer.Info[baseIndex].Codepoint, out ShapeExtents baseExtents))
        {
            ZeroMarkAdvances(buffer, baseIndex + 1, end, adjustOffsetsWhenZeroing);
            return;
        }

        baseExtents.YBearing = LayoutApplier.SaturateAdd(baseExtents.YBearing, buffer.Pos[baseIndex].YOffset);

        // The horizontal advance gives better horizontal positioning, and works for glyphs without ink.
        baseExtents.XBearing = 0;
        baseExtents.Width = font.GetHAdvance(buffer.Info[baseIndex].Codepoint);

        int ligId = LayoutApplier.LigId(buffer.Info[baseIndex]);
        int numLigComponents = LayoutApplier.LigNumComps(buffer.Info[baseIndex]);

        int xOffset = 0, yOffset = 0;
        if (LayoutBuffer.IsForward(buffer.Direction))
        {
            xOffset = LayoutApplier.SaturateNeg(buffer.Pos[baseIndex].XAdvance);
            yOffset = LayoutApplier.SaturateNeg(buffer.Pos[baseIndex].YAdvance);
        }

        ShapeExtents componentExtents = baseExtents;
        int lastLigComponent = -1;
        int lastCombiningClass = 255;
        ShapeExtents clusterExtents = baseExtents;
        GlyphInfo[] info = buffer.Info;
        for (int i = baseIndex + 1; i < end; i++)
        {
            if (UnicodeProps.GetModifiedCombiningClass(info[i]) != 0)
            {
                if (numLigComponents > 1)
                {
                    int thisLigId = LayoutApplier.LigId(info[i]);
                    int thisLigComponent = LayoutApplier.LigComp(info[i]) - 1;

                    // Conditions for attaching to the last component.
                    if (ligId == 0 || ligId != thisLigId || thisLigComponent >= numLigComponents)
                    {
                        thisLigComponent = numLigComponents - 1;
                    }

                    if (lastLigComponent != thisLigComponent)
                    {
                        lastLigComponent = thisLigComponent;
                        lastCombiningClass = 255;
                        componentExtents = baseExtents;
                        if (horizDir == LayoutDirection.Invalid)
                        {
                            horizDir = LayoutBuffer.IsHorizontal(plan.Direction) ? plan.Direction : OtShaper.HorizontalDirection(plan.Script);
                        }

                        if (horizDir == LayoutDirection.LeftToRight)
                        {
                            componentExtents.XBearing = Clamp(componentExtents.XBearing + ((long)thisLigComponent * componentExtents.Width / numLigComponents));
                        }
                        else
                        {
                            componentExtents.XBearing = Clamp(componentExtents.XBearing + ((long)(numLigComponents - 1 - thisLigComponent) * componentExtents.Width / numLigComponents));
                        }

                        componentExtents.Width /= numLigComponents;
                    }
                }

                int thisCombiningClass = UnicodeProps.GetModifiedCombiningClass(info[i]);
                if (lastCombiningClass != thisCombiningClass)
                {
                    lastCombiningClass = thisCombiningClass;
                    clusterExtents = componentExtents;
                }

                PositionMark(font, buffer, ref clusterExtents, i, thisCombiningClass);
                ref GlyphPos pos = ref buffer.Pos[i];
                pos.XAdvance = 0;
                pos.YAdvance = 0;
                pos.XOffset = LayoutApplier.SaturateAdd(pos.XOffset, xOffset);
                pos.YOffset = LayoutApplier.SaturateAdd(pos.YOffset, yOffset);
            }
            else if (LayoutBuffer.IsForward(buffer.Direction))
            {
                xOffset = LayoutApplier.SaturateSub(xOffset, buffer.Pos[i].XAdvance);
                yOffset = LayoutApplier.SaturateSub(yOffset, buffer.Pos[i].YAdvance);
            }
            else
            {
                xOffset = LayoutApplier.SaturateAdd(xOffset, buffer.Pos[i].XAdvance);
                yOffset = LayoutApplier.SaturateAdd(yOffset, buffer.Pos[i].YAdvance);
            }
        }
    }

    private static void PositionCluster(ShapePlan plan, ShapeFont font, LayoutBuffer buffer, int start, int end, bool adjustOffsetsWhenZeroing)
    {
        if (end - start < 2)
        {
            return;
        }

        GlyphInfo[] info = buffer.Info;
        for (int i = start; i < end; i++)
        {
            if (!UnicodeProps.IsUnicodeMark(info[i]))
            {
                int j;
                for (j = i + 1; j < end; j++)
                {
                    if (UnicodeProps.IsHidden(info[j]) || UnicodeProps.IsDefaultIgnorable(info[j]))
                    {
                        continue;
                    }

                    if (!UnicodeProps.IsUnicodeMark(info[j]))
                    {
                        break;
                    }
                }

                PositionAroundBase(plan, font, buffer, i, j, adjustOffsetsWhenZeroing);
                i = j - 1;
            }
        }
    }

    public static void MarkPosition(ShapePlan plan, ShapeFont font, LayoutBuffer buffer, bool adjustOffsetsWhenZeroing)
    {
        int start = 0;
        int count = buffer.Len;
        GlyphInfo[] info = buffer.Info;
        for (int i = 1; i < count; i++)
        {
            if (!UnicodeProps.IsUnicodeMark(info[i]) && !UnicodeProps.IsHidden(info[i]) && !UnicodeProps.IsDefaultIgnorable(info[i]))
            {
                PositionCluster(plan, font, buffer, start, i, adjustOffsetsWhenZeroing);
                start = i;
            }
        }

        PositionCluster(plan, font, buffer, start, count, adjustOffsetsWhenZeroing);
    }

    /// <summary>Sizes the spaces the font lacks, drawn with its space glyph, from the em.</summary>
    public static void Spaces(ShapeFont font, LayoutBuffer buffer)
    {
        GlyphInfo[] info = buffer.Info;
        GlyphPos[] pos = buffer.Pos;
        bool horizontal = LayoutBuffer.IsHorizontal(buffer.Direction);
        int scale = font.UnitsPerEm;
        for (int i = 0; i < buffer.Len; i++)
        {
            if (!UnicodeProps.IsUnicodeSpace(info[i]) || (info[i].GlyphProps & GlyphProps.Ligated) != 0)
            {
                continue;
            }

            // A font without an ASCII space draws the invisible glyph with a quarter em.
            if (buffer.Invisible != 0 && info[i].Codepoint == buffer.Invisible)
            {
                if (horizontal)
                {
                    pos[i].XAdvance = scale / 4;
                }
                else
                {
                    pos[i].YAdvance = -scale / 4;
                }
            }

            int spaceType = UnicodeProps.GetSpaceFallbackType(info[i]);
            switch (spaceType)
            {
                case UnicodeProps.SpaceEm:
                case UnicodeProps.SpaceEm2:
                case UnicodeProps.SpaceEm3:
                case UnicodeProps.SpaceEm4:
                case UnicodeProps.SpaceEm5:
                case UnicodeProps.SpaceEm6:
                case UnicodeProps.SpaceEm16:
                    if (horizontal)
                    {
                        pos[i].XAdvance = (int)(((long)scale + (spaceType / 2)) / spaceType);
                    }
                    else
                    {
                        pos[i].YAdvance = -(int)(((long)scale + (spaceType / 2)) / spaceType);
                    }

                    break;
                case UnicodeProps.Space4Em18:
                    if (horizontal)
                    {
                        pos[i].XAdvance = (int)((long)scale * 4 / 18);
                    }
                    else
                    {
                        pos[i].YAdvance = -(int)((long)scale * 4 / 18);
                    }

                    break;
                case UnicodeProps.SpaceFigure:
                    for (int u = '0'; u <= '9'; u++)
                    {
                        if (font.TryGetNominalGlyph(u, out uint glyph))
                        {
                            if (horizontal)
                            {
                                pos[i].XAdvance = font.GetHAdvance(glyph);
                            }

                            break;
                        }
                    }

                    break;
                case UnicodeProps.SpacePunctuation:
                    if (font.TryGetNominalGlyph('.', out uint period) || font.TryGetNominalGlyph(',', out period))
                    {
                        if (horizontal)
                        {
                            pos[i].XAdvance = font.GetHAdvance(period);
                        }
                    }

                    break;
                case UnicodeProps.SpaceNarrow:
                    if (horizontal)
                    {
                        pos[i].XAdvance /= 2;
                    }
                    else
                    {
                        pos[i].YAdvance /= 2;
                    }

                    break;
            }
        }
    }
}
