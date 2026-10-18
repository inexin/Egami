namespace Egami.Formats.Av1;

/// <summary>
/// The rates of coefficient symbols in every context, in 1/512 bits, from a set of CDFs (libaom's av1_fill_coeff_costs, rd.c),
/// for the trellis to weigh levels without running the syntax: base levels, base levels of the last coefficient, the base
/// range with its Golomb remainder, the all-zero flag, DC signs and the end of block.
/// </summary>
internal sealed class Av1CoeffCosts
{
    /// <summary>COEFF_BASE_RANGE: how far the base range codes above the base levels.</summary>
    internal const int BaseRange = 12;

    /// <summary>NUM_BASE_LEVELS: the levels the base symbols code by themselves.</summary>
    internal const int BaseLevels = 2;

    /// <summary>The entries of <see cref="Lps"/> per context: levels 0 to 13, then 13 to 26 the differences between neighbours.</summary>
    internal const int LpsStride = (2 * BaseRange) + 2;

    /// <summary>Gets base_cost[txs][plane type][ctx][level 0 to 3, then 4 to 7 the cost differences libaom keeps], flattened.</summary>
    internal int[] Base { get; } = new int[5 * 2 * 42 * 8];

    /// <summary>Gets base_eob_cost[txs][plane type][ctx][level - 1], flattened.</summary>
    internal int[] BaseEob { get; } = new int[5 * 2 * 4 * 3];

    /// <summary>Gets lps_cost[txs][plane type][ctx][<see cref="LpsStride"/> entries], flattened.</summary>
    internal int[] Lps { get; } = new int[5 * 2 * 21 * LpsStride];

    /// <summary>Gets txb_skip_cost[txs][ctx][skip], flattened.</summary>
    internal int[] TxbSkip { get; } = new int[5 * 13 * 2];

    /// <summary>Gets eob_extra_cost[txs][plane type][ctx][bit], flattened.</summary>
    internal int[] EobExtra { get; } = new int[5 * 2 * 9 * 2];

    /// <summary>Gets dc_sign_cost[plane type][ctx][sign], flattened.</summary>
    internal int[] DcSign { get; } = new int[2 * 3 * 2];

    /// <summary>Gets eob_cost[eob multi size][plane type][1D][eob_pt - 1], flattened.</summary>
    internal int[] Eob { get; } = new int[7 * 2 * 2 * 11];

    /// <summary>Gets the rate of the all-zero flag of a transform size context in a skip context.</summary>
    internal int TxbSkipCost(int txs, int ctx, int skip) => TxbSkip[(((txs * 13) + ctx) * 2) + skip];

    /// <summary>Fills the tables from a context's CDFs.</summary>
    internal void Fill(ushort[] cdf)
    {
        Span<int> br = stackalloc int[4];
        for (int size = 0; size < 7; size++)
        {
            for (int pt = 0; pt < 2; pt++)
            {
                for (int ctx = 0; ctx < 2; ctx++)
                {
                    int at = Av1FrameContext.EobMulti[size][(pt * 2) + ctx];
                    for (int s = 0; s < 5 + size; s++)
                    {
                        Eob[(((((size * 2) + pt) * 2) + ctx) * 11) + s] = Cost(cdf, at, s);
                    }
                }
            }
        }

        for (int txs = 0; txs < 5; txs++)
        {
            for (int ctx = 0; ctx < 13; ctx++)
            {
                int at = Av1FrameContext.TxbSkip[(txs * 13) + ctx];
                TxbSkip[(((txs * 13) + ctx) * 2) + 0] = Cost(cdf, at, 0);
                TxbSkip[(((txs * 13) + ctx) * 2) + 1] = Cost(cdf, at, 1);
            }

            for (int pt = 0; pt < 2; pt++)
            {
                for (int ctx = 0; ctx < 4; ctx++)
                {
                    int at = Av1FrameContext.CoeffBaseEob[(((txs * 2) + pt) * 4) + ctx];
                    for (int s = 0; s < 3; s++)
                    {
                        BaseEob[(((((txs * 2) + pt) * 4) + ctx) * 3) + s] = Cost(cdf, at, s);
                    }
                }

                for (int ctx = 0; ctx < 42; ctx++)
                {
                    int at = Av1FrameContext.CoeffBase[(((txs * 2) + pt) * 42) + ctx];
                    Span<int> b = Base.AsSpan(((((txs * 2) + pt) * 42) + ctx) * 8, 8);
                    for (int s = 0; s < 4; s++)
                    {
                        b[s] = Cost(cdf, at, s);
                    }

                    b[4] = 0;
                    b[5] = b[1] + Av1CostSink.BitCost - b[0];
                    b[6] = b[2] - b[1];
                    b[7] = b[3] - b[2];
                }

                for (int ctx = 0; ctx < 9; ctx++)
                {
                    int at = Av1FrameContext.EobExtra[(((txs * 2) + pt) * 9) + ctx];
                    EobExtra[(((((txs * 2) + pt) * 9) + ctx) * 2) + 0] = Cost(cdf, at, 0);
                    EobExtra[(((((txs * 2) + pt) * 9) + ctx) * 2) + 1] = Cost(cdf, at, 1);
                }

                for (int ctx = 0; ctx < 21; ctx++)
                {
                    int at = Av1FrameContext.CoeffBr[(((Math.Min(txs, 3) * 2) + pt) * 21) + ctx];
                    for (int s = 0; s < 4; s++)
                    {
                        br[s] = Cost(cdf, at, s);
                    }

                    Span<int> lps = Lps.AsSpan(((((txs * 2) + pt) * 21) + ctx) * LpsStride, LpsStride);
                    int prev = 0, i;
                    for (i = 0; i < BaseRange; i += 3)
                    {
                        for (int j = 0; j < 3; j++)
                        {
                            lps[i + j] = prev + br[j];
                        }

                        prev += br[3];
                    }

                    lps[i] = prev;
                    lps[BaseRange + 1] = lps[0];
                    for (int k = 1; k <= BaseRange; k++)
                    {
                        lps[k + BaseRange + 1] = lps[k] - lps[k - 1];
                    }
                }
            }
        }

        for (int pt = 0; pt < 2; pt++)
        {
            for (int ctx = 0; ctx < 3; ctx++)
            {
                int at = Av1FrameContext.DcSign[(pt * 3) + ctx];
                DcSign[(((pt * 3) + ctx) * 2) + 0] = Cost(cdf, at, 0);
                DcSign[(((pt * 3) + ctx) * 2) + 1] = Cost(cdf, at, 1);
            }
        }

        static int Cost(ushort[] cdf, int at, int symbol)
        {
            int high = symbol > 0 ? cdf[at + symbol - 1] : 32768;
            return Av1Rate.ProbabilityCost(high - cdf[at + symbol]);
        }
    }
}

/// <summary>
/// Rate-distortion optimized quantization of one transform block, after libaom's av1_optimize_txb (txb_rdopt.c, BSD 2-clause,
/// see THIRD-PARTY-NOTICES): from the last coefficient back to the DC, each level is lowered by one (or dropped, moving the end
/// of block) where that saves more rate than it costs distortion, with the coefficient contexts as the levels around it stand.
/// Tune IQ's settings: sharpness 7, which keeps small levels, the trellis multiplier's shift of 7, and all-intra's lighter
/// multiplier for chroma (use_chroma_trellis_rd_mult).
/// </summary>
[System.Runtime.CompilerServices.SkipLocalsInit]
internal static class Av1Trellis
{
    private const int Sharpness = 7;

    private static ReadOnlySpan<int> GolombBitsCost =>
    [
        0, 512, 512 * 3, 512 * 3, 512 * 5, 512 * 5, 512 * 5, 512 * 5, 512 * 7, 512 * 7, 512 * 7, 512 * 7, 512 * 7, 512 * 7, 512 * 7, 512 * 7,
        512 * 9, 512 * 9, 512 * 9, 512 * 9, 512 * 9, 512 * 9, 512 * 9, 512 * 9, 512 * 9, 512 * 9, 512 * 9, 512 * 9, 512 * 9, 512 * 9, 512 * 9, 512 * 9,
    ];

    private static ReadOnlySpan<int> GolombCostDiff =>
    [
        0, 512, 512 * 2, 0, 512 * 2, 0, 0, 0, 512 * 2, 0, 0, 0, 0, 0, 0, 0, 512 * 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    ];

    private static ReadOnlySpan<byte> NzMapCtxOffset1d =>
    [
        26, 31, 36, 36, 36, 36, 36, 36, 36, 36, 36, 36, 36, 36, 36, 36, 36, 36, 36, 36, 36, 36, 36, 36, 36, 36, 36, 36, 36, 36, 36, 36,
    ];

    /// <summary>The per-block state of one optimization.</summary>
    private ref struct Block
    {
        internal int[] TCoeff;
        internal int[] QCoeff;
        internal int[] DqCoeff;
        internal Span<byte> Levels;
        internal short[] Scan;
        internal int[] Steps;
        internal int[]? Weights;
        internal ReadOnlySpan<int> Base;
        internal ReadOnlySpan<int> BaseEob;
        internal ReadOnlySpan<int> Lps;
        internal ReadOnlySpan<int> EobExtra;
        internal ReadOnlySpan<int> Eob;
        internal ReadOnlySpan<int> DcSign;
        internal sbyte[] NzOffset;
        internal int TxClass;
        internal int Bhl;
        internal int Width;
        internal int Shift;
        internal long RdMult;
    }

    /// <summary>
    /// Optimizes the levels of a quantized block in place: <paramref name="qcoeff"/> and <paramref name="dqcoeff"/> (column by
    /// column at the coded size) follow each change. Returns the new end of block, and the rate of the levels, their signs and
    /// the end of block (without the all-zero flag and the transform type), as the search goes.
    /// </summary>
    internal static int Optimize(int[] tcoeff, int[] qcoeff, int[] dqcoeff, int eob, int txSize, int txType, int plane, int dcSignCtx,
        int[] steps, int[]? weights, Av1CoeffCosts costs, long rdmult, int bitDepth, out long rate)
    {
        rate = 0;
        if (eob == 0)
        {
            return 0;
        }

        int adjusted = Av1Tables.AdjustedTxSize(txSize);
        int bhl = Av1Tables.TxSizeHighLog2[adjusted];
        int width = Av1Tables.TxSizeWide[adjusted], height = Av1Tables.TxSizeHigh[adjusted];
        int pels = Av1Tables.TxSizeWide[txSize] * Av1Tables.TxSizeHigh[txSize];
        int planeType = plane > 0 ? 1 : 0;
        int stride = height + 4;
        Span<byte> levels = stackalloc byte[((32 + 4) * (32 + 4)) + 16];
        levels[..(((width + 4) * stride) + 16)].Clear();
        for (int c = 0; c < width; c++)
        {
            for (int r = 0; r < height; r++)
            {
                levels[(c * stride) + r] = (byte)Math.Min(Math.Abs(qcoeff[(c * height) + r]), 127);
            }
        }

        int txs = (Av1Tables.TxSizeSqrMap[txSize] + Av1Tables.TxSizeSqrUpMap[txSize] + 1) >> 1;
        int eobMultiSize = Av1Tables.TxSizeLog2Minus4[txSize];
        int txClass = TxType.Class(txType);
        Block b = new()
        {
            Base = costs.Base.AsSpan(((txs * 2) + planeType) * 42 * 8, 42 * 8),
            BaseEob = costs.BaseEob.AsSpan(((txs * 2) + planeType) * 4 * 3, 4 * 3),
            Lps = costs.Lps.AsSpan(((txs * 2) + planeType) * 21 * Av1CoeffCosts.LpsStride, 21 * Av1CoeffCosts.LpsStride),
            EobExtra = costs.EobExtra.AsSpan(((txs * 2) + planeType) * 9 * 2, 9 * 2),
            Eob = costs.Eob.AsSpan(((((eobMultiSize * 2) + planeType) * 2) + (txClass == 0 ? 0 : 1)) * 11, 11),
            DcSign = costs.DcSign.AsSpan(((planeType * 3) + dcSignCtx) * 2, 2),
            TCoeff = tcoeff,
            QCoeff = qcoeff,
            DqCoeff = dqcoeff,
            Levels = levels,
            Scan = Av1Tables.ScanOrders[(txSize * 16) + txType],
            Steps = steps,
            Weights = weights,

            NzOffset = Av1Tables.NzMapCtxOffset(txSize),
            TxClass = txClass,
            Bhl = bhl,
            Width = width,
            Shift = (pels > 256 ? 1 : 0) + (pels > 1024 ? 1 : 0),
            RdMult = ((rdmult * (8 - Sharpness) * ((planeType == 0 ? 17 : 13) << (2 * (bitDepth - 8)))) + 64) >> 7,
        };

        long accuRate = EobCost(ref b, eob);
        long accuDist = 0;
        int si = eob - 1;
        int ci = b.Scan[si];
        int absQc = Math.Abs(qcoeff[ci]);
        Span<int> nzCi = stackalloc int[3];
        nzCi[0] = ci;
        int nzNum = 1;
        const int maxNzNum = 2;
        if (absQc >= 2)
        {
            UpdateGeneral(ref b, ref accuRate, ref accuDist, si, eob);
            si--;
        }
        else
        {
            int ctx = LowerLevelsCtxEob(bhl, width, si);
            accuRate += CoeffCostEob(ref b, ci, absQc, qcoeff[ci] < 0 ? 1 : 0, ctx);
            accuDist += DistDiff(ref b, ci, tcoeff[ci], dqcoeff[ci]);
            si--;
        }

        for (; si >= 0 && nzNum <= maxNzNum; si--)
        {
            UpdateEob(ref b, ref accuRate, ref accuDist, ref eob, ref nzNum, nzCi, si);
        }

        for (; si >= 1; si--)
        {
            UpdateSimple(ref b, ref accuRate, si);
        }

        if (si == 0)
        {
            long dummy = 0;
            UpdateGeneral(ref b, ref accuRate, ref dummy, si, eob);
        }

        rate = accuRate;
        return eob;
    }

    /// <summary>
    /// Gets how much more distortion a dequantized value leaves than zero does, each error weighted by the quantization matrix
    /// when tune IQ's QM-PSNR has one (get_coeff_dist).
    /// </summary>
    private static long DistDiff(ref Block b, int ci, int tcoeff, int dqcoeff)
    {
        if (b.Weights is null)
        {
            return ((long)dqcoeff * (dqcoeff - (2L * tcoeff))) << (2 * b.Shift);
        }

        return Weighted(tcoeff - dqcoeff, b.Shift, b.Weights[ci]) - Weighted(tcoeff, b.Shift, b.Weights[ci]);

        static long Weighted(long diff, int shift, int weight)
        {
            long d = (diff << shift) * weight;
            return ((d * d) + 512) >> 10;
        }
    }

    private static long Rd(long rdmult, long rate, long dist) => (((rate * rdmult) + 256) >> 9) + (dist << 7);

    private static int Padded(int idx, int bhl) => idx + ((idx >> bhl) << 2);

    private static int LowerLevelsCtxEob(int bhl, int width, int scanIdx) =>
        scanIdx == 0 ? 0 : scanIdx <= (width << bhl) / 8 ? 1 : scanIdx <= (width << bhl) / 4 ? 2 : 3;

    private static int LowerLevelsCtx(ref Block b, int ci)
    {
        Span<byte> levels = b.Levels[Padded(ci, b.Bhl)..];
        int stride = (1 << b.Bhl) + 4;
        int mag = Math.Min((int)levels[stride], 3) + Math.Min((int)levels[1], 3);
        if (b.TxClass == 0)
        {
            mag += Math.Min((int)levels[stride + 1], 3) + Math.Min((int)levels[2 * stride], 3) + Math.Min((int)levels[2], 3);
        }
        else if (b.TxClass == 2)
        {
            mag += Math.Min((int)levels[2], 3) + Math.Min((int)levels[3], 3) + Math.Min((int)levels[4], 3);
        }
        else
        {
            mag += Math.Min((int)levels[2 * stride], 3) + Math.Min((int)levels[3 * stride], 3) + Math.Min((int)levels[4 * stride], 3);
        }

        if ((b.TxClass | ci) == 0)
        {
            return 0;
        }

        int ctx = Math.Min((mag + 1) >> 1, 4);
        int col = ci >> b.Bhl, row = ci - (col << b.Bhl);
        return b.TxClass switch
        {
            0 => ctx + b.NzOffset[ci],
            1 => ctx + NzMapCtxOffset1d[col],
            _ => ctx + NzMapCtxOffset1d[row],
        };
    }

    private static int BrCtxEob(int c, int bhl, int txClass)
    {
        int col = c >> bhl, row = c - (col << bhl);
        if (c == 0)
        {
            return 0;
        }

        return (txClass == 0 && row < 2 && col < 2) || (txClass == 1 && col == 0) || (txClass == 2 && row == 0) ? 7 : 14;
    }

    private static int BrCtx(ref Block b, int c)
    {
        int col = c >> b.Bhl, row = c - (col << b.Bhl);
        int stride = (1 << b.Bhl) + 4;
        int pos = (col * stride) + row;
        int mag = b.Levels[pos + 1] + b.Levels[pos + stride];
        mag += b.TxClass switch
        {
            0 => b.Levels[pos + stride + 1],
            1 => b.Levels[pos + (stride << 1)],
            _ => b.Levels[pos + 2],
        };
        mag = Math.Min((mag + 1) >> 1, 6);
        if (c == 0)
        {
            return mag;
        }

        bool near = b.TxClass switch
        {
            0 => row < 2 && col < 2,
            1 => col == 0,
            _ => row == 0,
        };
        return near ? mag + 7 : mag + 14;
    }

    private static int GolombCost(int absQc)
    {
        if (absQc >= 1 + Av1CoeffCosts.BaseLevels + Av1CoeffCosts.BaseRange)
        {
            int r = absQc - Av1CoeffCosts.BaseRange - Av1CoeffCosts.BaseLevels;
            int length = 32 - int.LeadingZeroCount(r);
            return ((2 * length) - 1) * Av1CostSink.BitCost;
        }

        return 0;
    }

    private static int BrCost(ref Block b, int level, int ctx) =>
        b.Lps[(ctx * Av1CoeffCosts.LpsStride) + Math.Min(level - 1 - Av1CoeffCosts.BaseLevels, Av1CoeffCosts.BaseRange)] + GolombCost(level);

    private static int BrCostWithDiff(ref Block b, int level, int ctx, ref int diff)
    {
        const int range = Av1CoeffCosts.BaseRange;
        int baseRange = Math.Min(level - 1 - Av1CoeffCosts.BaseLevels, range);
        int golombBits = 0;
        if (level <= range + 1 + Av1CoeffCosts.BaseLevels)
        {
            diff += b.Lps[(ctx * Av1CoeffCosts.LpsStride) + baseRange + range + 1];
        }

        if (level >= range + 1 + Av1CoeffCosts.BaseLevels)
        {
            int r = level - range - Av1CoeffCosts.BaseLevels;
            if (r < 32)
            {
                golombBits = GolombBitsCost[r];
                diff += GolombCostDiff[r];
            }
            else
            {
                golombBits = GolombCost(level);
                diff += (r & (r - 1)) == 0 ? 1024 : 0;
            }
        }

        return b.Lps[(ctx * Av1CoeffCosts.LpsStride) + baseRange] + golombBits;
    }

    private static int CoeffCostEob(ref Block b, int ci, int absQc, int sign, int ctx)
    {
        int cost = b.BaseEob[(ctx * 3) + Math.Min(absQc, 3) - 1];
        if (absQc != 0)
        {
            cost += ci == 0 ? b.DcSign[sign] : Av1CostSink.BitCost;
            if (absQc > 2)
            {
                cost += BrCost(ref b, absQc, BrCtxEob(ci, b.Bhl, b.TxClass));
            }
        }

        return cost;
    }

    private static int CoeffCostGeneral(ref Block b, bool isLast, int ci, int absQc, int sign, int ctx)
    {
        int cost = isLast ? b.BaseEob[(ctx * 3) + Math.Min(absQc, 3) - 1] : b.Base[(ctx * 8) + Math.Min(absQc, 3)];
        if (absQc != 0)
        {
            cost += ci == 0 ? b.DcSign[sign] : Av1CostSink.BitCost;
            if (absQc > 2)
            {
                cost += BrCost(ref b, absQc, isLast ? BrCtxEob(ci, b.Bhl, b.TxClass) : BrCtx(ref b, ci));
            }
        }

        return cost;
    }

    private static long EobCost(ref Block b, int eob)
    {
        int eobPt = 1;
        while (eobPt < Av1Tables.EobGroupStart.Length - 1 && Av1Tables.EobGroupStart[eobPt + 1] <= eob)
        {
            eobPt++;
        }

        int eobExtra = eob - Av1Tables.EobGroupStart[eobPt];
        long cost = b.Eob[eobPt - 1];
        int offsetBits = Av1Tables.EobOffsetBits[eobPt];
        if (offsetBits > 0)
        {
            int bit = (eobExtra >> (offsetBits - 1)) & 1;
            cost += b.EobExtra[((eobPt - 3) * 2) + bit];
            if (offsetBits > 1)
            {
                cost += (offsetBits - 1) * Av1CostSink.BitCost;
            }
        }

        return cost;
    }

    private static void Lower(ref Block b, int ci, int absQcLow, int sign)
    {
        int dqv = b.Steps[ci];
        b.QCoeff[ci] = sign != 0 ? -absQcLow : absQcLow;
        int dq = (int)(((long)absQcLow * dqv) & 0xffffff) >> b.Shift;
        b.DqCoeff[ci] = sign != 0 ? -dq : dq;
        b.Levels[Padded(ci, b.Bhl)] = (byte)Math.Min(absQcLow, 127);
    }

    private static void UpdateGeneral(ref Block b, ref long accuRate, ref long accuDist, int si, int eob)
    {
        int ci = b.Scan[si];
        int qc = b.QCoeff[ci];
        bool isLast = si == eob - 1;
        int ctx = isLast ? LowerLevelsCtxEob(b.Bhl, b.Width, si) : LowerLevelsCtx(ref b, ci);
        if (qc == 0)
        {
            accuRate += b.Base[(ctx * 8) + 0];
            return;
        }

        int sign = qc < 0 ? 1 : 0;
        int absQc = Math.Abs(qc);
        int tqc = b.TCoeff[ci], dqc = b.DqCoeff[ci];
        int rate = CoeffCostGeneral(ref b, isLast, ci, absQc, sign, ctx);
        int rateLow, dqcLow = 0;
        if (absQc == 1)
        {
            rateLow = b.Base[(ctx * 8) + 0];
        }
        else
        {
            int abs = (int)(((long)(absQc - 1) * b.Steps[ci]) & 0xffffff) >> b.Shift;
            dqcLow = sign != 0 ? -abs : abs;
            rateLow = CoeffCostGeneral(ref b, isLast, ci, absQc - 1, sign, ctx);
        }

        long distDiff = DistDiff(ref b, ci, tqc, dqc);
        long distDiffLow = absQc == 1 ? 0 : DistDiff(ref b, ci, tqc, dqcLow);
        long rd = Rd(b.RdMult, rate, distDiff), rdLow = Rd(b.RdMult, rateLow, distDiffLow);
        if (rdLow < rd)
        {
            Lower(ref b, ci, absQc - 1, sign);
            accuRate += rateLow;
            accuDist += distDiffLow;
        }
        else
        {
            accuRate += rate;
            accuDist += distDiff;
        }
    }

    private static void UpdateSimple(ref Block b, ref long accuRate, int si)
    {
        int ci = b.Scan[si];
        int qc = b.QCoeff[ci];
        int ctx = LowerLevelsCtx(ref b, ci);
        if (qc == 0)
        {
            accuRate += b.Base[(ctx * 8) + 0];
            return;
        }

        int absQc = Math.Abs(qc), absTqc = Math.Abs(b.TCoeff[ci]), absDqc = Math.Abs(b.DqCoeff[ci]);
        if (absQc == 1)
        {
            // Sharpness keeps every level 1 here.
            accuRate += b.Base[(ctx * 8) + 1] + Av1CostSink.BitCost;
            return;
        }

        int diff = 0;
        int rate = b.Base[(ctx * 8) + Math.Min(absQc, 3)];
        if (absQc <= 3)
        {
            diff = b.Base[(ctx * 8) + absQc + 4];
        }

        rate += Av1CostSink.BitCost;
        if (absQc > 2)
        {
            int brDiff = 0;
            rate += BrCostWithDiff(ref b, absQc, BrCtx(ref b, ci), ref brDiff);
            diff += brDiff;
        }

        int rateLow = rate - diff;
        if (absDqc < absTqc)
        {
            accuRate += rate;
            return;
        }

        int dqv = b.Steps[ci];
        int absQcLow = absQc - 1;
        int absDqcLow = (int)(((long)absQcLow * dqv) & 0xffffff) >> b.Shift;
        long distDiff = DistDiff(ref b, ci, absTqc, absDqc);
        long distDiffLow = DistDiff(ref b, ci, absTqc, absDqcLow);
        if (Rd(b.RdMult, rateLow, distDiffLow) < Rd(b.RdMult, rate, distDiff))
        {
            Lower(ref b, ci, absQcLow, b.QCoeff[ci] < 0 ? 1 : 0);
            accuRate += rateLow;
            return;
        }

        accuRate += rate;
    }

    private static void UpdateEob(ref Block b, ref long accuRate, ref long accuDist, ref int eob, ref int nzNum, Span<int> nzCi, int si)
    {
        int ci = b.Scan[si];
        int qc = b.QCoeff[ci];
        int ctx = LowerLevelsCtx(ref b, ci);
        if (qc == 0)
        {
            accuRate += b.Base[(ctx * 8) + 0];
            return;
        }

        int dqv = b.Steps[ci];
        bool lowerLevel = false;
        int absQc = Math.Abs(qc);
        int tqc = b.TCoeff[ci], dqc = b.DqCoeff[ci];
        int sign = qc < 0 ? 1 : 0;
        int absQcLow = 0, dqcLow = 0;
        int rateLow;
        if (absQc == 1)
        {
            rateLow = b.Base[(ctx * 8) + 0];
        }
        else
        {
            absQcLow = absQc - 1;
            int abs = (int)(((long)absQcLow * dqv) & 0xffffff) >> b.Shift;
            dqcLow = sign != 0 ? -abs : abs;
            rateLow = CoeffCostGeneral(ref b, false, ci, absQcLow, sign, ctx);
        }

        long dist = DistDiff(ref b, ci, tqc, dqc);
        long distLow = absQc == 1 ? 0 : DistDiff(ref b, ci, tqc, dqcLow);
        long rate = CoeffCostGeneral(ref b, false, ci, absQc, sign, ctx);
        long rd = Rd(b.RdMult, accuRate + rate, accuDist + dist);
        long rdLow = Rd(b.RdMult, accuRate + rateLow, accuDist + distLow);
        bool lowerNewEob = false;
        int newEob = si + 1;
        int ctxNewEob = LowerLevelsCtxEob(b.Bhl, b.Width, si);
        long newEobCost = EobCost(ref b, newEob);
        long rateCoeffEob = newEobCost + CoeffCostEob(ref b, ci, absQc, sign, ctxNewEob);
        long distNewEob = dist;
        long rdNewEob = Rd(b.RdMult, rateCoeffEob, distNewEob);
        if (absQcLow > 0)
        {
            long rateCoeffEobLow = newEobCost + CoeffCostEob(ref b, ci, absQcLow, sign, ctxNewEob);
            long rdNewEobLow = Rd(b.RdMult, rateCoeffEobLow, distLow);
            if (rdNewEobLow < rdNewEob)
            {
                lowerNewEob = true;
                rdNewEob = rdNewEobLow;
                rateCoeffEob = rateCoeffEobLow;
                distNewEob = distLow;
            }
        }

        int threshold = si <= 5 ? 2 : 1;
        if (absQc > threshold && rdLow < rd)
        {
            lowerLevel = true;
            rd = rdLow;
            rate = rateLow;
            dist = distLow;
        }

        if (newEob >= 5 && rdNewEob < rd)
        {
            for (int ni = 0; ni < nzNum; ni++)
            {
                int last = nzCi[ni];
                b.Levels[Padded(last, b.Bhl)] = 0;
                b.QCoeff[last] = 0;
                b.DqCoeff[last] = 0;
            }

            eob = newEob;
            nzNum = 0;
            accuRate = rateCoeffEob;
            accuDist = distNewEob;
            lowerLevel = lowerNewEob;
        }
        else
        {
            accuRate += rate;
            accuDist += dist;
        }

        if (lowerLevel)
        {
            Lower(ref b, ci, absQcLow, sign);
        }

        if (b.QCoeff[ci] != 0)
        {
            nzCi[nzNum++] = ci;
        }
    }
}
