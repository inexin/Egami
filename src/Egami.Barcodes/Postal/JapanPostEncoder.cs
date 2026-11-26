namespace Egami.Barcodes.Postal;

/// <summary>
/// The Japan Post customer barcode, from Japan Post's postal code manual (郵便番号・バーコードマニュアル, pages 10 to 13 and 17 to
/// 25): a start code, 20 data characters (the 7 digits of the postal code, then the address number, padded with CC4), a check
/// character and a stop code. Each character is three bars of the 4-state code; a letter is a control code, CC1 to CC3, and a
/// digit. The bar patterns are zint's (backend/postal.c, BSD-3), as the manual gives them only as a picture.
/// </summary>
internal static class JapanPostEncoder
{
    /// <summary>The data characters between the start code and the check character.</summary>
    public const int DataLength = 20;

    /// <summary>The value of a hyphen; digits are 0 to 9, and CC1 to CC8 are 11 to 18.</summary>
    public const byte Hyphen = 10;

    /// <summary>The value of CC1, the first control code; a letter is CC1 to CC3 and a digit.</summary>
    public const byte Cc1 = 11;

    /// <summary>The value of CC4, which pads the address number.</summary>
    public const byte Cc4 = 14;

    /// <summary>The bars of each character by its value, 0 to 18: digits, hyphen, CC1 to CC8.</summary>
    private static readonly FourStateBar[][] s_patterns = Patterns(
        "FTT", "FFT", "FDA", "DFA", "FAD", "FTF", "DAF", "AFD", "ADF", "TFF",
        "TFT", "DAT", "DTA", "ADT", "TDA", "ATD", "TAD", "TTF", "FFF");

    /// <summary>Gets the value of each data character and the check character: 21 values, as the symbol holds them.</summary>
    /// <param name="postalCode">The 7 digits of the postal code.</param>
    /// <param name="address">The address number: digits, hyphens and A to Z.</param>
    /// <param name="truncate">Whether to drop the end of an address number that does not fit, as the manual says, or report it.</param>
    /// <returns>The values, or <see langword="null"/> when the address number does not fit and <paramref name="truncate"/> is off.</returns>
    public static byte[]? Values(ReadOnlySpan<char> postalCode, ReadOnlySpan<char> address, bool truncate)
    {
        byte[] values = new byte[DataLength + 1];
        int at = 0;
        foreach (char c in postalCode)
        {
            values[at++] = (byte)(c - '0');
        }

        foreach (char c in address)
        {
            int needed = c is >= 'A' and <= 'Z' ? 2 : 1;
            if (at + needed > DataLength)
            {
                if (!truncate)
                {
                    return null;
                }

                // A letter whose control code is the 20th character keeps its control code alone (manual page 20).
                if (at < DataLength)
                {
                    values[at++] = (byte)(Cc1 + ((c - 'A') / 10));
                }

                break;
            }

            if (needed == 2)
            {
                values[at++] = (byte)(Cc1 + ((c - 'A') / 10));
                values[at++] = (byte)((c - 'A') % 10);
            }
            else
            {
                values[at++] = c == '-' ? Hyphen : (byte)(c - '0');
            }
        }

        values.AsSpan(at, DataLength - at).Fill(Cc4);
        values[DataLength] = CheckValue(values.AsSpan(0, DataLength));
        return values;
    }

    /// <summary>Gets the check character's value: the sum of all 21 values is a multiple of 19 (manual page 21).</summary>
    public static byte CheckValue(ReadOnlySpan<byte> data)
    {
        int sum = 0;
        foreach (byte value in data)
        {
            sum += value;
        }

        return (byte)((19 - (sum % 19)) % 19);
    }

    /// <summary>Makes the grid of a symbol: start code (full, descender), the 21 characters, stop code (descender, full).</summary>
    public static BarcodeModules Grid(ReadOnlySpan<byte> values)
    {
        var bars = new FourStateBar[2 + (3 * values.Length) + 2];
        bars[0] = FourStateBar.Full;
        bars[1] = FourStateBar.Descender;
        for (int i = 0; i < values.Length; i++)
        {
            s_patterns[values[i]].CopyTo(bars, 2 + (3 * i));
        }

        bars[^2] = FourStateBar.Descender;
        bars[^1] = FourStateBar.Full;
        return FourStateGrid.Create(bars);
    }

    private static FourStateBar[][] Patterns(params string[] patterns) =>
        [.. patterns.Select(p => p.Select(c => c switch
        {
            'F' => FourStateBar.Full,
            'A' => FourStateBar.Ascender,
            'D' => FourStateBar.Descender,
            _ => FourStateBar.Tracker,
        }).ToArray())];
}
