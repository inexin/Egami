namespace Egami.Formats.Av1;

/// <summary>
/// The state of one tile of an intra frame that its parser and the encoder's tile writer share, so both derive every context
/// alike: the current block and its neighbours (libaom's MACROBLOCKD), the above and left partition, transform and entropy
/// contexts, the tile's CDFs, and the context derivations of libaom's decodeframe.c, decodemv.c, decodetxb.c, blockd.h,
/// pred_common.c and mvref_common.c (BSD 2-clause, see THIRD-PARTY-NOTICES). <see cref="IAv1Reconstructor"/> reads the current
/// block from here.
/// </summary>
internal abstract partial class Av1TileState
{
    private protected const int MaxMibMask = 31;

    private protected static readonly byte[][] SkipContexts = [[1, 2, 2, 2, 3], [2, 4, 4, 4, 5], [2, 4, 4, 4, 5], [2, 4, 4, 4, 5], [3, 5, 5, 5, 6]];
    private protected readonly Av1Frame _frame;
    private protected readonly Av1FrameHeader _header;
    private protected readonly Av1SequenceHeader _sequence;
    private protected readonly ushort[] _cdf;
    private protected readonly int _planes;
    private protected readonly int _ssx, _ssy;
    private protected readonly int _miCols;
    private protected readonly int _sbMi;
    private protected readonly byte[][] _aboveEntropy;
    private protected readonly byte[] _abovePartition;
    private protected readonly byte[] _aboveTxfm;
    private protected readonly byte[][] _leftEntropy = [new byte[32], new byte[32], new byte[32]];
    private protected readonly byte[] _leftPartition = new byte[32];
    private protected readonly byte[] _leftTxfm = new byte[32];
    private protected readonly int[] _deltaLf = new int[4];
    private protected readonly int[,] _wienerRef = new int[3, 6];
    private protected readonly int[,] _sgrRef = new int[3, 2];
    private protected readonly bool[] _cdefTransmitted = new bool[4];
    private protected readonly int[] _aboveOffset = new int[3];
    private protected readonly int[] _leftOffset = new int[3];
    private protected int _currentQIndex;

    // The current block (libaom's MACROBLOCKD).
    private protected Av1BlockInfo _b = null!;
    private protected int _miRow, _miCol, _bw, _bh;
    private protected int _toTopEdge, _toBottomEdge, _toLeftEdge, _toRightEdge;
    private protected bool _upAvailable, _leftAvailable, _chromaUpAvailable, _chromaLeftAvailable;
    private protected Av1BlockInfo? _above, _left, _chromaAbove, _chromaLeft;
    private protected bool _isChromaRef;
    private protected bool _isLastVerticalRect, _isFirstHorizontalRect;

    /// <summary>
    /// Starts a tile of a frame: its bounds, its CDFs (a copy of the frame's) and empty above and left contexts, or the above
    /// contexts of <paramref name="shared"/>, another state of the same tile whose superblock rows this one takes turns with.
    /// </summary>
    private protected Av1TileState(Av1Frame frame, int tileRow, int tileCol, Av1TileState? shared = null)
    {
        _frame = frame;
        _header = frame.Header;
        _sequence = frame.Sequence;
        _planes = _header.NumPlanes;
        _ssx = _sequence.SubsamplingX;
        _ssy = _sequence.SubsamplingY;
        _miCols = frame.MiCols;
        _sbMi = 1 << frame.SbMiLog2;
        MiRowStart = _header.MiRowStarts[tileRow];
        MiRowEnd = Math.Min(_header.MiRowStarts[tileRow + 1], frame.MiRows);
        MiColStart = _header.MiColStarts[tileCol];
        MiColEnd = Math.Min(_header.MiColStarts[tileCol + 1], frame.MiCols);
        if (shared is not null)
        {
            (_aboveEntropy, _abovePartition, _aboveTxfm) = (shared._aboveEntropy, shared._abovePartition, shared._aboveTxfm);
        }
        else
        {
            int width = ((MiColEnd - MiColStart + _sbMi - 1) & ~(_sbMi - 1)) + 32;
            _aboveEntropy = [new byte[width], new byte[width], new byte[width]];
            _abovePartition = new byte[width];
            _aboveTxfm = new byte[width];
            _aboveTxfm.AsSpan().Fill(64);
        }

        Context = frame.Context.Clone();
        _cdf = Context.Cdf;
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

    /// <summary>Clears the left contexts, as each superblock row of the tile starts.</summary>
    private protected void ResetLeftContext()
    {
        foreach (byte[] left in _leftEntropy)
        {
            Array.Clear(left);
        }

        Array.Clear(_leftPartition);
        _leftTxfm.AsSpan().Fill(64);
    }

    /// <summary>
    /// Clears what a superblock row carries from the superblocks before it, as a row of the tile starts in the encoder's search:
    /// the left contexts, the quantizer and loop filter deltas back at the frame's, and the loop restoration references.
    /// </summary>
    private protected void ResetRow()
    {
        ResetLeftContext();
        _currentQIndex = _header.BaseQIdx;
        Array.Clear(_deltaLf);
        Array.Clear(_cdefTransmitted);
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

    /// <summary>Gets the current block's top row, in 4x4 units.</summary>
    internal int MiRow => _miRow;

    /// <summary>Gets the current block's left column, in 4x4 units.</summary>
    internal int MiCol => _miCol;

    /// <summary>Gets the current block's width, in 4x4 units.</summary>
    internal int BlockWidth4 => _bw;

    /// <summary>Gets the current block's height, in 4x4 units.</summary>
    internal int BlockHeight4 => _bh;

    /// <summary>Gets how far right of the frame's edge the current block reaches, in eighth samples (negative when it crosses).</summary>
    internal int ToRightEdge => _toRightEdge;

    /// <summary>Gets how far below the frame's edge the current block reaches, in eighth samples (negative when it crosses).</summary>
    internal int ToBottomEdge => _toBottomEdge;

    /// <summary>Gets the current block's distance from the frame's left edge, in eighth samples (zero or negative).</summary>
    internal int ToLeftEdge => _toLeftEdge;

    /// <summary>Gets the current block's distance from the frame's top edge, in eighth samples (zero or negative).</summary>
    internal int ToTopEdge => _toTopEdge;

    /// <summary>Gets whether the row above the block is in the tile, for a plane.</summary>
    internal bool UpAvailable(int plane) => plane == 0 || _ssy == 0 ? _upAvailable : _chromaUpAvailable;

    /// <summary>Gets whether the column left of the block is in the tile, for a plane.</summary>
    internal bool LeftAvailable(int plane) => plane == 0 || _ssx == 0 ? _leftAvailable : _chromaLeftAvailable;

    /// <summary>Gets the block above, for a plane (the chroma one for chroma), or null.</summary>
    internal Av1BlockInfo? AboveBlock(int plane) => plane == 0 ? _above : _chromaAbove;

    /// <summary>Gets the block to the left, for a plane, or null.</summary>
    internal Av1BlockInfo? LeftBlock(int plane) => plane == 0 ? _left : _chromaLeft;

    private protected void UpdatePartitionContext(int miRow, int miCol, int subsize, int bsize)
    {
        _abovePartition.AsSpan(miCol - MiColStart, Av1Tables.MiSizeWide[bsize]).Fill(Av1Tables.PartitionContextAbove[subsize]);
        _leftPartition.AsSpan(miRow & MaxMibMask, Av1Tables.MiSizeHigh[bsize]).Fill(Av1Tables.PartitionContextLeft[subsize]);
    }

    private protected void UpdateExtPartitionContext(int miRow, int miCol, int subsize, int bsize, int partition)
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

    private protected void SetBlockPosition(int miRow, int miCol, int bw, int bh)
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
        _chromaUpAvailable = _ssy != 0 && bh < 2 ? miRow - 1 > MiRowStart : _upAvailable;
        _chromaLeftAvailable = _ssx != 0 && bw < 2 ? miCol - 1 > MiColStart : _leftAvailable;
        _chromaAbove = _chromaLeft = null;
        if (_isChromaRef)
        {
            // The chroma block's neighbours: the bottom right 4x4 unit of the luma area left of and above its chroma block.
            int baseRow = miRow - (miRow & _ssy), baseCol = miCol - (miCol & _ssx);
            _chromaAbove = _chromaUpAvailable ? _frame.Grid[((baseRow - 1) * _miCols) + baseCol + _ssx] : null;
            _chromaLeft = _chromaLeftAvailable ? _frame.Grid[((baseRow + _ssy) * _miCols) + baseCol - 1] : null;
        }

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

    private protected bool SegFeatureActive(int segment, int feature) => _header.SegmentationEnabled && _header.FeatureEnabled[segment, feature];

    /// <summary>Gets whether chroma from luma is allowed for the current block (is_cfl_allowed).</summary>
    internal bool CflAllowed() => _header.LosslessArray[_b.SegmentId]
        ? Av1Tables.PlaneBlockSize(_b.Size, _ssx, _ssy) == Bs.B4x4
        : Av1Tables.BlockSizeWide[_b.Size] <= 32 && Av1Tables.BlockSizeHigh[_b.Size] <= 32;

    private protected static int NegDeinterleave(int diff, int reference, int max)
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

    private protected int PaletteCache(int plane, Span<ushort> cache)
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

    private protected static int ColorContext(byte[] map, int stride, int r, int c, int n, Span<byte> order)
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

    private protected int TxSizeContext(int bsize)
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

    private protected int TxfmPartitionContext(int blkRow, int blkCol, int bsize, int txSize)
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

    private protected void ResetEntropyContext(int bsize)
    {
        int planes = 1 + ((_planes - 1) * (_isChromaRef ? 1 : 0));
        for (int plane = 0; plane < planes; plane++)
        {
            int planeBsize = plane == 0 ? bsize : Av1Tables.PlaneBlockSize(bsize, _ssx, _ssy);
            _aboveEntropy[plane].AsSpan(_aboveOffset[plane], Av1Tables.MiSizeWide[planeBsize]).Clear();
            _leftEntropy[plane].AsSpan(_leftOffset[plane], Av1Tables.MiSizeHigh[planeBsize]).Clear();
        }
    }
    private protected static readonly byte[] NzMapCtxOffset1d = [26, 31, 36, 36, 36, 36, 36, 36, 36, 36, 36, 36, 36, 36, 36, 36, 36, 36, 36, 36, 36, 36, 36, 36, 36, 36, 36, 36, 36, 36, 36, 36];

    private protected int SegmentQIndex(int segment, int baseQIndex) => SegFeatureActive(segment, 0)
        ? Math.Clamp(baseQIndex + _header.FeatureData[segment, 0], 0, 255)
        : baseQIndex;

    private protected static int DcQ(int qindex, int bitDepth)
    {
        int q = Math.Clamp(qindex, 0, 255);
        return bitDepth == 8 ? Av1Tables.DcQLookup8[q] : bitDepth == 10 ? Av1Tables.DcQLookup10[q] : Av1Tables.DcQLookup12[q];
    }

    private protected static int AcQ(int qindex, int bitDepth)
    {
        int q = Math.Clamp(qindex, 0, 255);
        return bitDepth == 8 ? Av1Tables.AcQLookup8[q] : bitDepth == 10 ? Av1Tables.AcQLookup10[q] : Av1Tables.AcQLookup12[q];
    }

    /// <summary>Gets a transform block's type (av1_get_tx_type).</summary>
    private protected int GetTxType(int planeType, int blkRow, int blkCol, int txSize)
    {
        Av1BlockInfo b = _b;
        if (_header.LosslessArray[b.SegmentId] || Av1Tables.TxSizeSqrUpMap[txSize] > Tx.T32x32)
        {
            return TxType.DctDct;
        }

        if (planeType == 0)
        {
            return _frame.TxTypeMap[((_miRow + blkRow) * _miCols) + _miCol + blkCol];
        }

        int txType = b.IsInter
            ? _frame.TxTypeMap[((_miRow + (blkRow << _ssy)) * _miCols) + _miCol + (blkCol << _ssx)]
            : Av1Tables.IntraModeToTxType[b.UvModeAsY];
        int setType = Av1Tables.ExtTxSetType(txSize, b.IsInter, _header.ReducedTxSet);
        return ((Av1Tables.ExtTxUsedMask[setType] >> txType) & 1) != 0 ? txType : TxType.DctDct;
    }
    private protected static readonly sbyte[] InterExtTxSetIndex = [0, 3, -1, -1, 2, 1];

    private protected readonly int[] _stackRow = new int[8];
    private protected readonly int[] _stackCol = new int[8];
    private protected readonly int[] _stackWeight = new int[8];
    private protected int _stackCount;

    /// <summary>Finds the nearest and near reference vectors of an intra block copy (av1_find_mv_refs for INTRA_FRAME).</summary>
    private protected (int NearestRow, int NearestCol, int NearRow, int NearCol) FindIntraReferenceVectors()
    {
        int bs = Math.Max(_bw, _bh);
        bool hasTopRight = HasTopRight(bs);
        int maxRowOffset = 0, maxColOffset = 0;
        int rowAdj = _bh < 2 && (_miRow & 1) != 0 ? 1 : 0;
        int colAdj = _bw < 2 && (_miCol & 1) != 0 ? 1 : 0;
        int processedRows = 0, processedCols = 0;
        _stackCount = 0;
        if (_upAvailable)
        {
            maxRowOffset = _bh < 2 ? -(2 << 1) + rowAdj : -(3 << 1) + rowAdj;
            maxRowOffset = Math.Clamp(maxRowOffset, MiRowStart - _miRow, MiRowEnd - _miRow - 1);
        }

        if (_leftAvailable)
        {
            maxColOffset = _bw < 2 ? -(2 << 1) + colAdj : -(3 << 1) + colAdj;
            maxColOffset = Math.Clamp(maxColOffset, MiColStart - _miCol, MiColEnd - _miCol - 1);
        }

        if (Math.Abs(maxRowOffset) >= 1)
        {
            ScanRow(-1, maxRowOffset, ref processedRows);
        }

        if (Math.Abs(maxColOffset) >= 1)
        {
            ScanCol(-1, maxColOffset, ref processedCols);
        }

        if (hasTopRight)
        {
            ScanBlock(-1, _bw);
        }

        int nearestCount = _stackCount;
        for (int i = 0; i < nearestCount; i++)
        {
            _stackWeight[i] += 640;
        }

        ScanBlock(-1, -1);
        for (int idx = 2; idx <= 3; idx++)
        {
            int rowOffset = -(idx << 1) + 1 + rowAdj;
            int colOffset = -(idx << 1) + 1 + colAdj;
            if (Math.Abs(rowOffset) <= Math.Abs(maxRowOffset) && Math.Abs(rowOffset) > processedRows)
            {
                ScanRow(rowOffset, maxRowOffset, ref processedRows);
            }

            if (Math.Abs(colOffset) <= Math.Abs(maxColOffset) && Math.Abs(colOffset) > processedCols)
            {
                ScanCol(colOffset, maxColOffset, ref processedCols);
            }
        }

        SortStack(0, nearestCount);
        SortStack(nearestCount, _stackCount);

        // Clamp to the block's surroundings (clamp_mv_ref), then take the first two.
        int bwPx = _bw * 4, bhPx = _bh * 4;
        for (int i = 0; i < _stackCount; i++)
        {
            _stackCol[i] = Math.Clamp(_stackCol[i], _toLeftEdge - (bwPx * 8) - 128, _toRightEdge + (bwPx * 8) + 128);
            _stackRow[i] = Math.Clamp(_stackRow[i], _toTopEdge - (bhPx * 8) - 128, _toBottomEdge + (bhPx * 8) + 128);
        }

        int nearestRow = _stackCount > 0 ? LowerPrecision(_stackRow[0]) : 0, nearestCol = _stackCount > 0 ? LowerPrecision(_stackCol[0]) : 0;
        int nearRow = _stackCount > 1 ? LowerPrecision(_stackRow[1]) : 0, nearCol = _stackCount > 1 ? LowerPrecision(_stackCol[1]) : 0;
        return (nearestRow, nearestCol, nearRow, nearCol);

        static int LowerPrecision(int v) => (v & 1) != 0 ? v + (v > 0 ? -1 : 1) : v;
    }

    private protected void SortStack(int start, int end)
    {
        int len = end;
        while (len > start)
        {
            int newLen = start;
            for (int idx = start + 1; idx < len; idx++)
            {
                if (_stackWeight[idx - 1] < _stackWeight[idx])
                {
                    (_stackRow[idx - 1], _stackRow[idx]) = (_stackRow[idx], _stackRow[idx - 1]);
                    (_stackCol[idx - 1], _stackCol[idx]) = (_stackCol[idx], _stackCol[idx - 1]);
                    (_stackWeight[idx - 1], _stackWeight[idx]) = (_stackWeight[idx], _stackWeight[idx - 1]);
                    newLen = idx;
                }
            }

            len = newLen;
        }
    }

    private protected Av1BlockInfo? GridAt(int row, int col) => _frame.Grid[((_miRow + row) * _miCols) + _miCol + col];

    private protected void AddCandidate(Av1BlockInfo? candidate, int weight)
    {
        if (candidate is null || !candidate.IsInter)
        {
            return;
        }

        int index = 0;
        for (; index < _stackCount; index++)
        {
            if (_stackRow[index] == candidate.MvRow && _stackCol[index] == candidate.MvCol)
            {
                _stackWeight[index] += weight;
                break;
            }
        }

        if (index == _stackCount && _stackCount < 8)
        {
            _stackRow[index] = candidate.MvRow;
            _stackCol[index] = candidate.MvCol;
            _stackWeight[index] = weight;
            _stackCount++;
        }
    }

    private protected void ScanRow(int rowOffset, int maxRowOffset, ref int processedRows)
    {
        int endMi = Math.Min(Math.Min(_bw, _miCols - _miCol), 16);
        int colOffset = 0;
        if (Math.Abs(rowOffset) > 1)
        {
            colOffset = 1;
            if ((_miCol & 1) != 0 && _bw < 2)
            {
                colOffset--;
            }
        }

        bool step16 = _bw >= 16;
        for (int i = 0; i < endMi;)
        {
            Av1BlockInfo? candidate = GridAt(rowOffset, colOffset + i);
            int candidateSize = candidate?.Size ?? Bs.B4x4;
            int n4w = Av1Tables.MiSizeWide[candidateSize];
            int len = Math.Min(_bw, n4w);
            len = step16 ? Math.Max(4, len) : Math.Abs(rowOffset) > 1 ? Math.Max(len, 2) : len;
            int weight = 2;
            if (_bw >= 2 && _bw <= n4w)
            {
                int inc = Math.Min(-maxRowOffset + rowOffset + 1, Av1Tables.MiSizeHigh[candidateSize]);
                weight = Math.Max(weight, inc);
                processedRows = inc - rowOffset - 1;
            }

            AddCandidate(candidate, len * weight);
            i += len;
        }
    }

    private protected void ScanCol(int colOffset, int maxColOffset, ref int processedCols)
    {
        int endMi = Math.Min(Math.Min(_bh, _frame.MiRows - _miRow), 16);
        int rowOffset = 0;
        if (Math.Abs(colOffset) > 1)
        {
            rowOffset = 1;
            if ((_miRow & 1) != 0 && _bh < 2)
            {
                rowOffset--;
            }
        }

        bool step16 = _bh >= 16;
        for (int i = 0; i < endMi;)
        {
            Av1BlockInfo? candidate = GridAt(rowOffset + i, colOffset);
            int candidateSize = candidate?.Size ?? Bs.B4x4;
            int n4h = Av1Tables.MiSizeHigh[candidateSize];
            int len = Math.Min(_bh, n4h);
            len = step16 ? Math.Max(4, len) : Math.Abs(colOffset) > 1 ? Math.Max(len, 2) : len;
            int weight = 2;
            if (_bh >= 2 && _bh <= n4h)
            {
                int inc = Math.Min(-maxColOffset + colOffset + 1, Av1Tables.MiSizeWide[candidateSize]);
                weight = Math.Max(weight, inc);
                processedCols = inc - colOffset - 1;
            }

            AddCandidate(candidate, len * weight);
            i += len;
        }
    }

    private protected void ScanBlock(int rowOffset, int colOffset)
    {
        int row = _miRow + rowOffset, col = _miCol + colOffset;
        if (row >= MiRowStart && col >= MiColStart && row < MiRowEnd && col < MiColEnd)
        {
            AddCandidate(GridAt(rowOffset, colOffset), 2 * 2);
        }
    }

    private protected bool HasTopRight(int bs)
    {
        int sbMiSize = _sbMi;
        int maskRow = _miRow & (sbMiSize - 1), maskCol = _miCol & (sbMiSize - 1);
        if (bs > 16)
        {
            return false;
        }

        bool hasTr = !((maskRow & bs) != 0 && (maskCol & bs) != 0);
        while (bs < sbMiSize)
        {
            if ((maskCol & bs) != 0)
            {
                if ((maskCol & (2 * bs)) != 0 && (maskRow & (2 * bs)) != 0)
                {
                    hasTr = false;
                    break;
                }
            }
            else
            {
                break;
            }

            bs <<= 1;
        }

        if (_bw < _bh && !_isLastVerticalRect)
        {
            hasTr = true;
        }

        if (_bw > _bh && !_isFirstHorizontalRect)
        {
            hasTr = false;
        }

        if (_b.Partition == Partition.VertA && _bw == _bh && (maskRow & bs) != 0)
        {
            hasTr = false;
        }

        return hasTr;
    }

    /// <summary>Gets the partition context of a square block (partition_plane_context).</summary>
    private protected int PartitionContext(int miRow, int miCol, int bsize)
    {
        int bsl = Av1Tables.MiSizeWideLog2[bsize] - 1;
        int above = (_abovePartition[miCol - MiColStart] >> bsl) & 1;
        int left = (_leftPartition[miRow & MaxMibMask] >> bsl) & 1;
        return (left * 2) + above + (bsl * 4);
    }

    /// <summary>Gets how many partition types a square block size codes from.</summary>
    private protected static int PartitionSymbols(int bsize) => bsize <= Bs.B8x8 ? 4 : bsize == Bs.B128x128 ? 8 : 10;

    /// <summary>
    /// Gets the first entry of the two-symbol CDF a block at the frame's bottom or right edge codes its split flag with: the
    /// probability of the partitions that split the missing direction, gathered from the partition CDF at <paramref name="at"/>
    /// (partition_gather_vert_alike and partition_gather_horz_alike).
    /// </summary>
    private protected ushort GatheredPartitionCdf(int at, int bsize, bool hasRows)
    {
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

        return (ushort)(32768 - p);
    }

    /// <summary>Gets the skip_txfm context: how many of the blocks above and left skip their residual.</summary>
    private protected int SkipContext() => (_above?.Skip == true ? 1 : 0) + (_left?.Skip == true ? 1 : 0);

    /// <summary>Gets the key frame luma mode context, from the modes above and left.</summary>
    private protected int KfYModeContext()
    {
        int aboveMode = _above?.YMode ?? Pred.Dc, leftMode = _left?.YMode ?? Pred.Dc;
        return (Av1Tables.IntraModeContext[aboveMode] * 5) + Av1Tables.IntraModeContext[leftMode];
    }

    /// <summary>Gets the intra segment id's CDF index and predicted segment, from the segments above, left and above left.</summary>
    private protected (int CdfIndex, int Pred) SegmentPrediction()
    {
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
        return (cdfIndex, pred);
    }

    /// <summary>Gets a transform block's all-zero and DC sign contexts from the entropy contexts above and left (get_txb_ctx).</summary>
    private protected (int SkipCtx, int DcSignCtx) TxbContext(int plane, int blkRow, int blkCol, int txSize)
    {
        int planeBsize = plane == 0 ? _b.Size : Av1Tables.PlaneBlockSize(_b.Size, _ssx, _ssy);
        byte[] above = _aboveEntropy[plane], left = _leftEntropy[plane];
        int aOff = _aboveOffset[plane] + blkCol, lOff = _leftOffset[plane] + blkRow;
        int txw = Av1Tables.TxSizeWideUnit[txSize], txh = Av1Tables.TxSizeHighUnit[txSize];
        int dcSign = 0;
        for (int k = 0; k < txw; k++)
        {
            int sign = above[aOff + k] >> 3;
            dcSign += sign == 1 ? -1 : sign == 2 ? 1 : 0;
        }

        for (int k = 0; k < txh; k++)
        {
            int sign = left[lOff + k] >> 3;
            dcSign += sign == 1 ? -1 : sign == 2 ? 1 : 0;
        }

        int dcSignCtx = dcSign < 0 ? 1 : dcSign > 0 ? 2 : 0;
        int skipCtx;
        if (plane == 0)
        {
            if (planeBsize == Av1Tables.TxSizeToBlockSize[txSize])
            {
                skipCtx = 0;
            }
            else
            {
                int top = 0, leftCtx = 0;
                for (int k = 0; k < txw; k++)
                {
                    top |= above[aOff + k];
                }

                for (int k = 0; k < txh; k++)
                {
                    leftCtx |= left[lOff + k];
                }

                skipCtx = SkipContexts[Math.Min(top & 7, 4)][Math.Min(leftCtx & 7, 4)];
            }
        }
        else
        {
            int aboveAny = 0, leftAny = 0;
            for (int k = 0; k < txw; k++)
            {
                aboveAny |= above[aOff + k];
            }

            for (int k = 0; k < txh; k++)
            {
                leftAny |= left[lOff + k];
            }

            int ctxBase = (aboveAny != 0 ? 1 : 0) + (leftAny != 0 ? 1 : 0);
            skipCtx = ctxBase + (Av1Tables.NumPelsLog2Lookup[planeBsize] > Av1Tables.NumPelsLog2Lookup[Av1Tables.TxSizeToBlockSize[txSize]] ? 10 : 7);
        }

        return (skipCtx, dcSignCtx);
    }

    /// <summary>Records a coded transform block's level and DC sign in the entropy contexts above and left (av1_set_entropy_contexts).</summary>
    private protected void SetEntropyContexts(int plane, int blkRow, int blkCol, int txSize, int culLevel)
    {
        int planeBsize = plane == 0 ? _b.Size : Av1Tables.PlaneBlockSize(_b.Size, _ssx, _ssy);
        byte[] above = _aboveEntropy[plane], left = _leftEntropy[plane];
        int aOff = _aboveOffset[plane] + blkCol, lOff = _leftOffset[plane] + blkRow;
        int txw = Av1Tables.TxSizeWideUnit[txSize], txh = Av1Tables.TxSizeHighUnit[txSize];
        int aboveCount = txw;
        if (culLevel != 0 && _toRightEdge < 0)
        {
            aboveCount = Math.Clamp(MaxBlockWide(planeBsize, plane) - blkCol, 0, txw);
        }

        above.AsSpan(aOff, aboveCount).Fill((byte)culLevel);
        above.AsSpan(aOff + aboveCount, txw - aboveCount).Clear();
        int leftCount = txh;
        if (culLevel != 0 && _toBottomEdge < 0)
        {
            leftCount = Math.Clamp(MaxBlockHigh(planeBsize, plane) - blkRow, 0, txh);
        }

        left.AsSpan(lOff, leftCount).Fill((byte)culLevel);
        left.AsSpan(lOff + leftCount, txh - leftCount).Clear();
    }

    /// <summary>Gets the vector an intra block copy vector is coded against: the nearest, else the near, else av1_find_ref_dv's.</summary>
    private protected (int Row, int Col) IntrabcReference()
    {
        (int nearestRow, int nearestCol, int nearRow, int nearCol) = FindIntraReferenceVectors();
        (int refRow, int refCol) = nearestRow != 0 || nearestCol != 0 ? (nearestRow, nearestCol) : (nearRow, nearCol);
        if (refRow == 0 && refCol == 0)
        {
            // av1_find_ref_dv
            if (_miRow - _sbMi < MiRowStart)
            {
                refRow = 0;
                refCol = ((-4 * _sbMi) - 256) * 8;
            }
            else
            {
                refRow = -4 * _sbMi * 8;
                refCol = 0;
            }
        }

        return (refRow, refCol);
    }
}
