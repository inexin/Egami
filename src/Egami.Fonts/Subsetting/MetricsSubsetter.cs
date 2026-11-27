// The kern subsetting follows fontTools' subset module (MIT) and the shaper's reading of the table (after HarfBuzz's hb-kern.hh;
// see THIRD-PARTY-NOTICES.md); vmtx, vhea and VORG follow the OpenType specification.
using Egami.Fonts.Shaping;

using static Egami.Fonts.Shaping.OtData;

namespace Egami.Fonts.Subsetting;

/// <summary>
/// The legacy kern table and the vertical metrics (vhea, vmtx, VORG) of a <see cref="SubsetOptions.LayoutClosure"/> subset, cut
/// down to the kept glyphs and renumbered, so the subset kerns and sets vertical text as the full face does.
/// </summary>
internal static class MetricsSubsetter
{
    private static readonly FontTag s_vhea = new("vhea");
    private static readonly FontTag s_vmtx = new("vmtx");
    private static readonly FontTag s_vorg = new("VORG");

    public static void Write(Typeface face, Dictionary<ushort, ushort> map, bool retain, SortedDictionary<uint, byte[]> tables)
    {
        if (face.TryGetTable(Tags.Kern, out ReadOnlyMemory<byte> kern) && Kern(kern.Span, map, retain) is byte[] written)
        {
            tables[Tags.Kern.Value] = written;
        }

        Vertical(face, map, retain, tables);
    }

    /// <summary>
    /// The kern table with each format 0 subtable's pairs of kept glyphs, renumbered and sorted (only pairs the shaper's binary
    /// search finds, so an unsorted table kerns as before); other formats stay only while glyph ids do. Null when nothing is left.
    /// </summary>
    public static byte[]? Kern(ReadOnlySpan<byte> d, Dictionary<ushort, ushort> map, bool retain)
    {
        if (d.Length < 4)
        {
            return null;
        }

        bool apple = U16(d, 0) != 0;
        if (apple && U32(d, 0) != 0x00010000)
        {
            return null;
        }

        int headerSize = apple ? 8 : 6;
        long count = apple ? U32(d, 4) : U16(d, 2);
        int at = apple ? 8 : 4;
        List<byte[]> subtables = [];
        for (long i = 0; i < count && at + headerSize <= d.Length; i++)
        {
            long length = apple ? U32(d, at) : U16(d, at + 2);
            int format = apple ? d[at + 5] : d[at + 4];
            int end = (int)Math.Min(d.Length, at + Math.Max(length, headerSize));
            if (format == 0)
            {
                if (Format0(d, at, headerSize, map, apple) is byte[] pairs)
                {
                    subtables.Add(pairs);
                }
            }
            else if (retain)
            {
                subtables.Add(d[at..end].ToArray());
            }

            if (length < headerSize || length > int.MaxValue)
            {
                break;
            }

            at += (int)length;
        }

        if (subtables.Count == 0)
        {
            return null;
        }

        var w = new ByteWriter();
        if (apple)
        {
            w.U32(0x00010000);
            w.U32((uint)subtables.Count);
        }
        else
        {
            w.U16(0);
            w.U16(subtables.Count);
        }

        subtables.ForEach(s => w.Bytes(s));
        return w.ToArray();
    }

    private static byte[]? Format0(ReadOnlySpan<byte> d, int subtable, int headerSize, Dictionary<ushort, ushort> map, bool apple)
    {
        int body = subtable + headerSize;
        if (body + 8 > d.Length)
        {
            return null;
        }

        int count = U16(d, body);
        List<(uint Key, short Value)> pairs = [];
        for (int i = 0; i < count && body + 8 + (i * 6) + 6 <= d.Length; i++)
        {
            int r = body + 8 + (i * 6);
            uint key = U32(d, r);
            if (map.TryGetValue((ushort)(key >> 16), out ushort left) && map.TryGetValue((ushort)key, out ushort right) && Found(d, body, count, key) == r)
            {
                pairs.Add((((uint)left << 16) | right, (short)S16(d, r + 4)));
            }
        }

        if (pairs.Count == 0)
        {
            return null;
        }

        pairs.Sort((a, b) => a.Key.CompareTo(b.Key));
        int entrySelector = 0;
        while ((2 << entrySelector) <= pairs.Count)
        {
            entrySelector++;
        }

        int searchRange = (1 << entrySelector) * 6;
        int length = headerSize + 8 + (pairs.Count * 6);
        var w = new ByteWriter(length);
        if (apple)
        {
            w.U32((uint)length);
            w.Bytes(d.Slice(subtable + 4, 4));
        }
        else
        {
            // The length field is 16 bits; as in fonts with more pairs, the shaper reads the pairs by their count.
            w.U16(U16(d, subtable));
            w.U16(length & 0xFFFF);
            w.Bytes(d.Slice(subtable + 4, 2));
        }

        w.U16(pairs.Count);
        w.U16(searchRange);
        w.U16(entrySelector);
        w.U16((pairs.Count * 6) - searchRange);
        foreach ((uint key, short value) in pairs)
        {
            w.U32(key);
            w.U16(value);
        }

        return w.ToArray();
    }

    /// <summary>The record the shaper's binary search finds for a key, or -1.</summary>
    private static int Found(ReadOnlySpan<byte> d, int body, int count, uint key)
    {
        int lo = 0, hi = count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            int r = body + 8 + (mid * 6);
            uint k = r + 4 <= d.Length ? U32(d, r) : 0;
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
                return r;
            }
        }

        return -1;
    }

    /// <summary>
    /// vhea and vmtx with a long metric for every glyph, and VORG with the kept glyphs' origins, as the shaper reads them; at a
    /// variation position with the varied advances and origins, rounded.
    /// </summary>
    private static void Vertical(Typeface face, Dictionary<ushort, ushort> map, bool retain, SortedDictionary<uint, byte[]> tables)
    {
        VerticalTables vertical = VerticalTables.For(face);
        if (!vertical.HasVmtx && !vertical.HasVorg)
        {
            return;
        }

        int glyphCount = retain ? (map.Count == 0 ? 0 : map.Keys.Max() + 1) : map.Count;
        var oldOf = new int[glyphCount];
        Array.Fill(oldOf, -1);
        foreach ((ushort old, ushort now) in map)
        {
            oldOf[now] = old;
        }

        bool varied = face.IsVariable;
        ShapeFont? font = varied ? new ShapeFont(face) : null;
        Func<int, int?> yMax = StaticYMax(tables);
        if (vertical.HasVmtx && face.TryGetTable(s_vhea, out ReadOnlyMemory<byte> vhea) && vhea.Length >= 36)
        {
            var vmtx = new ByteWriter(glyphCount * 4);
            int advanceMax = 0;
            for (int now = 0; now < glyphCount; now++)
            {
                int old = oldOf[now];
                int advance = 0, bearing = 0;
                if (old >= 0)
                {
                    advance = vertical.GetAdvance((uint)old);
                    bearing = vertical.GetTopBearing((uint)old);
                    if (font is not null)
                    {
                        advance = -font.GetVAdvance((uint)old);
                        if (!vertical.HasVorg && face.Glyf is not null)
                        {
                            // The shaper's vertical origin is the top phantom point, the glyph's top plus its top bearing.
                            font.GetVOrigin((uint)old, out _, out int origin);
                            bearing = origin - (yMax(now) ?? 0);
                        }
                    }
                }

                vmtx.U16(advance);
                vmtx.U16(bearing);
                advanceMax = Math.Max(advanceMax, advance);
            }

            byte[] header = vhea[..36].ToArray();
            header[10] = (byte)(advanceMax >> 8);
            header[11] = (byte)advanceMax;
            header[34] = (byte)(glyphCount >> 8);
            header[35] = (byte)glyphCount;
            tables[s_vhea.Value] = header;
            tables[s_vmtx.Value] = vmtx.ToArray();
        }

        if (vertical.HasVorg && face.TryGetTable(s_vorg, out ReadOnlyMemory<byte> vorg))
        {
            int defaultOrigin = S16(vorg.Span, 4);
            List<(int Glyph, int Origin)> records = [];
            for (int now = 0; now < glyphCount; now++)
            {
                if (oldOf[now] < 0)
                {
                    continue;
                }

                int origin = vertical.GetYOrigin((uint)oldOf[now]);
                if (font is not null)
                {
                    font.GetVOrigin((uint)oldOf[now], out _, out origin);
                }

                if (origin != defaultOrigin)
                {
                    records.Add((now, origin));
                }
            }

            var w = new ByteWriter(8 + (records.Count * 4));
            w.U16(1);
            w.U16(0);
            w.U16(defaultOrigin);
            w.U16(records.Count);
            foreach ((int glyph, int origin) in records)
            {
                w.U16(glyph);
                w.U16(origin);
            }

            tables[s_vorg.Value] = w.ToArray();
        }
    }

    /// <summary>The top of each written glyph (its header's yMax), by new id; null for an empty glyph or without glyf.</summary>
    private static Func<int, int?> StaticYMax(SortedDictionary<uint, byte[]> tables)
    {
        if (!tables.TryGetValue(Tags.Glyf.Value, out byte[]? glyf) || !tables.TryGetValue(Tags.Loca.Value, out byte[]? loca) ||
            !tables.TryGetValue(Tags.Head.Value, out byte[]? head) || head.Length < 54)
        {
            return _ => null;
        }

        bool longLoca = U16(head, 50) != 0;
        return glyph =>
        {
            int entry = longLoca ? 4 : 2;
            if ((glyph + 2) * entry > loca.Length)
            {
                return null;
            }

            int start = longLoca ? (int)U32(loca, glyph * 4) : U16(loca, glyph * 2) * 2;
            int end = longLoca ? (int)U32(loca, (glyph + 1) * 4) : U16(loca, (glyph + 1) * 2) * 2;
            return end - start >= 10 ? Math.Max(S16(glyf, start + 4), S16(glyf, start + 8)) : null;
        };
    }
}
