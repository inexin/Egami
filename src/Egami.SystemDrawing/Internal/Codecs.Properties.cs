using System.Buffers.Binary;

using Egami.SystemDrawing.Imaging;

namespace Egami.SystemDrawing.Internal;

/// <summary>The property items GDI+ reads straight from a file: every tag of a TIFF's first IFD, and a JPEG's quantization tables.</summary>
internal static partial class Codecs
{
    /// <summary>The order T.81 stores the 64 coefficients of a table in (zigzag), as natural row-major positions.</summary>
    private static ReadOnlySpan<byte> ZigZag =>
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
    ];

    /// <summary>
    /// The tags of a TIFF's first IFD in file order, as GDI+ lists them: the tag, its type, and its values (read from the offset
    /// when they do not fit in the entry) made little-endian.
    /// </summary>
    private static List<PropertyItem> TiffTags(byte[] data)
    {
        List<PropertyItem> items = [];
        if (data.Length < 8 || !(data[0] == data[1] && data[0] is (byte)'I' or (byte)'M'))
        {
            return items;
        }

        bool big = data[0] == (byte)'M';
        uint U32(int at) => big ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at)) : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
        ushort U16(int at) => big ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at)) : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at));
        if (U16(2) != 42)
        {
            return items;
        }

        long ifd = U32(4);
        if (ifd <= 0 || ifd + 2 > data.Length)
        {
            return items;
        }

        int count = U16((int)ifd);
        for (int i = 0; i < count && ifd + 2 + ((i + 1) * 12) <= data.Length; i++)
        {
            int entry = (int)ifd + 2 + (i * 12);
            ushort tag = U16(entry), type = U16(entry + 2);
            uint n = U32(entry + 4);
            int size = type switch { 1 or 2 or 6 or 7 => 1, 3 or 8 => 2, 4 or 9 or 11 or 13 => 4, 5 or 10 or 12 => 8, _ => 0 };
            long length = (long)size * n;
            if (size == 0 || length > int.MaxValue)
            {
                continue;
            }

            long at = length <= 4 ? entry + 8 : U32(entry + 8);
            if (at + length > data.Length)
            {
                continue;
            }

            byte[] value = data.AsSpan((int)at, (int)length).ToArray();
            if (big)
            {
                int unit = type is 5 or 10 ? 4 : size;
                for (int v = 0; unit > 1 && v + unit <= value.Length; v += unit)
                {
                    Array.Reverse(value, v, unit);
                }
            }

            items.Add(new PropertyItem { Id = tag, Type = (short)type, Len = value.Length, Value = value });
        }

        return items;
    }

    /// <summary>
    /// A baseline JPEG's quantization tables in natural order as 16-bit values: table 1 (chrominance, 0x5091) then table 0
    /// (luminance, 0x5090), the order GDI+ lists them in.
    /// </summary>
    private static List<PropertyItem> QuantizationTables(byte[] data)
    {
        byte[]?[] tables = new byte[4][];
        int at = 2;
        while (at + 4 <= data.Length && data[at] == 0xFF)
        {
            byte marker = data[at + 1];
            int length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at + 2));
            if (marker == 0xDA || at + 2 + length > data.Length)
            {
                break;
            }

            if (marker == 0xDB)
            {
                int p = at + 4, end = at + 2 + length;
                while (p < end)
                {
                    int precision = data[p] >> 4, id = data[p] & 3;
                    p++;
                    byte[] table = new byte[128];
                    for (int k = 0; k < 64 && p < end; k++)
                    {
                        int value = precision == 0 ? data[p] : BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p));
                        p += precision == 0 ? 1 : 2;
                        BinaryPrimitives.WriteUInt16LittleEndian(table.AsSpan(ZigZag[k] * 2), (ushort)value);
                    }

                    tables[id] = table;
                }
            }

            at += 2 + length;
        }

        List<PropertyItem> items = [];
        if (tables[1] is byte[] chroma)
        {
            items.Add(new PropertyItem { Id = 0x5091, Type = 3, Len = 128, Value = chroma });
        }

        if (tables[0] is byte[] luma)
        {
            items.Add(new PropertyItem { Id = 0x5090, Type = 3, Len = 128, Value = luma });
        }

        return items;
    }
}
