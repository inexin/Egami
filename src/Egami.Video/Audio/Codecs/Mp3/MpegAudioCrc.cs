namespace Egami.Video.Audio.Codecs.Mp3;

/// <summary>
/// The CRC-16 of an MPEG audio frame (ISO/IEC 11172-3 section 2.4.3.1): polynomial x^16 + x^15 + x^2 + 1, start value 0xFFFF,
/// over the last two header bytes and the protected bits after the CRC word (Layer III's side information, Layer I's bit
/// allocation, Layer II's bit allocation and scale factor selection).
/// </summary>
internal static class MpegAudioCrc
{
    private static readonly ushort[] s_table = BuildTable();

    /// <summary>Returns whether the CRC stored after the header matches. <paramref name="frame"/> starts at the header.</summary>
    /// <param name="frame">The frame, header first.</param>
    /// <param name="protectedBits">How many bits after the CRC word it covers.</param>
    /// <returns>Whether it matches; <see langword="true"/> when the frame is too short to tell.</returns>
    public static bool Check(ReadOnlySpan<byte> frame, int protectedBits)
    {
        if (frame.Length < 6 + ((protectedBits + 7) >> 3))
        {
            return true;
        }

        ushort crc = 0xFFFF;
        crc = Byte(crc, frame[2]);
        crc = Byte(crc, frame[3]);
        ReadOnlySpan<byte> data = frame[6..];
        int whole = protectedBits >> 3;
        for (int i = 0; i < whole; i++)
        {
            crc = Byte(crc, data[i]);
        }

        int rest = protectedBits & 7;
        if (rest > 0)
        {
            int b = data[whole];
            for (int i = 0; i < rest; i++)
            {
                bool bit = ((b >> (7 - i)) & 1) != ((crc >> 15) & 1);
                crc = (ushort)(crc << 1);
                if (bit)
                {
                    crc ^= 0x8005;
                }
            }
        }

        return crc == ((frame[4] << 8) | frame[5]);
    }

    /// <summary>Computes the CRC of whole bytes, for writers and tests.</summary>
    public static ushort Compute(ReadOnlySpan<byte> headerTail, ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (byte b in headerTail)
        {
            crc = Byte(crc, b);
        }

        foreach (byte b in data)
        {
            crc = Byte(crc, b);
        }

        return crc;
    }

    private static ushort Byte(ushort crc, byte b) => (ushort)((crc << 8) ^ s_table[((crc >> 8) ^ b) & 0xFF]);

    private static ushort[] BuildTable()
    {
        ushort[] table = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            int c = i << 8;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 0x8000) != 0 ? (c << 1) ^ 0x8005 : c << 1;
            }

            table[i] = (ushort)c;
        }

        return table;
    }
}
