namespace Egami.Barcodes.Matrix;

/// <summary>
/// Lays out a QR Code symbol (ISO/IEC 18004 section 7): finder, separator, timing and alignment patterns, format and version
/// information, the codewords in the zigzag order, and the mask whose penalty (section 7.8.3) is lowest.
/// </summary>
internal static class QrMatrix
{
    /// <summary>Builds the symbol: its modules row by row and the mask used.</summary>
    public static (bool[] Modules, int Mask) Build(int version, int level, byte[] codewords, int fixedMask)
    {
        int size = 17 + (4 * version);
        bool[] dark = new bool[size * size];
        bool[] function = new bool[size * size];
        DrawFunctionPatterns(dark, function, size, version);
        PlaceCodewords(dark, function, size, codewords);

        int mask = fixedMask;
        if (mask < 0)
        {
            int bestPenalty = int.MaxValue;
            for (int m = 0; m < 8; m++)
            {
                ApplyMask(dark, function, size, m);
                DrawFormat(dark, size, level, m);
                int penalty = Penalty(dark, size);
                if (penalty < bestPenalty)
                {
                    (bestPenalty, mask) = (penalty, m);
                }

                ApplyMask(dark, function, size, m);
            }
        }

        ApplyMask(dark, function, size, mask);
        DrawFormat(dark, size, level, mask);
        return (dark, mask);
    }

    /// <summary>Returns which modules of a version are function patterns or format and version areas, not data.</summary>
    internal static bool[] FunctionMask(int version)
    {
        int size = 17 + (4 * version);
        bool[] function = new bool[size * size];
        DrawFunctionPatterns(new bool[size * size], function, size, version);
        return function;
    }

    /// <summary>Returns the 15-bit format word (masked with 0x5412) for a level (0 L to 3 H) and mask.</summary>
    internal static int FormatBits(int level, int mask)
    {
        int data = (level switch { 0 => 1, 1 => 0, 2 => 3, _ => 2 } << 3) | mask;
        int remainder = data;
        for (int i = 0; i < 10; i++)
        {
            remainder = (remainder << 1) ^ ((remainder >> 9) * 0x537);
        }

        return ((data << 10) | remainder) ^ 0x5412;
    }

    /// <summary>Returns the 18-bit version word for versions 7 to 40.</summary>
    internal static int VersionBits(int version)
    {
        int remainder = version;
        for (int i = 0; i < 12; i++)
        {
            remainder = (remainder << 1) ^ ((remainder >> 11) * 0x1F25);
        }

        return (version << 12) | remainder;
    }

    /// <summary>Returns whether a data module is inverted by a mask.</summary>
    internal static bool Masked(int mask, int x, int y) => mask switch
    {
        0 => (x + y) % 2 == 0,
        1 => y % 2 == 0,
        2 => x % 3 == 0,
        3 => (x + y) % 3 == 0,
        4 => ((y / 2) + (x / 3)) % 2 == 0,
        5 => ((x * y) % 2) + ((x * y) % 3) == 0,
        6 => (((x * y) % 2) + ((x * y) % 3)) % 2 == 0,
        _ => (((x + y) % 2) + ((x * y) % 3)) % 2 == 0,
    };

    private static void DrawFunctionPatterns(bool[] dark, bool[] function, int size, int version)
    {
        void Set(int x, int y, bool value)
        {
            dark[(y * size) + x] = value;
            function[(y * size) + x] = true;
        }

        // Timing patterns.
        for (int i = 0; i < size; i++)
        {
            Set(6, i, i % 2 == 0);
            Set(i, 6, i % 2 == 0);
        }

        // Finder patterns with their separators.
        foreach ((int cx, int cy) in (ReadOnlySpan<(int, int)>)[(3, 3), (size - 4, 3), (3, size - 4)])
        {
            for (int dy = -4; dy <= 4; dy++)
            {
                for (int dx = -4; dx <= 4; dx++)
                {
                    int x = cx + dx, y = cy + dy;
                    if (x >= 0 && x < size && y >= 0 && y < size)
                    {
                        int distance = Math.Max(Math.Abs(dx), Math.Abs(dy));
                        Set(x, y, distance != 2 && distance != 4);
                    }
                }
            }
        }

        // Alignment patterns, except where they would overlap a finder.
        int[] positions = AlignmentPositions(version);
        for (int i = 0; i < positions.Length; i++)
        {
            for (int j = 0; j < positions.Length; j++)
            {
                bool corner = (i == 0 && j == 0) || (i == 0 && j == positions.Length - 1) || (i == positions.Length - 1 && j == 0);
                if (corner)
                {
                    continue;
                }

                for (int dy = -2; dy <= 2; dy++)
                {
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        Set(positions[i] + dx, positions[j] + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
                    }
                }
            }
        }

        // Reserve the format areas (drawn per mask) and the dark module.
        for (int i = 0; i < 9; i++)
        {
            function[(8 * size) + i] = function[(i * size) + 8] = true;
        }

        for (int i = 0; i < 8; i++)
        {
            function[(8 * size) + size - 1 - i] = function[((size - 1 - i) * size) + 8] = true;
        }

        Set(8, size - 8, true);

        // Version information: 6 bits and a BCH(18, 6) remainder, in two 6 x 3 blocks.
        if (version >= 7)
        {
            int remainder = version;
            for (int i = 0; i < 12; i++)
            {
                remainder = (remainder << 1) ^ ((remainder >> 11) * 0x1F25);
            }

            int bits = (version << 12) | remainder;
            for (int i = 0; i < 18; i++)
            {
                bool bit = ((bits >> i) & 1) != 0;
                int a = size - 11 + (i % 3), b = i / 3;
                Set(a, b, bit);
                Set(b, a, bit);
            }
        }
    }

    /// <summary>Returns the row and column positions of the alignment patterns' centers (ISO/IEC 18004 Table E.1).</summary>
    internal static int[] AlignmentPositions(int version)
    {
        if (version == 1)
        {
            return [];
        }

        int count = (version / 7) + 2, size = 17 + (4 * version);
        int step = version == 32 ? 26 : ((((size - 13) + (count * 2) - 3) / ((count * 2) - 2)) * 2);
        int[] positions = new int[count];
        positions[0] = 6;
        for (int i = count - 1, p = size - 7; i >= 1; i--, p -= step)
        {
            positions[i] = p;
        }

        return positions;
    }

    /// <summary>Places the codewords' bits in two-module columns from the bottom right, up and down in turn, skipping the timing column.</summary>
    private static void PlaceCodewords(bool[] dark, bool[] function, int size, byte[] codewords)
    {
        int bit = 0, total = codewords.Length * 8;
        for (int right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6)
            {
                right = 5;
            }

            for (int vertical = 0; vertical < size; vertical++)
            {
                for (int j = 0; j < 2; j++)
                {
                    int x = right - j;
                    bool upward = ((right + 1) & 2) == 0;
                    int y = upward ? size - 1 - vertical : vertical;
                    if (!function[(y * size) + x] && bit < total)
                    {
                        dark[(y * size) + x] = ((codewords[bit >> 3] >> (7 - (bit & 7))) & 1) != 0;
                        bit++;
                    }
                }
            }
        }
    }

    private static void ApplyMask(bool[] dark, bool[] function, int size, int mask)
    {
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                bool invert = mask switch
                {
                    0 => (x + y) % 2 == 0,
                    1 => y % 2 == 0,
                    2 => x % 3 == 0,
                    3 => (x + y) % 3 == 0,
                    4 => ((y / 2) + (x / 3)) % 2 == 0,
                    5 => ((x * y) % 2) + ((x * y) % 3) == 0,
                    6 => (((x * y) % 2) + ((x * y) % 3)) % 2 == 0,
                    _ => (((x + y) % 2) + ((x * y) % 3)) % 2 == 0,
                };
                if (invert && !function[(y * size) + x])
                {
                    dark[(y * size) + x] = !dark[(y * size) + x];
                }
            }
        }
    }

    /// <summary>Draws the 15 format bits (level, mask and a BCH(15, 5) remainder, masked with 0x5412) in both places.</summary>
    private static void DrawFormat(bool[] dark, int size, int level, int mask)
    {
        int data = (level switch { 0 => 1, 1 => 0, 2 => 3, _ => 2 } << 3) | mask;
        int remainder = data;
        for (int i = 0; i < 10; i++)
        {
            remainder = (remainder << 1) ^ ((remainder >> 9) * 0x537);
        }

        int bits = ((data << 10) | remainder) ^ 0x5412;
        void Set(int x, int y, int i) => dark[(y * size) + x] = ((bits >> i) & 1) != 0;

        for (int i = 0; i <= 5; i++)
        {
            Set(8, i, i);
        }

        Set(8, 7, 6);
        Set(8, 8, 7);
        Set(7, 8, 8);
        for (int i = 9; i < 15; i++)
        {
            Set(14 - i, 8, i);
        }

        for (int i = 0; i < 8; i++)
        {
            Set(size - 1 - i, 8, i);
        }

        for (int i = 8; i < 15; i++)
        {
            Set(8, size - 15 + i, i);
        }

        dark[((size - 8) * size) + 8] = true;
    }

    /// <summary>The mask penalty: runs of five or more, 2 x 2 blocks, finder-like patterns and the share of dark modules.</summary>
    internal static int Penalty(bool[] dark, int size)
    {
        int penalty = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            for (int a = 0; a < size; a++)
            {
                int run = 0;
                bool color = false;
                int pattern = 0;
                for (int b = 0; b < size; b++)
                {
                    bool value = pass == 0 ? dark[(a * size) + b] : dark[(b * size) + a];
                    if (b > 0 && value == color)
                    {
                        run++;
                        if (run == 5)
                        {
                            penalty += 3;
                        }
                        else if (run > 5)
                        {
                            penalty++;
                        }
                    }
                    else
                    {
                        color = value;
                        run = 1;
                    }

                    // 1:1:3:1:1 with four light modules on one side: 10111010000 or 00001011101, light beyond the edge.
                    pattern = ((pattern << 1) & 0x7FF) | (value ? 1 : 0);
                    if (b >= 10 && (pattern == 0b10111010000 || pattern == 0b00001011101))
                    {
                        penalty += 40;
                    }
                }

                // Patterns that run into the edge, with the light modules beyond it.
                for (int pad = 1; pad <= 4; pad++)
                {
                    pattern = (pattern << 1) & 0x7FF;
                    if (pattern == 0b10111010000)
                    {
                        penalty += 40;
                    }
                }
            }
        }

        // The left edge: a pattern 1011101 starting at the edge with four light after it, or ending within the first ten modules.
        penalty += EdgePatterns(dark, size);

        int count = 0;
        for (int y = 0; y + 1 < size; y++)
        {
            for (int x = 0; x + 1 < size; x++)
            {
                bool c = dark[(y * size) + x];
                if (c == dark[(y * size) + x + 1] && c == dark[((y + 1) * size) + x] && c == dark[((y + 1) * size) + x + 1])
                {
                    penalty += 3;
                }
            }
        }

        foreach (bool d in dark)
        {
            count += d ? 1 : 0;
        }

        int total = size * size;
        int k = ((Math.Abs((count * 20) - (total * 10)) + total - 1) / total) - 1;
        return penalty + (k * 10);
    }

    /// <summary>Finder-like patterns that start within four modules of the leading edge, the light modules continuing beyond it.</summary>
    private static int EdgePatterns(bool[] dark, int size)
    {
        int penalty = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            for (int a = 0; a < size; a++)
            {
                for (int start = -4; start < 0; start++)
                {
                    // 0000 1011101 where the leading light modules fall partly outside: the window starts at `start`.
                    int pattern = 0;
                    for (int k = 0; k < 11; k++)
                    {
                        int b = start + k;
                        bool value = b >= 0 && b < size && (pass == 0 ? dark[(a * size) + b] : dark[(b * size) + a]);
                        pattern = (pattern << 1) | (value ? 1 : 0);
                    }

                    if (pattern == 0b00001011101)
                    {
                        penalty += 40;
                    }
                }
            }
        }

        return penalty;
    }
}
