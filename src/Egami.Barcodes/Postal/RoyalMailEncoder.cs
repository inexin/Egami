namespace Egami.Barcodes.Postal;

/// <summary>
/// The Royal Mail 4-State Customer Code (RM4SCC, Royal Mail's CBC) and PostNL's KIX code, which uses its characters: digits and
/// A to Z, each four bars of which two have an ascender and two a descender. The ascenders' places give a character its top
/// value, the sum of the weights 4, 2, 1 and 0 of the bars that have one, modulo 6; the descenders give its bottom value.
/// Characters are in order of their values: top 1, 2, 3, 4, 5, 0 for 0 to 5, 6 to B and so on, and within each, bottom 1 to 5
/// and 0. RM4SCC adds a check character, the one whose values are the sums of the others' top and bottom values modulo 6, and
/// frames the symbol with a start bar (an ascender) and a stop bar (a full bar).
/// </summary>
internal static class RoyalMailEncoder
{
    /// <summary>The characters in order.</summary>
    public const string Characters = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    /// <summary>Gets the index of each character, lowercase letters as uppercase ones.</summary>
    /// <param name="text">The text.</param>
    /// <param name="bad">The index in the text of the first character that is not a digit or a letter A to Z, or -1.</param>
    /// <returns>The indices, or an empty array when a character is bad.</returns>
    public static int[] Indices(ReadOnlySpan<char> text, out int bad)
    {
        int[] indices = new int[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            int index = char.IsAsciiLetterOrDigit(text[i]) ? Characters.IndexOf(char.ToUpperInvariant(text[i]), StringComparison.Ordinal) : -1;
            if (index < 0)
            {
                bad = i;
                return [];
            }

            indices[i] = index;
        }

        bad = -1;
        return indices;
    }

    /// <summary>Gets the top value of a character, 0 to 5.</summary>
    public static int Top(int index) => ((index / 6) + 1) % 6;

    /// <summary>Gets the bottom value of a character, 0 to 5.</summary>
    public static int Bottom(int index) => ((index % 6) + 1) % 6;

    /// <summary>Gets the check character of RM4SCC: the character whose values are the sums of the others' modulo 6.</summary>
    public static int Check(ReadOnlySpan<int> indices)
    {
        int top = 0, bottom = 0;
        foreach (int index in indices)
        {
            top += Top(index);
            bottom += Bottom(index);
        }

        return (6 * ((top + 5) % 6)) + ((bottom + 5) % 6);
    }

    /// <summary>Makes the bars of a row of characters, with room for <paramref name="start"/> bars before them and <paramref name="end"/> after, which the caller fills.</summary>
    public static FourStateBar[] Bars(ReadOnlySpan<int> indices, int start, int end)
    {
        var bars = new FourStateBar[start + (4 * indices.Length) + end];
        for (int i = 0; i < indices.Length; i++)
        {
            int up = Places(Top(indices[i])), down = Places(Bottom(indices[i]));
            for (int j = 0; j < 4; j++)
            {
                bool ascender = (up & (8 >> j)) != 0, descender = (down & (8 >> j)) != 0;
                bars[start + (4 * i) + j] = (ascender, descender) switch
                {
                    (true, true) => FourStateBar.Full,
                    (true, false) => FourStateBar.Ascender,
                    (false, true) => FourStateBar.Descender,
                    _ => FourStateBar.Tracker,
                };
            }
        }

        return bars;
    }

    /// <summary>Gets the two of the four bars, as bits from the first in the high bit, whose weights 4, 2, 1, 0 sum to a value modulo 6.</summary>
    private static int Places(int value) => value switch
    {
        0 => 0b1100, // 4 + 2
        5 => 0b1010, // 4 + 1
        4 => 0b1001, // 4 + 0
        3 => 0b0110, // 2 + 1
        2 => 0b0101, // 2 + 0
        _ => 0b0011, // 1 + 0
    };
}
