// Translated from HarfBuzz's hb-ot-shaper-hangul.cc (MIT); see THIRD-PARTY-NOTICES.md.
namespace Egami.Fonts.Shaping;

/// <summary>
/// The Hangul shaper: syllables composed when the font has the precomposed glyph and decomposed into jamo with 'ljmo', 'vjmo'
/// and 'tjmo' otherwise, tone marks moved before their syllable, and 'calt' kept away from jamo.
/// </summary>
internal sealed class HangulShaper : ComplexShaper
{
    public static readonly HangulShaper Instance = new();

    // Jamo features, in the order of s_features; the shaper-position byte holds one per glyph, None for none.
    private const byte None = 0;
    private const byte Ljmo = 1;
    private const byte Vjmo = 2;
    private const byte Tjmo = 3;
    private const int FeatureCount = Tjmo + 1;

    private static readonly uint[] s_features =
        [0, ShapeMapBuilder.Tag("ljmo"), ShapeMapBuilder.Tag("vjmo"), ShapeMapBuilder.Tag("tjmo")];

    private static readonly uint s_calt = ShapeMapBuilder.Tag("calt");

    // Algorithmic Hangul syllable composition.
    private const uint LBase = 0x1100;
    private const uint VBase = 0x1161;
    private const uint TBase = 0x11A7;
    private const uint LCount = 19;
    private const uint VCount = 21;
    private const uint TCount = 28;
    private const uint SBase = 0xAC00;
    private const uint NCount = VCount * TCount;
    private const uint SCount = LCount * NCount;

    private sealed class PlanData
    {
        public readonly uint[] MaskArray = new uint[FeatureCount];
        public uint CaltMask;
    }

    private static bool IsCombiningL(uint u) => u is >= LBase and <= LBase + LCount - 1;

    private static bool IsCombiningV(uint u) => u is >= VBase and <= VBase + VCount - 1;

    private static bool IsCombiningT(uint u) => u is >= TBase + 1 and <= TBase + TCount - 1;

    private static bool IsCombinedS(uint u) => u is >= SBase and <= SBase + SCount - 1;

    private static bool IsL(uint u) => u is (>= 0x1100 and <= 0x115F) or (>= 0xA960 and <= 0xA97C);

    private static bool IsV(uint u) => u is (>= 0x1160 and <= 0x11A7) or (>= 0xD7B0 and <= 0xD7C6);

    private static bool IsT(uint u) => u is (>= 0x11A8 and <= 0x11FF) or (>= 0xD7CB and <= 0xD7FB);

    private static bool IsHangulTone(uint u) => u is >= 0x302E and <= 0x302F;

    public override NormalizationMode NormalizationPreference => NormalizationMode.None;

    public override ZeroWidthMarks ZeroWidthMarks => ZeroWidthMarks.None;

    public override void CollectFeatures(ShapePlanner planner)
    {
        for (int i = Ljmo; i < FeatureCount; i++)
        {
            planner.Map.AddFeature(s_features[i]);
        }
    }

    public override void OverrideFeatures(ShapePlanner planner)
    {
        // Uniscribe does not apply 'calt' to Hangul, and some fonts (Noto Sans CJK, Source Han Sans) run all their jamo lookups
        // in 'calt'. Rather than turning 'calt' off for the whole run, it gets a mask that setup clears on jamo only.
        planner.Map.AddFeature(s_calt);
    }

    public override object? CreateData(ShapePlan plan)
    {
        PlanData data = new() { CaltMask = plan.Map.Get1Mask(s_calt) };
        for (int i = 0; i < FeatureCount; i++)
        {
            data.MaskArray[i] = plan.Map.Get1Mask(s_features[i]);
        }

        return data;
    }

    private static bool IsZeroWidthChar(ShapeFont font, uint u) =>
        font.TryGetNominalGlyph((int)u, out uint glyph) && font.GetHAdvance(glyph) == 0;

    public override void PreprocessText(ShapePlan plan, LayoutBuffer buffer, ShapeFont font)
    {
        // Syllables are LV or LVT: <LV> precomposed or <L,V> decomposed, and <LVT>, <LV,T> or <L,V,T>. A syllable is composed
        // when the whole of it can be, and fully decomposed with the jamo features otherwise. A tone mark after a syllable moves
        // in front of it, unless its glyph has zero width, made to overstrike.
        buffer.ClearOutput();

        // The extent of the latest syllable, valid only when start < end.
        int start = 0, end = 0;
        int count = buffer.Len;
        Span<uint> chars = stackalloc uint[3];
        while (buffer.Idx < count && buffer.Successful)
        {
            uint u = buffer.Cur().Codepoint;

            if (IsHangulTone(u))
            {
                if (start < end && end == buffer.OutLen)
                {
                    // A tone mark after a valid syllable: move it in front, unless it has zero width.
                    buffer.UnsafeToBreakFromOutbuffer(start, buffer.Idx);
                    buffer.NextGlyph();
                    if (!IsZeroWidthChar(font, u))
                    {
                        buffer.MergeOutClusters(start, end + 1);
                        GlyphInfo[] info = buffer.OutInfo;
                        GlyphInfo tone = info[end];
                        Array.Copy(info, start, info, start + 1, end - start);
                        info[start] = tone;
                    }
                }
                else if (!buffer.DoNotInsertDottedCircle && font.HasGlyph(0x25CC))
                {
                    // No syllable to carry the tone mark: give it a dotted circle.
                    if (!IsZeroWidthChar(font, u))
                    {
                        chars[0] = u;
                        chars[1] = 0x25CC;
                    }
                    else
                    {
                        chars[0] = 0x25CC;
                        chars[1] = u;
                    }

                    buffer.ReplaceGlyphs(1, chars[..2]);
                }
                else
                {
                    // No dotted circle in the font: leave the tone mark alone.
                    buffer.NextGlyph();
                }

                start = end = buffer.OutLen;
                continue;
            }

            // A potential syllable start, used only if end moves past it.
            start = buffer.OutLen;

            if (IsL(u) && buffer.Idx + 1 < count)
            {
                uint l = u;
                uint v = buffer.Cur(1).Codepoint;
                if (IsV(v))
                {
                    // <L,V> or <L,V,T>.
                    uint t = 0;
                    uint tindex = 0;
                    if (buffer.Idx + 2 < count)
                    {
                        t = buffer.Cur(2).Codepoint;
                        if (IsT(t))
                        {
                            // Only meaningful when t is a combining T.
                            tindex = t - TBase;
                        }
                        else
                        {
                            t = 0;
                        }
                    }

                    buffer.UnsafeToBreak(buffer.Idx, buffer.Idx + (t != 0 ? 3 : 2));

                    // Compose when the font has the syllable.
                    if (IsCombiningL(l) && IsCombiningV(v) && (t == 0 || IsCombiningT(t)))
                    {
                        uint s = SBase + ((l - LBase) * NCount) + ((v - VBase) * TCount) + tindex;
                        if (font.HasGlyph((int)s))
                        {
                            chars[0] = s;
                            buffer.ReplaceGlyphs(t != 0 ? 3 : 2, chars[..1]);
                            end = start + 1;
                            continue;
                        }
                    }

                    // An Old Hangul syllable with no precomposed character, or one the font lacks: the jamo features.
                    buffer.Cur().ShaperPosition = Ljmo;
                    buffer.NextGlyph();
                    buffer.Cur().ShaperPosition = Vjmo;
                    buffer.NextGlyph();
                    if (t != 0)
                    {
                        buffer.Cur().ShaperPosition = Tjmo;
                        buffer.NextGlyph();
                        end = start + 3;
                    }
                    else
                    {
                        end = start + 2;
                    }

                    if (!buffer.Successful)
                    {
                        break;
                    }

                    buffer.MergeOutGraphemeClusters(start, end);
                    continue;
                }
            }
            else if (IsCombinedS(u))
            {
                // <LV>, <LVT> or <LV,T>.
                uint s = u;
                bool hasGlyph = font.HasGlyph((int)s);
                uint lindex = (s - SBase) / NCount;
                uint nindex = (s - SBase) % NCount;
                uint vindex = nindex / TCount;
                uint tindex = nindex % TCount;

                if (tindex == 0 && buffer.Idx + 1 < count && IsCombiningT(buffer.Cur(1).Codepoint))
                {
                    // <LV,T>: compose when the font has the syllable.
                    uint newTindex = buffer.Cur(1).Codepoint - TBase;
                    uint newS = s + newTindex;
                    if (font.HasGlyph((int)newS))
                    {
                        chars[0] = newS;
                        buffer.ReplaceGlyphs(2, chars[..1]);
                        end = start + 1;
                        continue;
                    }

                    buffer.UnsafeToBreak(buffer.Idx, buffer.Idx + 2);
                }

                // Decompose when the font lacks <LV> or <LVT>, or for <LV> with a non-combining T after it (a combining one was
                // handled above).
                if (!hasGlyph || (tindex == 0 && buffer.Idx + 1 < count && IsT(buffer.Cur(1).Codepoint)))
                {
                    chars[0] = LBase + lindex;
                    chars[1] = VBase + vindex;
                    chars[2] = TBase + tindex;
                    if (font.HasGlyph((int)chars[0]) && font.HasGlyph((int)chars[1]) && (tindex == 0 || font.HasGlyph((int)chars[2])))
                    {
                        int sLen = tindex != 0 ? 3 : 2;
                        buffer.ReplaceGlyphs(1, chars[..sLen]);

                        // An <LV> decomposed for the non-combining T after it takes the T into the syllable.
                        if (hasGlyph && tindex == 0)
                        {
                            buffer.NextGlyph();
                            sLen++;
                        }

                        if (!buffer.Successful)
                        {
                            break;
                        }

                        // The decomposed jamo, now in the output, get their features.
                        GlyphInfo[] info = buffer.OutInfo;
                        end = start + sLen;

                        int i = start;
                        info[i++].ShaperPosition = Ljmo;
                        info[i++].ShaperPosition = Vjmo;
                        if (i < end)
                        {
                            info[i++].ShaperPosition = Tjmo;
                        }

                        buffer.MergeOutGraphemeClusters(start, end);
                        continue;
                    }

                    if (tindex == 0 && buffer.Idx + 1 < count && IsT(buffer.Cur(1).Codepoint))
                    {
                        buffer.UnsafeToBreak(buffer.Idx, buffer.Idx + 2);
                    }
                }

                if (hasGlyph)
                {
                    // The syllable stays as it is.
                    end = start + 1;
                }
            }

            // No recognizable syllable leaves end <= start, so no tone mark moves before it.
            buffer.NextGlyph();
        }

        buffer.Sync();
    }

    public override void SetupMasks(ShapePlan plan, LayoutBuffer buffer, ShapeFont font)
    {
        if (plan.ShaperData is not PlanData data)
        {
            return;
        }

        GlyphInfo[] info = buffer.Info;
        for (int i = 0; i < buffer.Len; i++)
        {
            info[i].Mask |= data.MaskArray[info[i].ShaperPosition];

            // Keep 'calt' away from jamo.
            uint u = info[i].Codepoint;
            if (IsL(u) || IsV(u) || IsT(u))
            {
                info[i].Mask &= ~data.CaltMask;
            }
        }
    }
}
