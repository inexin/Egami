using Egami.Barcodes.ErrorCorrection;

namespace Egami.Barcodes.Postal;

/// <summary>
/// The Australia Post 4-State Customer Barcode (Australia Post Customer Barcoding Technical Specifications): bar values 0 (full),
/// 1 (ascender), 2 (descender) and 3 (tracker). Two start bars 1 3, the Format Control Code and the eight-digit DPID in the N
/// table, the customer information in the N or C table, filler bars of value 3, four Reed-Solomon check symbols of three bars
/// over GF(64) and two stop bars 1 3.
/// </summary>
internal static class AustraliaPostEncoder
{
    /// <summary>The C table's characters, in the order of the bar values in <see cref="CTable"/>.</summary>
    public const string CCharacters = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789 #";

    /// <summary>The three bars of each C table character, from the spec's C Encoding Table.</summary>
    private const string CTable =
        "000 001 002 010 011 012 020 021 022 100 101 102 110 111 112 120 121 122 200 201 202 210 211 212 220 221 " +
        "023 030 031 032 033 103 113 123 130 131 132 133 203 213 223 230 231 232 233 303 313 323 330 331 332 333 " +
        "222 300 301 302 310 311 312 320 321 322 003 013";

    /// <summary>Gets the number of bars before the check bars: start, FCC, DPID and the customer information or filler.</summary>
    public static int DataBars(AustraliaPostFormat format) => format switch
    {
        AustraliaPostFormat.Customer2 => 38,
        AustraliaPostFormat.Customer3 => 53,
        _ => 23,
    };

    /// <summary>Gets the bar values of the whole symbol.</summary>
    public static byte[] Values(AustraliaPostFormat format, ReadOnlySpan<char> dpid, ReadOnlySpan<char> information, AustraliaPostEncoding encoding, out int[] check)
    {
        int data = DataBars(format);
        var values = new byte[data + 14];
        values.AsSpan(0, data).Fill(3);
        values[0] = 1;
        int i = 2;
        int fcc = (int)format;
        N(values, ref i, fcc / 10);
        N(values, ref i, fcc % 10);
        foreach (char c in dpid)
        {
            N(values, ref i, c - '0');
        }

        foreach (char c in information)
        {
            if (encoding == AustraliaPostEncoding.N)
            {
                N(values, ref i, c - '0');
            }
            else
            {
                int index = CCharacters.IndexOf(c, StringComparison.Ordinal) * 4;
                for (int bar = 0; bar < 3; bar++)
                {
                    values[i++] = (byte)(CTable[index + bar] - '0');
                }
            }
        }

        Span<int> triples = stackalloc int[(data - 2) / 3];
        for (int t = 0; t < triples.Length; t++)
        {
            triples[t] = (values[2 + (3 * t)] << 4) | (values[3 + (3 * t)] << 2) | values[4 + (3 * t)];
        }

        check = new int[4];
        ReedSolomon.Encode(GaloisField.Aztec6, triples, check);
        i = data;
        foreach (int symbol in check)
        {
            values[i++] = (byte)(symbol >> 4);
            values[i++] = (byte)((symbol >> 2) & 3);
            values[i++] = (byte)(symbol & 3);
        }

        values[i++] = 1;
        values[i] = 3;
        return values;
    }

    /// <summary>Gets the bars of bar values.</summary>
    public static FourStateBar[] Bars(ReadOnlySpan<byte> values)
    {
        var bars = new FourStateBar[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            bars[i] = values[i] switch
            {
                0 => FourStateBar.Full,
                1 => FourStateBar.Ascender,
                2 => FourStateBar.Descender,
                _ => FourStateBar.Tracker,
            };
        }

        return bars;
    }

    // The N table: 0 to 9 as 00 01 02 10 11 12 20 21 22 30.
    private static void N(Span<byte> values, ref int i, int digit)
    {
        values[i++] = (byte)(digit / 3);
        values[i++] = (byte)(digit % 3);
    }
}
