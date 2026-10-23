namespace Egami.Video.Codecs.Vp9;

/// <summary>
/// Forward probability updates in the compressed header (vp9_subexp.c and vp9_bitstream.c): each probability the frame's
/// symbols would code cheaper with a new value is sent as a sub-exponential delta when the saving beats the update's cost.
/// </summary>
internal sealed partial class Vp9Encoder<T>
{
    private const int DiffUpdateProb = 252;
    private const int MvUpdateProb = 252;

    /// <summary>The full token counts the frame codes: [tx][type][ref][band][ctx][ZERO..CAT6, EOB].</summary>
    private readonly int[] _tokenCounts = new int[4 * 2 * 2 * 6 * 6 * 12];

    private static ReadOnlySpan<byte> UpdateBits =>
    [
        5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 8, 8, 8, 8, 8, 8, 8, 8,
        8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10,
        10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10,
        10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 11, 11, 11, 11, 11, 11,
        11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11,
        11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11,
        11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11,
        11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11, 11,
        11, 11, 11, 11, 11, 11, 11, 0,
    ];

    private static ReadOnlySpan<byte> MapTable =>
    [
        20, 21, 22, 23, 24, 25, 0, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 1, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47,
        48, 49, 2, 50, 51, 52, 53, 54, 55, 56, 57, 58, 59, 60, 61, 3, 62, 63, 64, 65, 66, 67, 68, 69, 70, 71, 72, 73, 4, 74,
        75, 76, 77, 78, 79, 80, 81, 82, 83, 84, 85, 5, 86, 87, 88, 89, 90, 91, 92, 93, 94, 95, 96, 97, 6, 98, 99, 100, 101, 102,
        103, 104, 105, 106, 107, 108, 109, 7, 110, 111, 112, 113, 114, 115, 116, 117, 118, 119, 120, 121, 8, 122, 123, 124, 125,
        126, 127, 128, 129, 130, 131, 132, 133, 9, 134, 135, 136, 137, 138, 139, 140, 141, 142, 143, 144, 145, 10, 146, 147,
        148, 149, 150, 151, 152, 153, 154, 155, 156, 157, 11, 158, 159, 160, 161, 162, 163, 164, 165, 166, 167, 168, 169, 12,
        170, 171, 172, 173, 174, 175, 176, 177, 178, 179, 180, 181, 13, 182, 183, 184, 185, 186, 187, 188, 189, 190, 191, 192,
        193, 14, 194, 195, 196, 197, 198, 199, 200, 201, 202, 203, 204, 205, 15, 206, 207, 208, 209, 210, 211, 212, 213, 214,
        215, 216, 217, 16, 218, 219, 220, 221, 222, 223, 224, 225, 226, 227, 228, 229, 17, 230, 231, 232, 233, 234, 235, 236,
        237, 238, 239, 240, 241, 18, 242, 243, 244, 245, 246, 247, 248, 249, 250, 251, 252, 253, 19,
    ];

    private static int RecenterNonneg(int v, int m) => v > (m << 1) ? v : v >= m ? (v - m) << 1 : ((m - v) << 1) - 1;

    private static int RemapProb(int v, int m)
    {
        v--;
        m--;
        int i = (m << 1) <= 255 ? RecenterNonneg(v, m) - 1 : RecenterNonneg(255 - 1 - v, 255 - 1 - m) - 1;
        return MapTable[i];
    }

    private static int UpdateCost(int newp, int oldp) => UpdateBits[RemapProb(newp, oldp)] << Vp9EncoderTables.CostShift;

    private static long CostBranch(long ct0, long ct1, int p) => (ct0 * Vp9EncoderTables.Cost(p, 0)) + (ct1 * Vp9EncoderTables.Cost(p, 1));

    private static int BinaryProb(long n0, long n1)
    {
        long den = n0 + n1;
        if (den == 0)
        {
            return 128;
        }

        long p = ((n0 * 256) + (den >> 1)) / den;
        return (int)Math.Clamp(p, 1, 255);
    }

    /// <summary>The best new value of a probability and its saving in 1/512 bits (vp9_prob_diff_update_savings_search).</summary>
    private static long SavingsSearch(long ct0, long ct1, int oldp, ref int bestp)
    {
        long oldB = CostBranch(ct0, ct1, oldp);
        long best = 0;
        int bestNew = oldp;
        int step = bestp > oldp ? -1 : 1;
        int updCost = Vp9EncoderTables.Cost(DiffUpdateProb, 1) - Vp9EncoderTables.Cost(DiffUpdateProb, 0);
        if (oldB > updCost + (5 << Vp9EncoderTables.CostShift))
        {
            for (int newp = bestp; newp != oldp; newp += step)
            {
                long savings = oldB - CostBranch(ct0, ct1, newp) - (UpdateCost(newp, oldp) + updCost);
                if (savings > best)
                {
                    best = savings;
                    bestNew = newp;
                }
            }
        }

        bestp = bestNew;
        return best;
    }

    /// <summary>The same for a coefficient tree's pivot node, whose value also sets the Pareto tail's eight probabilities.</summary>
    private static long SavingsSearchModel(ReadOnlySpan<long> ct, int oldp, ref int bestp)
    {
        ReadOnlySpan<byte> oldList = Vp9Tables.Pareto8Full.AsSpan((oldp - 1) * 8, 8);
        long oldB = CostBranch(ct[4], ct[5], oldp);
        for (int i = 3; i < 11; i++)
        {
            oldB += CostBranch(ct[2 * i], ct[(2 * i) + 1], oldList[i - 3]);
        }

        long best = 0;
        int bestNew = oldp;
        int sign = bestp > oldp ? -1 : 1;
        int updCost = Vp9EncoderTables.Cost(DiffUpdateProb, 1) - Vp9EncoderTables.Cost(DiffUpdateProb, 0);
        if (oldB > updCost + (5 << Vp9EncoderTables.CostShift))
        {
            for (int newp = bestp; (newp - oldp) * sign < 0; newp += sign)
            {
                if (newp is < 1 or > 255)
                {
                    continue;
                }

                ReadOnlySpan<byte> list = Vp9Tables.Pareto8Full.AsSpan((newp - 1) * 8, 8);
                long newB = CostBranch(ct[4], ct[5], newp);
                for (int i = 3; i < 11; i++)
                {
                    newB += CostBranch(ct[2 * i], ct[(2 * i) + 1], list[i - 3]);
                }

                long savings = oldB - newB - (UpdateCost(newp, oldp) + updCost);
                if (savings > best)
                {
                    best = savings;
                    bestNew = newp;
                }
            }
        }

        bestp = bestNew;
        return best;
    }

    /// <summary>Writes a probability's new value as a delta (vp9_write_prob_diff_update).</summary>
    private static void WriteDiff(ref Vp9BoolEncoder w, int newp, int oldp)
    {
        int word = RemapProb(newp, oldp);
        if (word < 16)
        {
            w.WriteBit(0);
            w.WriteLiteral(word, 4);
        }
        else if (word < 32)
        {
            w.WriteBit(1);
            w.WriteBit(0);
            w.WriteLiteral(word - 16, 4);
        }
        else if (word < 64)
        {
            w.WriteBit(1);
            w.WriteBit(1);
            w.WriteBit(0);
            w.WriteLiteral(word - 32, 5);
        }
        else
        {
            w.WriteBit(1);
            w.WriteBit(1);
            w.WriteBit(1);
            int v = word - 64;
            const int m = (1 << 8) - 191;
            if (v < m)
            {
                w.WriteLiteral(v, 7);
            }
            else
            {
                w.WriteLiteral(m + ((v - m) >> 1), 7);
                w.WriteLiteral((v - m) & 1, 1);
            }
        }
    }

    /// <summary>Updates one probability when that saves bits (vp9_cond_prob_diff_update).</summary>
    private static void UpdateBinary(ref Vp9BoolEncoder w, byte[] p, int index, long ct0, long ct1)
    {
        int oldp = p[index];
        int newp = BinaryProb(ct0, ct1);
        long savings = SavingsSearch(ct0, ct1, oldp, ref newp);
        if (savings > 0)
        {
            w.Write(1, DiffUpdateProb);
            WriteDiff(ref w, newp, oldp);
            p[index] = (byte)newp;
        }
        else
        {
            w.Write(0, DiffUpdateProb);
        }
    }

    /// <summary>Updates a tree's probabilities from its symbol counts (prob_diff_update).</summary>
    private static void UpdateTree(ref Vp9BoolEncoder w, byte[] p, int offset, TreeCodes codes, ReadOnlySpan<int> counts)
    {
        Span<long> branches = stackalloc long[2 * 16];
        Branches(codes.Tree, 0, counts, branches);
        for (int i = 0; i < codes.Count - 1; i++)
        {
            UpdateBinary(ref w, p, offset + i, branches[2 * i], branches[(2 * i) + 1]);
        }
    }

    /// <summary>The counts of each tree node's two branches from the leaves' counts (vp9_tree_probs_from_distribution).</summary>
    private static long Branches(ReadOnlySpan<sbyte> tree, int i, ReadOnlySpan<int> counts, Span<long> branches)
    {
        int l = tree[i];
        long left = l <= 0 ? counts[-l] : Branches(tree, l, counts, branches);
        int r = tree[i + 1];
        long right = r <= 0 ? counts[-r] : Branches(tree, r, counts, branches);
        branches[i] = left;
        branches[i + 1] = right;
        return left + right;
    }

    private void WriteTxProbs(ref Vp9BoolEncoder w, byte[] p)
    {
        Vp9Counts c = _counts;
        for (int i = 0; i < 2; i++)
        {
            UpdateBinary(ref w, p, Vp9FrameContext.Tx8x8 + i, c.Tx8x8[i * 2], c.Tx8x8[(i * 2) + 1]);
        }

        for (int i = 0; i < 2; i++)
        {
            ReadOnlySpan<int> t = c.Tx16x16.AsSpan(i * 3, 3);
            UpdateBinary(ref w, p, Vp9FrameContext.Tx16x16 + (i * 2), t[0], t[1] + t[2]);
            UpdateBinary(ref w, p, Vp9FrameContext.Tx16x16 + (i * 2) + 1, t[1], t[2]);
        }

        for (int i = 0; i < 2; i++)
        {
            ReadOnlySpan<int> t = c.Tx32x32.AsSpan(i * 4, 4);
            UpdateBinary(ref w, p, Vp9FrameContext.Tx32x32 + (i * 3), t[0], t[1] + t[2] + t[3]);
            UpdateBinary(ref w, p, Vp9FrameContext.Tx32x32 + (i * 3) + 1, t[1], t[2] + t[3]);
            UpdateBinary(ref w, p, Vp9FrameContext.Tx32x32 + (i * 3) + 2, t[2], t[3]);
        }
    }

    /// <summary>The coefficient probability updates (update_coef_probs with libvpx's two loops).</summary>
    private void WriteCoefProbs(ref Vp9BoolEncoder w, byte[] p)
    {
        int maxTx = Vp9Tables.TxModeToBiggestTxSize[_txMode];
        Span<long> ct = stackalloc long[22];
        for (int tx = 0; tx <= maxTx; tx++)
        {
            if (_txBlocks[tx] <= 20)
            {
                w.WriteBit(0);
                continue;
            }

            // A dry run: is any update worth its flags?
            long savings = 0;
            int updates = 0;
            for (int pass = 0; pass < 2; pass++)
            {
                if (pass == 1)
                {
                    if (updates == 0 || savings < 0)
                    {
                        w.WriteBit(0);
                        break;
                    }

                    w.WriteBit(1);
                }

                for (int type = 0; type < 2; type++)
                {
                    for (int reference = 0; reference < 2; reference++)
                    {
                        int probBase = Vp9FrameContext.CoefOffset(tx, type, reference);
                        for (int band = 0; band < 6; band++)
                        {
                            for (int ctx = 0; ctx < (band == 0 ? 3 : 6); ctx++)
                            {
                                CoefBranches(tx, type, reference, band, ctx, ct);
                                for (int t = 0; t < 3; t++)
                                {
                                    int index = probBase + (band * 18) + (ctx * 3) + t;
                                    int oldp = p[index];
                                    int newp = BinaryProb(ct[2 * t], ct[(2 * t) + 1]);
                                    long s = t == 2 ? SavingsSearchModel(ct, oldp, ref newp) : SavingsSearch(ct[2 * t], ct[(2 * t) + 1], oldp, ref newp);
                                    bool u = s > 0 && newp != oldp;
                                    if (pass == 0)
                                    {
                                        int zero = Vp9EncoderTables.Cost(DiffUpdateProb, 0);
                                        savings += u ? s - zero : -zero;
                                        updates += u ? 1 : 0;
                                    }
                                    else
                                    {
                                        w.Write(u ? 1 : 0, DiffUpdateProb);
                                        if (u)
                                        {
                                            WriteDiff(ref w, newp, oldp);
                                            p[index] = (byte)newp;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    /// <summary>The eleven nodes' branch counts of one coefficient context, from the full token counts.</summary>
    private void CoefBranches(int tx, int type, int reference, int band, int ctx, Span<long> ct)
    {
        int cb = ((((((tx * 2) + type) * 2) + reference) * 6) + band) * 6 + ctx;
        ReadOnlySpan<int> n = _tokenCounts.AsSpan(cb * 12, 12);
        long eob = n[11];
        long two = n[2] + n[3] + n[4], cat = n[5] + n[6] + n[7] + n[8] + n[9] + n[10];
        ct[0] = eob;
        ct[1] = _counts.EobBranch[cb] - eob;
        ct[2] = n[0];
        ct[3] = n[1] + two + cat;
        ct[4] = n[1];
        ct[5] = two + cat;
        ct[6] = two;
        ct[7] = cat;
        ct[8] = n[2];
        ct[9] = n[3] + n[4];
        ct[10] = n[3];
        ct[11] = n[4];
        ct[12] = n[5] + n[6];
        ct[13] = n[7] + n[8] + n[9] + n[10];
        ct[14] = n[5];
        ct[15] = n[6];
        ct[16] = n[7] + n[8];
        ct[17] = n[9] + n[10];
        ct[18] = n[7];
        ct[19] = n[8];
        ct[20] = n[9];
        ct[21] = n[10];
    }

    /// <summary>The motion vector probability updates (vp9_write_nmv_probs), each a 7-bit value when it saves bits.</summary>
    private void WriteMvProbs(ref Vp9BoolEncoder w, byte[] p)
    {
        Vp9Counts c = _counts;
        Span<long> branches = stackalloc long[32];
        Branches(Vp9Tables.MvJointTree, 0, c.MvJoints, branches);
        for (int i = 0; i < 3; i++)
        {
            UpdateMv(ref w, p, Vp9FrameContext.MvJoints + i, branches[2 * i], branches[(2 * i) + 1]);
        }

        for (int i = 0; i < 2; i++)
        {
            int o = Vp9FrameContext.MvComp + (i * Vp9FrameContext.MvCompSize);
            int k = i * Vp9Counts.MvCompSize;
            int[] mc = c.MvComp;
            UpdateMv(ref w, p, o + Vp9FrameContext.MvSign, mc[k + Vp9Counts.MvSign], mc[k + Vp9Counts.MvSign + 1]);
            Branches(Vp9Tables.MvClassTree, 0, mc.AsSpan(k + Vp9Counts.MvClasses, 11), branches);
            for (int j = 0; j < 10; j++)
            {
                UpdateMv(ref w, p, o + Vp9FrameContext.MvClasses + j, branches[2 * j], branches[(2 * j) + 1]);
            }

            UpdateMv(ref w, p, o + Vp9FrameContext.MvClass0, mc[k + Vp9Counts.MvClass0], mc[k + Vp9Counts.MvClass0 + 1]);
            for (int j = 0; j < 10; j++)
            {
                int b = k + Vp9Counts.MvBits + (j * 2);
                UpdateMv(ref w, p, o + Vp9FrameContext.MvBits + j, mc[b], mc[b + 1]);
            }
        }

        for (int i = 0; i < 2; i++)
        {
            int o = Vp9FrameContext.MvComp + (i * Vp9FrameContext.MvCompSize);
            int k = i * Vp9Counts.MvCompSize;
            int[] mc = c.MvComp;
            for (int j = 0; j < 2; j++)
            {
                Branches(Vp9Tables.MvFpTree, 0, mc.AsSpan(k + Vp9Counts.MvClass0Fp + (j * 4), 4), branches);
                for (int n = 0; n < 3; n++)
                {
                    UpdateMv(ref w, p, o + Vp9FrameContext.MvClass0Fp + (j * 3) + n, branches[2 * n], branches[(2 * n) + 1]);
                }
            }

            Branches(Vp9Tables.MvFpTree, 0, mc.AsSpan(k + Vp9Counts.MvFp, 4), branches);
            for (int n = 0; n < 3; n++)
            {
                UpdateMv(ref w, p, o + Vp9FrameContext.MvFp + n, branches[2 * n], branches[(2 * n) + 1]);
            }
        }

        if (_allowHighPrecisionMv)
        {
            for (int i = 0; i < 2; i++)
            {
                int o = Vp9FrameContext.MvComp + (i * Vp9FrameContext.MvCompSize);
                int k = i * Vp9Counts.MvCompSize;
                int[] mc = c.MvComp;
                UpdateMv(ref w, p, o + Vp9FrameContext.MvClass0Hp, mc[k + Vp9Counts.MvClass0Hp], mc[k + Vp9Counts.MvClass0Hp + 1]);
                UpdateMv(ref w, p, o + Vp9FrameContext.MvHp, mc[k + Vp9Counts.MvHp], mc[k + Vp9Counts.MvHp + 1]);
            }
        }
    }

    private static void UpdateMv(ref Vp9BoolEncoder w, byte[] p, int index, long ct0, long ct1)
    {
        int cur = p[index];
        int newp = BinaryProb(ct0, ct1) | 1;
        bool update = CostBranch(ct0, ct1, cur) + Vp9EncoderTables.Cost(MvUpdateProb, 0) >
            CostBranch(ct0, ct1, newp) + Vp9EncoderTables.Cost(MvUpdateProb, 1) + (7 << Vp9EncoderTables.CostShift);
        w.Write(update ? 1 : 0, MvUpdateProb);
        if (update)
        {
            p[index] = (byte)newp;
            w.WriteLiteral(newp >> 1, 7);
        }
    }
}
