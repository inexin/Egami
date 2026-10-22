namespace Egami.Barcodes;

/// <summary>
/// The modules of a barcode symbol: a grid of dark and light squares for a 2D symbol, or a row of bars and spaces for a linear
/// one. Every encoder ends in one, and drawing, rasterizing and path building work from it. It holds the symbol only, without
/// its quiet zone. A grid is immutable and safe to read from many threads.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-modules"/>
/// </example>
public sealed class BarcodeModules
{
    private readonly bool[] _dark;
    private readonly int[] _runs;

    private BarcodeModules(int width, int height, bool[] dark, int[] runs)
    {
        Width = width;
        Height = height;
        _dark = dark;
        _runs = runs;
    }

    /// <summary>Gets the width in modules.</summary>
    public int Width { get; }

    /// <summary>Gets the height in modules; for a linear symbol, the height of its bars.</summary>
    public int Height { get; }

    /// <summary>Gets a value indicating whether this is a linear symbol, whose columns are all dark or all light.</summary>
    public bool IsLinear => _runs.Length > 0;

    /// <summary>
    /// Gets the widths of a linear symbol's bars and spaces in modules, in turn from its first bar, or an empty span for a 2D
    /// symbol.
    /// </summary>
    public ReadOnlySpan<int> Runs => _runs;

    /// <summary>Gets a value indicating whether the module at a column and row is dark.</summary>
    /// <param name="x">The column, from 0 at the left.</param>
    /// <param name="y">The row, from 0 at the top.</param>
    /// <exception cref="ArgumentOutOfRangeException">The module is outside the symbol.</exception>
    public bool this[int x, int y]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)x, (uint)Width, nameof(x));
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)y, (uint)Height, nameof(y));
            return IsLinear ? _dark[x] : _dark[(y * Width) + x];
        }
    }

    /// <summary>Makes a 2D grid from its modules, row by row from the top.</summary>
    /// <param name="width">The width in modules.</param>
    /// <param name="height">The height in modules.</param>
    /// <param name="modules"><see langword="true"/> for each dark module; <paramref name="width"/> times <paramref name="height"/> values.</param>
    /// <returns>The grid, which keeps its own copy of the modules.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The width or height is not positive.</exception>
    /// <exception cref="ArgumentException">The number of modules is not the width times the height.</exception>
    public static BarcodeModules Create(int width, int height, ReadOnlySpan<bool> modules)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if ((long)width * height != modules.Length)
        {
            throw new ArgumentException($"A {width} by {height} grid needs {(long)width * height} modules, not {modules.Length}.", nameof(modules));
        }

        return new BarcodeModules(width, height, modules.ToArray(), []);
    }

    /// <summary>Makes a linear symbol from the widths of its bars and spaces.</summary>
    /// <param name="runs">The widths in modules, in turn bar, space, bar and so on, starting and ending with a bar.</param>
    /// <param name="height">The height of the bars in modules.</param>
    /// <returns>The symbol.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The height is not positive.</exception>
    /// <exception cref="ArgumentException">There is no run, a run is not positive, or the last run is a space.</exception>
    public static BarcodeModules CreateLinear(ReadOnlySpan<int> runs, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (runs.IsEmpty || runs.Length % 2 == 0)
        {
            throw new ArgumentException("A linear symbol needs an odd number of runs, starting and ending with a bar.", nameof(runs));
        }

        long width = 0;
        foreach (int run in runs)
        {
            if (run <= 0)
            {
                throw new ArgumentException("Every bar and space must be at least one module wide.", nameof(runs));
            }

            width += run;
        }

        if (width > Array.MaxLength)
        {
            throw new ArgumentException("The symbol is too wide.", nameof(runs));
        }

        bool[] dark = new bool[width];
        int x = 0;
        for (int i = 0; i < runs.Length; i++)
        {
            if (i % 2 == 0)
            {
                dark.AsSpan(x, runs[i]).Fill(true);
            }

            x += runs[i];
        }

        return new BarcodeModules((int)width, height, dark, runs.ToArray());
    }

    /// <summary>Makes a 2D grid from an array the encoder owns and hands over, without copying it.</summary>
    internal static BarcodeModules Wrap(int width, int height, bool[] modules)
    {
        System.Diagnostics.Debug.Assert(modules.Length == width * height);
        return new BarcodeModules(width, height, modules, []);
    }

    /// <summary>Makes a linear symbol from runs the encoder owns and hands over, without copying them.</summary>
    internal static BarcodeModules WrapLinear(int[] runs, int height)
    {
        System.Diagnostics.Debug.Assert(runs.Length % 2 == 1 && height > 0);
        int width = 0;
        foreach (int run in runs)
        {
            width += run;
        }

        bool[] dark = new bool[width];
        int x = 0;
        for (int i = 0; i < runs.Length; i += 2)
        {
            dark.AsSpan(x, runs[i]).Fill(true);
            x += runs[i] + (i + 1 < runs.Length ? runs[i + 1] : 0);
        }

        return new BarcodeModules(width, height, dark, runs);
    }

    /// <summary>Gets a row of a 2D grid, or the columns of a linear symbol.</summary>
    internal ReadOnlySpan<bool> Row(int y) => IsLinear ? _dark : _dark.AsSpan(y * Width, Width);
}
