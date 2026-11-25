using Egami.Barcodes.Linear;

namespace Egami.Barcodes.Reading;

internal static partial class LinearDecoder
{
    // ---- GS1 DataBar Limited ----

    /// <summary>
    /// GS1 DataBar Limited from its guard bar at <paramref name="start"/>: two characters of 14 elements over 26 modules with the
    /// check pattern (14 elements over 18 modules) between them, 73 modules to the right guard's bar, then at least 3 modules
    /// of space. The characters' odd module counts are tried nearest first, and the check pattern decides.
    /// </summary>
    private static LinearRead? DataBarLimited(ReadOnlySpan<float> runs, ReadOnlySpan<float> offsets, int start)
    {
        if (start + 45 > runs.Length)
        {
            return null;
        }

        float total = Sum(runs.Slice(start, 45)), unit = total / 73;
        if (unit <= 0 || !Module(runs[start], unit) || !Module(runs[start + 43], unit) || !Module(runs[start + 44], unit)
            || !QuietBefore(runs, start, 0.6f * unit) || !QuietAfter(runs, start + 45, 4 * unit))
        {
            return null;
        }

        float leftWidth = Sum(runs.Slice(start + 1, 14)), checkWidth = Sum(runs.Slice(start + 15, 14)), rightWidth = Sum(runs.Slice(start + 29, 14));
        if (MathF.Abs((leftWidth / 26) - unit) > 0.12f * unit || MathF.Abs((rightWidth / 26) - unit) > 0.12f * unit || MathF.Abs((checkWidth / 18) - unit) > 0.12f * unit)
        {
            return null;
        }

        Span<(int Value, int Checksum)> left = stackalloc (int, int)[3], right = stackalloc (int, int)[3];
        int lefts = LimitedCharacters(runs.Slice(start + 1, 14), leftWidth / 26, 0, left);
        int rights = lefts == 0 ? 0 : LimitedCharacters(runs.Slice(start + 29, 14), rightWidth / 26, 1, right);
        Span<int> check = stackalloc int[14];
        for (int a = 0; a < lefts; a++)
        {
            for (int b = 0; b < rights; b++)
            {
                int checksum = (left[a].Checksum + right[b].Checksum) % 89;
                if (Variance(runs.Slice(start + 15, 14), CheckPattern(checksum, check), 0.5f) > 0.15f)
                {
                    continue;
                }

                long value = ((long)left[a].Value * 2013571) + right[b].Value;
                if (value > DataBarEncoder.MaxLimitedValue)
                {
                    continue;
                }

                return Result(DataBarReader.Gtin(BarcodeFormat.DataBarLimited, value), offsets, start, start + 45);
            }
        }

        return null;
    }

    private static bool Module(float run, float unit) => run >= 0.4f * unit && run <= 2.2f * unit;

    private static ReadOnlySpan<int> CheckPattern(int checksum, Span<int> into)
    {
        for (int k = 0; k < 14; k++)
        {
            into[k] = DataBarTables.LimitedFinders[(checksum * 14) + k];
        }

        return into;
    }

    /// <summary>
    /// The readings of a Limited character, the likeliest odd module counts first (at most 3), each with its value and its part
    /// of the checksum (weights row <paramref name="row"/>).
    /// </summary>
    private static int LimitedCharacters(ReadOnlySpan<float> runs, float module, int row, Span<(int Value, int Checksum)> readings)
    {
        Span<float> elements = stackalloc float[14];
        float odd = 0;
        for (int k = 0; k < 14; k++)
        {
            elements[k] = runs[k] / module;
            odd += k % 2 == 0 ? elements[k] : 0;
        }

        Span<int> groups = [0, 1, 2, 3, 4, 5, 6];
        DataBarReader.SortByDistance(groups, odd, DataBarTables.LimitedModules);
        int[] widths = new int[14];
        Span<float> half = stackalloc float[7];
        Span<int> rounded = stackalloc int[7];
        int count = 0;
        for (int attempt = 0; attempt < 3 && count < readings.Length; attempt++)
        {
            int group = groups[attempt];
            int oddModules = DataBarTables.LimitedModules[group], widest = DataBarTables.LimitedWidest[group];
            if (!DataBarReader.Half(elements, 0, oddModules, half, rounded, widths) || !DataBarReader.Half(elements, 1, 26 - oddModules, half, rounded, widths))
            {
                continue;
            }

            int oddValue = DataBarWidths.Value(DataBarReader.Pick(widths, 0, rounded), widest, false);
            int evenValue = DataBarWidths.Value(DataBarReader.Pick(widths, 1, rounded), 9 - widest, true);
            int t = DataBarTables.LimitedEvenCombinations[group];
            int end = group == 6 ? 2013571 : DataBarTables.LimitedGroupSum[group + 1];
            if (oddValue < 0 || evenValue < 0 || evenValue >= t || oddValue >= (end - DataBarTables.LimitedGroupSum[group]) / t)
            {
                continue;
            }

            int checksum = 0;
            for (int k = 0; k < 14; k++)
            {
                checksum += DataBarTables.LimitedChecksumWeights[(row * 14) + k] * widths[k];
            }

            readings[count++] = (DataBarTables.LimitedGroupSum[group] + (oddValue * t) + evenValue, checksum);
        }

        return count;
    }
}
