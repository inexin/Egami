using System.Buffers.Binary;

using Egami.Fonts.Woff;

namespace Egami.Fonts.Tests;

/// <summary>
/// A MicroType Express writer for the tests, written from the W3C submission's description of the format: the Compact Table
/// Format (glyf with split push data and hop codes, cvt, hdmx), then each block through a greedy LZCOMP that uses copies, the
/// one-byte DUP copies and literals, optionally under the run-length layer. Egami only reads MicroType Express; this writer
/// lets the decoder be checked on whole fonts.
/// </summary>
internal static class MtxTestEncoder
{
    public static byte[] Encode(byte[] sfnt, bool runLength = false)
    {
        (byte[] ctf, byte[] push, byte[] code) = ToCtf(sfnt);
        byte[] b1 = Lzcomp(ctf, runLength), b2 = Lzcomp(push, runLength), b3 = Lzcomp(code, runLength);
        byte[] mtx = new byte[10 + b1.Length + b2.Length + b3.Length];
        mtx[0] = 3;
        WriteU24(mtx, 1, 0x100000);
        WriteU24(mtx, 4, 10 + b1.Length);
        WriteU24(mtx, 7, 10 + b1.Length + b2.Length);
        b1.CopyTo(mtx, 10);
        b2.CopyTo(mtx, 10 + b1.Length);
        b3.CopyTo(mtx, 10 + b1.Length + b2.Length);
        return mtx;
    }

    /// <summary>Wraps a MicroType Express font in an EOT 0x00020001 header, XOR-ed when asked.</summary>
    public static byte[] Eot(byte[] sfnt, byte[] fontData, bool compressed, bool xor)
    {
        byte[] plain = WebFontWriter.Write(sfnt, new WebFontOptions { Format = WebFontFormat.Eot });
        Woff.Eot.Header header = Woff.Eot.ReadHeader(plain);
        byte[] eot = new byte[header.FontDataOffset + fontData.Length];
        plain.AsSpan(0, header.FontDataOffset).CopyTo(eot);
        fontData.CopyTo(eot, header.FontDataOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(eot, (uint)eot.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(eot.AsSpan(4), (uint)fontData.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(eot.AsSpan(12), (compressed ? Woff.Eot.FlagCompressed : 0) | (xor ? Woff.Eot.FlagXor : 0));
        if (xor)
        {
            Woff.Eot.Xor(eot.AsSpan(header.FontDataOffset));
        }

        return eot;
    }

    private static void WriteU24(byte[] data, int at, int value)
    {
        data[at] = (byte)(value >> 16);
        data[at + 1] = (byte)(value >> 8);
        data[at + 2] = (byte)value;
    }

    // ---- Compact Table Format ----

    /// <summary>
    /// Returns a font's glyphs in the compact form: the outline records, push values and other instructions, which say the same
    /// for two fonts whose glyphs differ only in flag encoding, padding or how push instructions were written.
    /// </summary>
    public static (byte[] Glyf, byte[] Push, byte[] Code) GlyphStreams(byte[] sfnt)
    {
        Dictionary<string, byte[]> tables = Tables(sfnt, out _);
        var push = new List<byte>();
        var code = new List<byte>();
        return (CtfGlyf(tables, push, code), [.. push], [.. code]);
    }

    private static Dictionary<string, byte[]> Tables(byte[] sfnt, out List<string> order)
    {
        int count = BinaryPrimitives.ReadUInt16BigEndian(sfnt.AsSpan(4));
        var tables = new Dictionary<string, byte[]>();
        order = [];
        for (int i = 0; i < count; i++)
        {
            int e = 12 + (16 * i);
            string tag = System.Text.Encoding.ASCII.GetString(sfnt, e, 4);
            int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(sfnt.AsSpan(e + 8));
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(sfnt.AsSpan(e + 12));
            tables[tag] = sfnt.AsSpan(offset, length).ToArray();
            order.Add(tag);
        }

        return tables;
    }

    public static (byte[] Ctf, byte[] Push, byte[] Code) ToCtf(byte[] sfnt)
    {
        int count = BinaryPrimitives.ReadUInt16BigEndian(sfnt.AsSpan(4));
        Dictionary<string, byte[]> tables = Tables(sfnt, out List<string> order);
        var push = new List<byte>();
        var code = new List<byte>();
        var ctfTables = new Dictionary<string, byte[]>();
        foreach (string tag in order)
        {
            ctfTables[tag] = tag switch
            {
                "loca" => [],
                "glyf" => CtfGlyf(tables, push, code),
                "cvt " => CtfCvt(tables[tag]),
                "hdmx" => CtfHdmx(tables),
                _ => tables[tag],
            };
        }

        // The table directory as in the sfnt, loca with offset and length 0.
        var w = new List<byte>();
        w.AddRange(sfnt.AsSpan(0, 12).ToArray());
        int at = 12 + (16 * count);
        var bodies = new List<byte>();
        foreach (string tag in order)
        {
            byte[] body = ctfTables[tag];
            w.AddRange(System.Text.Encoding.ASCII.GetBytes(tag));
            w.AddRange([0, 0, 0, 0]);
            AddU32(w, tag == "loca" ? 0 : (uint)(at + bodies.Count));
            AddU32(w, (uint)body.Length);
            bodies.AddRange(body);
            while (bodies.Count % 4 != 0)
            {
                bodies.Add(0);
            }
        }

        w.AddRange(bodies);
        return ([.. w], [.. push], [.. code]);
    }

    private static void AddU32(List<byte> w, uint value)
    {
        w.Add((byte)(value >> 24));
        w.Add((byte)(value >> 16));
        w.Add((byte)(value >> 8));
        w.Add((byte)value);
    }

    private static void AddU16(List<byte> w, int value)
    {
        w.Add((byte)(value >> 8));
        w.Add((byte)value);
    }

    private static byte[] CtfCvt(byte[] cvt)
    {
        var w = new List<byte>();
        int count = cvt.Length / 2;
        AddU16(w, count);
        int last = 0;
        for (int i = 0; i < count; i++)
        {
            int value = BinaryPrimitives.ReadUInt16BigEndian(cvt.AsSpan(2 * i));
            int delta = (short)(value - last);
            last = value;
            if (delta is >= 0 and < 238)
            {
                w.Add((byte)delta);
            }
            else if (delta >= 238 && delta <= (238 * 8) + 237)
            {
                w.Add((byte)(247 + (delta / 238)));
                w.Add((byte)(delta % 238));
            }
            else if (delta < 0 && -delta <= (238 * 8) + 237)
            {
                w.Add((byte)(239 + (-delta / 238)));
                w.Add((byte)(-delta % 238));
            }
            else
            {
                w.Add(238);
                AddU16(w, delta);
            }
        }

        return [.. w];
    }

    private static byte[] CtfHdmx(Dictionary<string, byte[]> tables)
    {
        byte[] hdmx = tables["hdmx"];
        int records = BinaryPrimitives.ReadUInt16BigEndian(hdmx.AsSpan(2));
        int recordSize = BinaryPrimitives.ReadInt32BigEndian(hdmx.AsSpan(4));
        int glyphs = BinaryPrimitives.ReadUInt16BigEndian(tables["maxp"].AsSpan(4));
        int upem = BinaryPrimitives.ReadUInt16BigEndian(tables["head"].AsSpan(18));
        int metrics = BinaryPrimitives.ReadUInt16BigEndian(tables["hhea"].AsSpan(34));
        byte[] hmtx = tables["hmtx"];
        var w = new List<byte>(hdmx.AsSpan(0, 8).ToArray());
        for (int i = 0; i < records; i++)
        {
            w.Add(hdmx[8 + (i * recordSize)]);
            w.Add(hdmx[9 + (i * recordSize)]);
        }

        var bits = new List<bool>();
        for (int i = 0; i < records; i++)
        {
            long ppem = hdmx[8 + (i * recordSize)];
            for (int g = 0; g < glyphs; g++)
            {
                long advance = BinaryPrimitives.ReadUInt16BigEndian(hmtx.AsSpan(4 * Math.Min(g, metrics - 1)));
                long predicted = ((((64 * ppem * advance) + (upem / 2)) / upem) + 32) / 64;
                Magnitude(bits, (int)(hdmx[10 + (i * recordSize) + g] - predicted));
            }
        }

        w.AddRange(PackLsbFirst(bits));
        return [.. w];
    }

    public static void Magnitude(List<bool> bits, int value)
    {
        for (int i = 0; i < Math.Abs(value); i++)
        {
            bits.Add(true);
        }

        bits.Add(false);
        if (value != 0)
        {
            bits.Add(value < 0);
        }
    }

    public static byte[] PackLsbFirst(List<bool> bits)
    {
        byte[] bytes = new byte[(bits.Count + 7) / 8];
        for (int i = 0; i < bits.Count; i++)
        {
            if (bits[i])
            {
                bytes[i / 8] |= (byte)(1 << (i % 8));
            }
        }

        return bytes;
    }

    private static byte[] CtfGlyf(Dictionary<string, byte[]> tables, List<byte> push, List<byte> code)
    {
        byte[] glyf = tables["glyf"], loca = tables["loca"];
        int glyphs = BinaryPrimitives.ReadUInt16BigEndian(tables["maxp"].AsSpan(4));
        bool longLoca = BinaryPrimitives.ReadInt16BigEndian(tables["head"].AsSpan(50)) != 0;
        int Loca(int g) => longLoca ? (int)BinaryPrimitives.ReadUInt32BigEndian(loca.AsSpan(4 * g)) : 2 * BinaryPrimitives.ReadUInt16BigEndian(loca.AsSpan(2 * g));
        var w = new List<byte>();
        for (int g = 0; g < glyphs; g++)
        {
            ReadOnlySpan<byte> data = glyf.AsSpan(Loca(g), Loca(g + 1) - Loca(g));
            if (data.IsEmpty)
            {
                AddU16(w, 0);
                w.Add(0);
                w.Add(0);
                continue;
            }

            int contours = BinaryPrimitives.ReadInt16BigEndian(data);
            if (contours < 0)
            {
                AddU16(w, -1);
                w.AddRange(data[2..10].ToArray());
                int at = 10, flags;
                do
                {
                    flags = BinaryPrimitives.ReadUInt16BigEndian(data[at..]);
                    int size = 4 + ((flags & 1) != 0 ? 4 : 2) + ((flags & 8) != 0 ? 2 : (flags & 0x40) != 0 ? 4 : (flags & 0x80) != 0 ? 8 : 0);
                    w.AddRange(data.Slice(at, size).ToArray());
                    at += size;
                }
                while ((flags & 0x20) != 0);

                if ((flags & 0x100) != 0)
                {
                    int length = BinaryPrimitives.ReadUInt16BigEndian(data[at..]);
                    SplitInstructions(data.Slice(at + 2, length), w, push, code);
                }

                continue;
            }

            short x0 = BinaryPrimitives.ReadInt16BigEndian(data[2..]), y0 = BinaryPrimitives.ReadInt16BigEndian(data[4..]);
            short x1 = BinaryPrimitives.ReadInt16BigEndian(data[6..]), y1 = BinaryPrimitives.ReadInt16BigEndian(data[8..]);
            var ends = new int[contours];
            for (int i = 0; i < contours; i++)
            {
                ends[i] = BinaryPrimitives.ReadUInt16BigEndian(data[(10 + (2 * i))..]);
            }

            int p = 10 + (2 * contours);
            int instructionLength = BinaryPrimitives.ReadUInt16BigEndian(data[p..]);
            ReadOnlySpan<byte> instructions = data.Slice(p + 2, instructionLength);
            p += 2 + instructionLength;
            int points = contours == 0 ? 0 : ends[^1] + 1;
            var flagList = new byte[points];
            for (int i = 0; i < points;)
            {
                byte flag = data[p++];
                flagList[i++] = flag;
                if ((flag & 8) != 0)
                {
                    for (int n = data[p++]; n > 0; n--)
                    {
                        flagList[i++] = flag;
                    }
                }
            }

            var xs = new int[points];
            var ys = new int[points];
            int v = 0;
            for (int i = 0; i < points; i++)
            {
                byte f = flagList[i];
                v += (f & 2) != 0 ? ((f & 0x10) != 0 ? data[p++] : -data[p++]) : (f & 0x10) != 0 ? 0 : ReadShort(data, ref p);
                xs[i] = v;
            }

            v = 0;
            for (int i = 0; i < points; i++)
            {
                byte f = flagList[i];
                v += (f & 4) != 0 ? ((f & 0x20) != 0 ? data[p++] : -data[p++]) : (f & 0x20) != 0 ? 0 : ReadShort(data, ref p);
                ys[i] = v;
            }

            bool boxMatches = points > 0 && xs.Min() == x0 && ys.Min() == y0 && xs.Max() == x1 && ys.Max() == y1;
            if (boxMatches)
            {
                AddU16(w, contours);
            }
            else
            {
                AddU16(w, 0x7FFF);
                AddU16(w, contours);
                w.AddRange(data[2..10].ToArray());
            }

            for (int i = 0; i < contours; i++)
            {
                Write255UShort(w, i == 0 ? ends[0] : ends[i] - ends[i - 1]);
            }

            var triplets = new List<byte>();
            int lastX = 0, lastY = 0;
            for (int i = 0; i < points; i++)
            {
                int flag = Triplet(triplets, xs[i] - lastX, ys[i] - lastY);
                w.Add((byte)(flag | ((flagList[i] & 1) == 0 ? 0x80 : 0)));
                lastX = xs[i];
                lastY = ys[i];
            }

            w.AddRange(triplets);
            SplitInstructions(instructions, w, push, code);
        }

        return [.. w];
    }

    private static int ReadShort(ReadOnlySpan<byte> data, ref int p)
    {
        int value = BinaryPrimitives.ReadInt16BigEndian(data[p..]);
        p += 2;
        return value;
    }

    /// <summary>Encodes a point's deltas in the triplet form and returns its flag (without the on-curve bit).</summary>
    private static int Triplet(List<byte> w, int dx, int dy)
    {
        int ax = Math.Abs(dx), ay = Math.Abs(dy);
        int signs = (dx > 0 ? 1 : 0) | (dy > 0 ? 2 : 0);
        if (dx == 0 && ay < 1280)
        {
            w.Add((byte)(ay & 255));
            return (2 * (ay >> 8)) + (dy > 0 ? 1 : 0);
        }

        if (dy == 0 && ax < 1280)
        {
            w.Add((byte)(ax & 255));
            return 10 + (2 * (ax >> 8)) + (dx > 0 ? 1 : 0);
        }

        if (ax is >= 1 and <= 64 && ay is >= 1 and <= 64)
        {
            w.Add((byte)((((ax - 1) & 15) << 4) | ((ay - 1) & 15)));
            return 20 + ((ax - 1) & 0x30) + (((ay - 1) >> 4) << 2) + signs;
        }

        if (ax is >= 1 and <= 768 && ay is >= 1 and <= 768)
        {
            w.Add((byte)((ax - 1) & 255));
            w.Add((byte)((ay - 1) & 255));
            return 84 + (12 * ((ax - 1) >> 8)) + (4 * ((ay - 1) >> 8)) + signs;
        }

        if (ax < 4096 && ay < 4096)
        {
            w.Add((byte)(ax >> 4));
            w.Add((byte)(((ax & 15) << 4) | (ay >> 8)));
            w.Add((byte)(ay & 255));
            return 120 + signs;
        }

        w.Add((byte)(ax >> 8));
        w.Add((byte)ax);
        w.Add((byte)(ay >> 8));
        w.Add((byte)ay);
        return 124 + signs;
    }

    /// <summary>Takes the leading push instructions' values into the push block (with hop codes) and the rest into the code block.</summary>
    private static void SplitInstructions(ReadOnlySpan<byte> instructions, List<byte> glyph, List<byte> push, List<byte> code)
    {
        var values = new List<int>();
        int p = 0;
        while (p < instructions.Length)
        {
            int op = instructions[p];
            int n;
            bool words;
            if (op is >= 0xB0 and <= 0xB7)
            {
                n = op - 0xAF;
                words = false;
                p++;
            }
            else if (op is >= 0xB8 and <= 0xBF)
            {
                n = op - 0xB7;
                words = true;
                p++;
            }
            else if (op is 0x40 or 0x41 && p + 1 < instructions.Length)
            {
                n = instructions[p + 1];
                words = op == 0x41;
                p += 2;
            }
            else
            {
                break;
            }

            if (p + (n * (words ? 2 : 1)) > instructions.Length)
            {
                // A push running past the end stays in the code as it is.
                p -= op is 0x40 or 0x41 ? 2 : 1;
                break;
            }

            for (int i = 0; i < n; i++)
            {
                values.Add(words ? BinaryPrimitives.ReadInt16BigEndian(instructions[(p + (2 * i))..]) : instructions[p + i]);
            }

            p += n * (words ? 2 : 1);
        }

        Write255UShort(glyph, values.Count);
        Write255UShort(glyph, instructions.Length - p);
        code.AddRange(instructions[p..].ToArray());
        for (int i = 0; i < values.Count;)
        {
            int a = i >= 2 ? values[i - 2] : 0;
            if (i >= 2 && values[i] == a && i + 2 < values.Count && values[i + 2] == a)
            {
                if (i + 4 < values.Count && values[i + 4] == a)
                {
                    push.Add(252);
                    Write255Short(push, values[i + 1]);
                    Write255Short(push, values[i + 3]);
                    i += 5;
                }
                else
                {
                    push.Add(251);
                    Write255Short(push, values[i + 1]);
                    i += 3;
                }

                continue;
            }

            Write255Short(push, values[i]);
            i++;
        }
    }

    public static void Write255UShort(List<byte> w, int value)
    {
        if (value < 253)
        {
            w.Add((byte)value);
        }
        else if (value < 506)
        {
            w.Add(255);
            w.Add((byte)(value - 253));
        }
        else if (value < 762)
        {
            w.Add(254);
            w.Add((byte)(value - 506));
        }
        else
        {
            w.Add(253);
            AddU16(w, value);
        }
    }

    public static void Write255Short(List<byte> w, int value)
    {
        int magnitude = Math.Abs(value);
        if (magnitude >= 756)
        {
            w.Add(253);
            AddU16(w, value);
            return;
        }

        if (value < 0)
        {
            w.Add(250);
        }

        if (magnitude < 250)
        {
            w.Add((byte)magnitude);
        }
        else if (magnitude < 506)
        {
            w.Add(255);
            w.Add((byte)(magnitude - 250));
        }
        else
        {
            w.Add(254);
            w.Add((byte)(magnitude - 500));
        }
    }

    // ---- LZCOMP ----

    public static byte[] Lzcomp(byte[] data, bool runLength)
    {
        byte[] input = runLength ? RunLengthEncode(data) : data;
        var bits = new List<bool> { runLength };
        var distances = new Woff.Lzcomp.AdaptiveHuffman(8);
        var lengths = new Woff.Lzcomp.AdaptiveHuffman(8);
        for (int i = 23; i >= 0; i--)
        {
            bits.Add(((input.Length >> i) & 1) != 0);
        }

        int ranges = Woff.Lzcomp.DistanceRanges(input.Length);
        int dup2 = 256 + (8 * ranges);
        var symbols = new Woff.Lzcomp.AdaptiveHuffman(dup2 + 3);
        int pre = Woff.Lzcomp.PreloadSize;
        byte[] window = new byte[pre + input.Length];
        Woff.Lzcomp.Preload(window);
        input.CopyTo(window, pre);
        long maxDistance = 1L << (3 * ranges);
        var heads = new Dictionary<int, List<int>>();
        void Index(int at)
        {
            if (at + 2 < window.Length)
            {
                int key = window[at] | (window[at + 1] << 8) | (window[at + 2] << 16);
                if (!heads.TryGetValue(key, out List<int>? list))
                {
                    heads[key] = list = [];
                }

                list.Add(at);
            }
        }

        for (int i = 0; i < pre; i++)
        {
            Index(i);
        }

        int pos = pre;
        while (pos < window.Length)
        {
            int bestLength = 0, bestDistance = 0;
            if (pos + 2 < window.Length && heads.TryGetValue(window[pos] | (window[pos + 1] << 8) | (window[pos + 2] << 16), out List<int>? candidates))
            {
                for (int c = candidates.Count - 1; c >= Math.Max(0, candidates.Count - 32); c--)
                {
                    int from = candidates[c];
                    int distance = pos - from;
                    if (distance > maxDistance)
                    {
                        break;
                    }

                    // The decoder counts the distance back from the copy's last byte, so a copy never overlaps itself.
                    int length = 0;
                    while (pos + length < window.Length && length < distance && window[from + length] == window[pos + length] && length < 4000)
                    {
                        length++;
                    }

                    if (length > bestLength)
                    {
                        bestLength = length;
                        bestDistance = distance;
                    }
                }
            }

            if (bestLength >= 3)
            {
                // The decoder starts the copy distance + length - 1 back.
                int distance = bestDistance - bestLength + 1;
                int coded = bestLength - 2 - (distance >= Woff.Lzcomp.MaxTwoByteDistance ? 1 : 0);
                var digits = new List<int>();
                do
                {
                    digits.Insert(0, coded & 3);
                    coded >>= 2;
                }
                while (coded > 0);

                var distanceDigits = new List<int>();
                int d = distance - 1;
                do
                {
                    distanceDigits.Insert(0, d & 7);
                    d >>= 3;
                }
                while (d > 0);

                symbols.Write(256 + (8 * (distanceDigits.Count - 1)) + digits[0] + (digits.Count > 1 ? 4 : 0), bits);
                for (int i = 1; i < digits.Count; i++)
                {
                    lengths.Write(digits[i] + (i < digits.Count - 1 ? 4 : 0), bits);
                }

                foreach (int digit in distanceDigits)
                {
                    distances.Write(digit, bits);
                }

                for (int i = 0; i < bestLength; i++)
                {
                    Index(pos++);
                }

                continue;
            }

            byte b = window[pos];
            if (window[pos - 2] == b)
            {
                symbols.Write(dup2, bits);
            }
            else if (window[pos - 4] == b)
            {
                symbols.Write(dup2 + 1, bits);
            }
            else if (window[pos - 6] == b)
            {
                symbols.Write(dup2 + 2, bits);
            }
            else
            {
                symbols.Write(b, bits);
            }

            Index(pos++);
        }

        byte[] bytes = new byte[(bits.Count + 7) / 8];
        for (int i = 0; i < bits.Count; i++)
        {
            if (bits[i])
            {
                bytes[i / 8] |= (byte)(0x80 >> (i % 8));
            }
        }

        return bytes;
    }

    public static byte[] RunLengthEncode(byte[] data)
    {
        var counts = new int[256];
        foreach (byte b in data)
        {
            counts[b]++;
        }

        byte escape = (byte)Array.IndexOf(counts, counts.Min());
        var w = new List<byte> { escape };
        for (int i = 0; i < data.Length;)
        {
            int run = 1;
            while (i + run < data.Length && data[i + run] == data[i] && run < 255)
            {
                run++;
            }

            if (run >= 4)
            {
                w.Add(escape);
                w.Add((byte)run);
                w.Add(data[i]);
                i += run;
            }
            else if (data[i] == escape)
            {
                w.Add(escape);
                w.Add(0);
                i++;
            }
            else
            {
                w.Add(data[i]);
                i++;
            }
        }

        return [.. w];
    }
}
