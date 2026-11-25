namespace Egami.Barcodes.Linear;

/// <summary>
/// Builds GS1 DataBar symbols (ISO/IEC 24724:2011) as element widths, each list starting with a space, and lays them out in
/// rows. The separator rows of the stacked forms follow the standard as zint's backend reads it (BSD-3; see
/// THIRD-PARTY-NOTICES.md), so that Egami's symbols equal zint's.
/// </summary>
internal static class DataBarEncoder
{
    /// <summary>The largest value an Omnidirectional symbol holds: the 13 digits of a GTIN-14 before its check digit.</summary>
    public const long MaxOmniValue = 9_999_999_999_999;

    /// <summary>The 46 element widths of an Omnidirectional symbol, first a space: guard, 4 characters, 2 finders, guard.</summary>
    public static int[] OmniWidths(long value)
    {
        System.Diagnostics.Debug.Assert(value is >= 0 and <= MaxOmniValue);
        int left = (int)(value / 4537077), right = (int)(value % 4537077);
        Span<int> characters = [left / 1597, left % 1597, right / 1597, right % 1597];
        var widths = new int[4][];
        int checksum = 0;
        for (int i = 0; i < 4; i++)
        {
            widths[i] = OmniCharacter(characters[i], (i & 1) == 0);
            for (int j = 0; j < 8; j++)
            {
                checksum += DataBarTables.OmniChecksumWeights[(i * 8) + j] * widths[i][j];
            }
        }

        checksum %= 79;
        if (checksum >= 8)
        {
            checksum++;
        }

        if (checksum >= 72)
        {
            checksum++;
        }

        int leftFinder = checksum / 9, rightFinder = checksum % 9;
        int[] total = new int[46];
        total[0] = total[1] = total[44] = total[45] = 1;
        for (int i = 0; i < 8; i++)
        {
            total[i + 2] = widths[0][i];
            total[i + 15] = widths[1][7 - i];
            total[i + 23] = widths[3][i];
            total[i + 36] = widths[2][7 - i];
        }

        for (int i = 0; i < 5; i++)
        {
            total[i + 10] = DataBarTables.OmniFinders[(leftFinder * 5) + i];
            total[i + 31] = DataBarTables.OmniFinders[(rightFinder * 5) + 4 - i];
        }

        return total;
    }

    /// <summary>The 8 element widths of an Omnidirectional character, outside (16 modules) or inside (15).</summary>
    public static int[] OmniCharacter(int value, bool outside)
    {
        int group = OmniGroup(value, outside);
        int v = value - DataBarTables.OmniGroupSum[group], t = DataBarTables.OmniCombinations[group];
        int odd = outside ? v / t : v % t, even = outside ? v % t : v / t;
        int[] widths = new int[8];
        DataBarWidths.Character(widths, odd, even, DataBarTables.OmniModules[group], DataBarTables.OmniModules[group + 9], 4, DataBarTables.OmniWidest[group], !outside);
        return widths;
    }

    private static int OmniGroup(int value, bool outside)
    {
        int end = outside ? 4 : 8;
        for (int group = outside ? 0 : 5; group < end; group++)
        {
            if (value < DataBarTables.OmniGroupSum[group + 1])
            {
                return group;
            }
        }

        return end;
    }

    /// <summary>The largest value a Limited symbol holds: a GTIN-14 that starts with 0 or 1, before its check digit.</summary>
    public const long MaxLimitedValue = 1_999_999_999_999;

    /// <summary>The 47 element widths of a Limited symbol, first a space: guard, character, check pattern, character, guard.</summary>
    public static int[] LimitedWidths(long value)
    {
        System.Diagnostics.Debug.Assert(value is >= 0 and <= MaxLimitedValue);
        int[] left = LimitedCharacter((int)(value / 2013571)), right = LimitedCharacter((int)(value % 2013571));
        int checksum = 0;
        for (int i = 0; i < 14; i++)
        {
            checksum += (DataBarTables.LimitedChecksumWeights[i] * left[i]) + (DataBarTables.LimitedChecksumWeights[14 + i] * right[i]);
        }

        checksum %= 89;
        int[] total = new int[47];
        total[0] = total[1] = total[44] = total[45] = 1;
        total[46] = 5;
        for (int i = 0; i < 14; i++)
        {
            total[i + 2] = left[i];
            total[i + 16] = DataBarTables.LimitedFinders[(checksum * 14) + i];
            total[i + 30] = right[i];
        }

        return total;
    }

    /// <summary>The 14 element widths of a Limited character (26 modules).</summary>
    public static int[] LimitedCharacter(int value)
    {
        int group = 6;
        while (value < DataBarTables.LimitedGroupSum[group])
        {
            group--;
        }

        int v = value - DataBarTables.LimitedGroupSum[group], t = DataBarTables.LimitedEvenCombinations[group];
        int modules = DataBarTables.LimitedModules[group];
        int[] widths = new int[14];
        DataBarWidths.Character(widths, v / t, v % t, modules, 26 - modules, 7, DataBarTables.LimitedWidest[group], false);
        return widths;
    }

    /// <summary>The bars of a one-row symbol: the widths without their first space, ending with a bar.</summary>
    public static int[] Runs(ReadOnlySpan<int> widths)
    {
        int end = widths.Length % 2 == 0 ? widths.Length : widths.Length - 1;
        return widths[1..end].ToArray();
    }

    /// <summary>Writes widths into a row from <paramref name="x"/>, the first element dark or light; returns the end.</summary>
    private static int Expand(Span<bool> row, int x, bool dark, ReadOnlySpan<int> widths)
    {
        foreach (int width in widths)
        {
            row.Slice(x, width).Fill(dark);
            x += width;
            dark = !dark;
        }

        return x;
    }

    /// <summary>DataBar Stacked: the left half over the right, 5 and 7 modules tall, with a separator row between.</summary>
    public static BarcodeModules Stacked(int[] widths)
    {
        const int width = 50;
        bool[] top = new bool[width], separator = new bool[width], bottom = new bool[width];
        int writer = Expand(top, 0, false, widths.AsSpan(0, 23));
        top[writer] = true;
        bottom[0] = true;
        Expand(bottom, 2, true, widths.AsSpan(23, 23));
        for (int i = 1; i < 46; i++)
        {
            separator[i] = top[i] == bottom[i] ? !top[i] : !separator[i - 1];
        }

        separator[1] = separator[2] = separator[3] = false;
        return Grid(width, [(top, 5), (separator, 1), (bottom, 7)]);
    }

    /// <summary>
    /// DataBar Stacked Omnidirectional: the left half over the right, each 33 modules tall, with a separator row on each and an
    /// alternating row between.
    /// </summary>
    public static BarcodeModules StackedOmni(int[] widths)
    {
        const int width = 50;
        bool[] top = new bool[width], topSeparator = new bool[width], middle = new bool[width], bottomSeparator = new bool[width], bottom = new bool[width];
        int writer = Expand(top, 0, false, widths.AsSpan(0, 23));
        top[writer] = true;
        bottom[0] = true;
        Expand(bottom, 2, true, widths.AsSpan(23, 23));
        for (int i = 5; i < 46; i += 2)
        {
            middle[i] = true;
        }

        OmniSeparator(topSeparator, top, 18, false);

        // The right finder's value 3 shifts its separator's single dark module (ISO/IEC 24724 5.3.2.2).
        int rightFinder = RightFinder(widths);
        OmniSeparator(bottomSeparator, bottom, 19, rightFinder == 3);
        return Grid(width, [(top, 33), (topSeparator, 1), (middle, 1), (bottomSeparator, 1), (bottom, 33)]);
    }

    private static int RightFinder(int[] widths)
    {
        for (int k = 0; k < 9; k++)
        {
            bool match = true;
            for (int i = 0; i < 5 && match; i++)
            {
                match = widths[31 + i] == DataBarTables.OmniFinders[(k * 5) + 4 - i];
            }

            if (match)
            {
                return k;
            }
        }

        return -1;
    }

    /// <summary>A separator row: light over the bars, dark over the spaces, and the finder's 13 modules alternated.</summary>
    private static void OmniSeparator(Span<bool> separator, ReadOnlySpan<bool> row, int finderStart, bool finderValue3)
    {
        for (int i = 4; i < separator.Length - 4; i++)
        {
            separator[i] = !row[i];
        }

        if (finderValue3)
        {
            for (int i = finderStart; i < finderStart + 13; i++)
            {
                separator[i] = i == finderStart + 10;
            }

            return;
        }

        bool latch = true;
        for (int i = finderStart; i < finderStart + 13; i++)
        {
            if (row[i])
            {
                separator[i] = false;
                latch = true;
            }
            else
            {
                separator[i] = latch;
                latch = !latch;
            }
        }
    }

    /// <summary>Stacks rows, each repeated for its height, into a grid.</summary>
    private static BarcodeModules Grid(int width, ReadOnlySpan<(bool[] Row, int Height)> rows)
    {
        int height = 0;
        foreach ((_, int h) in rows)
        {
            height += h;
        }

        bool[] modules = new bool[width * height];
        int y = 0;
        foreach ((bool[] row, int h) in rows)
        {
            for (int k = 0; k < h; k++, y++)
            {
                row.AsSpan(0, width).CopyTo(modules.AsSpan(y * width, width));
            }
        }

        return BarcodeModules.Wrap(width, height, modules);
    }
}
