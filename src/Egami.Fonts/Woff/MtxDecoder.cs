using System.Buffers.Binary;

using Egami.Fonts.Parsing;
using Egami.Fonts.Subsetting;

namespace Egami.Fonts.Woff;

/// <summary>
/// Decodes MicroType Express (W3C member submission, 2008): a 10-byte header, then three LZCOMP blocks holding the font in the
/// Compact Table Format (the tables, with glyf, cvt, hdmx and VDMX in their compact forms and no loca), the glyphs' initial push
/// data and their instructions. The result is an ordinary sfnt whose glyf and loca are rebuilt, each glyph in the compact
/// simple-glyph form and padded to four bytes.
/// </summary>
internal static class MtxDecoder
{
    private const int HeaderSize = 10;

    private const int ArgsAreWords = 0x0001;
    private const int HaveScale = 0x0008;
    private const int MoreComponents = 0x0020;
    private const int HaveXYScale = 0x0040;
    private const int HaveTwoByTwo = 0x0080;
    private const int HaveInstructions = 0x0100;

    private const int Hop3Code = 251;
    private const int Hop4Code = 252;

    private static readonly FontTag Glyf = new("glyf");
    private static readonly FontTag Loca = new("loca");
    private static readonly FontTag Cvt = new("cvt ");
    private static readonly FontTag Hdmx = new("hdmx");
    private static readonly FontTag Vdmx = new("VDMX");
    private static readonly FontTag Head = new("head");
    private static readonly FontTag Maxp = new("maxp");
    private static readonly FontTag Hhea = new("hhea");
    private static readonly FontTag Hmtx = new("hmtx");

    /// <summary>Returns whether the data has a MicroType Express header whose block offsets fit its length.</summary>
    public static bool IsMtx(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize)
        {
            return false;
        }

        uint offset2 = BigEndian.UInt24(data, 4), offset3 = BigEndian.UInt24(data, 7);
        return offset2 >= HeaderSize && offset2 <= offset3 && offset3 <= data.Length;
    }

    /// <summary>Decodes a MicroType Express font into sfnt bytes.</summary>
    /// <param name="data">The MicroType Express data.</param>
    /// <param name="limit">The most bytes any block or the result may take.</param>
    /// <param name="offset">The data's offset in the file, for errors.</param>
    public static byte[] Decode(ReadOnlySpan<byte> data, long limit, long offset)
    {
        if (!IsMtx(data))
        {
            throw FontErrors.Corrupt(offset, "The MicroType Express header's block offsets do not fit the data.");
        }

        int version = data[0];
        int offset2 = (int)BigEndian.UInt24(data, 4), offset3 = (int)BigEndian.UInt24(data, 7);
        bool runLengthFlag = version != 1;
        byte[] tables = Lzcomp.Decompress(data[HeaderSize..offset2], runLengthFlag, limit, offset + HeaderSize);
        byte[] push = Lzcomp.Decompress(data[offset2..offset3], runLengthFlag, limit, offset + offset2);
        byte[] code = Lzcomp.Decompress(data[offset3..], runLengthFlag, limit, offset + offset3);
        return Rebuild(tables, push, code, limit);
    }

    /// <summary>Turns the three decompressed blocks into an sfnt.</summary>
    internal static byte[] Rebuild(byte[] ctf, byte[] push, byte[] code, long limit)
    {
        var r = new FontReader(ctf);
        uint flavor = r.ReadUInt32();
        int count = r.ReadUInt16();
        r.Skip(6);
        var tags = new List<FontTag>(count);
        var bodies = new List<byte[]>(count);
        var found = new Dictionary<FontTag, int>();
        for (int i = 0; i < count; i++)
        {
            FontTag tag = r.ReadTag();
            r.Skip(4);
            uint at = r.ReadUInt32(), length = r.ReadUInt32();
            if (tag == Loca || found.ContainsKey(tag))
            {
                continue;
            }

            if (at > ctf.Length || length > ctf.Length - at)
            {
                throw FontErrors.Corrupt(12 + (16 * i), $"The MicroType Express {tag} table lies outside the font block.");
            }

            found.Add(tag, bodies.Count);
            tags.Add(tag);
            bodies.Add(ctf.AsSpan((int)at, (int)length).ToArray());
        }

        ReadOnlySpan<byte> Table(FontTag tag) => found.TryGetValue(tag, out int index) ? bodies[index] : [];

        if (found.TryGetValue(Cvt, out int cvt))
        {
            bodies[cvt] = DecodeCvt(bodies[cvt], limit);
        }

        if (found.TryGetValue(Hdmx, out int hdmx))
        {
            bodies[hdmx] = DecodeHdmx(bodies[hdmx], Table(Head), Table(Maxp), Table(Hhea), Table(Hmtx), limit);
        }

        if (found.TryGetValue(Vdmx, out int vdmx))
        {
            bodies[vdmx] = DecodeVdmx(bodies[vdmx]);
        }

        if (found.TryGetValue(Glyf, out int glyf))
        {
            ReadOnlySpan<byte> maxp = Table(Maxp);
            if (maxp.Length < 6 || !found.TryGetValue(Head, out int head) || bodies[head].Length < 54)
            {
                throw FontErrors.Corrupt(0, "The MicroType Express font has glyf but no maxp or head to rebuild loca with.");
            }

            int glyphs = BigEndian.UInt16(maxp, 4);
            (byte[] glyfTable, byte[] locaTable, bool longLoca) = DecodeGlyf(bodies[glyf], push, code, glyphs, BigEndian.Int16(bodies[head], 50) != 0, limit);
            bodies[glyf] = glyfTable;
            byte[] headTable = bodies[head];
            BinaryPrimitives.WriteInt16BigEndian(headTable.AsSpan(50), (short)(longLoca ? 1 : 0));
            found.Add(Loca, bodies.Count);
            tags.Add(Loca);
            bodies.Add(locaTable);
        }

        var face = new SfntFace(flavor, [.. tags.Select((t, i) => (t, i))]);
        return SfntAssembler.Build([face], bodies, collectionVersion: 0, limit);
    }

    /// <summary>Decodes the compact cvt: a count, then deltas from the previous value in one to three bytes each.</summary>
    internal static byte[] DecodeCvt(ReadOnlySpan<byte> data, long limit)
    {
        var r = new FontReader(data);
        int count = r.ReadUInt16();
        WebFontDecoder.CheckLimit(2L * count, limit);
        byte[] cvt = new byte[2 * count];
        int last = 0;
        for (int i = 0; i < count; i++)
        {
            int b = r.ReadByte();
            int delta = b switch
            {
                >= 248 => (238 * (b - 247)) + r.ReadByte(),
                >= 239 => -((238 * (b - 239)) + r.ReadByte()),
                238 => r.ReadInt16(),
                _ => b,
            };
            last = (last + delta) & 0xFFFF;
            BinaryPrimitives.WriteUInt16BigEndian(cvt.AsSpan(2 * i), (ushort)last);
        }

        return cvt;
    }

    /// <summary>
    /// Decodes the compact hdmx: the header and each record's ppem and maximum width, then for every record and glyph the
    /// difference from the advance scaled to that ppem, in the magnitude-dependent bit code.
    /// </summary>
    internal static byte[] DecodeHdmx(ReadOnlySpan<byte> data, ReadOnlySpan<byte> head, ReadOnlySpan<byte> maxp, ReadOnlySpan<byte> hhea, ReadOnlySpan<byte> hmtx, long limit)
    {
        var r = new FontReader(data);
        int version = r.ReadUInt16();
        if (version >= 0x8000)
        {
            // Stored as it is, with the version flipped to say so.
            byte[] raw = data.ToArray();
            BinaryPrimitives.WriteUInt16BigEndian(raw, (ushort)(0xFFFF - version));
            return raw;
        }

        int records = r.ReadUInt16();
        r.Skip(4);
        if (head.Length < 20 || maxp.Length < 6 || hhea.Length < 36)
        {
            throw FontErrors.Corrupt(0, "The MicroType Express hdmx table needs head, maxp and hhea.");
        }

        int unitsPerEm = BigEndian.UInt16(head, 18);
        int glyphs = BigEndian.UInt16(maxp, 4);
        int metrics = Math.Min(BigEndian.UInt16(hhea, 34), hmtx.Length / 4);
        if (unitsPerEm == 0 || (metrics == 0 && glyphs > 0))
        {
            throw FontErrors.Corrupt(0, "The MicroType Express hdmx table needs advances and units per em.");
        }

        int recordSize = (int)WebFontDecoder.Pad4(2 + glyphs);
        WebFontDecoder.CheckLimit(8 + ((long)recordSize * records), limit);
        byte[] table = new byte[8 + (recordSize * records)];
        BinaryPrimitives.WriteUInt16BigEndian(table, (ushort)version);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(2), (ushort)records);
        BinaryPrimitives.WriteInt32BigEndian(table.AsSpan(4), recordSize);
        var ppems = new int[records];
        for (int i = 0; i < records; i++)
        {
            ppems[i] = r.ReadByte();
            table[8 + (i * recordSize)] = (byte)ppems[i];
            table[9 + (i * recordSize)] = r.ReadByte();
        }

        var bits = new MagnitudeReader(data[r.Position..]);
        for (int i = 0; i < records; i++)
        {
            long ppem = ppems[i];
            Span<byte> widths = table.AsSpan(10 + (i * recordSize), glyphs);
            for (int g = 0; g < glyphs; g++)
            {
                long advance = BigEndian.UInt16(hmtx, 4 * Math.Min(g, metrics - 1));
                long predicted = ((((64 * ppem * advance) + (unitsPerEm / 2)) / unitsPerEm) + 32) / 64;
                widths[g] = (byte)(predicted + bits.Next());
            }
        }

        return table;
    }

    /// <summary>
    /// Decodes the compact VDMX: the header, ratios and offsets as they are, then each group's count and its yMax and yMin
    /// multipliers (21.11 fixed point) with each record's ppem, yMax and yMin as differences from their predictions.
    /// </summary>
    internal static byte[] DecodeVdmx(ReadOnlySpan<byte> data)
    {
        var r = new FontReader(data);
        int version = r.ReadUInt16();
        if (version >= 0x8000)
        {
            byte[] raw = data.ToArray();
            BinaryPrimitives.WriteUInt16BigEndian(raw, (ushort)(0xFFFF - version));
            return raw;
        }

        int groupCount = r.ReadUInt16();
        int ratioCount = r.ReadUInt16();
        ReadOnlySpan<byte> ratios = r.ReadBytes(4 * ratioCount);
        var offsets = new int[ratioCount];
        for (int i = 0; i < ratioCount; i++)
        {
            offsets[i] = r.ReadUInt16();
        }

        var groups = new List<(int Start, int[] Entries)>(groupCount);
        int at = ratioCount > 0 ? offsets[0] : r.Position;
        for (int g = 0; g < groupCount; g++)
        {
            var gr = new FontReader(data);
            gr.Seek(at);
            int start = at;
            int recs = gr.ReadUInt16();
            int yMaxMultiplier = gr.ReadInt16(), yMinMultiplier = gr.ReadInt16();
            var bits = new MagnitudeReader(data[gr.Position..]);
            var entries = new int[3 * recs];
            int predictedPpem = 8;
            for (int j = 0; j < recs; j++)
            {
                int ppem = bits.Next() + predictedPpem;
                int yMax = bits.Next() + (((ppem * yMaxMultiplier) + 1024) / 2048);
                int yMin = bits.Next() - (((ppem * yMinMultiplier) + 1024) / 2048);
                entries[3 * j] = ppem;
                entries[(3 * j) + 1] = yMax;
                entries[(3 * j) + 2] = yMin;
                predictedPpem = ppem + 1;
            }

            at = gr.Position + bits.BytesUsed;
            groups.Add((start, entries));
        }

        var w = new ByteWriter(data.Length * 2);
        w.U16(version);
        w.U16(groupCount);
        w.U16(ratioCount);
        w.Bytes(ratios);
        int offsetsAt = w.Length;
        for (int i = 0; i < ratioCount; i++)
        {
            w.U16(0);
        }

        var groupAt = new int[groups.Count];
        for (int g = 0; g < groups.Count; g++)
        {
            groupAt[g] = w.Length;
            int[] entries = groups[g].Entries;
            int recs = entries.Length / 3;
            w.U16(recs);
            w.U8(recs > 0 ? entries[0] : 0);
            w.U8(recs > 0 ? entries[^3] : 0);
            for (int j = 0; j < recs; j++)
            {
                w.U16(entries[3 * j]);
                w.U16(entries[(3 * j) + 1]);
                w.U16(entries[(3 * j) + 2]);
            }
        }

        for (int i = 0; i < ratioCount; i++)
        {
            int group = groups.FindIndex(g => g.Start == offsets[i]);
            if (group < 0)
            {
                group = Math.Min(i, groups.Count - 1);
            }

            if (group >= 0)
            {
                w.PatchU16(offsetsAt + (2 * i), groupAt[group]);
            }
        }

        return w.ToArray();
    }

    /// <summary>
    /// Rebuilds glyf and loca from the compact glyph records, taking each glyph's initial push values from the push block and
    /// its other instructions from the code block, and writing push instructions for the values.
    /// </summary>
    internal static (byte[] Glyf, byte[] Loca, bool LongLoca) DecodeGlyf(ReadOnlySpan<byte> ctf, ReadOnlySpan<byte> push, ReadOnlySpan<byte> code, int glyphs, bool longLoca, long limit)
    {
        var r = new FontReader(ctf);
        var p = new FontReader(push);
        var c = new FontReader(code);
        var w = new ByteWriter(ctf.Length * 2);
        var offsets = new long[glyphs + 1];
        var ends = new List<int>();
        var points = new List<(int X, int Y, bool On)>();
        var instructions = new ByteWriter();
        var values = new List<int>();
        for (int g = 0; g < glyphs; g++)
        {
            offsets[g] = w.Length;
            int contours = r.ReadInt16();
            if (contours < 0)
            {
                // Composite: the box, the components as in glyf, then the instructions if the last component has them.
                w.U16(-1);
                w.Bytes(r.ReadBytes(8));
                int flags;
                do
                {
                    flags = r.ReadUInt16();
                    w.U16(flags);
                    w.Bytes(r.ReadBytes(2 + ComponentArgumentBytes(flags)));
                }
                while ((flags & MoreComponents) != 0);

                if ((flags & HaveInstructions) != 0)
                {
                    ReadInstructions(ref r, ref p, ref c, values, instructions);
                    w.U16(instructions.Length);
                    w.Bytes(instructions.Written);
                }
            }
            else
            {
                bool hasBox = contours == 0x7FFF;
                short x0 = 0, y0 = 0, x1 = 0, y1 = 0;
                if (hasBox)
                {
                    contours = r.ReadInt16();
                    if (contours < 0)
                    {
                        throw FontErrors.Corrupt(r.Position, $"MicroType Express glyph {g} gives {contours} contours.");
                    }

                    x0 = r.ReadInt16();
                    y0 = r.ReadInt16();
                    x1 = r.ReadInt16();
                    y1 = r.ReadInt16();
                }

                ends.Clear();
                // The first value is the first contour's end point; the rest are point counts.
                int end = -1;
                for (int i = 0; i < contours; i++)
                {
                    int value = Woff2Decoder.Read255UInt16(ref r);
                    end = i == 0 ? value : end + value;
                    ends.Add(end);
                }

                int pointCount = contours == 0 ? 0 : ends[^1] + 1;
                if (pointCount > ctf.Length)
                {
                    throw FontErrors.Corrupt(r.Position, $"MicroType Express glyph {g} has more points than the block has bytes.");
                }

                ReadOnlySpan<byte> pointFlags = r.ReadBytes(pointCount);
                points.Clear();
                int x = 0, y = 0;
                for (int i = 0; i < pointCount; i++)
                {
                    int flag = pointFlags[i];
                    (int dx, int dy) = Woff2Glyf.Triplet(flag & 0x7F, ref r);
                    x += dx;
                    y += dy;
                    points.Add((x, y, (flag & 0x80) == 0));
                }

                ReadInstructions(ref r, ref p, ref c, values, instructions);
                if (contours == 0 && instructions.Length == 0)
                {
                    // An empty glyph takes no bytes.
                    continue;
                }

                if (!hasBox && points.Count > 0)
                {
                    int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
                    foreach ((int px, int py, _) in points)
                    {
                        minX = Math.Min(minX, px);
                        minY = Math.Min(minY, py);
                        maxX = Math.Max(maxX, px);
                        maxY = Math.Max(maxY, py);
                    }

                    (x0, y0, x1, y1) = (Clamp16(minX), Clamp16(minY), Clamp16(maxX), Clamp16(maxY));
                }

                Woff2Glyf.WriteSimple(w, contours, x0, y0, x1, y1, ends, instructions.Written, points, overlap: false);
            }

            // Short loca offsets count words, so glyphs stay 2-byte aligned there; long ones get 4 as the WOFF2 rebuild does.
            w.PadTo(longLoca ? 4 : 2);
            WebFontDecoder.CheckLimit(w.Length, limit);
        }

        offsets[glyphs] = w.Length;
        longLoca |= w.Length > 0x1FFFE;
        byte[] loca = new byte[(glyphs + 1) * (longLoca ? 4 : 2)];
        for (int g = 0; g <= glyphs; g++)
        {
            if (longLoca)
            {
                BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(4 * g), (uint)offsets[g]);
            }
            else
            {
                BinaryPrimitives.WriteUInt16BigEndian(loca.AsSpan(2 * g), (ushort)(offsets[g] / 2));
            }
        }

        return (w.ToArray(), loca, longLoca);
    }

    /// <summary>Reads a glyph's push count and code size and writes its instructions: push instructions, then the code.</summary>
    private static void ReadInstructions(ref FontReader r, ref FontReader p, ref FontReader c, List<int> values, ByteWriter instructions)
    {
        int pushCount = Woff2Decoder.Read255UInt16(ref r);
        int codeSize = Woff2Decoder.Read255UInt16(ref r);
        values.Clear();
        while (values.Count < pushCount)
        {
            if (p.Remaining == 0)
            {
                throw FontErrors.Corrupt(p.Position, "The MicroType Express push data ends early.");
            }

            int b = p.Data[p.Position];
            if (b == Hop3Code || b == Hop4Code)
            {
                if (values.Count < 2)
                {
                    throw FontErrors.Corrupt(p.Position, "A MicroType Express hop code comes before two push values.");
                }

                p.Skip(1);
                int a = values[^2];
                values.Add(a);
                values.Add(Read255Short(ref p));
                values.Add(a);
                if (b == Hop4Code)
                {
                    values.Add(Read255Short(ref p));
                    values.Add(a);
                }
            }
            else
            {
                values.Add(Read255Short(ref p));
            }
        }

        if (values.Count > pushCount)
        {
            throw FontErrors.Corrupt(p.Position, "A MicroType Express hop code runs past the glyph's push count.");
        }

        instructions.Clear();
        WritePushes(instructions, values);
        instructions.Bytes(c.ReadBytes(codeSize));
        if (instructions.Length > 0xFFFF)
        {
            throw FontErrors.Corrupt(c.Position, "A MicroType Express glyph has more than 65535 bytes of instructions.");
        }
    }

    /// <summary>Writes push instructions for the values: runs of bytes as PUSHB or NPUSHB, runs of words as PUSHW or NPUSHW.</summary>
    internal static void WritePushes(ByteWriter w, List<int> values)
    {
        int i = 0;
        while (i < values.Count)
        {
            bool bytes = values[i] is >= 0 and <= 255;
            int j = i;
            while (j < values.Count && j - i < 255 && (values[j] is >= 0 and <= 255) == bytes)
            {
                j++;
            }

            int count = j - i;
            if (count <= 8)
            {
                w.U8((bytes ? 0xB0 : 0xB8) + count - 1);
            }
            else
            {
                w.U8(bytes ? 0x40 : 0x41);
                w.U8(count);
            }

            for (; i < j; i++)
            {
                if (bytes)
                {
                    w.U8(values[i]);
                }
                else
                {
                    w.U16(values[i]);
                }
            }
        }
    }

    /// <summary>Reads a 255Short: a byte, a flipped sign, one more byte above 250 or 500, or a word.</summary>
    internal static int Read255Short(ref FontReader r)
    {
        int code = r.ReadByte();
        if (code == 253)
        {
            return r.ReadInt16();
        }

        int sign = 1;
        if (code == 250)
        {
            sign = -1;
            code = r.ReadByte();
        }

        int value = code switch
        {
            255 => 250 + r.ReadByte(),
            254 => 500 + r.ReadByte(),
            _ => code,
        };
        return sign * value;
    }

    private static int ComponentArgumentBytes(int flags)
    {
        int bytes = (flags & ArgsAreWords) != 0 ? 4 : 2;
        if ((flags & HaveScale) != 0)
        {
            bytes += 2;
        }
        else if ((flags & HaveXYScale) != 0)
        {
            bytes += 4;
        }
        else if ((flags & HaveTwoByTwo) != 0)
        {
            bytes += 8;
        }

        return bytes;
    }

    private static short Clamp16(int value) => (short)Math.Clamp(value, short.MinValue, short.MaxValue);

    /// <summary>Reads the magnitude-dependent code: as many 1 bits as the value's size, a 0, then a sign bit (1 for minus) if nonzero; bits least significant first.</summary>
    private ref struct MagnitudeReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _bit;

        public MagnitudeReader(ReadOnlySpan<byte> data)
        {
            _data = data;
        }

        public readonly int BytesUsed => (_bit + 7) / 8;

        public int Next()
        {
            int size = 0;
            while (Bit())
            {
                if (++size > 0xFFFF)
                {
                    throw FontErrors.Corrupt(0, "A MicroType Express hdmx or VDMX value is too large.");
                }
            }

            return size == 0 ? 0 : Bit() ? -size : size;
        }

        private bool Bit()
        {
            int at = _bit >> 3;
            if (at >= _data.Length)
            {
                throw FontErrors.Corrupt(0, "The MicroType Express hdmx or VDMX bit data ends early.");
            }

            return ((_data[at] >> (_bit++ & 7)) & 1) != 0;
        }
    }
}
