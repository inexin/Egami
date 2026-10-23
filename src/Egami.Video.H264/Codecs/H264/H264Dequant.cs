namespace Egami.Video.Codecs.H264;

/// <summary>
/// The LevelScale tables (H.264 8.5.9): for each scaling list and each qP % 6, weightScale times normAdjust per raster position.
/// </summary>
internal sealed class H264Dequant
{
    // normAdjust4x4 (8-315): v[m][0] for positions with both coordinates even, v[m][1] both odd, v[m][2] otherwise.
    private static readonly int[] V4 = [10, 16, 13, 11, 18, 14, 13, 20, 16, 14, 23, 18, 16, 25, 20, 18, 29, 23];

    // normAdjust8x8 (8-318).
    private static readonly int[] V8 =
    [
        20, 18, 32, 19, 25, 24, 22, 19, 35, 21, 28, 26, 26, 23, 42, 24, 33, 31, 28, 25, 45, 26, 35, 33, 32, 28, 51, 30, 40, 38,
        36, 32, 58, 34, 46, 43,
    ];

    internal H264Dequant(H264ScalingLists lists)
    {
        Scale4x4 = new int[6][];
        for (int list = 0; list < 6; list++)
        {
            int[] table = Scale4x4[list] = new int[6 * 16];
            for (int m = 0; m < 6; m++)
            {
                for (int pos = 0; pos < 16; pos++)
                {
                    int i = pos >> 2, j = pos & 3;
                    int v = (i & 1) == 0 && (j & 1) == 0 ? V4[(m * 3) + 0] : (i & 1) == 1 && (j & 1) == 1 ? V4[(m * 3) + 1] : V4[(m * 3) + 2];
                    table[(m * 16) + pos] = lists.List4x4[list][pos] * v;
                }
            }
        }

        Scale8x8 = new int[2][];
        for (int list = 0; list < 2; list++)
        {
            int[] table = Scale8x8[list] = new int[6 * 64];
            for (int m = 0; m < 6; m++)
            {
                for (int pos = 0; pos < 64; pos++)
                {
                    int i = pos >> 3, j = pos & 7;
                    int k = (i % 4 == 0 && j % 4 == 0) ? 0
                        : (i % 2 == 1 && j % 2 == 1) ? 1
                        : (i % 4 == 2 && j % 4 == 2) ? 2
                        : ((i % 4 == 0 && j % 2 == 1) || (i % 2 == 1 && j % 4 == 0)) ? 3
                        : ((i % 4 == 0 && j % 4 == 2) || (i % 4 == 2 && j % 4 == 0)) ? 4 : 5;
                    table[(m * 64) + pos] = lists.List8x8[list][pos] * V8[(m * 6) + k];
                }
            }
        }
    }

    /// <summary>Gets LevelScale4x4 per list (0 to 2 intra Y, Cb, Cr; 3 to 5 inter), indexed [m * 16 + raster position].</summary>
    internal int[][] Scale4x4 { get; }

    /// <summary>Gets LevelScale8x8 per list (0 intra Y, 1 inter Y), indexed [m * 64 + raster position].</summary>
    internal int[][] Scale8x8 { get; }
}
