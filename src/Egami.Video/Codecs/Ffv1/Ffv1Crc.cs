namespace Egami.Video.Codecs.Ffv1;

/// <summary>
/// The CRC-32 that FFV1 protects its configuration record and slices with (RFC 9043 section 4.9.3): the IEEE polynomial
/// 0x04C11DB7, most significant bit first, initial value 0, no inversion before or after. A block that ends in its own parity
/// has a remainder of zero.
/// </summary>
internal static class Ffv1Crc
{
    // Eight tables of 256: table k gives the remainder of a byte followed by k zero bytes, so eight bytes are folded at a time.
    private static readonly uint[] Tables = Build();

    /// <summary>Returns the CRC remainder of <paramref name="data"/>.</summary>
    public static uint Compute(ReadOnlySpan<byte> data, uint crc = 0)
    {
        uint[] t = Tables;
        int i = 0;
        for (; i + 8 <= data.Length; i += 8)
        {
            uint high = crc ^ System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data[i..]);
            uint low = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data[(i + 4)..]);
            crc = t[(7 * 256) + (high >> 24)] ^ t[(6 * 256) + ((high >> 16) & 255)] ^ t[(5 * 256) + ((high >> 8) & 255)] ^ t[(4 * 256) + (high & 255)]
                ^ t[(3 * 256) + (low >> 24)] ^ t[(2 * 256) + ((low >> 16) & 255)] ^ t[256 + ((low >> 8) & 255)] ^ t[low & 255];
        }

        for (; i < data.Length; i++)
        {
            crc = (crc << 8) ^ t[(crc >> 24) ^ data[i]];
        }

        return crc;
    }

    private static uint[] Build()
    {
        uint[] tables = new uint[8 * 256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i << 24;
            for (int bit = 0; bit < 8; bit++)
            {
                c = (c & 0x80000000) != 0 ? (c << 1) ^ 0x04C11DB7 : c << 1;
            }

            tables[i] = c;
        }

        for (int k = 1; k < 8; k++)
        {
            for (int i = 0; i < 256; i++)
            {
                uint previous = tables[((k - 1) * 256) + i];
                tables[(k * 256) + i] = (previous << 8) ^ tables[previous >> 24];
            }
        }

        return tables;
    }
}
