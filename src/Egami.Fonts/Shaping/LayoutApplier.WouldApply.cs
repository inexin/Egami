// Translated from HarfBuzz's hb_ot_layout_lookup_would_substitute and the would_apply methods of hb-ot-layout-gsubgpos.hh and
// its GSUB lookup headers (MIT); see THIRD-PARTY-NOTICES.md.
using static Egami.Fonts.Shaping.OtData;

namespace Egami.Fonts.Shaping;

internal sealed partial class LayoutApplier
{
    /// <summary>
    /// Whether a GSUB lookup would substitute the glyph sequence on its own, as HarfBuzz's hb_ot_layout_lookup_would_substitute:
    /// the sequence must be a lookup's whole input; with <paramref name="zeroContext"/> a chaining rule must have no backtrack
    /// or lookahead. Glyph properties and lookup flags play no part.
    /// </summary>
    public static bool WouldSubstitute(LayoutTable gsub, int lookupIndex, ReadOnlySpan<uint> glyphs, bool zeroContext)
    {
        LayoutLookup? lookup = gsub.GetLookup(lookupIndex);
        if (lookup is null || glyphs.Length == 0)
        {
            return false;
        }

        // The lookup's coverage stands in for HarfBuzz's accelerator digest.
        if (lookup.Coverage is { } coverage && !coverage.Contains((int)glyphs[0]))
        {
            return false;
        }

        ReadOnlySpan<byte> d = gsub.Data.Span;
        for (int i = 0; i < lookup.Subtables.Length; i++)
        {
            if (WouldApplyGsubSubtable(d, lookup.SubtableTypes[i], lookup.Subtables[i], glyphs, zeroContext))
            {
                return true;
            }
        }

        return false;
    }

    private static bool WouldApplyGsubSubtable(ReadOnlySpan<byte> d, int type, int t, ReadOnlySpan<uint> glyphs, bool zeroContext)
    {
        switch (type)
        {
            case 1:
            case 2:
            case 3:
                // Single substitution has formats 1 and 2, multiple and alternate format 1; each applies to one covered glyph.
                return (type == 1 ? U16(d, t) is 1 or 2 : U16(d, t) == 1) &&
                    glyphs.Length == 1 && Coverage(d, Off16(d, t, t + 2), glyphs[0]) >= 0;
            case 4:
                return WouldApplyLigatureSubst(d, t, glyphs);
            case 5:
                return WouldApplyContext(d, t, glyphs);
            case 6:
                return WouldApplyChainContext(d, t, glyphs, zeroContext);
            case 8:
                return U16(d, t) == 1 && glyphs.Length == 1 && Coverage(d, Off16(d, t, t + 2), glyphs[0]) >= 0;
            default:
                return false;
        }
    }

    private static bool WouldApplyLigatureSubst(ReadOnlySpan<byte> d, int t, ReadOnlySpan<uint> glyphs)
    {
        if (U16(d, t) != 1)
        {
            return false;
        }

        int index = Coverage(d, Off16(d, t, t + 2), glyphs[0]);
        if (index < 0)
        {
            return false;
        }

        int set = index < U16(d, t + 4) ? Off16(d, t, t + 6 + (index * 2)) : 0;
        int count = set == 0 ? 0 : U16(d, set);
        for (int i = 0; i < count; i++)
        {
            int ligature = Off16(d, set, set + 2 + (i * 2));
            if (ligature == 0 || U16(d, ligature + 2) != glyphs.Length)
            {
                continue;
            }

            bool match = true;
            for (int j = 1; j < glyphs.Length && match; j++)
            {
                match = U16(d, ligature + 4 + ((j - 1) * 2)) == glyphs[j];
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the glyphs after the first match a rule's input, as HarfBuzz's would_match_input.</summary>
    private static bool WouldMatchInput(ReadOnlySpan<byte> d, int count, int values, MatchKind kind, int data, ReadOnlySpan<uint> glyphs)
    {
        if (count != glyphs.Length)
        {
            return false;
        }

        for (int i = 1; i < count; i++)
        {
            int value = U16(d, values + ((i - 1) * 2));
            bool match = kind switch
            {
                MatchKind.Glyph => glyphs[i] == (uint)value,
                MatchKind.Class => ClassOf(d, data, glyphs[i]) == value,
                MatchKind.Coverage => Coverage(d, value == 0 ? 0 : data + value, glyphs[i]) >= 0,
                _ => false,
            };
            if (!match)
            {
                return false;
            }
        }

        return true;
    }

    private static bool WouldApplyContext(ReadOnlySpan<byte> d, int t, ReadOnlySpan<uint> glyphs)
    {
        switch (U16(d, t))
        {
            case 1:
                {
                    int index = Coverage(d, Off16(d, t, t + 2), glyphs[0]);
                    int set = index >= 0 && index < U16(d, t + 4) ? Off16(d, t, t + 6 + (index * 2)) : 0;
                    return WouldApplyRuleSet(d, set, MatchKind.Glyph, 0, glyphs);
                }

            case 2:
                {
                    // Format 2 picks the rule set by class alone, without the coverage check of apply.
                    int classDef = Off16(d, t, t + 4);
                    int index = ClassOf(d, classDef, glyphs[0]);
                    int set = index < U16(d, t + 6) ? Off16(d, t, t + 8 + (index * 2)) : 0;
                    return WouldApplyRuleSet(d, set, MatchKind.Class, classDef, glyphs);
                }

            case 3:
                {
                    // Format 3 matches the glyphs after the first only.
                    int glyphCount = U16(d, t + 2);
                    return WouldMatchInput(d, glyphCount, t + 8, MatchKind.Coverage, t, glyphs);
                }

            default:
                return false;
        }
    }

    private static bool WouldApplyRuleSet(ReadOnlySpan<byte> d, int ruleSet, MatchKind kind, int data, ReadOnlySpan<uint> glyphs)
    {
        if (ruleSet == 0)
        {
            return false;
        }

        int count = U16(d, ruleSet);
        for (int i = 0; i < count; i++)
        {
            int rule = Off16(d, ruleSet, ruleSet + 2 + (i * 2));
            if (rule != 0 && WouldMatchInput(d, U16(d, rule), rule + 4, kind, data, glyphs))
            {
                return true;
            }
        }

        return false;
    }

    private static bool WouldApplyChainContext(ReadOnlySpan<byte> d, int t, ReadOnlySpan<uint> glyphs, bool zeroContext)
    {
        switch (U16(d, t))
        {
            case 1:
                {
                    int index = Coverage(d, Off16(d, t, t + 2), glyphs[0]);
                    int set = index >= 0 && index < U16(d, t + 4) ? Off16(d, t, t + 6 + (index * 2)) : 0;
                    return WouldApplyChainRuleSet(d, set, MatchKind.Glyph, 0, glyphs, zeroContext);
                }

            case 2:
                {
                    int inputClassDef = Off16(d, t, t + 6);
                    int index = ClassOf(d, inputClassDef, glyphs[0]);
                    int set = index < U16(d, t + 10) ? Off16(d, t, t + 12 + (index * 2)) : 0;
                    return WouldApplyChainRuleSet(d, set, MatchKind.Class, inputClassDef, glyphs, zeroContext);
                }

            case 3:
                {
                    int backtrackCount = U16(d, t + 2);
                    int inputCountAt = t + 4 + (backtrackCount * 2);
                    int inputCount = U16(d, inputCountAt);
                    int lookaheadCount = U16(d, inputCountAt + 2 + (inputCount * 2));
                    return (!zeroContext || (backtrackCount == 0 && lookaheadCount == 0)) &&
                        WouldMatchInput(d, inputCount, inputCountAt + 4, MatchKind.Coverage, t, glyphs);
                }

            default:
                return false;
        }
    }

    private static bool WouldApplyChainRuleSet(ReadOnlySpan<byte> d, int ruleSet, MatchKind kind, int inputData, ReadOnlySpan<uint> glyphs, bool zeroContext)
    {
        if (ruleSet == 0)
        {
            return false;
        }

        int count = U16(d, ruleSet);
        for (int i = 0; i < count; i++)
        {
            int rule = Off16(d, ruleSet, ruleSet + 2 + (i * 2));
            if (rule == 0)
            {
                continue;
            }

            int backtrackCount = U16(d, rule);
            int inputCountAt = rule + 2 + (backtrackCount * 2);
            int inputCount = U16(d, inputCountAt);
            int lookaheadCount = U16(d, inputCountAt + 2 + (Math.Max(inputCount - 1, 0) * 2));
            if ((!zeroContext || (backtrackCount == 0 && lookaheadCount == 0)) &&
                WouldMatchInput(d, inputCount, inputCountAt + 2, kind, inputData, glyphs))
            {
                return true;
            }
        }

        return false;
    }
}
