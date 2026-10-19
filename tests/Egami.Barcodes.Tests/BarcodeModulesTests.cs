namespace Egami.Barcodes.Tests;

public class BarcodeModulesTests
{
    [Fact]
    public void AGridKeepsItsModules()
    {
        bool[] cells = [true, false, false, true, true, false];
        BarcodeModules grid = BarcodeModules.Create(3, 2, cells);
        cells[0] = false;

        Assert.Equal((3, 2), (grid.Width, grid.Height));
        Assert.False(grid.IsLinear);
        Assert.True(grid.Runs.IsEmpty);
        Assert.True(grid[0, 0]);
        Assert.False(grid[1, 0]);
        Assert.True(grid[0, 1]);
        Assert.True(grid[1, 1]);
        Assert.False(grid[2, 1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => grid[3, 0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => grid[0, -1]);
    }

    [Fact]
    public void ALinearSymbolHasDarkAndLightColumns()
    {
        BarcodeModules bars = BarcodeModules.CreateLinear([2, 1, 1, 3, 1], 5);

        Assert.True(bars.IsLinear);
        Assert.Equal((8, 5), (bars.Width, bars.Height));
        Assert.Equal([2, 1, 1, 3, 1], bars.Runs.ToArray());
        bool[] expected = [true, true, false, true, false, false, false, true];
        for (int y = 0; y < 5; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                Assert.Equal(expected[x], bars[x, y]);
            }
        }
    }

    [Fact]
    public void BadShapesThrow()
    {
        Assert.Throws<ArgumentException>(() => BarcodeModules.Create(2, 2, new bool[3]));
        Assert.Throws<ArgumentOutOfRangeException>(() => BarcodeModules.Create(0, 2, []));
        Assert.Throws<ArgumentException>(() => BarcodeModules.CreateLinear([], 1));
        Assert.Throws<ArgumentException>(() => BarcodeModules.CreateLinear([1, 2], 1));
        Assert.Throws<ArgumentException>(() => BarcodeModules.CreateLinear([1, 0, 1], 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => BarcodeModules.CreateLinear([1], 0));
    }
}
