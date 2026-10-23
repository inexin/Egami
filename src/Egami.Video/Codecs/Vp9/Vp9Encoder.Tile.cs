using System.Runtime.CompilerServices;

namespace Egami.Video.Codecs.Vp9;

/// <summary>One tile column's encoder: its left contexts, the partition search and the tokens it codes.</summary>
internal sealed partial class Vp9Encoder<T>
{
    /// <summary>A rate in 1/512 bits and a distortion in 16 times the squared error (libvpx's units), with their RD cost.</summary>
    internal readonly record struct Rd(int Rate, long Dist)
    {
        public static readonly Rd Max = new(int.MaxValue / 4, long.MaxValue / 1024);

        public bool IsMax => Rate >= int.MaxValue / 4;

        public static Rd operator +(Rd a, Rd b) => a.IsMax || b.IsMax ? Max : new Rd(a.Rate + b.Rate, a.Dist + b.Dist);
    }

    /// <summary>One node of a superblock's partition tree: the chosen partition and its blocks' modes.</summary>
    private sealed class Node
    {
        public int Partition;
        public Vp9ModeInfo Block0;
        public Vp9ModeInfo Block1;
        public Vp9ModeInfo Best0;
        public Vp9ModeInfo Best1;
    }

    internal sealed partial class Tile
    {
        private readonly Vp9Encoder<T> _e;
        private readonly byte[][] _leftContext = [new byte[16], new byte[16], new byte[16]];
        private readonly byte[] _leftPartition = new byte[8];

        // The block sizes the current superblock's search may use (rd_auto_partition_range).
        private int _minSize;
        private int _maxSize = Vp9Tables.Block64x64;

        // The block being coded (MACROBLOCKD).
        private int _miRow;
        private int _miCol;
        private int _miIndex;
        private int _aboveMi;
        private int _leftMi;
        private int _toLeftEdge;
        private int _toRightEdge;
        private int _toTopEdge;
        private int _toBottomEdge;
        private int _bsize;
        private readonly int[] _n4W = new int[3];
        private readonly int[] _n4H = new int[3];
        private readonly int[] _n4Wl = new int[3];
        private readonly int[] _n4Hl = new int[3];
        private readonly int[] _ssX = new int[3];
        private readonly int[] _ssY = new int[3];

        // Search state saved per partition level: 64, 32, 16 and 8.
        private readonly Node[][] _nodes = [new Node[1], new Node[4], new Node[16], new Node[64]];
        private readonly byte[][][] _savedAbove = new byte[4][][];
        private readonly byte[][][] _savedLeft = new byte[4][][];
        private readonly byte[][] _savedAbovePartition = new byte[4][];
        private readonly byte[][] _savedLeftPartition = new byte[4][];

        // Scratch.
        private readonly short[] _residual = new short[64 * 64];
        private readonly int[] _coeff = new int[32 * 32];
        private readonly int[] _dqcoeff = new int[32 * 32];
        private readonly int[] _qcoeff = new int[32 * 32];
        private readonly int[] _bestQcoeff = new int[32 * 32];
        private readonly byte[] _tokenCache = new byte[32 * 32];
        private readonly byte[] _ta = new byte[16];
        private readonly byte[] _tl = new byte[16];
        private readonly byte[] _ta2 = new byte[16];
        private readonly byte[] _tl2 = new byte[16];
        private readonly T[][] _pred = [new T[64 * 64], new T[64 * 64], new T[64 * 64]];
        private readonly T[] _predTemp = new T[64 * 64];
        private readonly int[] _convolveTemp = new int[64 * 135];
        private readonly T[] _mcBuffer = new T[80 * 2 * 80 * 2];

        // The tokens the tile codes, in coding order: per transform block its end of block, then its values in scan order.
        private int[] _tokens = new int[1 << 16];
        private int _tokenCount;
        private bool _output;

        public Tile(Vp9Encoder<T> encoder)
        {
            _e = encoder;
            for (int d = 0; d < 4; d++)
            {
                for (int i = 0; i < _nodes[d].Length; i++)
                {
                    _nodes[d][i] = new Node();
                }

                _savedAbove[d] = [new byte[16], new byte[16], new byte[16]];
                _savedLeft[d] = [new byte[16], new byte[16], new byte[16]];
                _savedAbovePartition[d] = new byte[8];
                _savedLeftPartition[d] = new byte[8];
            }
        }

        /// <summary>Gets the tile's symbol counts, for the probability updates.</summary>
        public Vp9Counts Counts { get; } = new();

        public int MiColStart { get; private set; }

        public int MiColEnd { get; private set; }

        /// <summary>Gets the tile column the worker codes.</summary>
        public int TileColumn { get; private set; }

        public void Start(int tileColumn, int miColStart, int miColEnd)
        {
            TileColumn = tileColumn;
            MiColStart = miColStart;
            MiColEnd = miColEnd;
            _tokenCount = 0;
            for (int p = 0; p < 3; p++)
            {
                _ssX[p] = p == 0 ? 0 : _e._subX;
                _ssY[p] = p == 0 ? 0 : _e._subY;
            }
        }

        /// <summary>
        /// Searches and codes the tile's superblocks of one superblock row, keeping its tokens in the row's buffer. With
        /// <paramref name="progress"/>, each superblock waits until the row above (entry <paramref name="above"/>) has coded the
        /// superblock above and to its right, and the row's own count (entry <paramref name="self"/>) is published as it goes.
        /// </summary>
        public void EncodeRow(int miRow, int[]? progress = null, int above = -1, int self = -1, Func<bool>? failed = null)
        {
            Vp9Encoder<T> e = _e;
            for (int p = 0; p < 3; p++)
            {
                Array.Clear(_leftContext[p]);
            }

            Array.Clear(_leftPartition);
            int sbRow = miRow >> 3;
            _tokens = e._rowTokens[TileColumn][sbRow];
            _tokenCount = 0;
            int sbColumns = (MiColEnd - MiColStart + 7) >> 3;
            int k = 0;
            for (int miCol = MiColStart; miCol < MiColEnd; miCol += 8, k++)
            {
                if (progress is not null && above >= 0)
                {
                    int needed = Math.Min(k + 2, sbColumns);
                    SpinWait spin = default;
                    while (Volatile.Read(ref progress[above]) < needed)
                    {
                        if (failed?.Invoke() == true)
                        {
                            return;
                        }

                        spin.SpinOnce(sleep1Threshold: -1);
                    }
                }

                _output = false;
                (_minSize, _maxSize) = e.PartitionRange(miRow, miCol);
                SearchPartition(miRow, miCol, Vp9Tables.Block64x64, 0, 0, Rd.Max);

                // Code the chosen tree again, now keeping its tokens.
                RestoreContext(miRow, miCol, Vp9Tables.Block64x64, 0);
                _output = true;
                EncodeTree(miRow, miCol, Vp9Tables.Block64x64, 0, 0);
                _output = false;
                if (progress is not null)
                {
                    Volatile.Write(ref progress[self], k + 1);
                }
            }

            e._rowTokens[TileColumn][sbRow] = _tokens;
            e._rowTokenCounts[TileColumn][sbRow] = _tokenCount;
        }

        // Block setup, as the decoder's DecodeBlock does it.

        private void SetBlock(int miRow, int miCol, int bsize)
        {
            Vp9Encoder<T> e = _e;
            int bwl = Math.Max(1, (int)Vp9Tables.BWidthLog2Lookup[bsize]), bhl = Math.Max(1, (int)Vp9Tables.BHeightLog2Lookup[bsize]);
            int bw = 1 << (bwl - 1), bh = 1 << (bhl - 1);
            int cols = e._miCols;
            _miRow = miRow;
            _miCol = miCol;
            _bsize = bsize;
            _miIndex = (miRow * cols) + miCol;
            for (int p = 0; p < 3; p++)
            {
                _n4W[p] = (bw << 1) >> _ssX[p];
                _n4H[p] = (bh << 1) >> _ssY[p];
                _n4Wl[p] = bwl - _ssX[p];
                _n4Hl[p] = bhl - _ssY[p];
            }

            _toTopEdge = -(miRow * 8 * 8);
            _toBottomEdge = (e._miRows - bh - miRow) * 8 * 8;
            _toLeftEdge = -(miCol * 8 * 8);
            _toRightEdge = (e._miCols - bw - miCol) * 8 * 8;
            _aboveMi = miRow != 0 ? e._miGrid[_miIndex - cols] : -1;
            _leftMi = miCol > MiColStart ? e._miGrid[_miIndex - 1] : -1;
        }

        /// <summary>Points the block's area of the mode info grid at its top-left entry and stores its mode info there.</summary>
        private void PlaceBlock(ref Vp9ModeInfo mi)
        {
            Vp9Encoder<T> e = _e;
            int bw = 1 << (Math.Max(1, (int)Vp9Tables.BWidthLog2Lookup[_bsize]) - 1);
            int bh = 1 << (Math.Max(1, (int)Vp9Tables.BHeightLog2Lookup[_bsize]) - 1);
            int xMis = Math.Min(bw, e._miCols - _miCol), yMis = Math.Min(bh, e._miRows - _miRow);
            int cols = e._miCols;
            for (int y = 0; y < yMis; y++)
            {
                e._miGrid.AsSpan(_miIndex + (y * cols), xMis).Fill(_miIndex);
                e._segmentMap.AsSpan(_miIndex + (y * cols), xMis).Fill(mi.SegmentId);
            }

            e._mi[_miIndex] = mi;
        }

        private void MaxBlocks(int plane, out int maxWide, out int maxHigh)
        {
            maxWide = _n4W[plane] + (_toRightEdge >= 0 ? 0 : _toRightEdge >> (5 + _ssX[plane]));
            maxHigh = _n4H[plane] + (_toBottomEdge >= 0 ? 0 : _toBottomEdge >> (5 + _ssY[plane]));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int AboveContextIndex(int plane) => (_miCol * 2) >> _ssX[plane];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int LeftContextIndex(int plane) => ((_miRow * 2) & 15) >> _ssY[plane];

        /// <summary>Copies the block's above and left nonzero contexts of a plane into <paramref name="above"/> and <paramref name="left"/>.</summary>
        private void LoadContexts(int plane, Span<byte> above, Span<byte> left)
        {
            _e._aboveContext[plane].AsSpan(AboveContextIndex(plane), _n4W[plane]).CopyTo(above);
            _leftContext[plane].AsSpan(LeftContextIndex(plane), _n4H[plane]).CopyTo(left);
        }

        private void StoreContexts(int plane, ReadOnlySpan<byte> above, ReadOnlySpan<byte> left)
        {
            above[.._n4W[plane]].CopyTo(_e._aboveContext[plane].AsSpan(AboveContextIndex(plane)));
            left[.._n4H[plane]].CopyTo(_leftContext[plane].AsSpan(LeftContextIndex(plane)));
        }

        /// <summary>The token context of a transform block from the block's contexts (get_entropy_context).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int EntropyContext(ReadOnlySpan<byte> above, ReadOnlySpan<byte> left, int x, int y, int n)
        {
            int a = 0, l = 0;
            for (int i = 0; i < n; i++)
            {
                a |= above[x + i];
                l |= left[y + i];
            }

            return (a != 0 ? 1 : 0) + (l != 0 ? 1 : 0);
        }

        /// <summary>Sets a transform block's contexts after it is coded, cleared past the frame's edge as the decoder does.</summary>
        private void SetEntropyContext(int plane, Span<byte> above, Span<byte> left, int x, int y, int n, bool has)
        {
            int maxWide = _toRightEdge >= 0 ? 0 : _n4W[plane] + (_toRightEdge >> (5 + _ssX[plane]));
            int maxHigh = _toBottomEdge >= 0 ? 0 : _n4H[plane] + (_toBottomEdge >> (5 + _ssY[plane]));
            int aboveCount = n, leftCount = n;
            if (maxWide != 0 && n + x > maxWide)
            {
                aboveCount = Math.Max(0, maxWide - x);
            }

            if (maxHigh != 0 && n + y > maxHigh)
            {
                leftCount = Math.Max(0, maxHigh - y);
            }

            byte v = (byte)(has ? 1 : 0);
            for (int i = 0; i < n; i++)
            {
                above[x + i] = i < aboveCount ? v : (byte)0;
                left[y + i] = i < leftCount ? v : (byte)0;
            }
        }

        // Context saving around the partition search.

        private void SaveContext(int miRow, int miCol, int bsize, int depth)
        {
            int num8x8 = 8 >> depth;
            for (int p = 0; p < 3; p++)
            {
                int ax = (miCol * 2) >> _ssX[p], ly = ((miRow * 2) & 15) >> _ssY[p];
                _e._aboveContext[p].AsSpan(ax, (num8x8 * 2) >> _ssX[p]).CopyTo(_savedAbove[depth][p]);
                _leftContext[p].AsSpan(ly, (num8x8 * 2) >> _ssY[p]).CopyTo(_savedLeft[depth][p]);
            }

            _e._abovePartition.AsSpan(miCol, num8x8).CopyTo(_savedAbovePartition[depth]);
            _leftPartition.AsSpan(miRow & 7, num8x8).CopyTo(_savedLeftPartition[depth]);
            _ = bsize;
        }

        private void RestoreContext(int miRow, int miCol, int bsize, int depth)
        {
            int num8x8 = 8 >> depth;
            for (int p = 0; p < 3; p++)
            {
                int ax = (miCol * 2) >> _ssX[p], ly = ((miRow * 2) & 15) >> _ssY[p];
                _savedAbove[depth][p].AsSpan(0, (num8x8 * 2) >> _ssX[p]).CopyTo(_e._aboveContext[p].AsSpan(ax));
                _savedLeft[depth][p].AsSpan(0, (num8x8 * 2) >> _ssY[p]).CopyTo(_leftContext[p].AsSpan(ly));
            }

            _savedAbovePartition[depth].AsSpan(0, num8x8).CopyTo(_e._abovePartition.AsSpan(miCol));
            _savedLeftPartition[depth].AsSpan(0, num8x8).CopyTo(_leftPartition.AsSpan(miRow & 7));
            _ = bsize;
        }

        private void UpdatePartitionContext(int miRow, int miCol, int subsize, int bsize)
        {
            int num8x8 = Vp9Tables.Num8x8BlocksWideLookup[bsize];
            byte above = Vp9Tables.PartitionContextLookup[subsize * 2], left = Vp9Tables.PartitionContextLookup[(subsize * 2) + 1];
            _e._abovePartition.AsSpan(miCol, num8x8).Fill(above);
            _leftPartition.AsSpan(miRow & 7, num8x8).Fill(left);
        }

        /// <summary>The partition's cost at a node, by the frame's probabilities and what the frame's edge allows.</summary>
        private int PartitionCost(int miRow, int miCol, int bsize, int partition, bool hasRows, bool hasCols)
        {
            int bsl = Vp9Tables.BWidthLog2Lookup[bsize] - 1;
            int above = (_e._abovePartition[miCol] >> bsl) & 1, left = (_leftPartition[miRow & 7] >> bsl) & 1;
            int ctx = (left * 2) + above + (bsl * 4);
            if (hasRows && hasCols)
            {
                return _e._costs.Partition[(ctx * 4) + partition];
            }

            ReadOnlySpan<byte> probs = _e._frameIsIntra
                ? Vp9Tables.KfPartitionProbs.AsSpan(ctx * 3, 3)
                : _e._fc.P.AsSpan(Vp9FrameContext.Partition + (ctx * 3), 3);
            if (!hasRows && hasCols)
            {
                return Vp9EncoderTables.Cost(probs[1], partition == Vp9Tables.PartitionSplit ? 1 : 0);
            }

            if (hasRows && !hasCols)
            {
                return Vp9EncoderTables.Cost(probs[2], partition == Vp9Tables.PartitionSplit ? 1 : 0);
            }

            return 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private long RdCost(Rd rd) => RdCost(rd.Rate, rd.Dist);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private long RdCost(int rate, long dist) => (((long)rate * _e._rdMult) + 256 >> 9) + (dist << 7);

        /// <summary>
        /// Searches the partitions of a node and leaves the best one coded (recon, contexts and mode info as if coded), returning
        /// its rate and distortion, or <see cref="Rd.Max"/> when nothing beats <paramref name="bestLimit"/>.
        /// </summary>
        private Rd SearchPartition(int miRow, int miCol, int bsize, int depth, int index, Rd bestLimit)
        {
            Vp9Encoder<T> e = _e;
            Node node = _nodes[depth][index];
            int num8x8 = 8 >> depth;
            int hbs = num8x8 >> 1;
            bool hasRows = (miRow + hbs) < e._miRows;
            bool hasCols = (miCol + hbs) < e._miCols;
            int effort = e._plan.Effort;
            SaveContext(miRow, miCol, bsize, depth);

            long bestCost = bestLimit.IsMax ? long.MaxValue : RdCost(bestLimit);
            Rd best = Rd.Max;
            int bestPartition = -1;
            int lastCoded = -1;

            bool allowNone = hasRows && hasCols && bsize <= MaxSearchBlock && bsize >= e.MinSearchBlock(bsize) && bsize <= _maxSize && bsize >= _minSize;
            bool allowSplit = bsize > e.MinBlock && (bsize > Vp9Tables.Block8x8 || e.AllowSub8x8) && bsize > _minSize;
            bool allowRect = effort >= 5 && bsize > Vp9Tables.Block8x8 && bsize <= MaxSearchBlock;
            if (!hasRows || !hasCols)
            {
                allowSplit = bsize > Vp9Tables.Block8x8 || e.AllowSub8x8;
            }

            if (!allowNone && !allowSplit && !(!hasRows && hasCols) && !(hasRows && !hasCols))
            {
                allowNone = hasRows && hasCols;
            }

            int subsizeNone = bsize;
            if (allowNone)
            {
                Rd rd = SearchBlock(miRow, miCol, bsize, ref node.Block0, bestCost);
                if (!rd.IsMax)
                {
                    rd += new Rd(PartitionCost(miRow, miCol, bsize, Vp9Tables.PartitionNone, hasRows, hasCols), 0);
                    UpdatePartitionContext(miRow, miCol, subsizeNone, bsize);
                    long cost = RdCost(rd);
                    if (cost < bestCost)
                    {
                        bestCost = cost;
                        best = rd;
                        bestPartition = Vp9Tables.PartitionNone;
                        node.Best0 = node.Block0;
                    }
                }

                lastCoded = Vp9Tables.PartitionNone;
            }

            // libvpx's partition search breakout: a block cheap and close at its full size is not split further.
            int bwl = Vp9Tables.BWidthLog2Lookup[bsize], bhl = Vp9Tables.BHeightLog2Lookup[bsize];
            long distThreshold = e.BreakoutDistance >> (8 - (bwl + bhl));
            int rateThreshold = e.BreakoutRate * (bwl + bhl + 4);
            bool breakout = bestPartition == Vp9Tables.PartitionNone && !e._lossless && best.Rate < rateThreshold && best.Dist < distThreshold;
            bool skipSplit = breakout || (bestPartition == Vp9Tables.PartitionNone && effort <= 3 && node.Best0.Skip != 0 && bsize <= Vp9Tables.Block32x32);
            bool noRect = breakout;

            if (allowSplit && !skipSplit)
            {
                RestoreContext(miRow, miCol, bsize, depth);
                Rd rd = new(PartitionCost(miRow, miCol, bsize, Vp9Tables.PartitionSplit, hasRows, hasCols), 0);
                if (bsize == Vp9Tables.Block8x8)
                {
                    // Below 8x8: one block of four 4x4 sub-blocks.
                    rd += SearchBlock(miRow, miCol, Vp9Tables.Block4x4, ref node.Block0, bestCost == long.MaxValue ? long.MaxValue : bestCost - RdCost(rd));
                    if (!rd.IsMax)
                    {
                        UpdatePartitionContext(miRow, miCol, Vp9Tables.Block4x4, bsize);
                    }
                }
                else
                {
                    int subsize = Vp9Tables.SubsizeLookup[(Vp9Tables.PartitionSplit * Vp9Tables.BlockSizes) + bsize];
                    for (int i = 0; i < 4 && !rd.IsMax; i++)
                    {
                        int r = miRow + ((i >> 1) * hbs), c = miCol + ((i & 1) * hbs);
                        if (r >= e._miRows || c >= e._miCols)
                        {
                            continue;
                        }

                        long left = bestCost == long.MaxValue ? long.MaxValue : bestCost - RdCost(rd);
                        if (left <= 0)
                        {
                            rd = Rd.Max;
                            break;
                        }

                        rd += SearchPartition(r, c, subsize, depth + 1, (index * 4) + i, LimitOf(left));
                    }
                }

                lastCoded = Vp9Tables.PartitionSplit;
                if (!rd.IsMax && RdCost(rd) < bestCost)
                {
                    bestCost = RdCost(rd);
                    best = rd;
                    bestPartition = Vp9Tables.PartitionSplit;
                    if (bsize == Vp9Tables.Block8x8)
                    {
                        node.Best0 = node.Block0;
                    }

                    noRect |= !e._lossless && (rd.Dist < (distThreshold >> 2) || (rd.Dist < distThreshold && rd.Rate < rateThreshold));
                }
            }

            if (((allowRect && !noRect) || (!hasRows && hasCols)) && bsize > Vp9Tables.Block8x8 && hasCols)
            {
                RestoreContext(miRow, miCol, bsize, depth);
                Rd rd = SearchRect(miRow, miCol, bsize, Vp9Tables.PartitionHorz, hasRows, hasCols, node, bestCost);
                lastCoded = Vp9Tables.PartitionHorz;
                if (!rd.IsMax && RdCost(rd) < bestCost)
                {
                    bestCost = RdCost(rd);
                    best = rd;
                    bestPartition = Vp9Tables.PartitionHorz;
                    node.Best0 = node.Block0;
                    node.Best1 = node.Block1;
                }
            }

            if (((allowRect && !noRect) || (hasRows && !hasCols)) && bsize > Vp9Tables.Block8x8 && hasRows)
            {
                RestoreContext(miRow, miCol, bsize, depth);
                Rd rd = SearchRect(miRow, miCol, bsize, Vp9Tables.PartitionVert, hasRows, hasCols, node, bestCost);
                lastCoded = Vp9Tables.PartitionVert;
                if (!rd.IsMax && RdCost(rd) < bestCost)
                {
                    bestCost = RdCost(rd);
                    best = rd;
                    bestPartition = Vp9Tables.PartitionVert;
                    node.Best0 = node.Block0;
                    node.Best1 = node.Block1;
                }
            }

            node.Partition = bestPartition;
            if (bestPartition < 0)
            {
                RestoreContext(miRow, miCol, bsize, depth);
                return Rd.Max;
            }

            if (bestPartition != lastCoded)
            {
                // Leave the best partition coded: recon, contexts and mode info for the blocks after this node.
                RestoreContext(miRow, miCol, bsize, depth);
                EncodeTree(miRow, miCol, bsize, depth, index);
            }

            return best;
        }

        /// <summary>A limit for a sub-search: the rate and distortion whose RD cost is about <paramref name="cost"/>.</summary>
        private static Rd LimitOf(long cost) => cost == long.MaxValue ? Rd.Max : new Rd(0, Math.Max(0, cost >> 7));

        private Rd SearchRect(int miRow, int miCol, int bsize, int partition, bool hasRows, bool hasCols, Node node, long bestCost)
        {
            int subsize = Vp9Tables.SubsizeLookup[(partition * Vp9Tables.BlockSizes) + bsize];
            int hbs = Vp9Tables.Num8x8BlocksWideLookup[bsize] >> 1;
            Rd rd = new(PartitionCost(miRow, miCol, bsize, partition, hasRows, hasCols), 0);
            rd += SearchBlock(miRow, miCol, subsize, ref node.Block0, bestCost - RdCost(rd));
            if (rd.IsMax)
            {
                return Rd.Max;
            }

            bool second = partition == Vp9Tables.PartitionHorz ? hasRows : hasCols;
            if (second)
            {
                int r = partition == Vp9Tables.PartitionHorz ? miRow + hbs : miRow;
                int c = partition == Vp9Tables.PartitionHorz ? miCol : miCol + hbs;
                long left = bestCost == long.MaxValue ? long.MaxValue : bestCost - RdCost(rd);
                if (left <= 0)
                {
                    return Rd.Max;
                }

                rd += SearchBlock(r, c, subsize, ref node.Block1, left);
            }

            if (!rd.IsMax)
            {
                UpdatePartitionContext(miRow, miCol, subsize, bsize);
            }

            return rd;
        }

        /// <summary>Codes a node's chosen partition from the decisions the search kept (encode_sb).</summary>
        private void EncodeTree(int miRow, int miCol, int bsize, int depth, int index)
        {
            Vp9Encoder<T> e = _e;
            if (miRow >= e._miRows || miCol >= e._miCols)
            {
                return;
            }

            Node node = _nodes[depth][index];
            int num8x8 = 8 >> depth;
            int hbs = num8x8 >> 1;
            bool hasRows = (miRow + hbs) < e._miRows;
            bool hasCols = (miCol + hbs) < e._miCols;
            int partition = node.Partition;

            switch (partition)
            {
                case Vp9Tables.PartitionNone:
                    EncodeBlock(miRow, miCol, bsize, ref node.Best0);
                    UpdatePartitionContext(miRow, miCol, bsize, bsize);
                    break;
                case Vp9Tables.PartitionHorz:
                    {
                        int subsize = Vp9Tables.SubsizeLookup[(partition * Vp9Tables.BlockSizes) + bsize];
                        EncodeBlock(miRow, miCol, subsize, ref node.Best0);
                        if (hasRows)
                        {
                            EncodeBlock(miRow + hbs, miCol, subsize, ref node.Best1);
                        }

                        UpdatePartitionContext(miRow, miCol, subsize, bsize);
                        break;
                    }

                case Vp9Tables.PartitionVert:
                    {
                        int subsize = Vp9Tables.SubsizeLookup[(partition * Vp9Tables.BlockSizes) + bsize];
                        EncodeBlock(miRow, miCol, subsize, ref node.Best0);
                        if (hasCols)
                        {
                            EncodeBlock(miRow, miCol + hbs, subsize, ref node.Best1);
                        }

                        UpdatePartitionContext(miRow, miCol, subsize, bsize);
                        break;
                    }

                default:
                    if (bsize == Vp9Tables.Block8x8)
                    {
                        EncodeBlock(miRow, miCol, Vp9Tables.Block4x4, ref node.Best0);
                        UpdatePartitionContext(miRow, miCol, Vp9Tables.Block4x4, bsize);
                    }
                    else
                    {
                        int subsize = Vp9Tables.SubsizeLookup[(partition * Vp9Tables.BlockSizes) + bsize];
                        for (int i = 0; i < 4; i++)
                        {
                            EncodeTree(miRow + ((i >> 1) * hbs), miCol + ((i & 1) * hbs), subsize, depth + 1, (index * 4) + i);
                        }
                    }

                    break;
            }
        }

        // The token buffer.

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnsureTokens(int count)
        {
            if (_tokenCount + count > _tokens.Length)
            {
                Array.Resize(ref _tokens, Math.Max(_tokens.Length * 2, _tokenCount + count));
            }
        }
    }
}
