using Egami.Barcodes.Linear;

namespace Egami.Barcodes.Tests;

public class DataBarTests
{
    /// <summary>
    /// Values 0 to <paramref name="count"/> - 1 each give their own pattern of <paramref name="n"/> modules that keeps the set's
    /// rules, and come back from it.
    /// </summary>
    private static void CheckSet(int n, int elements, int maxWidth, bool noNarrow, int count)
    {
        var seen = new HashSet<string>();
        int[] widths = new int[elements];
        for (int value = 0; value < count; value++)
        {
            DataBarWidths.Widths(widths, value, n, elements, maxWidth, noNarrow);
            Assert.Equal(n, widths.Sum());
            Assert.All(widths, w => Assert.InRange(w, 1, maxWidth));
            Assert.True(!noNarrow || widths.Contains(1));
            Assert.True(seen.Add(string.Join(',', widths)));
            Assert.Equal(value, DataBarWidths.Value(widths, maxWidth, noNarrow));
        }
    }

    [Fact]
    public void OmnidirectionalGroupsRoundTrip()
    {
        ReadOnlySpan<int> sums = DataBarTables.OmniGroupSum;
        for (int group = 0; group < 9; group++)
        {
            bool outside = group < 5;
            int end = group == 4 ? 2841 : group == 8 ? 1597 : sums[group + 1];
            int t = DataBarTables.OmniCombinations[group], other = (end - sums[group]) / t;
            Assert.Equal(end - sums[group], t * other);
            CheckSet(DataBarTables.OmniModules[group], 4, DataBarTables.OmniWidest[group], !outside, outside ? other : t);
            CheckSet(DataBarTables.OmniModules[group + 9], 4, 9 - DataBarTables.OmniWidest[group], outside, outside ? t : other);
        }
    }

    [Fact]
    public void LimitedGroupsRoundTrip()
    {
        ReadOnlySpan<int> sums = DataBarTables.LimitedGroupSum;
        for (int group = 0; group < 7; group++)
        {
            int end = group == 6 ? 2013571 : sums[group + 1];
            int t = DataBarTables.LimitedEvenCombinations[group], odd = (end - sums[group]) / t;
            Assert.Equal(end - sums[group], t * odd);
            int oddModules = DataBarTables.LimitedModules[group];
            CheckSet(oddModules, 7, DataBarTables.LimitedWidest[group], false, odd);
            CheckSet(26 - oddModules, 7, 9 - DataBarTables.LimitedWidest[group], true, t);
        }
    }

    [Fact]
    public void ExpandedGroupsRoundTrip()
    {
        ReadOnlySpan<int> sums = DataBarTables.ExpandedGroupSum;
        for (int group = 0; group < 5; group++)
        {
            int end = group == 4 ? 4192 : sums[group + 1];
            int t = DataBarTables.ExpandedEvenCombinations[group], odd = (end - sums[group]) / t;
            Assert.Equal(end - sums[group], t * odd);
            int oddModules = DataBarTables.ExpandedModules[group];
            CheckSet(oddModules, 4, DataBarTables.ExpandedWidest[group], true, odd);
            CheckSet(17 - oddModules, 4, 9 - DataBarTables.ExpandedWidest[group], false, t);
        }
    }

    [Fact]
    public void ValueRejectsPatternsOutsideTheSet()
    {
        Assert.Equal(-1, DataBarWidths.Value([2, 2, 2, 2], 6, true));
        Assert.Equal(-1, DataBarWidths.Value([9, 1, 1, 1], 8, false));
        Assert.Equal(-1, DataBarWidths.Value([0, 4, 4, 4], 8, false));
    }
}
