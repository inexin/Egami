namespace Egami.Formats;

/// <summary>The Adler-32 checksum from RFC 1950, section 8.2, which ends every zlib stream.</summary>
internal static class Adler32
{
    private const uint Modulus = 65521;

    /// <summary>The largest number of bytes that can be summed before the 32-bit sums must be reduced (RFC 1950 and zlib's NMAX).</summary>
    private const int BlockSize = 5552;

    /// <summary>Continues a checksum. Start with 1.</summary>
    internal static uint Update(uint adler, ReadOnlySpan<byte> data)
    {
        uint a = adler & 0xFFFF;
        uint b = adler >> 16;
        while (!data.IsEmpty)
        {
            ReadOnlySpan<byte> block = data[..Math.Min(BlockSize, data.Length)];
            foreach (byte value in block)
            {
                a += value;
                b += a;
            }

            a %= Modulus;
            b %= Modulus;
            data = data[block.Length..];
        }

        return (b << 16) | a;
    }
}
