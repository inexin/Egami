using System.Runtime.CompilerServices;

namespace Egami.Video.Codecs.Vp9;

/// <summary>Tile decoding: the partition tree and each block's parse and reconstruction (vp9_decodeframe.c's decode_partition and decode_block).</summary>
internal sealed partial class Vp9Decoder<T>
{
    /// <summary>
    /// One tile column's decoder: its boolean decoder, left contexts, counts and the scratch the blocks need. Tile columns touch
    /// disjoint parts of the frame-wide above contexts, so separate tile columns can run on separate threads.
    /// </summary>
    internal sealed partial class Tile
    {
        private readonly Vp9Decoder<T> _d;
        private readonly byte[][] _leftContext = [new byte[16], new byte[16], new byte[16]];
        private readonly byte[] _leftPartition = new byte[8];
        private readonly int[] _dq = new int[32 * 32];
        private readonly byte[] _tokenCache = new byte[32 * 32];
        private readonly T[] _mcBuffer = new T[80 * 2 * 80 * 2];
        private readonly int[] _convolveTemp = new int[64 * 135];
        private readonly T[] _predictTemp = new T[64 * 64];
        private Vp9BoolDecoder _r;

        // The block being decoded (MACROBLOCKD).
        private int _miRow;
        private int _miCol;
        private int _miIndex;
        private int _aboveMi;
        private int _leftMi;
        private int _toLeftEdge;
        private int _toRightEdge;
        private int _toTopEdge;
        private int _toBottomEdge;
        private int _bmodeBlocksWl;
        private int _bmodeBlocksHl;
        private int _maxBlocksWide;
        private int _maxBlocksHigh;
        private readonly int[] _n4W = new int[3];
        private readonly int[] _n4H = new int[3];
        private readonly int[] _n4Wl = new int[3];
        private readonly int[] _n4Hl = new int[3];
        private readonly int[] _ssX = new int[3];
        private readonly int[] _ssY = new int[3];

        public Tile(Vp9Decoder<T> decoder)
        {
            _d = decoder;
        }

        /// <summary>Gets the tile's symbol counts.</summary>
        public Vp9Counts Counts { get; } = new();

        /// <summary>Gets or sets whether counts are kept (not in frame parallel or error resilient mode).</summary>
        public bool CountsEnabled { get; set; }

        public int MiColStart { get; private set; }

        public int MiColEnd { get; private set; }

        /// <summary>Gets whether the tile's data ran out or broke a rule.</summary>
        public bool Corrupted { get; private set; }

        /// <summary>Gets the offset just past the data the tile's boolean decoder used.</summary>
        public int End => _r.FindEnd();

        public void Start(int dataStart, int dataLength, int miColStart, int miColEnd)
        {
            if (_r.Init(_d._data, dataStart, dataLength))
            {
                throw new CorruptDataException("VP9", dataStart, "A tile's marker bit is set.");
            }

            MiColStart = miColStart;
            MiColEnd = miColEnd;
            Corrupted = false;
            if (CountsEnabled)
            {
                Counts.Clear();
            }

            for (int p = 0; p < 3; p++)
            {
                _ssX[p] = p == 0 ? 0 : _d._subX;
                _ssY[p] = p == 0 ? 0 : _d._subY;
            }
        }

        /// <summary>Decodes the tile's superblocks of one superblock row.</summary>
        public void DecodeRow(int miRow)
        {
            for (int p = 0; p < 3; p++)
            {
                Array.Clear(_leftContext[p]);
            }

            Array.Clear(_leftPartition);
            for (int miCol = MiColStart; miCol < MiColEnd; miCol += 8)
            {
                DecodePartition(miRow, miCol, Vp9Tables.Block64x64, 4);
            }

            if (_r.HasError)
            {
                Corrupted = true;
            }
        }

        private void DecodePartition(int miRow, int miCol, int bsize, int n4x4L2)
        {
            Vp9Decoder<T> d = _d;
            int n8x8L2 = n4x4L2 - 1;
            int num8x8 = 1 << n8x8L2;
            int hbs = num8x8 >> 1;
            if (miRow >= d._miRows || miCol >= d._miCols)
            {
                return;
            }

            bool hasRows = (miRow + hbs) < d._miRows;
            bool hasCols = (miCol + hbs) < d._miCols;
            int partition = ReadPartition(miRow, miCol, hasRows, hasCols, n8x8L2);
            int subsize = Vp9Tables.SubsizeLookup[(partition * Vp9Tables.BlockSizes) + bsize];
            if (hbs == 0)
            {
                _bmodeBlocksWl = (partition & Vp9Tables.PartitionVert) != 0 ? 0 : 1;
                _bmodeBlocksHl = (partition & Vp9Tables.PartitionHorz) != 0 ? 0 : 1;
                DecodeBlock(miRow, miCol, subsize, 1, 1);
            }
            else
            {
                switch (partition)
                {
                    case Vp9Tables.PartitionNone:
                        DecodeBlock(miRow, miCol, subsize, n4x4L2, n4x4L2);
                        break;
                    case Vp9Tables.PartitionHorz:
                        DecodeBlock(miRow, miCol, subsize, n4x4L2, n8x8L2);
                        if (hasRows)
                        {
                            DecodeBlock(miRow + hbs, miCol, subsize, n4x4L2, n8x8L2);
                        }

                        break;
                    case Vp9Tables.PartitionVert:
                        DecodeBlock(miRow, miCol, subsize, n8x8L2, n4x4L2);
                        if (hasCols)
                        {
                            DecodeBlock(miRow, miCol + hbs, subsize, n8x8L2, n4x4L2);
                        }

                        break;
                    default:
                        DecodePartition(miRow, miCol, subsize, n8x8L2);
                        DecodePartition(miRow, miCol + hbs, subsize, n8x8L2);
                        DecodePartition(miRow + hbs, miCol, subsize, n8x8L2);
                        DecodePartition(miRow + hbs, miCol + hbs, subsize, n8x8L2);
                        break;
                }
            }

            if (bsize >= Vp9Tables.Block8x8 && (bsize == Vp9Tables.Block8x8 || partition != Vp9Tables.PartitionSplit))
            {
                byte above = Vp9Tables.PartitionContextLookup[subsize * 2], left = Vp9Tables.PartitionContextLookup[(subsize * 2) + 1];
                d._abovePartition.AsSpan(miCol, num8x8).Fill(above);
                _leftPartition.AsSpan(miRow & 7, num8x8).Fill(left);
            }
        }

        private int ReadPartition(int miRow, int miCol, bool hasRows, bool hasCols, int bsl)
        {
            int above = (_d._abovePartition[miCol] >> bsl) & 1, left = (_leftPartition[miRow & 7] >> bsl) & 1;
            int ctx = (left * 2) + above + (bsl * 4);
            ReadOnlySpan<byte> probs = _d._header.FrameIsIntraOnly
                ? Vp9Tables.KfPartitionProbs.AsSpan(ctx * 3, 3)
                : _d._fc.P.AsSpan(Vp9FrameContext.Partition + (ctx * 3), 3);
            int p;
            if (hasRows && hasCols)
            {
                p = _r.ReadTree(Vp9Tables.PartitionTree, probs);
            }
            else if (!hasRows && hasCols)
            {
                p = _r.Read(probs[1]) != 0 ? Vp9Tables.PartitionSplit : Vp9Tables.PartitionHorz;
            }
            else if (hasRows && !hasCols)
            {
                p = _r.Read(probs[2]) != 0 ? Vp9Tables.PartitionSplit : Vp9Tables.PartitionVert;
            }
            else
            {
                p = Vp9Tables.PartitionSplit;
            }

            if (CountsEnabled)
            {
                Counts.Partition[(ctx * 4) + p]++;
            }

            return p;
        }

        private void DecodeBlock(int miRow, int miCol, int bsize, int bwl, int bhl)
        {
            Vp9Decoder<T> d = _d;
            int bw = 1 << (bwl - 1), bh = 1 << (bhl - 1);
            int xMis = Math.Min(bw, d._miCols - miCol), yMis = Math.Min(bh, d._miRows - miRow);
            int cols = d._miCols;
            int index = (miRow * cols) + miCol;
            _miRow = miRow;
            _miCol = miCol;
            _miIndex = index;
            for (int y = 0; y < yMis; y++)
            {
                d._miGrid.AsSpan(index + (y * cols), xMis).Fill(index);
            }

            ref Vp9ModeInfo mi = ref d._mi[index];
            mi.SbType = (byte)bsize;
            for (int p = 0; p < 3; p++)
            {
                _n4W[p] = (bw << 1) >> _ssX[p];
                _n4H[p] = (bh << 1) >> _ssY[p];
                _n4Wl[p] = bwl - _ssX[p];
                _n4Hl[p] = bhl - _ssY[p];
            }

            _toTopEdge = -(miRow * 8 * 8);
            _toBottomEdge = (d._miRows - bh - miRow) * 8 * 8;
            _toLeftEdge = -(miCol * 8 * 8);
            _toRightEdge = (d._miCols - bw - miCol) * 8 * 8;
            _aboveMi = miRow != 0 ? d._miGrid[index - cols] : -1;
            _leftMi = miCol > MiColStart ? d._miGrid[index - 1] : -1;

            if (bsize >= Vp9Tables.Block8x8 && (d._subX != 0 || d._subY != 0) &&
                Vp9Tables.SsSizeLookup[(bsize * 4) + (d._subX * 2) + d._subY] == Vp9Tables.BlockInvalid)
            {
                throw new CorruptDataException("VP9", -1, "A block size has no chroma block at this subsampling.");
            }

            ReadModeInfo(miRow, miCol, xMis, yMis);

            if (mi.Skip != 0)
            {
                ResetSkipContext();
            }

            if (!mi.IsInter)
            {
                for (int plane = 0; plane < 3; plane++)
                {
                    int txSize = plane == 0 ? mi.TxSize : UVTxSize(ref mi, plane);
                    int step = 1 << txSize;
                    SetMaxBlocks(plane, out int maxWide, out int maxHigh);
                    for (int row = 0; row < maxHigh; row += step)
                    {
                        for (int col = 0; col < maxWide; col += step)
                        {
                            PredictAndReconstructIntra(ref mi, plane, row, col, txSize);
                        }
                    }
                }
            }
            else
            {
                BuildInterPredictors(ref mi, miRow, miCol);
                if (mi.Skip == 0)
                {
                    int eobTotal = 0;
                    for (int plane = 0; plane < 3; plane++)
                    {
                        int txSize = plane == 0 ? mi.TxSize : UVTxSize(ref mi, plane);
                        int step = 1 << txSize;
                        SetMaxBlocks(plane, out int maxWide, out int maxHigh);
                        for (int row = 0; row < maxHigh; row += step)
                        {
                            for (int col = 0; col < maxWide; col += step)
                            {
                                eobTotal += ReconstructInter(ref mi, plane, row, col, txSize);
                            }
                        }
                    }

                    if (bsize >= Vp9Tables.Block8x8 && eobTotal == 0)
                    {
                        mi.Skip = 1;
                    }
                }
            }

            if (_r.HasError)
            {
                Corrupted = true;
            }

            if (d._header.LoopFilterLevel != 0)
            {
                d._loopFilter.BuildMask(ref mi, miRow, miCol, bw, bh);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int UVTxSize(ref Vp9ModeInfo mi, int plane) =>
            Vp9Tables.UVTxSizeLookup[(mi.SbType * 16) + (mi.TxSize * 4) + (_ssX[plane] * 2) + _ssY[plane]];

        private void SetMaxBlocks(int plane, out int maxWide, out int maxHigh)
        {
            maxWide = _n4W[plane] + (_toRightEdge >= 0 ? 0 : _toRightEdge >> (5 + _ssX[plane]));
            maxHigh = _n4H[plane] + (_toBottomEdge >= 0 ? 0 : _toBottomEdge >> (5 + _ssY[plane]));
            _maxBlocksWide = _toRightEdge >= 0 ? 0 : maxWide;
            _maxBlocksHigh = _toBottomEdge >= 0 ? 0 : maxHigh;
        }

        private void ResetSkipContext()
        {
            for (int p = 0; p < 3; p++)
            {
                _d._aboveContext[p].AsSpan(AboveContextIndex(p), _n4W[p]).Clear();
                _leftContext[p].AsSpan(LeftContextIndex(p), _n4H[p]).Clear();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int AboveContextIndex(int plane) => (_miCol * 2) >> _ssX[plane];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int LeftContextIndex(int plane) => ((_miRow * 2) & 15) >> _ssY[plane];
    }
}
