using Egami.Barcodes.Gs1Data;
using Egami.Barcodes.Rendering;

namespace Egami.Barcodes.Linear;

/// <summary>
/// EAN-13, EAN-8, UPC-A and UPC-E (ISO/IEC 15420) and the EAN-2 and EAN-5 add-ons: each digit seven modules in two bars and two
/// spaces, in set A (odd parity), set B (even parity, set A mirrored) or set C (set A with bars and spaces swapped, which has the
/// same widths). Guard bars reach down into the text line, and the digits are placed between them.
/// </summary>
internal static class EanUpcEncoder
{
    // Set A widths, space first; set C has the same widths bar first; set B is set A reversed.
    private static readonly string[] s_setA = ["3211", "2221", "2122", "1411", "1132", "1231", "1114", "1312", "1213", "3112"];

    // EAN-13: the sets of digits 2 to 7, from the first digit (true = set B).
    private static readonly string[] s_ean13Parity = ["AAAAAA", "AABABB", "AABBAB", "AABBBA", "ABAABB", "ABBAAB", "ABBBAA", "ABABAB", "ABABBA", "ABBABA"];

    // UPC-E with number system 0: the sets of the six digits, from the check digit; number system 1 swaps A and B.
    private static readonly string[] s_upcEParity = ["BBBAAA", "BBABAA", "BBAABA", "BBAAAB", "BABBAA", "BAABBA", "BAAABB", "BABABA", "BABAAB", "BAABAB"];

    // EAN-5: the sets of the five digits, from the add-on's checksum.
    private static readonly string[] s_ean5Parity = ["BBAAA", "BABAA", "BAABA", "BAAAB", "ABBAA", "AABBA", "AAABB", "ABABA", "ABAAB", "AABAB"];

    /// <summary>A symbol's runs, text and decoration.</summary>
    public readonly record struct Symbol(int[] Runs, string Text, LinearDecoration Decoration);

    /// <summary>Encodes 13 digits as EAN-13, or as UPC-A when the first is the 0 that UPC-A leaves out.</summary>
    public static Symbol Ean13(string digits, bool upcA, string? addOn)
    {
        var runs = new Runs();
        runs.Guard("111", tall: true);
        string parity = s_ean13Parity[digits[0] - '0'];
        for (int i = 1; i <= 6; i++)
        {
            runs.Digit(digits[i], parity[i - 1] == 'B', tall: upcA && i == 1);
        }

        runs.Guard("11111", tall: true);
        for (int i = 7; i <= 12; i++)
        {
            runs.Digit(digits[i], setB: false, tall: upcA && i == 12);
        }

        runs.Guard("111", tall: true);
        TextSegment[] segments = upcA
            ? [new(digits[1..2], -8, -1), new(digits[2..7], 10, 45), new(digits[7..12], 50, 85), new(digits[12..], 96, 103)]
            : [new(digits[..1], -8, -1), new(digits[1..7], 3, 45), new(digits[7..], 50, 92)];
        return Finish(runs, upcA ? digits[1..] : digits, segments, addOn, upcA ? 9 : 7);
    }

    /// <summary>Encodes 8 digits as EAN-8.</summary>
    public static Symbol Ean8(string digits, string? addOn)
    {
        var runs = new Runs();
        runs.Guard("111", tall: true);
        for (int i = 0; i < 4; i++)
        {
            runs.Digit(digits[i], setB: false, tall: false);
        }

        runs.Guard("11111", tall: true);
        for (int i = 4; i < 8; i++)
        {
            runs.Digit(digits[i], setB: false, tall: false);
        }

        runs.Guard("111", tall: true);
        return Finish(runs, digits, [new(digits[..4], 3, 31), new(digits[4..], 36, 64)], addOn, 7);
    }

    /// <summary>Encodes UPC-E: the number system, six digits and the check digit.</summary>
    public static Symbol UpcE(string digits, string? addOn)
    {
        var runs = new Runs();
        runs.Guard("111", tall: true);
        string parity = s_upcEParity[digits[7] - '0'];
        for (int i = 1; i <= 6; i++)
        {
            runs.Digit(digits[i], (parity[i - 1] == 'B') == (digits[0] == '0'), tall: false);
        }

        runs.Guard("111111", tall: true);
        return Finish(runs, digits, [new(digits[..1], -8, -1), new(digits[1..7], 3, 45), new(digits[7..], 52, 59)], addOn, 9);
    }

    /// <summary>Returns the UPC-A digits (11, without the check digit) that a UPC-E symbol's number system and six digits stand for.</summary>
    public static string ExpandUpcE(string system, string d) => system + (d[5] switch
    {
        '0' or '1' or '2' => $"{d[..2]}{d[5]}0000{d[2..5]}",
        '3' => $"{d[..3]}00000{d[3..5]}",
        '4' => $"{d[..4]}00000{d[4]}",
        _ => $"{d[..5]}0000{d[5]}",
    });

    /// <summary>Returns the six UPC-E digits for 11 UPC-A digits (no check digit), or <see langword="null"/> when they do not compress.</summary>
    public static string? CompressUpcA(string a)
    {
        string m = a[1..6], p = a[6..11];
        if (m[2..] is "000" or "100" or "200" && p[..2] == "00")
        {
            return $"{m[..2]}{p[2..]}{m[2]}";
        }

        if (m[3..] == "00" && p[..3] == "000")
        {
            return $"{m[..3]}{p[3..]}3";
        }

        if (m[4] == '0' && p[..4] == "0000")
        {
            return $"{m[..4]}{p[4]}4";
        }

        return p[..4] == "0000" && p[4] >= '5' ? $"{m}{p[4]}" : null;
    }

    private static Symbol Finish(Runs runs, string text, TextSegment[] segments, string? addOn, int gap)
    {
        int addOnStart = -1;
        if (addOn is not null)
        {
            addOnStart = runs.Width + gap;
            runs.Space(gap);
            runs.AddOn(addOn);
            segments = [.. segments, new TextSegment(addOn, addOnStart, runs.Width, Above: true)];
            text += " " + addOn;
        }

        return new Symbol([.. runs.Widths], text, new LinearDecoration([.. runs.Tall], addOnStart, 0, segments));
    }

    /// <summary>Builds the runs, bar first, and which bars are guards.</summary>
    private sealed class Runs
    {
        // Room for EAN-13 with an EAN-5 add-on (59 + 1 + 47 runs), so the lists never grow.
        public List<int> Widths { get; } = new(112);

        public List<bool> Tall { get; } = new(112);

        public int Width { get; private set; }

        public void Guard(string widths, bool tall)
        {
            foreach (char c in widths)
            {
                Add(c - '0', tall);
            }
        }

        public void Digit(char digit, bool setB, bool tall)
        {
            string widths = s_setA[digit - '0'];
            for (int i = 0; i < 4; i++)
            {
                Add(widths[setB ? 3 - i : i] - '0', tall);
            }
        }

        public void Space(int width) => Add(width, false);

        public void AddOn(string digits)
        {
            string parity = digits.Length == 2
                ? ((((digits[0] - '0') * 10) + (digits[1] - '0')) % 4) switch { 0 => "AA", 1 => "AB", 2 => "BA", _ => "BB" }
                : s_ean5Parity[((3 * (digits[0] + digits[2] + digits[4] - (3 * '0'))) + (9 * (digits[1] + digits[3] - (2 * '0')))) % 10];
            Guard("112", tall: false);
            for (int i = 0; i < digits.Length; i++)
            {
                if (i > 0)
                {
                    Guard("11", tall: false);
                }

                Digit(digits[i], parity[i] == 'B', tall: false);
            }
        }

        private void Add(int width, bool tall)
        {
            // Runs alternate bar and space; only bars extend.
            Widths.Add(width);
            Tall.Add(tall && Widths.Count % 2 == 1);
            Width += width;
        }
    }

    /// <summary>Returns the GS1 check digit of digits.</summary>
    public static char CheckDigit(string digits) => (char)('0' + Gs1Checks.CheckDigit(digits));
}
