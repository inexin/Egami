using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Egami.Video.Codecs.Vp9;

/// <summary>
/// Writing the frame (vp9_bitstream.c): the uncompressed header, the compressed header with its probability updates, and each
/// tile's partitions, mode info and tokens, mirroring the decoder's reads symbol for symbol.
/// </summary>
internal sealed partial class Vp9Encoder<T>
{
    private int WriteFrame(Vp9FramePlan plan)
    {
        int tileCols = 1 << _tileColsLog2;
        EnsureRowTokens(tileCols);

        // Search and code every superblock row of every tile column. Rows run as a wavefront on up to MaxParallelism threads: a
        // superblock waits until the row above has coded the superblock above and to its right, the last whose mode info,
        // contexts and pixels it reads, so every block sees what it would on one thread.
        ClearAboveContexts();
        int jobs = tileCols * _sbRows;
        int workers = Math.Clamp(MaxParallelism, 1, jobs);
        for (int i = 0; i < workers; i++)
        {
            _tiles[i] ??= new Tile(this);
        }

        if (workers <= 1)
        {
            for (int job = 0; job < jobs; job++)
            {
                RunRow(_tiles[0]!, job, tileCols, null);
            }
        }
        else
        {
            int[] progress = _rowProgress.Length >= jobs ? _rowProgress : _rowProgress = new int[jobs];
            Array.Clear(progress, 0, jobs);
            int next = -1, failed = 0;
            try
            {
                Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers }, w =>
                {
                    try
                    {
                        for (int job = Interlocked.Increment(ref next); job < jobs; job = Interlocked.Increment(ref next))
                        {
                            RunRow(_tiles[w]!, job, tileCols, progress, () => Volatile.Read(ref failed) != 0);
                        }
                    }
                    catch
                    {
                        Volatile.Write(ref failed, 1);
                        throw;
                    }
                });
            }
            catch (AggregateException e)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(e.InnerExceptions[0]);
            }
        }

        PickFilterLevel(plan);
        if (plan.Show && !plan.Overlay)
        {
            KeepSizes();
        }

        // Count what the tiles code, choose the probability updates, then write.
        _counts.Clear();
        ClearAboveContexts();
        Array.Clear(_tokenCounts);
        Array.Clear(_txBlocks);
        Tile packer = _tiles[0]!;
        for (int c = 0; c < tileCols; c++)
        {
            packer.Start(c, TileOffset(c, _miCols, _tileColsLog2), TileOffset(c + 1, _miCols, _tileColsLog2));
            packer.Pack(counting: true);
            _counts.Add(packer.Counts);
            int[] tokens = packer.TokenCounts;
            for (int i = 0; i < tokens.Length; i++)
            {
                _tokenCounts[i] += tokens[i];
            }

            for (int i = 0; i < 4; i++)
            {
                _txBlocks[i] += packer.TxBlocks[i];
            }
        }

        Span<byte> header = stackalloc byte[64];
        Vp9BitWriter w = new(header);
        WriteUncompressedHeader(ref w, plan, out int sizePosition);
        int uncompressedSize = w.BytesWritten;

        Vp9BoolEncoder bw = default;
        bw.Start(_output, uncompressedSize);
        WriteCompressedHeader(ref bw, plan);
        int compressedSize = bw.Stop();
        _output = bw.Buffer;
        w.Patch16(sizePosition, compressedSize);
        header[..uncompressedSize].CopyTo(_output);

        ClearAboveContexts();
        int at = uncompressedSize + compressedSize;
        for (int c = 0; c < tileCols; c++)
        {
            bool last = c == tileCols - 1;
            int start = last ? at : at + 4;
            packer.Start(c, TileOffset(c, _miCols, _tileColsLog2), TileOffset(c + 1, _miCols, _tileColsLog2));
            int size = packer.Pack(counting: false, _output, start, out byte[] buffer);
            _output = buffer;
            if (!last)
            {
                BinaryPrimitives.WriteUInt32BigEndian(_output.AsSpan(at, 4), (uint)size);
            }

            at = start + size;
        }

        return at;
    }

    private int[] _rowProgress = [];
    private int[][][] _rowTokens = [];
    private int[][] _rowTokenCounts = [];

    /// <summary>Codes one superblock row of one tile column (job: row-major over the tile columns), waiting on the row above.</summary>
    private void RunRow(Tile tile, int job, int tileCols, int[]? progress, Func<bool>? failed = null)
    {
        int c = job % tileCols, row = job / tileCols;
        tile.Start(c, TileOffset(c, _miCols, _tileColsLog2), TileOffset(c + 1, _miCols, _tileColsLog2));
        tile.EncodeRow(row * 8, progress, row > 0 ? job - tileCols : -1, job, failed);
    }

    private void EnsureRowTokens(int tileCols)
    {
        if (_rowTokens.Length < tileCols)
        {
            _rowTokens = new int[tileCols][][];
            _rowTokenCounts = new int[tileCols][];
            for (int c = 0; c < tileCols; c++)
            {
                _rowTokens[c] = new int[_sbRows][];
                _rowTokenCounts[c] = new int[_sbRows];
                for (int r = 0; r < _sbRows; r++)
                {
                    _rowTokens[c][r] = new int[1 << 12];
                }
            }
        }
    }

    private void ClearAboveContexts()
    {
        for (int p = 0; p < 3; p++)
        {
            Array.Clear(_aboveContext[p]);
        }

        Array.Clear(_abovePartition);
    }

    /// <summary>Where tile <paramref name="index"/> starts, in 8x8 units (get_tile_offset).</summary>
    private static int TileOffset(int index, int mis, int log2)
    {
        int sbCols = ((mis + 7) & ~7) >> 3;
        int offset = ((index * sbCols) >> log2) << 3;
        return Math.Min(offset, mis);
    }

    private void WriteUncompressedHeader(ref Vp9BitWriter w, Vp9FramePlan plan, out int sizePosition)
    {
        w.Literal(2, 2);
        w.Bit(_profile & 1);
        w.Bit(_profile >> 1);
        if (_profile == 3)
        {
            w.Bit(0);
        }

        w.Bit(0); // show_existing_frame
        w.Bit(plan.Key ? 0 : 1);
        w.Bit(plan.Show ? 1 : 0);
        w.Bit(plan.ErrorResilient ? 1 : 0);
        if (plan.Key)
        {
            w.Literal(Vp9FrameHeader.SyncCode, 24);
            WriteColorConfig(ref w);
            WriteFrameSize(ref w);
        }
        else
        {
            if (!plan.Show)
            {
                w.Bit(0); // intra_only
            }

            if (!plan.ErrorResilient)
            {
                w.Literal(0, 2); // reset_frame_context
            }

            w.Literal(plan.RefreshFlags, 8);
            int[] slots = [plan.LastSlot, plan.GoldenSlot, plan.AltRefSlot];
            for (int i = 0; i < 3; i++)
            {
                w.Literal(slots[i], 3);
                w.Bit(_refSignBias[Vp9Tables.LastFrame + i]);
            }

            // The size comes from LAST, which always has the frame's size.
            w.Bit(1);
            w.Bit(0); // render_and_frame_size_different
            w.Bit(_allowHighPrecisionMv ? 1 : 0);
            w.Bit(_interpFilter == Vp9Tables.Switchable ? 1 : 0);
            if (_interpFilter != Vp9Tables.Switchable)
            {
                w.Literal(FilterToLiteral(_interpFilter), 2);
            }
        }

        if (!plan.ErrorResilient)
        {
            w.Bit(plan.RefreshContext ? 1 : 0);
            w.Bit(plan.RefreshContext ? 0 : 1); // frame_parallel_decoding_mode: no backward adaptation without a refresh
        }

        w.Literal(plan.Key || plan.ErrorResilient ? 0 : plan.FrameContext, 2);

        // Loop filter: the level, sharpness 0, and libvpx's default reference deltas sent on every frame that resets them.
        w.Literal(_filterLevel, 6);
        w.Literal(0, 3);
        w.Bit(1); // loop_filter_delta_enabled
        bool resetsDeltas = plan.Key || plan.ErrorResilient;
        w.Bit(resetsDeltas ? 1 : 0);
        if (resetsDeltas)
        {
            ReadOnlySpan<int> refDeltas = [1, 0, -1, -1];
            for (int i = 0; i < 4; i++)
            {
                w.Bit(1);
                w.SignedLiteral(refDeltas[i], 6);
            }

            for (int i = 0; i < 2; i++)
            {
                w.Bit(1);
                w.SignedLiteral(0, 6);
            }
        }

        // Quantization.
        w.Literal(plan.Quantizer, 8);
        w.Bit(0);
        w.Bit(0);
        w.Bit(0);

        WriteSegmentation(ref w);

        // Tiles: one row, the chosen columns.
        Vp9FrameHeader.TileLog2Bounds(_miCols, out int minLog2, out int maxLog2);
        for (int i = minLog2; i < _tileColsLog2; i++)
        {
            w.Bit(1);
        }

        if (_tileColsLog2 < maxLog2)
        {
            w.Bit(0);
        }

        w.Bit(0); // tile_rows_log2
        sizePosition = w.BitPosition;
        w.Literal(0, 16);
    }

    private static int FilterToLiteral(int filter) => filter switch
    {
        Vp9Tables.EightTapSmooth => 0,
        Vp9Tables.EightTap => 1,
        Vp9Tables.EightTapSharp => 2,
        _ => 3,
    };

    private void WriteColorConfig(ref Vp9BitWriter w)
    {
        if (_profile >= 2)
        {
            w.Bit(_bitDepth == 12 ? 1 : 0);
        }

        w.Literal(_colorSpace, 3);
        if (_colorSpace != 7)
        {
            w.Bit(_colorRange);
            if (_profile is 1 or 3)
            {
                w.Bit(_subX);
                w.Bit(_subY);
                w.Bit(0);
            }
        }
        else if (_profile is 1 or 3)
        {
            w.Bit(0);
        }
    }

    private void WriteFrameSize(ref Vp9BitWriter w)
    {
        w.Literal(_width - 1, 16);
        w.Literal(_height - 1, 16);
        w.Bit(0); // render_and_frame_size_different
    }

    private void WriteCompressedHeader(ref Vp9BoolEncoder w, Vp9FramePlan plan)
    {
        byte[] p = _fc.P;
        if (!_lossless)
        {
            w.WriteLiteral(Math.Min(_txMode, Vp9Tables.Allow32x32), 2);
            if (_txMode >= Vp9Tables.Allow32x32)
            {
                w.WriteBit(_txMode == Vp9Tables.TxModeSelect ? 1 : 0);
            }
        }

        if (_txMode == Vp9Tables.TxModeSelect)
        {
            WriteTxProbs(ref w, p);
        }

        WriteCoefProbs(ref w, p);
        for (int i = 0; i < 3; i++)
        {
            UpdateBinary(ref w, p, Vp9FrameContext.Skip + i, _counts.Skip[i * 2], _counts.Skip[(i * 2) + 1]);
        }

        if (!_frameIsIntra)
        {
            for (int i = 0; i < 7; i++)
            {
                UpdateTree(ref w, p, Vp9FrameContext.InterMode + (i * 3), Vp9EncoderTables.InterMode, _counts.InterMode.AsSpan(i * 4, 4));
            }

            if (_interpFilter == Vp9Tables.Switchable)
            {
                for (int i = 0; i < 4; i++)
                {
                    UpdateTree(ref w, p, Vp9FrameContext.SwitchableInterp + (i * 2), Vp9EncoderTables.SwitchableInterp, _counts.SwitchableInterp.AsSpan(i * 3, 3));
                }
            }

            for (int i = 0; i < 4; i++)
            {
                UpdateBinary(ref w, p, Vp9FrameContext.IntraInter + i, _counts.IntraInter[i * 2], _counts.IntraInter[(i * 2) + 1]);
            }

            bool allowed = _refSignBias[2] != _refSignBias[1] || _refSignBias[3] != _refSignBias[1];
            if (allowed)
            {
                w.WriteBit(_referenceMode != Vp9Tables.SingleReference ? 1 : 0);
                if (_referenceMode != Vp9Tables.SingleReference)
                {
                    w.WriteBit(_referenceMode == Vp9Tables.ReferenceModeSelect ? 1 : 0);
                }
            }

            if (_referenceMode == Vp9Tables.ReferenceModeSelect)
            {
                for (int i = 0; i < 5; i++)
                {
                    UpdateBinary(ref w, p, Vp9FrameContext.CompInter + i, _counts.CompInter[i * 2], _counts.CompInter[(i * 2) + 1]);
                }
            }

            if (_referenceMode != Vp9Tables.CompoundReference)
            {
                for (int i = 0; i < 5; i++)
                {
                    for (int j = 0; j < 2; j++)
                    {
                        int k = (i * 2) + j;
                        UpdateBinary(ref w, p, Vp9FrameContext.SingleRef + k, _counts.SingleRef[k * 2], _counts.SingleRef[(k * 2) + 1]);
                    }
                }
            }

            if (_referenceMode != Vp9Tables.SingleReference)
            {
                for (int i = 0; i < 5; i++)
                {
                    UpdateBinary(ref w, p, Vp9FrameContext.CompRef + i, _counts.CompRef[i * 2], _counts.CompRef[(i * 2) + 1]);
                }
            }

            for (int i = 0; i < 4; i++)
            {
                UpdateTree(ref w, p, Vp9FrameContext.YMode + (i * 9), Vp9EncoderTables.IntraMode, _counts.YMode.AsSpan(i * 10, 10));
            }

            for (int i = 0; i < 16; i++)
            {
                UpdateTree(ref w, p, Vp9FrameContext.Partition + (i * 3), Vp9EncoderTables.Partition, _counts.Partition.AsSpan(i * 4, 4));
            }

            WriteMvProbs(ref w, p);
        }
    }

    // The tiles.

    internal sealed partial class Tile
    {
        private Vp9BoolEncoder _w;
        private bool _counting;
        private int _tokenRead;

        /// <summary>Gets the full token counts the counting pass found: [tx][type][ref][band][ctx][ZERO..CAT6, EOB].</summary>
        public int[] TokenCounts { get; } = new int[4 * 2 * 2 * 6 * 6 * 12];

        /// <summary>Gets how many transform blocks of each size the counting pass found.</summary>
        public int[] TxBlocks { get; } = new int[4];

        /// <summary>Counts the tile's symbols (<paramref name="counting"/>) or writes them; returns the bytes written.</summary>
        public int Pack(bool counting) => Pack(counting, new byte[16], 0, out _);

        public int Pack(bool counting, byte[] buffer, int start, out byte[] result)
        {
            _counting = counting;
            _tokenRead = 0;
            if (counting)
            {
                Counts.Clear();
                Array.Clear(TokenCounts);
                Array.Clear(TxBlocks);
            }
            else
            {
                _w.Start(buffer, start);
            }

            Vp9Encoder<T> e = _e;
            for (int miRow = 0; miRow < e._miRows; miRow += 8)
            {
                for (int p = 0; p < 3; p++)
                {
                    Array.Clear(_leftContext[p]);
                }

                Array.Clear(_leftPartition);
                _tokens = e._rowTokens[TileColumn][miRow >> 3];
                _tokenRead = 0;
                for (int miCol = MiColStart; miCol < MiColEnd; miCol += 8)
                {
                    PackPartition(miRow, miCol, Vp9Tables.Block64x64, 4);
                }
            }

            if (counting)
            {
                result = buffer;
                return 0;
            }

            int size = _w.Stop();
            result = _w.Buffer;
            return size;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Put(int bit, int probability)
        {
            if (!_counting)
            {
                _w.Write(bit, probability);
            }
        }

        private void PutTree(TreeCodes codes, ReadOnlySpan<byte> probabilities, int symbol)
        {
            if (!_counting)
            {
                codes.Write(ref _w, probabilities, symbol);
            }
        }

        private void PackPartition(int miRow, int miCol, int bsize, int n4x4L2)
        {
            Vp9Encoder<T> e = _e;
            int n8x8L2 = n4x4L2 - 1;
            int num8x8 = 1 << n8x8L2;
            int hbs = num8x8 >> 1;
            if (miRow >= e._miRows || miCol >= e._miCols)
            {
                return;
            }

            bool hasRows = (miRow + hbs) < e._miRows;
            bool hasCols = (miCol + hbs) < e._miCols;
            int sbType = e._mi[e._miGrid[(miRow * e._miCols) + miCol]].SbType;
            int partition;
            if (bsize == Vp9Tables.Block8x8)
            {
                partition = sbType == Vp9Tables.Block8x8 ? Vp9Tables.PartitionNone
                    : sbType == Vp9Tables.Block8x4 ? Vp9Tables.PartitionHorz
                    : sbType == Vp9Tables.Block4x8 ? Vp9Tables.PartitionVert
                    : Vp9Tables.PartitionSplit;
            }
            else
            {
                partition = sbType == bsize ? Vp9Tables.PartitionNone
                    : sbType == Vp9Tables.SubsizeLookup[(Vp9Tables.PartitionHorz * Vp9Tables.BlockSizes) + bsize] ? Vp9Tables.PartitionHorz
                    : sbType == Vp9Tables.SubsizeLookup[(Vp9Tables.PartitionVert * Vp9Tables.BlockSizes) + bsize] ? Vp9Tables.PartitionVert
                    : Vp9Tables.PartitionSplit;
            }

            WritePartition(miRow, miCol, hasRows, hasCols, n8x8L2, partition);
            int subsize = Vp9Tables.SubsizeLookup[(partition * Vp9Tables.BlockSizes) + bsize];
            if (hbs == 0)
            {
                PackBlock(miRow, miCol, subsize);
            }
            else
            {
                switch (partition)
                {
                    case Vp9Tables.PartitionNone:
                        PackBlock(miRow, miCol, subsize);
                        break;
                    case Vp9Tables.PartitionHorz:
                        PackBlock(miRow, miCol, subsize);
                        if (hasRows)
                        {
                            PackBlock(miRow + hbs, miCol, subsize);
                        }

                        break;
                    case Vp9Tables.PartitionVert:
                        PackBlock(miRow, miCol, subsize);
                        if (hasCols)
                        {
                            PackBlock(miRow, miCol + hbs, subsize);
                        }

                        break;
                    default:
                        PackPartition(miRow, miCol, subsize, n8x8L2);
                        PackPartition(miRow, miCol + hbs, subsize, n8x8L2);
                        PackPartition(miRow + hbs, miCol, subsize, n8x8L2);
                        PackPartition(miRow + hbs, miCol + hbs, subsize, n8x8L2);
                        break;
                }
            }

            if (bsize >= Vp9Tables.Block8x8 && (bsize == Vp9Tables.Block8x8 || partition != Vp9Tables.PartitionSplit))
            {
                byte above = Vp9Tables.PartitionContextLookup[subsize * 2], left = Vp9Tables.PartitionContextLookup[(subsize * 2) + 1];
                e._abovePartition.AsSpan(miCol, num8x8).Fill(above);
                _leftPartition.AsSpan(miRow & 7, num8x8).Fill(left);
            }
        }

        private void WritePartition(int miRow, int miCol, bool hasRows, bool hasCols, int bsl, int p)
        {
            Vp9Encoder<T> e = _e;
            int above = (e._abovePartition[miCol] >> bsl) & 1, left = (_leftPartition[miRow & 7] >> bsl) & 1;
            int ctx = (left * 2) + above + (bsl * 4);
            ReadOnlySpan<byte> probs = e._frameIsIntra
                ? Vp9Tables.KfPartitionProbs.AsSpan(ctx * 3, 3)
                : e._fc.P.AsSpan(Vp9FrameContext.Partition + (ctx * 3), 3);
            if (hasRows && hasCols)
            {
                PutTree(Vp9EncoderTables.Partition, probs, p);
            }
            else if (!hasRows && hasCols)
            {
                Put(p == Vp9Tables.PartitionSplit ? 1 : 0, probs[1]);
            }
            else if (hasRows && !hasCols)
            {
                Put(p == Vp9Tables.PartitionSplit ? 1 : 0, probs[2]);
            }

            if (_counting)
            {
                Counts.Partition[(ctx * 4) + p]++;
            }
        }

        private void PackBlock(int miRow, int miCol, int bsize)
        {
            Vp9Encoder<T> e = _e;
            SetBlock(miRow, miCol, bsize);
            ref Vp9ModeInfo mi = ref e._mi[_miIndex];
            if (e._frameIsIntra)
            {
                PackIntraFrameModeInfo(ref mi);
            }
            else
            {
                PackInterFrameModeInfo(ref mi);
            }

            if (mi.Skip != 0)
            {
                for (int p = 0; p < 3; p++)
                {
                    e._aboveContext[p].AsSpan(AboveContextIndex(p), _n4W[p]).Clear();
                    _leftContext[p].AsSpan(LeftContextIndex(p), _n4H[p]).Clear();
                }

                return;
            }

            for (int plane = 0; plane < 3; plane++)
            {
                int txSize = plane == 0 ? mi.TxSize : UVTxSize(ref mi, plane);
                int step = 1 << txSize;
                MaxBlocks(plane, out int maxWide, out int maxHigh);
                Span<byte> above = e._aboveContext[plane].AsSpan(AboveContextIndex(plane));
                Span<byte> left = _leftContext[plane].AsSpan(LeftContextIndex(plane));
                for (int row = 0; row < maxHigh; row += step)
                {
                    for (int col = 0; col < maxWide; col += step)
                    {
                        Vp9Scan scan;
                        if (!mi.IsInter && plane == 0 && !e._lossless)
                        {
                            int mode = mi.SbType < Vp9Tables.Block8x8 ? mi.Bmi[(row << 1) + col].Mode : mi.Mode;
                            scan = Vp9Scan.For(txSize, Vp9Tables.IntraModeToTxTypeLookup[mode]);
                        }
                        else
                        {
                            scan = Vp9Scan.Default(txSize);
                        }

                        int ctx = EntropyContext(above, left, col, row, step);
                        if (_counting)
                        {
                            TxBlocks[txSize]++;
                        }

                        int eob = PackTokens(plane > 0 ? 1 : 0, txSize, ctx, scan, mi.IsInter ? 1 : 0);
                        SetEntropyContext(plane, above, left, col, row, step, eob > 0);
                    }
                }
            }
        }

        /// <summary>Writes one transform block's tokens from the token buffer, mirroring the decoder's DecodeCoefs.</summary>
        private int PackTokens(int type, int txSize, int ctx, Vp9Scan scan, int reference)
        {
            int eob = _tokens[_tokenRead++];
            int maxEob = 16 << (txSize << 1);
            byte[] p = _e._fc.P;
            int probBase = Vp9FrameContext.CoefOffset(txSize, type, reference);
            int countBase = ((((txSize * 2) + type) * 2) + reference) * 36;
            ReadOnlySpan<byte> bands = txSize == Vp9Tables.Tx4x4 ? Vp9Tables.CoefbandTrans4x4 : Vp9Tables.CoefbandTrans8x8Plus;
            ReadOnlySpan<short> order = scan.Order;
            ReadOnlySpan<short> neighbors = scan.Neighbors;
            ReadOnlySpan<byte> energy = Vp9EncoderTables.EnergyClass;
            Span<byte> cache = _tokenCache;
            int bitDepth = _e._bitDepth;
            bool counting = _counting;
            int[] coefCounts = Counts.Coef, eobBranch = Counts.EobBranch;
            bool afterZero = false;
            for (int c = 0; c < eob; c++)
            {
                if (c > 0)
                {
                    ctx = (1 + cache[neighbors[c * 2]] + cache[neighbors[(c * 2) + 1]]) >> 1;
                }

                int band = bands[c];
                int pi = probBase + (band * 18) + (ctx * 3);
                int cb = countBase + (band * 6) + ctx;
                int v = _tokens[_tokenRead + c];
                int m = Math.Abs(v);
                int token = Vp9EncoderTables.Token(m);
                if (!afterZero)
                {
                    Put(1, p[pi]);
                    if (counting)
                    {
                        eobBranch[cb]++;
                    }
                }

                if (counting)
                {
                    TokenCounts[(cb * 12) + token]++;
                }

                if (m == 0)
                {
                    Put(0, p[pi + 1]);
                    if (counting)
                    {
                        coefCounts[cb * 4]++;
                    }
                }
                else
                {
                    Put(1, p[pi + 1]);
                    if (m == 1)
                    {
                        Put(0, p[pi + 2]);
                        if (counting)
                        {
                            coefCounts[(cb * 4) + 1]++;
                        }
                    }
                    else
                    {
                        Put(1, p[pi + 2]);
                        if (counting)
                        {
                            coefCounts[(cb * 4) + 2]++;
                        }
                        else
                        {
                            WriteLargeToken(p[pi + 2], token, m, bitDepth);
                        }
                    }

                    Put(v < 0 ? 1 : 0, 128);
                }

                cache[order[c]] = energy[token];
                afterZero = m == 0;
            }

            if (eob < maxEob)
            {
                if (eob > 0)
                {
                    ctx = (1 + cache[neighbors[eob * 2]] + cache[neighbors[(eob * 2) + 1]]) >> 1;
                }

                int band = bands[eob];
                int pi = probBase + (band * 18) + (ctx * 3);
                int cb = countBase + (band * 6) + ctx;
                Put(0, p[pi]);
                if (counting)
                {
                    eobBranch[cb]++;
                    coefCounts[(cb * 4) + 3]++;
                    TokenCounts[(cb * 12) + 11]++;
                }
            }

            _tokenRead += eob;
            return eob;
        }

        private void WriteLargeToken(byte probability, int token, int magnitude, int bitDepth)
        {
            ReadOnlySpan<byte> pareto = Vp9Tables.Pareto8Full.AsSpan((probability - 1) * 8, 8);
            switch (token)
            {
                case 2:
                    _w.Write(0, pareto[0]);
                    _w.Write(0, pareto[1]);
                    return;
                case 3:
                case 4:
                    _w.Write(0, pareto[0]);
                    _w.Write(1, pareto[1]);
                    _w.Write(token - 3, pareto[2]);
                    return;
            }

            _w.Write(1, pareto[0]);
            switch (token)
            {
                case 5:
                case 6:
                    _w.Write(0, pareto[3]);
                    _w.Write(token - 5, pareto[4]);
                    break;
                case 7:
                case 8:
                    _w.Write(1, pareto[3]);
                    _w.Write(0, pareto[5]);
                    _w.Write(token - 7, pareto[6]);
                    break;
                default:
                    _w.Write(1, pareto[3]);
                    _w.Write(1, pareto[5]);
                    _w.Write(token - 9, pareto[7]);
                    break;
            }

            int bits = Vp9EncoderTables.ExtraBits(token, bitDepth);
            int extra = magnitude - Vp9EncoderTables.TokenBase[token];
            ReadOnlySpan<byte> probs = Vp9EncoderTables.CatProbs(token, bitDepth);
            for (int i = 0; i < bits; i++)
            {
                _w.Write((extra >> (bits - 1 - i)) & 1, probs[i]);
            }
        }

        // Mode info.

        private void PackSegmentId(ref Vp9ModeInfo mi)
        {
            Vp9Encoder<T> e = _e;
            if (e._segmentationEnabled)
            {
                PutTree(Vp9EncoderTables.Segment, e._segmentTreeProbs, mi.SegmentId);
            }
        }

        private void PackSkip(ref Vp9ModeInfo mi)
        {
            if (SegmentSkips(mi.SegmentId))
            {
                return;
            }

            int ctx = SkipContext();
            Put(mi.Skip, _e._fc.P[Vp9FrameContext.Skip + ctx]);
            if (_counting)
            {
                Counts.Skip[(ctx * 2) + mi.Skip]++;
            }
        }

        private void PackTxSize(ref Vp9ModeInfo mi, bool allowSelect)
        {
            Vp9Encoder<T> e = _e;
            if (!allowSelect || e._txMode != Vp9Tables.TxModeSelect || mi.SbType < Vp9Tables.Block8x8)
            {
                return;
            }

            int maxTx = Vp9Tables.MaxTxSizeLookup[mi.SbType];
            if (maxTx == 0)
            {
                return;
            }

            int ctx = TxSizeContext(maxTx);
            byte[] p = e._fc.P;
            int probs = maxTx switch
            {
                Vp9Tables.Tx8x8 => Vp9FrameContext.Tx8x8 + ctx,
                Vp9Tables.Tx16x16 => Vp9FrameContext.Tx16x16 + (ctx * 2),
                _ => Vp9FrameContext.Tx32x32 + (ctx * 3),
            };
            int tx = mi.TxSize;
            Put(tx != 0 ? 1 : 0, p[probs]);
            if (tx != 0 && maxTx >= Vp9Tables.Tx16x16)
            {
                Put(tx != 1 ? 1 : 0, p[probs + 1]);
                if (tx != 1 && maxTx >= Vp9Tables.Tx32x32)
                {
                    Put(tx != 2 ? 1 : 0, p[probs + 2]);
                }
            }

            if (_counting)
            {
                switch (maxTx)
                {
                    case Vp9Tables.Tx8x8:
                        Counts.Tx8x8[(ctx * 2) + tx]++;
                        break;
                    case Vp9Tables.Tx16x16:
                        Counts.Tx16x16[(ctx * 3) + tx]++;
                        break;
                    default:
                        Counts.Tx32x32[(ctx * 4) + tx]++;
                        break;
                }
            }
        }

        private void PackIntraFrameModeInfo(ref Vp9ModeInfo mi)
        {
            PackSegmentId(ref mi);
            PackSkip(ref mi);
            PackTxSize(ref mi, true);
            if (mi.SbType == Vp9Tables.Block4x4)
            {
                for (int i = 0; i < 4; i++)
                {
                    int a = AboveBlockMode(ref mi, i), l = LeftBlockMode(ref mi, i);
                    PutTree(Vp9EncoderTables.IntraMode, Vp9Tables.KfYModeProb.AsSpan(((a * 10) + l) * 9, 9), mi.Bmi[i].Mode);
                }
            }
            else
            {
                int a = AboveBlockMode(ref mi, 0), l = LeftBlockMode(ref mi, 0);
                PutTree(Vp9EncoderTables.IntraMode, Vp9Tables.KfYModeProb.AsSpan(((a * 10) + l) * 9, 9), mi.Mode);
            }

            PutTree(Vp9EncoderTables.IntraMode, Vp9Tables.KfUVModeProb.AsSpan(mi.Mode * 9, 9), mi.UVMode);
        }

        private void PackInterFrameModeInfo(ref Vp9ModeInfo mi)
        {
            Vp9Encoder<T> e = _e;
            PackSegmentId(ref mi);
            PackSkip(ref mi);
            bool inter = mi.IsInter;
            if (!e.SegmentFeatureActive(mi.SegmentId, Vp9Tables.SegLvlRefFrame))
            {
                int ctx = IntraInterContext();
                Put(inter ? 1 : 0, e._fc.P[Vp9FrameContext.IntraInter + ctx]);
                if (_counting)
                {
                    Counts.IntraInter[(ctx * 2) + (inter ? 1 : 0)]++;
                }
            }

            PackTxSize(ref mi, mi.Skip == 0 || !inter);
            if (!inter)
            {
                byte[] p = e._fc.P;
                if (mi.SbType == Vp9Tables.Block4x4)
                {
                    for (int i = 0; i < 4; i++)
                    {
                        PutTree(Vp9EncoderTables.IntraMode, p.AsSpan(Vp9FrameContext.YMode, 9), mi.Bmi[i].Mode);
                        if (_counting)
                        {
                            Counts.YMode[mi.Bmi[i].Mode]++;
                        }
                    }
                }
                else
                {
                    int group = Vp9Tables.SizeGroupLookup[mi.SbType];
                    PutTree(Vp9EncoderTables.IntraMode, p.AsSpan(Vp9FrameContext.YMode + (group * 9), 9), mi.Mode);
                    if (_counting)
                    {
                        Counts.YMode[(group * 10) + mi.Mode]++;
                    }
                }

                PutTree(Vp9EncoderTables.IntraMode, p.AsSpan(Vp9FrameContext.UVMode + (mi.Mode * 9), 9), mi.UVMode);
                if (_counting)
                {
                    Counts.UVMode[(mi.Mode * 10) + mi.UVMode]++;
                }

                return;
            }

            PackInterBlock(ref mi);
        }
    }
}
