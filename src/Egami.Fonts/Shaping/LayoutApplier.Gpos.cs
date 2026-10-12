// Translated from HarfBuzz's OT/Layout/GPOS headers (MIT); see THIRD-PARTY-NOTICES.md.
using System.Numerics;

using static Egami.Fonts.Shaping.OtData;

namespace Egami.Fonts.Shaping;

internal sealed partial class LayoutApplier
{
    public const byte AttachTypeMark = 1;
    public const byte AttachTypeCursive = 2;

    private const int XPlacement = 0x0001;
    private const int YPlacement = 0x0002;
    private const int XAdvance = 0x0004;
    private const int YAdvance = 0x0008;
    private const int XPlaDevice = 0x0010;
    private const int YPlaDevice = 0x0020;
    private const int XAdvDevice = 0x0040;
    private const int YAdvDevice = 0x0080;

    private bool ApplyGposSubtable(int type, int t)
    {
        switch (type)
        {
            case 1:
                return ApplySinglePos(t);
            case 2:
                return ApplyPairPos(t);
            case 3:
                return ApplyCursivePos(t);
            case 4:
                return ApplyMarkBasePos(t);
            case 5:
                return ApplyMarkLigPos(t);
            case 6:
                return ApplyMarkMarkPos(t);
            case 7:
                return ApplyContext(t);
            case 8:
                return ApplyChainContext(t);
            default:
                return false;
        }
    }

    private static int ValueLength(int format) => BitOperations.PopCount((uint)format & 0xFF);

    private static bool IsHorizontalDirection(LayoutDirection d) => LayoutBuffer.IsHorizontal(d);

    /// <summary>Adds a value record to a position; true when any of its values is not zero.</summary>
    private bool ApplyValue(int format, int baseOffset, int values, ref GlyphPos pos)
    {
        bool ret = false;
        if (format == 0)
        {
            return false;
        }

        ReadOnlySpan<byte> d = D;
        bool horizontal = IsHorizontalDirection(Direction);
        int v = values;
        if ((format & XPlacement) != 0)
        {
            int value = S16(d, v);
            ret |= value != 0;
            pos.XOffset = SaturateAdd(pos.XOffset, EmScale(value, XMult));
            v += 2;
        }

        if ((format & YPlacement) != 0)
        {
            int value = S16(d, v);
            ret |= value != 0;
            pos.YOffset = SaturateAdd(pos.YOffset, EmScale(value, YMult));
            v += 2;
        }

        if ((format & XAdvance) != 0)
        {
            int value = S16(d, v);
            ret |= value != 0;
            if (horizontal)
            {
                pos.XAdvance = SaturateAdd(pos.XAdvance, EmScale(value, XMult));
            }

            v += 2;
        }

        if ((format & YAdvance) != 0)
        {
            int value = S16(d, v);
            ret |= value != 0;
            if (!horizontal)
            {
                pos.YAdvance = SaturateSub(pos.YAdvance, EmScale(value, YMult));
            }

            v += 2;
        }

        if ((format & 0xF0) == 0 || VariationDelta is null)
        {
            return ret;
        }

        if ((format & XPlaDevice) != 0)
        {
            pos.XOffset = SaturateAdd(pos.XOffset, DeviceDelta(baseOffset, v, XMultF, ref ret));
            v += 2;
        }

        if ((format & YPlaDevice) != 0)
        {
            pos.YOffset = SaturateAdd(pos.YOffset, DeviceDelta(baseOffset, v, YMultF, ref ret));
            v += 2;
        }

        if ((format & XAdvDevice) != 0)
        {
            int delta = DeviceDelta(baseOffset, v, XMultF, ref ret);
            if (horizontal)
            {
                pos.XAdvance = SaturateAdd(pos.XAdvance, delta);
            }

            v += 2;
        }

        if ((format & YAdvDevice) != 0)
        {
            int delta = DeviceDelta(baseOffset, v, YMultF, ref ret);
            if (!horizontal)
            {
                pos.YAdvance = SaturateSub(pos.YAdvance, delta);
            }
        }

        return ret;
    }

    /// <summary>A font-unit value scaled as HarfBuzz's em_mult: 16.16 fixed point, rounded.</summary>
    private static int EmScale(int value, long mult) => (int)(((value * mult) + 32768) >> 16);

    /// <summary>
    /// A variation-index device table's delta, scaled and rounded as em_scalef; hinting device tables give
    /// nothing (Egami does not hint layout).
    /// </summary>
    private int DeviceDelta(int baseOffset, int at, float mult, ref bool ret)
    {
        ReadOnlySpan<byte> d = D;
        int device = Off16(d, baseOffset, at);
        if (device == 0)
        {
            return 0;
        }

        ret = true;
        if (U16(d, device + 4) != 0x8000)
        {
            return 0;
        }

        return RoundF(VariationDelta!(U16(d, device), U16(d, device + 2)) * mult);
    }

    /// <summary>An anchor's device delta, rounded as get_x_delta and get_y_delta give it.</summary>
    private int AnchorDelta(int device, float mult)
    {
        ReadOnlySpan<byte> d = D;
        if (device == 0 || VariationDelta is null || U16(d, device + 4) != 0x8000)
        {
            return 0;
        }

        return RoundF(VariationDelta(U16(d, device), U16(d, device + 2)) * mult);
    }

    /// <summary>Whether an anchor fits in the table, as HarfBuzz checks one before using it (GPOS anchors are checked lazily).</summary>
    private bool AnchorSound(int anchor)
    {
        ReadOnlySpan<byte> d = D;
        if (anchor <= 0 || anchor + 2 > d.Length)
        {
            return false;
        }

        switch (U16(d, anchor))
        {
            case 1:
                return anchor + 6 <= d.Length;
            case 2:
                return anchor + 8 <= d.Length;
            case 3:
                return anchor + 10 <= d.Length && DeviceSound(anchor, anchor + 6) && DeviceSound(anchor, anchor + 8);
            default:
                return true;
        }
    }

    private bool DeviceSound(int baseOffset, int at)
    {
        ReadOnlySpan<byte> d = D;
        int off = U16(d, at);
        if (off == 0)
        {
            return true;
        }

        int t = baseOffset + off;
        if (t + 6 > d.Length)
        {
            return false;
        }

        int format = U16(d, t + 4);
        if (format is >= 1 and <= 3)
        {
            int start = U16(d, t), end = U16(d, t + 2);
            int size = start > end ? 6 : 2 * (4 + ((end - start) >> (4 - format)));
            return t + size <= d.Length;
        }

        return true;
    }

    /// <summary>An anchor's point in font units.</summary>
    private void GetAnchor(int anchor, out float x, out float y)
    {
        ReadOnlySpan<byte> d = D;
        x = 0;
        y = 0;
        if (anchor == 0)
        {
            return;
        }

        int format = U16(d, anchor);
        if (format is < 1 or > 3)
        {
            return;
        }

        x = S16(d, anchor + 2) * XMultF;
        y = S16(d, anchor + 4) * YMultF;
        if (format == 3 && VariationDelta is not null)
        {
            x += AnchorDelta(Off16(d, anchor, anchor + 6), XMultF);
            y += AnchorDelta(Off16(d, anchor, anchor + 8), YMultF);
        }
    }

    private bool ApplySinglePos(int t)
    {
        ReadOnlySpan<byte> d = D;
        int index = Coverage(d, Off16(d, t, t + 2), Buffer.Cur().Codepoint);
        if (index < 0)
        {
            return false;
        }

        int format = U16(d, t);
        int valueFormat = U16(d, t + 4);
        if (format == 1)
        {
            ApplyValue(valueFormat, t, t + 6, ref Buffer.CurPos());
        }
        else if (format == 2)
        {
            if (index >= U16(d, t + 6))
            {
                return false;
            }

            ApplyValue(valueFormat, t, t + 8 + (index * ValueLength(valueFormat) * 2), ref Buffer.CurPos());
        }
        else
        {
            return false;
        }

        Buffer.Idx++;
        return true;
    }

    private bool ApplyPairPos(int t)
    {
        ReadOnlySpan<byte> d = D;
        LayoutBuffer buffer = Buffer;
        int format = U16(d, t);
        int index = Coverage(d, Off16(d, t, t + 2), buffer.Cur().Codepoint);
        if (index < 0 || format is < 1 or > 2)
        {
            return false;
        }

        SkippingIterator iter = IterInput;
        iter.ResetFast(buffer.Idx);
        if (!iter.Next(out int unsafeTo))
        {
            buffer.UnsafeToConcat(buffer.Idx, unsafeTo);
            return false;
        }

        int valueFormat1 = U16(d, t + 4);
        int valueFormat2 = U16(d, t + 6);
        int len1 = ValueLength(valueFormat1);
        int len2 = ValueLength(valueFormat2);
        int pos = iter.Idx;

        if (format == 1)
        {
            int set = index < U16(d, t + 8) ? Off16(d, t, t + 10 + (index * 2)) : 0;
            int count = set == 0 ? 0 : U16(d, set);
            int recordSize = 2 + ((len1 + len2) * 2);
            uint second = buffer.Info[pos].Codepoint;
            int lo = 0, hi = count - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >>> 1;
                uint g = U16(d, set + 2 + (mid * recordSize));
                if (second < g)
                {
                    hi = mid - 1;
                }
                else if (second > g)
                {
                    lo = mid + 1;
                }
                else
                {
                    found = mid;
                    break;
                }
            }

            if (found < 0)
            {
                buffer.UnsafeToConcat(buffer.Idx, pos + 1);
                return false;
            }

            int record = set + 2 + (found * recordSize) + 2;
            bool appliedFirst = len1 != 0 && ApplyValue(valueFormat1, set, record, ref buffer.CurPos());
            bool appliedSecond = len2 != 0 && ApplyValue(valueFormat2, set, record + (len1 * 2), ref buffer.Pos[pos]);
            FinishPair(appliedFirst || appliedSecond, len2, pos);
            return true;
        }
        else
        {
            int classDef1 = Off16(d, t, t + 8);
            int classDef2 = Off16(d, t, t + 10);
            int class1Count = U16(d, t + 12);
            int class2Count = U16(d, t + 14);
            int klass1 = ClassOf(d, classDef1, buffer.Cur().Codepoint);
            int klass2 = ClassOf(d, classDef2, buffer.Info[pos].Codepoint);
            if (klass1 >= class1Count || klass2 >= class2Count)
            {
                buffer.UnsafeToConcat(buffer.Idx, pos + 1);
                return false;
            }

            int record = t + 16 + ((((klass1 * class2Count) + klass2) * (len1 + len2)) * 2);
            bool appliedFirst = len1 != 0 && ApplyValue(valueFormat1, t, record, ref buffer.CurPos());
            bool appliedSecond = len2 != 0 && ApplyValue(valueFormat2, t, record + (len1 * 2), ref buffer.Pos[pos]);
            FinishPair(appliedFirst || appliedSecond, len2, pos);
            return true;
        }
    }

    private void FinishPair(bool applied, int len2, int pos)
    {
        LayoutBuffer buffer = Buffer;
        if (applied)
        {
            buffer.UnsafeToBreak(buffer.Idx, pos + 1);
        }
        else
        {
            buffer.UnsafeToConcat(buffer.Idx, pos + 1);
        }

        if (len2 != 0)
        {
            pos++;
            buffer.UnsafeToBreak(buffer.Idx, pos + 1);
        }

        buffer.Idx = pos;
    }

    private bool ApplyCursivePos(int t)
    {
        ReadOnlySpan<byte> d = D;
        LayoutBuffer buffer = Buffer;
        if (U16(d, t) != 1)
        {
            return false;
        }

        int coverage = Off16(d, t, t + 2);
        int count = U16(d, t + 4);
        int thisIndex = Coverage(d, coverage, buffer.Cur().Codepoint);
        int thisEntry = (uint)thisIndex < (uint)count ? Off16(d, t, t + 6 + (thisIndex * 4)) : 0;
        if (thisEntry == 0 || !AnchorSound(thisEntry))
        {
            return false;
        }

        SkippingIterator iter = IterInput;
        iter.ResetFast(buffer.Idx);
        if (!iter.Prev(out int unsafeFrom))
        {
            buffer.UnsafeToConcatFromOutbuffer(unsafeFrom, buffer.Idx + 1);
            return false;
        }

        int prevIndex = Coverage(d, coverage, buffer.Info[iter.Idx].Codepoint);
        int prevExit = (uint)prevIndex < (uint)count ? Off16(d, t, t + 6 + (prevIndex * 4) + 2) : 0;
        if (prevExit == 0 || !AnchorSound(prevExit))
        {
            buffer.UnsafeToConcatFromOutbuffer(iter.Idx, buffer.Idx + 1);
            return false;
        }

        int i = iter.Idx;
        int j = buffer.Idx;
        buffer.UnsafeToBreak(i, j + 1);
        GetAnchor(prevExit, out float exitX, out float exitY);
        GetAnchor(thisEntry, out float entryX, out float entryY);

        GlyphPos[] pos = buffer.Pos;
        int dd;
        switch (Direction)
        {
            case LayoutDirection.LeftToRight:
                pos[i].XAdvance = SaturateAdd(RoundF(exitX), pos[i].XOffset);
                dd = SaturateAdd(RoundF(entryX), pos[j].XOffset);
                pos[j].XAdvance = SaturateSub(pos[j].XAdvance, dd);
                pos[j].XOffset = SaturateSub(pos[j].XOffset, dd);
                break;
            case LayoutDirection.RightToLeft:
                dd = SaturateAdd(RoundF(exitX), pos[i].XOffset);
                pos[i].XAdvance = SaturateSub(pos[i].XAdvance, dd);
                pos[i].XOffset = SaturateSub(pos[i].XOffset, dd);
                pos[j].XAdvance = SaturateAdd(RoundF(entryX), pos[j].XOffset);
                break;
            case LayoutDirection.TopToBottom:
                pos[i].YAdvance = SaturateAdd(RoundF(exitY), pos[i].YOffset);
                dd = SaturateAdd(RoundF(entryY), pos[j].YOffset);
                pos[j].YAdvance = SaturateSub(pos[j].YAdvance, dd);
                pos[j].YOffset = SaturateSub(pos[j].YOffset, dd);
                break;
            case LayoutDirection.BottomToTop:
                dd = SaturateAdd(RoundF(exitY), pos[i].YOffset);
                pos[i].YAdvance = SaturateSub(pos[i].YAdvance, dd);
                pos[i].YOffset = SaturateSub(pos[i].YOffset, dd);
                pos[j].YAdvance = RoundF(entryY);
                break;
        }

        int child = i;
        int parent = j;
        int xOffset = RoundF(entryX - exitX);
        int yOffset = RoundF(entryY - exitY);
        if ((LookupProps & LookupFlag.RightToLeft) == 0)
        {
            (child, parent) = (parent, child);
            xOffset = -xOffset;
            yOffset = -yOffset;
        }

        ReverseCursiveMinorOffset(pos, buffer.Len, child, Direction, parent, 0);
        int chain = parent - child;
        if (chain is >= short.MinValue and <= short.MaxValue)
        {
            pos[child].AttachChain = (short)chain;
            pos[child].AttachType = AttachTypeCursive;
            buffer.ScratchFlags |= LayoutBuffer.ScratchHasGposAttachment;
            if (IsHorizontalDirection(Direction))
            {
                pos[child].YOffset = yOffset;
            }
            else
            {
                pos[child].XOffset = xOffset;
            }

            if (pos[parent].AttachChain == -pos[child].AttachChain)
            {
                pos[parent].AttachChain = 0;
                if (IsHorizontalDirection(Direction))
                {
                    pos[parent].YOffset = 0;
                }
                else
                {
                    pos[parent].XOffset = 0;
                }
            }
        }
        else
        {
            pos[child].AttachChain = 0;
        }

        buffer.Idx++;
        return true;
    }

    private static void ReverseCursiveMinorOffset(GlyphPos[] pos, int len, int i, LayoutDirection direction, int newParent, int nesting)
    {
        if (nesting > MaxNestingLevel)
        {
            return;
        }

        int chain = pos[i].AttachChain;
        int type = pos[i].AttachType;
        if (chain == 0 || (type & AttachTypeCursive) == 0)
        {
            return;
        }

        pos[i].AttachChain = 0;
        int j = i + chain;
        if ((uint)j >= (uint)len || j == newParent)
        {
            return;
        }

        int reversed = -chain;
        if (reversed is < short.MinValue or > short.MaxValue)
        {
            return;
        }

        ReverseCursiveMinorOffset(pos, len, j, direction, newParent, nesting + 1);
        if (IsHorizontalDirection(direction))
        {
            pos[j].YOffset = SaturateNeg(pos[i].YOffset);
        }
        else
        {
            pos[j].XOffset = SaturateNeg(pos[i].XOffset);
        }

        pos[j].AttachChain = (short)reversed;
        pos[j].AttachType = (byte)type;
    }

    private static bool AcceptMarkBase(LayoutBuffer buffer, int idx)
    {
        GlyphInfo[] info = buffer.Info;
        return !IsMultiplied(info[idx]) ||
            LigComp(info[idx]) == 0 ||
            idx == 0 ||
            IsMark(info[idx - 1]) ||
            !IsMultiplied(info[idx - 1]) ||
            LigId(info[idx]) != LigId(info[idx - 1]) ||
            LigComp(info[idx]) != LigComp(info[idx - 1]) + 1;
    }

    private static bool AcceptMarkLig(LayoutBuffer buffer, int idx) =>
        !IsMultiplied(buffer.Info[idx]) || LigComp(buffer.Info[idx]) == 0;

    /// <summary>Finds the base a mark attaches to, searching back past marks with the cached last base, as HarfBuzz does.</summary>
    private int FindMarkBase(int baseCoverage, bool ligature)
    {
        LayoutBuffer buffer = Buffer;
        ReadOnlySpan<byte> d = D;
        SkippingIterator iter = IterInput;
        iter.SetLookupProps(LookupFlag.IgnoreMarks);
        if (LastBaseUntil > buffer.Idx)
        {
            LastBaseUntil = 0;
            LastBase = -1;
        }

        for (int j = buffer.Idx; j > LastBaseUntil; j--)
        {
            IterMatch match = iter.Match(buffer.Info[j - 1]);
            if (match == IterMatch.Match)
            {
                bool accept = ligature ? AcceptMarkLig(buffer, j - 1) : AcceptMarkBase(buffer, j - 1);
                if (!accept && Coverage(d, baseCoverage, buffer.Info[j - 1].Codepoint) < 0)
                {
                    match = IterMatch.Skip;
                }
            }

            if (match == IterMatch.Match)
            {
                LastBase = j - 1;
                break;
            }
        }

        LastBaseUntil = buffer.Idx;
        return LastBase;
    }

    private bool ApplyMarkBasePos(int t)
    {
        ReadOnlySpan<byte> d = D;
        LayoutBuffer buffer = Buffer;
        if (U16(d, t) != 1)
        {
            return false;
        }

        int markIndex = Coverage(d, Off16(d, t, t + 2), buffer.Cur().Codepoint);
        if (markIndex < 0)
        {
            return false;
        }

        int baseCoverage = Off16(d, t, t + 4);
        int baseIdx = FindMarkBase(baseCoverage, ligature: false);
        if (baseIdx == -1)
        {
            buffer.UnsafeToConcatFromOutbuffer(0, buffer.Idx + 1);
            return false;
        }

        int baseIndex = Coverage(d, baseCoverage, buffer.Info[baseIdx].Codepoint);
        if (baseIndex < 0)
        {
            buffer.UnsafeToConcatFromOutbuffer(baseIdx, buffer.Idx + 1);
            return false;
        }

        int classCount = U16(d, t + 6);
        int markArray = Off16(d, t, t + 8);
        int baseArray = Off16(d, t, t + 10);
        return ApplyMarkArray(markArray, markIndex, baseIndex, baseArray, classCount, baseIdx);
    }

    private bool ApplyMarkLigPos(int t)
    {
        ReadOnlySpan<byte> d = D;
        LayoutBuffer buffer = Buffer;
        if (U16(d, t) != 1)
        {
            return false;
        }

        int markIndex = Coverage(d, Off16(d, t, t + 2), buffer.Cur().Codepoint);
        if (markIndex < 0)
        {
            return false;
        }

        int ligatureCoverage = Off16(d, t, t + 4);
        int idx = FindMarkBase(ligatureCoverage, ligature: true);
        if (idx == -1)
        {
            buffer.UnsafeToConcatFromOutbuffer(0, buffer.Idx + 1);
            return false;
        }

        int ligIndex = Coverage(d, ligatureCoverage, buffer.Info[idx].Codepoint);
        if (ligIndex < 0)
        {
            buffer.UnsafeToConcatFromOutbuffer(idx, buffer.Idx + 1);
            return false;
        }

        int classCount = U16(d, t + 6);
        int markArray = Off16(d, t, t + 8);
        int ligatureArray = Off16(d, t, t + 10);
        int ligAttach = ligatureArray != 0 && ligIndex < U16(d, ligatureArray) ? Off16(d, ligatureArray, ligatureArray + 2 + (ligIndex * 2)) : 0;
        int compCount = ligAttach == 0 ? 0 : U16(d, ligAttach);
        if (compCount == 0)
        {
            buffer.UnsafeToConcatFromOutbuffer(idx, buffer.Idx + 1);
            return false;
        }

        int ligId = LigId(buffer.Info[idx]);
        int markId = LigId(buffer.Cur());
        int markComp = LigComp(buffer.Cur());
        int compIndex = ligId != 0 && ligId == markId && markComp > 0 ? Math.Min(compCount, markComp) - 1 : compCount - 1;
        return ApplyMarkArray(markArray, markIndex, compIndex, ligAttach, classCount, idx);
    }

    private bool ApplyMarkMarkPos(int t)
    {
        ReadOnlySpan<byte> d = D;
        LayoutBuffer buffer = Buffer;
        if (U16(d, t) != 1)
        {
            return false;
        }

        int mark1Index = Coverage(d, Off16(d, t, t + 2), buffer.Cur().Codepoint);
        if (mark1Index < 0)
        {
            return false;
        }

        SkippingIterator iter = IterInput;
        iter.ResetFast(buffer.Idx);
        iter.SetLookupProps(LookupProps & ~LookupFlag.IgnoreFlags);
        if (!iter.Prev(out int unsafeFrom))
        {
            buffer.UnsafeToConcatFromOutbuffer(unsafeFrom, buffer.Idx + 1);
            return false;
        }

        if (!IsMark(buffer.Info[iter.Idx]))
        {
            buffer.UnsafeToConcatFromOutbuffer(iter.Idx, buffer.Idx + 1);
            return false;
        }

        int j = iter.Idx;
        int id1 = LigId(buffer.Cur());
        int id2 = LigId(buffer.Info[j]);
        int comp1 = LigComp(buffer.Cur());
        int comp2 = LigComp(buffer.Info[j]);
        bool good;
        if (id1 == id2)
        {
            good = id1 == 0 || comp1 == comp2;
        }
        else
        {
            good = (id1 > 0 && comp1 == 0) || (id2 > 0 && comp2 == 0);
        }

        if (!good)
        {
            buffer.UnsafeToConcatFromOutbuffer(iter.Idx, buffer.Idx + 1);
            return false;
        }

        int mark2Index = Coverage(d, Off16(d, t, t + 4), buffer.Info[j].Codepoint);
        if (mark2Index < 0)
        {
            buffer.UnsafeToConcatFromOutbuffer(iter.Idx, buffer.Idx + 1);
            return false;
        }

        int classCount = U16(d, t + 6);
        int mark1Array = Off16(d, t, t + 8);
        int mark2Array = Off16(d, t, t + 10);
        return ApplyMarkArray(mark1Array, mark1Index, mark2Index, mark2Array, classCount, j);
    }

    /// <summary>Attaches the current mark to the glyph at <paramref name="glyphPos"/>, as HarfBuzz's MarkArray::apply.</summary>
    private bool ApplyMarkArray(int markArray, int markIndex, int glyphIndex, int anchors, int classCount, int glyphPos)
    {
        ReadOnlySpan<byte> d = D;
        LayoutBuffer buffer = Buffer;
        int markClass = 0;
        int markAnchor = 0;
        if (markArray != 0 && markIndex < U16(d, markArray))
        {
            int markRecord = markArray + 2 + (markIndex * 4);
            markClass = U16(d, markRecord);
            markAnchor = Off16(d, markArray, markRecord + 2);
        }

        // The anchor matrix: rows of classCount anchor offsets.
        if (anchors == 0)
        {
            return false;
        }

        int rows = U16(d, anchors);
        if (glyphIndex >= rows || markClass >= classCount)
        {
            return false;
        }

        int glyphAnchor = Off16(d, anchors, anchors + 2 + (((glyphIndex * classCount) + markClass) * 2));
        if (glyphAnchor == 0 || !AnchorSound(glyphAnchor))
        {
            return false;
        }

        buffer.UnsafeToBreak(glyphPos, buffer.Idx + 1);
        GetAnchor(markAnchor, out float markX, out float markY);
        GetAnchor(glyphAnchor, out float baseX, out float baseY);

        int baseOffset = ResolveCrossOffset(buffer.Pos, buffer.Len, glyphPos, buffer.Direction);
        ref GlyphPos mark = ref buffer.CurPos();
        int chain = glyphPos - buffer.Idx;
        if (chain is >= short.MinValue and <= short.MaxValue)
        {
            mark.AttachChain = (short)chain;
            mark.AttachType = AttachTypeMark;
            mark.XOffset = RoundF(baseX - markX);
            mark.YOffset = RoundF(baseY - markY);
            if (IsHorizontalDirection(buffer.Direction))
            {
                mark.YOffset = SaturateAdd(mark.YOffset, baseOffset);
            }
            else
            {
                mark.XOffset = SaturateAdd(mark.XOffset, baseOffset);
            }

            buffer.ScratchFlags |= LayoutBuffer.ScratchHasGposAttachment;
        }
        else
        {
            mark.AttachChain = 0;
        }

        buffer.Idx++;
        return true;
    }

    private static int ResolveCrossOffset(GlyphPos[] pos, int len, int glyphPos, LayoutDirection direction)
    {
        bool horizontal = IsHorizontalDirection(direction);
        int offset = horizontal ? pos[glyphPos].YOffset : pos[glyphPos].XOffset;
        while ((pos[glyphPos].AttachType & AttachTypeCursive) != 0)
        {
            int chain = pos[glyphPos].AttachChain;
            if (chain == 0)
            {
                break;
            }

            int parent = glyphPos + chain;
            if ((uint)parent >= (uint)len)
            {
                break;
            }

            glyphPos = parent;
            offset = SaturateAdd(offset, horizontal ? pos[glyphPos].YOffset : pos[glyphPos].XOffset);
        }

        return offset;
    }

    /// <summary>Resolves the attachment chains into offsets, as HarfBuzz's position_finish_offsets.</summary>
    public static void PropagateAttachmentOffsets(LayoutBuffer buffer)
    {
        if ((buffer.ScratchFlags & LayoutBuffer.ScratchHasGposAttachment) == 0)
        {
            return;
        }

        GlyphPos[] pos = buffer.Pos;
        int len = buffer.Len;
        LayoutDirection direction = buffer.Direction;
        if (LayoutBuffer.IsForward(direction))
        {
            for (int i = 0; i < len; i++)
            {
                if (pos[i].AttachChain != 0)
                {
                    Propagate(pos, len, i, direction, MaxNestingLevel);
                }
            }
        }
        else
        {
            for (int i = len - 1; i >= 0; i--)
            {
                if (pos[i].AttachChain != 0)
                {
                    Propagate(pos, len, i, direction, MaxNestingLevel);
                }
            }
        }
    }

    private static void Propagate(GlyphPos[] pos, int len, int i, LayoutDirection direction, int nesting)
    {
        int chain = pos[i].AttachChain;
        int type = pos[i].AttachType;
        pos[i].AttachChain = 0;
        int j = i + chain;
        if ((uint)j >= (uint)len || nesting == 0)
        {
            return;
        }

        if (pos[j].AttachChain != 0)
        {
            Propagate(pos, len, j, direction, nesting - 1);
        }

        bool horizontal = IsHorizontalDirection(direction);
        if ((type & AttachTypeCursive) != 0)
        {
            if (horizontal)
            {
                pos[i].YOffset = SaturateAdd(pos[i].YOffset, pos[j].YOffset);
            }
            else
            {
                pos[i].XOffset = SaturateAdd(pos[i].XOffset, pos[j].XOffset);
            }

            return;
        }

        if (horizontal)
        {
            pos[i].XOffset = SaturateAdd(pos[i].XOffset, pos[j].XOffset);
        }
        else
        {
            pos[i].YOffset = SaturateAdd(pos[i].YOffset, pos[j].YOffset);
        }

        bool forward = LayoutBuffer.IsForward(direction);
        if (j < i)
        {
            if (forward)
            {
                for (int k = j; k < i; k++)
                {
                    pos[i].XOffset = SaturateSub(pos[i].XOffset, pos[k].XAdvance);
                    pos[i].YOffset = SaturateSub(pos[i].YOffset, pos[k].YAdvance);
                }
            }
            else
            {
                for (int k = j + 1; k < i + 1; k++)
                {
                    pos[i].XOffset = SaturateAdd(pos[i].XOffset, pos[k].XAdvance);
                    pos[i].YOffset = SaturateAdd(pos[i].YOffset, pos[k].YAdvance);
                }
            }
        }
        else
        {
            if (forward)
            {
                for (int k = i; k < j; k++)
                {
                    pos[i].XOffset = SaturateAdd(pos[i].XOffset, pos[k].XAdvance);
                    pos[i].YOffset = SaturateAdd(pos[i].YOffset, pos[k].YAdvance);
                }
            }
            else
            {
                for (int k = i + 1; k < j + 1; k++)
                {
                    pos[i].XOffset = SaturateSub(pos[i].XOffset, pos[k].XAdvance);
                    pos[i].YOffset = SaturateSub(pos[i].YOffset, pos[k].YAdvance);
                }
            }
        }
    }

    public static int RoundF(float v) => (int)MathF.Floor(v + .5f);

    public static int SaturateAdd(int a, int b) => (int)Math.Clamp((long)a + b, int.MinValue, int.MaxValue);

    public static int SaturateSub(int a, int b) => (int)Math.Clamp((long)a - b, int.MinValue, int.MaxValue);

    public static int SaturateNeg(int a) => a == int.MinValue ? int.MaxValue : -a;
}
