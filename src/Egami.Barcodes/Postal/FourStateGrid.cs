namespace Egami.Barcodes.Postal;

/// <summary>
/// Lays 4-state bars out as a grid of square modules: one dark column per bar, one light column between bars, and rows for
/// the ascender, the tracker and the descender, so that at its standard's module size each bar has the standard's heights.
/// </summary>
internal static class FourStateGrid
{
    /// <summary>Makes the grid of a row of bars.</summary>
    /// <param name="bars">The bars, left to right.</param>
    /// <param name="extender">The rows of the ascender above the tracker, and of the descender below it.</param>
    /// <param name="tracker">The rows of the tracker.</param>
    public static BarcodeModules Create(ReadOnlySpan<FourStateBar> bars, int extender, int tracker)
    {
        int width = (2 * bars.Length) - 1, rows = (2 * extender) + tracker;
        bool[] modules = new bool[width * rows];
        for (int i = 0; i < bars.Length; i++)
        {
            FourStateBar bar = bars[i];
            int top = bar is FourStateBar.Ascender or FourStateBar.Full ? 0 : extender;
            int bottom = bar is FourStateBar.Descender or FourStateBar.Full ? rows : extender + tracker;
            for (int y = top; y < bottom; y++)
            {
                modules[(y * width) + (2 * i)] = true;
            }
        }

        return BarcodeModules.Wrap(width, rows, modules);
    }

    /// <summary>Gets the bars a row of letters names: F full, A ascender, D descender, T tracker.</summary>
    public static FourStateBar[] Bars(string letters) =>
        [.. letters.Select(c => c switch
        {
            'F' => FourStateBar.Full,
            'A' => FourStateBar.Ascender,
            'D' => FourStateBar.Descender,
            _ => FourStateBar.Tracker,
        })];
}
