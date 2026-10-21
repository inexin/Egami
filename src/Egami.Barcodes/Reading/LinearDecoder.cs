using System.Text;

namespace Egami.Barcodes.Reading;

/// <summary>A linear symbol read from one scan line: its data and where it starts and ends on the line, in runs and pixels.</summary>
internal sealed record class LinearRead(DecodedData Data, float Start, float End);

/// <summary>
/// Reads linear symbols from the runs of one scan line: runs[0] is light (possibly empty), then dark and light in turn. Each
/// decoder finds its start pattern after a quiet zone, matches characters by their bar and space widths, checks the stop
/// pattern, quiet zone and check characters, and returns the first symbol it reads.
/// </summary>
internal static class LinearDecoder
{
    /// <summary>Reads the first symbol of the allowed formats on the line, left to right.</summary>
    public static LinearRead? Read(ReadOnlySpan<float> runs, ReadOnlySpan<float> offsets, BarcodeFormat formats)
    {
        for (int i = 1; i < runs.Length; i += 2)
        {
            LinearRead? read = null;
            if ((formats & (BarcodeFormat.Ean13 | BarcodeFormat.Ean8 | BarcodeFormat.UpcA | BarcodeFormat.UpcE)) != 0)
            {
                read = EanUpc(runs, offsets, i, formats);
            }

            if (read is null && (formats & BarcodeFormat.Code128) != 0)
            {
                read = Code128(runs, offsets, i);
            }

            if (read is null && (formats & BarcodeFormat.Code39) != 0)
            {
                read = Code39(runs, offsets, i);
            }

            if (read is null && (formats & BarcodeFormat.Itf) != 0)
            {
                read = Itf(runs, offsets, i);
            }

            if (read is not null)
            {
                return read;
            }
        }

        return null;
    }

    /// <summary>
    /// How far runs are from a pattern of module widths: the mean deviation per module, scaled to the runs' total; or
    /// <see cref="float.MaxValue"/> when one run is off by more than <paramref name="maximum"/> of a module.
    /// </summary>
    private static float Variance(ReadOnlySpan<float> runs, ReadOnlySpan<int> pattern, float maximum = 0.7f)
    {
        float total = 0;
        int modules = 0;
        for (int i = 0; i < pattern.Length; i++)
        {
            total += runs[i];
            modules += pattern[i];
        }

        if (total < modules)
        {
            return float.MaxValue;
        }

        float unit = total / modules, sum = 0;
        for (int i = 0; i < pattern.Length; i++)
        {
            float deviation = MathF.Abs(runs[i] - (pattern[i] * unit));
            if (deviation > maximum * unit * Math.Max(1, pattern[i] * 0.75f))
            {
                return float.MaxValue;
            }

            sum += deviation;
        }

        return sum / total;
    }

    private static float Sum(ReadOnlySpan<float> runs)
    {
        float total = 0;
        foreach (int r in runs)
        {
            total += r;
        }

        return total;
    }

    /// <summary>
    /// Whether the light run before a pattern is at least the given width. Light up to the image edge counts; a bar on the edge
    /// itself only when <paramref name="edge"/> is set, for symbologies whose start and stop patterns make a cropped read safe.
    /// </summary>
    private static bool QuietBefore(ReadOnlySpan<float> runs, int start, float width, bool edge = false) =>
        start == 1 && runs[0] == 0 ? edge : runs[start - 1] >= width || start - 1 == 0;

    private static bool QuietAfter(ReadOnlySpan<float> runs, int end, float width) => end >= runs.Length || runs[end] >= width || end == runs.Length - 1;

    private static LinearRead Result(DecodedData data, ReadOnlySpan<float> offsets, int start, int end) =>
        new(data, offsets[start], offsets[Math.Min(end, offsets.Length - 1)]);

    // ---- Code 128 ----

    private static readonly int[][] s_code128 = BuildCode128();

    private static int[][] BuildCode128()
    {
        // The same patterns the encoder writes, as module widths.
        int[] stop = [2, 3, 3, 1, 1, 1, 2];
        var patterns = new int[107][];
        for (int v = 0; v < 107; v++)
        {
            List<int> runs = [.. Linear.Code128Encoder.Runs([v])];
            patterns[v] = v == 106 ? stop : [.. runs.Take(6)];
        }

        return patterns;
    }

    private static LinearRead? Code128(ReadOnlySpan<float> runs, ReadOnlySpan<float> offsets, int start)
    {
        if (start + 6 > runs.Length)
        {
            return null;
        }

        int startCode = Best(runs.Slice(start, 6), 103, 106, 0.25f);
        if (startCode < 0 || !QuietBefore(runs, start, Sum(runs.Slice(start, 6)) / 2f))
        {
            return null;
        }

        var values = new List<int> { startCode };
        int at = start + 6;
        while (true)
        {
            // The stop pattern counts only with the quiet zone after it: a character and the next bar can look like it.
            if (at + 7 <= runs.Length && Variance(runs.Slice(at, 7), s_code128[106]) < 0.25f && QuietAfter(runs, at + 7, Sum(runs.Slice(at, 7)) / 2f))
            {
                break;
            }

            if (at + 6 > runs.Length)
            {
                return null;
            }

            int value = Best(runs.Slice(at, 6), 0, 106, 0.3f);
            if (value < 0)
            {
                // Bars printed wider or narrower than the spaces: edge-to-similar-edge distances do not change with that.
                value = EdgeDecode(runs.Slice(at, 6));
                if (value == 106 && at + 7 <= runs.Length && QuietAfter(runs, at + 7, Sum(runs.Slice(at, 7)) / 2f))
                {
                    break;
                }
            }

            if (value is < 0 or 106)
            {
                return null;
            }

            values.Add(value);
            at += 6;
        }

        if (values.Count < 3 || !QuietAfter(runs, at + 7, Sum(runs.Slice(at, 7)) / 2f))
        {
            return null;
        }

        int check = values[0];
        for (int i = 1; i < values.Count - 1; i++)
        {
            check += values[i] * i;
        }

        if (check % 103 != values[^1])
        {
            return null;
        }

        // FNC4 adds 128 to the next character; two in a row switch that on for every character until two more.
        var text = new TextBuilder();
        int set = values[0] - 103, shiftedSet = -1;
        bool fnc4 = false, fnc4Latch = false;
        for (int i = 1; i < values.Count - 1; i++)
        {
            int v = values[i], current = shiftedSet >= 0 ? shiftedSet : set;
            shiftedSet = -1;
            if (current == 2)
            {
                if (v < 100)
                {
                    text.AppendText(v.ToString("00", System.Globalization.CultureInfo.InvariantCulture));
                }
                else if (v == 100 || v == 101)
                {
                    set = v == 100 ? 1 : 0;
                }
                else if (v == 102)
                {
                    Fnc1(text, i);
                }

                continue;
            }

            if (v < 96)
            {
                int c = current == 0 ? (v < 64 ? v + 32 : v - 64) : v + 32;
                text.Append((byte)(c + (fnc4 != fnc4Latch ? 128 : 0)));
                fnc4 = false;
                continue;
            }

            bool isFnc4 = (current == 0 && v == 101) || (current == 1 && v == 100);
            if (isFnc4)
            {
                if (fnc4)
                {
                    fnc4Latch = !fnc4Latch;
                    fnc4 = false;
                }
                else
                {
                    fnc4 = true;
                }

                continue;
            }

            switch (v)
            {
                case 96 or 97:
                    break;
                case 98:
                    shiftedSet = current == 0 ? 1 : 0;
                    break;
                case 99:
                    set = 2;
                    break;
                case 100:
                    set = 1;
                    break;
                case 101:
                    set = 0;
                    break;
                case 102:
                    Fnc1(text, i);
                    break;
                default:
                    return null;
            }
        }

        (string result, IReadOnlyList<Gs1ElementString>? elements) = text.Result();
        return Result(new DecodedData(BarcodeFormat.Code128, result, text.Bytes) { Gs1 = elements }, offsets, start, at + 7);
    }

    private static void Fnc1(TextBuilder text, int position)
    {
        if (position == 1)
        {
            text.Gs1 = true;
        }
        else
        {
            text.Append(0x1D);
        }
    }

    /// <summary>The Code 128 value in a range whose pattern is nearest the runs, or -1.</summary>
    /// <summary>
    /// The reference decode of ISO/IEC 15417: the four distances from a bar's leading edge to the next bar's (and a space's to
    /// the next space's), in elevenths of the character, name the character whatever the ink spread; the few characters that
    /// share them are told apart by the total width of their bars. Returns -1 when none matches; 106 is the stop's start.
    /// </summary>
    private static int EdgeDecode(ReadOnlySpan<float> runs)
    {
        float total = Sum(runs);
        if (total < 11)
        {
            return -1;
        }

        Span<int> e = stackalloc int[4];
        for (int i = 0; i < 4; i++)
        {
            e[i] = (int)MathF.Round((runs[i] + runs[i + 1]) * 11 / total);
        }

        float bars = (runs[0] + runs[2] + runs[4]) * 11 / total;
        int best = -1;
        float bestError = float.MaxValue;
        for (int v = 0; v < 107; v++)
        {
            int[] pattern = s_code128[v];
            if (pattern[0] + pattern[1] != e[0] || pattern[1] + pattern[2] != e[1] || pattern[2] + pattern[3] != e[2] || pattern[3] + pattern[4] != e[3])
            {
                continue;
            }

            float error = MathF.Abs(bars - (pattern[0] + pattern[2] + pattern[4]));
            if (error < bestError)
            {
                (best, bestError) = (v, error);
            }
        }

        return bestError <= 1.5f ? best : -1;
    }

    private static int Best(ReadOnlySpan<float> runs, int from, int to, float limit)
    {
        int best = -1;
        float bestVariance = limit;
        for (int v = from; v < to; v++)
        {
            float variance = Variance(runs, s_code128[v]);
            if (variance < bestVariance)
            {
                (best, bestVariance) = (v, variance);
            }
        }

        return best;
    }

    // ---- Code 39 ----

    private const string Code39Characters = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ-. $/+%*";
    private static readonly int[] s_code39 =
    [
        0x034, 0x121, 0x061, 0x160, 0x031, 0x130, 0x070, 0x025, 0x124, 0x064, 0x109, 0x049, 0x148, 0x019, 0x118, 0x058,
        0x00D, 0x10C, 0x04C, 0x01C, 0x103, 0x043, 0x142, 0x013, 0x112, 0x052, 0x007, 0x106, 0x046, 0x016, 0x181, 0x0C1,
        0x1C0, 0x091, 0x190, 0x0D0, 0x085, 0x184, 0x0C4, 0x0A8, 0x0A2, 0x08A, 0x02A, 0x094,
    ];

    private static LinearRead? Code39(ReadOnlySpan<float> runs, ReadOnlySpan<float> offsets, int start)
    {
        if (start + 9 > runs.Length || !MayBeCode39Start(runs.Slice(start, 9)) || Code39Character(runs.Slice(start, 9)) != '*' || !QuietBefore(runs, start, Sum(runs.Slice(start, 9)) / 2f, edge: true))
        {
            return null;
        }

        var chars = new StringBuilder();
        int at = start + 10;
        while (true)
        {
            if (at + 9 > runs.Length)
            {
                return null;
            }

            char c = Code39Character(runs.Slice(at, 9));
            if (c == '\0')
            {
                return null;
            }

            if (c == '*')
            {
                break;
            }

            chars.Append(c);
            at += 10;
        }

        if (chars.Length == 0 || !QuietAfter(runs, at + 9, Sum(runs.Slice(at, 9)) / 2f))
        {
            return null;
        }

        string text = FullAscii(chars.ToString()) ?? chars.ToString();
        byte[] bytes = Encoding.Latin1.GetBytes(text);
        return Result(new DecodedData(BarcodeFormat.Code39, text, bytes), offsets, start, at + 9);
    }

    /// <summary>
    /// Whether 9 runs can be the start character '*' (wide space 1, wide bars 4 and 6): its wide bars are at least as wide as
    /// the other bars and its wide space as the other spaces, in either way <see cref="Code39Character"/> tells them apart.
    /// A quick test before sorting, as most positions on a line are not a start.
    /// </summary>
    private static bool MayBeCode39Start(ReadOnlySpan<float> r)
    {
        float bar = MathF.Max(r[0], MathF.Max(r[2], r[8]));
        return r[4] >= bar && r[6] >= bar && r[1] >= MathF.Max(r[3], MathF.Max(r[5], r[7]));
    }

    /// <summary>
    /// The Code 39 character of 9 runs (the three widest are wide), or '\0'. When ink spread makes the bars much thinner or
    /// thicker than the spaces, bars and spaces are told wide or narrow apart: a character has 0 or 2 wide bars and 3 or 1
    /// wide spaces.
    /// </summary>
    private static char Code39Character(ReadOnlySpan<float> runs)
    {
        Span<float> sorted = stackalloc float[9];
        runs.CopyTo(sorted);
        sorted.Sort();
        float narrowMax = sorted[5], wideMin = sorted[6];
        if (sorted[8] > sorted[0] * 5)
        {
            return '\0';
        }

        if (wideMin >= narrowMax * 1.5f)
        {
            int pattern = 0;
            for (int i = 0; i < 9; i++)
            {
                pattern = (pattern << 1) | (runs[i] >= wideMin ? 1 : 0);
            }

            int index = Array.IndexOf(s_code39, pattern);
            return index < 0 ? '\0' : Code39Characters[index];
        }

        Span<float> bars = [runs[0], runs[2], runs[4], runs[6], runs[8]];
        Span<float> spaces = [runs[1], runs[3], runs[5], runs[7]];
        bars.Sort();
        spaces.Sort();

        // Two wide bars and one wide space, or no wide bar and three wide spaces; each split needs a clear gap.
        float barsWide = bars[3] >= bars[2] * 1.5f ? bars[3] : float.MaxValue, spacesWide = float.MaxValue;
        if (barsWide != float.MaxValue && spaces[3] >= spaces[2] * 1.5f)
        {
            spacesWide = spaces[3];
        }
        else if (bars[4] < bars[0] * 1.5f && spaces[1] >= spaces[0] * 1.5f)
        {
            (barsWide, spacesWide) = (float.MaxValue, spaces[1]);
        }
        else
        {
            return '\0';
        }

        int split = 0;
        for (int i = 0; i < 9; i++)
        {
            split = (split << 1) | (runs[i] >= (i % 2 == 0 ? barsWide : spacesWide) ? 1 : 0);
        }

        int found = Array.IndexOf(s_code39, split);
        return found < 0 ? '\0' : Code39Characters[found];
    }

    /// <summary>Decodes Full ASCII pairs ($, %, / and + followed by a letter), or returns null when a pair is not valid.</summary>
    private static string? FullAscii(string text)
    {
        if (!text.AsSpan().ContainsAny("$%/+"))
        {
            return null;
        }

        var result = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is not ('$' or '%' or '/' or '+'))
            {
                result.Append(c);
                continue;
            }

            if (i + 1 >= text.Length)
            {
                return null;
            }

            char n = text[++i];
            int? decoded = c switch
            {
                '$' when n is >= 'A' and <= 'Z' => n - 'A' + 1,
                '%' when n is >= 'A' and <= 'E' => n - 'A' + 27,
                '%' when n is >= 'F' and <= 'J' => n - 'F' + ';',
                '%' when n is >= 'K' and <= 'O' => "[\\]^_"[n - 'K'],
                '%' when n is >= 'P' and <= 'S' => "{|}~"[n - 'P'],
                '%' when n == 'T' || n == 'X' || n == 'Y' || n == 'Z' => 127,
                '%' when n == 'U' => 0,
                '%' when n == 'V' => '@',
                '%' when n == 'W' => '`',
                '/' when n is >= 'A' and <= 'L' => "!\"#$%&'()*+,"[n - 'A'],
                '/' when n == 'O' => '/',
                '/' when n == 'Z' => ':',
                '+' when n is >= 'A' and <= 'Z' => n - 'A' + 'a',
                _ => null,
            };
            if (decoded is not int value)
            {
                return null;
            }

            result.Append((char)value);
        }

        return result.ToString();
    }

    // ---- ITF ----

    private static readonly int[] s_itf = [0b00110, 0b10001, 0b01001, 0b11000, 0b00101, 0b10100, 0b01100, 0b00011, 0b10010, 0b01010];

    private static LinearRead? Itf(ReadOnlySpan<float> runs, ReadOnlySpan<float> offsets, int start)
    {
        if (start + 4 > runs.Length || Variance(runs.Slice(start, 4), [1, 1, 1, 1], 0.5f) > 0.3f)
        {
            return null;
        }

        float narrow = Sum(runs.Slice(start, 4)) / 4f;
        if (!QuietBefore(runs, start, narrow * 8))
        {
            return null;
        }

        var digits = new StringBuilder();
        int at = start + 4;
        Span<float> bars = stackalloc float[5], spaces = stackalloc float[5];
        while (at + 10 <= runs.Length)
        {
            // The stop pattern: wide bar, narrow space, narrow bar, then the quiet zone.
            if (at + 3 <= runs.Length && runs[at] > narrow * 1.8f && runs[at + 1] < narrow * 1.6f && runs[at + 2] < narrow * 1.6f
                && QuietAfter(runs, at + 3, narrow * 8))
            {
                break;
            }

            for (int k = 0; k < 5; k++)
            {
                bars[k] = runs[at + (2 * k)];
                spaces[k] = runs[at + (2 * k) + 1];
            }

            int a = TwoOfFive(bars), b = TwoOfFive(spaces);
            if (a < 0 || b < 0)
            {
                return null;
            }

            digits.Append((char)('0' + a)).Append((char)('0' + b));
            at += 10;
        }

        if (at + 3 > runs.Length || digits.Length < 4 || !(runs[at] > narrow * 1.8f))
        {
            return null;
        }

        string text = digits.ToString();
        return Result(new DecodedData(BarcodeFormat.Itf, text, Encoding.ASCII.GetBytes(text)), offsets, start, at + 3);
    }

    /// <summary>The digit of five elements of which the two widest are wide, or -1.</summary>
    private static int TwoOfFive(ReadOnlySpan<float> elements)
    {
        Span<float> sorted = stackalloc float[5];
        elements.CopyTo(sorted);
        sorted.Sort();
        if (sorted[3] < sorted[2] * 1.5f)
        {
            return -1;
        }

        int pattern = 0;
        for (int i = 0; i < 5; i++)
        {
            pattern = (pattern << 1) | (elements[i] >= sorted[3] ? 1 : 0);
        }

        return Array.IndexOf(s_itf, pattern);
    }

    // ---- EAN and UPC ----

    private static readonly int[][] s_setA = [[3, 2, 1, 1], [2, 2, 2, 1], [2, 1, 2, 2], [1, 4, 1, 1], [1, 1, 3, 2], [1, 2, 3, 1], [1, 1, 1, 4], [1, 3, 1, 2], [1, 2, 1, 3], [3, 1, 1, 2]];
    private static readonly string[] s_ean13Parity = ["AAAAAA", "AABABB", "AABBAB", "AABBBA", "ABAABB", "ABBAAB", "ABBBAA", "ABABAB", "ABABBA", "ABBABA"];
    private static readonly string[] s_upcEParity = ["BBBAAA", "BBABAA", "BBAABA", "BBAAAB", "BABBAA", "BAABBA", "BAAABB", "BABABA", "BABAAB", "BAABAB"];
    private static readonly string[] s_ean5Parity = ["BBAAA", "BABAA", "BAABA", "BAAAB", "ABBAA", "AABBA", "AAABB", "ABABA", "ABAAB", "AABAB"];

    /// <summary>A digit of 4 runs: its value and set (A, or B for the mirrored set A), or (-1, ' ').</summary>
    private static (int Digit, char Set) EanDigit(ReadOnlySpan<float> runs, bool allowB)
    {
        int best = -1;
        char set = ' ';
        float bestVariance = 0.48f;
        Span<int> reversed = stackalloc int[4];
        for (int d = 0; d < 10; d++)
        {
            float variance = Variance(runs, s_setA[d]);
            if (variance < bestVariance)
            {
                (best, set, bestVariance) = (d, 'A', variance);
            }

            if (allowB)
            {
                for (int k = 0; k < 4; k++)
                {
                    reversed[k] = s_setA[d][3 - k];
                }

                variance = Variance(runs, reversed);
                if (variance < bestVariance)
                {
                    (best, set, bestVariance) = (d, 'B', variance);
                }
            }
        }

        return (best, set);
    }

    private static LinearRead? EanUpc(ReadOnlySpan<float> runs, ReadOnlySpan<float> offsets, int start, BarcodeFormat formats)
    {
        if (start + 3 + 24 > runs.Length || Variance(runs.Slice(start, 3), [1, 1, 1], 0.75f) > 0.48f)
        {
            return null;
        }

        float module = Sum(runs.Slice(start, 3)) / 3f;
        if (!QuietBefore(runs, start, module * 3))
        {
            return null;
        }

        int at = start + 3;
        return EanUpc13or8(runs, offsets, start, at, 6, formats)
            ?? EanUpc13or8(runs, offsets, start, at, 4, formats)
            ?? UpcE(runs, offsets, start, at, formats);
    }

    private static LinearRead? EanUpc13or8(ReadOnlySpan<float> runs, ReadOnlySpan<float> offsets, int start, int at, int half, BarcodeFormat formats)
    {
        bool ean13 = half == 6;
        if (ean13 && (formats & (BarcodeFormat.Ean13 | BarcodeFormat.UpcA)) == 0 || !ean13 && (formats & BarcodeFormat.Ean8) == 0)
        {
            return null;
        }

        if (at + (half * 4) + 5 + (half * 4) + 3 > runs.Length)
        {
            return null;
        }

        var digits = new StringBuilder();
        var parity = new StringBuilder();
        for (int k = 0; k < half; k++)
        {
            (int d, char set) = EanDigit(runs.Slice(at, 4), allowB: ean13);
            if (d < 0)
            {
                return null;
            }

            digits.Append((char)('0' + d));
            parity.Append(set);
            at += 4;
        }

        if (Variance(runs.Slice(at, 5), [1, 1, 1, 1, 1], 0.75f) > 0.48f)
        {
            return null;
        }

        at += 5;
        for (int k = 0; k < half; k++)
        {
            (int d, char set) = EanDigit(runs.Slice(at, 4), allowB: false);
            if (d < 0 || set != 'A')
            {
                return null;
            }

            digits.Append((char)('0' + d));
            at += 4;
        }

        if (Variance(runs.Slice(at, 3), [1, 1, 1], 0.75f) > 0.48f)
        {
            return null;
        }

        int end = at + 3;
        // EAN-8 has a weaker check than EAN-13, so it needs its full quiet zones (7 modules; 5 accepted).
        float module = (offsets[Math.Min(end, offsets.Length - 1)] - offsets[start]) / (ean13 ? 95f : 67f);
        if (!QuietAfter(runs, end, module * (ean13 ? 3 : 5)) || (!ean13 && !QuietBefore(runs, start, module * 5)))
        {
            return null;
        }

        string text = digits.ToString();
        BarcodeFormat format = BarcodeFormat.Ean8;
        if (ean13)
        {
            int first = Array.IndexOf(s_ean13Parity, parity.ToString());
            if (first < 0)
            {
                return null;
            }

            text = (char)('0' + first) + text;
            format = BarcodeFormat.Ean13;
            if (first == 0 && (formats & BarcodeFormat.UpcA) != 0)
            {
                (text, format) = (text[1..], BarcodeFormat.UpcA);
            }
            else if ((formats & BarcodeFormat.Ean13) == 0)
            {
                return null;
            }
        }

        if (!CheckDigitValid(text))
        {
            return null;
        }

        return Finish(runs, offsets, start, end, text, format);
    }

    private static LinearRead? UpcE(ReadOnlySpan<float> runs, ReadOnlySpan<float> offsets, int start, int at, BarcodeFormat formats)
    {
        if ((formats & BarcodeFormat.UpcE) == 0 || at + 24 + 6 > runs.Length)
        {
            return null;
        }

        var digits = new StringBuilder();
        var parity = new StringBuilder();
        for (int k = 0; k < 6; k++)
        {
            (int d, char set) = EanDigit(runs.Slice(at, 4), allowB: true);
            if (d < 0)
            {
                return null;
            }

            digits.Append((char)('0' + d));
            parity.Append(set);
            at += 4;
        }

        // UPC-E has the weakest check, so its guards and quiet zones are held to more.
        float unit = (offsets[Math.Min(at + 6, offsets.Length - 1)] - offsets[start]) / 51f;
        if (Variance(runs.Slice(at, 6), [1, 1, 1, 1, 1, 1], 0.6f) > 0.35f || Variance(runs.Slice(start, 3), [1, 1, 1], 0.6f) > 0.35f
            || !QuietAfter(runs, at + 6, unit * 5) || !QuietBefore(runs, start, unit * 5))
        {
            return null;
        }

        // The parity gives the number system and the check digit.
        string p = parity.ToString();
        int check = Array.IndexOf(s_upcEParity, p), system = 0;
        if (check < 0)
        {
            string inverted = new([.. p.Select(c => c == 'A' ? 'B' : 'A')]);
            check = Array.IndexOf(s_upcEParity, inverted);
            system = 1;
        }

        if (check < 0)
        {
            return null;
        }

        string six = digits.ToString();
        string expanded = Linear.EanUpcEncoder.ExpandUpcE(system.ToString(System.Globalization.CultureInfo.InvariantCulture), six);
        if (Linear.EanUpcEncoder.CheckDigit(expanded) != (char)('0' + check))
        {
            return null;
        }

        return Finish(runs, offsets, start, at + 6, $"{system}{six}{check}", BarcodeFormat.UpcE);
    }

    private static bool CheckDigitValid(string digits) => Linear.EanUpcEncoder.CheckDigit(digits[..^1]) == digits[^1];

    /// <summary>Adds an EAN-2 or EAN-5 add-on found 5 to 15 modules after the symbol.</summary>
    private static LinearRead Finish(ReadOnlySpan<float> runs, ReadOnlySpan<float> offsets, int start, int end, string text, BarcodeFormat format)
    {
        float module = (offsets[Math.Min(end, offsets.Length - 1)] - offsets[start]) / (format switch { BarcodeFormat.Ean8 => 67f, BarcodeFormat.UpcE => 51f, _ => 95f });
        string? addOn = null;
        if (end + 1 < runs.Length && runs[end] >= module * 4 && runs[end] <= module * 16 && AddOn(runs, end + 1) is (string found, int addOnEnd))
        {
            (addOn, end) = (found, addOnEnd);
        }

        return Result(new DecodedData(format, text, Encoding.ASCII.GetBytes(text)) { AddOn = addOn }, offsets, start, end);
    }

    private static (string, int)? AddOn(ReadOnlySpan<float> runs, int at)
    {
        if (at + 3 + 4 > runs.Length || Variance(runs.Slice(at, 3), [1, 1, 2], 0.6f) > 0.35f)
        {
            return null;
        }

        int p = at + 3;
        var digits = new StringBuilder();
        var parity = new StringBuilder();
        for (int k = 0; k < 5; k++)
        {
            if (k > 0)
            {
                if (p + 2 + 4 > runs.Length || Variance(runs.Slice(p, 2), [1, 1], 0.6f) > 0.35f)
                {
                    break;
                }

                p += 2;
            }

            if (p + 4 > runs.Length)
            {
                break;
            }

            (int d, char set) = EanDigit(runs.Slice(p, 4), allowB: true);
            if (d < 0)
            {
                break;
            }

            digits.Append((char)('0' + d));
            parity.Append(set);
            p += 4;
        }

        string text = digits.ToString(), parities = parity.ToString();
        if (text.Length == 2)
        {
            int value = int.Parse(text, System.Globalization.CultureInfo.InvariantCulture) % 4;
            return parities == (value switch { 0 => "AA", 1 => "AB", 2 => "BA", _ => "BB" }) ? (text, p) : null;
        }

        if (text.Length == 5)
        {
            int sum = (3 * (text[0] + text[2] + text[4] - (3 * '0'))) + (9 * (text[1] + text[3] - (2 * '0')));
            return parities == s_ean5Parity[sum % 10] ? (text, p) : null;
        }

        return null;
    }
}
