using System.Buffers;

namespace Egami.Barcodes.Linear;

/// <summary>
/// Code 93 (ANSI/AIM BC5, AIM USS-93): 43 data characters, four shift characters and the start and stop character *, each
/// nine modules in three bars and three spaces of one to four modules. Two check characters, C and K, go before the stop
/// character, and a one-module termination bar after it. Full ASCII writes the other ASCII characters as a shift and a letter.
/// </summary>
internal static class Code93Encoder
{
    /// <summary>The data characters in order of their values 0 to 42; the shifts ($), (%), (/) and (+) are 43 to 46.</summary>
    public const string Characters = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ-. $/+%";

    /// <summary>The 43 data characters, for searching.</summary>
    public static readonly SearchValues<char> CharacterSet = SearchValues.Create(Characters);

    /// <summary>The value of the start and stop character.</summary>
    public const int StartStop = 47;

    /// <summary>
    /// The bar and space widths of each value 0 to 47, six digits of one to four modules, bar first: the data characters, the
    /// shifts ($), (%), (/) and (+), and the start and stop character.
    /// </summary>
    public static readonly int[] Patterns =
    [
        131112, 111213, 111312, 111411, 121113, 121212, 121311, 111114, 131211, 141111,
        211113, 211212, 211311, 221112, 221211, 231111, 112113, 112212, 112311, 122112,
        132111, 111123, 111222, 111321, 121122, 131121, 212112, 212211, 211122, 211221,
        221121, 222111, 112122, 112221, 122121, 123111, 121131, 311112, 311211, 321111,
        112131, 113121, 211131, 121221, 312111, 311121, 122211, 111141,
    ];

    // Full ASCII: the values each character 0 to 127 is written with, one or a shift and a letter.
    private static readonly int[][] s_fullAscii = BuildFullAscii();

    /// <summary>Returns whether every character is one of the 43 data characters.</summary>
    public static bool IsStandard(string text) => !text.AsSpan().ContainsAnyExcept(CharacterSet);

    /// <summary>Returns the values of text in the 43 data characters.</summary>
    public static List<int> Values(string text)
    {
        var values = new List<int>(text.Length + 2);
        foreach (char c in text)
        {
            values.Add(Characters.IndexOf(c, StringComparison.Ordinal));
        }

        return values;
    }

    /// <summary>Returns the values of ASCII text in Full ASCII, each character as one value or a shift and a letter.</summary>
    public static List<int> FullAsciiValues(string text)
    {
        var values = new List<int>(text.Length * 2);
        foreach (char c in text)
        {
            values.AddRange(s_fullAscii[c]);
        }

        return values;
    }

    /// <summary>Adds the check characters C (weights 1 to 20) and K (weights 1 to 15, C included), both modulo 47.</summary>
    public static void AddCheckCharacters(List<int> values)
    {
        values.Add(Check(values, 20));
        values.Add(Check(values, 15));
    }

    /// <summary>The check value of the values with weights 1 to <paramref name="maxWeight"/> from the right, repeating.</summary>
    public static int Check(IReadOnlyList<int> values, int maxWeight)
    {
        int sum = 0;
        for (int i = values.Count - 1, weight = 1; i >= 0; i--)
        {
            sum += values[i] * weight;
            weight = weight == maxWeight ? 1 : weight + 1;
        }

        return sum % 47;
    }

    /// <summary>Returns the bar and space widths for the values (check characters included) between start and stop characters.</summary>
    public static int[] Runs(List<int> values)
    {
        int[] runs = new int[((values.Count + 2) * 6) + 1];
        int at = Add(runs, 0, StartStop);
        foreach (int value in values)
        {
            at = Add(runs, at, value);
        }

        at = Add(runs, at, StartStop);
        runs[at] = 1;
        return runs;
    }

    private static int Add(int[] runs, int at, int value)
    {
        int pattern = Patterns[value];
        for (int divisor = 100000; divisor > 0; divisor /= 10)
        {
            runs[at++] = pattern / divisor % 10;
        }

        return at;
    }

    private static int[][] BuildFullAscii()
    {
        // Shifts: ($) 43, (%) 44, (/) 45, (+) 46; then a letter A to Z, values 10 to 35.
        const int dollar = 43, percent = 44, slash = 45, plus = 46;
        int[][] map = new int[128][];
        static int[] Shift(int shift, char letter) => [shift, letter - 'A' + 10];
        map[0] = Shift(percent, 'U');
        for (int c = 1; c <= 26; c++)
        {
            map[c] = Shift(dollar, (char)('A' + c - 1));
        }

        for (int c = 27; c <= 31; c++)
        {
            map[c] = Shift(percent, (char)('A' + c - 27));
        }

        // The 43 data characters stand for themselves.
        for (int i = 0; i < Characters.Length; i++)
        {
            map[Characters[i]] = [i];
        }

        const string punctuation = "!\"#&'()*,";
        foreach (char c in punctuation)
        {
            map[c] = Shift(slash, (char)('A' + (c - '!')));
        }

        map[':'] = Shift(slash, 'Z');
        const string between = ";<=>?";
        for (int i = 0; i < between.Length; i++)
        {
            map[between[i]] = Shift(percent, (char)('F' + i));
        }

        map['@'] = Shift(percent, 'V');
        for (char c = 'a'; c <= 'z'; c++)
        {
            map[c] = Shift(plus, (char)(c - 32));
        }

        const string brackets = "[\\]^_";
        for (int i = 0; i < brackets.Length; i++)
        {
            map[brackets[i]] = Shift(percent, (char)('K' + i));
        }

        map['`'] = Shift(percent, 'W');
        const string braces = "{|}~";
        for (int i = 0; i < braces.Length; i++)
        {
            map[braces[i]] = Shift(percent, (char)('P' + i));
        }

        map[127] = Shift(percent, 'T');
        return map;
    }
}
