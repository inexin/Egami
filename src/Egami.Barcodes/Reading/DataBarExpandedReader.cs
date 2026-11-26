using System.Numerics;
using System.Runtime.Intrinsics;
using System.Text;

using Egami.Barcodes.Linear;

namespace Egami.Barcodes.Reading;

/// <summary>
/// Reads GS1 DataBar Expanded and Expanded Stacked (ISO/IEC 24724:2011 7.2). A line yields segments: runs of blocks, each a
/// finder with the character on its left (read forwards) and usually one on its right (read backwards), in the order the
/// symbol reads. Segments from one line or from several rows join in the finder sequence of Table 16; the check character
/// confirms the whole, and the binary data decodes by the encodation methods of 7.2.5 and the general-purpose field rules.
/// </summary>
internal static class DataBarExpandedReader
{
    /// <summary>
    /// A finder (0 for A1 to 11 for F2) with its characters' values and widths (4 bits each, the first lowest);
    /// <see cref="Right"/> is -1 when there is none.
    /// </summary>
    internal readonly record struct Block(int Finder, int Left, int Right, int LeftWidths, int RightWidths)
    {
        public bool SameAs(in Block other) => Finder == other.Finder && Left == other.Left && Right == other.Right;
    }

    [ThreadStatic]
    private static List<Block>? t_chain;

    /// <summary>Finds the segments on a line whose runs start light, each read in the line's direction.</summary>
    public static void Segments(ReadOnlySpan<float> runs, ReadOnlySpan<float> offsets, List<(Block[] Blocks, float Start, float End)> segments)
    {
        if (runs.Length < 13)
        {
            return;
        }

        int words = (runs.Length >> 6) + 1;
        Span<ulong> places = words <= 256 ? stackalloc ulong[words] : new ulong[words];
        places.Clear();
        Places(runs, offsets, places);
        List<Block> chain = t_chain ??= [];
        chain.Clear();
        float chainStart = 0, chainEnd = 0;
        for (int i = Next(places, 8); i + 5 <= runs.Length; i = Next(places, i + 1))
        {
            if (Read(runs, offsets, i) is not Block block)
            {
                continue;
            }

            chainStart = offsets[i - 8];
            chain.Add(block);

            // The next block, 21 elements on, before any finder that a character in between might look like.
            while (block.Right >= 0 && i + 26 <= runs.Length && Read(runs, offsets, i + 21) is Block next)
            {
                (block, i) = (next, i + 21);
                chain.Add(block);
            }

            chainEnd = offsets[block.Right >= 0 ? i + 13 : i + 5];
            segments.Add(([.. chain], chainStart, chainEnd));
            chain.Clear();
            i += block.Right >= 0 ? 12 : 4;
        }
    }

    /// <summary>
    /// Marks the runs where a finder may start, a bit each: a finder is 15 modules, its middle element 3 to 9 of them and its
    /// two ends at its narrow side 2 (tested without dividing, and 8 places at a time, as most places are not a finder).
    /// </summary>
    private static void Places(ReadOnlySpan<float> runs, ReadOnlySpan<float> offsets, Span<ulong> places)
    {
        int i = 8;
        if (Vector256.IsHardwareAccelerated)
        {
            // Lanes at even runs (finders A1, B1, ...) test the last two elements, at odd runs the first two.
            Vector256<float> five = Vector256.Create(5f), even = Vector256.Create(-1, 0, -1, 0, -1, 0, -1, 0).AsSingle();
            for (; i + 12 <= runs.Length; i += 8)
            {
                Vector256<float> total = Vector256.Create(offsets.Slice(i + 5, 8)) - Vector256.Create(offsets.Slice(i, 8));
                Vector256<float> ends = Vector256.ConditionalSelect(even, Vector256.Create(runs.Slice(i + 3, 8)) + Vector256.Create(runs.Slice(i + 4, 8)),
                    Vector256.Create(runs.Slice(i, 8)) + Vector256.Create(runs.Slice(i + 1, 8)));
                Vector256<float> fits = Vector256.LessThanOrEqual(ends * five, total) & Vector256.GreaterThanOrEqual(Vector256.Create(runs.Slice(i + 2, 8)) * five, total);
                places[i >> 6] |= (ulong)fits.ExtractMostSignificantBits() << (i & 63);
            }
        }

        for (; i + 5 <= runs.Length; i++)
        {
            float total = offsets[i + 5] - offsets[i], ends = i % 2 == 0 ? runs[i + 3] + runs[i + 4] : runs[i] + runs[i + 1];
            if (ends * 5 <= total && runs[i + 2] * 5 >= total)
            {
                places[i >> 6] |= 1UL << (i & 63);
            }
        }
    }

    /// <summary>The first marked place from <paramref name="i"/> on, or past the end when there is none.</summary>
    private static int Next(ReadOnlySpan<ulong> places, int i)
    {
        int word = i >> 6;
        if (word >= places.Length)
        {
            return int.MaxValue - 64;
        }

        ulong bits = places[word] & (ulong.MaxValue << (i & 63));
        while (bits == 0)
        {
            if (++word == places.Length)
            {
                return int.MaxValue - 64;
            }

            bits = places[word];
        }

        return (word << 6) + BitOperations.TrailingZeroCount(bits);
    }

    /// <summary>The block whose finder starts at run <paramref name="i"/>, with the character on its left and any on its right.</summary>
    private static Block? Read(ReadOnlySpan<float> runs, ReadOnlySpan<float> offsets, int i)
    {
        // Widths from the offsets, which are where the runs start: a finder is 15 modules.
        float total = offsets[i + 5] - offsets[i];
        if (total <= 0)
        {
            return null;
        }

        // Read in the symbol's order, finders A1, B1, ... (at even runs) end with two single modules and A2, B2, ... (at odd
        // runs) start with them; the middle element is 4 to 9 modules.
        float unit = total / 15;
        float ends = i % 2 == 0 ? runs[i + 3] + runs[i + 4] : runs[i] + runs[i + 1];
        if (ends > 3 * unit || runs[i + 2] < 3 * unit)
        {
            return null;
        }

        // The character on the left is 17 modules, near the finder's module.
        float leftWidth = offsets[i] - offsets[i - 8];
        if (MathF.Abs((leftWidth / 17) - unit) > 0.25f * unit)
        {
            return null;
        }

        Span<float> finder = stackalloc float[5], elements = stackalloc float[8];
        for (int k = 0; k < 5; k++)
        {
            finder[k] = runs[i + k] / unit;
        }

        // Read in the symbol's order, finders A1, B1, ... start light and A2, B2, ... dark.
        int value = DataBarReader.FinderValue(finder, DataBarTables.ExpandedFinders, 12, i % 2 == 0 ? 1 : 0, out float spread, i % 2, 2);
        if (value < 0)
        {
            return null;
        }

        for (int k = 0; k < 8; k++)
        {
            elements[k] = DataBarReader.Corrected(runs[i - 8 + k], leftWidth / 17, i - 8 + k, spread);
        }

        int leftWidths = Character(elements, out int left), rightWidths = 0, right = -1;
        if (leftWidths == 0)
        {
            return null;
        }

        if (i + 13 <= runs.Length)
        {
            float rightWidth = offsets[i + 13] - offsets[i + 5];
            if (MathF.Abs((rightWidth / 17) - unit) <= 0.25f * unit)
            {
                for (int k = 0; k < 8; k++)
                {
                    elements[k] = DataBarReader.Corrected(runs[i + 12 - k], rightWidth / 17, i + 12 - k, spread);
                }

                rightWidths = Character(elements, out right);
            }
        }

        return new Block(value, left, right, leftWidths, rightWidths);
    }

    /// <summary>The value of a symbol character from its 8 widths in modules, and its integer widths (4 bits each), or 0.</summary>
    internal static int Character(ReadOnlySpan<float> elements, out int value)
    {
        value = -1;
        float odd = elements[0] + elements[2] + elements[4] + elements[6];
        Span<int> order = [0, 1, 2, 3, 4];
        DataBarReader.SortByDistance(order, odd, DataBarTables.ExpandedModules);
        Span<int> widths = stackalloc int[8];
        Span<float> half = stackalloc float[4];
        Span<int> rounded = stackalloc int[4];
        for (int attempt = 0; attempt < 2; attempt++)
        {
            int group = order[attempt];
            int oddModules = DataBarTables.ExpandedModules[group], widest = DataBarTables.ExpandedWidest[group];
            if (!DataBarReader.Half(elements, 0, oddModules, half, rounded, widths) || !DataBarReader.Half(elements, 1, 17 - oddModules, half, rounded, widths))
            {
                continue;
            }

            int oddValue = DataBarWidths.Value(DataBarReader.Pick(widths, 0, rounded), widest, true);
            int evenValue = DataBarWidths.Value(DataBarReader.Pick(widths, 1, rounded), 9 - widest, false);
            int t = DataBarTables.ExpandedEvenCombinations[group];
            int odds = group == 4 ? 1 : (DataBarTables.ExpandedGroupSum[group + 1] - DataBarTables.ExpandedGroupSum[group]) / t;
            if (oddValue < 0 || evenValue < 0 || evenValue >= t || oddValue >= odds)
            {
                continue;
            }

            value = DataBarTables.ExpandedGroupSum[group] + (oddValue * t) + evenValue;
            if (value < 4096)
            {
                int packed = 0;
                for (int j = 0; j < 8; j++)
                {
                    packed |= widths[j] << (4 * j);
                }

                return packed;
            }
        }

        value = -1;
        return 0;
    }

    /// <summary>Whether the blocks, from one of them on, can stand in the finder sequence <paramref name="finders"/>.</summary>
    public static bool Fits(ReadOnlySpan<Block> blocks, ReadOnlySpan<byte> finders)
    {
        for (int o = 0; o < blocks.Length; o++)
        {
            int place = finders.IndexOf((byte)(blocks[o].Finder + 1));
            if (place < 0 || place + blocks.Length - o > finders.Length)
            {
                continue;
            }

            bool fits = true;
            for (int k = o + 1; k < blocks.Length && fits; k++)
            {
                fits = blocks[k].Finder == finders[place + k - o] - 1;
            }

            if (fits)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The check sum mod 211 of a symbol of <paramref name="finders"/> and <paramref name="symbolCharacters"/> characters with
    /// the blocks placed from <paramref name="place"/> on, from the sum so far; null when they do not fit there. The first
    /// block's left character, the check character, counts as minus its part mod 211, so that a whole symbol sums to 0.
    /// </summary>
    public static int? Sum(ReadOnlySpan<Block> blocks, int place, ReadOnlySpan<byte> finders, int symbolCharacters, int sum)
    {
        int dataCharacters = symbolCharacters - 1, rows = ((dataCharacters - 2) / 2) * 21;
        for (int k = 0; k < blocks.Length; k++, place++)
        {
            Block block = blocks[k];
            bool last = place == finders.Length - 1;
            if (block.Finder != finders[place] - 1 || (last ? (block.Right < 0) != (symbolCharacters % 2 == 1) : block.Right < 0))
            {
                return null;
            }

            if (place == 0)
            {
                int check = block.Left - (211 * (symbolCharacters - 4));
                if (check is < 0 or >= 211)
                {
                    return null;
                }

                sum += 211 - check;
            }
            else
            {
                sum += Weighted(block.LeftWidths, DataBarTables.ExpandedWeightRows[rows + (2 * place) - 1]);
            }

            if (2 * place < dataCharacters)
            {
                sum += Weighted(block.RightWidths, DataBarTables.ExpandedWeightRows[rows + (2 * place)]);
            }

            sum %= 211;
        }

        return sum;
    }

    /// <summary>A character's part of the check sum: its widths (4 bits each) by the weights of its row.</summary>
    private static int Weighted(int widths, int row)
    {
        int sum = 0;
        for (int j = 0; j < 8; j++)
        {
            sum += ((widths >> (4 * j)) & 15) * DataBarTables.ExpandedChecksumWeights[(row * 8) + j];
        }

        return sum;
    }

    /// <summary>The symbol the blocks make, when they are a whole finder sequence and the check character holds.</summary>
    public static DecodedData? Decode(ReadOnlySpan<Block> blocks)
    {
        int count = blocks.Length;
        if (count is < 2 or > 11)
        {
            return null;
        }

        int sequence = count - 2;
        for (int b = 0; b < count; b++)
        {
            if (blocks[b].Finder != DataBarTables.ExpandedFinderSequences[(sequence * 11) + b] - 1 || (b < count - 1 && blocks[b].Right < 0))
            {
                return null;
            }
        }

        int symbolCharacters = (2 * count) - (blocks[^1].Right < 0 ? 1 : 0);
        if (symbolCharacters < 4)
        {
            return null;
        }

        int dataCharacters = symbolCharacters - 1, checksum = 0;
        Span<int> values = stackalloc int[dataCharacters];
        for (int i = 0; i < dataCharacters; i++)
        {
            // Data character i is the right of block i / 2 when i is even, the left of block (i + 1) / 2 when odd.
            Block block = blocks[(i + 1) / 2];
            int widths = i % 2 == 1 ? block.LeftWidths : blocks[i / 2].RightWidths;
            values[i] = i % 2 == 1 ? block.Left : blocks[i / 2].Right;
            checksum += Weighted(widths, DataBarTables.ExpandedWeightRows[(((dataCharacters - 2) / 2) * 21) + i]);
        }

        if (blocks[0].Left != (211 * (symbolCharacters - 4)) + (checksum % 211))
        {
            return null;
        }

        // Symbols made to the first edition of the standard stay in the alphanumeric or ISO/IEC 646 mode after an FNC1; when the
        // data does not make valid element strings as this edition reads it, it is read that way. Element strings that are
        // still not valid come back as they are, as other readers return them.
        string? raw = Data(values, symbolCharacters, false);
        if (raw is not null && Gs1Raw.TryParse(raw, out IReadOnlyList<Gs1ElementString>? elements))
        {
            return new DecodedData(BarcodeFormat.DataBarExpanded, string.Concat(elements), Encoding.ASCII.GetBytes(raw)) { Gs1 = elements };
        }

        string? legacy = Data(values, symbolCharacters, true);
        if (legacy is not null && legacy != raw && Gs1Raw.TryParse(legacy, out elements))
        {
            return new DecodedData(BarcodeFormat.DataBarExpanded, string.Concat(elements), Encoding.ASCII.GetBytes(legacy)) { Gs1 = elements };
        }

        foreach (string? data in (ReadOnlySpan<string?>)[raw, legacy])
        {
            if (data is not null && Gs1Raw.Shown(data) is string shown)
            {
                return new DecodedData(BarcodeFormat.DataBarExpanded, shown, Encoding.ASCII.GetBytes(data));
            }
        }

        return null;
    }

    /// <summary>
    /// The element strings the binary data holds, AIs without parentheses and FNC1 as GS, or <see langword="null"/>;
    /// <paramref name="legacy"/> keeps the mode after an FNC1, as the first edition of the standard did.
    /// </summary>
    internal static string? Data(ReadOnlySpan<int> values, int symbolCharacters, bool legacy)
    {
        var bits = new Bits(values);
        var text = new StringBuilder();
        int at, variableLength;

        // 7.2.5.2 and Table 11: the encodation method from its leading bits (after the linkage flag).
        if (bits[1])
        {
            // Method 1: (01) with its digits, then the general field.
            text.Append("01").Append((char)('0' + bits.Read(4, 4)));
            if (!Gtin12(bits, 8, text))
            {
                return null;
            }

            (at, variableLength) = (48, 2);
        }
        else if (!bits[2])
        {
            (at, variableLength) = (5, 3);
        }
        else if (!bits[3])
        {
            // Methods 3 and 4: (01) with an indicator digit 9 and a weight in kilograms or pounds; nothing else.
            if (bits.Length != 60)
            {
                return null;
            }

            text.Append("019");
            if (!Gtin12(bits, 5, text))
            {
                return null;
            }

            int weight = bits.Read(45, 15);
            if (!bits[4])
            {
                text.Append("3103").Append(weight.ToString("D6", System.Globalization.CultureInfo.InvariantCulture));
            }
            else
            {
                text.Append(weight >= 10000 ? "3203" : "3202").Append((weight >= 10000 ? weight - 10000 : weight).ToString("D6", System.Globalization.CultureInfo.InvariantCulture));
            }

            return text.ToString();
        }
        else if (!bits[4])
        {
            // Methods 5 and 6: (01) with an indicator digit 9, then (392x) or (393x) with its currency, then the general field.
            text.Append("019");
            if (!Gtin12(bits, 8, text))
            {
                return null;
            }

            text.Append(bits[5] ? "393" : "392").Append((char)('0' + bits.Read(48, 2)));
            at = 50;
            if (bits[5])
            {
                int currency = bits.Read(50, 10);
                if (currency > 999)
                {
                    return null;
                }

                text.Append(currency.ToString("D3", System.Globalization.CultureInfo.InvariantCulture));
                at = 60;
            }

            variableLength = 6;
        }
        else
        {
            // Methods 7 to 14: (01) with an indicator digit 9, a weight, and a date or none; nothing else.
            if (bits.Length != 84)
            {
                return null;
            }

            int method = 7 + bits.Read(5, 3);
            text.Append("019");
            if (!Gtin12(bits, 8, text))
            {
                return null;
            }

            int weight = bits.Read(48, 20), date = bits.Read(68, 16);
            if (weight >= 1_000_000)
            {
                return null;
            }

            text.Append(method % 2 == 1 ? "310" : "320").Append((char)('0' + (weight / 100000)))
                .Append((weight % 100000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture));
            if (date != 38400)
            {
                int yy = date / 384, mm = (date % 384 / 32) + 1, dd = date % 32;
                if (yy > 99 || mm > 12)
                {
                    return null;
                }

                text.Append('1').Append((char)('1' + ((method - 7) / 2 * 2)))
                    .Append(System.Globalization.CultureInfo.InvariantCulture, $"{yy:D2}{mm:D2}{dd:D2}");
            }

            return text.ToString();
        }

        // The variable length field: an odd number of symbol characters, and more than 14.
        if (bits[variableLength] != (symbolCharacters % 2 == 1) || bits[variableLength + 1] != (symbolCharacters > 14))
        {
            return null;
        }

        return GeneralField(bits, at, text, legacy) ? text.ToString() : null;
    }

    /// <summary>Twelve digits as four 10-bit groups from <paramref name="at"/>, and the check digit of the 13 then written.</summary>
    private static bool Gtin12(Bits bits, int at, StringBuilder text)
    {
        if (at + 40 > bits.Length)
        {
            return false;
        }

        int from = text.Length - 1;
        for (int k = 0; k < 4; k++)
        {
            int group = bits.Read(at + (10 * k), 10);
            if (group > 999)
            {
                return false;
            }

            text.Append(group.ToString("D3", System.Globalization.CultureInfo.InvariantCulture));
        }

        string digits = text.ToString(from, 13);
        text.Append((char)('0' + Gs1Data.Gs1Checks.CheckDigit(digits)));
        return true;
    }

    private enum Mode
    {
        Numeric,
        Alphanumeric,
        IsoIec,
    }

    private const string AlphanumericPunctuation = "*,-./";
    private const string IsoIecPunctuation = "!\"%&'()*+,-./:;<=>?_ ";

    /// <summary>The general-purpose field (7.2.5.5): numeric pairs, alphanumeric and ISO/IEC 646 characters, latches and padding.</summary>
    private static bool GeneralField(Bits bits, int at, StringBuilder text, bool legacy)
    {
        var mode = Mode.Numeric;
        int length = bits.Length;
        while (at < length)
        {
            int left = length - at;
            if (mode == Mode.Numeric)
            {
                if (left < 7)
                {
                    // A last digit in 4 bits, or padding.
                    int last = left >= 4 ? bits.Read(at, 4) : 0;
                    if (last is > 0 and <= 10)
                    {
                        text.Append((char)('0' + last - 1));
                    }

                    return last <= 10;
                }

                if (bits.Read(at, 4) == 0)
                {
                    mode = Mode.Alphanumeric;
                    at += 4;
                    continue;
                }

                int pair = bits.Read(at, 7) - 8;
                Digit(text, pair / 11);
                Digit(text, pair % 11);
                at += 7;
                continue;
            }

            if (left >= 3 && bits.Read(at, 3) == 0)
            {
                mode = Mode.Numeric;
                at += 3;
                continue;
            }

            if (left < 5)
            {
                return true;
            }

            int five = bits.Read(at, 5);
            if (five == 4)
            {
                mode = mode == Mode.Alphanumeric ? Mode.IsoIec : Mode.Alphanumeric;
                at += 5;
            }
            else if (five is >= 5 and < 15)
            {
                text.Append((char)('0' + five - 5));
                at += 5;
            }
            else if (five == 15)
            {
                text.Append('\x1D');
                mode = legacy ? mode : Mode.Numeric;
                at += 5;
            }
            else if (five < 16)
            {
                return false;
            }
            else if (mode == Mode.Alphanumeric)
            {
                if (left < 6)
                {
                    return true;
                }

                int six = bits.Read(at, 6);
                if (six is >= 32 and <= 57)
                {
                    text.Append((char)('A' + six - 32));
                }
                else if (six is >= 58 and <= 62)
                {
                    text.Append(AlphanumericPunctuation[six - 58]);
                }
                else
                {
                    return false;
                }

                at += 6;
            }
            else
            {
                if (left < 7)
                {
                    return true;
                }

                int seven = bits.Read(at, 7);
                if (seven is >= 64 and <= 89)
                {
                    text.Append((char)('A' + seven - 64));
                    at += 7;
                }
                else if (seven is >= 90 and <= 115)
                {
                    text.Append((char)('a' + seven - 90));
                    at += 7;
                }
                else
                {
                    if (left < 8)
                    {
                        return true;
                    }

                    int eight = bits.Read(at, 8);
                    if (eight is < 232 or > 252)
                    {
                        return false;
                    }

                    text.Append(IsoIecPunctuation[eight - 232]);
                    at += 8;
                }
            }
        }

        return true;
    }

    private static void Digit(StringBuilder text, int digit) => text.Append(digit == 10 ? '\x1D' : (char)('0' + digit));

    /// <summary>The bits of the data characters, 12 each, first bit first.</summary>
    private readonly ref struct Bits(ReadOnlySpan<int> values)
    {
        private readonly ReadOnlySpan<int> _values = values;

        public int Length => _values.Length * 12;

        public bool this[int index] => index < Length && ((_values[index / 12] >> (11 - (index % 12))) & 1) != 0;

        public int Read(int at, int count)
        {
            int value = 0;
            for (int k = 0; k < count; k++)
            {
                value = (value << 1) | (this[at + k] ? 1 : 0);
            }

            return value;
        }
    }

    private static float Sum(ReadOnlySpan<float> runs)
    {
        float total = 0;
        foreach (float r in runs)
        {
            total += r;
        }

        return total;
    }
}
