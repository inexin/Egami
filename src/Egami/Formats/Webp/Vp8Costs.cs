using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Egami.Formats.Webp;

/// <summary>
/// Bit costs for the VP8 encoder's rate estimates, in 1/256 bit: the cost of a boolean at a probability, of each prediction
/// mode (from the fixed mode probabilities of RFC 6386), and of coefficient levels given the token probabilities.
/// </summary>
internal static class Vp8Costs
{
    /// <summary>The highest level whose token-tree cost depends on the level; larger levels share it (category 6).</summary>
    internal const int MaxVariableLevel = 67;

    /// <summary>The largest coefficient level the format codes.</summary>
    internal const int MaxLevel = 2047;

    /// <summary>The number of token probabilities in a frame: 4 types × 8 bands × 3 contexts × 11 nodes.</summary>
    internal const int ProbabilityCount = 4 * 8 * 3 * 11;

    /// <summary>Gets −log2(p / 256) × 256 for each probability p (the cost of a 0 bit), with p = 0 costed as 1.</summary>
    internal static readonly ushort[] Entropy = BuildEntropy();

    /// <summary>Gets the cost of each level that does not depend on the probabilities: the sign and the category's extra bits.</summary>
    internal static readonly ushort[] LevelFixed = BuildLevelFixed();

    /// <summary>Gets the cost of each 16 × 16 mode (DC, vertical, horizontal, TrueMotion), the 16 × 16 flag included.</summary>
    internal static readonly ushort[] Intra16 = new ushort[4];

    /// <summary>Gets the cost of each chroma mode (DC, vertical, horizontal, TrueMotion).</summary>
    internal static readonly ushort[] Chroma = new ushort[4];

    /// <summary>Gets the cost of each 4 × 4 mode given the modes above and to the left, [above × 100 + left × 10 + mode].</summary>
    internal static readonly ushort[] Intra4 = new ushort[1000];

    /// <summary>Gets the path of each 4 × 4 mode through the mode tree: (node, bit) pairs, node being the probability index.</summary>
    internal static readonly (int Node, int Bit)[][] Intra4Paths = new (int, int)[10][];

    /// <summary>Gets the cost of the 4 × 4 flag (a 0 at probability 145).</summary>
    internal static readonly int Intra4Flag;

    static Vp8Costs()
    {
        for (int m = 0; m < 4; m++)
        {
            Intra16[m] = (ushort)(Bit(1, 145) + (m >= 2 ? Bit(1, 156) + Bit(m == 3 ? 1 : 0, 128) : Bit(0, 156) + Bit(m == 1 ? 1 : 0, 163)));
            Chroma[m] = (ushort)(Bit(m != 0 ? 1 : 0, 142) + (m == 0 ? 0 : Bit(m != 1 ? 1 : 0, 114) + (m == 1 ? 0 : Bit(m == 3 ? 1 : 0, 183))));
        }

        Intra4Flag = Bit(0, 145);
        for (int m = 0; m < 10; m++)
        {
            List<(int, int)> path = [];
            FindPath(0, m, path);
            Intra4Paths[m] = [.. path];
        }

        ReadOnlySpan<byte> probs = Vp8Tables.BModeProbs;
        for (int above = 0; above < 10; above++)
        {
            for (int left = 0; left < 10; left++)
            {
                int at = ((above * 10) + left) * 9;
                for (int m = 0; m < 10; m++)
                {
                    int cost = 0;
                    foreach ((int node, int bit) in Intra4Paths[m])
                    {
                        cost += Bit(bit, probs[at + node]);
                    }

                    Intra4[(above * 100) + (left * 10) + m] = (ushort)cost;
                }
            }
        }
    }

    /// <summary>The cost of <paramref name="bit"/> coded at probability <paramref name="probability"/> / 256 of a 0.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Bit(int bit, int probability) => Entropy[bit != 0 ? 255 - probability : probability];

    /// <summary>The cost of a level after the token-tree part looked up in <paramref name="table"/> at <paramref name="at"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Level(ushort[] table, int at, int level) => LevelFixed[level] + table[at + Math.Min(level, MaxVariableLevel)];

    /// <summary>The cost of the token-tree nodes from node 2 on for a level of 1 to <see cref="MaxVariableLevel"/>.</summary>
    internal static int VariableLevel(int v, ReadOnlySpan<byte> p)
    {
        if (v == 1)
        {
            return Bit(0, p[2]);
        }

        int cost = Bit(1, p[2]);
        if (v <= 4)
        {
            return cost + Bit(0, p[3]) + (v == 2 ? Bit(0, p[4]) : Bit(1, p[4]) + Bit(v == 4 ? 1 : 0, p[5]));
        }

        if (v <= 10)
        {
            return cost + Bit(1, p[3]) + Bit(0, p[6]) + Bit(v > 6 ? 1 : 0, p[7]);
        }

        cost += Bit(1, p[3]) + Bit(1, p[6]);
        return cost + (v < 19 ? Bit(0, p[8]) + Bit(0, p[9])
            : v < 35 ? Bit(0, p[8]) + Bit(1, p[9])
            : v < 67 ? Bit(1, p[8]) + Bit(0, p[10])
            : Bit(1, p[8]) + Bit(1, p[10]));
    }

    private static bool FindPath(int node, int mode, List<(int, int)> path)
    {
        ReadOnlySpan<sbyte> tree = Vp8Decoder.BModeTree;
        for (int bit = 0; bit < 2; bit++)
        {
            int child = tree[node + bit];
            path.Add((node >> 1, bit));
            if (child <= 0 ? -child == mode : FindPath(child, mode, path))
            {
                return true;
            }

            path.RemoveAt(path.Count - 1);
        }

        return false;
    }

    private static ushort[] BuildEntropy()
    {
        ushort[] table = new ushort[256];
        for (int p = 0; p < 256; p++)
        {
            table[p] = (ushort)Math.Round(-Math.Log2(Math.Max(p, 1) / 256.0) * 256);
        }

        return table;
    }

    private static ushort[] BuildLevelFixed()
    {
        ushort[] table = new ushort[MaxLevel + 1];
        for (int v = 1; v <= MaxLevel; v++)
        {
            int cost = 256;
            if (v >= 5 && v <= 6)
            {
                cost += Bit(v == 6 ? 1 : 0, 159);
            }
            else if (v >= 7 && v <= 10)
            {
                cost += Bit(v >= 9 ? 1 : 0, 165) + Bit((v & 1) == 0 ? 1 : 0, 145);
            }
            else if (v >= 11)
            {
                ReadOnlySpan<byte> cat = v < 19 ? Vp8Decoder.Cat3 : v < 35 ? Vp8Decoder.Cat4 : v < 67 ? Vp8Decoder.Cat5 : Vp8Decoder.Cat6;
                int start = v < 19 ? 11 : v < 35 ? 19 : v < 67 ? 35 : 67;
                int extra = v - start;
                for (int i = 0; i < cat.Length; i++)
                {
                    cost += Bit((extra >> (cat.Length - 1 - i)) & 1, cat[i]);
                }
            }

            table[v] = (ushort)cost;
        }

        return table;
    }
}

/// <summary>
/// The level costs for one set of token probabilities: for each type, coefficient position and context, the cost of each level
/// 0 to <see cref="Vp8Costs.MaxVariableLevel"/>, with the not-the-end bit included where the format codes one.
/// </summary>
internal sealed class Vp8LevelCosts
{
    /// <summary>The entries of one table.</summary>
    internal const int Levels = Vp8Costs.MaxVariableLevel + 1;

    /// <summary>Gets the tables, [((type × 16 + position) × 3 + context) × <see cref="Levels"/> + level].</summary>
    internal ushort[] Table { get; } = new ushort[4 * 16 * 3 * Levels];

    /// <summary>Gets the probabilities the tables were computed from.</summary>
    internal byte[] Probabilities { get; } = new byte[Vp8Costs.ProbabilityCount];

    /// <summary>Recomputes the tables from <paramref name="probabilities"/>.</summary>
    internal void Update(ReadOnlySpan<byte> probabilities)
    {
        probabilities.CopyTo(Probabilities);
        Span<ushort> band = stackalloc ushort[8 * 3 * Levels];
        for (int type = 0; type < 4; type++)
        {
            for (int b = 0; b < 8; b++)
            {
                for (int ctx = 0; ctx < 3; ctx++)
                {
                    ReadOnlySpan<byte> p = probabilities.Slice((((type * 8) + b) * 3 + ctx) * 11, 11);
                    Span<ushort> t = band.Slice(((b * 3) + ctx) * Levels, Levels);
                    int cost0 = ctx > 0 ? Vp8Costs.Bit(1, p[0]) : 0;
                    int costBase = Vp8Costs.Bit(1, p[1]) + cost0;
                    t[0] = (ushort)(Vp8Costs.Bit(0, p[1]) + cost0);
                    for (int v = 1; v < Levels; v++)
                    {
                        t[v] = (ushort)(costBase + Vp8Costs.VariableLevel(v, p));
                    }
                }
            }

            for (int n = 0; n < 16; n++)
            {
                band.Slice(Vp8Decoder.Bands[n] * 3 * Levels, 3 * Levels).CopyTo(Table.AsSpan(At(type, n, 0), 3 * Levels));
            }
        }

        for (int i = 0; i < Table.Length; i++)
        {
            _withFixed[i] = (ushort)(Table[i] + Vp8Costs.LevelFixed[i % Levels]);
        }
    }

    // Table plus each level's fixed cost, for levels up to MaxVariableLevel; one entry of padding lets the last be read as 32 bits.
    private readonly ushort[] _withFixed = new ushort[(4 * 16 * 3 * Levels) + 1];

    // The start of each position's tables within one type, positions 0 to 7 and 8 to 15.
    private static readonly Vector256<int> PositionsLow = Vector256.Create(0, 1, 2, 3, 4, 5, 6, 7) * (3 * Levels);
    private static readonly Vector256<int> PositionsHigh = Vector256.Create(8, 9, 10, 11, 12, 13, 14, 15) * (3 * Levels);

    /// <summary>The index of the table for <paramref name="type"/>, coefficient position <paramref name="n"/> and <paramref name="ctx"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int At(int type, int n, int ctx) => (((type * 16) + n) * 3 + ctx) * Levels;

    /// <summary>
    /// The cost of one block's levels (zigzag order, from <paramref name="first"/>) with the context <paramref name="ctx0"/> from
    /// its neighbours.
    /// </summary>
    internal int Residual(int type, int first, int ctx0, ReadOnlySpan<short> levels)
    {
        int last = LastNonZero(levels, first);
        int p0 = Probabilities[((type * 8) + Vp8Decoder.Bands[first]) * 33 + (ctx0 * 11)];
        if (last < 0)
        {
            return Vp8Costs.Bit(0, p0);
        }

        // Levels past MaxLevel never occur: quantization clamps them.
        ref ushort table = ref MemoryMarshal.GetArrayDataReference(Table);
        ref ushort levelFixed = ref MemoryMarshal.GetArrayDataReference(Vp8Costs.LevelFixed);
        ref short level = ref MemoryMarshal.GetReference(levels);
        int cost = ctx0 == 0 ? Vp8Costs.Bit(1, p0) : 0;
        int n = first;
        if (Avx2.IsSupported)
        {
            cost += Levels16(type, first, ctx0, last, levels);
            n = last;
        }
        else
        {
            int position = At(type, first, 0);
            int at = position + (ctx0 * Levels);
            for (; n < last; n++)
            {
                int v = Math.Abs((int)Unsafe.Add(ref level, n));
                cost += Unsafe.Add(ref levelFixed, v) + Unsafe.Add(ref table, at + Math.Min(v, Vp8Costs.MaxVariableLevel));
                position += 3 * Levels;
                at = position + (Math.Min(v, 2) * Levels);
            }

            int v1 = Math.Abs((int)Unsafe.Add(ref level, n));
            cost += Unsafe.Add(ref levelFixed, v1) + Unsafe.Add(ref table, at + Math.Min(v1, Vp8Costs.MaxVariableLevel));
        }

        if (n < 15)
        {
            int ctx = Math.Abs((int)Unsafe.Add(ref level, n)) == 1 ? 1 : 2;
            cost += Vp8Costs.Bit(0, Probabilities[((type * 8) + Vp8Decoder.Bands[n + 1]) * 33 + (ctx * 11)]);
        }

        return cost;
    }

    /// <summary>
    /// The cost of levels <paramref name="first"/> to <paramref name="last"/> with AVX2: every position's table (its context is
    /// the previous level, or <paramref name="ctx0"/>) is known from the levels alone, so all sixteen costs are gathered at once.
    /// </summary>
    private unsafe int Levels16(int type, int first, int ctx0, int last, ReadOnlySpan<short> levels)
    {
        _ = levels[15];
        ref short l = ref MemoryMarshal.GetReference(levels);
        Vector128<short> a0 = Vector128.Abs(Vector128.LoadUnsafe(ref l)), a1 = Vector128.Abs(Vector128.LoadUnsafe(ref l, 8));

        // The previous level of each position, the context ctx0 at the first.
        Vector128<short> previous0 = Sse2.ShiftLeftLogical128BitLane(a0, 2).WithElement(first, (short)ctx0);
        Vector128<short> previous1 = Ssse3.AlignRight(a1, a0, 14);
        Vector128<short> two = Vector128.Create((short)2), top = Vector128.Create((short)Vp8Costs.MaxVariableLevel);
        Vector256<int> typeAt = Vector256.Create(type * 16 * 3 * Levels);
        Vector256<int> at0 = typeAt + PositionsLow + (Avx2.ConvertToVector256Int32(Vector128.Min(previous0, two)) * Levels) + Avx2.ConvertToVector256Int32(Vector128.Min(a0, top));
        Vector256<int> at1 = typeAt + PositionsHigh + (Avx2.ConvertToVector256Int32(Vector128.Min(previous1, two)) * Levels) + Avx2.ConvertToVector256Int32(Vector128.Min(a1, top));
        Vector256<int> costs0, costs1;
        fixed (ushort* table = _withFixed)
        {
            costs0 = Avx2.GatherVector256((int*)table, at0, 2) & Vector256.Create(0xFFFF);
            costs1 = Avx2.GatherVector256((int*)table, at1, 2) & Vector256.Create(0xFFFF);
        }

        // Positions first to last only.
        Vector256<int> from = Vector256.Create(first - 1), to = Vector256.Create(last + 1);
        Vector256<int> n0 = Vector256.Create(0, 1, 2, 3, 4, 5, 6, 7), n1 = Vector256.Create(8, 9, 10, 11, 12, 13, 14, 15);
        costs0 &= Vector256.GreaterThan(n0, from) & Vector256.LessThan(n0, to);
        costs1 &= Vector256.GreaterThan(n1, from) & Vector256.LessThan(n1, to);
        int cost = Vector256.Sum(costs0 + costs1);

        // Levels past MaxVariableLevel share its table entry; their own fixed costs differ.
        if (Vector128.GreaterThanAny(Vector128.Max(a0, a1), top))
        {
            for (int n = first; n <= last; n++)
            {
                int v = Math.Abs((int)Unsafe.Add(ref l, n));
                if (v > Vp8Costs.MaxVariableLevel)
                {
                    cost += Vp8Costs.LevelFixed[v] - Vp8Costs.LevelFixed[Vp8Costs.MaxVariableLevel];
                }
            }
        }

        return cost;
    }

    /// <summary>The position of the last non-zero level of a block (16 levels) at or after <paramref name="first"/>, or −1.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int LastNonZero(ReadOnlySpan<short> levels, int first)
    {
        _ = levels[15];
        ref short l = ref MemoryMarshal.GetReference(levels);
        uint zero = Vector128.Equals(Vector128.LoadUnsafe(ref l), Vector128<short>.Zero).ExtractMostSignificantBits()
            | (Vector128.Equals(Vector128.LoadUnsafe(ref l, 8), Vector128<short>.Zero).ExtractMostSignificantBits() << 8);
        uint nonZero = ~zero & (0xFFFFu << first) & 0xFFFF;
        return nonZero == 0 ? -1 : BitOperations.Log2(nonZero);
    }
}
