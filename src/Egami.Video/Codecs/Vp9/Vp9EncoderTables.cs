using System.Runtime.CompilerServices;

namespace Egami.Video.Codecs.Vp9;

/// <summary>
/// What the VP9 encoder adds to <see cref="Vp9Tables"/>: the cost of a coded bit (libvpx 1.14.1's vp9_prob_cost, vp9/encoder
/// /vp9_cost.c, BSD-3; see THIRD-PARTY-NOTICES), each tree's codes, and the token of each coefficient value.
/// </summary>
internal static class Vp9EncoderTables
{
    /// <summary>The cost shift: costs are in 1/512 bits.</summary>
    public const int CostShift = 9;

    /// <summary>round(-log2(i / 256) * 512), with a bogus first entry (vp9_prob_cost).</summary>
    public static ReadOnlySpan<ushort> ProbCost =>
    [
        4096, 4096, 3584, 3284, 3072, 2907, 2772, 2659, 2560, 2473, 2395, 2325, 2260, 2201, 2147, 2096, 2048, 2003, 1961, 1921,
        1883, 1847, 1813, 1780, 1748, 1718, 1689, 1661, 1635, 1609, 1584, 1559, 1536, 1513, 1491, 1470, 1449, 1429, 1409, 1390,
        1371, 1353, 1335, 1318, 1301, 1284, 1268, 1252, 1236, 1221, 1206, 1192, 1177, 1163, 1149, 1136, 1123, 1110, 1097, 1084,
        1072, 1059, 1047, 1036, 1024, 1013, 1001, 990, 979, 968, 958, 947, 937, 927, 917, 907, 897, 887, 878, 868, 859, 850, 841,
        832, 823, 814, 806, 797, 789, 780, 772, 764, 756, 748, 740, 732, 724, 717, 709, 702, 694, 687, 680, 673, 665, 658, 651,
        644, 637, 631, 624, 617, 611, 604, 598, 591, 585, 578, 572, 566, 560, 554, 547, 541, 535, 530, 524, 518, 512, 506, 501,
        495, 489, 484, 478, 473, 467, 462, 456, 451, 446, 441, 435, 430, 425, 420, 415, 410, 405, 400, 395, 390, 385, 380, 375,
        371, 366, 361, 356, 352, 347, 343, 338, 333, 329, 324, 320, 316, 311, 307, 302, 298, 294, 289, 285, 281, 277, 273, 268,
        264, 260, 256, 252, 248, 244, 240, 236, 232, 228, 224, 220, 216, 212, 209, 205, 201, 197, 194, 190, 186, 182, 179, 175,
        171, 168, 164, 161, 157, 153, 150, 146, 143, 139, 136, 132, 129, 125, 122, 119, 115, 112, 109, 105, 102, 99, 95, 92, 89,
        86, 82, 79, 76, 73, 70, 66, 63, 60, 57, 54, 51, 48, 45, 42, 38, 35, 32, 29, 26, 23, 20, 18, 15, 12, 9, 6, 3,
    ];

    /// <summary>The cost of coding <paramref name="bit"/> with probability <paramref name="probability"/> of a 0.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Cost(int probability, int bit) => ProbCost[bit != 0 ? 256 - probability : probability];

    /// <summary>The codes of each tree: per symbol, the path's branches (first branch highest) and length.</summary>
    public static readonly TreeCodes IntraMode = new(Vp9Tables.IntraModeTree);

    public static readonly TreeCodes InterMode = new(Vp9Tables.InterModeTree);

    public static readonly TreeCodes Partition = new(Vp9Tables.PartitionTree);

    public static readonly TreeCodes SwitchableInterp = new(Vp9Tables.SwitchableInterpTree);

    public static readonly TreeCodes MvJoint = new(Vp9Tables.MvJointTree);

    public static readonly TreeCodes MvClass = new(Vp9Tables.MvClassTree);

    public static readonly TreeCodes MvClass0 = new(Vp9Tables.MvClass0Tree);

    public static readonly TreeCodes MvFp = new(Vp9Tables.MvFpTree);

    public static readonly TreeCodes Segment = new(Vp9Tables.SegmentTree);

    /// <summary>The probabilities of the extra bits of categories 1 to 5.</summary>
    public static ReadOnlySpan<byte> Cat1Prob => [159];

    public static ReadOnlySpan<byte> Cat2Prob => [165, 145];

    public static ReadOnlySpan<byte> Cat3Prob => [173, 148, 140];

    public static ReadOnlySpan<byte> Cat4Prob => [176, 155, 140, 135];

    public static ReadOnlySpan<byte> Cat5Prob => [180, 157, 141, 134, 130];

    /// <summary>The smallest value of each token: ZERO, ONE, TWO, THREE, FOUR, CAT1 to CAT6.</summary>
    public static ReadOnlySpan<short> TokenBase => [0, 1, 2, 3, 4, 5, 7, 11, 19, 35, 67];

    /// <summary>The extra bits of each token (CAT6's at 8 bit).</summary>
    public static ReadOnlySpan<byte> TokenExtraBits => [0, 0, 0, 0, 0, 1, 2, 3, 4, 5, 14];

    /// <summary>The token (0 ZERO to 10 CAT6) of a coefficient's magnitude.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Token(int magnitude) => magnitude < 5 ? magnitude : magnitude < 7 ? 5 : magnitude < 11 ? 6 : magnitude < 19 ? 7 :
        magnitude < 35 ? 8 : magnitude < 67 ? 9 : 10;

    /// <summary>The token cache class of a token (vp9_pt_energy_class): what the next coefficients' contexts see.</summary>
    public static ReadOnlySpan<byte> EnergyClass => [0, 1, 2, 3, 3, 4, 4, 5, 5, 5, 5, 5];

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<short[], short[]> InverseScans = new(ReferenceEqualityComparer.Instance);

    /// <summary>Each raster position's place in a scan order (libvpx's iscan).</summary>
    public static ReadOnlySpan<short> InverseScan(Vp9Scan scan) => InverseScans.GetOrAdd(scan.OrderArray, order =>
    {
        short[] inverse = new short[order.Length];
        for (int i = 0; i < order.Length; i++)
        {
            inverse[order[i]] = (short)i;
        }

        return inverse;
    });

    /// <summary>The probabilities of category <paramref name="token"/>'s extra bits at a bit depth.</summary>
    public static ReadOnlySpan<byte> CatProbs(int token, int bitDepth) => token switch
    {
        5 => Cat1Prob,
        6 => Cat2Prob,
        7 => Cat3Prob,
        8 => Cat4Prob,
        9 => Cat5Prob,
        _ => bitDepth == 12 ? Vp9Tables.Cat6ProbHigh12 : bitDepth == 10 ? Vp9Tables.Cat6ProbHigh12.AsSpan(2) : Vp9Tables.Cat6Prob,
    };

    /// <summary>The extra bits of category <paramref name="token"/> at a bit depth.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ExtraBits(int token, int bitDepth) => token == 10 ? 14 + (bitDepth - 8) : TokenExtraBits[token];
}

/// <summary>A tree's code for each symbol: the branches from the root (first branch in the highest bit) and their count.</summary>
internal sealed class TreeCodes
{
    private readonly int[] _bits;
    private readonly int[] _lengths;

    public TreeCodes(ReadOnlySpan<sbyte> tree)
    {
        int symbols = 0;
        foreach (sbyte v in tree)
        {
            symbols = Math.Max(symbols, -v + 1);
        }

        _bits = new int[symbols];
        _lengths = new int[symbols];
        Walk(tree, 0, 0, 0);
        Tree = tree.ToArray();
    }

    /// <summary>Gets the tree.</summary>
    public sbyte[] Tree { get; }

    public int Bits(int symbol) => _bits[symbol];

    public int Length(int symbol) => _lengths[symbol];

    public int Count => _bits.Length;

    /// <summary>The cost of a symbol with these probabilities.</summary>
    public int Cost(ReadOnlySpan<byte> probabilities, int symbol)
    {
        int bits = _bits[symbol], length = _lengths[symbol], cost = 0, i = 0;
        do
        {
            int bit = (bits >> --length) & 1;
            cost += Vp9EncoderTables.Cost(probabilities[i >> 1], bit);
            i = Tree[i + bit];
        }
        while (length != 0);

        return cost;
    }

    /// <summary>Fills <paramref name="costs"/> with every symbol's cost.</summary>
    public void Costs(ReadOnlySpan<byte> probabilities, Span<int> costs)
    {
        for (int s = 0; s < _bits.Length; s++)
        {
            costs[s] = Cost(probabilities, s);
        }
    }

    public void Write(ref Vp9BoolEncoder w, ReadOnlySpan<byte> probabilities, int symbol) =>
        w.WriteTree(Tree, probabilities, _bits[symbol], _lengths[symbol]);

    private void Walk(ReadOnlySpan<sbyte> tree, int i, int bits, int length)
    {
        for (int b = 0; b < 2; b++)
        {
            int next = tree[i + b];
            int code = (bits << 1) | b;
            if (next <= 0)
            {
                _bits[-next] = code;
                _lengths[-next] = length + 1;
            }
            else
            {
                Walk(tree, next, code, length + 1);
            }
        }
    }
}
