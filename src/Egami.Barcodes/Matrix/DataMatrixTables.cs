namespace Egami.Barcodes.Matrix;

/// <summary>
/// Data Matrix ECC 200 symbol sizes (ISO/IEC 16022 Table 7, and the DMRE sizes of ISO/IEC 21471), in ascending order of data
/// codewords, squares before rectangles of the same capacity; and the C40 and Text character tables (Tables C.1 and C.2).
/// Transcribed through zint's backend (dmatrix.h, BSD-3; see THIRD-PARTY-NOTICES.md).
/// </summary>
internal static class DataMatrixTables
{
    /// <summary>One symbol size: rows and columns, data region rows and columns with their finder, data and error codewords per block, DMRE.</summary>
    public readonly record struct Size(int Rows, int Columns, int RegionRows, int RegionColumns, int Data, int DataPerBlock, int CheckPerBlock, bool Dmre)
    {
        public bool IsSquare => Rows == Columns;
    }

    public static readonly Size[] Sizes =
    [
        new(10, 10, 10, 10, 3, 3, 5, false), new(12, 12, 12, 12, 5, 5, 7, false), new(8, 18, 8, 18, 5, 5, 7, false),
        new(14, 14, 14, 14, 8, 8, 10, false), new(8, 32, 8, 16, 10, 10, 11, false), new(16, 16, 16, 16, 12, 12, 12, false),
        new(12, 26, 12, 26, 16, 16, 14, false), new(18, 18, 18, 18, 18, 18, 14, false), new(8, 48, 8, 24, 18, 18, 15, true),
        new(20, 20, 20, 20, 22, 22, 18, false), new(12, 36, 12, 18, 22, 22, 18, false), new(8, 64, 8, 16, 24, 24, 18, true),
        new(22, 22, 22, 22, 30, 30, 20, false), new(16, 36, 16, 18, 32, 32, 24, false), new(8, 80, 8, 20, 32, 32, 22, true),
        new(24, 24, 24, 24, 36, 36, 24, false), new(8, 96, 8, 24, 38, 38, 28, true), new(12, 64, 12, 16, 43, 43, 27, true),
        new(26, 26, 26, 26, 44, 44, 28, false), new(20, 36, 20, 18, 44, 44, 28, true), new(16, 48, 16, 24, 49, 49, 28, false),
        new(8, 120, 8, 20, 49, 49, 32, true), new(20, 44, 20, 22, 56, 56, 34, true), new(32, 32, 16, 16, 62, 62, 36, false),
        new(16, 64, 16, 16, 62, 62, 36, true), new(8, 144, 8, 24, 63, 63, 36, true), new(12, 88, 12, 22, 64, 64, 36, true),
        new(26, 40, 26, 20, 70, 70, 38, true), new(22, 48, 22, 24, 72, 72, 38, true), new(24, 48, 24, 24, 80, 80, 41, true),
        new(20, 64, 20, 16, 84, 84, 42, true), new(36, 36, 18, 18, 86, 86, 42, false), new(26, 48, 26, 24, 90, 90, 42, true),
        new(24, 64, 24, 16, 108, 108, 46, true), new(40, 40, 20, 20, 114, 114, 48, false), new(26, 64, 26, 16, 118, 118, 50, true),
        new(44, 44, 22, 22, 144, 144, 56, false), new(48, 48, 24, 24, 174, 174, 68, false), new(52, 52, 26, 26, 204, 102, 42, false),
        new(64, 64, 16, 16, 280, 140, 56, false), new(72, 72, 18, 18, 368, 92, 36, false), new(80, 80, 20, 20, 456, 114, 48, false),
        new(88, 88, 22, 22, 576, 144, 56, false), new(96, 96, 24, 24, 696, 174, 68, false), new(104, 104, 26, 26, 816, 136, 56, false),
        new(120, 120, 20, 20, 1050, 175, 68, false), new(132, 132, 22, 22, 1304, 163, 62, false), new(144, 144, 24, 24, 1558, 156, 62, false),
    ];

    /// <summary>Gets the C40 set (0 basic, 1 to 3 shift sets) of each ASCII character.</summary>
    public static ReadOnlySpan<byte> C40Shift =>
    [
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        0, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 2, 2, 2, 2, 2,
        2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 2, 2, 2, 2,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3,
    ];

    /// <summary>Gets the C40 value of each ASCII character in its set.</summary>
    public static ReadOnlySpan<byte> C40Value =>
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31,
        3, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 15, 16, 17, 18, 19, 20,
        21, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 22, 23, 24, 25, 26,
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31,
    ];

    /// <summary>Gets the Text set of each ASCII character.</summary>
    public static ReadOnlySpan<byte> TextShift =>
    [
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        0, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 2, 2, 2, 2, 2,
        2, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 2, 2, 2, 2, 2,
        3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 3, 3, 3, 3, 3,
    ];

    /// <summary>Gets the Text value of each ASCII character in its set.</summary>
    public static ReadOnlySpan<byte> TextValue =>
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31,
        3, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 15, 16, 17, 18, 19, 20,
        21, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 22, 23, 24, 25, 26,
        0, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 27, 28, 29, 30, 31,
    ];
}
