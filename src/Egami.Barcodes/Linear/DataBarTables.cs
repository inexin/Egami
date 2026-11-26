namespace Egami.Barcodes.Linear;

/// <summary>
/// GS1 DataBar tables from ISO/IEC 24724:2011: Omnidirectional character groups, finders and checksum weights (Tables 1, 2, 4
/// and 5), Limited groups, checksum weights and check character patterns (Tables 6, 7 and Annex C), and Expanded groups,
/// checksum weights, finders and finder sequences (Tables 8, 14, 15 and 16), transcribed through zint's backend (rss.h, BSD-3;
/// see THIRD-PARTY-NOTICES.md).
/// </summary>
internal static class DataBarTables
{
    // Omnidirectional (Tables 1 and 2): groups 0-4 outside characters, 5-8 inside.
    internal static ReadOnlySpan<int> OmniGroupSum => [0, 161, 961, 2015, 2715, 0, 336, 1036, 1516];

    /// <summary>T<sub>even</sub> for outside groups, T<sub>odd</sub> for inside ones.</summary>
    internal static ReadOnlySpan<int> OmniCombinations => [1, 10, 34, 70, 126, 4, 20, 48, 81];

    /// <summary>Odd modules per group, then even modules per group.</summary>
    internal static ReadOnlySpan<byte> OmniModules => [12, 10, 8, 6, 4, 5, 7, 9, 11, 4, 6, 8, 10, 12, 10, 8, 6, 4];

    /// <summary>The widest odd element per group; the widest even one is 9 minus it.</summary>
    internal static ReadOnlySpan<byte> OmniWidest => [8, 6, 4, 3, 1, 2, 4, 6, 8];

    // Table 4: the nine finder patterns, 5 elements each.
    internal static ReadOnlySpan<byte> OmniFinders =>
    [
        3, 8, 2, 1, 1,
        3, 5, 5, 1, 1,
        3, 3, 7, 1, 1,
        3, 1, 9, 1, 1,
        2, 7, 4, 1, 1,
        2, 5, 6, 1, 1,
        2, 3, 8, 1, 1,
        1, 5, 7, 1, 1,
        1, 3, 9, 1, 1,
    ];

    // Table 5: checksum weights, 8 per character.
    internal static ReadOnlySpan<byte> OmniChecksumWeights =>
    [
        1, 3, 9, 27, 2, 6, 18, 54,
        4, 12, 36, 29, 8, 24, 72, 58,
        16, 48, 65, 37, 32, 17, 51, 74,
        64, 34, 23, 69, 49, 68, 46, 59,
    ];

    // Limited (Table 6).
    internal static ReadOnlySpan<int> LimitedGroupSum => [0, 183064, 820064, 1000776, 1491021, 1979845, 1996939];

    internal static ReadOnlySpan<int> LimitedEvenCombinations => [28, 728, 6454, 203, 2408, 1, 16632];

    /// <summary>Odd modules per group; even modules are 26 minus it.</summary>
    internal static ReadOnlySpan<byte> LimitedModules => [17, 13, 9, 15, 11, 19, 7];

    /// <summary>The widest odd element per group; the widest even one is 9 minus it.</summary>
    internal static ReadOnlySpan<byte> LimitedWidest => [6, 5, 3, 5, 4, 8, 1];

    // Table 7: checksum weights, 14 per character.
    internal static ReadOnlySpan<byte> LimitedChecksumWeights =>
    [
        1, 3, 9, 27, 81, 65, 17, 51, 64, 14, 42, 37, 22, 66,
        20, 60, 2, 6, 18, 54, 73, 41, 34, 13, 39, 28, 84, 74,
    ];

    // Annex C: the 89 check character patterns, 14 elements each.
    internal static ReadOnlySpan<byte> LimitedFinders =>
    [
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 3, 3, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 2, 3, 2, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 3, 3, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 2, 1, 1, 3, 2, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 2, 1, 2, 3, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 3, 1, 1, 3, 1, 1, 1,
        1, 1, 1, 1, 1, 2, 1, 1, 1, 1, 3, 2, 1, 1,
        1, 1, 1, 1, 1, 2, 1, 1, 1, 2, 3, 1, 1, 1,
        1, 1, 1, 1, 1, 2, 1, 2, 1, 1, 3, 1, 1, 1,
        1, 1, 1, 1, 1, 3, 1, 1, 1, 1, 3, 1, 1, 1,
        1, 1, 1, 2, 1, 1, 1, 1, 1, 1, 3, 2, 1, 1,
        1, 1, 1, 2, 1, 1, 1, 1, 1, 2, 3, 1, 1, 1,
        1, 1, 1, 2, 1, 1, 1, 2, 1, 1, 3, 1, 1, 1,
        1, 1, 1, 2, 1, 2, 1, 1, 1, 1, 3, 1, 1, 1,
        1, 1, 1, 3, 1, 1, 1, 1, 1, 1, 3, 1, 1, 1,
        1, 2, 1, 1, 1, 1, 1, 1, 1, 1, 3, 2, 1, 1,
        1, 2, 1, 1, 1, 1, 1, 1, 1, 2, 3, 1, 1, 1,
        1, 2, 1, 1, 1, 1, 1, 2, 1, 1, 3, 1, 1, 1,
        1, 2, 1, 1, 1, 2, 1, 1, 1, 1, 3, 1, 1, 1,
        1, 2, 1, 2, 1, 1, 1, 1, 1, 1, 3, 1, 1, 1,
        1, 3, 1, 1, 1, 1, 1, 1, 1, 1, 3, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 2, 1, 2, 3, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 2, 3, 2, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 2, 2, 1, 2, 2, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 3, 2, 1, 2, 1, 1, 1,
        1, 1, 1, 1, 1, 2, 1, 1, 2, 1, 2, 2, 1, 1,
        1, 1, 1, 1, 1, 2, 1, 1, 2, 2, 2, 1, 1, 1,
        1, 1, 1, 1, 1, 2, 1, 2, 2, 1, 2, 1, 1, 1,
        1, 1, 1, 1, 1, 3, 1, 1, 2, 1, 2, 1, 1, 1,
        1, 1, 1, 2, 1, 1, 1, 1, 2, 1, 2, 2, 1, 1,
        1, 1, 1, 2, 1, 1, 1, 1, 2, 2, 2, 1, 1, 1,
        1, 1, 1, 2, 1, 1, 1, 2, 2, 1, 2, 1, 1, 1,
        1, 1, 1, 2, 1, 2, 1, 1, 2, 1, 2, 1, 1, 1,
        1, 1, 1, 3, 1, 1, 1, 1, 2, 1, 2, 1, 1, 1,
        1, 2, 1, 1, 1, 1, 1, 1, 2, 1, 2, 2, 1, 1,
        1, 2, 1, 1, 1, 1, 1, 1, 2, 2, 2, 1, 1, 1,
        1, 2, 1, 1, 1, 1, 1, 2, 2, 1, 2, 1, 1, 1,
        1, 2, 1, 1, 1, 2, 1, 1, 2, 1, 2, 1, 1, 1,
        1, 2, 1, 2, 1, 1, 1, 1, 2, 1, 2, 1, 1, 1,
        1, 3, 1, 1, 1, 1, 1, 1, 2, 1, 2, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 3, 1, 1, 3, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 3, 2, 1, 2, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 2, 3, 1, 1, 2, 1, 1,
        1, 1, 1, 2, 1, 1, 1, 1, 3, 1, 1, 2, 1, 1,
        1, 2, 1, 1, 1, 1, 1, 1, 3, 1, 1, 2, 1, 1,
        1, 1, 1, 1, 1, 1, 2, 1, 1, 1, 2, 3, 1, 1,
        1, 1, 1, 1, 1, 1, 2, 1, 1, 2, 2, 2, 1, 1,
        1, 1, 1, 1, 1, 1, 2, 1, 1, 3, 2, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 2, 2, 1, 1, 2, 2, 1, 1,
        1, 1, 1, 2, 1, 1, 2, 1, 1, 1, 2, 2, 1, 1,
        1, 1, 1, 2, 1, 1, 2, 1, 1, 2, 2, 1, 1, 1,
        1, 1, 1, 2, 1, 1, 2, 2, 1, 1, 2, 1, 1, 1,
        1, 1, 1, 2, 1, 2, 2, 1, 1, 1, 2, 1, 1, 1,
        1, 1, 1, 3, 1, 1, 2, 1, 1, 1, 2, 1, 1, 1,
        1, 2, 1, 1, 1, 1, 2, 1, 1, 1, 2, 2, 1, 1,
        1, 2, 1, 1, 1, 1, 2, 1, 1, 2, 2, 1, 1, 1,
        1, 2, 1, 2, 1, 1, 2, 1, 1, 1, 2, 1, 1, 1,
        1, 1, 1, 1, 2, 1, 1, 1, 1, 1, 2, 3, 1, 1,
        1, 1, 1, 1, 2, 1, 1, 1, 1, 2, 2, 2, 1, 1,
        1, 1, 1, 1, 2, 1, 1, 1, 1, 3, 2, 1, 1, 1,
        1, 1, 1, 1, 2, 1, 1, 2, 1, 1, 2, 2, 1, 1,
        1, 1, 1, 1, 2, 1, 1, 2, 1, 2, 2, 1, 1, 1,
        1, 1, 1, 1, 2, 2, 1, 1, 1, 1, 2, 2, 1, 1,
        1, 2, 1, 1, 2, 1, 1, 1, 1, 1, 2, 2, 1, 1,
        1, 2, 1, 1, 2, 1, 1, 1, 1, 2, 2, 1, 1, 1,
        1, 2, 1, 1, 2, 1, 1, 2, 1, 1, 2, 1, 1, 1,
        1, 2, 1, 1, 2, 2, 1, 1, 1, 1, 2, 1, 1, 1,
        1, 2, 1, 2, 2, 1, 1, 1, 1, 1, 2, 1, 1, 1,
        1, 3, 1, 1, 2, 1, 1, 1, 1, 1, 2, 1, 1, 1,
        1, 1, 2, 1, 1, 1, 1, 1, 1, 1, 2, 3, 1, 1,
        1, 1, 2, 1, 1, 1, 1, 1, 1, 2, 2, 2, 1, 1,
        1, 1, 2, 1, 1, 1, 1, 1, 1, 3, 2, 1, 1, 1,
        1, 1, 2, 1, 1, 1, 1, 2, 1, 1, 2, 2, 1, 1,
        1, 1, 2, 1, 1, 1, 1, 2, 1, 2, 2, 1, 1, 1,
        1, 1, 2, 1, 1, 1, 1, 3, 1, 1, 2, 1, 1, 1,
        1, 1, 2, 1, 1, 2, 1, 1, 1, 1, 2, 2, 1, 1,
        1, 1, 2, 1, 1, 2, 1, 1, 1, 2, 2, 1, 1, 1,
        1, 1, 2, 2, 1, 1, 1, 1, 1, 1, 2, 2, 1, 1,
        2, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 1, 1,
        2, 1, 1, 1, 1, 1, 1, 1, 1, 3, 2, 1, 1, 1,
        2, 1, 1, 1, 1, 1, 1, 2, 1, 1, 2, 2, 1, 1,
        2, 1, 1, 1, 1, 1, 1, 2, 1, 2, 2, 1, 1, 1,
        2, 1, 1, 1, 1, 1, 1, 3, 1, 1, 2, 1, 1, 1,
        2, 1, 1, 1, 1, 2, 1, 1, 1, 2, 2, 1, 1, 1,
        2, 1, 1, 1, 1, 2, 1, 2, 1, 1, 2, 1, 1, 1,
        2, 1, 1, 2, 1, 1, 1, 1, 1, 2, 2, 1, 1, 1,
        2, 1, 1, 1, 1, 1, 1, 1, 2, 2, 1, 2, 1, 1,
    ];

    // Expanded (Table 8).
    internal static ReadOnlySpan<int> ExpandedGroupSum => [0, 348, 1388, 2948, 3988];

    internal static ReadOnlySpan<int> ExpandedEvenCombinations => [4, 20, 52, 104, 204];

    /// <summary>Odd modules per group; even modules are 17 minus it.</summary>
    internal static ReadOnlySpan<byte> ExpandedModules => [12, 10, 8, 6, 4];

    /// <summary>The widest odd element per group; the widest even one is 9 minus it.</summary>
    internal static ReadOnlySpan<byte> ExpandedWidest => [7, 5, 4, 3, 1];

    // Table 14: checksum weights, 8 per character position.
    internal static ReadOnlySpan<byte> ExpandedChecksumWeights =>
    [
        1, 3, 9, 27, 81, 32, 96, 77,
        20, 60, 180, 118, 143, 7, 21, 63,
        189, 145, 13, 39, 117, 140, 209, 205,
        193, 157, 49, 147, 19, 57, 171, 91,
        62, 186, 136, 197, 169, 85, 44, 132,
        185, 133, 188, 142, 4, 12, 36, 108,
        113, 128, 173, 97, 80, 29, 87, 50,
        150, 28, 84, 41, 123, 158, 52, 156,
        46, 138, 203, 187, 139, 206, 196, 166,
        76, 17, 51, 153, 37, 111, 122, 155,
        43, 129, 176, 106, 107, 110, 119, 146,
        16, 48, 144, 10, 30, 90, 59, 177,
        109, 116, 137, 200, 178, 112, 125, 164,
        70, 210, 208, 202, 184, 130, 179, 115,
        134, 191, 151, 31, 93, 68, 204, 190,
        148, 22, 66, 198, 172, 94, 71, 2,
        6, 18, 54, 162, 64, 192, 154, 40,
        120, 149, 25, 75, 14, 42, 126, 167,
        79, 26, 78, 23, 69, 207, 199, 175,
        103, 98, 83, 38, 114, 131, 182, 124,
        161, 61, 183, 127, 170, 88, 53, 159,
        55, 165, 73, 8, 24, 72, 5, 15,
        45, 135, 194, 160, 58, 174, 100, 89,
    ];

    // Table 15: finders A1, A2, B1, B2, ... F2 (A2 is A1 reversed), 5 elements each.
    internal static ReadOnlySpan<byte> ExpandedFinders =>
    [
        1, 8, 4, 1, 1,
        1, 1, 4, 8, 1,
        3, 6, 4, 1, 1,
        1, 1, 4, 6, 3,
        3, 4, 6, 1, 1,
        1, 1, 6, 4, 3,
        3, 2, 8, 1, 1,
        1, 1, 8, 2, 3,
        2, 6, 5, 1, 1,
        1, 1, 5, 6, 2,
        2, 2, 9, 1, 1,
        1, 1, 9, 2, 2,
    ];

    // Table 16: the finder sequence for 1 to 10 (and 11) pairs, 1-based finder numbers, 11 per row.
    internal static ReadOnlySpan<byte> ExpandedFinderSequences =>
    [
        1, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        1, 4, 3, 0, 0, 0, 0, 0, 0, 0, 0,
        1, 6, 3, 8, 0, 0, 0, 0, 0, 0, 0,
        1, 10, 3, 8, 5, 0, 0, 0, 0, 0, 0,
        1, 10, 3, 8, 7, 12, 0, 0, 0, 0, 0,
        1, 10, 3, 8, 9, 12, 11, 0, 0, 0, 0,
        1, 2, 3, 4, 5, 6, 7, 8, 0, 0, 0,
        1, 2, 3, 4, 5, 6, 7, 10, 9, 0, 0,
        1, 2, 3, 4, 5, 6, 7, 10, 11, 12, 0,
        1, 2, 3, 4, 5, 8, 7, 10, 9, 12, 11,
    ];

    // The checksum weight row of each data character, by sequence, 21 per row.
    internal static ReadOnlySpan<byte> ExpandedWeightRows =>
    [
        0, 1, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 5, 6, 3, 4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 9, 10, 3, 4, 13, 14, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 17, 18, 3, 4, 13, 14, 7, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 17, 18, 3, 4, 13, 14, 11, 12, 21, 22, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 17, 18, 3, 4, 13, 14, 15, 16, 21, 22, 19, 20, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 0, 0, 0, 0, 0, 0,
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 17, 18, 15, 16, 0, 0, 0, 0,
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 17, 18, 19, 20, 21, 22, 0, 0,
        0, 1, 2, 3, 4, 5, 6, 7, 8, 13, 14, 11, 12, 17, 18, 15, 16, 21, 22, 19, 20,
    ];
}
