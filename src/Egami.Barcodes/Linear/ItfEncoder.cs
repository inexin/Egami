namespace Egami.Barcodes.Linear;

/// <summary>
/// Interleaved 2 of 5 (ISO/IEC 16390): digits in pairs, the first of each pair in the bars and the second in the spaces, each
/// digit five elements of which two are wide; start narrow bar, space, bar, space; stop wide bar, narrow space, narrow bar.
/// </summary>
internal static class ItfEncoder
{
    // The wide elements of each digit, first element in the high bit.
    private static readonly int[] s_patterns = [0b00110, 0b10001, 0b01001, 0b11000, 0b00101, 0b10100, 0b01100, 0b00011, 0b10010, 0b01010];

    /// <summary>Returns the bar and space widths for an even number of digits.</summary>
    public static int[] Runs(string digits, int wide)
    {
        var runs = new List<int>(7 + (digits.Length * 5)) { 1, 1, 1, 1 };
        for (int i = 0; i < digits.Length; i += 2)
        {
            int bars = s_patterns[digits[i] - '0'], spaces = s_patterns[digits[i + 1] - '0'];
            for (int bit = 4; bit >= 0; bit--)
            {
                runs.Add((bars >> bit & 1) != 0 ? wide : 1);
                runs.Add((spaces >> bit & 1) != 0 ? wide : 1);
            }
        }

        runs.AddRange([wide, 1, 1]);
        return [.. runs];
    }
}
