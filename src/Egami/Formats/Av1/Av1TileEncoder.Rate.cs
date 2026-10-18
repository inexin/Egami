namespace Egami.Formats.Av1;

/// <summary>
/// The coefficient rate of <see cref="Av1TileEncoder"/>'s search, after libaom's av1_cost_coeffs_txb (txb_rdopt.c; BSD 2-clause,
/// see THIRD-PARTY-NOTICES): the symbols <see cref="Av1TileCoder.CodeCoefficients{TSink}(ref TSink, int, int, int, int, int, int[], int)"/>
/// codes, in the same contexts, priced from <see cref="Av1CoeffCosts"/> tables rather than from the CDFs one by one.
/// </summary>
internal sealed partial class Av1TileEncoder
{
    private readonly byte[] _rateLevels = new byte[((32 + 4) * (32 + 4)) + 16];

    /// <summary>
    /// Gets the rate of a transform block's levels in <see cref="_quantized"/> (all-zero flag, type, end of block, levels,
    /// signs) and sets its entropy contexts, as coding them would.
    /// </summary>
    private long CoefficientRate(int plane, int blkRow, int blkCol, int txSize, int txType, int eob)
    {
        (int skipCtx, int dcSignCtx) = TxbContext(plane, blkRow, blkCol, txSize);
        int txs = (Av1Tables.TxSizeSqrMap[txSize] + Av1Tables.TxSizeSqrUpMap[txSize] + 1) >> 1;
        if (eob == 0)
        {
            SetEntropyContexts(plane, blkRow, blkCol, txSize, 0);
            return _costs.TxbSkipCost(txs, skipCtx, 1);
        }

        long cost = _costs.TxbSkipCost(txs, skipCtx, 0) + TxTypeRate(plane, txSize, txType);
        int planeType = plane > 0 ? 1 : 0;
        int txClass = TxType.Class(txType);
        int adjusted = Av1Tables.AdjustedTxSize(txSize);
        int bhl = Av1Tables.TxSizeHighLog2[adjusted];
        int area = Av1Tables.TxSizeWide[adjusted] * Av1Tables.TxSizeHigh[adjusted];
        short[] scan = Av1Tables.ScanOrders[(txSize * 16) + txType];
        int[] coeffs = _quantized;

        // The end of block: its group, then the group's offset bits.
        int eobMultiSize = Av1Tables.TxSizeLog2Minus4[txSize];
        int eobPt = 1;
        while (eobPt < Av1Tables.EobGroupStart.Length - 1 && Av1Tables.EobGroupStart[eobPt + 1] <= eob)
        {
            eobPt++;
        }

        cost += _costs.Eob[(((((eobMultiSize * 2) + planeType) * 2) + (txClass == 0 ? 0 : 1)) * 11) + eobPt - 1];
        int eobOffsetBits = Av1Tables.EobOffsetBits[eobPt];
        if (eobOffsetBits > 0)
        {
            int bit = ((eob - Av1Tables.EobGroupStart[eobPt]) >> (eobOffsetBits - 1)) & 1;
            cost += _costs.EobExtra[(((((txs * 2) + planeType) * 9) + eobPt - 3) * 2) + bit] + ((eobOffsetBits - 1) * Av1CostSink.BitCost);
        }

        ReadOnlySpan<int> baseCosts = _costs.Base.AsSpan(((txs * 2) + planeType) * 42 * 8, 42 * 8);
        ReadOnlySpan<int> lps = _costs.Lps.AsSpan(((txs * 2) + planeType) * 21 * Av1CoeffCosts.LpsStride, 21 * Av1CoeffCosts.LpsStride);
        byte[] levels = _rateLevels;

        // The last nonzero level.
        {
            int c = eob - 1;
            int pos = scan[c];
            int level = Math.Min(Math.Abs(coeffs[pos]), 15);
            int ctx = c == 0 ? 0 : c <= area / 8 ? 1 : c <= area / 4 ? 2 : 3;
            cost += _costs.BaseEob[(((((txs * 2) + planeType) * 4) + ctx) * 3) + Math.Min(level, 3) - 1];
            if (level > 2)
            {
                int col = pos >> bhl, row = pos - (col << bhl);
                int brCtx = pos == 0 ? 0 : (txClass == 0 && row < 2 && col < 2) || (txClass == 1 && col == 0) || (txClass == 2 && row == 0) ? 7 : 14;
                cost += lps[(brCtx * Av1CoeffCosts.LpsStride) + Math.Min(level - 3, Av1CoeffCosts.BaseRange)];
            }

            levels[pos + ((pos >> bhl) << 2)] = (byte)level;
        }

        if (eob > 1)
        {
            sbyte[] nzOffset = Av1Tables.NzMapCtxOffset(txSize);
            int levelStride = (1 << bhl) + 4;
            for (int c = eob - 2; c >= 0; c--)
            {
                int pos = scan[c];
                int p = pos + ((pos >> bhl) << 2);
                int level = Math.Min(Math.Abs(coeffs[pos]), 15);
                int ctx;
                if (txClass == 0)
                {
                    if (c == 0)
                    {
                        ctx = 0;
                    }
                    else
                    {
                        int mag = Math.Min((int)levels[p + levelStride], 3) + Math.Min((int)levels[p + 1], 3) + Math.Min((int)levels[p + levelStride + 1], 3)
                            + Math.Min((int)levels[p + (2 * levelStride)], 3) + Math.Min((int)levels[p + 2], 3);
                        ctx = Math.Min((mag + 1) >> 1, 4) + nzOffset[pos];
                    }
                }
                else
                {
                    int mag = Math.Min((int)levels[p + levelStride], 3) + Math.Min((int)levels[p + 1], 3);
                    if (txClass == 2)
                    {
                        mag += Math.Min((int)levels[p + 2], 3) + Math.Min((int)levels[p + 3], 3) + Math.Min((int)levels[p + 4], 3);
                    }
                    else
                    {
                        mag += Math.Min((int)levels[p + (2 * levelStride)], 3) + Math.Min((int)levels[p + (3 * levelStride)], 3)
                            + Math.Min((int)levels[p + (4 * levelStride)], 3);
                    }

                    int colPos = pos >> bhl, rowPos = pos - (colPos << bhl);
                    ctx = Math.Min((mag + 1) >> 1, 4) + NzMapCtxOffset1d[txClass == 1 ? colPos : rowPos];
                }

                cost += baseCosts[(ctx * 8) + Math.Min(level, 3)];
                if (level > 2)
                {
                    int col = pos >> bhl, row = pos - (col << bhl);
                    int mag = levels[p + 1] + levels[p + levelStride];
                    mag += txClass switch
                    {
                        0 => levels[p + levelStride + 1],
                        1 => levels[p + (2 * levelStride)],
                        _ => levels[p + 2],
                    };
                    mag = Math.Min((mag + 1) >> 1, 6);
                    int brCtx = pos == 0 ? mag
                        : (txClass == 0 && row < 2 && col < 2) || (txClass == 1 && col == 0) || (txClass == 2 && row == 0) ? mag + 7 : mag + 14;
                    cost += lps[(brCtx * Av1CoeffCosts.LpsStride) + Math.Min(level - 3, Av1CoeffCosts.BaseRange)];
                }

                levels[p] = (byte)level;
            }
        }

        // Signs and Golomb remainders.
        int culLevel = 0;
        for (int c = 0; c < eob; c++)
        {
            int pos = scan[c];
            int value = coeffs[pos];
            levels[pos + ((pos >> bhl) << 2)] = 0;
            if (value == 0)
            {
                continue;
            }

            int abs = Math.Abs(value);
            cost += c == 0 ? _costs.DcSign[(((planeType * 3) + dcSignCtx) * 2) + (value < 0 ? 1 : 0)] : Av1CostSink.BitCost;
            if (abs >= 15)
            {
                int length = 64 - (int)ulong.LeadingZeroCount((ulong)(abs - 15) + 1);
                cost += ((2 * length) - 1) * Av1CostSink.BitCost;
            }

            culLevel += abs;
        }

        culLevel = Math.Min(7, culLevel);
        int dc = coeffs[0];
        culLevel = dc < 0 ? culLevel | 8 : dc > 0 ? culLevel + 16 : culLevel;
        SetEntropyContexts(plane, blkRow, blkCol, txSize, culLevel);
        return cost;
    }
}
