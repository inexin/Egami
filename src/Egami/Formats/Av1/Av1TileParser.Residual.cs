namespace Egami.Formats.Av1;

/// <summary>The residual of a block: the transform block walk and the coefficient reading of libaom's decodetxb.c.</summary>
internal sealed partial class Av1TileParser
{
    private void DecodeResidual(int bsize)
    {
        Av1BlockInfo b = _b;
        int maxW = MaxBlockWide(bsize, 0), maxH = MaxBlockHigh(bsize, 0);
        int muW = Math.Min(maxW, 16), muH = Math.Min(maxH, 16);
        bool lossless = _header.LosslessArray[b.SegmentId];
        if (!b.IsInter)
        {
            for (int row = 0; row < maxH; row += muH)
            {
                for (int col = 0; col < maxW; col += muW)
                {
                    for (int plane = 0; plane < _planes; plane++)
                    {
                        if (plane > 0 && !_isChromaRef)
                        {
                            break;
                        }

                        int ssx = plane > 0 ? _ssx : 0, ssy = plane > 0 ? _ssy : 0;
                        int txSize = lossless ? Tx.T4x4 : plane == 0 ? b.TxSize : Av1Tables.MaxUvTxSize(bsize, _ssx, _ssy);
                        int stepR = Av1Tables.TxSizeHighUnit[txSize], stepC = Av1Tables.TxSizeWideUnit[txSize];
                        int unitH = (Math.Min(muH + row, maxH) + ssy) >> ssy;
                        int unitW = (Math.Min(muW + col, maxW) + ssx) >> ssx;
                        (int col4, int row4) = PlaneOrigin(plane);
                        for (int blkRow = row >> ssy; blkRow < unitH; blkRow += stepR)
                        {
                            for (int blkCol = col >> ssx; blkCol < unitW; blkCol += stepC)
                            {
                                int eob = 0, txType = TxType.DctDct;
                                if (!b.Skip)
                                {
                                    (eob, txType) = ReadCoefficients(plane, blkRow, blkCol, txSize);
                                }

                                _reconstructor?.IntraBlock(this, plane, (col4 + blkCol) * 4, (row4 + blkRow) * 4, txSize, txType, eob, _coeffs);
                                ClearCoefficients(txSize, txType, eob);
                            }
                        }
                    }
                }
            }

            return;
        }

        _reconstructor?.InterPrediction(this);
        if (b.Skip)
        {
            _reconstructor?.InterDone(this);
            return;
        }

        for (int row = 0; row < maxH; row += muH)
        {
            for (int col = 0; col < maxW; col += muW)
            {
                for (int plane = 0; plane < _planes; plane++)
                {
                    if (plane > 0 && !_isChromaRef)
                    {
                        break;
                    }

                    int ssx = plane > 0 ? _ssx : 0, ssy = plane > 0 ? _ssy : 0;
                    int planeBsize = plane == 0 ? bsize : Av1Tables.PlaneBlockSize(bsize, _ssx, _ssy);
                    int maxTx = lossless ? Tx.T4x4 : plane == 0 ? Av1Tables.MaxTxSizeRectLookup[planeBsize] : Av1Tables.AdjustedTxSize(Av1Tables.MaxTxSizeRectLookup[planeBsize]);
                    int stepR = Av1Tables.TxSizeHighUnit[maxTx], stepC = Av1Tables.TxSizeWideUnit[maxTx];
                    int unitH = (Math.Min(muH + row, maxH) + ssy) >> ssy;
                    int unitW = (Math.Min(muW + col, maxW) + ssx) >> ssx;
                    for (int blkRow = row >> ssy; blkRow < unitH; blkRow += stepR)
                    {
                        for (int blkCol = col >> ssx; blkCol < unitW; blkCol += stepC)
                        {
                            DecodeInterTransformTree(plane, planeBsize, blkRow, blkCol, maxTx);
                        }
                    }
                }
            }
        }

        _reconstructor?.InterDone(this);
    }

    private void DecodeInterTransformTree(int plane, int planeBsize, int blkRow, int blkCol, int txSize)
    {
        int maxH = MaxBlockHigh(planeBsize, plane), maxW = MaxBlockWide(planeBsize, plane);
        if (blkRow >= maxH || blkCol >= maxW)
        {
            return;
        }

        int planeTx = plane > 0
            ? (_header.LosslessArray[_b.SegmentId] ? Tx.T4x4 : Av1Tables.MaxUvTxSize(_b.Size, _ssx, _ssy))
            : _frame.TxSizeMap[((_miRow + blkRow) * _miCols) + _miCol + blkCol];
        if (txSize == planeTx || plane > 0)
        {
            (int eob, int txType) = ReadCoefficients(plane, blkRow, blkCol, txSize);
            (int col4, int row4) = PlaneOrigin(plane);
            _reconstructor?.InterResidual(this, plane, (col4 + blkCol) * 4, (row4 + blkRow) * 4, txSize, txType, eob, _coeffs);
            ClearCoefficients(txSize, txType, eob);
            return;
        }

        int sub = Av1Tables.SubTxSizeMap[txSize];
        int bsw = Av1Tables.TxSizeWideUnit[sub], bsh = Av1Tables.TxSizeHighUnit[sub];
        int rowEnd = Math.Min(Av1Tables.TxSizeHighUnit[txSize], maxH - blkRow);
        int colEnd = Math.Min(Av1Tables.TxSizeWideUnit[txSize], maxW - blkCol);
        for (int row = 0; row < rowEnd; row += bsh)
        {
            for (int col = 0; col < colEnd; col += bsw)
            {
                DecodeInterTransformTree(plane, planeBsize, blkRow + row, blkCol + col, sub);
            }
        }
    }

    private void ClearCoefficients(int txSize, int txType, int eob)
    {
        if (eob == 0)
        {
            return;
        }

        short[] scan = Av1Tables.ScanOrders[(txSize * 16) + txType];
        for (int c = 0; c < eob; c++)
        {
            _coeffs[scan[c]] = 0;
        }
    }

    /// <summary>Reads a transform block's coefficients (av1_read_coeffs_txb): returns the end of block and the transform type.</summary>
    private (int Eob, int TxType) ReadCoefficients(int plane, int blkRow, int blkCol, int txSize)
    {
        Av1BlockInfo b = _b;
        int txw = Av1Tables.TxSizeWideUnit[txSize], txh = Av1Tables.TxSizeHighUnit[txSize];
        (int skipCtx, int dcSignCtx) = TxbContext(plane, blkRow, blkCol, txSize);
        (int eob, int txType, int culLevel) = ReadCoefficientsTxb(plane, blkRow, blkCol, txSize, skipCtx, dcSignCtx);
        SetEntropyContexts(plane, blkRow, blkCol, txSize, culLevel);

        if (b.IsInter && plane == 0 && (txw == 16 || txh == 16))
        {
            for (int idy = 0; idy < txh; idy += 4)
            {
                for (int idx = 0; idx < txw; idx += 4)
                {
                    int row = _miRow + blkRow + idy, col = _miCol + blkCol + idx;
                    if (row < _frame.MiRows && col < _miCols)
                    {
                        _frame.TxTypeMap[(row * _miCols) + col] = (byte)txType;
                    }
                }
            }
        }

        return (eob, txType);
    }

    private (int Eob, int TxType, int CulLevel) ReadCoefficientsTxb(int plane, int blkRow, int blkCol, int txSize, int skipCtx, int dcSignCtx)
    {
        Av1BlockInfo b = _b;
        int bitDepth = _sequence.BitDepth;
        int maxValue = (1 << (7 + bitDepth)) - 1, minValue = -(1 << (7 + bitDepth));
        int txsCtx = (Av1Tables.TxSizeSqrMap[txSize] + Av1Tables.TxSizeSqrUpMap[txSize] + 1) >> 1;
        int planeType = plane > 0 ? 1 : 0;
        int adjusted = Av1Tables.AdjustedTxSize(txSize);
        int bhl = Av1Tables.TxSizeHighLog2[adjusted];
        int width = Av1Tables.TxSizeWide[adjusted], height = Av1Tables.TxSizeHigh[adjusted];
        int originIndex = ((_miRow + blkRow) * _miCols) + _miCol + blkCol;
        if (Symbol(Av1FrameContext.TxbSkip[(txsCtx * 13) + skipCtx], 2) != 0)
        {
            if (plane == 0)
            {
                _frame.TxTypeMap[originIndex] = TxType.DctDct;
            }

            return (0, TxType.DctDct, 0);
        }

        if (plane == 0)
        {
            ReadTxType(originIndex, txSize);
        }

        int txType = GetTxType(planeType, blkRow, blkCol, txSize);
        int txClass = TxType.Class(txType);
        short[] scan = Av1Tables.ScanOrders[(txSize * 16) + txType];

        // Dequantization: the block's quantizer, and the segment's matrix for two-dimensional transforms.
        int segment = b.SegmentId;
        int qindex = _header.DeltaQPresent ? SegmentQIndex(segment, b.QIndex) : _header.SegQIdx[segment];
        (int dcDelta, int acDelta) = plane switch
        {
            0 => (_header.DeltaQYDc, 0),
            1 => (_header.DeltaQUDc, _header.DeltaQUAc),
            _ => (_header.DeltaQVDc, _header.DeltaQVAc),
        };
        int dcQ = DcQ(qindex + dcDelta, bitDepth), acQ = AcQ(qindex + acDelta, bitDepth);
        int qmLevel = _header.UsingQmatrix && !_header.LosslessArray[segment] ? (plane == 0 ? _header.QmY : plane == 1 ? _header.QmU : _header.QmV) : 15;
        int qmOffset = qmLevel < 15 && txType < TxType.Idtx ? (((qmLevel * 2) + (plane > 0 ? 1 : 0)) * 3344) + Av1Tables.QmOffset(adjusted) : -1;

        int eobMultiSize = Av1Tables.TxSizeLog2Minus4[txSize];
        int eobPt = Symbol(Av1FrameContext.EobMulti[eobMultiSize][(planeType * 2) + (txClass == 0 ? 0 : 1)], 5 + eobMultiSize) + 1;
        int eobExtra = 0;
        int eobOffsetBits = Av1Tables.EobOffsetBits[eobPt];
        if (eobOffsetBits > 0)
        {
            if (Symbol(Av1FrameContext.EobExtra[(((txsCtx * 2) + planeType) * 9) + eobPt - 3], 2) != 0)
            {
                eobExtra += 1 << (eobOffsetBits - 1);
            }

            for (int i = 1; i < eobOffsetBits; i++)
            {
                if (_r.Bit() != 0)
                {
                    eobExtra += 1 << (eobOffsetBits - 1 - i);
                }
            }
        }

        int eob = Av1Tables.EobGroupStart[eobPt];
        if (eob > 2)
        {
            eob += eobExtra;
        }

        // The levels buffer is all zero between blocks: each block clears the positions it wrote when it is done.
        byte[] levels = _levels;

        int baseTable = Av1FrameContext.CoeffBase[((txsCtx * 2) + planeType) * 42];
        int brTable = Av1FrameContext.CoeffBr[((Math.Min(txsCtx, 3) * 2) + planeType) * 21];
        int baseStride = Av1FrameContext.CoeffBase.Stride, brStride = Av1FrameContext.CoeffBr.Stride;

        // The last nonzero coefficient.
        {
            int c = eob - 1;
            int pos = scan[c];
            int area = width << bhl;
            int ctx = c == 0 ? 0 : c <= area / 8 ? 1 : c <= area / 4 ? 2 : 3;
            int level = Symbol(Av1FrameContext.CoeffBaseEob[(((txsCtx * 2) + planeType) * 4) + ctx], 3) + 1;
            if (level > 2)
            {
                int col = pos >> bhl, row = pos - (col << bhl);
                int brCtx = pos == 0 ? 0 : (txClass == 0 && row < 2 && col < 2) || (txClass == 1 && col == 0) || (txClass == 2 && row == 0) ? 7 : 14;
                level += ReadBaseRange(brTable + (brCtx * brStride));
            }

            levels[pos + ((pos >> bhl) << 2)] = (byte)level;
        }

        if (eob > 1)
        {
            int end = eob - 2;
            sbyte[] nzOffset = Av1Tables.NzMapCtxOffset(txSize);
            int stride = (1 << bhl) + 4;
            for (int c = end; c >= 0; c--)
            {
                int pos = scan[c];
                int p = pos + ((pos >> bhl) << 2);
                int ctx;
                if (txClass == 0)
                {
                    if (c == 0)
                    {
                        ctx = 0;
                    }
                    else
                    {
                        int mag = Math.Min((int)levels[p + stride], 3) + Math.Min((int)levels[p + 1], 3) + Math.Min((int)levels[p + stride + 1], 3)
                            + Math.Min((int)levels[p + (2 * stride)], 3) + Math.Min((int)levels[p + 2], 3);
                        ctx = Math.Min((mag + 1) >> 1, 4) + nzOffset[pos];
                    }
                }
                else
                {
                    int mag = Math.Min((int)levels[p + stride], 3) + Math.Min((int)levels[p + 1], 3);
                    if (txClass == 2)
                    {
                        mag += Math.Min((int)levels[p + 2], 3) + Math.Min((int)levels[p + 3], 3) + Math.Min((int)levels[p + 4], 3);
                    }
                    else
                    {
                        mag += Math.Min((int)levels[p + (2 * stride)], 3) + Math.Min((int)levels[p + (3 * stride)], 3) + Math.Min((int)levels[p + (4 * stride)], 3);
                    }

                    int col = pos >> bhl, row = pos - (col << bhl);
                    ctx = Math.Min((mag + 1) >> 1, 4) + NzMapCtxOffset1d[txClass == 1 ? col : row];
                }

                int level = Symbol(baseTable + (ctx * baseStride), 4);
                if (level > 2)
                {
                    int col = pos >> bhl, row = pos - (col << bhl);
                    int mag = levels[p + 1] + levels[p + stride];
                    mag += txClass switch
                    {
                        0 => levels[p + stride + 1],
                        1 => levels[p + (2 * stride)],
                        _ => levels[p + 2],
                    };
                    mag = Math.Min((mag + 1) >> 1, 6);
                    int brCtx = pos == 0 ? mag
                        : (txClass == 0 && row < 2 && col < 2) || (txClass == 1 && col == 0) || (txClass == 2 && row == 0) ? mag + 7 : mag + 14;
                    level += ReadBaseRange(brTable + (brCtx * brStride));
                }

                levels[p] = (byte)level;
            }
        }

        // Signs, Golomb remainders and dequantization.
        int culLevel = 0, dcValue = 0;
        int pels = Av1Tables.TxSizeWide[txSize] * Av1Tables.TxSizeHigh[txSize];
        int shift = (pels > 256 ? 1 : 0) + (pels > 1024 ? 1 : 0);
        byte[] matrices = Av1Tables.InverseQuantizerMatrices;
        for (int c = 0; c < eob; c++)
        {
            int pos = scan[c];
            int at = pos + ((pos >> bhl) << 2);
            int level = levels[at];
            if (level == 0)
            {
                continue;
            }

            levels[at] = 0;

            int sign = c == 0 ? Symbol(Av1FrameContext.DcSign[(planeType * 3) + dcSignCtx], 2) : _r.Bit();
            if (level >= 15)
            {
                level = (int)((_r.Golomb() + 15) & 0xfffff);
            }

            if (c == 0)
            {
                dcValue = sign != 0 ? -1 : 1;
            }

            level &= 0xfffff;
            culLevel += level;
            int dqv = pos == 0 ? dcQ : acQ;
            if (qmOffset >= 0)
            {
                dqv = ((matrices[qmOffset + pos] * dqv) + 16) >> 5;
            }

            int dq = (int)(((long)level * dqv) & 0xffffff) >> shift;
            _coeffs[pos] = Math.Clamp(sign != 0 ? -dq : dq, minValue, maxValue);
        }

        culLevel = Math.Min(7, culLevel);
        if (dcValue < 0)
        {
            culLevel |= 8;
        }
        else if (dcValue > 0)
        {
            culLevel += 16;
        }

        return (eob, txType, culLevel);
    }

    private int ReadBaseRange(int at)
    {
        int total = 0;
        for (int idx = 0; idx < 12; idx += 3)
        {
            int k = Symbol(at, 4);
            total += k;
            if (k < 3)
            {
                break;
            }
        }

        return total;
    }

    /// <summary>Reads a luma transform block's type into the frame's map (av1_read_tx_type).</summary>
    private void ReadTxType(int originIndex, int txSize)
    {
        Av1BlockInfo b = _b;
        _frame.TxTypeMap[originIndex] = TxType.DctDct;
        if (b.Skip || SegFeatureActive(b.SegmentId, 6) || _header.SegQIdx[b.SegmentId] == 0)
        {
            return;
        }

        bool inter = b.IsInter;
        int setType = Av1Tables.ExtTxSetType(txSize, inter, _header.ReducedTxSet);
        int n = Av1Tables.NumExtTxSet[setType];
        if (n <= 1)
        {
            return;
        }

        int square = Av1Tables.TxSizeSqrMap[txSize];
        int symbol;
        if (inter)
        {
            int eset = InterExtTxSetIndex[setType];
            symbol = Symbol(Av1FrameContext.InterExtTx[((eset - 1) * 4) + square], n);
        }
        else
        {
            int eset = Av1Tables.IntraExtTxSet(setType);
            int mode = b.UseFilterIntra ? Av1Tables.FilterIntraToMode[b.FilterIntraMode] : b.YMode;
            symbol = Symbol(Av1FrameContext.IntraExtTx[((eset - 1) * 52) + (square * 13) + mode], n);
        }

        _frame.TxTypeMap[originIndex] = Av1Tables.ExtTxInverse[(setType * 16) + symbol];
    }

}
