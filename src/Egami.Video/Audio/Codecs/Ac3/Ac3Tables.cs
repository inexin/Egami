namespace Egami.Video.Audio.Codecs.Ac3;

/// <summary>The fixed tables of the AC-3 decoder, from ATSC A/52:2018 section 7.</summary>
internal static class Ac3Tables
{
    /// <summary>Gets slowdec[] (Table 7.6).</summary>
    public static ReadOnlySpan<short> SlowDecay => [0x0f, 0x11, 0x13, 0x15];

    /// <summary>Gets fastdec[] (Table 7.7).</summary>
    public static ReadOnlySpan<short> FastDecay => [0x3f, 0x53, 0x67, 0x7b];

    /// <summary>Gets slowgain[] (Table 7.8).</summary>
    public static ReadOnlySpan<short> SlowGain => [0x540, 0x4d8, 0x478, 0x410];

    /// <summary>Gets dbpbtab[] (Table 7.9).</summary>
    public static ReadOnlySpan<short> DbPerBit => [0x000, 0x700, 0x900, 0xb00];

    /// <summary>Gets floortab[] (Table 7.10); the last entry, 0xf800, is negative.</summary>
    public static ReadOnlySpan<short> Floor => [0x2f0, 0x2b0, 0x270, 0x230, 0x1f0, 0x170, 0x0f0, unchecked((short)0xf800)];

    /// <summary>Gets fastgain[] (Table 7.11).</summary>
    public static ReadOnlySpan<short> FastGain => [0x080, 0x100, 0x180, 0x200, 0x280, 0x300, 0x380, 0x400];

    /// <summary>Gets bndtab[], the first bin of each of the 50 bit allocation bands (Table 7.12); a 51st entry closes the last.</summary>
    public static ReadOnlySpan<byte> BandStart =>
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 31, 34, 37, 40,
        43, 46, 49, 55, 61, 67, 73, 79, 85, 97, 109, 121, 133, 157, 181, 205, 229, 253,
    ];

    /// <summary>Gets latab[], the log-addition table (Table 7.14).</summary>
    public static ReadOnlySpan<byte> LogAdd =>
    [
        64, 63, 62, 61, 60, 59, 58, 57, 56, 55, 54, 53, 52, 52, 51, 50, 49, 48, 47, 47, 46, 45, 44, 44, 43, 42, 41, 41, 40, 39, 38, 38,
        37, 36, 36, 35, 35, 34, 33, 33, 32, 32, 31, 30, 30, 29, 29, 28, 28, 27, 27, 26, 26, 25, 25, 24, 24, 23, 23, 22, 22, 21, 21, 21,
        20, 20, 19, 19, 19, 18, 18, 18, 17, 17, 17, 16, 16, 16, 15, 15, 15, 14, 14, 14, 13, 13, 13, 13, 12, 12, 12, 12, 11, 11, 11, 11,
        10, 10, 10, 10, 10, 9, 9, 9, 9, 9, 8, 8, 8, 8, 8, 8, 7, 7, 7, 7, 7, 7, 6, 6, 6, 6, 6, 6, 6, 6, 5, 5, 5, 5, 5, 5, 5, 5, 4, 4, 4,
        4, 4, 4, 4, 4, 4, 4, 4, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    ];

    /// <summary>Gets hth[fscod][band], the hearing threshold (Table 7.15), 50 bands per sample rate code.</summary>
    public static ReadOnlySpan<short> HearingThreshold =>
    [
        1232, 1232, 1088, 1024, 992, 960, 944, 944, 928, 928, 928, 928, 928, 912, 912, 912, 896, 896, 880, 880, 864, 864, 848, 848, 832,
        832, 816, 800, 784, 768, 752, 752, 752, 752, 768, 784, 832, 912, 992, 1056, 1120, 1168, 1184, 1120, 1088, 1088, 1312, 2048, 2112, 2112,
        1264, 1264, 1120, 1040, 992, 976, 960, 944, 944, 928, 928, 928, 928, 928, 912, 912, 912, 896, 896, 896, 880, 880, 864, 864, 848,
        848, 832, 832, 800, 784, 768, 752, 752, 752, 752, 768, 800, 848, 912, 992, 1056, 1104, 1184, 1168, 1120, 1088, 1152, 1584, 2112, 2112,
        1408, 1408, 1200, 1104, 1056, 1008, 992, 976, 960, 944, 944, 944, 928, 928, 928, 928, 928, 928, 928, 928, 912, 912, 912, 912, 896,
        896, 896, 880, 864, 848, 832, 816, 800, 784, 768, 752, 752, 752, 768, 784, 816, 848, 960, 1040, 1136, 1184, 1120, 1088, 1104, 1248,
    ];

    /// <summary>Gets baptab[] (Table 7.16).</summary>
    public static ReadOnlySpan<byte> Bap =>
    [
        0, 1, 1, 1, 1, 1, 2, 2, 3, 3, 3, 4, 4, 5, 5, 6, 6, 6, 6, 7, 7, 7, 7, 8, 8, 8, 8, 9, 9, 9, 9, 10, 10, 10, 10, 11, 11, 11, 11,
        12, 12, 12, 12, 13, 13, 13, 13, 14, 14, 14, 14, 14, 14, 14, 14, 15, 15, 15, 15, 15, 15, 15, 15, 15,
    ];

    /// <summary>Gets the mantissa bits per bap (Table 7.18), with the group sizes for baps 1, 2 and 4.</summary>
    public static ReadOnlySpan<byte> MantissaBits => [0, 5, 7, 3, 7, 4, 5, 6, 7, 8, 9, 10, 11, 12, 14, 16];

    /// <summary>Gets masktab[], the bit allocation band of each bin (Table 7.13), built from <see cref="BandStart"/>.</summary>
    public static byte[] BandOfBin { get; } = MakeBandOfBin();

    /// <summary>Gets the 3-level mantissas of each group code 0 to 26 (Tables 7.19), three per code.</summary>
    public static float[] Group3 { get; } = MakeGroups(3, 3);

    /// <summary>Gets the 5-level mantissas of each group code 0 to 124 (Table 7.20), three per code.</summary>
    public static float[] Group5 { get; } = MakeGroups(5, 3);

    /// <summary>Gets the 11-level mantissas of each group code 0 to 120 (Table 7.22), two per code.</summary>
    public static float[] Group11 { get; } = MakeGroups(11, 2);

    /// <summary>Gets the 7-level mantissas (Table 7.21); code 7 is out of range and reads as 0.</summary>
    public static float[] Level7 { get; } = MakeLevels(7, 8);

    /// <summary>Gets the 15-level mantissas (Table 7.23); code 15 is out of range and reads as 0.</summary>
    public static float[] Level15 { get; } = MakeLevels(15, 16);

    /// <summary>Gets 2^-e for the exponents 0 to 24, and room for the shifts coupling adds.</summary>
    public static float[] PowerOfTwo { get; } = MakePowers();

    /// <summary>Gets the 512-point Kaiser-Bessel-derived window, alpha 5: the 256 values of Table 7.33, to full precision.</summary>
    public static float[] Window { get; } = MakeWindow();

    private static byte[] MakeBandOfBin()
    {
        byte[] table = new byte[256];
        for (int band = 0; band < 50; band++)
        {
            for (int bin = BandStart[band]; bin < BandStart[band + 1]; bin++)
            {
                table[bin] = (byte)band;
            }
        }

        return table;
    }

    private static float[] MakeLevels(int levels, int size)
    {
        float[] table = new float[size];
        for (int code = 0; code < levels; code++)
        {
            table[code] = (float)((2.0 * code - (levels - 1)) / levels);
        }

        return table;
    }

    private static float[] MakeGroups(int levels, int count)
    {
        int codes = count == 3 ? levels * levels * levels : levels * levels;
        float[] table = new float[codes * count];
        for (int code = 0; code < codes; code++)
        {
            int rest = code;
            for (int i = count - 1; i >= 0; i--)
            {
                int m = rest % levels;
                rest /= levels;
                table[(code * count) + i] = (float)((2.0 * m - (levels - 1)) / levels);
            }
        }

        return table;
    }

    private static float[] MakePowers()
    {
        float[] table = new float[64];
        for (int e = 0; e < table.Length; e++)
        {
            table[e] = MathF.ScaleB(1f, -e);
        }

        return table;
    }

    private static float[] MakeWindow()
    {
        // w[n] = sqrt(sum of the Kaiser kernel up to n / its total), with the kernel I0(pi * alpha * sqrt(1 - (n / 128 - 1)^2)).
        const int Half = 256;
        double[] kernel = new double[Half + 1];
        double total = 0;
        for (int n = 0; n <= Half; n++)
        {
            double x = (2.0 * n / Half) - 1;
            kernel[n] = BesselI0(Math.PI * 5 * Math.Sqrt(1 - (x * x)));
            total += kernel[n];
        }

        float[] window = new float[Half];
        double sum = 0;
        for (int n = 0; n < Half; n++)
        {
            sum += kernel[n];
            window[n] = (float)Math.Sqrt(sum / total);
        }

        return window;
    }

    private static double BesselI0(double x)
    {
        double sum = 1;
        double term = 1;
        double q = x * x / 4;
        for (int k = 1; k < 100 && term > sum * 1e-17; k++)
        {
            term *= q / ((double)k * k);
            sum += term;
        }

        return sum;
    }
}
