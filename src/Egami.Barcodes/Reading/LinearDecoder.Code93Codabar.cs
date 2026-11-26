using System.Text;

using Egami.Barcodes.Linear;

namespace Egami.Barcodes.Reading;

internal static partial class LinearDecoder
{
    // ---- Code 93 ----

    /// <summary>
    /// The Code 93 value of each set of four similar-edge distances (bar to next bar, space to next space, in ninths of the
    /// character, 2 to 8 each, as base-7 digits), or -1. AIM USS-93 chose the patterns so that these distances alone name
    /// every character, which makes the decode immune to ink spread.
    /// </summary>
    private static readonly sbyte[] s_code93Edges = BuildCode93Edges();

    private static sbyte[] BuildCode93Edges()
    {
        sbyte[] table = new sbyte[7 * 7 * 7 * 7];
        table.AsSpan().Fill(-1);
        Span<int> widths = stackalloc int[6];
        for (int v = 0; v < Code93Encoder.Patterns.Length; v++)
        {
            int pattern = Code93Encoder.Patterns[v], key = 0;
            for (int i = 5; i >= 0; i--, pattern /= 10)
            {
                widths[i] = pattern % 10;
            }

            for (int i = 0; i < 4; i++)
            {
                key = (key * 7) + widths[i] + widths[i + 1] - 2;
            }

            table[key] = (sbyte)v;
        }

        return table;
    }

    /// <summary>The Code 93 value (47 for the start and stop character) of 6 runs, or -1.</summary>
    private static int Code93Value(ReadOnlySpan<float> runs)
    {
        float total = runs[0] + runs[1] + runs[2] + runs[3] + runs[4] + runs[5];
        if (!(total >= 9))
        {
            return -1;
        }

        // Every element one to four modules, with room for blur and ink spread.
        float module = total / 9, lowest = module * 0.35f, highest = module * 4.9f;
        int key = 0;
        for (int i = 0; i < 6; i++)
        {
            if (runs[i] < lowest || runs[i] > highest)
            {
                return -1;
            }

            if (i < 4)
            {
                int edge = (int)MathF.Round((runs[i] + runs[i + 1]) / module);
                if (edge is < 2 or > 8)
                {
                    return -1;
                }

                key = (key * 7) + edge - 2;
            }
        }

        return s_code93Edges[key];
    }

    private static LinearRead? Code93(ReadOnlySpan<float> runs, ReadOnlySpan<float> offsets, int start)
    {
        // The quiet zone first: most positions on a line fail it, and it costs least.
        if (start + 6 > runs.Length)
        {
            return null;
        }

        float width = runs[start] + runs[start + 1] + runs[start + 2] + runs[start + 3] + runs[start + 4] + runs[start + 5];
        if (!QuietBefore(runs, start, width / 2f) || Code93Value(runs.Slice(start, 6)) != Code93Encoder.StartStop)
        {
            return null;
        }

        Span<int> buffer = stackalloc int[64];
        var values = new ValueList(buffer);
        int at = start + 6;
        while (true)
        {
            if (at + 6 > runs.Length)
            {
                return null;
            }

            ReadOnlySpan<float> character = runs.Slice(at, 6);
            float characterWidth = Width(character);
            if (characterWidth < width * 0.7f || characterWidth > width * 1.45f)
            {
                return null;
            }

            int value = Code93Value(character);
            if (value < 0)
            {
                return null;
            }

            width = characterWidth;
            at += 6;
            if (value == Code93Encoder.StartStop)
            {
                break;
            }

            values.Add(value);
        }

        // The termination bar, one module, then the quiet zone.
        float barModule = width / 9;
        if (at >= runs.Length || runs[at] < barModule * 0.35f || runs[at] > barModule * 2.2f || !QuietAfter(runs, at + 1, width / 2f))
        {
            return null;
        }

        // A data character and the check characters C and K at least.
        ReadOnlySpan<int> symbol = values.Span;
        if (symbol.Length < 3 || Code93Check(symbol[..^2], 20) != symbol[^2] || Code93Check(symbol[..^1], 15) != symbol[^1])
        {
            return null;
        }

        string? text = Code93Text(symbol[..^2]);
        if (text is null)
        {
            return null;
        }

        return Result(new DecodedData(BarcodeFormat.Code93, text, Encoding.Latin1.GetBytes(text)), offsets, start, at + 1);
    }

    private static int Code93Check(ReadOnlySpan<int> values, int maxWeight)
    {
        int sum = 0;
        for (int i = values.Length - 1, weight = 1; i >= 0; i--)
        {
            sum += values[i] * weight;
            weight = weight == maxWeight ? 1 : weight + 1;
        }

        return sum % 47;
    }

    /// <summary>The text of Code 93 data values, Full ASCII shifts decoded, or null when a shift is not followed by a letter it allows.</summary>
    private static string? Code93Text(ReadOnlySpan<int> values)
    {
        var text = new StringBuilder(values.Length);
        for (int i = 0; i < values.Length; i++)
        {
            int v = values[i];
            if (v < 43)
            {
                text.Append(Code93Encoder.Characters[v]);
                continue;
            }

            if (i + 1 >= values.Length || values[i + 1] is < 10 or > 35)
            {
                return null;
            }

            char letter = (char)('A' + values[++i] - 10);
            int? decoded = v switch
            {
                43 => letter - 'A' + 1,
                44 when letter <= 'E' => letter - 'A' + 27,
                44 when letter <= 'J' => letter - 'F' + ';',
                44 when letter <= 'O' => "[\\]^_"[letter - 'K'],
                44 when letter <= 'S' => "{|}~"[letter - 'P'],
                44 when letter == 'U' => 0,
                44 when letter == 'V' => '@',
                44 when letter == 'W' => '`',
                44 => 127,
                45 when letter <= 'O' => letter - 'A' + '!',
                45 when letter == 'Z' => ':',
                46 => letter - 'A' + 'a',
                _ => null,
            };
            if (decoded is not int c)
            {
                return null;
            }

            text.Append((char)c);
        }

        return text.ToString();
    }

    /// <summary>A list of ints on a stack buffer that moves to the heap when it fills.</summary>
    private ref struct ValueList(Span<int> buffer)
    {
        private Span<int> _items = buffer;
        private int _count;

        public readonly ReadOnlySpan<int> Span => _items[.._count];

        public void Add(int value)
        {
            if (_count == _items.Length)
            {
                int[] grown = new int[_items.Length * 2];
                _items.CopyTo(grown);
                _items = grown;
            }

            _items[_count++] = value;
        }
    }

    /// <summary>The total width of runs, in pixels.</summary>
    private static float Width(ReadOnlySpan<float> runs)
    {
        float total = 0;
        foreach (float r in runs)
        {
            total += r;
        }

        return total;
    }

    // ---- Codabar ----

    /// <summary>The Codabar character index of each 7-bit wide-element pattern, or -1.</summary>
    private static readonly sbyte[] s_codabar = BuildCodabar();

    private static sbyte[] BuildCodabar()
    {
        sbyte[] table = new sbyte[128];
        table.AsSpan().Fill(-1);
        for (int v = 0; v < CodabarEncoder.Patterns.Length; v++)
        {
            table[CodabarEncoder.Patterns[v]] = (sbyte)v;
        }

        return table;
    }

    /// <summary>The quiet zone a Codabar symbol needs before and after it, in its first or last character's widths.</summary>
    private const float CodabarQuiet = 0.6f;

    private static LinearRead? Codabar(ReadOnlySpan<float> runs, ReadOnlySpan<float> offsets, int start)
    {
        // The quiet zone first, as for Code 93; then the start character.
        if (start + 7 > runs.Length)
        {
            return null;
        }

        float width = Width(runs.Slice(start, 7));
        if (!QuietBefore(runs, start, width * CodabarQuiet))
        {
            return null;
        }

        int first = CodabarCharacter(runs.Slice(start, 7));
        if (first < 16)
        {
            return null;
        }

        var text = new StringBuilder();
        text.Append(CodabarEncoder.Characters[first]);
        int at = start + 7;
        while (true)
        {
            // A narrow gap, then the next character.
            if (at + 8 > runs.Length || runs[at] > width * 0.4f)
            {
                return null;
            }

            ReadOnlySpan<float> character = runs.Slice(at + 1, 7);
            float characterWidth = Width(character);
            if (characterWidth < width * 0.7f || characterWidth > width * 1.45f)
            {
                return null;
            }

            int value = CodabarCharacter(character);
            if (value < 0)
            {
                return null;
            }

            width = characterWidth;
            text.Append(CodabarEncoder.Characters[value]);
            at += 8;
            if (value >= 16)
            {
                break;
            }
        }

        if (text.Length < CodabarEncoder.MinimumLength || !QuietAfter(runs, at, width * CodabarQuiet))
        {
            return null;
        }

        string result = text.ToString();
        return Result(new DecodedData(BarcodeFormat.Codabar, result, Encoding.ASCII.GetBytes(result)), offsets, start, at);
    }

    /// <summary>
    /// The Codabar character index of 7 runs, or -1: two or three of them are wide. When ink spread makes the bars much
    /// thinner or thicker than the spaces, bars and spaces are told wide or narrow apart: one wide bar and one or two wide
    /// spaces, or three wide bars and no wide space.
    /// </summary>
    internal static int CodabarCharacter(ReadOnlySpan<float> runs)
    {
        Span<float> sorted = stackalloc float[7];
        runs.CopyTo(sorted);
        sorted.Sort();
        if (sorted[6] > sorted[0] * 10)
        {
            return -1;
        }

        // The split with the larger step: two wide (between sorted 4 and 5) or three wide (between 3 and 4).
        float two = sorted[5] / sorted[4], three = sorted[4] / sorted[3];
        float threshold = two >= three ? sorted[5] : sorted[4];
        if (MathF.Max(two, three) >= 1.5f)
        {
            int found = s_codabar[Pattern(runs, threshold, threshold)];
            if (found >= 0)
            {
                return found;
            }
        }

        Span<float> bars = [runs[0], runs[2], runs[4], runs[6]];
        Span<float> spaces = [runs[1], runs[3], runs[5]];
        bars.Sort();
        spaces.Sort();
        float barsWide, spacesWide;
        if (bars[3] >= bars[2] * 1.5f)
        {
            barsWide = bars[3];
            if (spaces[2] >= spaces[1] * 1.5f)
            {
                spacesWide = spaces[2];
            }
            else if (spaces[1] >= spaces[0] * 1.5f)
            {
                spacesWide = spaces[1];
            }
            else
            {
                return -1;
            }
        }
        else if (bars[1] >= bars[0] * 1.5f && spaces[2] < spaces[0] * 1.5f)
        {
            (barsWide, spacesWide) = (bars[1], float.MaxValue);
        }
        else
        {
            return -1;
        }

        return s_codabar[Pattern(runs, barsWide, spacesWide)];

        static int Pattern(ReadOnlySpan<float> runs, float barsWide, float spacesWide)
        {
            int pattern = 0;
            for (int i = 0; i < 7; i++)
            {
                pattern = (pattern << 1) | (runs[i] >= (i % 2 == 0 ? barsWide : spacesWide) ? 1 : 0);
            }

            return pattern;
        }
    }
}
