// Translated from HarfBuzz's OT/Layout/GSUB headers (MIT); see THIRD-PARTY-NOTICES.md.
using static Egami.Fonts.Shaping.OtData;

namespace Egami.Fonts.Shaping;

internal sealed partial class LayoutApplier
{
    private readonly uint[] _sequence = new uint[MaxContextLength * 4];

    private bool ApplyGsubSubtable(int type, int t)
    {
        switch (type)
        {
            case 1:
                return ApplySingleSubst(t);
            case 2:
                return ApplyMultipleSubst(t);
            case 3:
                return ApplyAlternateSubst(t);
            case 4:
                return ApplyLigatureSubst(t);
            case 5:
                return ApplyContext(t);
            case 6:
                return ApplyChainContext(t);
            case 8:
                return ApplyReverseChainSingleSubst(t);
            default:
                return false;
        }
    }

    private bool ApplySingleSubst(int t)
    {
        ReadOnlySpan<byte> d = D;
        uint glyph = Buffer.Cur().Codepoint;
        int format = U16(d, t);
        int index = Coverage(d, Off16(d, t, t + 2), glyph);
        if (index < 0)
        {
            return false;
        }

        if (format == 1)
        {
            uint delta = U16(d, t + 4);
            ReplaceGlyph((glyph + delta) & 0xFFFF);
            return true;
        }

        if (format == 2)
        {
            if (index >= U16(d, t + 4))
            {
                return false;
            }

            ReplaceGlyph(U16(d, t + 6 + (index * 2)));
            return true;
        }

        return false;
    }

    private bool ApplyMultipleSubst(int t)
    {
        ReadOnlySpan<byte> d = D;
        if (U16(d, t) != 1)
        {
            return false;
        }

        int index = Coverage(d, Off16(d, t, t + 2), Buffer.Cur().Codepoint);
        if (index < 0)
        {
            return false;
        }

        int sequence = index < U16(d, t + 4) ? Off16(d, t, t + 6 + (index * 2)) : 0;
        int count = sequence == 0 ? 0 : U16(d, sequence);
        if (count == 1)
        {
            ReplaceGlyph(U16(d, sequence + 2));
            return true;
        }

        if (count == 0)
        {
            Buffer.DeleteGlyph();
            return true;
        }

        int klass = IsLigature(Buffer.Cur()) ? GlyphProps.BaseGlyph : 0;
        int ligId = LigId(Buffer.Cur());
        for (int i = 0; i < count; i++)
        {
            if (ligId == 0)
            {
                SetLigPropsForComponent(ref Buffer.Cur(), i);
            }

            OutputGlyphForComponent(U16(d, sequence + 2 + (i * 2)), klass);
        }

        Buffer.SkipGlyph();
        return true;
    }

    private bool ApplyAlternateSubst(int t)
    {
        ReadOnlySpan<byte> d = D;
        if (U16(d, t) != 1)
        {
            return false;
        }

        int index = Coverage(d, Off16(d, t, t + 2), Buffer.Cur().Codepoint);
        if (index < 0)
        {
            return false;
        }

        int set = index < U16(d, t + 4) ? Off16(d, t, t + 6 + (index * 2)) : 0;
        int count = set == 0 ? 0 : U16(d, set);
        if (count == 0)
        {
            return false;
        }

        uint glyphMask = Buffer.Cur().Mask;
        int shift = System.Numerics.BitOperations.TrailingZeroCount(LookupMask);
        uint altIndex = (LookupMask & glyphMask) >> shift;
        if (altIndex == ShapeMap.MaxValue && Random)
        {
            Buffer.UnsafeToBreak(0, Buffer.Len);
            altIndex = (RandomNumber() % (uint)count) + 1;
        }

        if (altIndex > count || altIndex == 0)
        {
            return false;
        }

        ReplaceGlyph(U16(d, set + 2 + ((int)(altIndex - 1) * 2)));
        return true;
    }

    private bool ApplyLigatureSubst(int t)
    {
        ReadOnlySpan<byte> d = D;
        if (U16(d, t) != 1)
        {
            return false;
        }

        int index = Coverage(d, Off16(d, t, t + 2), Buffer.Cur().Codepoint);
        if (index < 0)
        {
            return false;
        }

        int set = index < U16(d, t + 4) ? Off16(d, t, t + 6 + (index * 2)) : 0;
        int count = set == 0 ? 0 : U16(d, set);
        for (int i = 0; i < count; i++)
        {
            int ligature = Off16(d, set, set + 2 + (i * 2));
            if (ligature != 0 && ApplyLigature(ligature))
            {
                return true;
            }
        }

        return false;
    }

    private bool ApplyLigature(int ligature)
    {
        ReadOnlySpan<byte> d = D;
        uint ligGlyph = U16(d, ligature);
        int count = U16(d, ligature + 2);
        if (count == 0)
        {
            return false;
        }

        if (count == 1)
        {
            ReplaceGlyph(ligGlyph);
            return true;
        }

        if (count > MaxContextLength)
        {
            return false;
        }

        if (!MatchInput(count, ligature + 4, MatchKind.Glyph, 0, out int matchEnd, out int totalComponentCount))
        {
            Buffer.UnsafeToConcat(Buffer.Idx, matchEnd);
            return false;
        }

        LigateInput(count, matchEnd, ligGlyph, totalComponentCount);
        return true;
    }

    private bool ApplyReverseChainSingleSubst(int t)
    {
        ReadOnlySpan<byte> d = D;
        if (U16(d, t) != 1)
        {
            return false;
        }

        int index = Coverage(d, Off16(d, t, t + 2), Buffer.Cur().Codepoint);
        if (index < 0)
        {
            return false;
        }

        if (NestingLevelLeft != MaxNestingLevel)
        {
            return false;
        }

        int backtrackCount = U16(d, t + 4);
        int backtrack = t + 6;
        int lookaheadCountAt = backtrack + (backtrackCount * 2);
        int lookaheadCount = U16(d, lookaheadCountAt);
        int lookahead = lookaheadCountAt + 2;
        int substituteCountAt = lookahead + (lookaheadCount * 2);
        int substituteCount = U16(d, substituteCountAt);
        if (index >= substituteCount)
        {
            return false;
        }

        if (MatchBacktrack(backtrackCount, backtrack, MatchKind.Coverage, t, out int startIndex) &&
            MatchLookahead(lookaheadCount, lookahead, MatchKind.Coverage, t, Buffer.Idx + 1, out int endIndex))
        {
            Buffer.UnsafeToBreakFromOutbuffer(startIndex, endIndex);
            ReplaceGlyphInplace(U16(d, substituteCountAt + 2 + (index * 2)));
            return true;
        }

        Buffer.UnsafeToConcatFromOutbuffer(startIndex, 0);
        return false;
    }
}
