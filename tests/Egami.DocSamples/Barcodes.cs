using Egami.Barcodes;

namespace Egami.DocSamples;

[Collection("Samples")]
public class Barcodes
{
    [Fact]
    public void ReadTheModules()
    {
        #region barcode-modules
        // A 2D grid, row by row from the top; true is a dark module.
        BarcodeModules grid = BarcodeModules.Create(3, 3,
        [
            true, false, true,
            false, true, false,
            true, false, true,
        ]);

        // A linear symbol: bar and space widths in modules, starting and ending with a bar.
        BarcodeModules bars = BarcodeModules.CreateLinear([2, 1, 1, 1, 3], height: 20);

        for (int y = 0; y < grid.Height; y++)
        {
            for (int x = 0; x < grid.Width; x++)
            {
                Console.Write(grid[x, y] ? '#' : '.');
            }

            Console.WriteLine();
        }

        Console.WriteLine($"{bars.Width} modules wide, bars {string.Join(' ', bars.Runs.ToArray())}");
        #endregion
        Assert.Equal(8, bars.Width);
        Assert.True(grid[1, 1]);
    }
}
