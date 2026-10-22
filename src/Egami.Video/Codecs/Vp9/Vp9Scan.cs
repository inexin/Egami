namespace Egami.Video.Codecs.Vp9;

/// <summary>A coefficient scan order and, for each position, the two earlier positions whose tokens give its context (vp9_scan.c).</summary>
internal readonly struct Vp9Scan
{
    private static readonly Vp9Scan[] Defaults =
    [
        new(Vp9Tables.DefaultScan4x4, Vp9Tables.DefaultScan4x4Neighbors),
        new(Vp9Tables.DefaultScan8x8, Vp9Tables.DefaultScan8x8Neighbors),
        new(Vp9Tables.DefaultScan16x16, Vp9Tables.DefaultScan16x16Neighbors),
        new(Vp9Tables.DefaultScan32x32, Vp9Tables.DefaultScan32x32Neighbors),
    ];

    // [tx size][tx type]: ADST_DCT (vertical ADST) scans by rows, DCT_ADST by columns, and 32x32 always uses the default scan.
    private static readonly Vp9Scan[] All =
    [
        Defaults[0], new(Vp9Tables.RowScan4x4, Vp9Tables.RowScan4x4Neighbors), new(Vp9Tables.ColScan4x4, Vp9Tables.ColScan4x4Neighbors), Defaults[0],
        Defaults[1], new(Vp9Tables.RowScan8x8, Vp9Tables.RowScan8x8Neighbors), new(Vp9Tables.ColScan8x8, Vp9Tables.ColScan8x8Neighbors), Defaults[1],
        Defaults[2], new(Vp9Tables.RowScan16x16, Vp9Tables.RowScan16x16Neighbors), new(Vp9Tables.ColScan16x16, Vp9Tables.ColScan16x16Neighbors), Defaults[2],
        Defaults[3], Defaults[3], Defaults[3], Defaults[3],
    ];

    private readonly short[] _order;
    private readonly short[] _neighbors;

    private Vp9Scan(short[] order, short[] neighbors)
    {
        _order = order;
        _neighbors = neighbors;
    }

    /// <summary>Gets the raster position of each coefficient, in coding order.</summary>
    public ReadOnlySpan<short> Order => _order;

    /// <summary>Gets two raster positions per coefficient (above and left in the scan's sense).</summary>
    public ReadOnlySpan<short> Neighbors => _neighbors;

    public static Vp9Scan Default(int txSize) => Defaults[txSize];

    public static Vp9Scan For(int txSize, int txType) => All[(txSize * 4) + txType];
}
