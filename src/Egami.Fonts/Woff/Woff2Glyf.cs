using System.Buffers.Binary;

using Egami.Fonts.Parsing;
using Egami.Fonts.Subsetting;

namespace Egami.Fonts.Woff;

/// <summary>
/// Rebuilds standard 'glyf' and 'loca' tables from WOFF2's transformed glyf (W3C WOFF 2.0, section 5.1): the seven sub-streams,
/// the bounding box and overlap bitmaps, and points in the triplet encoding. Each glyph is written in the compact simple-glyph
/// form with repeated flags and padded to four bytes, as the reference decoder writes it.
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

    private static void WriteSimple(ByteWriter w, int contours, short x0, short y0, short x1, short y1, List<int> ends, ReadOnlySpan<byte> code, List<(int X, int Y, bool On)> points, bool overlap)
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
