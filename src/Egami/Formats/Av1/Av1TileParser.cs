namespace Egami.Formats.Av1;

/// <summary>
/// Parses one tile of a frame: the partition tree, each block's mode info, palette color maps and coefficients, as libaom's
/// decodeframe.c, decodemv.c and decodetxb.c read them (BSD 2-clause, see THIRD-PARTY-NOTICES). Blocks go to an
/// <see cref="IAv1Reconstructor"/> as they are read.
/// </summary>
internal sealed partial class Av1TileParser : Av1TileState
{
    private readonly IAv1Reconstructor? _reconstructor;
    private readonly Av1SymbolDecoder _r;
    private readonly int[] _coeffs = new int[32 * 32];
    private readonly byte[] _levels = new byte[((32 + 4) * (32 + 4)) + 16];

    /// <summary>Starts parsing a tile.</summary>
    internal Av1TileParser(Av1Frame frame, int tileRow, int tileCol, byte[] data, int offset, int size, IAv1Reconstructor? reconstructor)
        : base(frame, tileRow, tileCol)
    {
        _reconstructor = reconstructor;
        _r = new Av1SymbolDecoder(data, offset, size, _header.DisableCdfUpdate);
    }

    /// <summary>Parses the tile.</summary>
    internal void Parse()
    {
        int sbIndexRowStride = _frame.SbCols;
        for (int miRow = MiRowStart; miRow < MiRowEnd; miRow += _sbMi)
        {
            _frame.CancellationToken.ThrowIfCancellationRequested();
            ResetLeftContext();
            for (int miCol = MiColStart; miCol < MiColEnd; miCol += _sbMi)
            {
                int start = _r.Tell;
                DecodePartition(miRow, miCol, _frame.SbSize);
                _frame.SuperblockBits[((miRow >> _frame.SbMiLog2) * sbIndexRowStride) + (miCol >> _frame.SbMiLog2)] = _r.Tell - start;
            }
        }

        if (!_r.TrailingBitsValid())
        {
            _frame.TileDataDamaged = true;
        }
    }

    private int Symbol(int at, int n) => _r.Symbol(_cdf, n, at);

    private CorruptDataException Corrupt(string message) => new CorruptDataException(_frame.FormatName, -1, message);

    private void DecodePartition(int miRow, int miCol, int bsize)
    {
        if (miRow >= _frame.MiRows || miCol >= _miCols)
        {
            return;
        }

        int bw = Av1Tables.MiSizeWide[bsize];
        int hbs = bw >> 1;
        int quarter = bw / 4;
        int bsize2 = Av1Tables.PartitionSubsize(bsize, Partition.Split);
        bool hasRows = miRow + hbs < _frame.MiRows;
        bool hasCols = miCol + hbs < _miCols;
        if (bsize == _frame.SbSize)
        {
            ReadRestorationUnits(miRow, miCol, bsize);
        }

        int partition = bsize < Bs.B8x8 ? Partition.None : ReadPartition(miRow, miCol, hasRows, hasCols, bsize);
        int subsize = Av1Tables.PartitionSubsize(bsize, partition);
        if (subsize == Bs.Invalid)
        {
            throw Corrupt("An AV1 partition is invalid for its block size.");
        }

        if (Av1Tables.PlaneBlockSize(subsize, _ssx, _ssy) == Bs.Invalid)
        {
            throw Corrupt("An AV1 block size is invalid with the chroma subsampling.");
        }

        switch (partition)
        {
            case Partition.None:
                DecodeBlock(miRow, miCol, partition, subsize);
                break;
            case Partition.Horz:
                DecodeBlock(miRow, miCol, partition, subsize);
                if (hasRows)
                {
                    DecodeBlock(miRow + hbs, miCol, partition, subsize);
                }

                break;
            case Partition.Vert:
                DecodeBlock(miRow, miCol, partition, subsize);
                if (hasCols)
                {
                    DecodeBlock(miRow, miCol + hbs, partition, subsize);
                }

                break;
            case Partition.Split:
                DecodePartition(miRow, miCol, subsize);
                DecodePartition(miRow, miCol + hbs, subsize);
                DecodePartition(miRow + hbs, miCol, subsize);
                DecodePartition(miRow + hbs, miCol + hbs, subsize);
                break;
            case Partition.HorzA:
                DecodeBlock(miRow, miCol, partition, bsize2);
                DecodeBlock(miRow, miCol + hbs, partition, bsize2);
                DecodeBlock(miRow + hbs, miCol, partition, subsize);
                break;
            case Partition.HorzB:
                DecodeBlock(miRow, miCol, partition, subsize);
                DecodeBlock(miRow + hbs, miCol, partition, bsize2);
                DecodeBlock(miRow + hbs, miCol + hbs, partition, bsize2);
                break;
            case Partition.VertA:
                DecodeBlock(miRow, miCol, partition, bsize2);
                DecodeBlock(miRow + hbs, miCol, partition, bsize2);
                DecodeBlock(miRow, miCol + hbs, partition, subsize);
                break;
            case Partition.VertB:
                DecodeBlock(miRow, miCol, partition, subsize);
                DecodeBlock(miRow, miCol + hbs, partition, bsize2);
                DecodeBlock(miRow + hbs, miCol + hbs, partition, bsize2);
                break;
            case Partition.Horz4:
                for (int i = 0; i < 4; i++)
                {
                    int row = miRow + (i * quarter);
                    if (i > 0 && row >= _frame.MiRows)
                    {
                        break;
                    }

                    DecodeBlock(row, miCol, partition, subsize);
                }

                break;
            default:
                for (int i = 0; i < 4; i++)
                {
                    int col = miCol + (i * quarter);
                    if (i > 0 && col >= _miCols)
                    {
                        break;
                    }

                    DecodeBlock(miRow, col, partition, subsize);
                }

                break;
        }

        UpdateExtPartitionContext(miRow, miCol, subsize, bsize, partition);
    }

    private int ReadPartition(int miRow, int miCol, bool hasRows, bool hasCols, int bsize)
    {
        if (!hasRows && !hasCols)
        {
            return Partition.Split;
        }

        int at = Av1FrameContext.Partition[PartitionContext(miRow, miCol, bsize)];
        if (hasRows && hasCols)
        {
            return Symbol(at, PartitionSymbols(bsize));
        }

        ReadOnlySpan<ushort> gathered = [GatheredPartitionCdf(at, bsize, hasRows), 0];
        int split = _r.SymbolFixed(gathered, 2);
        return split != 0 ? Partition.Split : !hasRows ? Partition.Horz : Partition.Vert;
    }

    private void DecodeBlock(int miRow, int miCol, int partition, int bsize)
    {
        int bw = Av1Tables.MiSizeWide[bsize], bh = Av1Tables.MiSizeHigh[bsize];
        int xMis = Math.Min(bw, _miCols - miCol), yMis = Math.Min(bh, _frame.MiRows - miRow);
        Av1BlockInfo b = new() { Size = bsize, Partition = partition, MiRow = miRow, MiCol = miCol };
        for (int y = 0; y < yMis; y++)
        {
            _frame.Grid.AsSpan(((miRow + y) * _miCols) + miCol, xMis).Fill(b);
        }

        _b = b;
        SetBlockPosition(miRow, miCol, bw, bh);
        if (_header.FrameIsIntra)
        {
            ReadIntraFrameModeInfo(xMis, yMis);
        }
        else
        {
            ReadInterFrameModeInfo(xMis, yMis);
        }
        if (bsize >= Bs.B8x8 && (_ssx != 0 || _ssy != 0) && Av1Tables.PlaneBlockSize(bsize, _ssx, _ssy) == Bs.Invalid)
        {
            throw Corrupt("An AV1 block size is invalid with the chroma subsampling.");
        }

        if (!b.IsInter)
        {
            for (int plane = 0; plane < Math.Min(2, _planes); plane++)
            {
                if ((plane == 0 || _isChromaRef) && b.PaletteSize[plane] != 0)
                {
                    ReadColorMap(plane);
                }
            }
        }

        bool lossless = _header.LosslessArray[b.SegmentId];
        if (_header.TxMode == Av1TxMode.Select && bsize > Bs.B4x4 && !b.Skip && b.IsInter && !lossless)
        {
            int maxTx = Av1Tables.MaxTxSizeRectLookup[bsize];
            int txh = Av1Tables.TxSizeHighUnit[maxTx], txw = Av1Tables.TxSizeWideUnit[maxTx];
            for (int idy = 0; idy < bh; idy += txh)
            {
                for (int idx = 0; idx < bw; idx += txw)
                {
                    ReadVarTxSize(maxTx, 0, idy, idx);
                }
            }
        }
        else
        {
            b.TxSize = ReadTxSize(bsize, b.IsInter, !b.Skip, lossless);
            for (int y = 0; y < yMis; y++)
            {
                _frame.TxSizeMap.AsSpan(((miRow + y) * _miCols) + miCol, xMis).Fill((byte)b.TxSize);
            }

            int txfmW = b.Skip && b.IsInter ? bw * 4 : Av1Tables.TxSizeWide[b.TxSize];
            int txfmH = b.Skip && b.IsInter ? bh * 4 : Av1Tables.TxSizeHigh[b.TxSize];
            _aboveTxfm.AsSpan(miCol - MiColStart, bw).Fill((byte)txfmW);
            _leftTxfm.AsSpan(miRow & MaxMibMask, bh).Fill((byte)txfmH);
        }

        if (b.Skip)
        {
            ResetEntropyContext(bsize);
        }

        DecodeResidual(bsize);
    }

    private void ReadIntraFrameModeInfo(int xMis, int yMis)
    {
        Av1BlockInfo b = _b;
        if (_header.SegIdPreSkip)
        {
            b.SegmentId = ReadIntraSegmentId(xMis, yMis, false);
        }

        b.Skip = ReadSkip(b.SegmentId);
        if (!_header.SegIdPreSkip)
        {
            b.SegmentId = ReadIntraSegmentId(xMis, yMis, b.Skip);
        }

        ReadCdef();
        ReadDeltaQParams();
        b.QIndex = _currentQIndex;
        if (_header.AllowScreenContentTools && _header.AllowIntrabc)
        {
            b.UseIntrabc = Symbol(Av1FrameContext.Intrabc[0], 2) != 0;
            if (b.UseIntrabc)
            {
                b.YMode = Pred.Dc;
                b.UvMode = Pred.Dc;
                ReadIntrabcVector();
                return;
            }
        }

        b.YMode = Symbol(Av1FrameContext.KfYMode[KfYModeContext()], 13);
        bool angleDelta = b.Size >= Bs.B8x8;
        b.AngleDeltaY = angleDelta && Pred.IsDirectional(b.YMode) ? Symbol(Av1FrameContext.AngleDelta[b.YMode - Pred.V], 7) - 3 : 0;
        if (!_sequence.Monochrome && _isChromaRef)
        {
            int cflAllowed = CflAllowed() ? 1 : 0;
            b.UvMode = Symbol(Av1FrameContext.UvMode[(cflAllowed * 13) + b.YMode], 13 + cflAllowed);
            if (b.UvMode == Pred.UvCfl)
            {
                ReadCflAlphas();
            }

            int uvMode = b.UvModeAsY;
            b.AngleDeltaUv = angleDelta && Pred.IsDirectional(uvMode) ? Symbol(Av1FrameContext.AngleDelta[uvMode - Pred.V], 7) - 3 : 0;
        }
        else
        {
            b.UvMode = Pred.Dc;
        }

        if (_header.AllowScreenContentTools && b.Size >= Bs.B8x8 && Av1Tables.BlockSizeWide[b.Size] <= 64 && Av1Tables.BlockSizeHigh[b.Size] <= 64)
        {
            ReadPaletteModeInfo();
        }

        if (_sequence.EnableFilterIntra && b.YMode == Pred.Dc && b.PaletteSize[0] == 0
            && Av1Tables.BlockSizeWide[b.Size] <= 32 && Av1Tables.BlockSizeHigh[b.Size] <= 32)
        {
            b.UseFilterIntra = Symbol(Av1FrameContext.FilterIntra[b.Size], 2) != 0;
            if (b.UseFilterIntra)
            {
                b.FilterIntraMode = Symbol(Av1FrameContext.FilterIntraMode[0], 5);
            }
        }
    }

    private void ReadCflAlphas()
    {
        int signs = Symbol(Av1FrameContext.CflSign[0], 8);
        int signU = ((signs + 1) * 11) >> 5, signV = signs + 1 - (3 * signU);
        int idx = 0;
        if (signU != 0)
        {
            idx = Symbol(Av1FrameContext.CflAlpha[signs + 1 - 3], 16) << 4;
        }

        if (signV != 0)
        {
            idx += Symbol(Av1FrameContext.CflAlpha[(signV * 3) + signU - 3], 16);
        }

        _b.CflAlphaIdx = idx;
        _b.CflAlphaSigns = signs;
    }

    private int ReadIntraSegmentId(int xMis, int yMis, bool skip)
    {
        if (!_header.SegmentationEnabled)
        {
            return 0;
        }

        byte[] map = _frame.SegmentMap;
        (int cdfIndex, int pred) = SegmentPrediction();
        int segment;
        if (skip)
        {
            segment = pred;
        }
        else
        {
            int coded = Symbol(Av1FrameContext.SpatialPredSegTree[cdfIndex], 8);
            segment = NegDeinterleave(coded, pred, _header.LastActiveSegId + 1);

            // libaom rejects a segment past the last active one; dav1d, whose output the gates hold to, takes segment 0.
            if (segment < 0 || segment > _header.LastActiveSegId)
            {
                segment = 0;
            }
        }

        for (int y = 0; y < yMis; y++)
        {
            map.AsSpan(((_miRow + y) * _miCols) + _miCol, xMis).Fill((byte)segment);
        }

        return segment;
    }

    private bool ReadSkip(int segment)
    {
        if (SegFeatureActive(segment, 6))
        {
            return true;
        }

        return Symbol(Av1FrameContext.SkipTxfm[SkipContext()], 2) != 0;
    }

    private void ReadCdef()
    {
        if (_header.CodedLossless || _header.AllowIntrabc)
        {
            return;
        }

        int sbMask = _sbMi - 1;
        if ((_miRow & sbMask) == 0 && (_miCol & sbMask) == 0)
        {
            Array.Clear(_cdefTransmitted);
        }

        int index = _frame.SbSize == Bs.B128x128 ? ((_miCol & 16) != 0 ? 1 : 0) + ((_miRow & 16) != 0 ? 2 : 0) : 0;
        if (!_cdefTransmitted[index] && !_b.Skip)
        {
            // A block wider or taller than 64 sets every 64x64 unit it covers (libaom reads the block at each unit's corner).
            sbyte value = (sbyte)_r.Literal(_header.CdefBits);
            int rows = Math.Max(1, Av1Tables.MiSizeHigh[_b.Size] >> 4), cols = Math.Max(1, Av1Tables.MiSizeWide[_b.Size] >> 4);
            for (int y = 0; y < rows && ((_miRow >> 4) + y) * 16 < _frame.MiRows; y++)
            {
                for (int x = 0; x < cols && ((_miCol >> 4) + x) * 16 < _miCols; x++)
                {
                    _frame.CdefIndex[(((_miRow >> 4) + y) * _frame.CdefStride) + (_miCol >> 4) + x] = value;
                }
            }

            _cdefTransmitted[index] = true;
        }
    }

    private void ReadDeltaQParams()
    {
        if (!_header.DeltaQPresent)
        {
            return;
        }

        int sbMask = _sbMi - 1;
        bool read = (_b.Size != _frame.SbSize || !_b.Skip) && (_miRow & sbMask) == 0 && (_miCol & sbMask) == 0;
        if (read)
        {
            _currentQIndex = Math.Clamp(_currentQIndex + (ReadDelta(Av1FrameContext.DeltaQ[0]) << _header.DeltaQRes), 1, 255);
        }

        if (_header.DeltaLfPresent)
        {
            int count = _header.DeltaLfMulti ? (_planes > 1 ? 4 : 2) : 1;
            for (int i = 0; i < count; i++)
            {
                int at = _header.DeltaLfMulti ? Av1FrameContext.DeltaLfMulti[i] : Av1FrameContext.DeltaLf[0];
                int delta = read ? ReadDelta(at) << _header.DeltaLfRes : 0;
                _b.DeltaLf[i] = _deltaLf[i] = Math.Clamp(_deltaLf[i] + delta, -63, 63);
            }
        }
    }

    private int ReadDelta(int at)
    {
        int abs = Symbol(at, 4);
        if (abs == 3)
        {
            int bits = _r.Literal(3) + 1;
            abs = _r.Literal(bits) + (1 << bits) + 1;
        }

        return abs != 0 && _r.Bit() != 0 ? -abs : abs;
    }

    private void ReadPaletteModeInfo()
    {
        Av1BlockInfo b = _b;
        int bsizeCtx = Av1Tables.NumPelsLog2Lookup[b.Size] - Av1Tables.NumPelsLog2Lookup[Bs.B8x8];
        int bitDepth = _sequence.BitDepth;
        if (b.YMode == Pred.Dc)
        {
            int modeCtx = (_above?.PaletteSize[0] > 0 ? 1 : 0) + (_left?.PaletteSize[0] > 0 ? 1 : 0);
            if (Symbol(Av1FrameContext.PaletteYMode[(bsizeCtx * 3) + modeCtx], 2) != 0)
            {
                b.PaletteSize[0] = Symbol(Av1FrameContext.PaletteYSize[bsizeCtx], 7) + 2;
                ReadPaletteColors(0, bitDepth);
            }
        }

        if (_planes > 1 && b.UvMode == Pred.Dc && _isChromaRef)
        {
            if (Symbol(Av1FrameContext.PaletteUvMode[b.PaletteSize[0] > 0 ? 1 : 0], 2) != 0)
            {
                b.PaletteSize[1] = Symbol(Av1FrameContext.PaletteUvSize[bsizeCtx], 7) + 2;
                ReadPaletteColors(1, bitDepth);
                ReadPaletteColorsV(bitDepth);
            }
        }
    }

    private void ReadPaletteColors(int plane, int bitDepth)
    {
        Span<ushort> cache = stackalloc ushort[16];
        Span<ushort> cached = stackalloc ushort[8];
        int n = _b.PaletteSize[plane];
        ushort[] colors = _b.PaletteColors;
        int baseIndex = plane * 8;
        int cacheCount = PaletteCache(plane, cache);
        int idx = 0;
        for (int i = 0; i < cacheCount && idx < n; i++)
        {
            if (_r.Bit() != 0)
            {
                cached[idx++] = cache[i];
            }
        }

        if (idx < n)
        {
            int cachedCount = idx;
            colors[baseIndex + idx++] = (ushort)_r.Literal(bitDepth);
            if (idx < n)
            {
                int bits = bitDepth - 3 + _r.Literal(2);
                int range = (1 << bitDepth) - colors[baseIndex + idx - 1] - (plane == 0 ? 1 : 0);
                for (; idx < n; idx++)
                {
                    int delta = _r.Literal(bits) + (plane == 0 ? 1 : 0);
                    colors[baseIndex + idx] = (ushort)Math.Clamp(colors[baseIndex + idx - 1] + delta, 0, (1 << bitDepth) - 1);
                    range -= colors[baseIndex + idx] - colors[baseIndex + idx - 1];
                    bits = Math.Min(bits, CeilLog2(range));
                }
            }

            // Merge the sorted cached and transmitted colors.
            if (cachedCount > 0)
            {
                Span<ushort> merged = stackalloc ushort[8];
                int c = 0, t = cachedCount;
                for (int i = 0; i < n; i++)
                {
                    merged[i] = c < cachedCount && (t >= n || cached[c] <= colors[baseIndex + t]) ? cached[c++] : colors[baseIndex + t++];
                }

                merged[..n].CopyTo(colors.AsSpan(baseIndex));
            }
        }
        else
        {
            cached[..n].CopyTo(colors.AsSpan(baseIndex));
        }

        static int CeilLog2(int x) => x < 2 ? 0 : 32 - int.LeadingZeroCount(x - 1);
    }

    private void ReadPaletteColorsV(int bitDepth)
    {
        int n = _b.PaletteSize[1];
        ushort[] colors = _b.PaletteColors;
        if (_r.Bit() != 0)
        {
            int maxValue = 1 << bitDepth;
            int bits = bitDepth - 4 + _r.Literal(2);
            colors[16] = (ushort)_r.Literal(bitDepth);
            for (int i = 1; i < n; i++)
            {
                int delta = _r.Literal(bits);
                if (delta != 0 && _r.Bit() != 0)
                {
                    delta = -delta;
                }

                int value = colors[16 + i - 1] + delta;
                value = value < 0 ? value + maxValue : value >= maxValue ? value - maxValue : value;
                colors[16 + i] = (ushort)value;
            }
        }
        else
        {
            for (int i = 0; i < n; i++)
            {
                colors[16 + i] = (ushort)_r.Literal(bitDepth);
            }
        }
    }

    private void ReadColorMap(int plane)
    {
        int n = _b.PaletteSize[plane];
        (int width, int height, int rows, int cols) = BlockDimensions(plane);
        byte[] map = ColorMaps[plane];
        Av1FrameContext.Table table = plane == 0 ? Av1FrameContext.PaletteYColorIndex : Av1FrameContext.PaletteUvColorIndex;
        Span<byte> order = stackalloc byte[8];
        map[0] = (byte)_r.Uniform(n);
        for (int i = 1; i < rows + cols - 1; i++)
        {
            for (int j = Math.Min(i, cols - 1); j >= Math.Max(0, i - rows + 1); j--)
            {
                int ctx = ColorContext(map, width, i - j, j, n, order);
                int index = Symbol(table[((n - 2) * 5) + ctx], n);
                map[((i - j) * width) + j] = order[index];
            }
        }

        if (cols < width)
        {
            for (int i = 0; i < rows; i++)
            {
                map.AsSpan((i * width) + cols, width - cols).Fill(map[(i * width) + cols - 1]);
            }
        }

        for (int i = rows; i < height; i++)
        {
            map.AsSpan((rows - 1) * width, width).CopyTo(map.AsSpan(i * width));
        }
    }

    private int ReadTxSize(int bsize, bool isInter, bool allowSelectInter, bool lossless)
    {
        if (lossless)
        {
            return Tx.T4x4;
        }

        if (bsize > Bs.B4x4)
        {
            if ((!isInter || allowSelectInter) && _header.TxMode == Av1TxMode.Select)
            {
                int category = Av1Tables.TxSizeDepthCategory[bsize] - 1;
                int maxDepth = Av1Tables.MaxTxDepth[bsize];
                int depth = Symbol(Av1FrameContext.TxSize[(category * 3) + TxSizeContext(bsize)], maxDepth + 1);
                int tx = Av1Tables.MaxTxSizeRectLookup[bsize];
                for (int d = 0; d < depth; d++)
                {
                    tx = Av1Tables.SubTxSizeMap[tx];
                }

                return tx;
            }

            int largest = _header.TxMode == Av1TxMode.Only4x4 ? Tx.T4x4 : Tx.T64x64;
            int maxRect = Av1Tables.MaxTxSizeRectLookup[bsize];
            return Av1Tables.TxSizeSqrMap[maxRect] <= largest ? maxRect : largest;
        }

        return Av1Tables.MaxTxSizeRectLookup[bsize];
    }

    private void ReadVarTxSize(int txSize, int depth, int blkRow, int blkCol)
    {
        int bsize = _b.Size;
        if (blkRow >= MaxBlockHigh(bsize, 0) || blkCol >= MaxBlockWide(bsize, 0))
        {
            return;
        }

        if (depth == 2)
        {
            SetVarTxSize(txSize, txSize, blkRow, blkCol);
            return;
        }

        int ctx = TxfmPartitionContext(blkRow, blkCol, bsize, txSize);
        if (Symbol(Av1FrameContext.TxfmPartition[ctx], 2) != 0)
        {
            int sub = Av1Tables.SubTxSizeMap[txSize];
            if (sub == Tx.T4x4)
            {
                SetVarTxSize(sub, txSize, blkRow, blkCol);
                return;
            }

            int bsw = Av1Tables.TxSizeWideUnit[sub], bsh = Av1Tables.TxSizeHighUnit[sub];
            for (int row = 0; row < Av1Tables.TxSizeHighUnit[txSize]; row += bsh)
            {
                for (int col = 0; col < Av1Tables.TxSizeWideUnit[txSize]; col += bsw)
                {
                    ReadVarTxSize(sub, depth + 1, blkRow + row, blkCol + col);
                }
            }
        }
        else
        {
            SetVarTxSize(txSize, txSize, blkRow, blkCol);
        }
    }

    /// <summary>Records a transform block of the transform tree: its size over the area of <paramref name="area"/> (set_inter_tx_size, txfm_partition_update).</summary>
    private void SetVarTxSize(int txSize, int area, int blkRow, int blkCol)
    {
        _b.TxSize = txSize;
        int rows = Math.Min(Av1Tables.TxSizeHighUnit[area], _frame.MiRows - _miRow - blkRow);
        int cols = Math.Min(Av1Tables.TxSizeWideUnit[area], _miCols - _miCol - blkCol);
        for (int y = 0; y < rows; y++)
        {
            _frame.TxSizeMap.AsSpan(((_miRow + blkRow + y) * _miCols) + _miCol + blkCol, cols).Fill((byte)txSize);
        }

        int bsize = Av1Tables.TxSizeToBlockSize[area];
        _leftTxfm.AsSpan((_miRow & MaxMibMask) + blkRow, Av1Tables.MiSizeHigh[bsize]).Fill((byte)Av1Tables.TxSizeHigh[txSize]);
        _aboveTxfm.AsSpan(_miCol - MiColStart + blkCol, Av1Tables.MiSizeWide[bsize]).Fill((byte)Av1Tables.TxSizeWide[txSize]);
    }

}
