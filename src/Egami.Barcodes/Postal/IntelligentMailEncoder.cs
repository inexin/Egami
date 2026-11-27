namespace Egami.Barcodes.Postal;

/// <summary>
/// The USPS Intelligent Mail barcode (USPS-B-3200 Rev H): the 20-digit tracking code and the 0, 5, 9 or 11-digit routing code
/// become a 102-bit number, which gives an 11-bit frame check sequence (section 2.2.2) and ten codewords, A in base 659 and J
/// in base 636 with the others in base 1365 (2.2.3). Codeword J is doubled and A carries the check's top bit (2.2.4); each
/// codeword becomes a 13-bit character of the 5-of-13 and 2-of-13 tables the spec's Appendix D code makes, inverted where the
/// check's other bits say (2.2.5), and the 130 bits of the characters are the descenders and ascenders of 65 bars, as Table 22
/// maps them (2.2.6).
/// </summary>
internal static class IntelligentMailEncoder
{
    /// <summary>
    /// The descender and ascender of each of the 65 bars as character (A to J) and bit (0 to 12), from USPS-B-3200 Table 22.
    /// </summary>
    private const string BarMap =
        "H2 E3, B10 A0, J12 C8, F5 G11, I9 D1, A1 F12, C5 B8, E4 J11, G3 I10, D9 H6, F11 B4, I5 C12, J10 A2, " +
        "H1 G7, D6 E9, A3 I6, G4 C7, B1 J9, H10 F2, E0 D8, G2 A4, I11 B0, J8 D12, C6 H7, F1 E10, B12 G9, " +
        "H3 I0, F8 J7, E6 C10, D4 A5, I4 F7, H11 B9, G0 J6, A6 E8, C1 D2, F9 I12, E11 G1, J5 H4, D3 B2, " +
        "A7 C0, B3 E1, G10 D5, I7 J4, C11 F6, A8 H12, E2 I1, F10 D0, J3 A9, G5 C4, H8 B7, F0 E5, C3 A10, " +
        "G12 J2, D11 B6, I8 H9, F4 A11, B5 C2, J1 E12, I3 G6, H0 D7, E7 H5, A12 B11, C9 J0, G8 F3, D10 I2";

    private static readonly ushort[] s_table5 = Table(5, 1287);
    private static readonly ushort[] s_table2 = Table(2, 78);
    private static readonly (byte Character, byte Bit)[] s_descenders = Map(0);
    private static readonly (byte Character, byte Bit)[] s_ascenders = Map(1);

    /// <summary>Gets the 102-bit number of a tracking code and a routing code (section 2.2.1).</summary>
    public static UInt128 Binary(ReadOnlySpan<char> tracking, ReadOnlySpan<char> routing)
    {
        UInt128 value = 0;
        foreach (char c in routing)
        {
            value = (value * 10) + (uint)(c - '0');
        }

        value += routing.Length switch
        {
            5 => 1,
            9 => 100001,
            11 => 1000100001,
            _ => 0,
        };
        value = (value * 10) + (uint)(tracking[0] - '0');
        value = (value * 5) + (uint)(tracking[1] - '0');
        foreach (char c in tracking[2..])
        {
            value = (value * 10) + (uint)(c - '0');
        }

        return value;
    }

    /// <summary>Gets the 11-bit frame check sequence of the 102 bits: generator 0xF35, start 0x7FF, top bit first (section 2.2.2).</summary>
    public static int FrameCheck(UInt128 binary)
    {
        int check = 0x7FF;
        for (int bit = 101; bit >= 0; bit--)
        {
            int data = (int)(binary >> bit) & 1;
            check = (((check >> 10) ^ data) & 1) != 0 ? (check << 1) ^ 0xF35 : check << 1;
            check &= 0x7FF;
        }

        return check;
    }

    /// <summary>Gets the ten codewords A to J, with J doubled and A raised by 659 when the check's top bit is set (2.2.3, 2.2.4).</summary>
    public static int[] Codewords(UInt128 binary, int frameCheck)
    {
        int[] codewords = new int[10];
        codewords[9] = (int)(binary % 636) * 2;
        binary /= 636;
        for (int i = 8; i > 0; i--)
        {
            codewords[i] = (int)(binary % 1365);
            binary /= 1365;
        }

        codewords[0] = (int)binary + ((frameCheck & 0x400) != 0 ? 659 : 0);
        return codewords;
    }

    /// <summary>Gets the 65 bars of a tracking code and a routing code.</summary>
    public static FourStateBar[] Bars(ReadOnlySpan<char> tracking, ReadOnlySpan<char> routing)
    {
        UInt128 binary = Binary(tracking, routing);
        int check = FrameCheck(binary);
        int[] codewords = Codewords(binary, check);
        Span<int> characters = stackalloc int[10];
        for (int i = 0; i < 10; i++)
        {
            int character = codewords[i] < 1287 ? s_table5[codewords[i]] : s_table2[codewords[i] - 1287];
            characters[i] = (check & (1 << i)) != 0 ? ~character & 0x1FFF : character;
        }

        var bars = new FourStateBar[65];
        for (int i = 0; i < 65; i++)
        {
            bool descender = ((characters[s_descenders[i].Character] >> s_descenders[i].Bit) & 1) != 0;
            bool ascender = ((characters[s_ascenders[i].Character] >> s_ascenders[i].Bit) & 1) != 0;
            bars[i] = (ascender, descender) switch
            {
                (true, true) => FourStateBar.Full,
                (true, false) => FourStateBar.Ascender,
                (false, true) => FourStateBar.Descender,
                _ => FourStateBar.Tracker,
            };
        }

        return bars;
    }

    /// <summary>
    /// Makes the table of the 13-bit characters with <paramref name="bits"/> bits set, as the spec's InitializeNof13Table does:
    /// each character and its bit reversal in turn from the start, characters that are their own reversal from the end.
    /// </summary>
    private static ushort[] Table(int bits, int length)
    {
        var table = new ushort[length];
        int lower = 0, upper = length - 1;
        for (int count = 0; count < 8192; count++)
        {
            if (System.Numerics.BitOperations.PopCount((uint)count) != bits)
            {
                continue;
            }

            int reverse = (int)(ReverseBits((uint)count) >> 19);
            if (reverse < count)
            {
                continue;
            }

            if (reverse == count)
            {
                table[upper--] = (ushort)count;
            }
            else
            {
                table[lower++] = (ushort)count;
                table[lower++] = (ushort)reverse;
            }
        }

        System.Diagnostics.Debug.Assert(lower == upper + 1);
        return table;
    }

    private static uint ReverseBits(uint value)
    {
        uint reverse = 0;
        for (int i = 0; i < 32; i++)
        {
            reverse = (reverse << 1) | (value & 1);
            value >>= 1;
        }

        return reverse;
    }

    private static (byte Character, byte Bit)[] Map(int column) =>
        [.. BarMap.Split(", ").Select(pair => pair.Split(' ')[column]).Select(c => ((byte)(c[0] - 'A'), byte.Parse(c[1..], System.Globalization.CultureInfo.InvariantCulture)))];
}
