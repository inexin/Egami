using System.Buffers.Binary;

namespace Egami.Fonts.Tests;

/// <summary>
/// Test strikes for EBLC and EBDT (2.7.6 task 22), written into a TrueType font: at 13 ppem a 1-bit strike with 'A' in image format
/// 1 (rows on whole bytes) and 'B' in format 2 (rows packed), and at 14 ppem a 2-bit gray strike with 'A'. Shared with
/// Egami.Drawing.Tests.
/// </summary>
internal static class EmbeddedStrikes
{
    // 'A' at 13 ppem: 3 × 4 pixels, one bit each.
    public static readonly byte[,] A1 = { { 0, 1, 0 }, { 1, 0, 1 }, { 1, 1, 1 }, { 1, 0, 1 } };

    // 'B' at 13 ppem: 5 × 2 pixels.
    public static readonly byte[,] B1 = { { 1, 1, 0, 1, 1 }, { 0, 1, 1, 1, 0 } };

    // 'A' at 14 ppem: 2 × 2 pixels of 2 bits.
    public static readonly byte[,] A2 = { { 0, 1 }, { 2, 3 } };

    /// <summary>A font with the test strikes added as EBLC and EBDT tables.</summary>
    public static byte[] WithStrikes(byte[] font)
    {
        Typeface face = Typeface.FromMemory(font);
        ushort a = face.GetGlyph('A'), b = face.GetGlyph('B');
        var ebdt = new List<byte> { 0, 2, 0, 0 };

        // One glyph's image data in EBDT: small metrics (height, width, bearing x, bearing y, advance), then the rows.
        int Image(byte[,] pixels, int depth, bool packed, int bearingX, int bearingY, int advance)
        {
            int at = ebdt.Count;
            int height = pixels.GetLength(0), width = pixels.GetLength(1);
            ebdt.AddRange([(byte)height, (byte)width, (byte)bearingX, (byte)bearingY, (byte)advance]);
            var bits = new List<int>();
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    for (int k = depth - 1; k >= 0; k--)
                    {
                        bits.Add((pixels[y, x] >> k) & 1);
                    }
                }

                while (!packed && bits.Count % 8 != 0)
                {
                    bits.Add(0);
                }
            }

            for (int i = 0; i < bits.Count; i += 8)
            {
                int value = 0;
                for (int k = 0; k < 8; k++)
                {
                    value = (value << 1) | (i + k < bits.Count ? bits[i + k] : 0);
                }

                ebdt.Add((byte)value);
            }

            return at;
        }

        int imageA1 = Image(A1, 1, false, 1, 9, 7), endA1 = ebdt.Count;
        int imageB1 = Image(B1, 1, true, 0, 8, 6), endB1 = ebdt.Count;
        int imageA2 = Image(A2, 2, false, 0, 6, 5), endA2 = ebdt.Count;

        // EBLC: two sizes, each with an index subtable array of format 1 subtables (one glyph each).
        var eblc = new List<byte>();
        void U16(List<byte> to, int v) => to.AddRange([(byte)(v >> 8), (byte)v]);
        void U32(List<byte> to, int v) => to.AddRange([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);
        U32(eblc, 0x00020000);
        U32(eblc, 2);
        int sizesAt = eblc.Count;
        eblc.AddRange(new byte[96]);

        // Writes an index subtable array of (glyph, image format, image start, image end) at the end; returns its offset.
        int Array(params (ushort Glyph, int Format, int Start, int End)[] entries)
        {
            int arrayAt = eblc.Count;
            eblc.AddRange(new byte[8 * entries.Length]);
            for (int i = 0; i < entries.Length; i++)
            {
                int sub = eblc.Count;
                U16(eblc, 1);
                U16(eblc, entries[i].Format);
                U32(eblc, entries[i].Start);
                U32(eblc, 0);
                U32(eblc, entries[i].End - entries[i].Start);
                byte[] record = new byte[8];
                BinaryPrimitives.WriteUInt16BigEndian(record, entries[i].Glyph);
                BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(2), entries[i].Glyph);
                BinaryPrimitives.WriteInt32BigEndian(record.AsSpan(4), sub - arrayAt);
                for (int k = 0; k < 8; k++)
                {
                    eblc[arrayAt + (i * 8) + k] = record[k];
                }
            }

            return arrayAt;
        }

        void Size(int index, int arrayAt, int tables, int start, int end, int ppem, int depth)
        {
            byte[] size = new byte[48];
            BinaryPrimitives.WriteInt32BigEndian(size, arrayAt);
            BinaryPrimitives.WriteInt32BigEndian(size.AsSpan(8), tables);
            BinaryPrimitives.WriteUInt16BigEndian(size.AsSpan(40), (ushort)start);
            BinaryPrimitives.WriteUInt16BigEndian(size.AsSpan(42), (ushort)end);
            (size[44], size[45], size[46], size[47]) = ((byte)ppem, (byte)ppem, (byte)depth, 1);
            for (int k = 0; k < 48; k++)
            {
                eblc[sizesAt + (index * 48) + k] = size[k];
            }
        }

        (ushort first, ushort second) = a < b ? (a, b) : (b, a);
        int array13 = Array(a < b ? [(a, 1, imageA1, endA1), (b, 2, imageB1, endB1)] : [(b, 2, imageB1, endB1), (a, 1, imageA1, endA1)]);
        Size(0, array13, 2, first, second, 13, 1);
        int array14 = Array((a, 1, imageA2, endA2));
        Size(1, array14, 1, a, a, 14, 2);
        return AddTables(font, ("EBDT", [.. ebdt]), ("EBLC", [.. eblc]));
    }

    // Rebuilds an sfnt with extra tables: records sorted by tag, each table on a 4-byte boundary (checksums left at 0).
    private static byte[] AddTables(byte[] font, params (string Tag, byte[] Data)[] extra)
    {
        int count = BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(4));
        var tables = new List<(string Tag, byte[] Data)>(extra);
        for (int i = 0; i < count; i++)
        {
            int record = 12 + (16 * i);
            string tag = System.Text.Encoding.ASCII.GetString(font, record, 4);
            int offset = BinaryPrimitives.ReadInt32BigEndian(font.AsSpan(record + 8)), length = BinaryPrimitives.ReadInt32BigEndian(font.AsSpan(record + 12));
            tables.Add((tag, font.AsSpan(offset, length).ToArray()));
        }

        tables.Sort((x, y) => string.CompareOrdinal(x.Tag, y.Tag));
        var result = new List<byte>(font.AsSpan(0, 12).ToArray());
        result[4] = (byte)(tables.Count >> 8);
        result[5] = (byte)tables.Count;
        int at = 12 + (16 * tables.Count);
        var data = new List<byte>();
        foreach ((string tag, byte[] table) in tables)
        {
            byte[] record = new byte[16];
            System.Text.Encoding.ASCII.GetBytes(tag, record);
            BinaryPrimitives.WriteInt32BigEndian(record.AsSpan(8), at + data.Count);
            BinaryPrimitives.WriteInt32BigEndian(record.AsSpan(12), table.Length);
            result.AddRange(record);
            data.AddRange(table);
            while (data.Count % 4 != 0)
            {
                data.Add(0);
            }
        }

        result.AddRange(data);
        return [.. result];
    }

}
