namespace Egami.Video.Codecs.H264;

/// <summary>
/// The encoder's forward transforms and quantization, the inverses of the decoder's scaling (8.5.9) and transforms (8.5.12.2,
/// 8.5.13.2). The forward 4x4 core transform is W = Cf X Cf^T with Cf's rows (1, 1, 1, 1), (2, 1, −1, −2), (1, −1, −1, 1),
/// (1, −2, 2, −1); the 8x8 one uses the integer matrix whose eighth is the decoder's inverse. Their rows are orthogonal with
/// squared norms n = (4, 10, 4, 10) and (512, 578, 320, 578, ...), which makes the inverse X = Ci^T (W ∘ d d^T) Ci with
/// d = 1 / (S n), so a level Z the decoder scales by LevelScale reconstructs W when Z = W · MF / 2^qbits with
/// MF = 2^qbits · 64 d_i d_j / (LevelScale · 2^(qP/6)) (written out per table below), and an error e in W costs e² / (n_i n_j)
/// in the samples. Every quantity is an integer or a double of basic operations, so every machine quantizes alike.
/// </summary>
internal static class H264Forward
{
    /// <summary>normAdjust4x4 v[m][0..2] (8-315), as <see cref="H264Dequant"/> has them.</summary>
    private static readonly int[] V4 = [10, 16, 13, 11, 18, 14, 13, 20, 16, 14, 23, 18, 16, 25, 20, 18, 29, 23];

    /// <summary>normAdjust8x8 v[m][0..5] (8-318).</summary>
    private static readonly int[] V8 =
    [
        20, 18, 32, 19, 25, 24, 22, 19, 35, 21, 28, 26, 26, 23, 42, 24, 33, 31, 28, 25, 45, 26, 35, 33, 32, 28, 51, 30, 40, 38,
        36, 32, 58, 34, 46, 43,
    ];

    private static readonly int[] Norm4 = [4, 10, 4, 10];

    private static readonly int[] Norm8 = [512, 578, 320, 578, 512, 578, 320, 578];

    /// <summary>The 8x8 forward matrix: eight times the decoder's inverse (8-338 to 8-353 written as a matrix).</summary>
    private static readonly int[] C8 =
    [
        8, 8, 8, 8, 8, 8, 8, 8,
        12, 10, 6, 3, -3, -6, -10, -12,
        8, 4, -4, -8, -8, -4, 4, 8,
        10, -3, -12, -6, 6, 12, 3, -10,
        8, -8, -8, 8, 8, -8, -8, 8,
        6, -12, 3, 10, -10, -3, 12, -6,
        4, -8, 8, -4, -4, 8, -8, 4,
        3, -6, 10, -12, 12, -10, 6, -3,
    ];

    /// <summary>The fractional bits of a 4x4 quantizer at qP % 6 (qbits = 15 + qP / 6).</summary>
    internal const int Shift4 = 15;

    /// <summary>The fractional bits of an 8x8 quantizer (qbits = 22 + qP / 6): the 8x8 transform's coefficients are 64 times larger.</summary>
    internal const int Shift8 = 22;

    /// <summary>MF for 4x4 blocks, [m * 16 + raster position]: 2^21 d_i d_j / v.</summary>
    internal static readonly int[] Mf4 = MakeMf4();

    /// <summary>MF for 8x8 blocks, [m * 64 + raster position]: 2^30 d_i d_j / v with d = 8 / n.</summary>
    internal static readonly int[] Mf8 = MakeMf8();

    /// <summary>The sample-domain weight of a squared error in each 4x4 coefficient, 1 / (n_i n_j).</summary>
    internal static readonly double[] Weight4 = [.. Enumerable.Range(0, 16).Select(p => 1.0 / (Norm4[p >> 2] * Norm4[p & 3]))];

    /// <summary>The sample-domain weight of a squared error in each 8x8 coefficient.</summary>
    internal static readonly double[] Weight8 = [.. Enumerable.Range(0, 64).Select(p => 1.0 / ((double)Norm8[p >> 3] * Norm8[p & 7]))];

    private static int V4At(int m, int pos)
    {
        int i = pos >> 2, j = pos & 3;
        return (i & 1) == 0 && (j & 1) == 0 ? V4[m * 3] : (i & 1) == 1 && (j & 1) == 1 ? V4[(m * 3) + 1] : V4[(m * 3) + 2];
    }

    private static int V8At(int m, int pos)
    {
        int i = pos >> 3, j = pos & 7;
        int k = (i % 4 == 0 && j % 4 == 0) ? 0
            : (i % 2 == 1 && j % 2 == 1) ? 1
            : (i % 4 == 2 && j % 4 == 2) ? 2
            : ((i % 4 == 0 && j % 2 == 1) || (i % 2 == 1 && j % 4 == 0)) ? 3
            : ((i % 4 == 0 && j % 4 == 2) || (i % 4 == 2 && j % 4 == 0)) ? 4 : 5;
        return V8[(m * 6) + k];
    }

    private static int[] MakeMf4()
    {
        // d = (1/4, 1/5, 1/4, 1/5): Z = 64 W d_i d_j / (v 2^(qP/6)), so MF = 2^21 d_i d_j / v.
        int[] d = [5, 4, 5, 4]; // 1 / d scaled: d_i = 1 / (4 or 5) = (5 or 4) / 20
        int[] table = new int[6 * 16];
        for (int m = 0; m < 6; m++)
        {
            for (int pos = 0; pos < 16; pos++)
            {
                long num = (1L << 21) * d[pos >> 2] * d[pos & 3];
                long den = 400L * V4At(m, pos);
                table[(m * 16) + pos] = (int)((num + (den / 2)) / den);
            }
        }

        return table;
    }

    private static int[] MakeMf8()
    {
        // Z = 256 W d_i d_j / (v 2^(qP/6)) with d = 8 / n; at qbits 22, MF = 2^30 · 64 / (n_i n_j v).
        int[] table = new int[6 * 64];
        for (int m = 0; m < 6; m++)
        {
            for (int pos = 0; pos < 64; pos++)
            {
                long num = (1L << 30) * 64;
                long den = (long)Norm8[pos >> 3] * Norm8[pos & 7] * V8At(m, pos);
                table[(m * 64) + pos] = (int)((num + (den / 2)) / den);
            }
        }

        return table;
    }

    /// <summary>The forward 4x4 core transform of a residual (raster order) in place.</summary>
    internal static void Transform4x4(Span<int> block)
    {
        Span<int> t = stackalloc int[16];
        for (int i = 0; i < 4; i++)
        {
            int a = block[(i * 4) + 0], b = block[(i * 4) + 1], c = block[(i * 4) + 2], d = block[(i * 4) + 3];
            int s03 = a + d, d03 = a - d, s12 = b + c, d12 = b - c;
            t[(i * 4) + 0] = s03 + s12;
            t[(i * 4) + 1] = (2 * d03) + d12;
            t[(i * 4) + 2] = s03 - s12;
            t[(i * 4) + 3] = d03 - (2 * d12);
        }

        for (int j = 0; j < 4; j++)
        {
            int a = t[j], b = t[4 + j], c = t[8 + j], d = t[12 + j];
            int s03 = a + d, d03 = a - d, s12 = b + c, d12 = b - c;
            block[j] = s03 + s12;
            block[4 + j] = (2 * d03) + d12;
            block[8 + j] = s03 - s12;
            block[12 + j] = d03 - (2 * d12);
        }
    }

    /// <summary>The forward 8x8 transform of a residual (raster order) in place.</summary>
    internal static void Transform8x8(Span<int> block)
    {
        Span<int> t = stackalloc int[64];
        ReadOnlySpan<int> c8 = C8;
        for (int i = 0; i < 8; i++)
        {
            for (int k = 0; k < 8; k++)
            {
                int sum = 0;
                for (int j = 0; j < 8; j++)
                {
                    sum += block[(i * 8) + j] * c8[(k * 8) + j];
                }

                t[(i * 8) + k] = sum;
            }
        }

        for (int j = 0; j < 8; j++)
        {
            for (int k = 0; k < 8; k++)
            {
                int sum = 0;
                for (int i = 0; i < 8; i++)
                {
                    sum += t[(i * 8) + j] * c8[(k * 8) + i];
                }

                block[(k * 8) + j] = sum;
            }
        }
    }

    /// <summary>The 4x4 Hadamard of the Intra16x16 DC coefficients (raster order, row i column j is block (j, i)) in place.</summary>
    internal static void Hadamard4x4(Span<int> c)
    {
        Span<int> t = stackalloc int[16];
        for (int i = 0; i < 4; i++)
        {
            int a = c[(i * 4) + 0], b = c[(i * 4) + 1], d = c[(i * 4) + 2], e = c[(i * 4) + 3];
            t[(i * 4) + 0] = a + b + d + e;
            t[(i * 4) + 1] = a + b - d - e;
            t[(i * 4) + 2] = a - b - d + e;
            t[(i * 4) + 3] = a - b + d - e;
        }

        for (int j = 0; j < 4; j++)
        {
            int a = t[j], b = t[4 + j], d = t[8 + j], e = t[12 + j];
            c[j] = a + b + d + e;
            c[4 + j] = a + b - d - e;
            c[8 + j] = a - b - d + e;
            c[12 + j] = a - b + d - e;
        }
    }

    /// <summary>
    /// Quantizes a value to a level with a rounding offset (a fraction of a level in 1/256: 85 for a third, as for intra blocks;
    /// 43 for a sixth, as for inter blocks).
    /// </summary>
    internal static int Quantize(long value, long mf, int qbits, int round256)
    {
        long a = Math.Abs(value) * mf;
        int level = (int)((a + ((long)round256 << (qbits - 8))) >> qbits);
        return value < 0 ? -level : level;
    }

    /// <summary>The step of a level in W units at a raster position of a 4x4 block: 2^qbits / MF.</summary>
    internal static double Step4(int qp, int pos) => Math.ScaleB(1.0, Shift4 + (qp / 6)) / Mf4[((qp % 6) * 16) + pos];

    /// <summary>The step of a level in W units at a raster position of an 8x8 block.</summary>
    internal static double Step8(int qp, int pos) => Math.ScaleB(1.0, Shift8 + (qp / 6)) / Mf8[((qp % 6) * 64) + pos];

    /// <summary>The Lagrange multiplier of squared sample error against bits at a QP (the H.264 reference model's 0.85 · 2^((QP − 12) / 3)).</summary>
    /// <remarks>2^(1/3) and 2^(2/3) are constants and the rest a power of two, so every machine gets the same value.</remarks>
    internal static double Lambda(int qp)
    {
        int e = qp - 12;
        int whole = (int)Math.Floor(e / 3.0), part = e - (whole * 3);
        double root = part switch { 0 => 1.0, 1 => 1.2599210498948732, _ => 1.5874010519681994 };
        return Math.ScaleB(0.85 * root, whole);
    }
}
