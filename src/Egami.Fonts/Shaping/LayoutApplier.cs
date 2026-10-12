// Translated from HarfBuzz's hb-ot-layout-gsubgpos.hh and its GSUB and GPOS lookup headers (MIT); see THIRD-PARTY-NOTICES.md.
using static Egami.Fonts.Shaping.OtData;

namespace Egami.Fonts.Shaping;

/// <summary>How the matcher compares a glyph with the value a rule lists for it.</summary>
internal enum MatchKind : byte
{
    None,
    Always,
    Glyph,
    Class,
    Coverage,
}

/// <summary>
/// Applies GSUB or GPOS lookups to a <see cref="LayoutBuffer"/>, as HarfBuzz's apply context: the skipping iterators, input and
/// context matching, ligature component bookkeeping, nested lookups and every lookup type.
/// </summary>
internal sealed partial class LayoutApplier
{
    public const int MaxNestingLevel = 64;
    public const int MaxContextLength = 64;

    public readonly LayoutTable Table;
    public readonly GdefTable Gdef;
    public readonly LayoutBuffer Buffer;
    public readonly int TableIndex;
    public readonly bool HasGlyphClasses;
    public readonly SkippingIterator IterInput;
    public readonly SkippingIterator IterContext;

    public LayoutDirection Direction;
    public uint LookupMask = 1;
    public int LookupIndex = -1;
    public int LookupProps;
    public int NestingLevelLeft = MaxNestingLevel;
    public bool AutoZwnj = true;
    public bool AutoZwj = true;
    public bool PerSyllable;
    public bool Random;
    public int NewSyllables = -1;
    public int LastBase = -1;
    public int LastBaseUntil;
    public int[] MatchPositions = new int[MaxContextLength + 1];

    /// <summary>Normalized variation coordinates in 2.14 units, for device-table deltas; empty at the default instance.</summary>
    public int[] Coords = [];

    /// <summary>Resolves a variation-index device table to a delta, or null when the font is not varied.</summary>
    public Func<int, int, float>? VariationDelta;

    /// <summary>The 16.16 multipliers HarfBuzz's em_scale applies to GPOS values: 0x10000 when positions are in font units.</summary>
    public long XMult = 0x10000;
    public long YMult = 0x10000;

    /// <summary>The float multipliers em_fscale and em_scalef apply to anchors and device deltas.</summary>
    public float XMultF = 1;
    public float YMultF = 1;

    public LayoutApplier(LayoutTable table, GdefTable gdef, LayoutBuffer buffer)
    {
        Table = table;
        Gdef = gdef;
        Buffer = buffer;
        TableIndex = table.IsGpos ? 1 : 0;
        HasGlyphClasses = gdef.HasGlyphClasses;
        Direction = buffer.Direction;
        IterInput = new SkippingIterator(this);
        IterContext = new SkippingIterator(this);
        InitIters();
    }

    public ReadOnlySpan<byte> D => Table.Data.Span;

    public void InitIters()
    {
        IterInput.Init(false);
        IterContext.Init(true);
    }

    public void SetLookupMask(uint mask)
    {
        LookupMask = mask;
        LastBase = -1;
        LastBaseUntil = 0;
    }

    public void SetLookupProps(int props)
    {
        LookupProps = Gdef.SanitizeLookupProps(props);
        InitIters();
    }

    public uint RandomNumber()
    {
        // As HarfBuzz's minstd_rand step, in 32-bit unsigned arithmetic that wraps.
        Buffer.RandomState = unchecked(Buffer.RandomState * 48271u) % 2147483647u;
        return Buffer.RandomState;
    }

    // ---- Glyph properties ----

    public bool MatchPropertiesMark(in GlyphInfo info, int glyphProps, int matchProps)
    {
        if ((matchProps & LookupFlag.UseMarkFilteringSet) != 0)
        {
            return Gdef.MarkSetCovers(matchProps >>> 16, info.Codepoint);
        }

        if ((matchProps & LookupFlag.MarkAttachmentType) != 0)
        {
            return (matchProps & LookupFlag.MarkAttachmentType) == (glyphProps & LookupFlag.MarkAttachmentType);
        }

        return true;
    }

    public bool CheckGlyphProperty(in GlyphInfo info, int matchProps)
    {
        int glyphProps = info.GlyphProps;
        if ((glyphProps & matchProps & LookupFlag.IgnoreFlags) != 0)
        {
            return false;
        }

        if ((glyphProps & GlyphProps.Mark) != 0)
        {
            return MatchPropertiesMark(info, glyphProps, matchProps);
        }

        return true;
    }

    private void SetGlyphClass(uint glyph, int classGuess = 0, bool ligature = false, bool component = false)
    {
        ref GlyphInfo cur = ref Buffer.Cur();
        if (NewSyllables != -1)
        {
            cur.Syllable = (byte)NewSyllables;
        }

        int props = cur.GlyphProps;
        props |= GlyphProps.Substituted;
        if (ligature)
        {
            props |= GlyphProps.Ligated;
            props &= ~GlyphProps.Multiplied;
        }

        if (component)
        {
            props |= GlyphProps.Multiplied;
        }

        if (HasGlyphClasses)
        {
            props &= GlyphProps.Preserve;
            cur.GlyphProps = (ushort)(props | Gdef.GetGlyphProps(glyph));
        }
        else if (classGuess != 0)
        {
            props &= GlyphProps.Preserve;
            cur.GlyphProps = (ushort)(props | classGuess);
        }
        else
        {
            cur.GlyphProps = (ushort)props;
        }
    }

    public void ReplaceGlyph(uint glyph)
    {
        SetGlyphClass(glyph);
        Buffer.ReplaceGlyph(glyph);
    }

    public void ReplaceGlyphInplace(uint glyph)
    {
        SetGlyphClass(glyph);
        Buffer.Cur().Codepoint = glyph;
    }

    public void ReplaceGlyphWithLigature(uint glyph, int classGuess)
    {
        SetGlyphClass(glyph, classGuess, ligature: true);
        Buffer.ReplaceGlyph(glyph);
    }

    public void OutputGlyphForComponent(uint glyph, int classGuess)
    {
        SetGlyphClass(glyph, classGuess, component: true);
        Buffer.OutputGlyph(glyph);
    }

    // ---- Recursion ----

    public bool Recurse(int lookupIndex)
    {
        if (NestingLevelLeft == 0)
        {
            Buffer.Successful = false;
            return false;
        }

        Buffer.MaxOps--;
        if (Buffer.MaxOps < 0)
        {
            Buffer.Successful = false;
            return false;
        }

        LayoutLookup? lookup = Table.GetLookup(lookupIndex);
        if (lookup is null)
        {
            return false;
        }

        NestingLevelLeft--;
        int savedProps = LookupProps;
        int savedIndex = LookupIndex;
        int[] savedPositions = MatchPositions;
        MatchPositions = new int[MaxContextLength + 1];
        LookupIndex = lookupIndex;
        SetLookupProps(lookup.Props);
        bool ret = ApplyLookupSubtables(lookup);
        LookupIndex = savedIndex;
        SetLookupProps(savedProps);
        MatchPositions = savedPositions;
        NestingLevelLeft++;
        return ret;
    }

    /// <summary>Applies the first subtable of the lookup that applies at the current glyph.</summary>
    public bool ApplyLookupSubtables(LayoutLookup lookup)
    {
        uint glyph = Buffer.Cur().Codepoint;
        if (!lookup.Covers(glyph))
        {
            return false;
        }

        for (int i = 0; i < lookup.Subtables.Length; i++)
        {
            if (TableIndex == 0 ? ApplyGsubSubtable(lookup.SubtableTypes[i], lookup.Subtables[i]) : ApplyGposSubtable(lookup.SubtableTypes[i], lookup.Subtables[i]))
            {
                return true;
            }
        }

        return false;
    }

    // ---- Lookup application over the buffer ----

    /// <summary>Applies one lookup to the whole buffer, forward (or backward for reverse chaining), as HarfBuzz's apply_string.</summary>
    public bool ApplyString(LayoutLookup lookup)
    {
        LayoutBuffer buffer = Buffer;
        if (buffer.Len == 0 || LookupMask == 0)
        {
            return false;
        }

        // As HarfBuzz's digest check: a lookup no glyph in the buffer can start is skipped whole, without copying the buffer.
        if (!MayApply(lookup))
        {
            return false;
        }

        SetLookupProps(lookup.Props);
        bool reverse = TableIndex == 0 && lookup.Type == 8;
        if (!reverse)
        {
            if (TableIndex == 0)
            {
                buffer.ClearOutput();
            }

            buffer.Idx = 0;
            bool ret = ApplyForward(lookup);
            if (TableIndex == 0)
            {
                buffer.Sync();
            }

            return ret;
        }
        else
        {
            buffer.Idx = buffer.Len - 1;
            return ApplyBackward(lookup);
        }
    }

    private bool MayApply(LayoutLookup lookup)
    {
        LayoutBuffer buffer = Buffer;
        GlyphInfo[] info = buffer.Info;
        for (int i = 0; i < buffer.Len; i++)
        {
            if ((info[i].Mask & LookupMask) != 0 && lookup.Covers(info[i].Codepoint))
            {
                return true;
            }
        }

        return false;
    }

    private bool ApplyForward(LayoutLookup lookup)
    {
        LayoutBuffer buffer = Buffer;
        bool ret = false;
        while (buffer.Successful)
        {
            int j = buffer.Idx;
            while (j < buffer.Len)
            {
                ref GlyphInfo info = ref buffer.Info[j];
                if (lookup.Covers(info.Codepoint) && (info.Mask & LookupMask) != 0 && CheckGlyphProperty(info, LookupProps))
                {
                    break;
                }

                j++;
            }

            if (j > buffer.Idx)
            {
                buffer.NextGlyphs(j - buffer.Idx);
            }

            if (buffer.Idx >= buffer.Len)
            {
                break;
            }

            if (ApplyLookupSubtables(lookup))
            {
                ret = true;
            }
            else
            {
                buffer.NextGlyph();
            }
        }

        return ret;
    }

    private bool ApplyBackward(LayoutLookup lookup)
    {
        LayoutBuffer buffer = Buffer;
        bool ret = false;
        do
        {
            ref GlyphInfo cur = ref buffer.Cur();
            if (lookup.Covers(cur.Codepoint) && (cur.Mask & LookupMask) != 0 && CheckGlyphProperty(cur, LookupProps))
            {
                ret |= ApplyLookupSubtables(lookup);
            }

            buffer.Idx--;
        }
        while (buffer.Idx >= 0);
        return ret;
    }

    // ---- Matching ----

    private bool MatchValue(in GlyphInfo info, MatchKind kind, int value, int data)
    {
        switch (kind)
        {
            case MatchKind.Always:
                return true;
            case MatchKind.Glyph:
                return info.Codepoint == (uint)value;
            case MatchKind.Class:
                return ClassOf(D, data, info.Codepoint) == value;
            case MatchKind.Coverage:
                return Coverage(D, value == 0 ? 0 : data + value, info.Codepoint) >= 0;
            default:
                return false;
        }
    }

    /// <summary>
    /// Matches the input sequence after the current glyph: <paramref name="count"/> glyphs including the current one, against the
    /// values at <paramref name="values"/> (one per glyph after the first).
    /// </summary>
    public bool MatchInput(int count, int values, MatchKind kind, int data, out int endPosition, out int totalComponentCount)
    {
        LayoutBuffer buffer = Buffer;
        totalComponentCount = 0;
        if (count == 1)
        {
            endPosition = buffer.Idx + 1;
            MatchPositions[0] = buffer.Idx;
            totalComponentCount = LigNumComps(buffer.Cur());
            return true;
        }

        if (count > MaxContextLength)
        {
            endPosition = 0;
            return false;
        }

        SkippingIterator iter = IterInput;
        iter.Reset(buffer.Idx);
        iter.SetMatch(kind, values, data);

        int firstLigId = LigId(buffer.Cur());
        int firstLigComp = LigComp(buffer.Cur());
        int ligbase = 0; // 0 not checked, 1 may not skip, 2 may skip
        for (int i = 1; i < count; i++)
        {
            if (!iter.Next(out int unsafeTo))
            {
                endPosition = unsafeTo;
                return false;
            }

            MatchPositions[i] = iter.Idx;
            ref GlyphInfo info = ref buffer.Info[iter.Idx];
            int thisLigId = LigId(info);
            int thisLigComp = LigComp(info);
            if (firstLigId != 0 && firstLigComp != 0)
            {
                if (firstLigId != thisLigId || firstLigComp != thisLigComp)
                {
                    if (ligbase == 0)
                    {
                        bool found = false;
                        int j = buffer.OutLen;
                        while (j > 0 && LigId(buffer.OutInfo[j - 1]) == firstLigId)
                        {
                            if (LigComp(buffer.OutInfo[j - 1]) == 0)
                            {
                                j--;
                                found = true;
                                break;
                            }

                            j--;
                        }

                        ligbase = found && iter.MaySkip(buffer.OutInfo[j]) == SkipResult.Yes ? 2 : 1;
                    }

                    if (ligbase == 1)
                    {
                        endPosition = 0;
                        return false;
                    }
                }
            }
            else if (thisLigId != 0 && thisLigComp != 0 && thisLigId != firstLigId)
            {
                endPosition = 0;
                return false;
            }

            totalComponentCount += LigNumCompsInLigation(info);
        }

        endPosition = iter.Idx + 1;
        totalComponentCount += LigNumComps(buffer.Cur());
        MatchPositions[0] = buffer.Idx;
        return true;
    }

    public void LigateInput(int count, int matchEnd, uint ligGlyph, int totalComponentCount)
    {
        LayoutBuffer buffer = Buffer;
        buffer.MergeClusters(buffer.Idx, matchEnd);

        bool isBaseLigature = IsBaseGlyph(buffer.Info[MatchPositions[0]]);
        bool isMarkLigature = IsMark(buffer.Info[MatchPositions[0]]);
        for (int i = 1; i < count; i++)
        {
            if (!IsMark(buffer.Info[MatchPositions[i]]))
            {
                isBaseLigature = false;
                isMarkLigature = false;
                break;
            }
        }

        bool isLigature = !isBaseLigature && !isMarkLigature;
        int klass = isLigature ? GlyphProps.Ligature : 0;
        int ligId = isLigature ? AllocateLigId() : 0;
        int lastLigId = LigId(buffer.Cur());
        int lastNumComponents = LigNumComps(buffer.Cur());
        int componentsSoFar = lastNumComponents;

        if (isLigature)
        {
            SetLigPropsForLigature(ref buffer.Cur(), ligId, totalComponentCount);
            if (UnicodeProps.GeneralCategory(buffer.Cur()) == UnicodeProps.NonspacingMark)
            {
                UnicodeProps.SetGeneralCategory(ref buffer.Cur(), UnicodeProps.OtherLetter);
            }
        }

        ReplaceGlyphWithLigature(ligGlyph, klass);

        for (int i = 1; i < count; i++)
        {
            while (buffer.Idx < MatchPositions[i] && buffer.Successful)
            {
                if (isLigature)
                {
                    int thisComp = LigComp(buffer.Cur());
                    if (thisComp == 0)
                    {
                        thisComp = lastNumComponents;
                    }

                    int newLigComp = componentsSoFar - lastNumComponents + Math.Min(thisComp, lastNumComponents);
                    SetLigPropsForMark(ref buffer.Cur(), ligId, newLigComp);
                }

                buffer.NextGlyph();
            }

            lastLigId = LigId(buffer.Cur());
            lastNumComponents = LigNumCompsInLigation(buffer.Cur());
            componentsSoFar += lastNumComponents;

            // Skip the base glyph.
            buffer.Idx++;
        }

        if (!isMarkLigature && lastLigId != 0)
        {
            for (int i = buffer.Idx; i < buffer.Len; i++)
            {
                if (lastLigId != LigId(buffer.Info[i]))
                {
                    break;
                }

                int thisComp = LigComp(buffer.Info[i]);
                if (thisComp == 0)
                {
                    break;
                }

                int newLigComp = componentsSoFar - lastNumComponents + Math.Min(thisComp, lastNumComponents);
                SetLigPropsForMark(ref buffer.Info[i], ligId, newLigComp);
            }
        }
    }

    public bool MatchBacktrack(int count, int values, MatchKind kind, int data, out int matchStart)
    {
        if (count == 0)
        {
            matchStart = Buffer.BacktrackLen;
            return true;
        }

        SkippingIterator iter = IterContext;
        iter.ResetBack(Buffer.BacktrackLen);
        iter.SetMatch(kind, values, data);
        for (int i = 0; i < count; i++)
        {
            if (!iter.Prev(out int unsafeFrom))
            {
                matchStart = unsafeFrom;
                return false;
            }
        }

        matchStart = iter.Idx;
        return true;
    }

    public bool MatchLookahead(int count, int values, MatchKind kind, int data, int startIndex, out int endIndex)
    {
        if (count == 0)
        {
            endIndex = startIndex;
            return true;
        }

        SkippingIterator iter = IterContext;
        iter.Reset(startIndex - 1);
        iter.SetMatch(kind, values, data);
        for (int i = 0; i < count; i++)
        {
            if (!iter.Next(out int unsafeTo))
            {
                endIndex = unsafeTo;
                return false;
            }
        }

        endIndex = iter.Idx + 1;
        return true;
    }

    /// <summary>Applies the lookup records of a matched context, as HarfBuzz's apply_lookup.</summary>
    public void ApplyLookup(int count, int lookupCount, int lookupRecords, int matchEnd)
    {
        LayoutBuffer buffer = Buffer;
        ReadOnlySpan<byte> d = D;
        int[] matchPositions = MatchPositions;

        int bl = buffer.BacktrackLen;
        int end = bl + matchEnd - buffer.Idx;
        int delta0 = bl - buffer.Idx;
        for (int j = 0; j < count; j++)
        {
            matchPositions[j] += delta0;
        }

        for (int i = 0; i < lookupCount && buffer.Successful; i++)
        {
            int record = lookupRecords + (i * 4);
            int idx = U16(d, record);
            int lookupIndex = U16(d, record + 2);
            if (idx >= count)
            {
                continue;
            }

            int origLen = buffer.BacktrackLen + buffer.LookaheadLen;
            if (matchPositions[idx] >= origLen)
            {
                continue;
            }

            if (!buffer.MoveTo(matchPositions[idx]))
            {
                break;
            }

            if (buffer.MaxOps <= 0)
            {
                break;
            }

            if (!Recurse(lookupIndex))
            {
                continue;
            }

            int newLen = buffer.BacktrackLen + buffer.LookaheadLen;
            int delta = newLen - origLen;
            if (delta == 0)
            {
                continue;
            }

            end += delta;
            if (end < matchPositions[idx])
            {
                delta += matchPositions[idx] - end;
                end = matchPositions[idx];
            }

            int next = idx + 1;
            if (delta > 0)
            {
                if (delta + count > MaxContextLength)
                {
                    break;
                }
            }
            else
            {
                delta = Math.Max(delta, next - count);
                next -= delta;
            }

            Array.Copy(matchPositions, next, matchPositions, next + delta, count - next);
            next += delta;
            count += delta;
            for (int j = idx + 1; j < next; j++)
            {
                matchPositions[j] = matchPositions[j - 1] + 1;
            }

            for (; next < count; next++)
            {
                matchPositions[next] += delta;
            }
        }

        buffer.MoveTo(end);
    }

    // ---- Context and chain context ----

    /// <summary>Matches a context rule's input and applies its lookups, as HarfBuzz's context_apply_lookup.</summary>
    public bool ContextApplyLookup(int inputCount, int inputValues, MatchKind kind, int data, int lookupCount, int lookupRecords)
    {
        if (inputCount > MaxContextLength)
        {
            return false;
        }

        if (MatchInput(inputCount, inputValues, kind, data, out int matchEnd, out _))
        {
            Buffer.UnsafeToBreak(Buffer.Idx, matchEnd);
            ApplyLookup(inputCount, lookupCount, lookupRecords, matchEnd);
            return true;
        }

        Buffer.UnsafeToConcat(Buffer.Idx, matchEnd);
        return false;
    }

    /// <summary>Matches a chain rule's input, lookahead and backtrack and applies its lookups.</summary>
    public bool ChainContextApplyLookup(
        int backtrackCount, int backtrackValues, MatchKind backtrackKind, int backtrackData,
        int inputCount, int inputValues, MatchKind inputKind, int inputData,
        int lookaheadCount, int lookaheadValues, MatchKind lookaheadKind, int lookaheadData,
        int lookupCount, int lookupRecords)
    {
        if (inputCount > MaxContextLength)
        {
            return false;
        }

        LayoutBuffer buffer = Buffer;
        int startIndex = buffer.OutLen;
        int endIndex = buffer.Idx;
        if (!MatchInput(inputCount, inputValues, inputKind, inputData, out int matchEnd, out _))
        {
            buffer.UnsafeToConcat(buffer.Idx, endIndex);
            return false;
        }

        endIndex = matchEnd;
        if (!MatchLookahead(lookaheadCount, lookaheadValues, lookaheadKind, lookaheadData, matchEnd, out endIndex))
        {
            buffer.UnsafeToConcat(buffer.Idx, endIndex);
            return false;
        }

        if (!MatchBacktrack(backtrackCount, backtrackValues, backtrackKind, backtrackData, out startIndex))
        {
            buffer.UnsafeToConcatFromOutbuffer(startIndex, endIndex);
            return false;
        }

        buffer.UnsafeToBreakFromOutbuffer(startIndex, endIndex);
        ApplyLookup(inputCount, lookupCount, lookupRecords, matchEnd);
        return true;
    }

    /// <summary>Context lookup subtables, shared by GSUB type 5 and GPOS type 7.</summary>
    private bool ApplyContext(int t)
    {
        ReadOnlySpan<byte> d = D;
        uint glyph = Buffer.Cur().Codepoint;
        switch (U16(d, t))
        {
            case 1:
                {
                    int index = Coverage(d, Off16(d, t, t + 2), glyph);
                    if (index < 0)
                    {
                        return false;
                    }

                    return ApplyRuleSet(index < U16(d, t + 4) ? Off16(d, t, t + 6 + (index * 2)) : 0, MatchKind.Glyph, 0);
                }

            case 2:
                {
                    if (Coverage(d, Off16(d, t, t + 2), glyph) < 0)
                    {
                        return false;
                    }

                    int classDef = Off16(d, t, t + 4);
                    int index = ClassOf(d, classDef, glyph);
                    return ApplyRuleSet(index < U16(d, t + 6) ? Off16(d, t, t + 8 + (index * 2)) : 0, MatchKind.Class, classDef);
                }

            case 3:
                {
                    int glyphCount = U16(d, t + 2);
                    int lookupCount = U16(d, t + 4);
                    if (glyphCount == 0 || Coverage(d, Off16(d, t, t + 6), glyph) < 0)
                    {
                        return false;
                    }

                    return ContextApplyLookup(glyphCount, t + 8, MatchKind.Coverage, t, lookupCount, t + 6 + (glyphCount * 2));
                }

            default:
                return false;
        }
    }

    private bool ApplyRuleSet(int ruleSet, MatchKind kind, int data)
    {
        if (ruleSet == 0)
        {
            return false;
        }

        ReadOnlySpan<byte> d = D;
        int count = U16(d, ruleSet);
        for (int i = 0; i < count; i++)
        {
            int rule = Off16(d, ruleSet, ruleSet + 2 + (i * 2));
            if (rule == 0)
            {
                continue;
            }

            int inputCount = U16(d, rule);
            int lookupCount = U16(d, rule + 2);
            int lookupRecords = rule + 4 + (Math.Max(inputCount - 1, 0) * 2);
            if (ContextApplyLookup(inputCount, rule + 4, kind, data, lookupCount, lookupRecords))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Chain context lookup subtables, shared by GSUB type 6 and GPOS type 8.</summary>
    private bool ApplyChainContext(int t)
    {
        ReadOnlySpan<byte> d = D;
        uint glyph = Buffer.Cur().Codepoint;
        switch (U16(d, t))
        {
            case 1:
                {
                    int index = Coverage(d, Off16(d, t, t + 2), glyph);
                    if (index < 0)
                    {
                        return false;
                    }

                    int set = index < U16(d, t + 4) ? Off16(d, t, t + 6 + (index * 2)) : 0;
                    return ApplyChainRuleSet(set, MatchKind.Glyph, 0, 0, 0);
                }

            case 2:
                {
                    if (Coverage(d, Off16(d, t, t + 2), glyph) < 0)
                    {
                        return false;
                    }

                    int backtrackClassDef = Off16(d, t, t + 4);
                    int inputClassDef = Off16(d, t, t + 6);
                    int lookaheadClassDef = Off16(d, t, t + 8);
                    int index = ClassOf(d, inputClassDef, glyph);
                    int set = index < U16(d, t + 10) ? Off16(d, t, t + 12 + (index * 2)) : 0;
                    return ApplyChainRuleSet(set, MatchKind.Class, backtrackClassDef, inputClassDef, lookaheadClassDef);
                }

            case 3:
                {
                    int backtrackCount = U16(d, t + 2);
                    int backtrack = t + 4;
                    int inputCountAt = backtrack + (backtrackCount * 2);
                    int inputCount = U16(d, inputCountAt);
                    int input = inputCountAt + 2;
                    int lookaheadCountAt = input + (inputCount * 2);
                    int lookaheadCount = U16(d, lookaheadCountAt);
                    int lookahead = lookaheadCountAt + 2;
                    int lookupCountAt = lookahead + (lookaheadCount * 2);
                    int lookupCount = U16(d, lookupCountAt);
                    if (inputCount == 0 || Coverage(d, Off16(d, t, input), glyph) < 0)
                    {
                        return false;
                    }

                    return ChainContextApplyLookup(
                        backtrackCount, backtrack, MatchKind.Coverage, t,
                        inputCount, input + 2, MatchKind.Coverage, t,
                        lookaheadCount, lookahead, MatchKind.Coverage, t,
                        lookupCount, lookupCountAt + 2);
                }

            default:
                return false;
        }
    }

    private bool ApplyChainRuleSet(int ruleSet, MatchKind kind, int backtrackData, int inputData, int lookaheadData)
    {
        if (ruleSet == 0)
        {
            return false;
        }

        ReadOnlySpan<byte> d = D;
        int count = U16(d, ruleSet);
        for (int i = 0; i < count; i++)
        {
            int rule = Off16(d, ruleSet, ruleSet + 2 + (i * 2));
            if (rule == 0)
            {
                continue;
            }

            int backtrackCount = U16(d, rule);
            int backtrack = rule + 2;
            int inputCountAt = backtrack + (backtrackCount * 2);
            int inputCount = U16(d, inputCountAt);
            int input = inputCountAt + 2;
            int lookaheadCountAt = input + (Math.Max(inputCount - 1, 0) * 2);
            int lookaheadCount = U16(d, lookaheadCountAt);
            int lookahead = lookaheadCountAt + 2;
            int lookupCountAt = lookahead + (lookaheadCount * 2);
            int lookupCount = U16(d, lookupCountAt);
            if (ChainContextApplyLookup(
                backtrackCount, backtrack, kind, backtrackData,
                inputCount, input, kind, inputData,
                lookaheadCount, lookahead, kind, lookaheadData,
                lookupCount, lookupCountAt + 2))
            {
                return true;
            }
        }

        return false;
    }

    // ---- Glyph info helpers ----

    public const int IsLigBase = 0x10;

    public static int LigId(in GlyphInfo info) => info.LigProps >> 5;

    public static bool LigatedInternal(in GlyphInfo info) => (info.LigProps & IsLigBase) != 0;

    public static int LigComp(in GlyphInfo info) => LigatedInternal(info) ? 0 : info.LigProps & 0x0F;

    public static int LigNumComps(in GlyphInfo info) =>
        (info.GlyphProps & GlyphProps.Ligature) != 0 && LigatedInternal(info) ? info.LigProps & 0x0F : 1;

    public static int LigNumCompsInLigation(in GlyphInfo info) =>
        IsMultiplied(info) && LigComp(info) != 0 ? 0 : LigNumComps(info);

    public static void SetLigPropsForLigature(ref GlyphInfo info, int ligId, int numComps) =>
        info.LigProps = (byte)((ligId << 5) | IsLigBase | (numComps & 0x0F));

    public static void SetLigPropsForMark(ref GlyphInfo info, int ligId, int comp) =>
        info.LigProps = (byte)((ligId << 5) | (comp & 0x0F));

    public static void SetLigPropsForComponent(ref GlyphInfo info, int comp) => SetLigPropsForMark(ref info, 0, comp);

    public static bool IsBaseGlyph(in GlyphInfo info) => (info.GlyphProps & GlyphProps.BaseGlyph) != 0;

    public static bool IsLigature(in GlyphInfo info) => (info.GlyphProps & GlyphProps.Ligature) != 0;

    public static bool IsMark(in GlyphInfo info) => (info.GlyphProps & GlyphProps.Mark) != 0;

    public static bool IsMultiplied(in GlyphInfo info) => (info.GlyphProps & GlyphProps.Multiplied) != 0;

    private int AllocateLigId()
    {
        int ligId = Buffer.NextSerial() & 0x07;
        while (ligId == 0)
        {
            ligId = Buffer.NextSerial() & 0x07;
        }

        return ligId;
    }

    // ---- The skipping iterator ----

    internal enum SkipResult
    {
        No,
        Yes,
        Maybe,
    }

    internal enum IterMatch
    {
        Match,
        NotMatch,
        Skip,
    }

    /// <summary>Walks the buffer forward or backward, skipping glyphs the lookup ignores, as HarfBuzz's skipping_iterator_t.</summary>
    internal sealed class SkippingIterator(LayoutApplier c)
    {
        public int Idx;
        private int _end;
        private int _lookupProps;
        private uint _mask;
        private bool _ignoreZwnj;
        private bool _ignoreZwj;
        private bool _ignoreHidden;
        private bool _perSyllable;
        private byte _syllable;
        private MatchKind _kind;
        private int _values;
        private int _data;

        public void Init(bool contextMatch)
        {
            _end = c.Buffer.Len;
            _kind = MatchKind.None;
            _values = 0;
            _data = 0;
            _lookupProps = c.LookupProps;
            _ignoreZwnj = c.TableIndex == 1 || (contextMatch && c.AutoZwnj);
            _ignoreZwj = contextMatch || c.AutoZwj;
            _ignoreHidden = c.TableIndex == 1;
            _mask = contextMatch ? uint.MaxValue : c.LookupMask;
            _perSyllable = c.TableIndex == 0 && c.PerSyllable;
            _syllable = 0;
        }

        public void SetLookupProps(int props) => _lookupProps = props;

        public void SetMatch(MatchKind kind, int values, int data)
        {
            _kind = kind;
            _values = values;
            _data = data;
        }

        public void Reset(int start)
        {
            Idx = start;
            _end = c.Buffer.Len;
            _syllable = c.Buffer.Idx < c.Buffer.Len ? c.Buffer.Cur().Syllable : (byte)0;
        }

        public void ResetBack(int start)
        {
            Idx = start;
            _syllable = c.Buffer.Idx < c.Buffer.Len ? c.Buffer.Cur().Syllable : (byte)0;
        }

        public void ResetFast(int start) => Idx = start;

        public SkipResult MaySkip(in GlyphInfo info)
        {
            if (!c.CheckGlyphProperty(info, _lookupProps))
            {
                return SkipResult.Yes;
            }

            if (UnicodeProps.IsDefaultIgnorable(info) &&
                (_ignoreZwnj || !UnicodeProps.IsZwnj(info)) &&
                (_ignoreZwj || !UnicodeProps.IsZwj(info)) &&
                (_ignoreHidden || !UnicodeProps.IsHidden(info)))
            {
                return SkipResult.Maybe;
            }

            return SkipResult.No;
        }

        private int MayMatch(in GlyphInfo info)
        {
            // 0 no, 1 yes, 2 maybe.
            if ((info.Mask & _mask) == 0 || (_perSyllable && _syllable != 0 && _syllable != info.Syllable))
            {
                return 0;
            }

            if (_kind != MatchKind.None)
            {
                int value = U16(c.D, _values);
                return c.MatchValue(info, _kind, value, _data) ? 1 : 0;
            }

            return 2;
        }

        public IterMatch Match(in GlyphInfo info)
        {
            SkipResult skip = MaySkip(info);
            if (skip == SkipResult.Yes)
            {
                return IterMatch.Skip;
            }

            int match = MayMatch(info);
            if (match == 1 || (match == 2 && skip == SkipResult.No))
            {
                return IterMatch.Match;
            }

            if (skip == SkipResult.No)
            {
                return IterMatch.NotMatch;
            }

            return IterMatch.Skip;
        }

        public bool Next(out int unsafeTo)
        {
            GlyphInfo[] info = c.Buffer.Info;
            int stop = _end - 1;
            while (Idx < stop)
            {
                Idx++;
                switch (Match(info[Idx]))
                {
                    case IterMatch.Match:
                        AdvanceGlyphData();
                        unsafeTo = 0;
                        return true;
                    case IterMatch.NotMatch:
                        unsafeTo = Idx + 1;
                        return false;
                }
            }

            unsafeTo = _end;
            return false;
        }

        public bool Prev(out int unsafeFrom)
        {
            GlyphInfo[] outInfo = c.Buffer.OutInfo;
            while (Idx > 0)
            {
                Idx--;
                switch (Match(outInfo[Idx]))
                {
                    case IterMatch.Match:
                        AdvanceGlyphData();
                        unsafeFrom = 0;
                        return true;
                    case IterMatch.NotMatch:
                        unsafeFrom = Math.Max(1, Idx) - 1;
                        return false;
                }
            }

            unsafeFrom = 0;
            return false;
        }

        private void AdvanceGlyphData()
        {
            if (_kind != MatchKind.None && _kind != MatchKind.Always)
            {
                _values += 2;
            }
        }
    }
}
