using System.Buffers.Binary;

using Egami.Fonts.Parsing;
using Egami.Fonts.Tables;

namespace Egami.Fonts.Subsetting;

/// <summary>Subsets a TrueType face: composite closure, glyf with component ids renumbered, loca, metrics and the small tables.</summary>
internal static class TrueTypeSubsetter
{
    private const int MoreComponents = 0x0020;
    private const int WeHaveInstructions = 0x0100;

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

        // glyf and loca; at a variation position each glyph is written varied, with its rounded metrics.
        Variations.VariationInstance? instance = face.Instance;
        int[]? advances = instance is null ? null : new int[oldIds.Length];
        int[]? bearings = instance is null ? null : new int[oldIds.Length];
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

            if (instance is not null)
            {
                Instancer.StaticGlyph varied = Instancer.Glyph(face, instance, old, map);
                advances![i] = varied.Advance;
                bearings![i] = varied.LeftBearing;
                glyphs.Bytes(options.DropHinting ? WithoutInstructions(varied.Data, 0) : varied.Data);
                glyphs.PadTo(4);
                continue;
            }

            ReadOnlySpan<byte> data = glyf.GetGlyphData(old, out long fileOffset);
            if (data.IsEmpty)
            {
                continue;
            }

            if (BigEndian.Int16(data, 0) < 0)
            {
                byte[] renumbered = Renumber(data, map, fileOffset);
                glyphs.Bytes(options.DropHinting ? WithoutInstructions(renumbered, fileOffset) : renumbered);
            }
            else
            {
                glyphs.Bytes(options.DropHinting ? WithoutInstructions(data, fileOffset) : data);
            }

            glyphs.PadTo(4);
        }

        offsets[oldIds.Length] = (uint)glyphs.Length;
        byte[] glyfBytes = glyphs.ToArray();
        if (instance is not null)
        {
            Instancer.FinishComposites(glyfBytes, offsets);
        }

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
            [Tags.Glyf.Value] = glyfBytes,
            [Tags.Loca.Value] = loca.ToArray(),
            [Tags.Head.Value] = SfntWriter.Head(face, shortLoca ? 0 : 1),
            [Tags.Maxp.Value] = SfntWriter.Maxp(face, oldIds.Length, trueType: true, options.DropHinting),
            [Tags.Post.Value] = instance is null ? SfntWriter.Post(face) : Instancer.Post(face, instance),
            [Tags.Name.Value] = SfntWriter.Name(face, options.FontNamePrefix),
        };
        (byte[] hmtx, byte[] hhea) = SfntWriter.Metrics(face, oldIds, map, options.RetainGlyphIds, advances, bearings);
        tables[Tags.Hmtx.Value] = hmtx;
        tables[Tags.Hhea.Value] = hhea;
        if (options.KeepCmap)
        {
            tables[Tags.Cmap.Value] = SfntWriter.Cmap(SfntWriter.MapCharacters(face, map), face.Cmap.Variations, map);
        }

        // Hinting programs stay unless hinting is dropped, so glyph instructions that call them still work in hinting renderers.
        foreach (FontTag tag in options.DropHinting ? (ReadOnlySpan<FontTag>)[Tags.OS2, Tags.Gasp] : [Tags.OS2, Tags.Cvt, Tags.Fpgm, Tags.Prep, Tags.Gasp])
        {
            SfntWriter.CopyTable(face, tag, tables);
        }

        if (instance is not null && Instancer.Os2(face, instance) is byte[] os2)
        {
            tables[Tags.OS2.Value] = os2;
        }

        SfntWriter.KeepTables(face, map, options, tables);
        if (options.DropHinting)
        {
            // KeepTables may name them; dropped hinting drops them all the same.
            tables.Remove(Tags.Cvt.Value);
            tables.Remove(Tags.Fpgm.Value);
            tables.Remove(Tags.Prep.Value);
        }

        byte[] font = SfntWriter.Write(SfntDirectory.TrueTypeVersion, tables);
        return new FontSubset(font, SubsetFormat.TrueType, oldIds, map);
    }

    /// <summary>
    /// A glyph without its instructions: a simple glyph's instruction length set to 0 and its bytes left out; a composite's
    /// instructions after the last component left out, with WE_HAVE_INSTRUCTIONS cleared on every component.
    /// </summary>
    internal static byte[] WithoutInstructions(ReadOnlySpan<byte> data, long fileOffset)
    {
        if (data.Length < 10)
        {
            return data.ToArray();
        }

        int contours = BinaryPrimitives.ReadInt16BigEndian(data);
        if (contours >= 0)
        {
            int at = 10 + (2 * contours);
            if (at + 2 > data.Length)
            {
                throw FontErrors.Truncated(fileOffset + at);
            }

            int length = BinaryPrimitives.ReadUInt16BigEndian(data[at..]);
            if (at + 2 + length > data.Length)
            {
                throw FontErrors.Truncated(fileOffset + at + 2);
            }

            byte[] simple = new byte[data.Length - length];
            data[..at].CopyTo(simple);
            data[(at + 2 + length)..].CopyTo(simple.AsSpan(at + 2));
            return simple;
        }

        byte[] copy = data.ToArray();
        int position = 10;
        int flags;
        do
        {
            if (position + 4 > copy.Length)
            {
                throw FontErrors.Truncated(fileOffset + position);
            }

            flags = BinaryPrimitives.ReadUInt16BigEndian(copy.AsSpan(position));
            BinaryPrimitives.WriteUInt16BigEndian(copy.AsSpan(position), (ushort)(flags & ~WeHaveInstructions));
            int size = (flags & 0x0001) != 0 ? 4 : 2;
            size += (flags & 0x0008) != 0 ? 2 : (flags & 0x0040) != 0 ? 4 : (flags & 0x0080) != 0 ? 8 : 0;
            position += 4 + size;
        }
        while ((flags & MoreComponents) != 0);

        return position >= copy.Length ? copy : copy[..position];
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
            // A component the face does not have stays out of range in the subset too, so it still draws nothing.
            BinaryPrimitives.WriteUInt16BigEndian(copy.AsSpan(at + 2), map.TryGetValue(old, out ushort now) ? now : (ushort)0xFFFF);
            int size = (flags & 0x0001) != 0 ? 4 : 2;
            size += (flags & 0x0008) != 0 ? 2 : (flags & 0x0040) != 0 ? 4 : (flags & 0x0080) != 0 ? 8 : 0;
            at += 4 + size;
        }
        while ((flags & MoreComponents) != 0);

        return copy;
    }
}
