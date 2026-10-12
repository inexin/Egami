// Translated from HarfBuzz's hb-ot-shaper-thai.cc (MIT); see THIRD-PARTY-NOTICES.md.
namespace Egami.Fonts.Shaping;

/// <summary>
/// The Thai and Lao shaper: sara am decomposed into nikhahit and sara aa with the nikhahit moved before the above-base marks,
/// and, for Thai fonts without Thai GSUB, the fallback mark positioning by the Windows and Mac private-use glyphs.
/// </summary>
internal sealed class ThaiShaper : ComplexShaper
{
    public static readonly ThaiShaper Instance = new();

    // Consonant types: normal, ascender, removable descender, strict descender; NotConsonant is the start state's column.
    private const int NC = 0, AC = 1, RC = 2, DC = 3, NotConsonant = 4;

    // Mark types: above vowel, below vowel, tone; NotMark is none.
    private const int AV = 0, BV = 1, T = 2, NotMark = 3;

    // Actions: none, shift down, shift left, shift down-left, remove the base's descender.
    private const int NOP = 0, SD = 1, SL = 2, SDL = 3, RD = 4;

    // Above states: the cluster's above-base space from empty (T0) to full (T3).
    private const byte T0 = 0, T1 = 1, T2 = 2, T3 = 3;

    // Below states: no descender, removable descender, strict descender.
    private const byte B0 = 0, B1 = 1, B2 = 2;

    // (character, Windows PUA, Mac PUA) for each action.
    private static readonly (int U, int WinPua, int MacPua)[] s_sdMappings =
    [
        (0x0E48, 0xF70A, 0xF88B), // Mai ek.
        (0x0E49, 0xF70B, 0xF88E), // Mai tho.
        (0x0E4A, 0xF70C, 0xF891), // Mai tri.
        (0x0E4B, 0xF70D, 0xF894), // Mai chattawa.
        (0x0E4C, 0xF70E, 0xF897), // Thanthakhat.
        (0x0E38, 0xF718, 0xF89B), // Sara u.
        (0x0E39, 0xF719, 0xF89C), // Sara uu.
        (0x0E3A, 0xF71A, 0xF89D), // Phinthu.
    ];

    private static readonly (int U, int WinPua, int MacPua)[] s_sdlMappings =
    [
        (0x0E48, 0xF705, 0xF88C), // Mai ek.
        (0x0E49, 0xF706, 0xF88F), // Mai tho.
        (0x0E4A, 0xF707, 0xF892), // Mai tri.
        (0x0E4B, 0xF708, 0xF895), // Mai chattawa.
        (0x0E4C, 0xF709, 0xF898), // Thanthakhat.
    ];

    private static readonly (int U, int WinPua, int MacPua)[] s_slMappings =
    [
        (0x0E48, 0xF713, 0xF88A), // Mai ek.
        (0x0E49, 0xF714, 0xF88D), // Mai tho.
        (0x0E4A, 0xF715, 0xF890), // Mai tri.
        (0x0E4B, 0xF716, 0xF893), // Mai chattawa.
        (0x0E4C, 0xF717, 0xF896), // Thanthakhat.
        (0x0E31, 0xF710, 0xF884), // Mai han-akat.
        (0x0E34, 0xF701, 0xF885), // Sara i.
        (0x0E35, 0xF702, 0xF886), // Sara ii.
        (0x0E36, 0xF703, 0xF887), // Sara ue.
        (0x0E37, 0xF704, 0xF888), // Sara uee.
        (0x0E47, 0xF712, 0xF889), // Maitaikhu.
        (0x0E4D, 0xF711, 0xF899), // Nikhahit.
    ];

    private static readonly (int U, int WinPua, int MacPua)[] s_rdMappings =
    [
        (0x0E0D, 0xF70F, 0xF89A), // Yo ying.
        (0x0E10, 0xF700, 0xF89E), // Tho than.
    ];

    // Start states by consonant type, NotConsonant last.
    private static readonly byte[] s_aboveStartState = [T0, T1, T0, T0, T3];
    private static readonly byte[] s_belowStartState = [B0, B0, B1, B2, B2];

    // (action, next state) by state and mark type (AV, BV, T).
    private static readonly (int Action, byte Next)[,] s_aboveStateMachine =
    {
        { (NOP, T3), (NOP, T0), (SD, T3) },
        { (SL, T2), (NOP, T1), (SDL, T2) },
        { (NOP, T3), (NOP, T2), (SL, T3) },
        { (NOP, T3), (NOP, T3), (NOP, T3) },
    };

    private static readonly (int Action, byte Next)[,] s_belowStateMachine =
    {
        { (NOP, B0), (NOP, B2), (NOP, B0) },
        { (NOP, B1), (RD, B2), (NOP, B1) },
        { (NOP, B2), (SD, B2), (NOP, B2) },
    };

    public override bool FallbackPosition => false;

    private static int GetConsonantType(uint u)
    {
        if (u is 0x0E1B or 0x0E1D or 0x0E1F)
        {
            return AC;
        }

        if (u is 0x0E0D or 0x0E10)
        {
            return RC;
        }

        if (u is 0x0E0E or 0x0E0F)
        {
            return DC;
        }

        return u is >= 0x0E01 and <= 0x0E2E ? NC : NotConsonant;
    }

    private static int GetMarkType(uint u)
    {
        if (u is 0x0E31 or (>= 0x0E34 and <= 0x0E37) or 0x0E47 or (>= 0x0E4D and <= 0x0E4E))
        {
            return AV;
        }

        if (u is >= 0x0E38 and <= 0x0E3A)
        {
            return BV;
        }

        return u is >= 0x0E48 and <= 0x0E4C ? T : NotMark;
    }

    /// <summary>The private-use form the font has for a character under an action, Windows' first; the character when none.</summary>
    private static uint PuaShape(uint u, int action, ShapeFont font)
    {
        (int U, int WinPua, int MacPua)[] mappings;
        switch (action)
        {
            case SD:
                mappings = s_sdMappings;
                break;
            case SDL:
                mappings = s_sdlMappings;
                break;
            case SL:
                mappings = s_slMappings;
                break;
            case RD:
                mappings = s_rdMappings;
                break;
            default:
                return u;
        }

        foreach ((int U, int WinPua, int MacPua) mapping in mappings)
        {
            if (mapping.U == u)
            {
                if (font.HasGlyph(mapping.WinPua))
                {
                    return (uint)mapping.WinPua;
                }

                if (font.HasGlyph(mapping.MacPua))
                {
                    return (uint)mapping.MacPua;
                }

                break;
            }
        }

        return u;
    }

    /// <summary>Fallback mark positioning by the shifted private-use forms, after https://linux.thai.net/~thep/th-otf/shaping.html.</summary>
    private static void PuaShaping(LayoutBuffer buffer, ShapeFont font)
    {
        byte aboveState = s_aboveStartState[NotConsonant];
        byte belowState = s_belowStartState[NotConsonant];
        int baseIndex = 0;

        GlyphInfo[] info = buffer.Info;
        int count = buffer.Len;
        for (int i = 0; i < count; i++)
        {
            int mt = GetMarkType(info[i].Codepoint);
            if (mt == NotMark)
            {
                int ct = GetConsonantType(info[i].Codepoint);
                aboveState = s_aboveStartState[ct];
                belowState = s_belowStartState[ct];
                baseIndex = i;
                continue;
            }

            (int Action, byte Next) aboveEdge = s_aboveStateMachine[aboveState, mt];
            (int Action, byte Next) belowEdge = s_belowStateMachine[belowState, mt];
            aboveState = aboveEdge.Next;
            belowState = belowEdge.Next;

            // At least one of the two actions is NOP.
            int action = aboveEdge.Action != NOP ? aboveEdge.Action : belowEdge.Action;

            buffer.UnsafeToBreak(baseIndex, i);
            if (action == RD)
            {
                info[baseIndex].Codepoint = PuaShape(info[baseIndex].Codepoint, action, font);
            }
            else
            {
                info[i].Codepoint = PuaShape(info[i].Codepoint, action, font);
            }
        }
    }

    // Thai and Lao alike, Lao being Thai + 0x80: sara am, and the above-base marks a nikhahit from it moves over.
    private static bool IsSaraAm(uint u) => (u & ~0x0080u) == 0x0E33;

    private static bool IsAboveBaseMark(uint u)
    {
        uint t = u & ~0x0080u;
        return t is (>= 0x0E34 and <= 0x0E37) or (>= 0x0E47 and <= 0x0E4E) or 0x0E31 or 0x0E3B;
    }

    public override void PreprocessText(ShapePlan plan, LayoutBuffer buffer, ShapeFont font)
    {
        // Not in the OpenType Thai spec, but what Uniscribe does: sara am decomposes into nikhahit and sara aa, and the nikhahit
        // moves back over any above-base marks; a nikhahit not from a sara am stays. U+0E3A goes after U+0E38 and U+0E39 by its
        // modified combining class instead.
        buffer.ClearOutput();
        int count = buffer.Len;
        while (buffer.Idx < count)
        {
            uint u = buffer.Cur().Codepoint;
            if (!IsSaraAm(u))
            {
                buffer.NextGlyph();
                continue;
            }

            // Sara am: decompose and reorder.
            buffer.OutputGlyph(u - 0x0E33 + 0x0E4D);
            UnicodeProps.SetContinuation(ref buffer.Prev(), buffer);
            buffer.ReplaceGlyph(u - 1);

            // The nikhahit counts as a ccc=0 mark when zeroing widths.
            int end = buffer.OutLen;
            UnicodeProps.SetGeneralCategory(ref buffer.OutInfo[end - 2], UnicodeProps.NonspacingMark);

            int start = end - 2;
            while (start > 0 && IsAboveBaseMark(buffer.OutInfo[start - 1].Codepoint))
            {
                start--;
            }

            if (start + 2 < end)
            {
                // Move the nikhahit to the front of the marks.
                buffer.MergeOutClusters(start, end);
                GlyphInfo[] outInfo = buffer.OutInfo;
                GlyphInfo nikhahit = outInfo[end - 2];
                Array.Copy(outInfo, start, outInfo, start + 1, end - start - 2);
                outInfo[start] = nikhahit;
            }

            // The nikhahit is combining, so it joins the previous cluster.
            if (start > 0)
            {
                buffer.MergeOutGraphemeClusters(start - 1, end);
            }
        }

        buffer.Sync();

        // A font with Thai GSUB positions the marks itself.
        if (plan.Script.Code == "Thai" && !plan.Map.FoundScript[0])
        {
            PuaShaping(buffer, font);
        }
    }
}
