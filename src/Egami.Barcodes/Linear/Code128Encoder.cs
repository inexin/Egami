namespace Egami.Barcodes.Linear;

/// <summary>
/// Code 128 (ISO/IEC 15417): picks the code sets A, B and C, with shifts and changes, that give the fewest symbol characters, and
/// builds the bars. Characters 128 to 255 take an FNC4 before each. FNC1 (for GS1-128) is <see cref="Fnc1"/> in the input.
/// </summary>
internal static class Code128Encoder
{
    /// <summary>The FNC1 function character in the input.</summary>
    public const int Fnc1 = -1;

    private const int ShiftValue = 98, CodeC = 99, CodeB = 100, CodeA = 101, Fnc1Value = 102, StartA = 103, StartB = 104, StartC = 105, Stop = 106;
    private const int SetA = 0, SetB = 1, SetC = 2;

    // Bar and space widths of each symbol character, bar first; the stop character has a final 2-module bar.
    private static readonly string[] s_patterns =
    [
        "212222", "222122", "222221", "121223", "121322", "131222", "122213", "122312", "132212", "221213",
        "221312", "231212", "112232", "122132", "122231", "113222", "123122", "123221", "223211", "221132",
        "221231", "213212", "223112", "312131", "311222", "321122", "321221", "312212", "322112", "322211",
        "212123", "212321", "232121", "111323", "131123", "131321", "112313", "132113", "132311", "211313",
        "231113", "231311", "112133", "112331", "132131", "113123", "113321", "133121", "313121", "211331",
        "231131", "213113", "213311", "213131", "311123", "311321", "331121", "312113", "312311", "332111",
        "314111", "221411", "431111", "111224", "111422", "121124", "121421", "141122", "141221", "112214",
        "112412", "122114", "122411", "142112", "142211", "241211", "221114", "413111", "241112", "134111",
        "111242", "121142", "121241", "114212", "124112", "124211", "411212", "421112", "421211", "212141",
        "214121", "412121", "111143", "111341", "131141", "114113", "114311", "411113", "411311", "113141",
        "114131", "311141", "411131", "211412", "211214", "211232", "2331112",
    ];

    /// <summary>Encodes the input (bytes 0 to 255 and <see cref="Fnc1"/>) as symbol character values, start to check, without the stop.</summary>
    public static List<int> Encode(ReadOnlySpan<int> input)
    {
        int n = input.Length;

        // cost[(i * 3) + s]: fewest characters for input[i..] when the symbol is in set s at i, set changes included.
        int cells = (n + 1) * 3;
        Span<int> cost = cells <= 1024 ? stackalloc int[cells] : new int[cells];
        Span<int> step = cells <= 1024 ? stackalloc int[cells] : new int[cells];
        cost[(n * 3)..].Clear();
        Span<int> direct = stackalloc int[3];
        Span<int> directStep = stackalloc int[3];
        for (int i = n - 1; i >= 0; i--)
        {
            for (int set = 0; set < 3; set++)
            {
                (direct[set], directStep[set]) = Direct(input, i, set, cost);
            }

            for (int set = 0; set < 3; set++)
            {
                // On a tie, change to C now, so digits go in pairs as early as they can; otherwise stay, and prefer B to A.
                int best = direct[set], how = directStep[set];
                foreach (int other in (ReadOnlySpan<int>)[SetC, SetB, SetA])
                {
                    if (other != set && direct[other] < int.MaxValue && (1 + direct[other] < best || (1 + direct[other] == best && other == SetC)))
                    {
                        best = 1 + direct[other];
                        how = Latch + other;
                    }
                }

                cost[(i * 3) + set] = best;
                step[(i * 3) + set] = how;
            }
        }

        // The start character picks the first set; ties go to C, then B, then A.
        int start = SetC;
        foreach (int set in (ReadOnlySpan<int>)[SetB, SetA])
        {
            if (cost[set] < cost[start])
            {
                start = set;
            }
        }

        var values = new List<int> { StartA + start };
        int current = start;
        for (int i = 0; i < n;)
        {
            int how = step[(i * 3) + current];
            if (how >= Latch)
            {
                int target = how - Latch;
                values.Add(target switch { SetA => CodeA, SetB => CodeB, _ => CodeC });
                current = target;
                how = step[(i * 3) + current] < Latch ? step[(i * 3) + current] : Direct(input, i, current, cost).Step;
            }

            switch (how)
            {
                case Pair:
                    values.Add(((input[i] - '0') * 10) + (input[i + 1] - '0'));
                    i += 2;
                    break;
                case Shift:
                    values.Add(ShiftValue);
                    AddCharacter(values, input[i], current == SetA ? SetB : SetA);
                    i++;
                    break;
                default:
                    AddCharacter(values, input[i], current);
                    i++;
                    break;
            }
        }

        int check = values[0];
        for (int i = 1; i < values.Count; i++)
        {
            check += values[i] * i;
        }

        values.Add(check % 103);
        return values;
    }

    /// <summary>Returns the bar and space widths of symbol characters followed by the stop character.</summary>
    public static int[] Runs(List<int> values)
    {
        int[] runs = new int[(values.Count * 6) + 7];
        int at = 0;
        foreach (int value in values)
        {
            s_widths[value].CopyTo(runs.AsSpan(at));
            at += 6;
        }

        s_widths[Stop].CopyTo(runs.AsSpan(at));
        return runs;
    }

    private static readonly int[][] s_widths = [.. s_patterns.Select(p => p.Select(c => c - '0').ToArray())];

    private const int Single = 0, Pair = 1, Shift = 2, Latch = 10;

    /// <summary>The cheapest way to encode from position i staying in the set (no change of set first), and its cost.</summary>
    private static (int Cost, int Step) Direct(ReadOnlySpan<int> input, int i, int set, ReadOnlySpan<int> cost)
    {
        int c = input[i];
        int best = int.MaxValue, how = Single;
        if (set == SetC)
        {
            if (c == Fnc1)
            {
                best = 1 + cost[((i + 1) * 3) + SetC];
            }
            else if (i + 1 < input.Length && char.IsAsciiDigit((char)c) && input[i + 1] is >= '0' and <= '9')
            {
                best = 1 + cost[((i + 2) * 3) + SetC];
                how = Pair;
            }

            return (best, how);
        }

        int other = set == SetA ? SetB : SetA;
        int extra = c >= 128 ? 1 : 0;
        if (c == Fnc1 || InSet(c & 0x7F, set))
        {
            best = 1 + (c == Fnc1 ? 0 : extra) + cost[((i + 1) * 3) + set];
        }
        else if (InSet(c & 0x7F, other))
        {
            best = 2 + extra + cost[((i + 1) * 3) + set];
            how = Shift;
        }

        return (best, how);
    }

    private static bool InSet(int c, int set) => set == SetA ? c < 96 : c >= 32;

    private static void AddCharacter(List<int> values, int c, int set)
    {
        if (c == Fnc1)
        {
            values.Add(Fnc1Value);
            return;
        }

        if (c >= 128)
        {
            // FNC4 is code 101 in set A and code 100 in set B.
            values.Add(set == SetA ? 101 : 100);
            c &= 0x7F;
        }

        values.Add(c >= 32 ? c - 32 : c + 64);
    }
}
