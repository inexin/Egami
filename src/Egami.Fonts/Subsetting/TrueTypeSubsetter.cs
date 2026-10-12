using System.Buffers.Binary;

using Egami.Fonts.Parsing;
using Egami.Fonts.Tables;

namespace Egami.Fonts.Subsetting;

/// <summary>Subsets a TrueType face: composite closure, glyf with component ids renumbered, loca, metrics and the small tables.</summary>
internal static class TrueTypeSubsetter
{
    private const int MoreComponents = 0x0020;

    public static FontSubset Subset(Typeface face, SortedSet<ushort> wanted, SubsetOptions options)
    {
        GlyfTable glyf = face.Glyf!;
        var kept = new SortedSet<ushort>(wanted);
        var pending = new Stack<ushort>(wanted);
        var components = new List<int>();
        while (pending.Count > 0)
        {
            ushort glyph = pending.Pop();
            components.Clear();
            glyf.GetComponents(glyph, components);
            foreach (int c in components)
            {
                if (c < face.GlyphCount && kept.Add((ushort)c))
                {
                    pending.Push((ushort)c);
                }
            }
        }

        (ushort[] oldIds, Dictionary<ushort, ushort> map) = FontSubsetter.Number(kept, options.RetainGlyphIds);

        // glyf and loca.
        var glyphs = new ByteWriter();
        var offsets = new uint[oldIds.Length + 1];
        for (int i = 0; i < oldIds.Length; i++)
        {
            offsets[i] = (uint)glyphs.Length;
            ushort old = oldIds[i];
            if (options.RetainGlyphIds && !map.ContainsKey(old))
            {
                continue;
            }

            ReadOnlySpan<byte> data = glyf.GetGlyphData(old, out long fileOffset);
            if (data.IsEmpty)
            {
                continue;
            }

            if (BigEndian.Int16(data, 0) < 0)
            {
                glyphs.Bytes(Renumber(data, map, fileOffset));
            }
            else
            {
                glyphs.Bytes(data);
            }

            glyphs.PadTo(4);
        }

        offsets[oldIds.Length] = (uint)glyphs.Length;
        bool shortLoca = glyphs.Length <= 0x1FFFE;
        var loca = new ByteWriter(offsets.Length * (shortLoca ? 2 : 4));
        foreach (uint offset in offsets)
        {
            if (shortLoca)
            {
                loca.U16((int)(offset / 2));
            }
            else
            {
                loca.U32(offset);
            }
        }

        var tables = new SortedDictionary<uint, byte[]>
        {
            [Tags.Glyf.Value] = glyphs.ToArray(),
            [Tags.Loca.Value] = loca.ToArray(),
            [Tags.Head.Value] = SfntWriter.Head(face, shortLoca ? 0 : 1),
            [Tags.Maxp.Value] = SfntWriter.Maxp(face, oldIds.Length, trueType: true),
            [Tags.Post.Value] = SfntWriter.Post(face),
            [Tags.Name.Value] = SfntWriter.Name(face, options.FontNamePrefix),
        };
        (byte[] hmtx, byte[] hhea) = SfntWriter.Metrics(face, oldIds, map, options.RetainGlyphIds);
        tables[Tags.Hmtx.Value] = hmtx;
        tables[Tags.Hhea.Value] = hhea;
        if (options.KeepCmap)
        {
            tables[Tags.Cmap.Value] = SfntWriter.Cmap(SfntWriter.MapCharacters(face, map));
        }

        // Hinting programs stay, so glyph instructions that call them still work in hinting renderers.
        foreach (FontTag tag in (ReadOnlySpan<FontTag>)[Tags.OS2, Tags.Cvt, Tags.Fpgm, Tags.Prep, Tags.Gasp])
        {
            SfntWriter.CopyTable(face, tag, tables);
        }

        foreach (FontTag tag in options.KeepTables)
        {
            if (!tables.ContainsKey(tag.Value))
            {
                SfntWriter.CopyTable(face, tag, tables);
            }
        }

        byte[] font = SfntWriter.Write(SfntDirectory.TrueTypeVersion, tables);
        return new FontSubset(font, SubsetFormat.TrueType, oldIds, map);
    }

    // A copy of a composite glyph with each component's glyph id replaced by its new id.
    private static byte[] Renumber(ReadOnlySpan<byte> data, Dictionary<ushort, ushort> map, long fileOffset)
    {
        byte[] copy = data.ToArray();
        int at = 10;
        int flags;
        do
        {
            if (at + 4 > copy.Length)
            {
                throw FontErrors.Truncated(fileOffset + at);
            }

            flags = BinaryPrimitives.ReadUInt16BigEndian(copy.AsSpan(at));
            ushort old = BinaryPrimitives.ReadUInt16BigEndian(copy.AsSpan(at + 2));
            BinaryPrimitives.WriteUInt16BigEndian(copy.AsSpan(at + 2), map[old]);
            int size = (flags & 0x0001) != 0 ? 4 : 2;
            size += (flags & 0x0008) != 0 ? 2 : (flags & 0x0040) != 0 ? 4 : (flags & 0x0080) != 0 ? 8 : 0;
            at += 4 + size;
        }
        while ((flags & MoreComponents) != 0);

        return copy;
    }
}
