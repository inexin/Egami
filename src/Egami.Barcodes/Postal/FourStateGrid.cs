namespace Egami.Barcodes.Postal;

/// <summary>Lays 4-state bars out as a grid: one dark column per bar, one light column between bars.</summary>
internal static class FourStateGrid
{
    /// <summary>
    /// The rows of each third: 6 rows in all, 2 per third, so that at a 0.6 mm module a Japan Post bar is 0.6 mm wide and its
    /// full bar 3.6 mm tall, the manual's sizes.
    /// </summary>
    public const int Rows = 6;

    /// <summary>Makes the grid of a row of bars.</summary>
    public static BarcodeModules Create(ReadOnlySpan<FourStateBar> bars)
    {
        int width = (2 * bars.Length) - 1;
        bool[] modules = new bool[width * Rows];
        for (int i = 0; i < bars.Length; i++)
        {
            FourStateBar bar = bars[i];
            int top = bar is FourStateBar.Ascender or FourStateBar.Full ? 0 : Rows / 3;
            int bottom = bar is FourStateBar.Descender or FourStateBar.Full ? Rows : 2 * Rows / 3;
            for (int y = top; y < bottom; y++)
            {
                modules[(y * width) + (2 * i)] = true;
            }
        }

        return BarcodeModules.Wrap(width, Rows, modules);
    }
}
