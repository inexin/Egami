using System.IO.Compression;

using Egami.Fonts.Parsing;

namespace Egami.Fonts.Woff;

/// <summary>
/// WOFF 2.0 (W3C Recommendation): the header, the table directory with known-tag flags and UIntBase128 lengths, the collection
/// directory, one Brotli stream holding every table (decoded by the BCL's <see cref="BrotliDecoder"/>), the glyf and loca
/// transform and the hmtx transform.
/// </summary>
internal static class Woff2Decoder
{
    private const int HeaderSize = 48;

    // The 63 known tags, by the index the low six bits of a directory entry's flags give.
    private static readonly string[] s_knownTags =
    [
        "cmap", "head", "hhea", "hmtx", "maxp", "name", "OS/2", "post", "cvt ", "fpgm", "glyf", "loca", "prep", "CFF ", "VORG", "EBDT",
        "EBLC", "gasp", "hdmx", "kern", "LTSH", "PCLT", "VDMX", "vhea", "vmtx", "BASE", "GDEF", "GPOS", "GSUB", "EBSC", "JSTF", "MATH",
        "CBDT", "CBLC", "COLR", "CPAL", "SVG ", "sbix", "acnt", "avar", "bdat", "bloc", "bsln", "cvar", "fdsc", "feat", "fmtx", "fvar",
        "gvar", "hsty", "just", "lcar", "mort", "morx", "opbd", "prop", "trak", "Zapf", "Silf", "Glat", "Gloc", "Feat", "Sill",
    ];

    public static DecodedFont Decode(ReadOnlyMemory<byte> memory, long limit, FontLoadOptions options)
    {
        ReadOnlySpan<byte> data = memory.Span;
        var r = new FontReader(data);
        r.Skip(4);
        uint flavor = r.ReadUInt32();
        uint length = r.ReadUInt32();
        int count = r.ReadUInt16();
        r.Skip(2);
        uint totalSfntSize = r.ReadUInt32();
        uint totalCompressedSize = r.ReadUInt32();
        r.Skip(4);
        uint metaOffset = r.ReadUInt32(), metaLength = r.ReadUInt32(), metaOrigLength = r.ReadUInt32();
        uint privOffset = r.ReadUInt32(), privLength = r.ReadUInt32();
        if (length != data.Length)
        {
            Typeface.Report(options, "Font.BadContainer", $"The WOFF2 header gives a length of {length} bytes; the data has {data.Length}.", 8);
        }

        if (count == 0)
        {
            throw FontErrors.Corrupt(12, "The WOFF2 file has no tables.");
        }

        WebFontDecoder.CheckLimit(totalSfntSize, limit);
        var entries = new Entry[count];
        long streamSize = 0;
        for (int i = 0; i < count; i++)
        {
            int at = r.Position;
            int flags = r.ReadByte();
            int known = flags & 0x3F;
            FontTag tag = known == 63 ? r.ReadTag() : new FontTag(s_knownTags[known]);
            int version = flags >> 6;
            uint origLength = ReadBase128(ref r);
            bool isGlyfOrLoca = tag == Tags.Glyf || tag == Tags.Loca;
            bool transformed = isGlyfOrLoca ? version == 0 : version != 0;
            if (isGlyfOrLoca ? version is 1 or 2 : version > (tag == Tags.Hmtx ? 1 : 0))
            {
                throw FontErrors.Corrupt(at, $"The WOFF2 {tag} table has the reserved transform version {version}.");
            }

            uint stored = transformed ? ReadBase128(ref r) : origLength;
            if (tag == Tags.Loca && transformed && stored != 0)
            {
                throw FontErrors.Corrupt(at, "A transformed WOFF2 loca table must have no data of its own.");
            }

            WebFontDecoder.CheckLimit(origLength, limit);
            entries[i] = new Entry(tag, transformed, origLength, stored, (int)streamSize);
            streamSize += stored;
            WebFontDecoder.CheckLimit(streamSize, limit);
        }

        // The collection directory, when the flavor is 'ttcf'.
        uint collectionVersion = 0;
        List<SfntFace> faces;
        if (flavor == SfntDirectory.CollectionTag)
        {
            collectionVersion = r.ReadUInt32();
            int fontCount = Read255UInt16(ref r);
            if (fontCount == 0)
            {
                throw FontErrors.Corrupt(r.Position, "The WOFF2 collection has no fonts.");
            }

            faces = new List<SfntFace>(fontCount);
            for (int f = 0; f < fontCount; f++)
            {
                int tables = Read255UInt16(ref r);
                uint faceFlavor = r.ReadUInt32();
                var list = new (FontTag, int)[tables];
                for (int t = 0; t < tables; t++)
                {
                    int index = Read255UInt16(ref r);
                    if (index >= count)
                    {
                        throw FontErrors.Corrupt(r.Position, $"A WOFF2 collection face names table {index} of {count}.");
                    }

                    list[t] = (entries[index].Tag, index);
                }

                faces.Add(new SfntFace(faceFlavor, list));
            }
        }
        else
        {
            faces = [new SfntFace(flavor, [.. entries.Select((e, i) => (e.Tag, i))])];
        }

        // The Brotli stream holding every table.
        int compressedAt = r.Position;
        if (totalCompressedSize > data.Length - compressedAt)
        {
            throw FontErrors.Corrupt(compressedAt, "The WOFF2 compressed data runs past the end of the file.");
        }

        byte[] stream = Unbrotli(data.Slice(compressedAt, (int)totalCompressedSize), (int)streamSize, compressedAt, "table data");

        // Untransformed tables are slices of the stream; glyf and loca are rebuilt, then hmtx from the rebuilt glyf.
        byte[][] bodies = new byte[count][];
        var glyfBounds = new Dictionary<int, short[]?>();
        for (int i = 0; i < count; i++)
        {
            Entry e = entries[i];
            if (!e.Transformed)
            {
                bodies[i] = stream.AsSpan(e.StreamOffset, (int)e.StoredLength).ToArray();
            }
        }

        foreach (SfntFace face in faces)
        {
            int glyf = Find(face, Tags.Glyf), loca = Find(face, Tags.Loca), hmtx = Find(face, Tags.Hmtx);
            if (glyf >= 0 && entries[glyf].Transformed)
            {
                if (loca < 0 || !entries[loca].Transformed)
                {
                    throw FontErrors.Corrupt(HeaderSize, "A WOFF2 face has a transformed glyf table without a transformed loca table.");
                }

                if (bodies[glyf] is null)
                {
                    Entry e = entries[glyf];
                    Woff2Glyf.Result rebuilt = Woff2Glyf.Rebuild(stream.AsSpan(e.StreamOffset, (int)e.StoredLength), compressedAt, limit);
                    if (rebuilt.Loca.Length != entries[loca].OrigLength)
                    {
                        Typeface.Report(options, "Font.BadContainer", $"The WOFF2 loca table rebuilds to {rebuilt.Loca.Length} bytes; the directory gives {entries[loca].OrigLength}.", HeaderSize);
                    }

                    bodies[glyf] = rebuilt.Glyf;
                    bodies[loca] = rebuilt.Loca;
                    glyfBounds[glyf] = rebuilt.XMin;
                }
            }
            else if (loca >= 0 && entries[loca].Transformed)
            {
                throw FontErrors.Corrupt(HeaderSize, "A WOFF2 face has a transformed loca table without a transformed glyf table.");
            }

            if (hmtx >= 0 && entries[hmtx].Transformed && bodies[hmtx] is null)
            {
                if (glyf < 0 || !glyfBounds.TryGetValue(glyf, out short[]? xMin) || xMin is null)
                {
                    throw FontErrors.Corrupt(HeaderSize, "A WOFF2 face has a transformed hmtx table without a transformed glyf table.");
                }

                Entry e = entries[hmtx];
                bodies[hmtx] = RebuildHmtx(stream.AsSpan(e.StreamOffset, (int)e.StoredLength), face, bodies, xMin, compressedAt + e.StreamOffset);
            }
        }

        for (int i = 0; i < count; i++)
        {
            if (bodies[i] is null)
            {
                throw FontErrors.Corrupt(HeaderSize, $"The WOFF2 {entries[i].Tag} table is transformed but belongs to no face that rebuilds it.");
            }
        }

        byte[]? metadata = null, priv = null;
        if (metaLength > 0)
        {
            if (metaOffset > data.Length || metaLength > data.Length - metaOffset)
            {
                Typeface.Report(options, "Font.BadContainer", "The WOFF2 metadata block lies outside the file; it is left out.", 28);
            }
            else
            {
                WebFontDecoder.CheckLimit(metaOrigLength, limit);
                metadata = Unbrotli(data.Slice((int)metaOffset, (int)metaLength), (int)metaOrigLength, metaOffset, "metadata block");
            }
        }

        if (privLength > 0 && privOffset <= data.Length && privLength <= data.Length - privOffset)
        {
            priv = data.Slice((int)privOffset, (int)privLength).ToArray();
        }

        byte[] sfnt = SfntAssembler.Build(faces, bodies, collectionVersion, limit);
        return new DecodedFont(sfnt, FontFileKind.Woff2, metadata, priv, faces.Count);
    }

    /// <summary>Reads the header and directories only, for <see cref="FontLoader.Probe(ReadOnlySpan{byte})"/>: the face count and outline kind.</summary>
    public static FontProbe Probe(ReadOnlySpan<byte> data)
    {
        try
        {
            var r = new FontReader(data);
            r.Skip(4);
            uint flavor = r.ReadUInt32();
            r.Skip(4);
            int count = r.ReadUInt16();
            r.Seek(HeaderSize);
            bool glyf = false, cff = false;
            for (int i = 0; i < count; i++)
            {
                int flags = r.ReadByte();
                int known = flags & 0x3F;
                FontTag tag = known == 63 ? r.ReadTag() : new FontTag(s_knownTags[known]);
                int version = flags >> 6;
                ReadBase128(ref r);
                bool isGlyfOrLoca = tag == Tags.Glyf || tag == Tags.Loca;
                if (isGlyfOrLoca ? version == 0 : version != 0)
                {
                    ReadBase128(ref r);
                }

                glyf |= tag == Tags.Glyf;
                cff |= tag == Tags.Cff || tag == Tags.Cff2;
            }

            FontOutlines? outlines = glyf ? FontOutlines.TrueType : cff ? FontOutlines.Cff : null;
            if (flavor != SfntDirectory.CollectionTag)
            {
                return new FontProbe(FontFileKind.Woff2, 1, outlines);
            }

            r.Skip(4);
            return new FontProbe(FontFileKind.Woff2, Read255UInt16(ref r), outlines);
        }
        catch (CorruptDataException)
        {
            return new FontProbe(FontFileKind.Woff2, 0, null);
        }
    }

    /// <summary>Reads a UIntBase128: up to five bytes, high bit set on all but the last, no leading zero byte, fitting 32 bits.</summary>
    internal static uint ReadBase128(ref FontReader r)
    {
        uint value = 0;
        for (int i = 0; i < 5; i++)
        {
            int at = r.Position;
            byte b = r.ReadByte();
            if (i == 0 && b == 0x80)
            {
                throw FontErrors.Corrupt(at, "A WOFF2 UIntBase128 has a leading zero.");
            }

            if ((value & 0xFE000000) != 0)
            {
                throw FontErrors.Corrupt(at, "A WOFF2 UIntBase128 overflows 32 bits.");
            }

            value = (value << 7) | (uint)(b & 0x7F);
            if ((b & 0x80) == 0)
            {
                return value;
            }
        }

        throw FontErrors.Corrupt(r.Position, "A WOFF2 UIntBase128 runs past five bytes.");
    }

    /// <summary>Reads a 255UInt16: one byte below 253, or a word or one more byte after the codes 253, 254 and 255.</summary>
    internal static int Read255UInt16(ref FontReader r)
    {
        byte code = r.ReadByte();
        return code switch
        {
            253 => r.ReadUInt16(),
            255 => 253 + r.ReadByte(),
            254 => (253 * 2) + r.ReadByte(),
            _ => code,
        };
    }

    internal static byte[] Unbrotli(ReadOnlySpan<byte> compressed, int length, long offset, string what)
    {
        byte[] result = new byte[length];
        if (!BrotliDecoder.TryDecompress(compressed, result, out int written) || written != length)
        {
            // The BCL reports a too-small buffer and a damaged stream alike; either way the stream is not what the header says.
            throw FontErrors.Corrupt(offset, $"The WOFF2 {what} does not decompress to its declared {length} bytes.");
        }

        return result;
    }

    private static int Find(SfntFace face, FontTag tag)
    {
        foreach ((FontTag t, int body) in face.Tables)
        {
            if (t == tag)
            {
                return body;
            }
        }

        return -1;
    }

    // The transformed hmtx: flags, the advances, then the bearings the flags keep; the rest are each glyph's xMin.
    private static byte[] RebuildHmtx(ReadOnlySpan<byte> data, SfntFace face, byte[][] bodies, short[] xMin, long offset)
    {
        int hhea = Find(face, Tags.Hhea), maxp = Find(face, Tags.Maxp);
        if (hhea < 0 || maxp < 0 || bodies[hhea] is null || bodies[maxp] is null || bodies[hhea].Length < 36 || bodies[maxp].Length < 6)
        {
            throw FontErrors.Corrupt(offset, "A transformed WOFF2 hmtx table needs hhea and maxp.");
        }

        int metrics = BigEndian.UInt16(bodies[hhea], 34);
        int glyphs = BigEndian.UInt16(bodies[maxp], 4);
        if (metrics < 1 || metrics > glyphs || glyphs > xMin.Length)
        {
            throw FontErrors.Corrupt(offset, "A transformed WOFF2 hmtx table's hhea and maxp counts do not fit the glyf table.");
        }

        var r = new FontReader(data, offset);
        int flags = r.ReadByte();
        if ((flags & 0xFC) != 0 || (flags & 3) == 0)
        {
            throw FontErrors.Corrupt(offset, $"A transformed WOFF2 hmtx table has the flags 0x{flags:X2}.");
        }

        bool proportional = (flags & 1) == 0, monospaced = (flags & 2) == 0;
        byte[] hmtx = new byte[(metrics * 4) + ((glyphs - metrics) * 2)];
        for (int i = 0; i < metrics; i++)
        {
            ushort advance = r.ReadUInt16();
            hmtx[i * 4] = (byte)(advance >> 8);
            hmtx[(i * 4) + 1] = (byte)advance;
        }

        for (int i = 0; i < metrics; i++)
        {
            short lsb = proportional ? r.ReadInt16() : xMin[i];
            hmtx[(i * 4) + 2] = (byte)(lsb >> 8);
            hmtx[(i * 4) + 3] = (byte)lsb;
        }

        for (int i = metrics; i < glyphs; i++)
        {
            short lsb = monospaced ? r.ReadInt16() : xMin[i];
            int at = (metrics * 4) + ((i - metrics) * 2);
            hmtx[at] = (byte)(lsb >> 8);
            hmtx[at + 1] = (byte)lsb;
        }

        return hmtx;
    }

    private readonly record struct Entry(FontTag Tag, bool Transformed, uint OrigLength, uint StoredLength, int StreamOffset);
}
