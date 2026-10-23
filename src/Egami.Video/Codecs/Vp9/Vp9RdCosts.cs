using System.Runtime.CompilerServices;

namespace Egami.Video.Codecs.Vp9;

/// <summary>
/// The cost in 1/512 bits of every symbol a frame codes, from the probabilities it codes with (libvpx's fill_token_costs,
/// vp9_fill_mode_costs and vp9_build_nmv_cost_table are the idea): tokens per context, modes, partitions, transform sizes,
/// references and motion vectors.
/// </summary>
internal sealed class Vp9RdCosts
{
    /// <summary>The magnitudes whose extra bits' cost is kept in a table; larger ones are costed as they come.</summary>
    private const int ValueTable = 1 << 12;

    public const int EobToken = 11;

    /// <summary>[tx][type][ref][band][ctx][eob check 0 or skipped 1][12 tokens].</summary>
    public readonly int[] Tokens = new int[4 * 2 * 2 * 6 * 6 * 2 * 12];

    /// <summary>The cost of each magnitude's extra bits and sign, below <see cref="ValueTable"/>.</summary>
    public readonly int[] ValueExtra = new int[ValueTable];

    public readonly int[] Skip = new int[3 * 2];

    public readonly int[] Partition = new int[16 * 4];

    /// <summary>Keyframe luma modes by above and left mode: [above][left][mode].</summary>
    public readonly int[] KfYMode = new int[10 * 10 * 10];

    public readonly int[] YMode = new int[4 * 10];

    /// <summary>Chroma modes by luma mode: [y][uv].</summary>
    public readonly int[] UVMode = new int[10 * 10];

    /// <summary>Transform sizes by largest size and context: [max tx][ctx][tx].</summary>
    public readonly int[] TxSize = new int[4 * 2 * 4];

    public readonly int[] IntraInter = new int[4 * 2];

    public readonly int[] CompInter = new int[5 * 2];

    public readonly int[] SingleRef = new int[5 * 2 * 2];

    public readonly int[] CompRef = new int[5 * 2];

    /// <summary>Inter modes by context, NEARESTMV first: [ctx][4].</summary>
    public readonly int[] InterMode = new int[7 * 4];

    public readonly int[] SwitchableInterp = new int[4 * 3];

    public readonly int[] MvJoint = new int[4];

    /// <summary>Each motion vector component's cost by its value (row then column), at high and normal precision, offset by <see cref="MvMax"/>.</summary>
    public readonly int[][] MvComponent = [new int[(2 * MvMax) + 1], new int[(2 * MvMax) + 1], new int[(2 * MvMax) + 1], new int[(2 * MvMax) + 1]];

    public readonly int[] Segment = new int[8];

    /// <summary>The largest motion vector difference costed from the table, in 1/8 pixels.</summary>
    public const int MvMax = (1 << 14) - 1;

    private int _bitDepth = 8;
    private bool _mvsFilled;
    private readonly byte[] _mvProbs = new byte[3 + (2 * Vp9FrameContext.MvCompSize)];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int TokenIndex(int tx, int type, int reference, int band, int ctx) =>
        ((((((tx * 2) + type) * 2) + reference) * 6) + band) * 6 * 24 + (ctx * 24);

    /// <summary>Recomputes every cost from <paramref name="p"/>.</summary>
    public void Fill(byte[] p, bool intraFrame, int bitDepth)
    {
        _bitDepth = bitDepth;
        FillTokens(p, bitDepth);
        for (int i = 0; i < 3; i++)
        {
            Skip[i * 2] = Vp9EncoderTables.Cost(p[Vp9FrameContext.Skip + i], 0);
            Skip[(i * 2) + 1] = Vp9EncoderTables.Cost(p[Vp9FrameContext.Skip + i], 1);
        }

        for (int ctx = 0; ctx < 16; ctx++)
        {
            ReadOnlySpan<byte> probs = intraFrame ? Vp9Tables.KfPartitionProbs.AsSpan(ctx * 3, 3) : p.AsSpan(Vp9FrameContext.Partition + (ctx * 3), 3);
            Vp9EncoderTables.Partition.Costs(probs, Partition.AsSpan(ctx * 4, 4));
        }

        for (int a = 0; a < 10; a++)
        {
            for (int l = 0; l < 10; l++)
            {
                Vp9EncoderTables.IntraMode.Costs(Vp9Tables.KfYModeProb.AsSpan(((a * 10) + l) * 9, 9), KfYMode.AsSpan(((a * 10) + l) * 10, 10));
            }
        }

        for (int g = 0; g < 4; g++)
        {
            Vp9EncoderTables.IntraMode.Costs(p.AsSpan(Vp9FrameContext.YMode + (g * 9), 9), YMode.AsSpan(g * 10, 10));
        }

        for (int y = 0; y < 10; y++)
        {
            ReadOnlySpan<byte> probs = intraFrame ? Vp9Tables.KfUVModeProb.AsSpan(y * 9, 9) : p.AsSpan(Vp9FrameContext.UVMode + (y * 9), 9);
            Vp9EncoderTables.IntraMode.Costs(probs, UVMode.AsSpan(y * 10, 10));
        }

        for (int ctx = 0; ctx < 2; ctx++)
        {
            int p8 = p[Vp9FrameContext.Tx8x8 + ctx];
            TxSize[(((1 * 2) + ctx) * 4) + 0] = Vp9EncoderTables.Cost(p8, 0);
            TxSize[(((1 * 2) + ctx) * 4) + 1] = Vp9EncoderTables.Cost(p8, 1);
            int b16 = Vp9FrameContext.Tx16x16 + (ctx * 2);
            TxSize[(((2 * 2) + ctx) * 4) + 0] = Vp9EncoderTables.Cost(p[b16], 0);
            TxSize[(((2 * 2) + ctx) * 4) + 1] = Vp9EncoderTables.Cost(p[b16], 1) + Vp9EncoderTables.Cost(p[b16 + 1], 0);
            TxSize[(((2 * 2) + ctx) * 4) + 2] = Vp9EncoderTables.Cost(p[b16], 1) + Vp9EncoderTables.Cost(p[b16 + 1], 1);
            int b32 = Vp9FrameContext.Tx32x32 + (ctx * 3);
            TxSize[(((3 * 2) + ctx) * 4) + 0] = Vp9EncoderTables.Cost(p[b32], 0);
            TxSize[(((3 * 2) + ctx) * 4) + 1] = Vp9EncoderTables.Cost(p[b32], 1) + Vp9EncoderTables.Cost(p[b32 + 1], 0);
            TxSize[(((3 * 2) + ctx) * 4) + 2] = Vp9EncoderTables.Cost(p[b32], 1) + Vp9EncoderTables.Cost(p[b32 + 1], 1) + Vp9EncoderTables.Cost(p[b32 + 2], 0);
            TxSize[(((3 * 2) + ctx) * 4) + 3] = Vp9EncoderTables.Cost(p[b32], 1) + Vp9EncoderTables.Cost(p[b32 + 1], 1) + Vp9EncoderTables.Cost(p[b32 + 2], 1);
        }

        if (intraFrame)
        {
            return;
        }

        Bits(p, Vp9FrameContext.IntraInter, 4, IntraInter);
        Bits(p, Vp9FrameContext.CompInter, 5, CompInter);
        Bits(p, Vp9FrameContext.SingleRef, 10, SingleRef);
        Bits(p, Vp9FrameContext.CompRef, 5, CompRef);
        for (int ctx = 0; ctx < 7; ctx++)
        {
            Vp9EncoderTables.InterMode.Costs(p.AsSpan(Vp9FrameContext.InterMode + (ctx * 3), 3), InterMode.AsSpan(ctx * 4, 4));
        }

        for (int ctx = 0; ctx < 4; ctx++)
        {
            Vp9EncoderTables.SwitchableInterp.Costs(p.AsSpan(Vp9FrameContext.SwitchableInterp + (ctx * 2), 2), SwitchableInterp.AsSpan(ctx * 3, 3));
        }

        // The motion vector tables are large: rebuilt only when their probabilities changed.
        ReadOnlySpan<byte> mv = p.AsSpan(Vp9FrameContext.MvJoints, _mvProbs.Length);
        if (!_mvsFilled || !mv.SequenceEqual(_mvProbs))
        {
            mv.CopyTo(_mvProbs);
            Vp9EncoderTables.MvJoint.Costs(p.AsSpan(Vp9FrameContext.MvJoints, 3), MvJoint);
            for (int c = 0; c < 2; c++)
            {
                int o = Vp9FrameContext.MvComp + (c * Vp9FrameContext.MvCompSize);
                FillMvComponent(p, o, MvComponent[c], true);
                FillMvComponent(p, o, MvComponent[2 + c], false);
            }

            _mvsFilled = true;
        }
    }

    /// <summary>Sets the segment tree's costs from the frame's segment probabilities.</summary>
    public void FillSegments(ReadOnlySpan<byte> probs) => Vp9EncoderTables.Segment.Costs(probs, Segment);

    private static void Bits(byte[] p, int offset, int count, int[] costs)
    {
        for (int i = 0; i < count; i++)
        {
            costs[i * 2] = Vp9EncoderTables.Cost(p[offset + i], 0);
            costs[(i * 2) + 1] = Vp9EncoderTables.Cost(p[offset + i], 1);
        }
    }

    /// <summary>A motion vector component's cost for each value (vp9_build_nmv_cost_table's build_nmv_component_cost_table).</summary>
    private static void FillMvComponent(byte[] p, int o, int[] costs, bool usehp)
    {
        int sign0 = Vp9EncoderTables.Cost(p[o + Vp9FrameContext.MvSign], 0), sign1 = Vp9EncoderTables.Cost(p[o + Vp9FrameContext.MvSign], 1);
        Span<int> classCost = stackalloc int[11];
        Vp9EncoderTables.MvClass.Costs(p.AsSpan(o + Vp9FrameContext.MvClasses, 10), classCost);
        Span<int> class0Cost = stackalloc int[2];
        Vp9EncoderTables.MvClass0.Costs(p.AsSpan(o + Vp9FrameContext.MvClass0, 1), class0Cost);
        Span<int> bitsCost = stackalloc int[20];
        for (int i = 0; i < 10; i++)
        {
            bitsCost[i * 2] = Vp9EncoderTables.Cost(p[o + Vp9FrameContext.MvBits + i], 0);
            bitsCost[(i * 2) + 1] = Vp9EncoderTables.Cost(p[o + Vp9FrameContext.MvBits + i], 1);
        }

        Span<int> class0FpCost = stackalloc int[8];
        for (int i = 0; i < 2; i++)
        {
            Vp9EncoderTables.MvFp.Costs(p.AsSpan(o + Vp9FrameContext.MvClass0Fp + (i * 3), 3), class0FpCost.Slice(i * 4, 4));
        }

        Span<int> fpCost = stackalloc int[4];
        Vp9EncoderTables.MvFp.Costs(p.AsSpan(o + Vp9FrameContext.MvFp, 3), fpCost);
        int class0Hp0 = 0, class0Hp1 = 0, hp0 = 0, hp1 = 0;
        if (usehp)
        {
            class0Hp0 = Vp9EncoderTables.Cost(p[o + Vp9FrameContext.MvClass0Hp], 0);
            class0Hp1 = Vp9EncoderTables.Cost(p[o + Vp9FrameContext.MvClass0Hp], 1);
            hp0 = Vp9EncoderTables.Cost(p[o + Vp9FrameContext.MvHp], 0);
            hp1 = Vp9EncoderTables.Cost(p[o + Vp9FrameContext.MvHp], 1);
        }

        costs[MvMax] = 0;
        for (int v = 1; v <= MvMax; v++)
        {
            int z = v - 1;
            int c = z >= 2 * 4096 ? 10 : Log2(z >> 3);
            int offset = z - (c != 0 ? 2 << (c + 2) : 0);
            int d = offset >> 3, f = (offset >> 1) & 3, e = offset & 1;
            int cost = classCost[c];
            if (c == 0)
            {
                cost += class0Cost[d] + class0FpCost[(d * 4) + f] + (usehp ? (e != 0 ? class0Hp1 : class0Hp0) : 0);
            }
            else
            {
                for (int i = 0; i < c; i++)
                {
                    cost += bitsCost[(i * 2) + ((d >> i) & 1)];
                }

                cost += fpCost[f] + (usehp ? (e != 0 ? hp1 : hp0) : 0);
            }

            costs[MvMax + v] = cost + sign0;
            costs[MvMax - v] = cost + sign1;
        }
    }

    private static int Log2(int n) => n < 2 ? 0 : System.Numerics.BitOperations.Log2((uint)n);

    private void FillTokens(byte[] p, int bitDepth)
    {
        Span<int> rest = stackalloc int[12];
        for (int tx = 0; tx < 4; tx++)
        {
            for (int type = 0; type < 2; type++)
            {
                for (int reference = 0; reference < 2; reference++)
                {
                    int probBase = Vp9FrameContext.CoefOffset(tx, type, reference);
                    for (int band = 0; band < 6; band++)
                    {
                        for (int ctx = 0; ctx < (band == 0 ? 3 : 6); ctx++)
                        {
                            int i = probBase + (band * 18) + (ctx * 3);
                            int p0 = p[i], p1 = p[i + 1], p2 = p[i + 2];
                            ReadOnlySpan<byte> pareto = Vp9Tables.Pareto8Full.AsSpan((p2 - 1) * 8, 8);
                            int more = Vp9EncoderTables.Cost(p1, 1) + Vp9EncoderTables.Cost(p2, 1);
                            int big = more + Vp9EncoderTables.Cost(pareto[0], 1);
                            int small = more + Vp9EncoderTables.Cost(pareto[0], 0);
                            rest[0] = Vp9EncoderTables.Cost(p1, 0);
                            rest[1] = Vp9EncoderTables.Cost(p1, 1) + Vp9EncoderTables.Cost(p2, 0);
                            rest[2] = small + Vp9EncoderTables.Cost(pareto[1], 0);
                            rest[3] = small + Vp9EncoderTables.Cost(pareto[1], 1) + Vp9EncoderTables.Cost(pareto[2], 0);
                            rest[4] = small + Vp9EncoderTables.Cost(pareto[1], 1) + Vp9EncoderTables.Cost(pareto[2], 1);
                            int low = big + Vp9EncoderTables.Cost(pareto[3], 0);
                            rest[5] = low + Vp9EncoderTables.Cost(pareto[4], 0);
                            rest[6] = low + Vp9EncoderTables.Cost(pareto[4], 1);
                            int high = big + Vp9EncoderTables.Cost(pareto[3], 1);
                            int mid = high + Vp9EncoderTables.Cost(pareto[5], 0);
                            rest[7] = mid + Vp9EncoderTables.Cost(pareto[6], 0);
                            rest[8] = mid + Vp9EncoderTables.Cost(pareto[6], 1);
                            int top = high + Vp9EncoderTables.Cost(pareto[5], 1);
                            rest[9] = top + Vp9EncoderTables.Cost(pareto[7], 0);
                            rest[10] = top + Vp9EncoderTables.Cost(pareto[7], 1);
                            int at = TokenIndex(tx, type, reference, band, ctx);
                            int notEob = Vp9EncoderTables.Cost(p0, 1);
                            for (int t = 0; t < 11; t++)
                            {
                                Tokens[at + t] = notEob + rest[t];
                                Tokens[at + 12 + t] = rest[t];
                            }

                            Tokens[at + EobToken] = Vp9EncoderTables.Cost(p0, 0);
                            Tokens[at + 12 + EobToken] = 0;
                        }
                    }
                }
            }
        }

        if (ValueExtra[0] == 0 || _valueDepth != bitDepth)
        {
            _valueDepth = bitDepth;
            ValueExtra[0] = 0;
            for (int v = 1; v < ValueTable; v++)
            {
                ValueExtra[v] = ExtraCost(v, bitDepth);
            }
        }
    }

    private int _valueDepth;

    /// <summary>The cost of a magnitude's extra bits and its sign.</summary>
    public static int ExtraCost(int magnitude, int bitDepth)
    {
        int token = Vp9EncoderTables.Token(magnitude);
        int cost = 512;
        if (token >= 5)
        {
            int extra = magnitude - Vp9EncoderTables.TokenBase[token];
            int bits = Vp9EncoderTables.ExtraBits(token, bitDepth);
            ReadOnlySpan<byte> probs = Vp9EncoderTables.CatProbs(token, bitDepth);
            for (int i = 0; i < bits; i++)
            {
                cost += Vp9EncoderTables.Cost(probs[i], (extra >> (bits - 1 - i)) & 1);
            }
        }

        return cost;
    }

    /// <summary>The cost of a magnitude's extra bits and sign (0 for zero).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Extra(int magnitude) => magnitude < ValueTable ? ValueExtra[magnitude] : ExtraCost(magnitude, _bitDepth);

    /// <summary>The cost of a motion vector difference (row, column) in 1/8 pixels.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Mv(int row, int col, bool usehp)
    {
        int joint = (row != 0 ? 2 : 0) | (col != 0 ? 1 : 0);
        int[] rows = MvComponent[usehp ? 0 : 2], cols = MvComponent[usehp ? 1 : 3];
        return MvJoint[joint] + rows[MvMax + Math.Clamp(row, -MvMax, MvMax)] + cols[MvMax + Math.Clamp(col, -MvMax, MvMax)];
    }
}
