namespace Egami.Formats.Av1;

/// <summary>
/// Parses one tile of an intra frame: the partition tree, each block's mode info, palette color maps and coefficients, as libaom's
/// decodeframe.c, decodemv.c and decodetxb.c read them (BSD 2-clause, see THIRD-PARTY-NOTICES). Blocks go to an
/// <see cref="IAv1Reconstructor"/> as they are read.
/// </summary>
internal sealed partial class Av1TileParser
{
    private const int MaxMibMask = 31;

    private static readonly byte[][] SkipContexts = [[1, 2, 2, 2, 3], [2, 4, 4, 4, 5], [2, 4, 4, 4, 5], [2, 4, 4, 4, 5], [3, 5, 5, 5, 6]];
    private static readonly sbyte[] InterExtTxSetIndex = [0, 3, -1, -1, 2, 1];

    private readonly Av1Frame _frame;
    private readonly Av1FrameHeader _header;
    private readonly Av1SequenceHeader _sequence;
    private readonly IAv1Reconstructor? _reconstructor;
    private readonly Av1SymbolDecoder _r;
    private readonly ushort[] _cdf;
    private readonly int _planes;
    private readonly int _ssx, _ssy;
    private readonly int _miCols;
    private readonly int _sbMi;
    private readonly byte[][] _aboveEntropy;
    private readonly byte[] _abovePartition;
    private readonly byte[] _aboveTxfm;
    private readonly byte[][] _leftEntropy = [new byte[32], new byte[32], new byte[32]];
    private readonly byte[] _leftPartition = new byte[32];
    private readonly byte[] _leftTxfm = new byte[32];
    private readonly int[] _deltaLf = new int[4];
    private readonly int[,] _wienerRef = new int[3, 6];
    private readonly int[,] _sgrRef = new int[3, 2];
    private readonly bool[] _cdefTransmitted = new bool[4];
    private readonly int[] _coeffs = new int[32 * 32];
    private readonly byte[] _levels = new byte[((32 + 4) * (32 + 4)) + 16];
    private readonly int[] _aboveOffset = new int[3];
    private readonly int[] _leftOffset = new int[3];
    private int _currentQIndex;

    // The current block (libaom's MACROBLOCKD).
    private Av1BlockInfo _b = null!;
    private int _miRow, _miCol, _bw, _bh;
    private int _toTopEdge, _toBottomEdge, _toLeftEdge, _toRightEdge;
    private bool _upAvailable, _leftAvailable;
    private Av1BlockInfo? _above, _left;
    private bool _isChromaRef;
    private bool _isLastVerticalRect, _isFirstHorizontalRect;

    /// <summary>Starts parsing a tile.</summary>
    internal Av1TileParser(Av1Frame frame, int tileRow, int tileCol, byte[] data, int offset, int size, IAv1Reconstructor? reconstructor)
    {
        _frame = frame;
        _header = frame.Header;
        _sequence = frame.Sequence;
        _reconstructor = reconstructor;
        _planes = _header.NumPlanes;
        _ssx = _sequence.SubsamplingX;
        _ssy = _sequence.SubsamplingY;
        _miCols = frame.MiCols;
        _sbMi = 1 << frame.SbMiLog2;
        MiRowStart = _header.MiRowStarts[tileRow];
        MiRowEnd = Math.Min(_header.MiRowStarts[tileRow + 1], frame.MiRows);
        MiColStart = _header.MiColStarts[tileCol];
        MiColEnd = Math.Min(_header.MiColStarts[tileCol + 1], frame.MiCols);
        int width = ((MiColEnd - MiColStart + _sbMi - 1) & ~(_sbMi - 1)) + 32;
        _aboveEntropy = [new byte[width], new byte[width], new byte[width]];
        _abovePartition = new byte[width];
        _aboveTxfm = new byte[width];
        _aboveTxfm.AsSpan().Fill(64);
        Context = frame.Context.Clone();
        _cdf = Context.Cdf;
        _r = new Av1SymbolDecoder(data, offset, size, _header.DisableCdfUpdate);
        _currentQIndex = _header.BaseQIdx;
        for (int plane = 0; plane < 3; plane++)
        {
            _wienerRef[plane, 0] = _wienerRef[plane, 3] = 3;
            _wienerRef[plane, 1] = _wienerRef[plane, 4] = -7;
            _wienerRef[plane, 2] = _wienerRef[plane, 5] = 15;
            _sgrRef[plane, 0] = -32;
            _sgrRef[plane, 1] = 31;
        }
    }

    /// <summary>Gets the tile's CDFs, as they adapt.</summary>
    internal Av1FrameContext Context { get; }

    /// <summary>Gets the frame.</summary>
    internal Av1Frame Frame => _frame;

    /// <summary>Gets the tile's first row, in 4x4 units.</summary>
    internal int MiRowStart { get; }

    /// <summary>Gets the row after the tile's last, in 4x4 units.</summary>
    internal int MiRowEnd { get; }

    /// <summary>Gets the tile's first column, in 4x4 units.</summary>
    internal int MiColStart { get; }

    /// <summary>Gets the column after the tile's last, in 4x4 units.</summary>
    internal int MiColEnd { get; }

    /// <summary>Gets the block being decoded.</summary>
    internal Av1BlockInfo Block => _b;

    /// <summary>Gets the current block's palette color indexes for luma and for chroma, row by row at the plane block's width.</summary>
    internal byte[][] ColorMaps { get; } = [new byte[64 * 64], new byte[64 * 64]];

    /// <summary>Gets whether the current block carries chroma for its area (is_chroma_ref).</summary>
    internal bool IsChromaRef => _isChromaRef;

    /// <summary>Parses the tile.</summary>
    internal void Parse()
    {
        int sbIndexRowStride = _frame.SbCols;
        for (int miRow = MiRowStart; miRow < MiRowEnd; miRow += _sbMi)
        {
            foreach (byte[] left in _leftEntropy)
            {
                Array.Clear(left);
            }

            Array.Clear(_leftPartition);
            _leftTxfm.AsSpan().Fill(64);
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
        int bsl = Av1Tables.MiSizeWideLog2[bsize] - 1;
        int above = (_abovePartition[miCol - MiColStart] >> bsl) & 1;
        int left = (_leftPartition[miRow & MaxMibMask] >> bsl) & 1;
        int ctx = (left * 2) + above + (bsl * 4);
        if (!hasRows && !hasCols)
        {
            return Partition.Split;
        }

        int at = Av1FrameContext.Partition[ctx];
        if (hasRows && hasCols)
        {
            int n = bsize <= Bs.B8x8 ? 4 : bsize == Bs.B128x128 ? 8 : 10;
            return Symbol(at, n);
        }

        // Gather the probability of the partitions that split the missing direction (partition_gather_*_alike).
        int p = 32768;
        ReadOnlySpan<int> parts = !hasRows
            ? [Partition.Vert, Partition.Split, Partition.HorzA, Partition.VertA, Partition.VertB, Partition.Vert4]
            : [Partition.Horz, Partition.Split, Partition.HorzA, Partition.HorzB, Partition.VertA, Partition.Horz4];
        foreach (int part in parts)
        {
            if (part >= Partition.Horz4 && bsize == Bs.B128x128)
            {
                continue;
            }

            p -= (part > 0 ? _cdf[at + part - 1] : 32768) - _cdf[at + part];
        }

        ReadOnlySpan<ushort> gathered = [(ushort)(32768 - p), 0];
        int split = _r.SymbolFixed(gathered, 2);
        return split != 0 ? Partition.Split : !hasRows ? Partition.Horz : Partition.Vert;
    }

    private void UpdatePartitionContext(int miRow, int miCol, int subsize, int bsize)
    {
        _abovePartition.AsSpan(miCol - MiColStart, Av1Tables.MiSizeWide[bsize]).Fill(Av1Tables.PartitionContextAbove[subsize]);
        _leftPartition.AsSpan(miRow & MaxMibMask, Av1Tables.MiSizeHigh[bsize]).Fill(Av1Tables.PartitionContextLeft[subsize]);
    }

    private void UpdateExtPartitionContext(int miRow, int miCol, int subsize, int bsize, int partition)
    {
        if (bsize < Bs.B8x8)
        {
            return;
        }

        int hbs = Av1Tables.MiSizeWide[bsize] / 2;
        int bsize2 = Av1Tables.PartitionSubsize(bsize, Partition.Split);
        switch (partition)
        {
            case Partition.Split when bsize != Bs.B8x8:
                break;
            case Partition.None or Partition.Horz or Partition.Vert or Partition.Horz4 or Partition.Vert4 or Partition.Split:
                UpdatePartitionContext(miRow, miCol, subsize, bsize);
                break;
            case Partition.HorzA:
                UpdatePartitionContext(miRow, miCol, bsize2, subsize);
                UpdatePartitionContext(miRow + hbs, miCol, subsize, subsize);
                break;
            case Partition.HorzB:
                UpdatePartitionContext(miRow, miCol, subsize, subsize);
                UpdatePartitionContext(miRow + hbs, miCol, bsize2, subsize);
                break;
            case Partition.VertA:
                UpdatePartitionContext(miRow, miCol, bsize2, subsize);
                UpdatePartitionContext(miRow, miCol + hbs, subsize, subsize);
                break;
            default:
                UpdatePartitionContext(miRow, miCol, subsize, subsize);
                UpdatePartitionContext(miRow, miCol + hbs, bsize2, subsize);
                break;
        }
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
        ReadIntraFrameModeInfo(xMis, yMis);
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

    private void SetBlockPosition(int miRow, int miCol, int bw, int bh)
    {
        _miRow = miRow;
        _miCol = miCol;
        _bw = bw;
        _bh = bh;
        _toTopEdge = -(miRow * 4 * 8);
        _toBottomEdge = (_frame.MiRows - bh - miRow) * 4 * 8;
        _toLeftEdge = -(miCol * 4 * 8);
        _toRightEdge = (_miCols - bw - miCol) * 4 * 8;
        _upAvailable = miRow > MiRowStart;
        _leftAvailable = miCol > MiColStart;
        _above = _upAvailable ? _frame.Grid[((miRow - 1) * _miCols) + miCol] : null;
        _left = _leftAvailable ? _frame.Grid[(miRow * _miCols) + miCol - 1] : null;
        _isChromaRef = ((miRow & 1) != 0 || (bh & 1) == 0 || _ssy == 0) && ((miCol & 1) != 0 || (bw & 1) == 0 || _ssx == 0);
        _isLastVerticalRect = bw < bh && ((miCol + bw) & (bh - 1)) == 0;
        _isFirstHorizontalRect = bw > bh && (miRow & (bw - 1)) == 0;

        // set_entropy_context: a chroma block of a sub-8x8 luma block starts at the even 4x4 unit.
        int rowOffset = miRow, colOffset = miCol;
        for (int plane = 0; plane < _planes; plane++)
        {
            int ssx = plane > 0 ? _ssx : 0, ssy = plane > 0 ? _ssy : 0;
            if (ssy != 0 && (miRow & 1) != 0 && bh == 1)
            {
                rowOffset = miRow - 1;
            }

            if (ssx != 0 && (miCol & 1) != 0 && bw == 1)
            {
                colOffset = miCol - 1;
            }

            _aboveOffset[plane] = (colOffset >> ssx) - (MiColStart >> ssx);
            _leftOffset[plane] = (rowOffset & MaxMibMask) >> ssy;
        }
    }

    /// <summary>Gets a plane's top left 4x4 unit of the current block, in the plane (libaom's dst origin).</summary>
    internal (int Col4, int Row4) PlaneOrigin(int plane)
    {
        if (plane == 0)
        {
            return (_miCol, _miRow);
        }

        int col = _ssx != 0 && (_miCol & 1) != 0 && _bw == 1 ? _miCol - 1 : _miCol;
        int row = _ssy != 0 && (_miRow & 1) != 0 && _bh == 1 ? _miRow - 1 : _miRow;
        return (col >> _ssx, row >> _ssy);
    }

    private bool SegFeatureActive(int segment, int feature) => _header.SegmentationEnabled && _header.FeatureEnabled[segment, feature];

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

        int aboveMode = _above?.YMode ?? Pred.Dc, leftMode = _left?.YMode ?? Pred.Dc;
        int kfCtx = (Av1Tables.IntraModeContext[aboveMode] * 5) + Av1Tables.IntraModeContext[leftMode];
        b.YMode = Symbol(Av1FrameContext.KfYMode[kfCtx], 13);
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

    /// <summary>Gets whether chroma from luma is allowed for the current block (is_cfl_allowed).</summary>
    internal bool CflAllowed() => _header.LosslessArray[_b.SegmentId]
        ? Av1Tables.PlaneBlockSize(_b.Size, _ssx, _ssy) == Bs.B4x4
        : Av1Tables.BlockSizeWide[_b.Size] <= 32 && Av1Tables.BlockSizeHigh[_b.Size] <= 32;

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

        int ul = -1, u = -1, l = -1;
        byte[] map = _frame.SegmentMap;
        if (_upAvailable && _leftAvailable)
        {
            ul = map[((_miRow - 1) * _miCols) + _miCol - 1];
        }

        if (_upAvailable)
        {
            u = map[((_miRow - 1) * _miCols) + _miCol];
        }

        if (_leftAvailable)
        {
            l = map[(_miRow * _miCols) + _miCol - 1];
        }

        int cdfIndex = ul < 0 ? 0 : ul == u && ul == l ? 2 : ul == u || ul == l || u == l ? 1 : 0;
        int pred = u < 0 ? (l < 0 ? 0 : l) : l < 0 ? u : ul == u ? u : l;
        int segment;
        if (skip)
        {
            segment = pred;
        }
        else
        {
            int coded = Symbol(Av1FrameContext.SpatialPredSegTree[cdfIndex], 8);
            segment = NegDeinterleave(coded, pred, _header.LastActiveSegId + 1);
            if (segment < 0 || segment > _header.LastActiveSegId)
            {
                throw Corrupt("An AV1 segment id is out of range.");
            }
        }

        for (int y = 0; y < yMis; y++)
        {
            map.AsSpan(((_miRow + y) * _miCols) + _miCol, xMis).Fill((byte)segment);
        }

        return segment;
    }

    private static int NegDeinterleave(int diff, int reference, int max)
    {
        if (reference == 0)
        {
            return diff;
        }

        if (reference >= max - 1)
        {
            return max - diff - 1;
        }

        if (2 * reference < max)
        {
            if (diff <= 2 * reference)
            {
                return (diff & 1) != 0 ? reference + ((diff + 1) >> 1) : reference - (diff >> 1);
            }

            return diff;
        }

        if (diff <= 2 * (max - reference - 1))
        {
            return (diff & 1) != 0 ? reference + ((diff + 1) >> 1) : reference - (diff >> 1);
        }

        return max - (diff + 1);
    }

    private bool ReadSkip(int segment)
    {
        if (SegFeatureActive(segment, 6))
        {
            return true;
        }

        int ctx = (_above?.Skip == true ? 1 : 0) + (_left?.Skip == true ? 1 : 0);
        return Symbol(Av1FrameContext.SkipTxfm[ctx], 2) != 0;
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
            _frame.CdefIndex[((_miRow >> 4) * _frame.CdefStride) + (_miCol >> 4)] = (sbyte)_r.Literal(_header.CdefBits);
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

    private int PaletteCache(int plane, Span<ushort> cache)
    {
        // Do not use the row above at a 64-sample boundary.
        Av1BlockInfo? above = (_miRow * 4) % 64 != 0 ? _above : null;
        int aboveN = above?.PaletteSize[plane] ?? 0, leftN = _left?.PaletteSize[plane] ?? 0;
        if (aboveN == 0 && leftN == 0)
        {
            return 0;
        }

        int aboveIdx = plane * 8, leftIdx = plane * 8, n = 0;
        ushort[]? aboveColors = above?.PaletteColors, leftColors = _left?.PaletteColors;
        while (aboveN > 0 && leftN > 0)
        {
            ushort vAbove = aboveColors![aboveIdx], vLeft = leftColors![leftIdx];
            if (vLeft < vAbove)
            {
                Add(cache, ref n, vLeft);
                leftIdx++;
                leftN--;
            }
            else
            {
                Add(cache, ref n, vAbove);
                aboveIdx++;
                aboveN--;
                if (vLeft == vAbove)
                {
                    leftIdx++;
                    leftN--;
                }
            }
        }

        while (aboveN-- > 0)
        {
            Add(cache, ref n, aboveColors![aboveIdx++]);
        }

        while (leftN-- > 0)
        {
            Add(cache, ref n, leftColors![leftIdx++]);
        }

        return n;

        static void Add(Span<ushort> cache, ref int n, ushort value)
        {
            if (n == 0 || cache[n - 1] != value)
            {
                cache[n++] = value;
            }
        }
    }

    /// <summary>Gets the current block's size in a plane (av1_get_block_dimensions): width, height and the part inside the frame.</summary>
    internal (int Width, int Height, int Rows, int Cols) BlockDimensions(int plane)
    {
        int blockHeight = Av1Tables.BlockSizeHigh[_b.Size], blockWidth = Av1Tables.BlockSizeWide[_b.Size];
        int rows = _toBottomEdge >= 0 ? blockHeight : (_toBottomEdge >> 3) + blockHeight;
        int cols = _toRightEdge >= 0 ? blockWidth : (_toRightEdge >> 3) + blockWidth;
        int ssx = plane > 0 ? _ssx : 0, ssy = plane > 0 ? _ssy : 0;
        int width = blockWidth >> ssx, height = blockHeight >> ssy;
        int subX = plane > 0 && width < 4 ? 2 : 0, subY = plane > 0 && height < 4 ? 2 : 0;
        return (width + subX, height + subY, (rows >> ssy) + subY, (cols >> ssx) + subX);
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

    private static int ColorContext(byte[] map, int stride, int r, int c, int n, Span<byte> order)
    {
        Span<int> scores = stackalloc int[8];
        if (c > 0)
        {
            scores[map[(r * stride) + c - 1]] += 2;
        }

        if (c > 0 && r > 0)
        {
            scores[map[((r - 1) * stride) + c - 1]] += 1;
        }

        if (r > 0)
        {
            scores[map[((r - 1) * stride) + c]] += 2;
        }

        for (int i = 0; i < 8; i++)
        {
            order[i] = (byte)i;
        }

        for (int i = 0; i < 3; i++)
        {
            int max = scores[i], maxIdx = i;
            for (int j = i + 1; j < n; j++)
            {
                if (scores[j] > max)
                {
                    max = scores[j];
                    maxIdx = j;
                }
            }

            if (maxIdx != i)
            {
                int maxScore = scores[maxIdx];
                byte maxOrder = order[maxIdx];
                for (int k = maxIdx; k > i; k--)
                {
                    scores[k] = scores[k - 1];
                    order[k] = order[k - 1];
                }

                scores[i] = maxScore;
                order[i] = maxOrder;
            }
        }

        int hash = scores[0] + (scores[1] * 2) + (scores[2] * 2);
        return hash switch
        {
            2 => 0,
            5 => 4,
            6 => 3,
            7 => 2,
            8 => 1,
            _ => throw new InvalidOperationException("Palette context hash out of range."),
        };
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

    private int TxSizeContext(int bsize)
    {
        int maxTx = Av1Tables.MaxTxSizeRectLookup[bsize];
        int maxW = Av1Tables.TxSizeWide[maxTx], maxH = Av1Tables.TxSizeHigh[maxTx];
        int above = _aboveTxfm[_miCol - MiColStart] >= maxW ? 1 : 0;
        int left = _leftTxfm[_miRow & MaxMibMask] >= maxH ? 1 : 0;
        if (_upAvailable && _above!.IsInter)
        {
            above = Av1Tables.BlockSizeWide[_above.Size] >= maxW ? 1 : 0;
        }

        if (_leftAvailable && _left!.IsInter)
        {
            left = Av1Tables.BlockSizeHigh[_left.Size] >= maxH ? 1 : 0;
        }

        return _upAvailable && _leftAvailable ? above + left : _upAvailable ? above : _leftAvailable ? left : 0;
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

    private int TxfmPartitionContext(int blkRow, int blkCol, int bsize, int txSize)
    {
        int above = _aboveTxfm[_miCol - MiColStart + blkCol] < Av1Tables.TxSizeWide[txSize] ? 1 : 0;
        int left = _leftTxfm[(_miRow & MaxMibMask) + blkRow] < Av1Tables.TxSizeHigh[txSize] ? 1 : 0;
        if (txSize <= Tx.T4x4)
        {
            return 0;
        }

        int maxDim = Math.Max(Av1Tables.BlockSizeWide[bsize], Av1Tables.BlockSizeHigh[bsize]);
        int maxTx = maxDim >= 64 ? Tx.T64x64 : maxDim == 32 ? Tx.T32x32 : maxDim == 16 ? Tx.T16x16 : maxDim == 8 ? Tx.T8x8 : Tx.T4x4;
        int category = ((Av1Tables.TxSizeSqrUpMap[txSize] != maxTx && maxTx > Tx.T8x8) ? 1 : 0) + ((Tx.SquareCount - 1 - maxTx) * 2);
        return (category * 3) + above + left;
    }

    /// <summary>Gets how many 4x4 columns of a block (in a plane, of a plane block size) lie inside the frame (max_block_wide).</summary>
    internal int MaxBlockWide(int bsize, int plane)
    {
        int w = Av1Tables.BlockSizeWide[bsize];
        if (_toRightEdge < 0)
        {
            w += _toRightEdge >> (3 + (plane > 0 ? _ssx : 0));
        }

        return w >> 2;
    }

    /// <summary>Gets how many 4x4 rows of a block lie inside the frame (max_block_high).</summary>
    internal int MaxBlockHigh(int bsize, int plane)
    {
        int h = Av1Tables.BlockSizeHigh[bsize];
        if (_toBottomEdge < 0)
        {
            h += _toBottomEdge >> (3 + (plane > 0 ? _ssy : 0));
        }

        return h >> 2;
    }

    private void ResetEntropyContext(int bsize)
    {
        int planes = 1 + ((_planes - 1) * (_isChromaRef ? 1 : 0));
        for (int plane = 0; plane < planes; plane++)
        {
            int planeBsize = plane == 0 ? bsize : Av1Tables.PlaneBlockSize(bsize, _ssx, _ssy);
            _aboveEntropy[plane].AsSpan(_aboveOffset[plane], Av1Tables.MiSizeWide[planeBsize]).Clear();
            _leftEntropy[plane].AsSpan(_leftOffset[plane], Av1Tables.MiSizeHigh[planeBsize]).Clear();
        }
    }
}
