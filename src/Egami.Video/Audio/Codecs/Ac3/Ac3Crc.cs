namespace Egami.Video.Audio.Codecs.Ac3;

/// <summary>
/// AC-3's two frame CRCs (ATSC A/52:2018 section 7.10.1): generator x^16 + x^15 + x^2 + 1, register cleared, bits shifted in most
/// significant first from the word after the sync word. crc1 is good when the register is zero after the first 5/8 of the frame,
/// crc2 when it is zero after the whole frame.
/// </summary>
internal static class Ac3Crc
{
    private static readonly ushort[] Table = MakeTable();

    /// <summary>Checks both CRCs of a whole AC-3 frame.</summary>
    /// <param name="frame">The frame, sync word first.</param>
    /// <param name="first">Whether crc1 matches.</param>
    /// <param name="second">Whether crc2 matches.</param>
    public static void Check(ReadOnlySpan<byte> frame, out bool first, out bool second)
    {
        int words = frame.Length / 2;
        int fiveEighths = (words >> 1) + (words >> 3);
        ushort crc = Update(0, frame[2..(fiveEighths * 2)]);
        first = crc == 0;

        // crc2 runs on from the 5/8 point over the rest; it is zero only if the whole frame after the sync word checks.
        second = Update(crc, frame[(fiveEighths * 2)..(words * 2)]) == 0;
    }

    private static ushort Update(ushort crc, ReadOnlySpan<byte> data)
    {
        ushort[] table = Table;
        foreach (byte b in data)
        {
            crc = (ushort)((crc << 8) ^ table[(crc >> 8) ^ b]);
        }

        return crc;
    }

    private static ushort[] MakeTable()
    {
        ushort[] table = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            int crc = i << 8;
            for (int b = 0; b < 8; b++)
            {
                crc = (crc & 0x8000) != 0 ? (crc << 1) ^ 0x8005 : crc << 1;
            }

            table[i] = (ushort)crc;
        }

        return table;
    }
}
