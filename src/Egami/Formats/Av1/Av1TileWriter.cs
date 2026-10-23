namespace Egami.Formats.Av1;

/// <summary>
/// What an encoder decided for a frame beyond the frame's own maps: each plane's quantized coefficients, at the positions of their
/// transform blocks (coefficient row r and column c of a block at x, y sit at x + c, y + r of the plane; a side of 64 keeps 32), and
/// the color index maps of palette blocks.
/// </summary>
internal sealed class Av1FrameDecisions
{
    /// <summary>Starts the decisions of a frame.</summary>
    internal Av1FrameDecisions(Av1Frame frame)
    {
        int planes = frame.Header.NumPlanes;
        Levels = new int[planes][];
        Strides = new int[planes];
        for (int plane = 0; plane < planes; plane++)
        {
            Strides[plane] = frame.Strides[plane];
            Levels[plane] = new int[frame.Planes![plane].Length];
        }
    }

    /// <summary>Gets each plane's quantized coefficients.</summary>
    internal int[][] Levels { get; }

    /// <summary>Gets each plane's stride in <see cref="Levels"/>.</summary>
    internal int[] Strides { get; }

    /// <summary>Gets the color index maps of the palette blocks: luma and chroma, row by row at the plane block's width.</summary>
    internal Dictionary<Av1BlockInfo, byte[][]> ColorMaps { get; } = new(ReferenceEqualityComparer.Instance);
}

/// <summary>
/// The tile syntax an encoder writes: every symbol <see cref="Av1TileParser"/> reads, in its order, from the frame's decisions (its
/// block grid, segment, transform and CDEF maps and loop restoration units, and <see cref="Av1FrameDecisions"/>), with the
/// contexts of the <see cref="Av1TileState"/> both share (libaom's bitstream.c and encodetxb.c write the same syntax; BSD
/// 2-clause, see THIRD-PARTY-NOTICES). Each part goes to an <see cref="IAv1SymbolSink"/>: the range encoder when writing, or a
/// cost counter when the encoder weighs a choice, so the rate a search estimates is the rate it would write.
/// </summary>
internal partial class Av1TileCoder : Av1TileState
{
    private protected readonly Av1FrameDecisions _decisions;
    private readonly int[] _coeffs = new int[32 * 32];
    private readonly byte[] _levels = new byte[((32 + 4) * (32 + 4)) + 16];

    /// <summary>Starts the syntax of a tile.</summary>
    internal Av1TileCoder(Av1Frame frame, int tileRow, int tileCol, Av1FrameDecisions decisions)
        : base(frame, tileRow, tileCol)
    {
        _decisions = decisions;
    }

    /// <summary>Codes every superblock of the tile.</summary>
    private protected void CodeTile<TSink>(ref TSink s)
        where TSink : struct, IAv1SymbolSink
    {
        for (int miRow = MiRowStart; miRow < MiRowEnd; miRow += _sbMi)
        {
            _frame.CancellationToken.ThrowIfCancellationRequested();
            ResetLeftContext();
            for (int miCol = MiColStart; miCol < MiColEnd; miCol += _sbMi)
            {
                CodePartition(ref s, miRow, miCol, _frame.SbSize);
            }
        }
    }

    /// <summary>Gets the partition the grid shows for a square block: the partition of its first block when that block is the first one the partition makes, else split.</summary>
    private protected int PartitionAt(int miRow, int miCol, int bsize)
    {
        Av1BlockInfo b = _frame.Grid[(miRow * _miCols) + miCol] ?? throw new InvalidOperationException("The encoder left a block of the frame undecided.");
        int partition = b.Partition;
        int first = partition is Partition.HorzA or Partition.VertA ? Av1Tables.PartitionSubsize(bsize, Partition.Split)
            : partition == Partition.Split ? Bs.Invalid : Av1Tables.PartitionSubsize(bsize, partition);
        return b.Size == first && b.MiRow == miRow && b.MiCol == miCol ? partition : Partition.Split;
    }

    private protected void CodePartition<TSink>(ref TSink s, int miRow, int miCol, int bsize)
        where TSink : struct, IAv1SymbolSink
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
            CodeRestorationUnits(ref s, miRow, miCol, bsize);
        }

        int partition = bsize < Bs.B8x8 ? Partition.None : PartitionAt(miRow, miCol, bsize);
        if (bsize >= Bs.B8x8)
        {
            CodePartitionSymbol(ref s, miRow, miCol, hasRows, hasCols, bsize, partition);
        }

        int subsize = Av1Tables.PartitionSubsize(bsize, partition);
        switch (partition)
        {
            case Partition.None:
                CodeBlock(ref s, miRow, miCol, subsize);
                break;
            case Partition.Horz:
                CodeBlock(ref s, miRow, miCol, subsize);
                if (hasRows)
                {
                    CodeBlock(ref s, miRow + hbs, miCol, subsize);
                }

                break;
            case Partition.Vert:
                CodeBlock(ref s, miRow, miCol, subsize);
                if (hasCols)
                {
                    CodeBlock(ref s, miRow, miCol + hbs, subsize);
                }

                break;
            case Partition.Split:
                CodePartition(ref s, miRow, miCol, subsize);
                CodePartition(ref s, miRow, miCol + hbs, subsize);
                CodePartition(ref s, miRow + hbs, miCol, subsize);
                CodePartition(ref s, miRow + hbs, miCol + hbs, subsize);
                break;
            case Partition.HorzA:
                CodeBlock(ref s, miRow, miCol, bsize2);
                CodeBlock(ref s, miRow, miCol + hbs, bsize2);
                CodeBlock(ref s, miRow + hbs, miCol, subsize);
                break;
            case Partition.HorzB:
                CodeBlock(ref s, miRow, miCol, subsize);
                CodeBlock(ref s, miRow + hbs, miCol, bsize2);
                CodeBlock(ref s, miRow + hbs, miCol + hbs, bsize2);
                break;
            case Partition.VertA:
                CodeBlock(ref s, miRow, miCol, bsize2);
                CodeBlock(ref s, miRow + hbs, miCol, bsize2);
                CodeBlock(ref s, miRow, miCol + hbs, subsize);
                break;
            case Partition.VertB:
                CodeBlock(ref s, miRow, miCol, subsize);
                CodeBlock(ref s, miRow, miCol + hbs, bsize2);
                CodeBlock(ref s, miRow + hbs, miCol + hbs, bsize2);
                break;
            case Partition.Horz4:
                for (int i = 0; i < 4; i++)
                {
                    int row = miRow + (i * quarter);
                    if (i > 0 && row >= _frame.MiRows)
                    {
                        break;
                    }

                    CodeBlock(ref s, row, miCol, subsize);
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

                    CodeBlock(ref s, miRow, col, subsize);
                }

                break;
        }

        UpdateExtPartitionContext(miRow, miCol, subsize, bsize, partition);
    }

    private protected void CodePartitionSymbol<TSink>(ref TSink s, int miRow, int miCol, bool hasRows, bool hasCols, int bsize, int partition)
        where TSink : struct, IAv1SymbolSink
    {
        if (!hasRows && !hasCols)
        {
            if (partition != Partition.Split)
            {
                throw new InvalidOperationException("A block past the frame's bottom right corner must be split.");
            }

            return;
        }

        int at = Av1FrameContext.Partition[PartitionContext(miRow, miCol, bsize)];
        if (hasRows && hasCols)
        {
            s.Symbol(_cdf, at, PartitionSymbols(bsize), partition);
            return;
        }

        int other = !hasRows ? Partition.Horz : Partition.Vert;
        if (partition != Partition.Split && partition != other)
        {
            throw new InvalidOperationException("A block across the frame's edge must split or be halved along the edge.");
        }

        ReadOnlySpan<ushort> gathered = [GatheredPartitionCdf(at, bsize, hasRows), 0];
        s.SymbolFixed(gathered, 2, partition == Partition.Split ? 1 : 0);
    }

    private protected void CodeBlock<TSink>(ref TSink s, int miRow, int miCol, int bsize)
        where TSink : struct, IAv1SymbolSink
    {
        Av1BlockInfo b = _frame.Grid[(miRow * _miCols) + miCol]!;
        if (b.MiRow != miRow || b.MiCol != miCol || b.Size != bsize)
        {
            throw new InvalidOperationException($"The grid holds a {b.Size} block at {b.MiRow}, {b.MiCol} where the partition makes a {bsize} block at {miRow}, {miCol}.");
        }

        int bw = Av1Tables.MiSizeWide[bsize], bh = Av1Tables.MiSizeHigh[bsize];
        _b = b;
        SetBlockPosition(miRow, miCol, bw, bh);
        CodeModeInfo(ref s);
        if (!b.IsInter)
        {
            for (int plane = 0; plane < Math.Min(2, _planes); plane++)
            {
                if ((plane == 0 || _isChromaRef) && b.PaletteSize[plane] != 0)
                {
                    CodeColorMap(ref s, plane);
                }
            }
        }

        bool lossless = _header.LosslessArray[b.SegmentId];
        int xMis = Math.Min(bw, _miCols - miCol), yMis = Math.Min(bh, _frame.MiRows - miRow);
        if (_header.TxMode == Av1TxMode.Select && bsize > Bs.B4x4 && !b.Skip && b.IsInter && !lossless)
        {
            CodeVarTxTree(ref s, bsize);
        }
        else
        {
            CodeTxSize(ref s, bsize, b.IsInter, !b.Skip, lossless);
            int txfmW = b.Skip && b.IsInter ? bw * 4 : Av1Tables.TxSizeWide[b.TxSize];
            int txfmH = b.Skip && b.IsInter ? bh * 4 : Av1Tables.TxSizeHigh[b.TxSize];
            _aboveTxfm.AsSpan(miCol - MiColStart, bw).Fill((byte)txfmW);
            _leftTxfm.AsSpan(miRow & MaxMibMask, bh).Fill((byte)txfmH);
        }

        if (b.Skip)
        {
            ResetEntropyContext(bsize);
        }

        CodeResidual(ref s, bsize, xMis, yMis);
    }

    private protected void CodeIntraFrameModeInfo<TSink>(ref TSink s)
        where TSink : struct, IAv1SymbolSink
    {
        Av1BlockInfo b = _b;
        if (_header.SegIdPreSkip)
        {
            CodeIntraSegmentId(ref s, false);
        }

        if (!SegFeatureActive(b.SegmentId, 6))
        {
            s.Symbol(_cdf, Av1FrameContext.SkipTxfm[SkipContext()], 2, b.Skip ? 1 : 0);
        }
        else if (!b.Skip)
        {
            throw new InvalidOperationException("A block of a skip segment must skip its residual.");
        }

        if (!_header.SegIdPreSkip)
        {
            CodeIntraSegmentId(ref s, b.Skip);
        }

        CodeCdef(ref s);
        CodeDeltaQParams(ref s);
        if (_header.AllowScreenContentTools && _header.AllowIntrabc)
        {
            s.Symbol(_cdf, Av1FrameContext.Intrabc[0], 2, b.UseIntrabc ? 1 : 0);
            if (b.UseIntrabc)
            {
                CodeIntrabcVector(ref s);
                return;
            }
        }
        else if (b.UseIntrabc)
        {
            throw new InvalidOperationException("Intra block copy needs allow_intrabc.");
        }

        CodeIntraModes(ref s, Av1FrameContext.KfYMode[KfYModeContext()]);
    }

    /// <summary>Codes an intra block's modes from its luma mode on: angles, chroma, palettes and filter intra, the luma mode from <paramref name="yModeAt"/>.</summary>
    private protected void CodeIntraModes<TSink>(ref TSink s, int yModeAt)
        where TSink : struct, IAv1SymbolSink
    {
        Av1BlockInfo b = _b;
        s.Symbol(_cdf, yModeAt, 13, b.YMode);
        bool angleDelta = b.Size >= Bs.B8x8;
        if (angleDelta && Pred.IsDirectional(b.YMode))
        {
            s.Symbol(_cdf, Av1FrameContext.AngleDelta[b.YMode - Pred.V], 7, b.AngleDeltaY + 3);
        }

        if (!_sequence.Monochrome && _isChromaRef)
        {
            int cflAllowed = CflAllowed() ? 1 : 0;
            s.Symbol(_cdf, Av1FrameContext.UvMode[(cflAllowed * 13) + b.YMode], 13 + cflAllowed, b.UvMode);
            if (b.UvMode == Pred.UvCfl)
            {
                CodeCflAlphas(ref s);
            }

            int uvMode = b.UvModeAsY;
            if (angleDelta && Pred.IsDirectional(uvMode))
            {
                s.Symbol(_cdf, Av1FrameContext.AngleDelta[uvMode - Pred.V], 7, b.AngleDeltaUv + 3);
            }
        }

        if (_header.AllowScreenContentTools && b.Size >= Bs.B8x8 && Av1Tables.BlockSizeWide[b.Size] <= 64 && Av1Tables.BlockSizeHigh[b.Size] <= 64)
        {
            CodePaletteModeInfo(ref s);
        }

        if (_sequence.EnableFilterIntra && b.YMode == Pred.Dc && b.PaletteSize[0] == 0
            && Av1Tables.BlockSizeWide[b.Size] <= 32 && Av1Tables.BlockSizeHigh[b.Size] <= 32)
        {
            s.Symbol(_cdf, Av1FrameContext.FilterIntra[b.Size], 2, b.UseFilterIntra ? 1 : 0);
            if (b.UseFilterIntra)
            {
                s.Symbol(_cdf, Av1FrameContext.FilterIntraMode[0], 5, b.FilterIntraMode);
            }
        }
    }

    private protected void CodeCflAlphas<TSink>(ref TSink s)
        where TSink : struct, IAv1SymbolSink
    {
        int signs = _b.CflAlphaSigns;
        s.Symbol(_cdf, Av1FrameContext.CflSign[0], 8, signs);
        int signU = ((signs + 1) * 11) >> 5, signV = signs + 1 - (3 * signU);
        if (signU != 0)
        {
            s.Symbol(_cdf, Av1FrameContext.CflAlpha[signs + 1 - 3], 16, _b.CflAlphaIdx >> 4);
        }

        if (signV != 0)
        {
            s.Symbol(_cdf, Av1FrameContext.CflAlpha[(signV * 3) + signU - 3], 16, _b.CflAlphaIdx & 15);
        }
    }

    private protected void CodeIntraSegmentId<TSink>(ref TSink s, bool skip)
        where TSink : struct, IAv1SymbolSink
    {
        if (!_header.SegmentationEnabled || skip)
        {
            return;
        }

        (int cdfIndex, int pred) = SegmentPrediction();
        int coded = NegInterleave(_b.SegmentId, pred, _header.LastActiveSegId + 1);
        s.Symbol(_cdf, Av1FrameContext.SpatialPredSegTree[cdfIndex], 8, coded);
    }

    /// <summary>neg_interleave: the inverse of <see cref="Av1TileState.NegDeinterleave"/>.</summary>
    private static int NegInterleave(int x, int reference, int max)
    {
        if (reference == 0)
        {
            return x;
        }

        if (reference >= max - 1)
        {
            return -x + max - 1;
        }

        int diff = x - reference;
        if (2 * reference < max)
        {
            if (Math.Abs(diff) <= reference)
            {
                return diff > 0 ? (diff << 1) - 1 : (-diff) << 1;
            }

            return x;
        }

        if (Math.Abs(diff) < max - reference)
        {
            return diff > 0 ? (diff << 1) - 1 : (-diff) << 1;
        }

        return -x + max - 1;
    }

    private protected void CodeCdef<TSink>(ref TSink s)
        where TSink : struct, IAv1SymbolSink
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
            int value = _frame.CdefIndex[((_miRow >> 4) * _frame.CdefStride) + (_miCol >> 4)];
            s.Literal(Math.Max(value, 0), _header.CdefBits);
            _cdefTransmitted[index] = true;
        }
    }

    private protected void CodeDeltaQParams<TSink>(ref TSink s)
        where TSink : struct, IAv1SymbolSink
    {
        if (!_header.DeltaQPresent)
        {
            return;
        }

        int sbMask = _sbMi - 1;
        bool write = (_b.Size != _frame.SbSize || !_b.Skip) && (_miRow & sbMask) == 0 && (_miCol & sbMask) == 0;
        if (write)
        {
            int delta = _b.QIndex - _currentQIndex;
            if (delta % (1 << _header.DeltaQRes) != 0)
            {
                throw new InvalidOperationException("A block's quantizer is not a whole number of delta_q_res steps from the one before.");
            }

            CodeDelta(ref s, Av1FrameContext.DeltaQ[0], delta >> _header.DeltaQRes);
            _currentQIndex = _b.QIndex;
        }
        else if (_b.QIndex != _currentQIndex)
        {
            throw new InvalidOperationException("A block's quantizer changes where no delta is coded.");
        }

        if (_header.DeltaLfPresent)
        {
            int count = _header.DeltaLfMulti ? (_planes > 1 ? 4 : 2) : 1;
            for (int i = 0; i < count; i++)
            {
                int at = _header.DeltaLfMulti ? Av1FrameContext.DeltaLfMulti[i] : Av1FrameContext.DeltaLf[0];
                if (write)
                {
                    int delta = _b.DeltaLf[i] - _deltaLf[i];
                    CodeDelta(ref s, at, delta >> _header.DeltaLfRes);
                }

                _deltaLf[i] = _b.DeltaLf[i];
            }
        }
    }

    private protected void CodeDelta<TSink>(ref TSink s, int at, int delta)
        where TSink : struct, IAv1SymbolSink
    {
        int abs = Math.Abs(delta);
        s.Symbol(_cdf, at, 4, Math.Min(abs, 3));
        if (abs >= 3)
        {
            // abs = Literal(bits) + (1 << bits) + 1, with bits - 1 in three bits.
            int rem = abs - 1;
            int bits = 31 - int.LeadingZeroCount(rem);
            s.Literal(bits - 1, 3);
            s.Literal(rem - (1 << bits), bits);
        }

        if (abs != 0)
        {
            s.Bit(delta < 0 ? 1 : 0);
        }
    }

    private protected void CodeIntrabcVector<TSink>(ref TSink s)
        where TSink : struct, IAv1SymbolSink
    {
        (int refRow, int refCol) = IntrabcReference();
        int row = _b.MvRow - refRow, col = _b.MvCol - refCol;
        Av1FrameContext.MvTables t = Av1FrameContext.Dv;
        int joint = (row != 0 ? 2 : 0) | (col != 0 ? 1 : 0);
        s.Symbol(_cdf, t.Joints[0], 4, joint);
        if (row != 0)
        {
            CodeVectorComponent(ref s, t, 0, row);
        }

        if (col != 0)
        {
            CodeVectorComponent(ref s, t, 1, col);
        }
    }

    private protected void CodeVectorComponent<TSink>(ref TSink s, Av1FrameContext.MvTables t, int comp, int value)
        where TSink : struct, IAv1SymbolSink
    {
        // An integer vector: the magnitude in whole samples, coded as class and offset; the fraction and high precision bits
        // take their defaults.
        int sign = value < 0 ? 1 : 0;
        int mag = Math.Abs(value) - 1;
        if ((mag & 7) != 7)
        {
            throw new InvalidOperationException("An intra block copy vector must be a whole number of samples.");
        }

        int offset = mag >> 3;
        int mvClass = offset < 2 ? 0 : Math.Min(10, 31 - int.LeadingZeroCount(offset));
        s.Symbol(_cdf, t.Sign[comp], 2, sign);
        s.Symbol(_cdf, t.Classes[comp], 11, mvClass);
        if (mvClass == 0)
        {
            s.Symbol(_cdf, t.Class0[comp], 2, offset);
        }
        else
        {
            int d = offset - (1 << mvClass);
            for (int i = 0; i < mvClass; i++)
            {
                s.Symbol(_cdf, t.Bits[(comp * 10) + i], 2, (d >> i) & 1);
            }
        }
    }

    private protected void CodePaletteModeInfo<TSink>(ref TSink s)
        where TSink : struct, IAv1SymbolSink
    {
        Av1BlockInfo b = _b;
        int bsizeCtx = Av1Tables.NumPelsLog2Lookup[b.Size] - Av1Tables.NumPelsLog2Lookup[Bs.B8x8];
        int bitDepth = _sequence.BitDepth;
        if (b.YMode == Pred.Dc)
        {
            int modeCtx = (_above?.PaletteSize[0] > 0 ? 1 : 0) + (_left?.PaletteSize[0] > 0 ? 1 : 0);
            s.Symbol(_cdf, Av1FrameContext.PaletteYMode[(bsizeCtx * 3) + modeCtx], 2, b.PaletteSize[0] > 0 ? 1 : 0);
            if (b.PaletteSize[0] > 0)
            {
                s.Symbol(_cdf, Av1FrameContext.PaletteYSize[bsizeCtx], 7, b.PaletteSize[0] - 2);
                CodePaletteColors(ref s, 0, bitDepth);
            }
        }

        if (_planes > 1 && b.UvMode == Pred.Dc && _isChromaRef)
        {
            s.Symbol(_cdf, Av1FrameContext.PaletteUvMode[b.PaletteSize[0] > 0 ? 1 : 0], 2, b.PaletteSize[1] > 0 ? 1 : 0);
            if (b.PaletteSize[1] > 0)
            {
                s.Symbol(_cdf, Av1FrameContext.PaletteUvSize[bsizeCtx], 7, b.PaletteSize[1] - 2);
                CodePaletteColors(ref s, 1, bitDepth);
                CodePaletteColorsV(ref s, bitDepth);
            }
        }
    }

    /// <summary>Writes a palette's sorted colors: a bit for each cached color it reuses, then the others as deltas (write_palette_colors_y/uv).</summary>
    private protected void CodePaletteColors<TSink>(ref TSink s, int plane, int bitDepth)
        where TSink : struct, IAv1SymbolSink
    {
        Span<ushort> cache = stackalloc ushort[16];
        int n = _b.PaletteSize[plane];
        ReadOnlySpan<ushort> colors = _b.PaletteColors.AsSpan(plane * 8, n);
        int cacheCount = PaletteCache(plane, cache);
        Span<ushort> rest = stackalloc ushort[8];
        int restCount = 0, found = 0;
        Span<bool> used = stackalloc bool[8];
        for (int i = 0; i < cacheCount && found < n; i++)
        {
            int index = colors.IndexOf(cache[i]);
            bool hit = index >= 0 && !used[index];
            s.Bit(hit ? 1 : 0);
            if (hit)
            {
                used[index] = true;
                found++;
            }
        }

        for (int i = 0; i < n; i++)
        {
            if (!used[i])
            {
                rest[restCount++] = colors[i];
            }
        }

        if (restCount == 0)
        {
            return;
        }

        s.Literal(rest[0], bitDepth);
        if (restCount == 1)
        {
            return;
        }

        // The deltas between the remaining colors, in the fewest bits that hold the largest (delta_encode_palette_colors).
        int minDelta = plane == 0 ? 1 : 0;
        int maxDelta = 0;
        for (int i = 1; i < restCount; i++)
        {
            maxDelta = Math.Max(maxDelta, rest[i] - rest[i - 1] - minDelta);
        }

        int minBits = bitDepth - 3;
        int bits = Math.Max(CeilLog2(maxDelta + 1), minBits);
        if (bits > minBits + 3)
        {
            throw new InvalidOperationException("Palette colors too far apart to code.");
        }

        s.Literal(bits - minBits, 2);
        int range = (1 << bitDepth) - rest[0] - minDelta;
        for (int i = 1; i < restCount; i++)
        {
            int delta = rest[i] - rest[i - 1] - minDelta;
            s.Literal(delta, bits);
            range -= delta + minDelta;
            bits = Math.Min(bits, CeilLog2(range));
        }

        static int CeilLog2(int x) => x < 2 ? 0 : 32 - int.LeadingZeroCount(x - 1);
    }

    /// <summary>Writes the V palette: deltas when they take fewer bits than the raw colors (write_palette_colors_uv's V part).</summary>
    private protected void CodePaletteColorsV<TSink>(ref TSink s, int bitDepth)
        where TSink : struct, IAv1SymbolSink
    {
        int n = _b.PaletteSize[1];
        ReadOnlySpan<ushort> colors = _b.PaletteColors.AsSpan(16, n);
        int maxValue = 1 << bitDepth;
        int minBits = bitDepth - 4;
        int maxDelta = 0;
        Span<int> deltas = stackalloc int[8];
        for (int i = 1; i < n; i++)
        {
            int delta = colors[i] - colors[i - 1];
            if (Math.Abs(delta) > maxValue / 2)
            {
                delta = delta > 0 ? delta - maxValue : delta + maxValue;
            }

            deltas[i] = delta;
            maxDelta = Math.Max(maxDelta, Math.Abs(delta));
        }

        int bits = Math.Max(32 - int.LeadingZeroCount(maxDelta), minBits);
        int zeroCount = 0;
        for (int i = 1; i < n; i++)
        {
            zeroCount += deltas[i] == 0 ? 1 : 0;
        }

        int deltaBits = 2 + bitDepth + ((bits + 1) * (n - 1)) - zeroCount;
        int rawBits = bitDepth * n;
        bool useDelta = bits <= minBits + 3 && deltaBits < rawBits;
        s.Bit(useDelta ? 1 : 0);
        if (useDelta)
        {
            s.Literal(bits - minBits, 2);
            s.Literal(colors[0], bitDepth);
            for (int i = 1; i < n; i++)
            {
                s.Literal(Math.Abs(deltas[i]), bits);
                if (deltas[i] != 0)
                {
                    s.Bit(deltas[i] < 0 ? 1 : 0);
                }
            }
        }
        else
        {
            for (int i = 0; i < n; i++)
            {
                s.Literal(colors[i], bitDepth);
            }
        }
    }

    /// <summary>Gets the current block's color index map in a plane (0 for luma, 1 for chroma).</summary>
    private protected virtual byte[] ColorMap(int plane) => _decisions.ColorMaps[_b][plane];

    private protected void CodeColorMap<TSink>(ref TSink s, int plane)
        where TSink : struct, IAv1SymbolSink
    {
        int n = _b.PaletteSize[plane];
        (int width, _, int rows, int cols) = BlockDimensions(plane);
        byte[] map = ColorMap(plane);
        Av1FrameContext.Table table = plane == 0 ? Av1FrameContext.PaletteYColorIndex : Av1FrameContext.PaletteUvColorIndex;
        Span<byte> order = stackalloc byte[8];
        s.Uniform(n, map[0]);
        for (int i = 1; i < rows + cols - 1; i++)
        {
            for (int j = Math.Min(i, cols - 1); j >= Math.Max(0, i - rows + 1); j--)
            {
                int ctx = ColorContext(map, width, i - j, j, n, order);
                int color = map[((i - j) * width) + j];
                s.Symbol(_cdf, table[((n - 2) * 5) + ctx], n, order[..n].IndexOf((byte)color));
            }
        }
    }

    /// <summary>
    /// Writes an inter block's transform tree (read_var_tx_size) down to the block's transform size, which the encoder keeps the
    /// same over the block, each node setting the transform contexts as the decoder does.
    /// </summary>
    private protected void CodeVarTxTree<TSink>(ref TSink s, int bsize)
        where TSink : struct, IAv1SymbolSink
    {
        int maxTx = Av1Tables.MaxTxSizeRectLookup[bsize];
        int txh = Av1Tables.TxSizeHighUnit[maxTx], txw = Av1Tables.TxSizeWideUnit[maxTx];
        for (int idy = 0; idy < _bh; idy += txh)
        {
            for (int idx = 0; idx < _bw; idx += txw)
            {
                CodeVarTxNode(ref s, maxTx, 0, idy, idx);
            }
        }
    }

    private void CodeVarTxNode<TSink>(ref TSink s, int txSize, int depth, int blkRow, int blkCol)
        where TSink : struct, IAv1SymbolSink
    {
        int bsize = _b.Size;
        if (blkRow >= MaxBlockHigh(bsize, 0) || blkCol >= MaxBlockWide(bsize, 0))
        {
            return;
        }

        int target = _b.TxSize;
        if (depth == 2)
        {
            Check(txSize);
            SetVarTxSize(txSize, txSize, blkRow, blkCol);
            return;
        }

        bool split = txSize != target;
        s.Symbol(_cdf, Av1FrameContext.TxfmPartition[TxfmPartitionContext(blkRow, blkCol, bsize, txSize)], 2, split ? 1 : 0);
        if (!split)
        {
            SetVarTxSize(txSize, txSize, blkRow, blkCol);
            return;
        }

        int sub = Av1Tables.SubTxSizeMap[txSize];
        if (sub == Tx.T4x4)
        {
            Check(sub);
            SetVarTxSize(sub, txSize, blkRow, blkCol);
            return;
        }

        int bsw = Av1Tables.TxSizeWideUnit[sub], bsh = Av1Tables.TxSizeHighUnit[sub];
        for (int row = 0; row < Av1Tables.TxSizeHighUnit[txSize]; row += bsh)
        {
            for (int col = 0; col < Av1Tables.TxSizeWideUnit[txSize]; col += bsw)
            {
                CodeVarTxNode(ref s, sub, depth + 1, blkRow + row, blkCol + col);
            }
        }

        void Check(int tx)
        {
            if (tx != target)
            {
                throw new InvalidOperationException($"An inter block's transform size {target} is not in its transform tree.");
            }
        }
    }

    private protected void CodeTxSize<TSink>(ref TSink s, int bsize, bool isInter, bool allowSelectInter, bool lossless)
        where TSink : struct, IAv1SymbolSink
    {
        Av1BlockInfo b = _b;
        if (lossless)
        {
            Check(Tx.T4x4);
            return;
        }

        if (bsize > Bs.B4x4)
        {
            if ((!isInter || allowSelectInter) && _header.TxMode == Av1TxMode.Select)
            {
                int category = Av1Tables.TxSizeDepthCategory[bsize] - 1;
                int maxDepth = Av1Tables.MaxTxDepth[bsize];
                int tx = Av1Tables.MaxTxSizeRectLookup[bsize];
                int depth = 0;
                while (tx != b.TxSize && depth < maxDepth)
                {
                    tx = Av1Tables.SubTxSizeMap[tx];
                    depth++;
                }

                Check(tx);
                s.Symbol(_cdf, Av1FrameContext.TxSize[(category * 3) + TxSizeContext(bsize)], maxDepth + 1, depth);
                return;
            }

            int largest = _header.TxMode == Av1TxMode.Only4x4 ? Tx.T4x4 : Tx.T64x64;
            int maxRect = Av1Tables.MaxTxSizeRectLookup[bsize];
            Check(Av1Tables.TxSizeSqrMap[maxRect] <= largest ? maxRect : largest);
            return;
        }

        Check(Av1Tables.MaxTxSizeRectLookup[bsize]);

        void Check(int tx)
        {
            if (b.TxSize != tx)
            {
                throw new InvalidOperationException($"A block's transform size {b.TxSize} cannot be coded; it must be {tx}.");
            }
        }
    }

    private protected void CodeResidual<TSink>(ref TSink s, int bsize, int xMis, int yMis)
        where TSink : struct, IAv1SymbolSink
    {
        Av1BlockInfo b = _b;
        int maxW = MaxBlockWide(bsize, 0), maxH = MaxBlockHigh(bsize, 0);
        int muW = Math.Min(maxW, 16), muH = Math.Min(maxH, 16);
        bool lossless = _header.LosslessArray[b.SegmentId];
        // An intra block copy block's unsplit transform tree visits the same transform blocks, in the same order, as an intra
        // block's transform size does.
        for (int y = 0; y < yMis; y++)
        {
            _frame.TxSizeMap.AsSpan(((_miRow + y) * _miCols) + _miCol, xMis).Fill((byte)b.TxSize);
        }

        if (b.Skip)
        {
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
                    int txSize = lossless ? Tx.T4x4 : plane == 0 ? b.TxSize : Av1Tables.MaxUvTxSize(bsize, _ssx, _ssy);
                    int stepR = Av1Tables.TxSizeHighUnit[txSize], stepC = Av1Tables.TxSizeWideUnit[txSize];
                    int unitH = (Math.Min(muH + row, maxH) + ssy) >> ssy;
                    int unitW = (Math.Min(muW + col, maxW) + ssx) >> ssx;
                    (int col4, int row4) = PlaneOrigin(plane);
                    for (int blkRow = row >> ssy; blkRow < unitH; blkRow += stepR)
                    {
                        for (int blkCol = col >> ssx; blkCol < unitW; blkCol += stepC)
                        {
                            CodeCoefficients(ref s, plane, blkRow, blkCol, txSize, (col4 + blkCol) * 4, (row4 + blkRow) * 4);
                        }
                    }
                }
            }
        }
    }

    /// <summary>Writes a transform block's coefficients (av1_write_coeffs_txb), taking them from the decisions at its place in the plane.</summary>
    private protected void CodeCoefficients<TSink>(ref TSink s, int plane, int blkRow, int blkCol, int txSize, int x, int y)
        where TSink : struct, IAv1SymbolSink
    {
        int adjusted = Av1Tables.AdjustedTxSize(txSize);
        int bhl = Av1Tables.TxSizeHighLog2[adjusted];
        int width = Av1Tables.TxSizeWide[adjusted], height = Av1Tables.TxSizeHigh[adjusted];

        // The coefficients, column by column as the scans index them.
        int[] coeffs = _coeffs;
        int[] source = _decisions.Levels[plane];
        int stride = _decisions.Strides[plane];
        for (int c = 0; c < width; c++)
        {
            for (int r = 0; r < height; r++)
            {
                coeffs[(c << bhl) + r] = source[((y + r) * stride) + x + c];
            }
        }

        int txType = GetTxType(plane > 0 ? 1 : 0, blkRow, blkCol, txSize);
        short[] scan = Av1Tables.ScanOrders[(txSize * 16) + txType];
        int eob = 0;
        for (int c = (width * height) - 1; c >= 0; c--)
        {
            if (coeffs[scan[c]] != 0)
            {
                eob = c + 1;
                break;
            }
        }

        CodeCoefficients(ref s, plane, blkRow, blkCol, txSize, txType, coeffs, eob);
    }

    /// <summary>Codes a transform block's levels, column by column at the coded size, with their end of block and type.</summary>
    private protected void CodeCoefficients<TSink>(ref TSink s, int plane, int blkRow, int blkCol, int txSize, int txType, int[] coeffs, int eob)
        where TSink : struct, IAv1SymbolSink
    {
        Av1BlockInfo b = _b;
        int planeType = plane > 0 ? 1 : 0;
        int originIndex = ((_miRow + blkRow) * _miCols) + _miCol + blkCol;
        int adjusted = Av1Tables.AdjustedTxSize(txSize);
        int bhl = Av1Tables.TxSizeHighLog2[adjusted];
        int width = Av1Tables.TxSizeWide[adjusted], height = Av1Tables.TxSizeHigh[adjusted];
        short[] scan = Av1Tables.ScanOrders[(txSize * 16) + txType];
        int area = width * height;
        (int skipCtx, int dcSignCtx) = TxbContext(plane, blkRow, blkCol, txSize);
        int txsCtx = (Av1Tables.TxSizeSqrMap[txSize] + Av1Tables.TxSizeSqrUpMap[txSize] + 1) >> 1;
        s.Symbol(_cdf, Av1FrameContext.TxbSkip[(txsCtx * 13) + skipCtx], 2, eob == 0 ? 1 : 0);
        if (eob == 0)
        {
            if (plane == 0)
            {
                _frame.TxTypeMap[originIndex] = TxType.DctDct;
            }

            SetEntropyContexts(plane, blkRow, blkCol, txSize, 0);
            return;
        }

        if (plane == 0)
        {
            CodeTxType(ref s, txSize, txType);
        }

        int txClass = TxType.Class(txType);
        int eobMultiSize = Av1Tables.TxSizeLog2Minus4[txSize];
        int eobPt = 1;
        while (eobPt < Av1Tables.EobGroupStart.Length - 1 && Av1Tables.EobGroupStart[eobPt + 1] <= eob)
        {
            eobPt++;
        }

        s.Symbol(_cdf, Av1FrameContext.EobMulti[eobMultiSize][(planeType * 2) + (txClass == 0 ? 0 : 1)], 5 + eobMultiSize, eobPt - 1);
        int eobOffsetBits = Av1Tables.EobOffsetBits[eobPt];
        if (eobOffsetBits > 0)
        {
            int eobExtra = eob - Av1Tables.EobGroupStart[eobPt];
            int bit = (eobExtra >> (eobOffsetBits - 1)) & 1;
            s.Symbol(_cdf, Av1FrameContext.EobExtra[(((txsCtx * 2) + planeType) * 9) + eobPt - 3], 2, bit);
            for (int i = 1; i < eobOffsetBits; i++)
            {
                s.Bit((eobExtra >> (eobOffsetBits - 1 - i)) & 1);
            }
        }

        byte[] levels = _levels;
        int baseTable = Av1FrameContext.CoeffBase[((txsCtx * 2) + planeType) * 42];
        int brTable = Av1FrameContext.CoeffBr[((Math.Min(txsCtx, 3) * 2) + planeType) * 21];
        int baseStride = Av1FrameContext.CoeffBase.Stride, brStride = Av1FrameContext.CoeffBr.Stride;

        // The last nonzero coefficient.
        {
            int c = eob - 1;
            int pos = scan[c];
            int level = Math.Min(Math.Abs(coeffs[pos]), 15);
            int ctx = c == 0 ? 0 : c <= area / 8 ? 1 : c <= area / 4 ? 2 : 3;
            s.Symbol(_cdf, Av1FrameContext.CoeffBaseEob[(((txsCtx * 2) + planeType) * 4) + ctx], 3, Math.Min(level, 3) - 1);
            if (level > 2)
            {
                int col = pos >> bhl, row = pos - (col << bhl);
                int brCtx = pos == 0 ? 0 : (txClass == 0 && row < 2 && col < 2) || (txClass == 1 && col == 0) || (txClass == 2 && row == 0) ? 7 : 14;
                CodeBaseRange(ref s, brTable + (brCtx * brStride), level - 3);
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

                s.Symbol(_cdf, baseTable + (ctx * baseStride), 4, Math.Min(level, 3));
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
                    CodeBaseRange(ref s, brTable + (brCtx * brStride), level - 3);
                }

                levels[p] = (byte)level;
            }
        }

        // Signs and Golomb remainders, in scan order.
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
            if (c == 0)
            {
                s.Symbol(_cdf, Av1FrameContext.DcSign[(planeType * 3) + dcSignCtx], 2, value < 0 ? 1 : 0);
            }
            else
            {
                s.Bit(value < 0 ? 1 : 0);
            }

            if (abs >= 15)
            {
                s.Golomb((uint)(abs - 15));
            }

            culLevel += abs;
        }

        culLevel = Math.Min(7, culLevel);
        int dc = coeffs[0];
        if (dc < 0)
        {
            culLevel |= 8;
        }
        else if (dc > 0)
        {
            culLevel += 16;
        }

        SetEntropyContexts(plane, blkRow, blkCol, txSize, culLevel);
    }

    private protected void CodeBaseRange<TSink>(ref TSink s, int at, int remaining)
        where TSink : struct, IAv1SymbolSink
    {
        for (int idx = 0; idx < 12; idx += 3)
        {
            int k = Math.Min(remaining, 3);
            s.Symbol(_cdf, at, 4, k);
            remaining -= k;
            if (k < 3)
            {
                break;
            }
        }
    }

    /// <summary>Writes a luma transform block's type (av1_write_tx_type).</summary>
    private protected void CodeTxType<TSink>(ref TSink s, int txSize, int txType)
        where TSink : struct, IAv1SymbolSink
    {
        Av1BlockInfo b = _b;
        if (b.Skip || SegFeatureActive(b.SegmentId, 6) || _header.SegQIdx[b.SegmentId] == 0)
        {
            if (txType != TxType.DctDct)
            {
                throw new InvalidOperationException("A lossless or skipped block's transform type must be DCT_DCT.");
            }

            return;
        }

        bool inter = b.IsInter;
        int setType = Av1Tables.ExtTxSetType(txSize, inter, _header.ReducedTxSet);
        int n = Av1Tables.NumExtTxSet[setType];
        if (n <= 1)
        {
            if (txType != TxType.DctDct)
            {
                throw new InvalidOperationException($"Transform size {txSize} codes no transform type but DCT_DCT.");
            }

            return;
        }

        int symbol = -1;
        for (int i = 0; i < n; i++)
        {
            if (Av1Tables.ExtTxInverse[(setType * 16) + i] == txType)
            {
                symbol = i;
                break;
            }
        }

        if (symbol < 0)
        {
            throw new InvalidOperationException($"Transform type {txType} is not in the set of transform size {txSize}.");
        }

        int square = Av1Tables.TxSizeSqrMap[txSize];
        if (inter)
        {
            int eset = InterExtTxSetIndex[setType];
            s.Symbol(_cdf, Av1FrameContext.InterExtTx[((eset - 1) * 4) + square], n, symbol);
        }
        else
        {
            int eset = Av1Tables.IntraExtTxSet(setType);
            int mode = b.UseFilterIntra ? Av1Tables.FilterIntraToMode[b.FilterIntraMode] : b.YMode;
            s.Symbol(_cdf, Av1FrameContext.IntraExtTx[((eset - 1) * 52) + (square * 13) + mode], n, symbol);
        }
    }

    private protected void CodeRestorationUnits<TSink>(ref TSink s, int miRow, int miCol, int bsize)
        where TSink : struct, IAv1SymbolSink
    {
        for (int plane = 0; plane < _planes; plane++)
        {
            Av1RestorationPlane lr = _frame.Restoration[plane];
            if (lr.Type == Av1RestorationType.None)
            {
                continue;
            }

            // av1_loop_restoration_corners_in_sb
            int ssx = plane > 0 ? _ssx : 0, ssy = plane > 0 ? _ssy : 0;
            int size = lr.UnitSize;
            bool scaled = _header.FrameWidth != _header.UpscaledWidth;
            int miToNumX = scaled ? (4 >> ssx) * _header.SuperresDenom : 4 >> ssx;
            int miToNumY = 4 >> ssy;
            int denomX = scaled ? size * 8 : size, denomY = size;
            int rcol0 = ((miCol * miToNumX) + denomX - 1) / denomX;
            int rrow0 = ((miRow * miToNumY) + denomY - 1) / denomY;
            int rcol1 = Math.Min((((miCol + Av1Tables.MiSizeWide[bsize]) * miToNumX) + denomX - 1) / denomX, lr.HorzUnits);
            int rrow1 = Math.Min((((miRow + Av1Tables.MiSizeHigh[bsize]) * miToNumY) + denomY - 1) / denomY, lr.VertUnits);
            for (int rrow = rrow0; rrow < rrow1; rrow++)
            {
                for (int rcol = rcol0; rcol < rcol1; rcol++)
                {
                    CodeRestorationUnit(ref s, plane, lr.Units[(rrow * lr.HorzUnits) + rcol], lr.Type);
                }
            }
        }
    }

    private protected void CodeRestorationUnit<TSink>(ref TSink s, int plane, Av1RestorationUnit unit, Av1RestorationType frameType)
        where TSink : struct, IAv1SymbolSink
    {
        switch (frameType)
        {
            case Av1RestorationType.Switchable:
                s.Symbol(_cdf, Av1FrameContext.SwitchableRestore[0], 3, unit.Type);
                break;
            case Av1RestorationType.Wiener:
                if (unit.Type == 2)
                {
                    throw new InvalidOperationException("A Wiener frame's unit cannot be self-guided.");
                }

                s.Symbol(_cdf, Av1FrameContext.WienerRestore[0], 2, unit.Type == 1 ? 1 : 0);
                break;
            default:
                if (unit.Type == 1)
                {
                    throw new InvalidOperationException("A self-guided frame's unit cannot be Wiener.");
                }

                s.Symbol(_cdf, Av1FrameContext.SgrprojRestore[0], 2, unit.Type == 2 ? 1 : 0);
                break;
        }

        if (unit.Type == 1)
        {
            bool full = plane == 0;
            if (full)
            {
                CodeTap(ref s, plane, 0, -5, 10, 1, unit.V0);
            }
            else if (unit.V0 != 0)
            {
                throw new InvalidOperationException("A chroma Wiener filter's outer tap is 0.");
            }

            CodeTap(ref s, plane, 1, -23, 8, 2, unit.V1);
            CodeTap(ref s, plane, 2, -17, 46, 3, unit.V2);
            if (full)
            {
                CodeTap(ref s, plane, 3, -5, 10, 1, unit.H0);
            }
            else if (unit.H0 != 0)
            {
                throw new InvalidOperationException("A chroma Wiener filter's outer tap is 0.");
            }

            CodeTap(ref s, plane, 4, -23, 8, 2, unit.H1);
            CodeTap(ref s, plane, 5, -17, 46, 3, unit.H2);
            _wienerRef[plane, 0] = unit.V0;
            _wienerRef[plane, 1] = unit.V1;
            _wienerRef[plane, 2] = unit.V2;
            _wienerRef[plane, 3] = unit.H0;
            _wienerRef[plane, 4] = unit.H1;
            _wienerRef[plane, 5] = unit.H2;
        }
        else if (unit.Type == 2)
        {
            s.Literal(unit.SgrSet, 4);
            (int r0, int r1) = Av1Tables.SgrRadii(unit.SgrSet);
            if (r0 == 0)
            {
                s.RefSubexpFin(128, 4, _sgrRef[plane, 1] + 32, unit.Xqd1 + 32);
            }
            else if (r1 == 0)
            {
                s.RefSubexpFin(128, 4, _sgrRef[plane, 0] + 96, unit.Xqd0 + 96);
            }
            else
            {
                s.RefSubexpFin(128, 4, _sgrRef[plane, 0] + 96, unit.Xqd0 + 96);
                s.RefSubexpFin(128, 4, _sgrRef[plane, 1] + 32, unit.Xqd1 + 32);
            }

            _sgrRef[plane, 0] = unit.Xqd0;
            _sgrRef[plane, 1] = unit.Xqd1;
        }

    }

    private void CodeTap<TSink>(ref TSink s, int plane, int index, int min, int max, int k, int value)
        where TSink : struct, IAv1SymbolSink => s.RefSubexpFin(max - min + 1, k, _wienerRef[plane, index] - min, value - min);
}

/// <summary>Writes one tile of an intra frame from the frame's decisions, with the range encoder.</summary>
internal sealed class Av1TileWriter(Av1Frame frame, int tileRow, int tileCol, Av1FrameDecisions decisions)
    : Av1TileCoder(frame, tileRow, tileCol, decisions)
{
    /// <summary>Writes the tile and returns its data.</summary>
    internal byte[] Write()
    {
        Av1WriteSink sink = new(new Av1SymbolEncoder(_header.DisableCdfUpdate));
        CodeTile(ref sink);
        return sink.Encoder.Finish();
    }
}
