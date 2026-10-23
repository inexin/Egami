namespace Egami.Video.Codecs.H264;

/// <summary>The decoder's constant tables and the CAVLC code tables built from them.</summary>
internal static partial class H264Tables
{
    /// <summary>coeff_token tables by nC class: 0 &lt;= nC &lt; 2, 2 &lt;= nC &lt; 4, 4 &lt;= nC &lt; 8, nC == -1, nC == -2 (8 &lt;= nC is a 6-bit code).</summary>
    internal static readonly H264Vlc[] CoeffTokenVlc = [Token(0), Token(1), Token(2), Token(4), Token(5)];

    /// <summary>total_zeros tables for 4x4 blocks by TotalCoeff (index 1 to 15).</summary>
    internal static readonly H264Vlc[] TotalZerosVlc = [.. Enumerable.Range(0, 16).Select(t => t == 0 ? null! : Pairs(TotalZeros, t * 16, 16))];

    /// <summary>total_zeros tables for 4:2:0 chroma DC by TotalCoeff (1 to 3).</summary>
    internal static readonly H264Vlc[] TotalZerosChromaDc420Vlc = [.. Enumerable.Range(0, 4).Select(t => t == 0 ? null! : Pairs(TotalZerosChromaDc420, t * 4, 4))];

    /// <summary>total_zeros tables for 4:2:2 chroma DC by TotalCoeff (1 to 7).</summary>
    internal static readonly H264Vlc[] TotalZerosChromaDc422Vlc = [.. Enumerable.Range(0, 8).Select(t => t == 0 ? null! : Pairs(TotalZerosChromaDc422, t * 8, 8))];

    /// <summary>run_before tables by Min(zerosLeft, 7) (1 to 7).</summary>
    internal static readonly H264Vlc[] RunBeforeVlc = [.. Enumerable.Range(0, 8).Select(z => z == 0 ? null! : Pairs(RunBefore, z * 15, 15))];

    /// <summary>The position of each luma4x4BlkIdx in its macroblock, in 4x4 blocks: x in the low 2 bits, y above (6.4.3).</summary>
    internal static ReadOnlySpan<byte> BlockXY => [0, 1, 4, 5, 2, 3, 6, 7, 8, 9, 12, 13, 10, 11, 14, 15];

    /// <summary>QPC for each qPI from 0 to 51 (Table 8-15).</summary>
    internal static ReadOnlySpan<byte> ChromaQp =>
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29,
        29, 30, 31, 32, 32, 33, 34, 34, 35, 35, 36, 36, 37, 37, 37, 38, 38, 38, 39, 39, 39, 39,
    ];

    /// <summary>coded_block_pattern for each codeNum (Table 9-4), intra then inter, for ChromaArrayType 1 and 2.</summary>
    internal static ReadOnlySpan<byte> CbpIntra =>
    [
        47, 31, 15, 0, 23, 27, 29, 30, 7, 11, 13, 14, 39, 43, 45, 46, 16, 3, 5, 10, 12, 19, 21, 26, 28, 35, 37, 42, 44, 1, 2, 4,
        8, 17, 18, 20, 24, 6, 9, 22, 25, 32, 33, 34, 36, 40, 38, 41,
    ];

    internal static ReadOnlySpan<byte> CbpInter =>
    [
        0, 16, 1, 2, 4, 8, 32, 3, 5, 10, 12, 15, 47, 7, 11, 13, 14, 6, 9, 31, 35, 37, 42, 44, 33, 34, 36, 40, 39, 43, 45, 46,
        17, 18, 20, 24, 19, 21, 26, 28, 23, 27, 29, 30, 22, 25, 38, 41,
    ];

    /// <summary>coded_block_pattern for each codeNum for ChromaArrayType 0 and 3, intra then inter.</summary>
    internal static ReadOnlySpan<byte> CbpIntraMono => [15, 0, 7, 11, 13, 14, 3, 5, 10, 12, 1, 2, 4, 8, 6, 9];

    internal static ReadOnlySpan<byte> CbpInterMono => [0, 1, 2, 4, 8, 3, 5, 10, 12, 15, 7, 11, 13, 14, 6, 9];

    /// <summary>The 4:2:2 chroma DC scan: the raster position (row * 2 + column) of chroma DC coefficient k (8-330).</summary>
    internal static ReadOnlySpan<byte> ChromaDc422Scan => [0, 2, 1, 4, 6, 3, 5, 7];

    /// <summary>B sub_mb_type: pred flags (bit 0 L0, bit 1 L1; 0 is direct) and partition shape (0 8x8, 1 8x4, 2 4x8, 3 4x4).</summary>
    internal static ReadOnlySpan<byte> BSubPred => [0, 1, 2, 3, 1, 1, 2, 2, 3, 3, 1, 2, 3];

    internal static ReadOnlySpan<byte> BSubShape => [0, 0, 0, 0, 1, 2, 1, 2, 1, 2, 3, 3, 3];

    /// <summary>The pred flags of the two partitions of B mb_type 4 to 21, by (mb_type - 4) / 2.</summary>
    internal static ReadOnlySpan<byte> BPartPred => [1, 1, 2, 2, 1, 2, 2, 1, 1, 3, 2, 3, 3, 1, 3, 2, 3, 3];

    // Table 8-16: alpha' and beta' by indexA and indexB, and tC0' by indexA and bS (1 to 3).
    internal static ReadOnlySpan<byte> Alpha =>
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 4, 4, 5, 6, 7, 8, 9, 10, 12, 13, 15, 17, 20, 22, 25, 28,
        32, 36, 40, 45, 50, 56, 63, 71, 80, 90, 101, 113, 127, 144, 162, 182, 203, 226, 255, 255,
    ];

    internal static ReadOnlySpan<byte> Beta =>
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 6, 6, 7, 7, 8, 8,
        9, 9, 10, 10, 11, 11, 12, 12, 13, 13, 14, 14, 15, 15, 16, 16, 17, 17, 18, 18,
    ];

    internal static ReadOnlySpan<byte> Tc0 =>
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 1, 1, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 2, 1, 1, 2, 1, 1, 2, 1, 1, 2, 1, 2, 3, 1, 2, 3, 2, 2, 3, 2, 2, 4, 2, 3, 4, 2, 3, 4, 3, 3, 5, 3, 4, 6,
        3, 4, 6, 4, 5, 7, 4, 5, 8, 4, 6, 9, 5, 7, 10, 6, 8, 11, 6, 8, 13, 7, 10, 14, 8, 11, 16, 9, 12, 18, 10, 13, 20, 11, 15, 23, 13, 17, 25,
    ];

    // Table 9-43: ctxIdxInc of significant_coeff_flag and last_significant_coeff_flag for 8x8 frame blocks.
    internal static ReadOnlySpan<byte> Significant8x8 =>
    [
        0, 1, 2, 3, 4, 5, 5, 4, 4, 3, 3, 4, 4, 4, 5, 5, 4, 4, 4, 4, 3, 3, 6, 7, 7, 7, 8, 9, 10, 9, 8, 7,
        7, 6, 11, 12, 13, 11, 6, 7, 8, 9, 14, 10, 9, 8, 6, 11, 12, 13, 11, 6, 9, 14, 10, 9, 11, 12, 13, 11, 14, 10, 12, 14,
    ];

    internal static ReadOnlySpan<byte> Last8x8 =>
    [
        0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2,
        3, 3, 3, 3, 3, 3, 3, 3, 4, 4, 4, 4, 4, 4, 4, 4, 5, 5, 5, 5, 6, 6, 6, 6, 7, 7, 7, 7, 8, 8, 8, 8,
    ];

    private static H264Vlc Token(int table)
    {
        List<(int, int, int)> codes = [];
        for (int total = 0; total <= 16; total++)
        {
            for (int ones = 0; ones < 4; ones++)
            {
                int i = (((table * 17) + total) * 4 + ones) * 2;
                codes.Add((CoeffToken[i], CoeffToken[i + 1], (total * 4) + ones));
            }
        }

        return new H264Vlc(codes);
    }

    private static H264Vlc Pairs(ReadOnlySpan<byte> table, int start, int count)
    {
        List<(int, int, int)> codes = [];
        for (int v = 0; v < count; v++)
        {
            codes.Add((table[(start + v) * 2], table[((start + v) * 2) + 1], v));
        }

        return new H264Vlc(codes);
    }
}
