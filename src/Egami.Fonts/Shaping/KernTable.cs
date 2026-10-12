// The application follows HarfBuzz's hb-kern.hh and kern table handling (MIT); see THIRD-PARTY-NOTICES.md.
using static Egami.Fonts.Shaping.OtData;

namespace Egami.Fonts.Shaping;

/// <summary>The legacy kern table (the OpenType and Apple versions), formats 0 and 2, for fonts without GPOS kerning.</summary>
internal sealed class KernTable
{
    public static readonly KernTable Empty = new(ReadOnlyMemory<byte>.Empty);

    private readonly ReadOnlyMemory<byte> _data;
    private readonly List<(int Offset, int Format, bool Horizontal, bool CrossStream, bool Variation)> _subtables = [];

    public KernTable(ReadOnlyMemory<byte> data)
    {
        _data = data;
        ReadOnlySpan<byte> d = data.Span;
        if (d.Length < 4)
        {
            return;
        }

        if (U16(d, 0) == 0)
        {
            // OpenType: u16 version, u16 count; subtables of u16 version, u16 length, u8 format, u8 coverage.
            int count = U16(d, 2);
            int at = 4;
            for (int i = 0; i < count && at + 6 <= d.Length; i++)
            {
                int length = U16(d, at + 2);
                int format = d[at + 4];
                int coverage = d[at + 5];
                _subtables.Add((at, format, (coverage & 0x01) != 0, (coverage & 0x04) != 0, false));
                if (length < 6)
                {
                    break;
                }

                at += length;
            }

            HeaderSize = 6;
        }
        else if (U32(d, 0) == 0x00010000)
        {
            // Apple: u32 version, u32 count; subtables of u32 length, u8 coverage, u8 format, u16 tuple index.
            uint count = U32(d, 4);
            int at = 8;
            for (int i = 0; i < count && at + 8 <= d.Length; i++)
            {
                uint length = U32(d, at);
                int coverage = d[at + 4];
                int format = d[at + 5];
                _subtables.Add((at, format, (coverage & 0x80) == 0, (coverage & 0x40) != 0, (coverage & 0x20) != 0));
                if (length < 8 || length > int.MaxValue)
                {
                    break;
                }

                at += (int)length;
            }

            HeaderSize = 8;
        }
    }

    public int HeaderSize { get; }

    public bool HasData => _subtables.Count > 0;

    /// <summary>Whether any subtable kerns across the line (machine kerning), as HarfBuzz's has_cross_kerning.</summary>
    public bool HasCrossKerning => _subtables.Exists(s => s.CrossStream);

    /// <summary>Whether any subtable is a state machine (format 1), as HarfBuzz's has_machine_kerning.</summary>
    public bool HasMachineKerning => _subtables.Exists(s => s.Format == 1);

    /// <summary>Applies every horizontal (or vertical) subtable as HarfBuzz's kern machine does.</summary>
    public void Apply(LayoutBuffer buffer, GdefTable gdef, uint kernMask, ShapeFont font)
    {
        ReadOnlySpan<byte> d = _data.Span;
        bool horizontal = LayoutBuffer.IsHorizontal(buffer.Direction);
        bool seenCrossStream = false;
        foreach ((int offset, int format, bool subHorizontal, bool crossStream, bool variation) in _subtables)
        {
            if (variation || horizontal != subHorizontal || format is not (0 or 2))
            {
                continue;
            }

            if (!seenCrossStream && crossStream)
            {
                seenCrossStream = true;
                for (int i = 0; i < buffer.Len; i++)
                {
                    buffer.Pos[i].AttachType = LayoutApplier.AttachTypeCursive;
                    buffer.Pos[i].AttachChain = (short)(LayoutBuffer.IsForward(buffer.Direction) ? -1 : 1);
                }
            }

            bool reverse = LayoutBuffer.IsBackward(buffer.Direction);
            if (reverse)
            {
                buffer.Reverse();
            }

            Kern(buffer, gdef, kernMask, offset, format, crossStream, font);
            if (reverse)
            {
                buffer.Reverse();
            }
        }
    }

    private void Kern(LayoutBuffer buffer, GdefTable gdef, uint kernMask, int subtable, int format, bool crossStream, ShapeFont font)
    {
        LayoutApplier c = new(LayoutTable.EmptyGpos, gdef, buffer);
        c.SetLookupMask(kernMask);
        c.SetLookupProps(LookupFlag.IgnoreMarks);
        LayoutApplier.SkippingIterator iter = c.IterInput;
        bool horizontal = LayoutBuffer.IsHorizontal(buffer.Direction);
        GlyphInfo[] info = buffer.Info;
        GlyphPos[] pos = buffer.Pos;
        int count = buffer.Len;
        for (int idx = 0; idx < count;)
        {
            if ((info[idx].Mask & kernMask) == 0)
            {
                idx++;
                continue;
            }

            iter.ResetFast(idx);
            if (!iter.Next(out int unsafeTo))
            {
                idx = unsafeTo;
                continue;
            }

            int i = idx;
            int j = iter.Idx;
            int kern = GetKerning(subtable, format, info[i].Codepoint, info[j].Codepoint);
            if (kern != 0)
            {
                if (horizontal)
                {
                    kern = font.EmScaleX(kern);
                    if (crossStream)
                    {
                        pos[j].YOffset = kern;
                        buffer.ScratchFlags |= LayoutBuffer.ScratchHasGposAttachment;
                    }
                    else
                    {
                        int kern1 = kern >> 1;
                        int kern2 = kern - kern1;
                        pos[i].XAdvance = LayoutApplier.SaturateAdd(pos[i].XAdvance, kern1);
                        pos[j].XAdvance = LayoutApplier.SaturateAdd(pos[j].XAdvance, kern2);
                        pos[j].XOffset = LayoutApplier.SaturateAdd(pos[j].XOffset, kern2);
                    }
                }
                else
                {
                    kern = font.EmScaleY(kern);
                    if (crossStream)
                    {
                        pos[j].XOffset = kern;
                        buffer.ScratchFlags |= LayoutBuffer.ScratchHasGposAttachment;
                    }
                    else
                    {
                        int kern1 = kern >> 1;
                        int kern2 = kern - kern1;
                        pos[i].YAdvance = LayoutApplier.SaturateAdd(pos[i].YAdvance, kern1);
                        pos[j].YAdvance = LayoutApplier.SaturateAdd(pos[j].YAdvance, kern2);
                        pos[j].YOffset = LayoutApplier.SaturateAdd(pos[j].YOffset, kern2);
                    }
                }

                buffer.UnsafeToBreak(i, j + 1);
            }

            idx = iter.Idx;
        }
    }

    private int GetKerning(int subtable, int format, uint left, uint right)
    {
        ReadOnlySpan<byte> d = _data.Span;
        int body = subtable + HeaderSize;
        if (format == 0)
        {
            int count = U16(d, body);
            uint key = (left << 16) | right;
            int lo = 0, hi = count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >>> 1;
                int r = body + 8 + (mid * 6);
                uint k = U32(d, r);
                if (key < k)
                {
                    hi = mid - 1;
                }
                else if (key > k)
                {
                    lo = mid + 1;
                }
                else
                {
                    return S16(d, r + 4);
                }
            }

            return 0;
        }

        // Format 2: class tables give byte offsets that add up to the value's offset from the subtable's start.
        int leftClass = ClassValue(d, subtable + U16(d, body + 2), left);
        int rightClass = ClassValue(d, subtable + U16(d, body + 4), right);
        int array = subtable + U16(d, body + 6);
        int at = subtable + leftClass + rightClass;
        if (leftClass == 0 || rightClass == 0 || at < array)
        {
            return 0;
        }

        return S16(d, at);
    }

    private static int ClassValue(ReadOnlySpan<byte> d, int table, uint glyph)
    {
        uint first = U16(d, table);
        int count = U16(d, table + 2);
        uint i = glyph - first;
        return i < (uint)count ? U16(d, table + 4 + (int)(i * 2)) : 0;
    }
}
