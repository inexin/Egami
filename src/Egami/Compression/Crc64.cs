namespace Egami.Compression;

/// <summary>CRC-64 as ECMA-182 defines it, bit-reflected, the xz format's CRC64 check (xz file format, section 3.4).</summary>
internal static class Crc64
{
    private const ulong Polynomial = 0xC96C5795D7870F42;

    private static readonly ulong[] Table = BuildTable();

    /// <summary>Computes the CRC-64 of <paramref name="data"/>.</summary>
    public static ulong Compute(ReadOnlySpan<byte> data)
    {
        ulong[] table = Table;
        ulong crc = ulong.MaxValue;
        foreach (byte b in data)
        {
            crc = table[(byte)(crc ^ b)] ^ (crc >> 8);
        }

        return ~crc;
    }

    private static ulong[] BuildTable()
    {
        ulong[] table = new ulong[256];
        for (uint i = 0; i < 256; i++)
        {
            ulong crc = i;
            for (int k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ Polynomial : crc >> 1;
            }

            table[i] = crc;
        }

        return table;
    }
}
