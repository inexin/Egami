namespace Egami.Barcodes.Linear;

/// <summary>
/// Builds GS1 DataBar Expanded and Expanded Stacked symbols (ISO/IEC 24724:2011 7.2): the element strings compacted into
/// 12-bit symbol characters by the encodation methods of 7.2.5 and the general-purpose field rules of 7.2.5.5, a check
/// character, finder pairs in the sequence of Table 16, and for stacked symbols rows with separators as zint's backend lays
/// them out (BSD-3; see THIRD-PARTY-NOTICES.md), so that Egami's symbols equal zint's.
/// </summary>
internal static class DataBarExpandedEncoder
{
    private const char Fnc1 = '\x1D';

    private enum Field
    {
        Numeric,
        Alphanumeric,
        IsoIec,
    }

    /// <summary>The element widths of the symbol: guards, the check character, finders and data characters, first a space.</summary>
    /// <param name="data">The element strings run together, AIs without parentheses, with FNC1 after each of variable length but the last.</param>
    /// <param name="columns">Segment pairs per row for Expanded Stacked, or 0 for one row.</param>
    /// <param name="symbolCharacters">The symbol characters, the check character included.</param>
    /// <param name="error">Why the data does not fit, or <see langword="null"/>.</param>
    public static int[]? Elements(string data, ref int columns, out int symbolCharacters, out string? error)
    {
        List<bool>? bits = Bits(data, ref columns, out error);
        symbolCharacters = 0;
        if (bits is null)
        {
            return null;
        }

        int dataCharacters = bits.Count / 12;
        symbolCharacters = dataCharacters + 1;
        var widths = new int[dataCharacters][];
        int checksum = 0;
        for (int i = 0; i < dataCharacters; i++)
        {
            int value = 0;
            for (int j = 0; j < 12; j++)
            {
                value = (value << 1) | (bits[(i * 12) + j] ? 1 : 0);
            }

            widths[i] = Character(value);
            int row = DataBarTables.ExpandedWeightRows[(((dataCharacters - 2) / 2) * 21) + i];
            for (int j = 0; j < 8; j++)
            {
                checksum += widths[i][j] * DataBarTables.ExpandedChecksumWeights[(row * 8) + j];
            }
        }

        int[] check = Character((211 * (symbolCharacters - 4)) + (checksum % 211));
        int blocks = (symbolCharacters + 1) / 2;
        int[] elements = new int[(blocks * 5) + (symbolCharacters * 8) + 4];
        int sequence = ((symbolCharacters - 1) / 2) - 1;
        for (int i = 0; i < blocks; i++)
        {
            int finder = DataBarTables.ExpandedFinderSequences[(sequence * 11) + i] - 1;
            for (int j = 0; j < 5; j++)
            {
                elements[(21 * i) + j + 10] = DataBarTables.ExpandedFinders[(finder * 5) + j];
            }
        }

        check.CopyTo(elements, 2);
        for (int i = 0; i < dataCharacters; i++)
        {
            // Data characters 1, 3, ... read forwards on the left of a finder; 0, 2, ... backwards on its right.
            int at = i % 2 == 1 ? ((i - 1) / 2 * 21) + 23 : (i / 2 * 21) + 15;
            for (int j = 0; j < 8; j++)
            {
                elements[at + j] = widths[i][i % 2 == 1 ? j : 7 - j];
            }
        }

        return elements;
    }

    /// <summary>The 8 element widths of a symbol character (17 modules).</summary>
    public static int[] Character(int value)
    {
        int group = 4;
        while (value < DataBarTables.ExpandedGroupSum[group])
        {
            group--;
        }

        int v = value - DataBarTables.ExpandedGroupSum[group], t = DataBarTables.ExpandedEvenCombinations[group];
        int modules = DataBarTables.ExpandedModules[group];
        int[] widths = new int[8];
        DataBarWidths.Character(widths, v / t, v % t, modules, 17 - modules, 4, DataBarTables.ExpandedWidest[group], true);
        return widths;
    }

    /// <summary>A one-row symbol's elements with their guards.</summary>
    public static int[] WithGuards(int[] elements)
    {
        int[] guarded = (int[])elements.Clone();
        guarded[0] = guarded[1] = guarded[^2] = guarded[^1] = 1;
        return guarded;
    }

    /// <summary>Lays the elements out in rows of <paramref name="columns"/> segment pairs, with separator rows between, each row <paramref name="rowHeight"/> tall.</summary>
    public static BarcodeModules Stacked(int[] elements, int symbolCharacters, int columns, int rowHeight)
    {
        int blocks = (symbolCharacters + 1) / 2;
        int dataCharacters = symbolCharacters - 1;
        int stackRows = (blocks + columns - 1) / columns;
        var rows = new List<bool[]>();
        int width = 0;
        int block = 0;
        bool v2Latch = false;
        bool[]? pendingTop = null;
        var heights = new List<int>();
        for (int row = 1; row <= stackRows; row++)
        {
            int count = row * columns > blocks ? blocks - block : columns;
            bool special = row == stackRows && count != columns && row % 2 == 0 && columns % 2 == 0 && count % 2 == 1;
            bool leftToRight = columns % 2 == 1 || row % 2 == 1 || special;
            var sub = new List<int> { special ? 2 : 1, 1 };
            int[] segment = new int[count * 21];
            for (int reader = 0; reader < count; reader++, block++)
            {
                int start = 2 + (block * 21);
                for (int j = 0; j < 21; j++)
                {
                    int value = start + j < elements.Length - 2 ? elements[start + j] : 0;
                    segment[leftToRight ? j + (reader * 21) : 20 - j + ((count - 1 - reader) * 21)] = value;
                }
            }

            sub.AddRange(segment);
            sub.Add(1);
            sub.Add(1);
            bool dark = !(row % 2 == 1 || special);
            int rowWidth = 0;
            foreach (int w in sub)
            {
                rowWidth += w;
            }

            bool[] modules = new bool[Math.Max(rowWidth, 49 * columns + 4)];
            Expand(modules, dark, sub);
            if (row != 1)
            {
                bool[] middle = new bool[modules.Length];
                for (int j = 5; j < 49 * columns; j += 2)
                {
                    middle[j] = true;
                }

                bool oddLastRow = row == stackRows && dataCharacters % 2 == 0;
                bool[] bottom = new bool[modules.Length];
                bool latch = v2Latch;
                Separator(bottom, modules, rowWidth, count, special, leftToRight, oddLastRow, ref latch);
                rows.Add(pendingTop!);
                heights.Add(1);
                rows.Add(middle);
                heights.Add(1);
                rows.Add(bottom);
                heights.Add(1);
            }

            rows.Add(modules);
            heights.Add(rowHeight);
            width = Math.Max(width, rowWidth);
            if (row != stackRows)
            {
                pendingTop = new bool[modules.Length];
                Separator(pendingTop, modules, rowWidth, count, false, leftToRight, false, ref v2Latch);
            }
        }

        int height = 0;
        foreach (int h in heights)
        {
            height += h;
        }

        bool[] grid = new bool[width * height];
        int y = 0;
        for (int r = 0; r < rows.Count; r++)
        {
            for (int k = 0; k < heights[r]; k++, y++)
            {
                rows[r].AsSpan(0, Math.Min(width, rows[r].Length)).CopyTo(grid.AsSpan(y * width, width));
            }
        }

        return BarcodeModules.Wrap(width, height, grid);
    }

    private static void Expand(Span<bool> row, bool dark, List<int> widths)
    {
        int x = 0;
        foreach (int width in widths)
        {
            row.Slice(x, width).Fill(dark);
            x += width;
            dark = !dark;
        }
    }

    /// <summary>
    /// A separator row next to a data row: light over its bars and dark over its spaces, and over each finder 13 modules that
    /// alternate, starting from the finder's end that the row is read from.
    /// </summary>
    private static void Separator(Span<bool> separator, ReadOnlySpan<bool> row, int width, int columns, bool special, bool leftToRight, bool oddLastRow, ref bool v2Latch)
    {
        int shift = special ? 1 : 0;
        for (int j = 4 + shift; j < width - 4; j++)
        {
            separator[j] = !row[j];
        }

        bool latch = v2Latch, space = false;
        for (int j = 0; j < columns; j++)
        {
            int k = (49 * j) + 19 + shift;
            if (leftToRight)
            {
                for (int i = latch ? 2 : 0; i < (latch ? 15 : 13); i++)
                {
                    Alternate(separator, row, i + k, ref space);
                }
            }
            else
            {
                if (oddLastRow)
                {
                    k -= 17;
                }

                for (int i = latch ? 14 : 12; i >= (latch ? 2 : 0); i--)
                {
                    Alternate(separator, row, i + k, ref space);
                }
            }

            latch = !latch;
        }

        v2Latch = latch;
    }

    private static void Alternate(Span<bool> separator, ReadOnlySpan<bool> row, int x, ref bool space)
    {
        if (row[x])
        {
            separator[x] = false;
            space = false;
        }
        else
        {
            separator[x] = !space;
            space = !space;
        }
    }

    /// <summary>The binary data of 7.2.5: linkage flag, encodation method, compressed field, general field and padding.</summary>
    private static List<bool>? Bits(string data, ref int columns, out string? error)
    {
        error = null;
        if (data.Length > 77)
        {
            error = $"GS1 DataBar Expanded holds at most 74 digits or 41 letters; these element strings need {data.Length} characters.";
            return null;
        }

        var bits = new List<bool> { false };
        int method = data.Length >= 16 && data[0] == '0' && data[1] == '1' ? 1 : 2;
        if (data.Length >= 20 && method == 1 && data[2] == '9' && data[16] == '3')
        {
            method = OtherMethod(data, method);
        }

        int read;
        switch (method)
        {
            case 1:
                Append(bits, 4, 3);
                read = 16;
                break;
            case 2:
                Append(bits, 0, 4);
                read = 0;
                break;
            case 3:
            case 4:
                Append(bits, 4 + (method - 3), 4);
                read = 26;
                break;
            case 5:
                Append(bits, 0x30, 7);
                read = 20;
                break;
            case 6:
                Append(bits, 0x34, 7);
                read = 23;
                break;
            default:
                Append(bits, 56 + (method - 7), 7);
                read = data.Length;
                break;
        }

        if (method == 1)
        {
            Append(bits, data[2] - '0', 4);
        }

        if (method != 2)
        {
            for (int i = 3; i < 15; i += 3)
            {
                Append(bits, Number(data, i, 3), 10);
            }
        }

        if (method is 3 or 4)
        {
            int weight = Number(data, 20, 6);
            Append(bits, method == 4 && data[19] == '3' ? weight + 10000 : weight, 15);
        }
        else if (method is 5 or 6)
        {
            Append(bits, data[19] - '0', 2);
            if (method == 6)
            {
                Append(bits, Number(data, 20, 3), 10);
            }
        }
        else if (method >= 7)
        {
            Append(bits, ((data[19] - '0') * 100000) + Number(data, 21, 5), 20);
            Append(bits, data.Length == 34 ? Date(data, 28) : 38400, 16);
        }

        Field mode = Field.Numeric;
        char lastDigit = '\0';
        if (read < data.Length && !GeneralField(data.AsSpan(read), bits, ref mode, out lastDigit))
        {
            error = "GS1 DataBar Expanded holds digits, letters, space and ! \" % & ' ( ) * + , - . / : ; < = > ? _ in its general field.";
            return null;
        }

        int symbolCharacters = Size(bits.Count, ref columns);
        int remainder = (12 * (symbolCharacters - 1)) - bits.Count;
        if (lastDigit != '\0')
        {
            // 7.2.5.5.1 c): a last odd digit as 4 bits when 4 to 6 bits are left, otherwise with FNC1 as a numeric pair.
            if (remainder is >= 4 and <= 6)
            {
                Append(bits, lastDigit - '0' + 1, 4);
            }
            else
            {
                Append(bits, ((lastDigit - '0') * 11) + 10 + 8, 7);
            }

            symbolCharacters = Size(bits.Count, ref columns);
            remainder = (12 * (symbolCharacters - 1)) - bits.Count;
        }

        if (bits.Count > 252)
        {
            error = $"The element strings need {(bits.Count + 11) / 12} data characters; GS1 DataBar Expanded holds 21.";
            return null;
        }

        // 7.2.5.5.4 padding: in numeric mode a latch to alphanumeric first, then "00100" repeated.
        int pad = remainder;
        if (mode == Field.Numeric)
        {
            Append(bits, 0, 4);
            pad -= 4;
        }

        for (; pad > 0; pad -= 5)
        {
            Append(bits, 4, 5);
        }

        bits.RemoveRange(12 * (symbolCharacters - 1), bits.Count - (12 * (symbolCharacters - 1)));
        if (method is 1 or 2 or 5 or 6)
        {
            // The variable length field: odd number of symbol characters, and more than 14.
            int at = method == 1 ? 2 : method == 2 ? 3 : 6;
            bits[at] = symbolCharacters % 2 == 1;
            bits[at + 1] = symbolCharacters > 14;
        }

        return bits;
    }

    /// <summary>Methods 3 to 14: (01) with an indicator digit of 9 and a weight, price or date.</summary>
    private static int OtherMethod(string data, int method)
    {
        if (data.Length >= 26 && data[17] == '1' && data[18] == '0')
        {
            int weight = Number(data, 20, 6);
            if (weight is >= 0 and <= 99999)
            {
                if (data.Length == 26)
                {
                    return data[19] == '3' && weight <= 32767 ? 3 : 7;
                }

                if (data.Length == 34 && data[26] == '1' && data[27] is '1' or '3' or '5' or '7' && Date(data, 28) >= 0)
                {
                    return 6 + (data[27] - '0');
                }
            }
        }
        else if (data.Length >= 26 && data[17] == '2' && data[18] == '0')
        {
            int weight = Number(data, 20, 6);
            if (weight is >= 0 and <= 99999)
            {
                if (data.Length == 26)
                {
                    return (data[19] == '2' && weight <= 9999) || (data[19] == '3' && weight <= 22767) ? 4 : 8;
                }

                if (data.Length == 34 && data[26] == '1' && data[27] is '1' or '3' or '5' or '7' && Date(data, 28) >= 0)
                {
                    return 7 + (data[27] - '0');
                }
            }
        }
        else if (data[17] == '9' && data[19] is >= '0' and <= '3')
        {
            if (data[18] == '2')
            {
                return 5;
            }

            if (data[18] == '3' && data.Length >= 23 && Number(data, 20, 3) >= 0)
            {
                return 6;
            }
        }

        return method;
    }

    /// <summary>The symbol characters for <paramref name="bits"/> bits: at least 4, and for stacked rows never one alone in a row.</summary>
    private static int Size(int bits, ref int columns)
    {
        int symbolCharacters = ((bits + 11) / 12) + 1;
        if (columns > 0 && symbolCharacters % (columns * 2) == 1)
        {
            symbolCharacters++;
        }

        return Math.Max(symbolCharacters, 4);
    }

    /// <summary>A date YYMMDD as YY × 384 + (MM − 1) × 32 + DD, or -1.</summary>
    private static int Date(string data, int at)
    {
        if (at + 6 > data.Length)
        {
            return -1;
        }

        int yy = Number(data, at, 2), mm = Number(data, at + 2, 2), dd = Number(data, at + 4, 2);
        return yy < 0 || mm is <= 0 or > 12 || dd is < 0 or > 31 ? -1 : (yy * 384) + ((mm - 1) * 32) + dd;
    }

    /// <summary>The number of <paramref name="length"/> digits at <paramref name="at"/>, or -1 when they are not all digits.</summary>
    private static int Number(string data, int at, int length)
    {
        if (at + length > data.Length)
        {
            return -1;
        }

        int value = 0;
        for (int i = at; i < at + length; i++)
        {
            if (!char.IsAsciiDigit(data[i]))
            {
                return -1;
            }

            value = (value * 10) + (data[i] - '0');
        }

        return value;
    }

    private static void Append(List<bool> bits, int value, int count)
    {
        for (int i = count - 1; i >= 0; i--)
        {
            bits.Add(((value >> i) & 1) != 0);
        }
    }

    private const string AlphanumericPunctuation = "*,-./";
    private const string IsoIecPunctuation = "!\"%&'()*+,-./:;<=>?_ ";

    private static Field? TypeOf(char c) =>
        c == Fnc1 || char.IsAsciiDigit(c) ? Field.Numeric
        : char.IsAsciiLetterUpper(c) || AlphanumericPunctuation.Contains(c) ? Field.Alphanumeric
        : char.IsAsciiLetterLower(c) || IsoIecPunctuation.Contains(c) ? Field.IsoIec
        : null;

    /// <summary>Whether the <paramref name="count"/> characters from <paramref name="at"/> are all of the type (or the second type).</summary>
    private static bool Next(ReadOnlySpan<char> field, int at, int count, Field type, Field? other = null)
    {
        if (at + count > field.Length)
        {
            return false;
        }

        for (int i = at; i < at + count; i++)
        {
            Field? t = TypeOf(field[i]);
            if (t != type && (other is null || t != other))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether the rest, from <paramref name="at"/>, is at least <paramref name="count"/> and at most <paramref name="max"/> characters of the type.</summary>
    private static bool NextToEnd(ReadOnlySpan<char> field, int at, int count, int max, Field type)
    {
        if (at + max < field.Length)
        {
            return false;
        }

        int i = at;
        for (; i < field.Length; i++, count--)
        {
            if (TypeOf(field[i]) != type)
            {
                return false;
            }
        }

        return count <= 0;
    }

    /// <summary>Whether none of the next <paramref name="count"/> characters (or up to the end) is of the type.</summary>
    private static bool NoneNext(ReadOnlySpan<char> field, int at, int count, Field type)
    {
        int i = at;
        for (; i < field.Length && count > 0; i++, count--)
        {
            if (TypeOf(field[i]) == type)
            {
                return false;
            }
        }

        return count == 0 || i == field.Length;
    }

    /// <summary>The general-purpose field (7.2.5.5.1 to 7.2.5.5.3); a last odd digit is left for the caller.</summary>
    private static bool GeneralField(ReadOnlySpan<char> field, List<bool> bits, ref Field mode, out char lastDigit)
    {
        lastDigit = '\0';
        for (int i = 0; i < field.Length;)
        {
            char c = field[i];
            Field? type = TypeOf(c);
            if (type is null)
            {
                return false;
            }

            switch (mode)
            {
                case Field.Numeric:
                    if (i < field.Length - 1)
                    {
                        if (type != Field.Numeric || TypeOf(field[i + 1]) != Field.Numeric)
                        {
                            Append(bits, 0, 4);
                            mode = Field.Alphanumeric;
                        }
                        else
                        {
                            int d1 = c == Fnc1 ? 10 : c - '0', d2 = field[i + 1] == Fnc1 ? 10 : field[i + 1] - '0';
                            Append(bits, (11 * d1) + d2 + 8, 7);
                            i += 2;
                        }
                    }
                    else if (type != Field.Numeric)
                    {
                        Append(bits, 0, 4);
                        mode = Field.Alphanumeric;
                    }
                    else
                    {
                        lastDigit = c;
                        i++;
                    }

                    break;
                case Field.Alphanumeric:
                    if (c == Fnc1)
                    {
                        Append(bits, 15, 5);
                        mode = Field.Numeric;
                        i++;
                    }
                    else if (type == Field.IsoIec)
                    {
                        Append(bits, 4, 5);
                        mode = Field.IsoIec;
                    }
                    else if (Next(field, i, 6, Field.Numeric) || NextToEnd(field, i, 4, 5, Field.Numeric))
                    {
                        Append(bits, 0, 3);
                        mode = Field.Numeric;
                    }
                    else if (char.IsAsciiDigit(c))
                    {
                        Append(bits, c - 43, 5);
                        i++;
                    }
                    else if (char.IsAsciiLetterUpper(c))
                    {
                        Append(bits, c - 33, 6);
                        i++;
                    }
                    else
                    {
                        Append(bits, AlphanumericPunctuation.IndexOf(c) + 58, 6);
                        i++;
                    }

                    break;
                default:
                    if (c == Fnc1)
                    {
                        Append(bits, 15, 5);
                        mode = Field.Numeric;
                        i++;
                        break;
                    }

                    bool noIsoIecNext = NoneNext(field, i, 10, Field.IsoIec);
                    if (noIsoIecNext && Next(field, i, 4, Field.Numeric))
                    {
                        Append(bits, 0, 3);
                        mode = Field.Numeric;
                    }
                    else if (noIsoIecNext && Next(field, i, 5, Field.Alphanumeric, Field.Numeric))
                    {
                        Append(bits, 4, 5);
                        mode = Field.Alphanumeric;
                    }
                    else if (char.IsAsciiDigit(c))
                    {
                        Append(bits, c - 43, 5);
                        i++;
                    }
                    else if (char.IsAsciiLetterUpper(c))
                    {
                        Append(bits, c - 1, 7);
                        i++;
                    }
                    else if (char.IsAsciiLetterLower(c))
                    {
                        Append(bits, c - 7, 7);
                        i++;
                    }
                    else
                    {
                        Append(bits, IsoIecPunctuation.IndexOf(c) + 232, 8);
                        i++;
                    }

                    break;
            }
        }

        return true;
    }
}
