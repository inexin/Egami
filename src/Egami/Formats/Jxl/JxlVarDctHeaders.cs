namespace Egami.Formats.Jxl;

/// <summary>The varblock transforms (I.2.3, DctSelect), in their coded order.</summary>
internal enum JxlTransformType
{
    Dct8 = 0,
    Hornuss,
    Dct2,
    Dct4,
    Dct16,
    Dct32,
    Dct16x8,
    Dct8x16,
    Dct32x8,
    Dct8x32,
    Dct32x16,
    Dct16x32,
    Dct4x8,
    Dct8x4,
    Afv0,
    Afv1,
    Afv2,
    Afv3,
    Dct64,
    Dct64x32,
    Dct32x64,
    Dct128,
    Dct128x64,
    Dct64x128,
    Dct256,
    Dct256x128,
    Dct128x256,
}

/// <summary>Facts about each varblock transform (I.2.3), from jxl-oxide's jxl-vardct.</summary>
internal static class JxlTransforms8
{
    /// <summary>Gets the varblock's width and height in 8×8 blocks.</summary>
    internal static (int W, int H) Size(JxlTransformType t) => t switch
    {
        JxlTransformType.Dct16 => (2, 2),
        JxlTransformType.Dct32 => (4, 4),
        JxlTransformType.Dct16x8 => (1, 2),
        JxlTransformType.Dct8x16 => (2, 1),
        JxlTransformType.Dct32x8 => (1, 4),
        JxlTransformType.Dct8x32 => (4, 1),
        JxlTransformType.Dct32x16 => (2, 4),
        JxlTransformType.Dct16x32 => (4, 2),
        JxlTransformType.Dct64 => (8, 8),
        JxlTransformType.Dct64x32 => (4, 8),
        JxlTransformType.Dct32x64 => (8, 4),
        JxlTransformType.Dct128 => (16, 16),
        JxlTransformType.Dct128x64 => (8, 16),
        JxlTransformType.Dct64x128 => (16, 8),
        JxlTransformType.Dct256 => (32, 32),
        JxlTransformType.Dct256x128 => (16, 32),
        JxlTransformType.Dct128x256 => (32, 16),
        _ => (1, 1),
    };

    /// <summary>Gets the dequantization matrix set index (0 to 16).</summary>
    internal static int MatrixIndex(JxlTransformType t) => t switch
    {
        JxlTransformType.Dct8 => 0,
        JxlTransformType.Hornuss => 1,
        JxlTransformType.Dct2 => 2,
        JxlTransformType.Dct4 => 3,
        JxlTransformType.Dct16 => 4,
        JxlTransformType.Dct32 => 5,
        JxlTransformType.Dct16x8 or JxlTransformType.Dct8x16 => 6,
        JxlTransformType.Dct32x8 or JxlTransformType.Dct8x32 => 7,
        JxlTransformType.Dct32x16 or JxlTransformType.Dct16x32 => 8,
        JxlTransformType.Dct4x8 or JxlTransformType.Dct8x4 => 9,
        JxlTransformType.Afv0 or JxlTransformType.Afv1 or JxlTransformType.Afv2 or JxlTransformType.Afv3 => 10,
        JxlTransformType.Dct64 => 11,
        JxlTransformType.Dct64x32 or JxlTransformType.Dct32x64 => 12,
        JxlTransformType.Dct128 => 13,
        JxlTransformType.Dct128x64 or JxlTransformType.Dct64x128 => 14,
        JxlTransformType.Dct256 => 15,
        _ => 16,
    };

    /// <summary>Gets the size of a matrix set entry's matrix: width (the longer side) and height.</summary>
    internal static (int W, int H) MatrixSize(int index) => index switch
    {
        4 => (16, 16),
        5 => (32, 32),
        6 => (16, 8),
        7 => (32, 8),
        8 => (32, 16),
        11 => (64, 64),
        12 => (64, 32),
        13 => (128, 128),
        14 => (128, 64),
        15 => (256, 256),
        16 => (256, 128),
        _ => (8, 8),
    };

    /// <summary>Gets the coefficient order index (0 to 12).</summary>
    internal static int OrderId(JxlTransformType t) => t switch
    {
        JxlTransformType.Dct8 => 0,
        JxlTransformType.Dct16 => 2,
        JxlTransformType.Dct32 => 3,
        JxlTransformType.Dct16x8 or JxlTransformType.Dct8x16 => 4,
        JxlTransformType.Dct32x8 or JxlTransformType.Dct8x32 => 5,
        JxlTransformType.Dct32x16 or JxlTransformType.Dct16x32 => 6,
        JxlTransformType.Dct64 => 7,
        JxlTransformType.Dct64x32 or JxlTransformType.Dct32x64 => 8,
        JxlTransformType.Dct128 => 9,
        JxlTransformType.Dct128x64 or JxlTransformType.Dct64x128 => 10,
        JxlTransformType.Dct256 => 11,
        JxlTransformType.Dct256x128 or JxlTransformType.Dct128x256 => 12,
        _ => 1,
    };

    /// <summary>Gets whether a varblock stores its coefficients transposed (taller than wide, or square, DCTs).</summary>
    internal static bool Transposed(JxlTransformType t)
    {
        if (t is JxlTransformType.Hornuss or JxlTransformType.Dct2 or JxlTransformType.Dct4 or JxlTransformType.Dct4x8 or JxlTransformType.Dct8x4
            or JxlTransformType.Afv0 or JxlTransformType.Afv1 or JxlTransformType.Afv2 or JxlTransformType.Afv3)
        {
            return false;
        }

        (int w, int h) = Size(t);
        return h >= w;
    }

    /// <summary>Gets whether the transform covers a single 8×8 block with its own sub-transforms (no LF DCT).</summary>
    internal static bool IsSmall(JxlTransformType t) => t is JxlTransformType.Hornuss or JxlTransformType.Dct2 or JxlTransformType.Dct4
        or JxlTransformType.Dct8x4 or JxlTransformType.Dct4x8 or JxlTransformType.Dct8 or JxlTransformType.Afv0 or JxlTransformType.Afv1
        or JxlTransformType.Afv2 or JxlTransformType.Afv3;
}

/// <summary>The VarDCT part of LF global (G.1.2 to G.1.4): quantizer, HF block context and LF channel correlation.</summary>
internal sealed class JxlVarDctGlobal
{
    internal uint GlobalScale { get; private init; }

    internal uint QuantLf { get; private init; }

    internal int[] QfThresholds { get; private init; } = [];

    internal int[][] LfThresholds { get; private init; } = [[], [], []];

    internal byte[] BlockContextMap { get; private init; } = [];

    internal int BlockClusters { get; private init; }

    internal uint ColourFactor { get; private init; } = 84;

    internal float BaseCorrelationX { get; private init; }

    internal float BaseCorrelationB { get; private init; } = 1;

    internal int XFactorLf { get; private init; } = 128;

    internal int BFactorLf { get; private init; } = 128;

    internal static JxlVarDctGlobal Read(JxlBitReader r)
    {
        uint globalScale = r.ReadU32(new JxlU32(1, 11, 2049, 11, 4097, 12, 8193, 16));
        uint quantLf = r.ReadU32(new JxlU32(16, 0, 1, 5, 1, 8, 1, 16));
        int[] qf = [];
        int[][] lf = [[], [], []];
        byte[] map;
        int clusters;
        if (r.ReadBool())
        {
            clusters = 15;
            map = [0, 1, 2, 2, 3, 3, 4, 5, 6, 6, 6, 6, 6, 7, 8, 9, 9, 10, 11, 12, 13, 14, 14, 14, 14, 14, 7, 8, 9, 9, 10, 11, 12, 13, 14, 14, 14, 14, 14];
        }
        else
        {
            int size = 1;
            for (int c = 0; c < 3; c++)
            {
                int count = (int)r.ReadBits(4);
                size *= count + 1;
                lf[c] = new int[count];
                for (int i = 0; i < count; i++)
                {
                    lf[c][i] = JxlHeaders.UnpackSigned(r.ReadU32(new JxlU32(0, 4, 16, 8, 272, 16, 65808, 32)));
                }
            }

            int qfCount = (int)r.ReadBits(4);
            size *= qfCount + 1;
            qf = new int[qfCount];
            for (int i = 0; i < qfCount; i++)
            {
                qf[i] = 1 + (int)r.ReadU32(new JxlU32(0, 2, 4, 3, 12, 5, 44, 8));
            }

            map = JxlEntropyDecoder.ReadContextMap(r, size * 39, out clusters);
        }

        JxlVarDctGlobal g = new() { GlobalScale = globalScale, QuantLf = quantLf, QfThresholds = qf, LfThresholds = lf, BlockContextMap = map, BlockClusters = clusters };
        if (r.ReadBool())
        {
            return g;
        }

        return new JxlVarDctGlobal
        {
            GlobalScale = globalScale,
            QuantLf = quantLf,
            QfThresholds = qf,
            LfThresholds = lf,
            BlockContextMap = map,
            BlockClusters = clusters,
            ColourFactor = r.ReadU32(new JxlU32(84, 0, 256, 0, 2, 8, 258, 16)),
            BaseCorrelationX = r.ReadF16(),
            BaseCorrelationB = r.ReadF16(),
            XFactorLf = (int)r.ReadBits(8),
            BFactorLf = (int)r.ReadBits(8),
        };
    }
}

/// <summary>A coefficient order and the HF distributions of one pass (I.3).</summary>
internal sealed class JxlHfPass
{
    private static readonly (int W, int H)[] BlockSizes = [(8, 8), (8, 8), (16, 16), (32, 32), (16, 8), (32, 8), (32, 16), (64, 64), (64, 32), (128, 128), (128, 64), (256, 256), (256, 128)];
    private static readonly int[][] NaturalOrders = new int[13][];

    private readonly int[][] _orders = new int[13 * 3][];

    private JxlHfPass(JxlEntropyDecoder decoder)
    {
        Decoder = decoder;
    }

    internal JxlEntropyDecoder Decoder { get; }

    internal static JxlHfPass Read(JxlBitReader r, int presets, int blockClusters)
    {
        uint used = r.ReadU32(new JxlU32(0x5F, 0, 0x13, 0, 0, 0, 0, 13));
        int[][] orders = new int[13 * 3][];
        if (used != 0)
        {
            JxlEntropyDecoder decoder = JxlEntropyDecoder.Read(r, 8);
            decoder.Begin(r);
            for (int o = 0; o < 13; o++)
            {
                if ((used >> o & 1) == 0)
                {
                    continue;
                }

                (int bw, int bh) = BlockSizes[o];
                int size = bw * bh;
                int[] natural = Natural(o);
                for (int c = 0; c < 3; c++)
                {
                    int[] permutation = decoder.ReadPermutation(r, size, size / 64);
                    int[] order = new int[size];
                    for (int i = 0; i < size; i++)
                    {
                        order[i] = natural[permutation[i]];
                    }

                    orders[(o * 3) + c] = order;
                }
            }

            decoder.Finish(r);
        }

        JxlHfPass pass = new(JxlEntropyDecoder.Read(r, 495 * presets * blockClusters));
        orders.CopyTo(pass._orders, 0);
        return pass;
    }

    /// <summary>
    /// Gets the coefficient order for an order id and channel: positions packed as y * W + x within the wide (W ≥ H) layout of
    /// <see cref="BlockSizes"/>.
    /// </summary>
    internal int[] Order(int orderId, int channel) => _orders[(orderId * 3) + channel] ?? Natural(orderId);

    internal static int OrderWidth(int orderId) => BlockSizes[orderId].W;

    /// <summary>The natural coefficient order (I.3.2): the LLF block row by row, then diagonals outwards.</summary>
    internal static int[] Natural(int orderId)
    {
        int[]? cached = Volatile.Read(ref NaturalOrders[orderId]);
        if (cached is not null)
        {
            return cached;
        }

        (int bw, int bh) = BlockSizes[orderId];
        int yScale = bw / bh;
        int[] order = new int[bw * bh];
        int lbw = bw / 8;
        int lbh = bh / 8;
        int i = 0;
        for (; i < lbw * lbh; i++)
        {
            order[i] = ((i / lbw) * bw) + (i % lbw);
        }

        for (int dist = 1; dist < 2 * bw; dist++)
        {
            int margin = Math.Max(0, dist - bw);
            for (int k = margin; k < dist - margin; k++)
            {
                (int x, int y) = dist % 2 == 1 ? (k, dist - 1 - k) : (dist - 1 - k, k);
                if (x < lbw && y < lbw)
                {
                    continue;
                }

                if (y % yScale != 0)
                {
                    continue;
                }

                order[i++] = ((y / yScale) * bw) + x;
            }
        }

        Volatile.Write(ref NaturalOrders[orderId], order);
        return order;
    }
}

/// <summary>HF global (I.2.4 and I.3): the dequantization matrices, the HF presets and each pass's orders and distributions.</summary>
internal sealed class JxlHfGlobal
{
    internal required JxlDequantMatrices Matrices { get; init; }

    internal required int Presets { get; init; }

    internal required JxlHfPass[] Passes { get; init; }
}
