namespace Egami.Video.Codecs.Ffv1;

/// <summary>
/// The CRC-32 that FFV1 protects its configuration record and slices with (RFC 9043 section 4.9.3): the IEEE polynomial
/// 0x04C11DB7, most significant bit first, initial value 0, no inversion before or after. A block that ends in its own parity
/// has a remainder of zero.
/// </summary>
internal static class Ffv1Crc
{
    private static readonly uint[] Table = Build();

    /// <summary>Returns the CRC remainder of <paramref name="data"/>.</summary>
    public static uint Compute(ReadOnlySpan<byte> data, uint crc = 0)
    {
        uint[] table = Table;
        foreach (byte b in data)
        {
            crc = (crc << 8) ^ table[(crc >> 24) ^ b];
        }

        return crc;
    }

    private static uint[] Build()
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i << 24;
            for (int bit = 0; bit < 8; bit++)
            {
                c = (c & 0x80000000) != 0 ? (c << 1) ^ 0x04C11DB7 : c << 1;
            }

            table[i] = c;
        }

        return table;
    }
}
