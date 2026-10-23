namespace Egami.Video.Audio.Codecs.G711;

/// <summary>
/// The G.711 expansion tables (ITU-T G.711, tables 1 and 2), built from the segment and step rules, as 16-bit values: μ-law's
/// 14-bit and A-law's 13-bit decision values shifted to the top of the 16 bits.
/// </summary>
internal static class G711Tables
{
    /// <summary>The μ-law bias that makes the segments line up: 33 in 14-bit units, 0x84 after the shift.</summary>
    private const int MuLawBias = 0x84;

    public static short[] MuLaw { get; } = Build(ExpandMuLaw);

    public static short[] ALaw { get; } = Build(ExpandALaw);

    /// <summary>Expands one μ-law byte. Bytes are stored inverted; then sign, 3-bit segment, 4-bit step.</summary>
    public static short ExpandMuLaw(byte value)
    {
        int u = ~value & 0xFF;
        int segment = (u >> 4) & 7;
        int magnitude = ((((u & 0x0F) << 3) + MuLawBias) << segment) - MuLawBias;
        return (short)((u & 0x80) != 0 ? -magnitude : magnitude);
    }

    /// <summary>Expands one A-law byte. Even bits are stored inverted (XOR 0x55); then sign (1 = positive), segment, step.</summary>
    public static short ExpandALaw(byte value)
    {
        int a = value ^ 0x55;
        int segment = (a >> 4) & 7;
        int step = (a & 0x0F) << 4;
        int magnitude = segment == 0 ? step + 8 : (step + 0x108) << (segment - 1);
        return (short)((a & 0x80) != 0 ? magnitude : -magnitude);
    }

    private static short[] Build(Func<byte, short> expand)
    {
        short[] table = new short[256];
        for (int i = 0; i < 256; i++)
        {
            table[i] = expand((byte)i);
        }

        return table;
    }
}
