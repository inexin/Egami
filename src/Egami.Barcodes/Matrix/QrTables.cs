namespace Egami.Barcodes.Matrix;

/// <summary>
/// QR Code capacity tables from ISO/IEC 18004: total codewords per version (Table 1), data codewords per version and level
/// (Table 7) and error correction blocks per version and level (Table 9), transcribed through zint's backend (qr.h, BSD-3; see
/// THIRD-PARTY-NOTICES.md). Levels are indexed L, M, Q, H.
/// </summary>
internal static class QrTables
{
    public static ReadOnlySpan<ushort> TotalCodewords =>
    [
        26, 44, 70, 100, 134, 172, 196, 242, 292, 346,
        404, 466, 532, 581, 655, 733, 815, 901, 991, 1085,
        1156, 1258, 1364, 1474, 1588, 1706, 1828, 1921, 2051, 2185,
        2323, 2465, 2611, 2761, 2876, 3034, 3196, 3362, 3532, 3706,
    ];

    private static ReadOnlySpan<ushort> DataCodewordsTable =>
    [
        19, 34, 55, 80, 108, 136, 156, 194, 232, 274,
        324, 370, 428, 461, 523, 589, 647, 721, 795, 861,
        932, 1006, 1094, 1174, 1276, 1370, 1468, 1531, 1631, 1735,
        1843, 1955, 2071, 2191, 2306, 2434, 2566, 2702, 2812, 2956,

        16, 28, 44, 64, 86, 108, 124, 154, 182, 216,
        254, 290, 334, 365, 415, 453, 507, 563, 627, 669,
        714, 782, 860, 914, 1000, 1062, 1128, 1193, 1267, 1373,
        1455, 1541, 1631, 1725, 1812, 1914, 1992, 2102, 2216, 2334,

        13, 22, 34, 48, 62, 76, 88, 110, 132, 154,
        180, 206, 244, 261, 295, 325, 367, 397, 445, 485,
        512, 568, 614, 664, 718, 754, 808, 871, 911, 985,
        1033, 1115, 1171, 1231, 1286, 1354, 1426, 1502, 1582, 1666,

        9, 16, 26, 36, 46, 60, 66, 86, 100, 122,
        140, 158, 180, 197, 223, 253, 283, 313, 341, 385,
        406, 442, 464, 514, 538, 596, 628, 661, 701, 745,
        793, 845, 901, 961, 986, 1054, 1096, 1142, 1222, 1276,
    ];

    private static ReadOnlySpan<byte> BlocksTable =>
    [
        1, 1, 1, 1, 1, 2, 2, 2, 2, 4,
        4, 4, 4, 4, 6, 6, 6, 6, 7, 8,
        8, 9, 9, 10, 12, 12, 12, 13, 14, 15,
        16, 17, 18, 19, 19, 20, 21, 22, 24, 25,

        1, 1, 1, 2, 2, 4, 4, 4, 5, 5,
        5, 8, 9, 9, 10, 10, 11, 13, 14, 16,
        17, 17, 18, 20, 21, 23, 25, 26, 28, 29,
        31, 33, 35, 37, 38, 40, 43, 45, 47, 49,

        1, 1, 2, 2, 4, 4, 6, 6, 8, 8,
        8, 10, 12, 16, 12, 17, 16, 18, 21, 20,
        23, 23, 25, 27, 29, 34, 34, 35, 38, 40,
        43, 45, 48, 51, 53, 56, 59, 62, 65, 68,

        1, 1, 2, 4, 4, 4, 5, 6, 8, 8,
        11, 11, 16, 16, 18, 16, 19, 21, 25, 25,
        25, 34, 30, 32, 35, 37, 40, 42, 45, 48,
        51, 54, 57, 60, 63, 66, 70, 74, 77, 81,
    ];

    /// <summary>Returns the data codewords of a version (1 to 40) at a level (0 L, 1 M, 2 Q, 3 H).</summary>
    public static int DataCodewords(int version, int level) => DataCodewordsTable[(level * 40) + version - 1];

    /// <summary>Returns the number of error correction blocks of a version at a level.</summary>
    public static int Blocks(int version, int level) => BlocksTable[(level * 40) + version - 1];
}
