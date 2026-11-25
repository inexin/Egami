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

    [Fact]
    public void DataBarTakesAGtinWithOrWithoutItsCheckDigit()
    {
        Barcode barcode = Barcode.DataBar("0950110153000");
        Assert.Equal(BarcodeFormat.DataBar, barcode.Format);
        Assert.Equal("(01)09501101530003", barcode.HumanReadableText);
        Assert.True(barcode.Modules.IsLinear);
        Assert.Equal(95, barcode.Modules.Width);
        Assert.Equal(33, barcode.Modules.Height);
        Assert.Equal(1, barcode.QuietZone);
        Assert.Equal(barcode.Modules.Runs.ToArray(), Barcode.DataBar("09501101530003").Modules.Runs.ToArray());
        Assert.Equal(barcode.Modules.Runs.ToArray(), Barcode.Create(BarcodeFormat.DataBar, "0950110153000").Modules.Runs.ToArray());
        Assert.Throws<ArgumentException>(() => Barcode.DataBar("09501101530004"));
        Assert.Throws<ArgumentException>(() => Barcode.DataBar("095011015300"));
        Assert.Throws<ArgumentException>(() => Barcode.DataBar("095011015300031"));
        Assert.Throws<ArgumentException>(() => Barcode.DataBar("095O110153000"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DataBarOptions { Kind = (DataBarKind)9 });
    }

    [Fact]
    public void DataBarKindsShareTheirCharacters()
    {
        Barcode omni = Barcode.DataBar("0950110153000");
        Barcode truncated = Barcode.DataBar("0950110153000", new DataBarOptions { Kind = DataBarKind.Truncated });
        Barcode stacked = Barcode.DataBar("0950110153000", new DataBarOptions { Kind = DataBarKind.Stacked });
        Barcode stackedOmni = Barcode.DataBar("0950110153000", new DataBarOptions { Kind = DataBarKind.StackedOmnidirectional });
        Assert.Equal(omni.Modules.Runs.ToArray(), truncated.Modules.Runs.ToArray());
        Assert.Equal(13, truncated.Modules.Height);
        Assert.False(stacked.Modules.IsLinear);
        Assert.Equal((50, 13), (stacked.Modules.Width, stacked.Modules.Height));
        Assert.Equal((50, 69), (stackedOmni.Modules.Width, stackedOmni.Modules.Height));

        // The top row of each stacked form is the left half of the one-row symbol.
        for (int x = 0; x < 48; x++)
        {
            bool dark = x > 0 && omni.Modules[x - 1, 0];
            Assert.Equal(dark, stacked.Modules[x, 0]);
            Assert.Equal(dark, stackedOmni.Modules[x, 0]);
        }

        // FromModules takes DataBar as a row or as a grid.
        Assert.Equal(BarcodeFormat.DataBar, Barcode.FromModules(BarcodeFormat.DataBar, stacked.Modules).Format);
        Assert.Equal(BarcodeFormat.DataBar, Barcode.FromModules(BarcodeFormat.DataBar, omni.Modules).Format);
    }

    [Fact]
    public void DataBarLimitedTakesGtinsStartingWithZeroOrOne()
    {
        Barcode barcode = Barcode.DataBarLimited("1501234567890");
        Assert.Equal(BarcodeFormat.DataBarLimited, barcode.Format);
        Assert.Equal("(01)15012345678907", barcode.HumanReadableText);
        Assert.Equal(73, barcode.Modules.Width);
        Assert.Equal(10, barcode.Modules.Height);
        Assert.Equal(5, barcode.QuietZone);
        Assert.Equal(barcode.Modules.Runs.ToArray(), Barcode.Create(BarcodeFormat.DataBarLimited, "15012345678907").Modules.Runs.ToArray());
        Assert.Throws<ArgumentException>(() => Barcode.DataBarLimited("2501234567890"));
        Assert.Throws<ArgumentException>(() => Barcode.DataBarLimited("15012345678906"));
        Assert.Throws<ArgumentException>(() => Barcode.DataBarLimited("150123456789"));
    }
}
