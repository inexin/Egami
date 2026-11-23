using System.Buffers.Binary;

using Egami.Fonts.Parsing;
using Egami.Fonts.Subsetting;

namespace Egami.Fonts.Woff;

/// <summary>
/// WOFF2's transformed glyf (W3C WOFF 2.0, section 5.1): the seven sub-streams, the bounding box and overlap bitmaps, and points
/// in the triplet encoding. <see cref="Rebuild"/> writes standard 'glyf' and 'loca' tables from it, each glyph in the compact
/// simple-glyph form with repeated flags and padded to four bytes, as the reference decoder writes it; <see cref="Transform"/>
/// writes it from 'glyf' and 'loca', and <see cref="TransformHmtx"/> the hmtx transform (section 5.4).
/// </summary>
internal static class Woff2Glyf
{
    private const int OnCurve = 0x01;
    private const int XShort = 0x02;
    private const int YShort = 0x04;
    private const int Repeat = 0x08;
    private const int XSame = 0x10;
    private const int YSame = 0x20;
    private const int OverlapSimple = 0x40;

    private const int ArgsAreWords = 0x0001;
    private const int HaveScale = 0x0008;
    private const int MoreComponents = 0x0020;
    private const int HaveXYScale = 0x0040;
    private const int HaveTwoByTwo = 0x0080;
    private const int HaveInstructions = 0x0100;

    public sealed record Result(byte[] Glyf, byte[] Loca, short[] XMin);

    public static Result Rebuild(ReadOnlySpan<byte> data, long offset, long limit)
    {
        var header = new FontReader(data, offset);
        header.Skip(2);
        int optionFlags = header.ReadUInt16();
        int glyphCount = header.ReadUInt16();
        int indexFormat = header.ReadUInt16();
        Span<int> sizes = stackalloc int[7];
        long at = 36;
        for (int i = 0; i < 7; i++)
        {
            uint size = header.ReadUInt32();
            if (size > data.Length)
            {
                throw FontErrors.Corrupt(offset, "A WOFF2 glyf sub-stream is longer than the table.");
            }

            sizes[i] = (int)size;
        }

        if (indexFormat > 1)
        {
            throw FontErrors.Corrupt(offset, $"The WOFF2 glyf table gives loca format {indexFormat}.");
        }

        // nContour, nPoints, flag, glyph, composite, bbox and instruction streams, then the optional overlap bitmap.
        var streams = new Stream[7];
        for (int i = 0; i < 7; i++)
        {
            if (at + sizes[i] > data.Length)
            {
                throw FontErrors.Corrupt(offset + at, "A WOFF2 glyf sub-stream runs past the end of the table.");
            }

            streams[i] = new Stream((int)at, sizes[i]);
            at += sizes[i];
        }

        int bitmapBytes = 4 * ((glyphCount + 31) / 32);
        if (streams[5].Length < bitmapBytes)
        {
            throw FontErrors.Corrupt(offset, "The WOFF2 bounding box stream is shorter than its bitmap.");
        }

        ReadOnlySpan<byte> overlap = default;
        if ((optionFlags & 1) != 0)
        {
            int overlapBytes = (glyphCount + 7) / 8;
            if (at + overlapBytes > data.Length)
            {
                throw FontErrors.Corrupt(offset + at, "The WOFF2 overlap bitmap runs past the end of the table.");
            }

            overlap = data.Slice((int)at, overlapBytes);
        }

        var nContours = new FontReader(Slice(data, streams[0]), offset + streams[0].Start);
        var nPoints = new FontReader(Slice(data, streams[1]), offset + streams[1].Start);
        var flags = new FontReader(Slice(data, streams[2]), offset + streams[2].Start);
        var glyphs = new FontReader(Slice(data, streams[3]), offset + streams[3].Start);
        var composites = new FontReader(Slice(data, streams[4]), offset + streams[4].Start);
        ReadOnlySpan<byte> bboxStream = Slice(data, streams[5]);
        ReadOnlySpan<byte> bboxBitmap = bboxStream[..bitmapBytes];
        var bboxes = new FontReader(bboxStream[bitmapBytes..], offset + streams[5].Start + bitmapBytes);
        var instructions = new FontReader(Slice(data, streams[6]), offset + streams[6].Start);

        var glyf = new ByteWriter(Math.Max(256, data.Length * 2));
        var loca = new uint[glyphCount + 1];
        var xMin = new short[glyphCount];
        var points = new List<(int X, int Y, bool On)>();
        var ends = new List<int>();
        for (int g = 0; g < glyphCount; g++)
        {
            loca[g] = (uint)glyf.Length;
            int contours = nContours.ReadInt16();
            bool hasBox = (bboxBitmap[g >> 3] & (0x80 >> (g & 7))) != 0;
            if (contours == 0)
            {
                if (hasBox)
                {
                    throw FontErrors.Corrupt(offset, $"Empty glyph {g} of the WOFF2 glyf table has a bounding box.");
                }

                continue;
            }

            if (contours < 0)
            {
                if (contours != -1)
                {
                    throw FontErrors.Corrupt(offset, $"Glyph {g} of the WOFF2 glyf table has {contours} contours.");
                }

                if (!hasBox)
                {
                    throw FontErrors.Corrupt(offset, $"Composite glyph {g} of the WOFF2 glyf table has no bounding box.");
                }

                glyf.U16(0xFFFF);
                short x0 = bboxes.ReadInt16(), y0 = bboxes.ReadInt16(), x1 = bboxes.ReadInt16(), y1 = bboxes.ReadInt16();
                glyf.U16(x0);
                glyf.U16(y0);
                glyf.U16(x1);
                glyf.U16(y1);
                xMin[g] = x0;
                bool haveInstructions = false;
                int componentStart = composites.Position, flagWord;
                do
                {
                    flagWord = composites.ReadUInt16();
                    composites.Skip(2 + ComponentArgumentBytes(flagWord));
                    haveInstructions |= (flagWord & HaveInstructions) != 0;
                }
                while ((flagWord & MoreComponents) != 0);

                glyf.Bytes(composites.Data[componentStart..composites.Position]);
                if (haveInstructions)
                {
                    int length = Woff2Decoder.Read255UInt16(ref glyphs);
                    glyf.U16(length);
                    glyf.Bytes(instructions.ReadBytes(length));
                }
            }
            else
            {
                points.Clear();
                ends.Clear();
                int total = 0;
                for (int c = 0; c < contours; c++)
                {
                    int n = Woff2Decoder.Read255UInt16(ref nPoints);
                    total += n;
                    if (total > 0xFFFF)
                    {
                        throw FontErrors.Corrupt(offset, $"Glyph {g} of the WOFF2 glyf table has more than 65535 points.");
                    }

                    ends.Add(total - 1);
                }

                int x = 0, y = 0;
                for (int p = 0; p < total; p++)
                {
                    int flag = flags.ReadByte();
                    (int dx, int dy) = Triplet(flag & 0x7F, ref glyphs);
                    x += dx;
                    y += dy;
                    points.Add((x, y, (flag & 0x80) == 0));
                }

                int instructionLength = Woff2Decoder.Read255UInt16(ref glyphs);
                ReadOnlySpan<byte> code = instructions.ReadBytes(instructionLength);
                short bx0, by0, bx1, by1;
                if (hasBox)
                {
                    (bx0, by0, bx1, by1) = (bboxes.ReadInt16(), bboxes.ReadInt16(), bboxes.ReadInt16(), bboxes.ReadInt16());
                }
                else
                {
                    int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
                    foreach ((int px, int py, _) in points)
                    {
                        minX = Math.Min(minX, px);
                        minY = Math.Min(minY, py);
                        maxX = Math.Max(maxX, px);
                        maxY = Math.Max(maxY, py);
                    }

                    if (total == 0)
                    {
                        minX = minY = maxX = maxY = 0;
                    }

                    (bx0, by0, bx1, by1) = (Clamp16(minX), Clamp16(minY), Clamp16(maxX), Clamp16(maxY));
                }

                xMin[g] = bx0;
                bool overlapping = !overlap.IsEmpty && (overlap[g >> 3] & (0x80 >> (g & 7))) != 0;
                WriteSimple(glyf, contours, bx0, by0, bx1, by1, ends, code, points, overlapping);
            }

            glyf.PadTo(4);
            if (glyf.Length > limit)
            {
                throw new LimitExceededException(nameof(SafetyLimits.MaxTotalBytes), limit, glyf.Length);
            }
        }

        loca[glyphCount] = (uint)glyf.Length;
        if (indexFormat == 0 && glyf.Length > 0x1FFFE)
        {
            throw FontErrors.Corrupt(offset, "The rebuilt glyf table is too long for the short loca format the WOFF2 file gives.");
        }

        byte[] locaBytes = new byte[(glyphCount + 1) * (indexFormat == 0 ? 2 : 4)];
        for (int i = 0; i <= glyphCount; i++)
        {
            if (indexFormat == 0)
            {
                BinaryPrimitives.WriteUInt16BigEndian(locaBytes.AsSpan(i * 2), (ushort)(loca[i] / 2));
            }
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(locaBytes.AsSpan(i * 4), loca[i]);
            }
        }

        return new Result(glyf.ToArray(), locaBytes, xMin);
    }

    /// <summary>A transformed glyf table and each glyph's xMin, as a decoder rebuilds it (0 for an empty glyph).</summary>
    public sealed record Transformed(byte[] Data, short[] XMin);

    /// <summary>
    /// Writes the transformed glyf from standard 'glyf' and 'loca' tables: bounding boxes only where they differ from the points'
    /// and always for composites, and the overlap bitmap when a simple glyph's first flag has OVERLAP_SIMPLE.
    /// </summary>
    /// <param name="glyf">The glyf table.</param>
    /// <param name="loca">The loca table.</param>
    /// <param name="glyphCount">maxp's numGlyphs.</param>
    /// <param name="indexFormat">head's indexToLocFormat: 0 short, 1 long.</param>
    /// <returns>The transformed table, or null when the rebuilt glyf would not fit the short loca format.</returns>
    /// <exception cref="CorruptDataException">loca or a glyph cannot be read.</exception>
    public static Transformed? Transform(ReadOnlySpan<byte> glyf, ReadOnlySpan<byte> loca, int glyphCount, int indexFormat)
    {
        if (indexFormat is not (0 or 1))
        {
            throw FontErrors.Corrupt(0, $"head gives loca format {indexFormat}.");
        }

        int entry = indexFormat == 0 ? 2 : 4;
        if (loca.Length < (glyphCount + 1) * entry)
        {
            throw FontErrors.Corrupt(0, $"The loca table holds fewer than {glyphCount + 1} offsets.");
        }

        var nContours = new ByteWriter(glyphCount * 2);
        var nPoints = new ByteWriter(glyphCount * 4);
        var flags = new ByteWriter(glyf.Length / 3);
        var glyphs = new ByteWriter(glyf.Length * 3 / 4);
        var composites = new ByteWriter(64);
        var bboxes = new ByteWriter(64);
        var instructions = new Slices(glyphCount);
        byte[] bboxBitmap = new byte[4 * ((glyphCount + 31) / 32)];
        byte[] overlap = new byte[(glyphCount + 7) / 8];
        bool anyOverlap = false;
        var xMin = new short[glyphCount];
        long rebuilt = 0;
        var scratch = new GlyphScratch();
        for (int g = 0; g < glyphCount; g++)
        {
            uint start = LocaAt(loca, g, indexFormat), end = LocaAt(loca, g + 1, indexFormat);
            if (end < start || end > glyf.Length)
            {
                throw FontErrors.Corrupt(0, $"loca gives glyph {g} the bytes {start} to {end} of a {glyf.Length}-byte glyf table.");
            }

            if (end - start < 10)
            {
                // Empty (or too short to hold a header, which readers draw empty too).
                nContours.U16(0);
                continue;
            }

            ReadOnlySpan<byte> data = glyf[(int)start..(int)end];
            var r = new FontReader(data, start);
            int contours = r.ReadInt16();
            short x0 = r.ReadInt16(), y0 = r.ReadInt16(), x1 = r.ReadInt16(), y1 = r.ReadInt16();
            if (contours == 0)
            {
                nContours.U16(0);
                continue;
            }

            if (contours < 0)
            {
                if (contours != -1)
                {
                    throw FontErrors.Corrupt(start, $"Glyph {g} has {contours} contours.");
                }

                nContours.U16(0xFFFF);
                int componentStart = r.Position, flagWord;
                bool haveInstructions = false;
                do
                {
                    flagWord = r.ReadUInt16();
                    r.Skip(2 + ComponentArgumentBytes(flagWord));
                    haveInstructions |= (flagWord & HaveInstructions) != 0;
                }
                while ((flagWord & MoreComponents) != 0);

                composites.Bytes(data[componentStart..r.Position]);
                int size = 10 + (r.Position - componentStart);
                if (haveInstructions)
                {
                    int length = r.ReadUInt16();
                    Write255UInt16(glyphs, length);
                    instructions.Add((int)start + r.Position, length);
                    r.Skip(length);
                    size += 2 + length;
                }

                bboxBitmap[g >> 3] |= (byte)(0x80 >> (g & 7));
                WriteBox(bboxes, x0, y0, x1, y1);
                xMin[g] = x0;
                rebuilt += (size + 3) & ~3;
                continue;
            }

            nContours.U16(contours);
            rebuilt += TransformSimple(ref r, start, g, contours, (x0, y0, x1, y1), scratch, nPoints, flags, glyphs, instructions, bboxes, bboxBitmap, overlap, ref anyOverlap);
            xMin[g] = x0;
        }

        if (indexFormat == 0 && rebuilt > 0x1FFFE)
        {
            return null;
        }

        var w = new ByteWriter(36 + nContours.Length + nPoints.Length + flags.Length + glyphs.Length + composites.Length + bboxBitmap.Length + bboxes.Length + instructions.Length + (anyOverlap ? overlap.Length : 0));
        w.U16(0);
        w.U16(anyOverlap ? 1 : 0);
        w.U16(glyphCount);
        w.U16(indexFormat);
        w.U32((uint)nContours.Length);
        w.U32((uint)nPoints.Length);
        w.U32((uint)flags.Length);
        w.U32((uint)glyphs.Length);
        w.U32((uint)composites.Length);
        w.U32((uint)(bboxBitmap.Length + bboxes.Length));
        w.U32((uint)instructions.Length);
        w.Bytes(nContours.Written);
        w.Bytes(nPoints.Written);
        w.Bytes(flags.Written);
        w.Bytes(glyphs.Written);
        w.Bytes(composites.Written);
        w.Bytes(bboxBitmap);
        w.Bytes(bboxes.Written);
        instructions.CopyTo(w, glyf);
        if (anyOverlap)
        {
            w.Bytes(overlap);
        }

        return new Transformed(w.Take(), xMin);
    }

    /// <summary>
    /// Writes the transformed hmtx (version 1): the bearings that equal each glyph's xMin are left out, the proportional and
    /// monospaced runs each as a whole.
    /// </summary>
    /// <returns>The table, or null when no run can be left out or hmtx has not exactly the length hhea and maxp give.</returns>
    public static byte[]? TransformHmtx(ReadOnlySpan<byte> hmtx, int metrics, int glyphs, short[] xMin)
    {
        if (metrics < 1 || metrics > glyphs || glyphs > xMin.Length || hmtx.Length != (metrics * 4) + ((glyphs - metrics) * 2))
        {
            return null;
        }

        bool proportional = true, monospaced = glyphs > metrics;
        for (int i = 0; i < metrics && proportional; i++)
        {
            proportional = BinaryPrimitives.ReadInt16BigEndian(hmtx[((i * 4) + 2)..]) == xMin[i];
        }

        for (int i = metrics; i < glyphs && monospaced; i++)
        {
            monospaced = BinaryPrimitives.ReadInt16BigEndian(hmtx[((metrics * 4) + ((i - metrics) * 2))..]) == xMin[i];
        }

        if (!proportional && !monospaced)
        {
            return null;
        }

        var w = new ByteWriter(1 + hmtx.Length);
        w.U8((proportional ? 1 : 0) | (monospaced ? 2 : 0));
        for (int i = 0; i < metrics; i++)
        {
            w.Bytes(hmtx.Slice(i * 4, 2));
        }

        if (!proportional)
        {
            for (int i = 0; i < metrics; i++)
            {
                w.Bytes(hmtx.Slice((i * 4) + 2, 2));
            }
        }

        if (!monospaced)
        {
            w.Bytes(hmtx[(metrics * 4)..]);
        }

        return w.ToArray();
    }

    /// <summary>Writes a 255UInt16 as the reference encoder does: one byte below 253, two below 762, else three.</summary>
    internal static void Write255UInt16(ByteWriter w, int value)
    {
        if (value < 253)
        {
            w.U8(value);
        }
        else if (value < 506)
        {
            w.U8(255);
            w.U8(value - 253);
        }
        else if (value < 762)
        {
            w.U8(254);
            w.U8(value - 506);
        }
        else
        {
            w.U8(253);
            w.U16(value);
        }
    }

    // One simple glyph: contour point counts, a flag and triplet per point, the instructions, and the box when the points' box
    // differs. Returns the length the decoder rebuilds it to, padded to four bytes.
    private static long TransformSimple(ref FontReader r, long glyphAt, int g, int contours, (short X0, short Y0, short X1, short Y1) box, GlyphScratch s, ByteWriter nPoints, ByteWriter flagStream, ByteWriter glyphs, Slices instructions, ByteWriter bboxes, byte[] bboxBitmap, byte[] overlap, ref bool anyOverlap)
    {
        int previous = -1;
        for (int c = 0; c < contours; c++)
        {
            int last = r.ReadUInt16();
            if (last < previous)
            {
                throw FontErrors.Corrupt(glyphAt, $"Glyph {g}'s contour ends go backwards.");
            }

            Write255UInt16(nPoints, last - previous);
            previous = last;
        }

        int count = previous + 1;
        int instructionLength = r.ReadUInt16();
        int codeAt = (int)glyphAt + r.Position;
        r.Skip(instructionLength);
        s.Ensure(count);
        byte[] f = s.Flags;
        for (int p = 0; p < count;)
        {
            byte flag = r.ReadByte();
            f[p++] = flag;
            if ((flag & Repeat) != 0)
            {
                int times = r.ReadByte();
                if (times > count - p)
                {
                    throw FontErrors.Corrupt(glyphAt, $"Glyph {g}'s flags repeat past its {count} points.");
                }

                for (int k = 0; k < times; k++)
                {
                    f[p++] = flag;
                }
            }
        }

        int[] dx = s.Dx, dy = s.Dy;
        for (int p = 0; p < count; p++)
        {
            int flag = f[p];
            dx[p] = (flag & XShort) != 0 ? ((flag & XSame) != 0 ? r.ReadByte() : -r.ReadByte()) : (flag & XSame) != 0 ? 0 : r.ReadInt16();
        }

        for (int p = 0; p < count; p++)
        {
            int flag = f[p];
            dy[p] = (flag & YShort) != 0 ? ((flag & YSame) != 0 ? r.ReadByte() : -r.ReadByte()) : (flag & YSame) != 0 ? 0 : r.ReadInt16();
        }

        // Each point's flag and triplet, the points' box, and the rebuilt glyph's flag, x and y bytes as WriteSimple writes them.
        bool overlapping = count > 0 && (f[0] & OverlapSimple) != 0;
        int x = 0, y = 0, minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        int flagBytes = 0, coordinateBytes = 0, lastFlag = -1, repeat = 0;
        for (int p = 0; p < count; p++)
        {
            int ddx = dx[p], ddy = dy[p];
            x += ddx;
            y += ddy;
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
            bool on = (f[p] & OnCurve) != 0;
            WriteTriplet(flagStream, glyphs, ddx, ddy, on);

            int rebuiltFlag = (on ? OnCurve : 0) | (overlapping && p == 0 ? OverlapSimple : 0);
            rebuiltFlag |= ddx == 0 ? XSame : ddx is > -256 and < 256 ? XShort | (ddx > 0 ? XSame : 0) : 0;
            rebuiltFlag |= ddy == 0 ? YSame : ddy is > -256 and < 256 ? YShort | (ddy > 0 ? YSame : 0) : 0;
            coordinateBytes += (ddx == 0 ? 0 : ddx is > -256 and < 256 ? 1 : 2) + (ddy == 0 ? 0 : ddy is > -256 and < 256 ? 1 : 2);
            if (rebuiltFlag == lastFlag && repeat != 255)
            {
                flagBytes += repeat == 0 ? 1 : 0;
                repeat++;
            }
            else
            {
                flagBytes++;
                repeat = 0;
            }

            lastFlag = rebuiltFlag;
        }

        Write255UInt16(glyphs, instructionLength);
        instructions.Add(codeAt, instructionLength);
        if (count == 0)
        {
            minX = minY = maxX = maxY = 0;
        }

        if (Clamp16(minX) != box.X0 || Clamp16(minY) != box.Y0 || Clamp16(maxX) != box.X1 || Clamp16(maxY) != box.Y1)
        {
            bboxBitmap[g >> 3] |= (byte)(0x80 >> (g & 7));
            WriteBox(bboxes, box.X0, box.Y0, box.X1, box.Y1);
        }

        if (overlapping)
        {
            overlap[g >> 3] |= (byte)(0x80 >> (g & 7));
            anyOverlap = true;
        }

        long size = 10 + (2L * contours) + 2 + instructionLength + flagBytes + coordinateBytes;
        return (size + 3) & ~3L;
    }

    // One point in the triplet encoding (WOFF 2.0, section 5.2): the flag's top bit set for an off-curve point, its low seven bits
    // choosing how many bytes the deltas take; bit 0 of the index is x's sign and bit 1 y's, set for positive.
    private static void WriteTriplet(ByteWriter flags, ByteWriter glyphs, int dx, int dy, bool on)
    {
        int ax = Math.Abs(dx), ay = Math.Abs(dy);
        int xSign = dx >= 0 ? 1 : 0, ySign = dy >= 0 ? 1 : 0;
        int onBit = on ? 0 : 0x80;
        if (dx == 0 && ay < 1280)
        {
            flags.U8(onBit | ((ay >> 8) << 1) | ySign);
            glyphs.U8(ay & 0xFF);
        }
        else if (dy == 0 && ax < 1280)
        {
            flags.U8(onBit | (10 + ((ax >> 8) << 1) + xSign));
            glyphs.U8(ax & 0xFF);
        }
        else if (ax <= 64 && ay <= 64)
        {
            flags.U8(onBit | (20 + ((ax - 1) & 0x30) + (((ay - 1) & 0x30) >> 2) + (ySign << 1) + xSign));
            glyphs.U8((((ax - 1) & 0x0F) << 4) | ((ay - 1) & 0x0F));
        }
        else if (ax <= 768 && ay <= 768)
        {
            flags.U8(onBit | (84 + (12 * ((ax - 1) >> 8)) + (((ay - 1) >> 8) << 2) + (ySign << 1) + xSign));
            glyphs.U8((ax - 1) & 0xFF);
            glyphs.U8((ay - 1) & 0xFF);
        }
        else if (ax < 4096 && ay < 4096)
        {
            flags.U8(onBit | (120 + (ySign << 1) + xSign));
            glyphs.U8(ax >> 4);
            glyphs.U8(((ax & 0x0F) << 4) | (ay >> 8));
            glyphs.U8(ay & 0xFF);
        }
        else
        {
            flags.U8(onBit | (124 + (ySign << 1) + xSign));
            glyphs.U16(ax);
            glyphs.U16(ay);
        }
    }

    private static void WriteBox(ByteWriter w, short x0, short y0, short x1, short y1)
    {
        w.U16(x0);
        w.U16(y0);
        w.U16(x1);
        w.U16(y1);
    }

    private static uint LocaAt(ReadOnlySpan<byte> loca, int index, int indexFormat) =>
        indexFormat == 0 ? BinaryPrimitives.ReadUInt16BigEndian(loca[(index * 2)..]) * 2u : BinaryPrimitives.ReadUInt32BigEndian(loca[(index * 4)..]);

    // Per-point buffers reused across the glyphs of one table.
    // The instructions of each glyph, as offsets into glyf: they go to the stream unchanged, so they are copied once, at the end.
    private sealed class Slices(int capacity)
    {
        private readonly int[] _at = new int[capacity];
        private readonly int[] _length = new int[capacity];
        private int _count;

        public int Length { get; private set; }

        public void Add(int at, int length)
        {
            if (length > 0)
            {
                _at[_count] = at;
                _length[_count++] = length;
                Length += length;
            }
        }

        public void CopyTo(ByteWriter w, ReadOnlySpan<byte> glyf)
        {
            for (int i = 0; i < _count; i++)
            {
                w.Bytes(glyf.Slice(_at[i], _length[i]));
            }
        }
    }

    private sealed class GlyphScratch
    {
        public byte[] Flags { get; private set; } = new byte[256];

        public int[] Dx { get; private set; } = new int[256];

        public int[] Dy { get; private set; } = new int[256];

        public void Ensure(int count)
        {
            if (count > Flags.Length)
            {
                int size = Math.Max(count, Flags.Length * 2);
                Flags = new byte[size];
                Dx = new int[size];
                Dy = new int[size];
            }
        }
    }

    /// <summary>Decodes one point's deltas from its flag (bit 7 cleared) and the bytes it takes from the glyph stream.</summary>
    internal static (int Dx, int Dy) Triplet(int flag, ref FontReader r)
    {
        int dx, dy;
        if (flag < 10)
        {
            dx = 0;
            dy = Sign(flag, ((flag & 14) << 7) + r.ReadByte());
        }
        else if (flag < 20)
        {
            dx = Sign(flag, (((flag - 10) & 14) << 7) + r.ReadByte());
            dy = 0;
        }
        else if (flag < 84)
        {
            int b0 = flag - 20, b1 = r.ReadByte();
            dx = Sign(flag, 1 + (b0 & 0x30) + (b1 >> 4));
            dy = Sign(flag >> 1, 1 + ((b0 & 0x0C) << 2) + (b1 & 0x0F));
        }
        else if (flag < 120)
        {
            int b0 = flag - 84, b1 = r.ReadByte(), b2 = r.ReadByte();
            dx = Sign(flag, 1 + ((b0 / 12) << 8) + b1);
            dy = Sign(flag >> 1, 1 + (((b0 % 12) >> 2) << 8) + b2);
        }
        else if (flag < 124)
        {
            int b1 = r.ReadByte(), b2 = r.ReadByte(), b3 = r.ReadByte();
            dx = Sign(flag, (b1 << 4) + (b2 >> 4));
            dy = Sign(flag >> 1, ((b2 & 0x0F) << 8) + b3);
        }
        else
        {
            int b1 = r.ReadByte(), b2 = r.ReadByte(), b3 = r.ReadByte(), b4 = r.ReadByte();
            dx = Sign(flag, (b1 << 8) + b2);
            dy = Sign(flag >> 1, (b3 << 8) + b4);
        }

        return (dx, dy);

        static int Sign(int flag, int value) => (flag & 1) != 0 ? value : -value;
    }

    /// <summary>Writes a simple glyph in the compact form: repeated flags, then x and y as bytes where they fit.</summary>
    internal static void WriteSimple(ByteWriter w, int contours, short x0, short y0, short x1, short y1, IReadOnlyList<int> ends, ReadOnlySpan<byte> code, List<(int X, int Y, bool On)> points, bool overlap)
    {
        w.U16(contours);
        w.U16(x0);
        w.U16(y0);
        w.U16(x1);
        w.U16(y1);
        foreach (int end in ends)
        {
            w.U16(end);
        }

        w.U16(code.Length);
        w.Bytes(code);

        // Flags with repeats, then x and y in their short or long forms.
        int lastFlag = -1, repeat = 0, lastX = 0, lastY = 0;
        var xs = new ByteWriter(points.Count * 2);
        var ys = new ByteWriter(points.Count * 2);
        for (int i = 0; i < points.Count; i++)
        {
            (int x, int y, bool on) = points[i];
            int flag = on ? OnCurve : 0;
            if (overlap && i == 0)
            {
                flag |= OverlapSimple;
            }

            int dx = x - lastX, dy = y - lastY;
            if (dx == 0)
            {
                flag |= XSame;
            }
            else if (dx > -256 && dx < 256)
            {
                flag |= XShort | (dx > 0 ? XSame : 0);
                xs.U8(Math.Abs(dx));
            }
            else
            {
                xs.U16(dx);
            }

            if (dy == 0)
            {
                flag |= YSame;
            }
            else if (dy > -256 && dy < 256)
            {
                flag |= YShort | (dy > 0 ? YSame : 0);
                ys.U8(Math.Abs(dy));
            }
            else
            {
                ys.U16(dy);
            }

            if (flag == lastFlag && repeat != 255)
            {
                // The count byte follows the flag it repeats.
                if (repeat == 0)
                {
                    w.PatchU8(w.Length - 1, flag | Repeat);
                    w.U8(1);
                }
                else
                {
                    w.PatchU8(w.Length - 1, repeat + 1);
                }

                repeat++;
            }
            else
            {
                w.U8(flag);
                repeat = 0;
            }

            lastX = x;
            lastY = y;
            lastFlag = flag;
        }

        w.Bytes(xs.Written);
        w.Bytes(ys.Written);
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

    private static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> data, Stream s) => data.Slice(s.Start, s.Length);

    private readonly record struct Stream(int Start, int Length);
}
