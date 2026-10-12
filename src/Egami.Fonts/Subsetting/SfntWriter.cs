using System.Buffers.Binary;
using System.Text;

namespace Egami.Fonts.Subsetting;

/// <summary>A growable big-endian byte buffer.</summary>
internal sealed class ByteWriter
{
    private byte[] _buffer;

    public ByteWriter(int capacity = 256)
    {
        _buffer = new byte[Math.Max(16, capacity)];
    }

    public int Length { get; private set; }

    public ReadOnlySpan<byte> Written => _buffer.AsSpan(0, Length);

    public byte[] ToArray() => _buffer.AsSpan(0, Length).ToArray();

    public void U8(int value)
    {
        Ensure(1);
        _buffer[Length++] = (byte)value;
    }

    public void U16(int value)
    {
        Ensure(2);
        BinaryPrimitives.WriteUInt16BigEndian(_buffer.AsSpan(Length), (ushort)value);
        Length += 2;
    }

    public void U24(int value)
    {
        U8(value >> 16);
        U16(value & 0xFFFF);
    }

    public void U32(uint value)
    {
        Ensure(4);
        BinaryPrimitives.WriteUInt32BigEndian(_buffer.AsSpan(Length), value);
        Length += 4;
    }

    public void Bytes(ReadOnlySpan<byte> bytes)
    {
        Ensure(bytes.Length);
        bytes.CopyTo(_buffer.AsSpan(Length));
        Length += bytes.Length;
    }

    public void PadTo(int alignment)
    {
        while (Length % alignment != 0)
        {
            U8(0);
        }
    }

    public void PatchU8(int at, int value) => _buffer[at] = (byte)value;

    public void PatchU16(int at, int value) => BinaryPrimitives.WriteUInt16BigEndian(_buffer.AsSpan(at), (ushort)value);

    public void PatchU32(int at, uint value) => BinaryPrimitives.WriteUInt32BigEndian(_buffer.AsSpan(at), value);

    private void Ensure(int extra)
    {
        if (Length + extra > _buffer.Length)
        {
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, Length + extra));
        }
    }
}

/// <summary>Writes an sfnt from tables: a sorted directory, 4-byte aligned tables, checksums and head's checksum adjustment.</summary>
internal static class SfntWriter
{
    private static readonly FontTag[] LayoutTables = [Tags.Gsub, Tags.Gpos, Tags.Gdef];

    public static byte[] Write(uint version, SortedDictionary<uint, byte[]> tables)
    {
        int count = tables.Count;
        int entrySelector = 0;
        while ((2 << entrySelector) <= count)
        {
            entrySelector++;
        }

        int searchRange = (1 << entrySelector) * 16;
        var w = new ByteWriter(12 + (count * 16) + tables.Values.Sum(t => t.Length + 3));
        w.U32(version);
        w.U16(count);
        w.U16(searchRange);
        w.U16(entrySelector);
        w.U16((count * 16) - searchRange);
        int directory = w.Length;
        for (int i = 0; i < count; i++)
        {
            w.U32(0);
            w.U32(0);
            w.U32(0);
            w.U32(0);
        }

        int headOffset = -1;
        int index = 0;
        foreach ((uint tag, byte[] data) in tables)
        {
            int offset = w.Length;
            if (tag == Tags.Head.Value)
            {
                headOffset = offset;
                BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), 0);
            }

            w.Bytes(data);
            w.PadTo(4);
            int at = directory + (index * 16);
            w.PatchU32(at, tag);
            w.PatchU32(at + 4, Checksum(data));
            w.PatchU32(at + 8, (uint)offset);
            w.PatchU32(at + 12, (uint)data.Length);
            index++;
        }

        byte[] font = w.ToArray();
        if (headOffset >= 0)
        {
            BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(headOffset + 8), 0xB1B0AFBA - Checksum(font));
        }

        return font;
    }

    public static uint Checksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        int i = 0;
        for (; i + 4 <= data.Length; i += 4)
        {
            sum += BinaryPrimitives.ReadUInt32BigEndian(data[i..]);
        }

        if (i < data.Length)
        {
            Span<byte> last = stackalloc byte[4];
            data[i..].CopyTo(last);
            sum += BinaryPrimitives.ReadUInt32BigEndian(last);
        }

        return sum;
    }

    /// <summary>A post table version 3: the header only, no glyph names.</summary>
    public static byte[] Post(Typeface face)
    {
        var w = new ByteWriter(32);
        w.U32(0x00030000);
        w.U32((uint)(int)Math.Round(face.Post.ItalicAngle * 65536));
        w.U16(face.Post.UnderlinePosition);
        w.U16(face.Post.UnderlineThickness);
        w.U32(face.Post.IsFixedPitch);
        w.U32(0);
        w.U32(0);
        w.U32(0);
        w.U32(0);
        return w.ToArray();
    }

    /// <summary>A minimal name table: IDs 1 to 6 for the Windows platform in US English, the prefix on the PostScript name.</summary>
    public static byte[] Name(Typeface face, string? prefix)
    {
        string family = face.Name.GetEnglish(1) ?? face.FamilyName;
        string subfamily = face.Instance is null ? face.Name.GetEnglish(2) ?? face.SubfamilyName : face.SubfamilyName;
        string[] values =
        [
            face.Name.GetEnglish(0) ?? string.Empty,
            family,
            subfamily,
            prefix + (face.Name.GetEnglish(3) ?? face.PostScriptName),
            face.FullName,
            face.Name.GetEnglish(5) ?? "Version 1.0",
            prefix + face.PostScriptName,
        ];
        var records = new List<(int Id, byte[] Text)>();
        for (int id = 0; id < values.Length; id++)
        {
            if (id == 0 && values[id].Length == 0)
            {
                continue;
            }

            records.Add((id, Encoding.BigEndianUnicode.GetBytes(values[id])));
        }

        var w = new ByteWriter();
        w.U16(0);
        w.U16(records.Count);
        w.U16(6 + (records.Count * 12));
        int offset = 0;
        foreach ((int id, byte[] text) in records)
        {
            w.U16(3);
            w.U16(1);
            w.U16(0x409);
            w.U16(id);
            w.U16(text.Length);
            w.U16(offset);
            offset += text.Length;
        }

        foreach ((_, byte[] text) in records)
        {
            w.Bytes(text);
        }

        return w.ToArray();
    }

    /// <summary>A cmap of format 4 for the BMP and, when needed, format 12 for every code point.</summary>
    public static byte[] Cmap(SortedDictionary<int, ushort> map)
    {
        bool needsFull = map.Count > 0 && map.Keys.Max() > 0xFFFF;
        byte[] format4 = Format4(map);
        byte[]? format12 = needsFull ? Format12(map) : null;
        var w = new ByteWriter();
        w.U16(0);
        int tables = needsFull ? 2 : 1;
        w.U16(tables);
        int offset = 4 + (tables * 8);
        w.U16(3);
        w.U16(1);
        w.U32((uint)offset);
        if (format12 is not null)
        {
            w.U16(3);
            w.U16(10);
            w.U32((uint)(offset + format4.Length));
        }

        w.Bytes(format4);
        if (format12 is not null)
        {
            w.Bytes(format12);
        }

        return w.ToArray();
    }

    private static byte[] Format4(SortedDictionary<int, ushort> map)
    {
        // One segment per run of consecutive code points whose glyphs are consecutive too, then the 0xFFFF end segment.
        var segments = new List<(int Start, int End, int Delta)>();
        foreach ((int cp, ushort glyph) in map)
        {
            if (cp > 0xFFFE)
            {
                break;
            }

            if (segments.Count > 0)
            {
                (int s, int e, int d) = segments[^1];
                if (cp == e + 1 && ((cp + d) & 0xFFFF) == glyph)
                {
                    segments[^1] = (s, cp, d);
                    continue;
                }
            }

            segments.Add((cp, cp, (glyph - cp) & 0xFFFF));
        }

        segments.Add((0xFFFF, 0xFFFF, 1));
        int n = segments.Count;
        int entrySelector = 0;
        while ((2 << entrySelector) <= n)
        {
            entrySelector++;
        }

        int searchRange = 2 << entrySelector;
        var w = new ByteWriter(16 + (n * 8));
        w.U16(4);
        w.U16(16 + (n * 8));
        w.U16(0);
        w.U16(n * 2);
        w.U16(searchRange);
        w.U16(entrySelector);
        w.U16((n * 2) - searchRange);
        foreach (var s in segments)
        {
            w.U16(s.End);
        }

        w.U16(0);
        foreach (var s in segments)
        {
            w.U16(s.Start);
        }

        foreach (var s in segments)
        {
            w.U16(s.Delta);
        }

        foreach (var _ in segments)
        {
            w.U16(0);
        }

        return w.ToArray();
    }

    private static byte[] Format12(SortedDictionary<int, ushort> map)
    {
        var groups = new List<(int Start, int End, int Glyph)>();
        foreach ((int cp, ushort glyph) in map)
        {
            if (groups.Count > 0)
            {
                (int s, int e, int g) = groups[^1];
                if (cp == e + 1 && glyph == g + (cp - s))
                {
                    groups[^1] = (s, cp, g);
                    continue;
                }
            }

            groups.Add((cp, cp, glyph));
        }

        var w = new ByteWriter(16 + (groups.Count * 12));
        w.U16(12);
        w.U16(0);
        w.U32((uint)(16 + (groups.Count * 12)));
        w.U32(0);
        w.U32((uint)groups.Count);
        foreach (var g in groups)
        {
            w.U32((uint)g.Start);
            w.U32((uint)g.End);
            w.U32((uint)g.Glyph);
        }

        return w.ToArray();
    }

    /// <summary>Maps the kept code points of the face to new glyph ids.</summary>
    public static SortedDictionary<int, ushort> MapCharacters(Typeface face, Dictionary<ushort, ushort> glyphMap)
    {
        var map = new SortedDictionary<int, ushort>();
        face.Cmap.Enumerate((cp, glyph) =>
        {
            if (glyph != 0 && glyphMap.TryGetValue((ushort)glyph, out ushort mapped))
            {
                map[cp] = mapped;
            }
        });
        return map;
    }

    /// <summary>
    /// hmtx for the new glyphs, with every glyph's advance written, and the matching hhea; an instance passes its rounded
    /// <paramref name="advances"/> and <paramref name="bearings"/> by new glyph id.
    /// </summary>
    public static (byte[] Hmtx, byte[] Hhea) Metrics(Typeface face, ushort[] oldIds, Dictionary<ushort, ushort> map, bool retain, int[]? advances = null, int[]? bearings = null)
    {
        var w = new ByteWriter(oldIds.Length * 4);
        int advanceMax = 0;
        for (int i = 0; i < oldIds.Length; i++)
        {
            ushort old = oldIds[i];
            bool kept = !retain || map.ContainsKey(old);
            int advance = !kept ? 0 : advances is not null ? advances[i] : (int)Math.Round(face.GetAdvance(old));
            int lsb = !kept ? 0 : bearings is not null ? bearings[i] : (int)Math.Round(face.GetLeftSideBearing(old));
            w.U16(advance);
            w.U16(lsb);
            advanceMax = Math.Max(advanceMax, advance);
        }

        Tables.HheaTable? h = face.Hhea;
        FontExtents e = face.Extents;
        var hhea = new ByteWriter(36);
        hhea.U32(0x00010000);
        hhea.U16(h?.Ascender ?? (int)e.Ascent);
        hhea.U16(h?.Descender ?? (int)e.Descent);
        hhea.U16(h?.LineGap ?? (int)e.LineGap);
        hhea.U16(advanceMax);
        hhea.U16(h?.MinLeftSideBearing ?? 0);
        hhea.U16(h?.MinRightSideBearing ?? 0);
        hhea.U16(h?.XMaxExtent ?? 0);
        hhea.U16(h?.CaretSlopeRise ?? 1);
        hhea.U16(h?.CaretSlopeRun ?? 0);
        hhea.U16(h?.CaretOffset ?? 0);
        hhea.U32(0);
        hhea.U32(0);
        hhea.U16(0);
        hhea.U16(oldIds.Length);
        return (w.ToArray(), hhea.ToArray());
    }

    /// <summary>The face's head table with its loca format changed; the checksum adjustment is set when the font is written.</summary>
    public static byte[] Head(Typeface face, int indexToLocFormat)
    {
        byte[] head;
        if (face.TryGetTable(Tags.Head, out ReadOnlyMemory<byte> original) && original.Length >= Tables.HeadTable.Size)
        {
            head = original[..Tables.HeadTable.Size].ToArray();
        }
        else
        {
            var w = new ByteWriter(Tables.HeadTable.Size);
            w.U32(0x00010000);
            w.U32(0x00010000);
            w.U32(0);
            w.U32(0x5F0F3CF5);
            w.U16(0x000B);
            w.U16(face.UnitsPerEm);
            w.U32(0);
            w.U32(0);
            w.U32(0);
            w.U32(0);
            w.U16(face.Head.XMin);
            w.U16(face.Head.YMin);
            w.U16(face.Head.XMax);
            w.U16(face.Head.YMax);
            w.U16(face.Head.MacStyle);
            w.U16(8);
            w.U16(2);
            w.U16(0);
            w.U16(0);
            head = w.ToArray();
        }

        BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(18), (ushort)face.UnitsPerEm);
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(50), (short)indexToLocFormat);
        return head;
    }

    /// <summary>maxp with the new glyph count: version 1.0 fields kept for TrueType, version 0.5 for CFF.</summary>
    public static byte[] Maxp(Typeface face, int glyphCount, bool trueType)
    {
        var w = new ByteWriter(32);
        byte[] fields = face.Maxp?.TrueTypeFields ?? [];
        if (trueType)
        {
            w.U32(0x00010000);
            w.U16(glyphCount);
            if (fields.Length == 26)
            {
                w.Bytes(fields);
            }
            else
            {
                for (int i = 0; i < 13; i++)
                {
                    w.U16(i is 4 or 5 ? 1 : 0);
                }
            }
        }
        else
        {
            w.U32(0x00005000);
            w.U16(glyphCount);
        }

        return w.ToArray();
    }

    /// <summary>Copies the tables the options keep, and the layout tables for a layout closure, except the variation tables a variable face's subset drops.</summary>
    public static void KeepTables(Typeface face, SubsetOptions options, SortedDictionary<uint, byte[]> tables)
    {
        IEnumerable<FontTag> keep = options.LayoutClosure ? options.KeepTables.Concat(LayoutTables) : options.KeepTables;
        foreach (FontTag tag in keep)
        {
            if (!tables.ContainsKey(tag.Value) && !(face.IsVariable && Instancer.VariationTables.Contains(tag)))
            {
                CopyTable(face, tag, tables);
            }
        }
    }

    public static void CopyTable(Typeface face, FontTag tag, SortedDictionary<uint, byte[]> into)
    {
        if (face.TryGetTable(tag, out ReadOnlyMemory<byte> data))
        {
            into[tag.Value] = data.ToArray();
        }
    }
}
