using System.Buffers;

namespace Egami.Barcodes.Linear;

/// <summary>
/// Codabar (EN 798, AIM USS-Codabar): digits, <c>- $ : / . +</c> and the start and stop characters A to D, each four bars and
/// three spaces of which two or three are wide, with a narrow gap between characters.
/// </summary>
internal static class CodabarEncoder
{
    /// <summary>The characters in order of their check values 0 to 19; the last four are the start and stop characters.</summary>
    public const string Characters = "0123456789-$:/.+ABCD";

    /// <summary>
    /// The fewest characters in a symbol, start and stop included, that Egami writes and reads, as zxing-cpp does: shorter
    /// symbols are too easily seen in noise.
    /// </summary>
    public const int MinimumLength = 4;

    /// <summary>The 16 data characters, for searching.</summary>
    public static readonly SearchValues<char> DataSet = SearchValues.Create("0123456789-$:/.+");

    /// <summary>The wide elements of each character, bar first, first element in the high bit of seven.</summary>
    public static readonly int[] Patterns =
    [
        0b0000011, 0b0000110, 0b0001001, 0b1100000, 0b0010010, 0b1000010, 0b0100001, 0b0100100, 0b0110000, 0b1001000,
        0b0001100, 0b0011000, 0b1000101, 0b1010001, 0b1010100, 0b0010101, 0b0011010, 0b0101001, 0b0001011, 0b0001110,
    ];

    /// <summary>Returns whether the character is a start or stop character, A to D.</summary>
    public static bool IsStartStop(char c) => c is >= 'A' and <= 'D';

    /// <summary>Returns the modulo 16 check character of the symbol's characters, start and stop included.</summary>
    public static char CheckCharacter(ReadOnlySpan<char> symbol)
    {
        int sum = 0;
        foreach (char c in symbol)
        {
            sum += Characters.IndexOf(c, StringComparison.Ordinal);
        }

        return Characters[(16 - (sum % 16)) % 16];
    }

    /// <summary>Returns the bar and space widths of the symbol's characters, start and stop included.</summary>
    public static int[] Runs(string symbol, int wide)
    {
        int[] runs = new int[(symbol.Length * 8) - 1];
        int at = 0;
        foreach (char c in symbol)
        {
            if (at > 0)
            {
                runs[at++] = 1;
            }

            int pattern = Patterns[Characters.IndexOf(c, StringComparison.Ordinal)];
            for (int bit = 6; bit >= 0; bit--)
            {
                runs[at++] = (pattern >> bit & 1) != 0 ? wide : 1;
            }
        }

        return runs;
    }
}
