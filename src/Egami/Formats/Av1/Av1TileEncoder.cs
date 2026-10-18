namespace Egami.Formats.Av1;

/// <summary>
/// Decides and reconstructs one tile of a key frame: the partition, each block's modes and each transform block's coefficients,
/// written into the frame's grid and maps and into <see cref="Av1FrameDecisions"/>. Every transform block is predicted and
/// reconstructed with the decoder's own <see cref="Av1Reconstructor"/> as it is decided, so later blocks predict from exactly
/// the samples a decoder will have; <see cref="Av1TileWriter"/> then codes the decisions.
/// </summary>
internal sealed class Av1TileEncoder : Av1TileState
{
    private static readonly int[] LossyModes = [Pred.Dc, Pred.V, Pred.H, Pred.D45, Pred.D135, Pred.D113, Pred.D157, Pred.D203, Pred.D67];
    private static readonly int[] LosslessModes = [Pred.Dc, Pred.Paeth];

    private readonly Av1Reconstructor _reconstructor;
    private readonly Av1EncoderSource _source;
    private readonly Av1FrameDecisions _decisions;
    private readonly Av1EncoderSettings _settings;
    private readonly int[] _residual = new int[64 * 64];
    private readonly int[] _transformed = new int[64 * 64];
    private readonly int[] _coeffs = new int[32 * 32];
    private readonly ushort[][] _saved;

    /// <summary>Starts a tile.</summary>
    internal Av1TileEncoder(Av1Frame frame, int tileRow, int tileCol, Av1EncoderSource source, Av1FrameDecisions decisions, Av1EncoderSettings settings)
        : base(frame, tileRow, tileCol)
    {
        _reconstructor = new Av1Reconstructor(frame);
        _source = source;
        _decisions = decisions;
        _settings = settings;
        _saved = [.. Enumerable.Range(0, _planes).Select(_ => new ushort[128 * 128])];
    }

    /// <summary>Decides and reconstructs the tile.</summary>
    internal void Encode()
    {
        for (int miRow = MiRowStart; miRow < MiRowEnd; miRow += _sbMi)
        {
            _frame.CancellationToken.ThrowIfCancellationRequested();
            ResetLeftContext();
            for (int miCol = MiColStart; miCol < MiColEnd; miCol += _sbMi)
            {
                EncodePartition(miRow, miCol, _frame.SbSize);
            }
        }
    }

    private void EncodePartition(int miRow, int miCol, int bsize)
    {
        if (miRow >= _frame.MiRows || miCol >= _miCols)
        {
            return;
        }

        int hbs = Av1Tables.MiSizeWide[bsize] >> 1;
        bool hasRows = miRow + hbs < _frame.MiRows;
        bool hasCols = miCol + hbs < _miCols;
        int partition = bsize < Bs.B8x8 ? Partition.None
            : bsize > _settings.BlockSize || !hasRows || !hasCols ? Partition.Split : Partition.None;
        int subsize = Av1Tables.PartitionSubsize(bsize, partition);
        if (partition == Partition.Split)
        {
            EncodePartition(miRow, miCol, subsize);
            EncodePartition(miRow, miCol + hbs, subsize);
            EncodePartition(miRow + hbs, miCol, subsize);
            EncodePartition(miRow + hbs, miCol + hbs, subsize);
        }
        else
        {
            EncodeBlock(miRow, miCol, partition, subsize);
        }

        UpdateExtPartitionContext(miRow, miCol, subsize, bsize, partition);
    }

    private void EncodeBlock(int miRow, int miCol, int partition, int bsize)
    {
        int bw = Av1Tables.MiSizeWide[bsize], bh = Av1Tables.MiSizeHigh[bsize];
        int xMis = Math.Min(bw, _miCols - miCol), yMis = Math.Min(bh, _frame.MiRows - miRow);
        Av1BlockInfo b = new() { Size = bsize, Partition = partition, MiRow = miRow, MiCol = miCol, QIndex = _currentQIndex };
        for (int y = 0; y < yMis; y++)
        {
            _frame.Grid.AsSpan(((miRow + y) * _miCols) + miCol, xMis).Fill(b);
        }

        _b = b;
        SetBlockPosition(miRow, miCol, bw, bh);
        bool lossless = _header.LosslessArray[b.SegmentId];
        b.TxSize = lossless ? Tx.T4x4 : LargestTxSize(bsize);
        for (int y = 0; y < yMis; y++)
        {
            _frame.TxSizeMap.AsSpan(((miRow + y) * _miCols) + miCol, xMis).Fill((byte)b.TxSize);
        }

        // Each plane's mode: the candidate whose prediction leaves the smallest residual, each tried through the whole block.
        int[] modes = lossless ? LosslessModes : LossyModes;
        b.YMode = Pred.Dc;
        b.UvMode = Pred.Dc;
        bool chroma = _planes > 1 && _isChromaRef;
        SaveBlock();
        int bestY = Pred.Dc, bestUv = Pred.Dc;
        long bestYCost = long.MaxValue, bestUvCost = long.MaxValue;
        foreach (int mode in modes)
        {
            b.YMode = mode;
            b.UvMode = mode;
            (long yCost, long uvCost, _) = EncodeResidual(bsize, commit: false);
            if (yCost < bestYCost)
            {
                (bestYCost, bestY) = (yCost, mode);
            }

            if (chroma && uvCost < bestUvCost)
            {
                (bestUvCost, bestUv) = (uvCost, mode);
            }

            RestoreBlock();
        }

        b.YMode = bestY;
        b.UvMode = chroma ? bestUv : Pred.Dc;
        (_, _, bool coded) = EncodeResidual(bsize, commit: true);
        b.Skip = !coded;
    }

    /// <summary>
    /// Predicts, transforms, quantizes and reconstructs every transform block of the current block in the decoder's order. Returns
    /// the sum of absolute residuals of luma and of chroma inside the frame, and whether any coefficient is nonzero; when
    /// <paramref name="commit"/> is set, the coefficients and transform types go into the decisions and maps.
    /// </summary>
    private (long YCost, long UvCost, bool Coded) EncodeResidual(int bsize, bool commit)
    {
        Av1BlockInfo b = _b;
        int maxW = MaxBlockWide(bsize, 0), maxH = MaxBlockHigh(bsize, 0);
        int muW = Math.Min(maxW, 16), muH = Math.Min(maxH, 16);
        bool lossless = _header.LosslessArray[b.SegmentId];
        long yCost = 0, uvCost = 0;
        bool coded = false;
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
                            (long cost, bool nonzero) = EncodeTransformBlock(plane, blkRow, blkCol, txSize, (col4 + blkCol) * 4, (row4 + blkRow) * 4, commit);
                            if (plane == 0)
                            {
                                yCost += cost;
                            }
                            else
                            {
                                uvCost += cost;
                            }

                            coded |= nonzero;
                        }
                    }
                }
            }
        }

        return (yCost, uvCost, coded);
    }

    private (long Cost, bool Nonzero) EncodeTransformBlock(int plane, int blkRow, int blkCol, int txSize, int x, int y, bool commit)
    {
        Av1BlockInfo b = _b;
        _reconstructor.PredictIntraBlock(this, plane, x, y, txSize);
        int w = Av1Tables.TxSizeWide[txSize], h = Av1Tables.TxSizeHigh[txSize];
        ushort[] prediction = _frame.Planes![plane];
        int stride = _frame.Strides[plane];
        ushort[] source = _source.Planes[plane];
        int visibleW = Math.Min(w, _source.Widths[plane] - x), visibleH = Math.Min(h, _source.Heights[plane] - y);
        long cost = 0;
        for (int r = 0; r < h; r++)
        {
            int at = ((y + r) * stride) + x;
            for (int c = 0; c < w; c++)
            {
                int d = source[at + c] - prediction[at + c];
                _residual[(r * w) + c] = d;
                if (r < visibleH && c < visibleW)
                {
                    cost += Math.Abs(d);
                }
            }
        }

        bool lossless = _header.LosslessArray[b.SegmentId];
        // Chroma's transform type follows from its mode; luma's is coded.
        int txType = plane == 0 ? TxType.DctDct : GetTxType(1, blkRow, blkCol, txSize);
        Av1ForwardTransform.Forward(_residual, w, _transformed, txSize, txType, lossless);
        int adjusted = Av1Tables.AdjustedTxSize(txSize);
        int codedW = Av1Tables.TxSizeWide[adjusted], codedH = Av1Tables.TxSizeHigh[adjusted];
        int area = codedW * codedH;
        int eob = Quantize(plane, txSize, txType, area, lossless);
        if (commit)
        {
            int[] levels = _decisions.Levels[plane];
            int levelStride = _decisions.Strides[plane];
            int bhl = Av1Tables.TxSizeHighLog2[adjusted];
            for (int c = 0; c < codedW; c++)
            {
                for (int r = 0; r < codedH; r++)
                {
                    levels[((y + r) * levelStride) + x + c] = _transformed[(c << bhl) + r];
                }
            }

            if (plane == 0)
            {
                int originIndex = ((_miRow + blkRow) * _miCols) + _miCol + blkCol;
                _frame.TxTypeMap[originIndex] = (byte)(eob == 0 ? TxType.DctDct : txType);
            }
        }

        _reconstructor.FinishIntraBlock(this, plane, x, y, txSize, txType, eob, _coeffs);
        Array.Clear(_coeffs, 0, area);
        return (cost, eob > 0);
    }

    /// <summary>
    /// Quantizes the transformed block in place into levels, and writes their dequantized values, as the decoder makes them, into
    /// the coefficients; returns the end of block in scan order.
    /// </summary>
    private int Quantize(int plane, int txSize, int txType, int area, bool lossless)
    {
        int bitDepth = _sequence.BitDepth;
        int qindex = _header.SegQIdx[_b.SegmentId];
        (int dcDelta, int acDelta) = plane switch
        {
            0 => (_header.DeltaQYDc, 0),
            1 => (_header.DeltaQUDc, _header.DeltaQUAc),
            _ => (_header.DeltaQVDc, _header.DeltaQVAc),
        };
        int dcQ = DcQ(qindex + dcDelta, bitDepth), acQ = AcQ(qindex + acDelta, bitDepth);
        int pels = Av1Tables.TxSizeWide[txSize] * Av1Tables.TxSizeHigh[txSize];
        int shift = (pels > 256 ? 1 : 0) + (pels > 1024 ? 1 : 0);
        int maxValue = (1 << (7 + bitDepth)) - 1, minValue = -(1 << (7 + bitDepth));
        short[] scan = Av1Tables.ScanOrders[(txSize * 16) + txType];
        int eob = 0;
        for (int c = 0; c < area; c++)
        {
            int pos = scan[c];
            int value = _transformed[pos];
            int dqv = pos == 0 ? dcQ : acQ;
            int level;
            if (lossless)
            {
                level = value / dqv;
            }
            else
            {
                // A dead zone: a third of a step rounds up for AC, half a step for DC.
                long abs = (long)Math.Abs(value) << shift;
                long rounding = pos == 0 ? dqv >> 1 : (dqv * 21) >> 6;
                level = (int)Math.Min((abs + rounding) / dqv, (1 << 20) - 1);
                level = value < 0 ? -level : level;
            }

            _transformed[pos] = level;
            if (level != 0)
            {
                eob = c + 1;
                int dq = (int)(((long)Math.Abs(level) * dqv) & 0xffffff) >> shift;
                _coeffs[pos] = Math.Clamp(level < 0 ? -dq : dq, minValue, maxValue);
            }
        }

        return eob;
    }

    /// <summary>Gets the largest transform of a block that TX_MODE_LARGEST gives.</summary>
    private int LargestTxSize(int bsize)
    {
        int maxRect = Av1Tables.MaxTxSizeRectLookup[bsize];
        return _header.TxMode == Av1TxMode.Only4x4 ? Tx.T4x4 : maxRect;
    }

    private void SaveBlock()
    {
        for (int plane = 0; plane < _planes; plane++)
        {
            (int x, int y, int w, int h) = PlaneArea(plane);
            ushort[] data = _frame.Planes![plane];
            int stride = _frame.Strides[plane];
            for (int r = 0; r < h; r++)
            {
                data.AsSpan(((y + r) * stride) + x, w).CopyTo(_saved[plane].AsSpan(r * w));
            }
        }
    }

    private void RestoreBlock()
    {
        for (int plane = 0; plane < _planes; plane++)
        {
            (int x, int y, int w, int h) = PlaneArea(plane);
            ushort[] data = _frame.Planes![plane];
            int stride = _frame.Strides[plane];
            for (int r = 0; r < h; r++)
            {
                _saved[plane].AsSpan(r * w, w).CopyTo(data.AsSpan(((y + r) * stride) + x));
            }
        }
    }

    /// <summary>Gets the area of a plane the current block's transform blocks cover, in samples.</summary>
    private (int X, int Y, int W, int H) PlaneArea(int plane)
    {
        int ssx = plane > 0 ? _ssx : 0, ssy = plane > 0 ? _ssy : 0;
        (int col4, int row4) = plane == 0 || _isChromaRef ? PlaneOrigin(plane) : (_miCol >> ssx, _miRow >> ssy);
        int w = Math.Max((_bw * 4) >> ssx, 4), h = Math.Max((_bh * 4) >> ssy, 4);
        return (col4 * 4, row4 * 4, w, h);
    }
}
