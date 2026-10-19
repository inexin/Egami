using System.Buffers;

namespace Egami.Barcodes.Linear;

/// <summary>
/// Code 39 (ISO/IEC 16388): 43 characters plus the start and stop character *, each five bars and four spaces of which three
/// are wide, with a narrow gap between characters. Full ASCII writes the other ASCII characters as pairs with $, %, / or +.
/// </summary>
internal static class Code39Encoder
{
    /// <summary>The characters in order of their check values 0 to 42.</summary>
    public const string Characters = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ-. $/+%";

    /// <summary>The 43 characters, for searching.</summary>
    public static readonly SearchValues<char> CharacterSet = SearchValues.Create(Characters);

    // The wide elements of each character, first element in the high bit; the last is the start and stop character *.
    private static readonly int[] s_patterns =
    [
        0x034, 0x121, 0x061, 0x160, 0x031, 0x130, 0x070, 0x025, 0x124, 0x064,
        0x109, 0x049, 0x148, 0x019, 0x118, 0x058, 0x00D, 0x10C, 0x04C, 0x01C,
        0x103, 0x043, 0x142, 0x013, 0x112, 0x052, 0x007, 0x106, 0x046, 0x016,
        0x181, 0x0C1, 0x1C0, 0x091, 0x190, 0x0D0, 0x085, 0x184, 0x0C4, 0x0A8,
        0x0A2, 0x08A, 0x02A, 0x094,
    ];

    // Full ASCII: how each character 0 to 127 is written in the 43 characters.
    private static readonly string[] s_fullAscii = BuildFullAscii();

    /// <summary>Returns whether every character is one of the 43.</summary>
    public static bool IsStandard(string text) => !text.AsSpan().ContainsAnyExcept(CharacterSet);

    /// <summary>Returns the text written in the 43 characters, each character 0 to 127 as one or two.</summary>
    public static string ToFullAscii(string text) => string.Concat(text.Select(c => s_fullAscii[c]));

    /// <summary>Returns the mod 43 check character of text in the 43 characters.</summary>
    public static char CheckCharacter(string text) => Characters[text.Sum(c => Characters.IndexOf(c, StringComparison.Ordinal)) % 43];

    /// <summary>Returns the bar and space widths for text in the 43 characters, between start and stop characters.</summary>
    public static int[] Runs(string text, int wide)
    {
        var runs = new List<int>(((text.Length + 2) * 10) - 1);
        Add(runs, s_patterns[43], wide);
        foreach (char c in text)
        {
            runs.Add(1);
            Add(runs, s_patterns[Characters.IndexOf(c, StringComparison.Ordinal)], wide);
        }

        runs.Add(1);
        Add(runs, s_patterns[43], wide);
        return [.. runs];
    }

    private static void Add(List<int> runs, int pattern, int wide)
    {
        for (int bit = 8; bit >= 0; bit--)
        {
            runs.Add((pattern >> bit & 1) != 0 ? wide : 1);
        }
    }

    private static string[] BuildFullAscii()
    {
        string[] map = new string[128];
        map[0] = "%U";
        for (int c = 1; c <= 26; c++)
        {
            map[c] = "$" + (char)('A' + c - 1);
        }

        for (int c = 27; c <= 31; c++)
        {
            map[c] = "%" + (char)('A' + c - 27);
        }

        map[' '] = " ";
        const string punctuation = "!\"#$%&'()*+,";
        for (int i = 0; i < punctuation.Length; i++)
        {
            map[punctuation[i]] = "/" + (char)('A' + i);
        }

        map['-'] = "-";
        map['.'] = ".";
        map['/'] = "/O";
        for (char c = '0'; c <= '9'; c++)
        {
            map[c] = c.ToString();
        }

        map[':'] = "/Z";
        const string between = ";<=>?";
        for (int i = 0; i < between.Length; i++)
        {
            map[between[i]] = "%" + (char)('F' + i);
        }

        map['@'] = "%V";
        for (char c = 'A'; c <= 'Z'; c++)
        {
            map[c] = c.ToString();
            map[c + 32] = "+" + c;
        }

        const string brackets = "[\\]^_";
        for (int i = 0; i < brackets.Length; i++)
        {
            map[brackets[i]] = "%" + (char)('K' + i);
        }

        map['`'] = "%W";
        const string braces = "{|}~";
        for (int i = 0; i < braces.Length; i++)
        {
            map[braces[i]] = "%" + (char)('P' + i);
        }

        map[127] = "%T";
        return map;
    }
}
