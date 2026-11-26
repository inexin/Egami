namespace Egami.Barcodes.Linear;

/// <summary>
/// The GS1 DataBar character set (ISO/IEC 24724:2011 Annex B): a value becomes the element widths of one half of a character,
/// and back. A half has <c>elements</c> elements (4, or 7 for Limited) over <c>n</c> modules, none wider than
/// <c>maxWidth</c>; with <c>noNarrow</c> the widths with no one-module element are skipped. Values count the allowed width
/// patterns in order, widest first element last.
/// </summary>
internal static class DataBarWidths
{
    // Combinations of up to 31 by up to 31, as Calculate gives them: reading takes them often.
    private static readonly int[] s_combinations = Table();

    /// <summary>The number of ways to choose <paramref name="r"/> of <paramref name="n"/>.</summary>
    public static int Combinations(int n, int r) => (uint)n < 32 && (uint)r < 32 ? s_combinations[(n * 32) + r] : Calculate(n, r);

    private static int[] Table()
    {
        int[] table = new int[32 * 32];
        for (int n = 0; n < 32; n++)
        {
            for (int r = 0; r < 32; r++)
            {
                table[(n * 32) + r] = Calculate(n, r);
            }
        }

        return table;
    }

    private static int Calculate(int n, int r)
    {
        int minDenominator = Math.Min(r, n - r), maxDenominator = Math.Max(r, n - r);
        int value = 1, j = 1;
        for (int i = n; i > maxDenominator; i--)
        {
            value *= i;
            if (j <= minDenominator)
            {
                value /= j;
                j++;
            }
        }

        for (; j <= minDenominator; j++)
        {
            value /= j;
        }

        return value;
    }

    /// <summary>The patterns that start with an element <paramref name="width"/> wide, given the elements before it.</summary>
    private static int Count(int n, int elements, int bar, int width, int maxWidth, bool noNarrow, bool hadNarrow)
    {
        int count = Combinations(n - width - 1, elements - bar - 2);
        if (noNarrow && !hadNarrow && n - width - (elements - bar - 1) >= elements - bar - 1)
        {
            count -= Combinations(n - width - (elements - bar), elements - bar - 2);
        }

        if (elements - bar - 1 > 1)
        {
            int tooWide = 0;
            for (int widest = n - width - (elements - bar - 2); widest > maxWidth; widest--)
            {
                tooWide += Combinations(n - width - widest - 1, elements - bar - 3);
            }

            count -= tooWide * (elements - 1 - bar);
        }
        else if (n - width > maxWidth)
        {
            count--;
        }

        return count;
    }

    /// <summary>Writes the widths of <paramref name="value"/> into <paramref name="widths"/> (<c>elements</c> of them).</summary>
    public static void Widths(Span<int> widths, int value, int n, int elements, int maxWidth, bool noNarrow)
    {
        bool hadNarrow = false;
        int bar;
        for (bar = 0; bar < elements - 1; bar++)
        {
            int width = 1;
            while (true)
            {
                int count = Count(n, elements, bar, width, maxWidth, noNarrow, hadNarrow || width == 1);
                if (value < count)
                {
                    break;
                }

                value -= count;
                width++;
            }

            hadNarrow |= width == 1;
            n -= width;
            widths[bar] = width;
        }

        widths[bar] = n;
    }

    // The values of 4 widths of 1 to 8 modules, by the widest allowed and the narrow rule, made when first asked for: reading
    // takes them often.
    private static readonly short[]?[] s_values = new short[16][];

    /// <summary>The value of <paramref name="widths"/>, or -1 when they are not a pattern of the set.</summary>
    public static int Value(ReadOnlySpan<int> widths, int maxWidth, bool noNarrow)
    {
        if (widths.Length != 4 || maxWidth is < 1 or > 8)
        {
            return Calculate(widths, maxWidth, noNarrow);
        }

        int index = 0;
        foreach (int width in widths)
        {
            if (width < 1 || width > maxWidth)
            {
                return -1;
            }

            index = (index * 8) + width - 1;
        }

        int table = ((maxWidth - 1) * 2) + (noNarrow ? 1 : 0);
        short[] values = s_values[table] ??= Values(maxWidth, noNarrow);
        return values[index];
    }

    private static short[] Values(int maxWidth, bool noNarrow)
    {
        short[] values = new short[8 * 8 * 8 * 8];
        Span<int> widths = stackalloc int[4];
        for (int index = 0; index < values.Length; index++)
        {
            for (int k = 0; k < 4; k++)
            {
                widths[k] = ((index >> (3 * (3 - k))) & 7) + 1;
            }

            values[index] = (short)Calculate(widths, maxWidth, noNarrow);
        }

        return values;
    }

    private static int Calculate(ReadOnlySpan<int> widths, int maxWidth, bool noNarrow)
    {
        int n = 0, elements = widths.Length;
        bool narrow = false;
        foreach (int width in widths)
        {
            if (width < 1 || width > maxWidth)
            {
                return -1;
            }

            narrow |= width == 1;
            n += width;
        }

        if (noNarrow && !narrow)
        {
            return -1;
        }

        int value = 0;
        bool hadNarrow = false;
        for (int bar = 0; bar < elements - 1; bar++)
        {
            for (int width = 1; width < widths[bar]; width++)
            {
                value += Count(n, elements, bar, width, maxWidth, noNarrow, hadNarrow || width == 1);
            }

            hadNarrow |= widths[bar] == 1;
            n -= widths[bar];
        }

        return value;
    }

    /// <summary>
    /// A whole character: the odd elements from <paramref name="oddValue"/> and the even ones from <paramref name="evenValue"/>,
    /// interleaved odd first. The even elements are at most 9 minus <paramref name="maxWidth"/> wide and take the opposite
    /// narrow rule.
    /// </summary>
    public static void Character(Span<int> widths, int oddValue, int evenValue, int oddModules, int evenModules, int elements, int maxWidth, bool noNarrow)
    {
        Span<int> odd = stackalloc int[elements], even = stackalloc int[elements];
        Widths(odd, oddValue, oddModules, elements, maxWidth, noNarrow);
        Widths(even, evenValue, evenModules, elements, 9 - maxWidth, !noNarrow);
        for (int i = 0; i < elements; i++)
        {
            widths[2 * i] = odd[i];
            widths[(2 * i) + 1] = even[i];
        }
    }
}
