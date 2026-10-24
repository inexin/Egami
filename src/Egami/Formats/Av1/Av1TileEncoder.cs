namespace Egami.Formats.Av1;

/// <summary>
/// Decides and reconstructs one tile of a key frame by rate-distortion search: for each superblock the partition tree, each
/// block's luma and chroma modes, transform sizes and types, and each transform block's coefficients, every choice weighed as
/// distortion (the sum of squared errors) plus lambda times its rate (<see cref="Av1Rate"/>), the rate counted by running the
/// writer's own syntax (<see cref="Av1TileCoder"/>) against the tile's CDFs. Every transform block is predicted and reconstructed
/// with the decoder's own <see cref="Av1Reconstructor{T}"/> as it is tried, so later blocks predict from exactly the samples a
/// decoder will have. A chosen superblock's symbols then adapt the CDFs, as writing them will, so the rates of the next stay true.
/// </summary>
internal sealed partial class Av1TileEncoder : Av1TileCoder
{
    private readonly Av1Reconstructor<ushort> _reconstructor;
    private readonly Av1EncoderSource _source;
    private readonly Av1SearchProfile _profile;
    private long _frameRdmult;
    private int _sbQIndex;
    private long _rdmult;

    /// <summary>Whether the block's chosen modes are being coded for keeps, which is when the trellis runs below the efforts that search with it.</summary>
    private bool _winner;
    private readonly int[] _residual = new int[64 * 64];
    private readonly int[] _transformed = new int[64 * 64];
    private readonly int[] _quantized = new int[32 * 32];
    private readonly int[] _coeffs = new int[32 * 32];
    private readonly ushort[] _predicted = new ushort[64 * 64];
    private readonly long[] _planeDistortion = new long[3];
    private readonly Stack<Snapshot> _pool = new();
    private readonly Dictionary<int, QuantTable> _tables = [];
    private readonly Av1CoeffCosts _costs = new();
    private QuantTable? _table;
    private readonly TxPlace[][] _places = [new TxPlace[1024], new TxPlace[1024], new TxPlace[1024]];
    private readonly List<LumaChoice> _lumaCandidates = [];
    private readonly List<ChromaChoice> _chromaCandidates = [];
    private readonly List<(long Cost, int Index)> _scores = [];

    /// <summary>Starts a tile, or another searcher of its rows sharing the above contexts of <paramref name="shared"/>.</summary>
    internal Av1TileEncoder(Av1Frame frame, int tileRow, int tileCol, Av1EncoderSource source, Av1FrameDecisions decisions, Av1EncoderSettings settings,
        Av1TileEncoder? shared = null)
        : base(frame, tileRow, tileCol, decisions, shared)
    {
        _reconstructor = new Av1Reconstructor<ushort>(frame);
        _source = source;
        _profile = Av1SearchProfile.For(settings.Effort, frame.Header.CodedLossless);
        _frameRdmult = _rdmult = Av1Rate.RdMultiplier(Math.Max(frame.Header.BaseQIdx, 1), frame.Sequence.BitDepth);
    }

    /// <summary>
    /// Finds the partition of a square block with the lowest rate-distortion cost, leaving it decided and reconstructed, and
    /// returns its cost; gives up and returns <see cref="long.MaxValue"/> once the cost passes <paramref name="budget"/>.
    /// </summary>
    private long SearchPartition(int miRow, int miCol, int bsize, long budget)
    {
        if (miRow >= _frame.MiRows || miCol >= _miCols)
        {
            return 0;
        }

        int bw = Av1Tables.MiSizeWide[bsize];
        int hbs = bw >> 1;
        bool hasRows = miRow + hbs < _frame.MiRows;
        bool hasCols = miCol + hbs < _miCols;
        Snapshot start = Take(miRow, miCol, bsize, pixels: true);
        Snapshot? best = null;
        long nodeRdmult = BlockRdmult(miRow, miCol, bsize);
        long bestCost = long.MaxValue;
        bool first = true;
        bool lastIsBest = false;
        long noneCost = long.MaxValue, splitCost = long.MaxValue;
        int noneMode = -1;
        foreach (int partition in Candidates(bsize, hasRows, hasCols))
        {
            // A block coded whole along one direction is not cut across it (prune_rect_part_using_none_pred_mode).
            if (_profile.PrunePartitions && (partition, noneMode) is (Partition.Horz, Pred.V) or (Partition.Vert, Pred.H))
            {
                continue;
            }

            // At lower efforts: a block coded whole with no residual is not split, and rectangles are tried only where
            // splitting beat keeping the block whole.
            if (_profile.PrunePartitions && partition == Partition.Split && noneCost != long.MaxValue
                && Av1Tables.BlockSizeWide[bsize] >= 16 && _frame.Grid[(miRow * _miCols) + miCol] is { Skip: true } whole && whole.Size == bsize)
            {
                continue;
            }

            if (_profile.PrunePartitions && partition is not (Partition.None or Partition.Split) && hasRows && hasCols
                && noneCost != long.MaxValue && splitCost >= noneCost)
            {
                continue;
            }

            // A partition whose blocks have no chroma block size under the subsampling is not allowed (4x8 in 4:2:2, say).
            if (Av1Tables.PlaneBlockSize(Av1Tables.PartitionSubsize(bsize, partition), _ssx, _ssy) == Bs.Invalid)
            {
                continue;
            }

            if (!first)
            {
                start.Restore(this);
            }

            first = false;
            long limit = Math.Min(budget, bestCost);
            _rdmult = nodeRdmult;
            long cost = PartitionCost(miRow, miCol, bsize, hasRows, hasCols, partition);
            cost = cost >= limit ? long.MaxValue : Add(cost, EncodePartitionChoice(miRow, miCol, bsize, partition, hasRows, hasCols, limit - cost));
            if (partition == Partition.None)
            {
                noneCost = cost;
                noneMode = cost != long.MaxValue && _frame.Grid[(miRow * _miCols) + miCol] is { UseIntrabc: false, PaletteSize: [0, _] } noneBlock
                    ? noneBlock.YMode : -1;
            }
            else if (partition == Partition.Split)
            {
                splitCost = cost;
            }

            lastIsBest = cost < bestCost;
            if (lastIsBest)
            {
                bestCost = cost;
                if (best is null)
                {
                    best = Take(miRow, miCol, bsize, pixels: true);
                }
                else
                {
                    best.Save(this, miRow, miCol, bsize, pixels: true);
                }
            }
        }

        if (best is not null && !lastIsBest)
        {
            best.Restore(this);
        }
        else if (best is null)
        {
            start.Restore(this);
        }

        Give(start);
        if (best is not null)
        {
            Give(best);
        }

        return bestCost;
    }

    /// <summary>Encodes the blocks of one partition of a square block in the decoder's order; returns their cost or gives up past the budget.</summary>
    private long EncodePartitionChoice(int miRow, int miCol, int bsize, int partition, bool hasRows, bool hasCols, long budget)
    {
        int bw = Av1Tables.MiSizeWide[bsize];
        int hbs = bw >> 1, quarter = bw / 4;
        int subsize = Av1Tables.PartitionSubsize(bsize, partition);
        int bsize2 = Av1Tables.PartitionSubsize(bsize, Partition.Split);
        long cost = 0;
        switch (partition)
        {
            case Partition.None:
                cost = SearchBlock(miRow, miCol, partition, subsize, budget);
                break;
            case Partition.Horz:
                cost = SearchBlock(miRow, miCol, partition, subsize, budget);
                if (hasRows)
                {
                    cost = Add(cost, SearchBlock(miRow + hbs, miCol, partition, subsize, Left(budget, cost)));
                }

                break;
            case Partition.Vert:
                cost = SearchBlock(miRow, miCol, partition, subsize, budget);
                if (hasCols)
                {
                    cost = Add(cost, SearchBlock(miRow, miCol + hbs, partition, subsize, Left(budget, cost)));
                }

                break;
            case Partition.Split:
                if (bsize == Bs.B8x8)
                {
                    // Four 4x4 blocks, each its own partition node, which codes no partition symbol.
                    cost = SearchBlock(miRow, miCol, Partition.None, Bs.B4x4, budget);
                    cost = Add(cost, SearchBlock(miRow, miCol + 1, Partition.None, Bs.B4x4, Left(budget, cost)));
                    cost = Add(cost, SearchBlock(miRow + 1, miCol, Partition.None, Bs.B4x4, Left(budget, cost)));
                    cost = Add(cost, SearchBlock(miRow + 1, miCol + 1, Partition.None, Bs.B4x4, Left(budget, cost)));
                }
                else
                {
                    cost = SearchPartition(miRow, miCol, subsize, budget);
                    cost = Add(cost, SearchPartition(miRow, miCol + hbs, subsize, Left(budget, cost)));
                    cost = Add(cost, SearchPartition(miRow + hbs, miCol, subsize, Left(budget, cost)));
                    cost = Add(cost, SearchPartition(miRow + hbs, miCol + hbs, subsize, Left(budget, cost)));
                }

                break;
            case Partition.HorzA:
                cost = SearchBlock(miRow, miCol, partition, bsize2, budget);
                cost = Add(cost, SearchBlock(miRow, miCol + hbs, partition, bsize2, Left(budget, cost)));
                cost = Add(cost, SearchBlock(miRow + hbs, miCol, partition, subsize, Left(budget, cost)));
                break;
            case Partition.HorzB:
                cost = SearchBlock(miRow, miCol, partition, subsize, budget);
                cost = Add(cost, SearchBlock(miRow + hbs, miCol, partition, bsize2, Left(budget, cost)));
                cost = Add(cost, SearchBlock(miRow + hbs, miCol + hbs, partition, bsize2, Left(budget, cost)));
                break;
            case Partition.VertA:
                cost = SearchBlock(miRow, miCol, partition, bsize2, budget);
                cost = Add(cost, SearchBlock(miRow + hbs, miCol, partition, bsize2, Left(budget, cost)));
                cost = Add(cost, SearchBlock(miRow, miCol + hbs, partition, subsize, Left(budget, cost)));
                break;
            case Partition.VertB:
                cost = SearchBlock(miRow, miCol, partition, subsize, budget);
                cost = Add(cost, SearchBlock(miRow, miCol + hbs, partition, bsize2, Left(budget, cost)));
                cost = Add(cost, SearchBlock(miRow + hbs, miCol + hbs, partition, bsize2, Left(budget, cost)));
                break;
            case Partition.Horz4:
                for (int i = 0; i < 4 && cost != long.MaxValue; i++)
                {
                    int row = miRow + (i * quarter);
                    if (i > 0 && row >= _frame.MiRows)
                    {
                        break;
                    }

                    cost = Add(cost, SearchBlock(row, miCol, partition, subsize, Left(budget, cost)));
                }

                break;
            default:
                for (int i = 0; i < 4 && cost != long.MaxValue; i++)
                {
                    int col = miCol + (i * quarter);
                    if (i > 0 && col >= _miCols)
                    {
                        break;
                    }

                    cost = Add(cost, SearchBlock(miRow, col, partition, subsize, Left(budget, cost)));
                }

                break;
        }

        UpdateExtPartitionContext(miRow, miCol, subsize, bsize, partition);
        return cost;
    }

    /// <summary>
    /// Gets a block's rate-distortion multiplier: the frame's, scaled by the geometric mean of the 16x16 cells it covers
    /// (av1_set_ssim_rdmult, encodeframe_utils.c).
    /// </summary>
    private long BlockRdmult(int miRow, int miCol, int bsize)
    {
        double[] scale = _source.RdScale;
        if (scale.Length == 0)
        {
            return _frameRdmult;
        }

        int cols = _source.RdScaleColumns, rows = scale.Length / cols;
        int bw = (Av1Tables.MiSizeWide[bsize] + 3) / 4, bh = (Av1Tables.MiSizeHigh[bsize] + 3) / 4;
        double log = 0;
        int count = 0;
        for (int row = miRow / 4; row < rows && row < (miRow / 4) + bh; row++)
        {
            for (int col = miCol / 4; col < cols && col < (miCol / 4) + bw; col++)
            {
                log += Math.Log(scale[(row * cols) + col]);
                count++;
            }
        }

        return Math.Max(1, (long)((_frameRdmult * Math.Exp(log / count)) + 0.5));
    }

    private static long Add(long a, long b) => a == long.MaxValue || b == long.MaxValue ? long.MaxValue : a + b;

    private static long Left(long budget, long spent) => spent == long.MaxValue ? 0 : budget == long.MaxValue ? long.MaxValue : Math.Max(0, budget - spent);

    /// <summary>Lists the partitions to try for a square block, as the frame's edges allow and the effort chooses.</summary>
    private List<int> Candidates(int bsize, bool hasRows, bool hasCols)
    {
        List<int> list = [];
        if (bsize < Bs.B8x8)
        {
            list.Add(Partition.None);
            return list;
        }

        if (!hasRows && !hasCols)
        {
            list.Add(Partition.Split);
            return list;
        }

        int side = Av1Tables.BlockSizeWide[bsize];
        bool whole = side <= _profile.MaxBlock;
        if (!hasRows || !hasCols)
        {
            list.Add(Partition.Split);
            if (whole)
            {
                list.Add(hasRows ? Partition.Vert : Partition.Horz);
            }

            return list;
        }

        if (whole)
        {
            list.Add(Partition.None);
        }

        if (side > _profile.MinBlock || !whole)
        {
            list.Add(Partition.Split);
        }

        if (!whole)
        {
            return list;
        }

        if (_profile.Rectangles)
        {
            list.Add(Partition.Horz);
            list.Add(Partition.Vert);
        }

        if (_profile.ExtendedPartitions && bsize > Bs.B8x8)
        {
            list.AddRange([Partition.HorzA, Partition.HorzB, Partition.VertA, Partition.VertB]);
            if (bsize != Bs.B128x128)
            {
                list.AddRange([Partition.Horz4, Partition.Vert4]);
            }
        }

        return list;
    }

    /// <summary>Gets the rate-distortion cost of a partition symbol, at the node's contexts.</summary>
    private long PartitionCost(int miRow, int miCol, int bsize, bool hasRows, bool hasCols, int partition)
    {
        if (bsize < Bs.B8x8)
        {
            return 0;
        }

        Av1CostSink cost = new(false);
        CodePartitionSymbol(ref cost, miRow, miCol, hasRows, hasCols, bsize, partition);
        return Av1Rate.RdCost(_rdmult, cost.Cost, 0);
    }

    /// <summary>
    /// Decides one block: its luma mode and transforms, then its chroma mode, each by rate-distortion; leaves it decided and
    /// reconstructed and returns its cost (the rate of every symbol it codes, and its distortion), or <see cref="long.MaxValue"/>
    /// once past the budget.
    /// </summary>
    private long SearchBlock(int miRow, int miCol, int partition, int bsize, long budget)
    {
        int bw = Av1Tables.MiSizeWide[bsize], bh = Av1Tables.MiSizeHigh[bsize];
        int xMis = Math.Min(bw, _miCols - miCol), yMis = Math.Min(bh, _frame.MiRows - miRow);
        // A superblock's first block codes its quantizer; the others keep it.
        bool sbOrigin = ((miRow | miCol) & (_sbMi - 1)) == 0;
        Av1BlockInfo b = new() { Size = bsize, Partition = partition, MiRow = miRow, MiCol = miCol, QIndex = sbOrigin ? _sbQIndex : _currentQIndex };
        for (int y = 0; y < yMis; y++)
        {
            _frame.Grid.AsSpan(((miRow + y) * _miCols) + miCol, xMis).Fill(b);
        }

        _b = b;
        SetBlockPosition(miRow, miCol, bw, bh);
        _rdmult = BlockRdmult(miRow, miCol, bsize);
        _blockSegment = b.SegmentId;
        _blockQIndex = b.QIndex;
        bool lossless = _header.LosslessArray[b.SegmentId];
        Snapshot start = Take(miRow, miCol, bsize, pixels: true);
        bool chroma = _planes > 1 && _isChromaRef;
        bool cfl = chroma && CflAllowed();
        (long Cost, InterChoice Choice) inter = _header.FrameIsIntra ? (long.MaxValue, default) : SearchInter(bsize, start, budget, chroma);
        budget = Math.Min(budget, inter.Cost);
        (long Cost, int Row, int Col) intrabc = IntrabcAllowed(bsize) ? SearchIntrabc(bsize, start, budget, chroma) : (long.MaxValue, 0, 0);
        budget = Math.Min(budget, intrabc.Cost);

        // An inter block predicted well enough to drop its residual is not searched for an intra mode, below the efforts that
        // search everything (libaom's skip_intra_in_interframe).
        if (inter.Cost != long.MaxValue && inter.Choice.Skip && !_profile.IntraAfterSkip)
        {
            start.Restore(this);
            long kept = EncodeInterWinner(inter.Choice, start, chroma);
            Give(start);
            return kept;
        }

        // Luma: the modes whose prediction alone costs least, coded in full at the largest transform; then the best of them at
        // each smaller transform depth. Chroma's own symbols count at DC.
        LumaChoice bestLuma = default;
        long bestLumaCost = long.MaxValue;
        List<LumaChoice> lumaCandidates = LumaCandidates(bsize, lossless);
        if (_profile.LumaShortlist > 0 && lumaCandidates.Count > _profile.LumaShortlist)
        {
            _scores.Clear();
            for (int i = 0; i < lumaCandidates.Count; i++)
            {
                _scores.Add((LumaPredictionCost(lumaCandidates[i], start), i));
            }

            Keep(lumaCandidates, _profile.LumaShortlist);
        }

        FindLumaPalettes(bsize);
        for (int i = 0; i < _lumaPaletteCount; i++)
        {
            lumaCandidates.Add(new LumaChoice(Pred.Dc, 0, -1, 0, i));
        }

        foreach (LumaChoice candidate in lumaCandidates)
        {
            start.Restore(this, lumaOnly: true);
            long cost = Luma(candidate, cfl, Math.Min(budget, bestLumaCost), searchTypes: _profile.TypesInModeSearch);
            if (cost < bestLumaCost)
            {
                (bestLumaCost, bestLuma) = (cost, candidate);
            }
        }

        int maxDepth = lossless || bsize == Bs.B4x4 ? 0 : Math.Min(Av1Tables.MaxTxDepth[bsize], _profile.TxDepth);
        LumaChoice depthBase = bestLuma;
        for (int depth = 1; depth <= maxDepth && bestLumaCost != long.MaxValue; depth++)
        {
            LumaChoice candidate = depthBase with { Depth = depth };
            start.Restore(this, lumaOnly: true);
            long cost = Luma(candidate, cfl, Math.Min(budget, bestLumaCost), searchTypes: _profile.TypesInModeSearch);
            if (cost < bestLumaCost)
            {
                (bestLumaCost, bestLuma) = (cost, candidate);
            }
        }

        if (bestLumaCost == long.MaxValue)
        {
            start.Restore(this);
            long cost = long.MaxValue;
            if (inter.Cost != long.MaxValue)
            {
                cost = EncodeInterWinner(inter.Choice, start, chroma);
            }
            else if (intrabc.Cost != long.MaxValue)
            {
                _winner = true;
                Intrabc(intrabc.Row, intrabc.Col, bsize, long.MaxValue);
                _winner = false;
                cost = FinishBlock(start, bsize, chroma);
            }

            Give(start);
            return cost;
        }

        start.Restore(this, lumaOnly: true);
        _winner = true;
        Luma(bestLuma, cfl, long.MaxValue, searchTypes: true);
        _winner = false;
        if (chroma)
        {
            Snapshot afterLuma = Take(miRow, miCol, bsize, pixels: true);
            ChromaChoice bestChroma = new(Pred.Dc, 0, 0, 0);
            long bestChromaCost = long.MaxValue;
            List<ChromaChoice> chromaCandidates = ChromaCandidates(bsize, cfl);
            if (_profile.ChromaShortlist > 0 && chromaCandidates.Count > _profile.ChromaShortlist)
            {
                _scores.Clear();
                for (int i = 0; i < chromaCandidates.Count; i++)
                {
                    _scores.Add((ChromaPredictionCost(chromaCandidates[i], afterLuma), i));
                }

                Keep(chromaCandidates, _profile.ChromaShortlist);
            }

            FindChromaPalettes(bsize);
            for (int i = 0; i < _chromaPaletteCount; i++)
            {
                chromaCandidates.Add(new ChromaChoice(Pred.Dc, 0, 0, 0, i));
            }

            foreach (ChromaChoice candidate in chromaCandidates)
            {
                afterLuma.Restore(this, chromaOnly: true);
                long cost = Chroma(candidate);
                if (cost < bestChromaCost)
                {
                    (bestChromaCost, bestChroma) = (cost, candidate);
                }
            }

            afterLuma.Restore(this, chromaOnly: true);
            _winner = true;
            Chroma(bestChroma);
            _winner = false;
            Give(afterLuma);
        }

        long best = FinishBlock(start, bsize, chroma);
        if (intrabc.Cost < best)
        {
            start.Restore(this);
            _winner = true;
            Intrabc(intrabc.Row, intrabc.Col, bsize, long.MaxValue);
            _winner = false;
            best = FinishBlock(start, bsize, chroma);
        }

        if (inter.Cost < best)
        {
            best = EncodeInterWinner(inter.Choice, start, chroma);
        }

        Give(start);
        return best;
    }

    /// <summary>
    /// Settles the coded block: its skip flag and color maps, then its exact cost, its symbols as the writer codes them from
    /// the contexts of <paramref name="start"/>, and its distortion.
    /// </summary>
    private long FinishBlock(Snapshot start, int bsize, bool chroma)
    {
        _b.Skip = !AnyCoefficient(bsize);
        if (_b.Skip && _b.IsInter)
        {
            SetSkipTxSize(bsize);
        }

        KeepSkipSegment();
        KeepColorMaps();
        start.RestoreContexts(this);

        // A whole superblock without residual codes no quantizer delta, and keeps the one before.
        if (_b.Skip && bsize == _frame.SbSize)
        {
            _b.QIndex = _currentQIndex;
        }

        Span<bool> cdef = stackalloc bool[4];
        _cdefTransmitted.CopyTo(cdef);
        Av1CostSink rate = new(false);
        CodeBlock(ref rate, _miRow, _miCol, bsize);
        cdef.CopyTo(_cdefTransmitted);
        long distortion = _planeDistortion[0] + (chroma ? _planeDistortion[1] + _planeDistortion[2] : 0);
        return Av1Rate.RdCost(_rdmult, rate.Cost, distortion);
    }

    /// <summary>
    /// Gives an inter block without residual the transform size the decoder takes for it, the largest (read_tx_size without
    /// tx_size symbols), its residual being none at any size.
    /// </summary>
    private void SetSkipTxSize(int bsize)
    {
        int tx;
        if (_header.LosslessArray[_b.SegmentId])
        {
            tx = Tx.T4x4;
        }
        else if (bsize > Bs.B4x4)
        {
            int largest = _header.TxMode == Av1TxMode.Only4x4 ? Tx.T4x4 : Tx.T64x64;
            int maxRect = Av1Tables.MaxTxSizeRectLookup[bsize];
            tx = Av1Tables.TxSizeSqrMap[maxRect] <= largest ? maxRect : largest;
        }
        else
        {
            tx = Av1Tables.MaxTxSizeRectLookup[bsize];
        }

        _b.TxSize = tx;
        int xMis = Math.Min(_bw, _miCols - _miCol), yMis = Math.Min(_bh, _frame.MiRows - _miRow);
        for (int y = 0; y < yMis; y++)
        {
            _frame.TxSizeMap.AsSpan(((_miRow + y) * _miCols) + _miCol, xMis).Fill((byte)tx);
        }
    }

    /// <summary>Keeps the <paramref name="count"/> candidates of lowest score in <see cref="_scores"/>, in order of score.</summary>
    private void Keep<T>(List<T> candidates, int count)
    {
        _scores.Sort();
        Span<T> kept = new T[count];
        for (int i = 0; i < count; i++)
        {
            kept[i] = candidates[_scores[i].Index];
        }

        candidates.Clear();
        foreach (T candidate in kept)
        {
            candidates.Add(candidate);
        }
    }

    /// <summary>
    /// Estimates a luma choice from its prediction alone: the squared prediction error plus its mode symbols, each transform
    /// block predicted with the source standing in for the reconstruction before it.
    /// </summary>
    private long LumaPredictionCost(LumaChoice choice, Snapshot start)
    {
        Av1BlockInfo b = _b;
        b.YMode = choice.Mode;
        b.AngleDeltaY = choice.AngleDelta;
        b.UseFilterIntra = choice.FilterMode >= 0;
        b.FilterIntraMode = Math.Max(choice.FilterMode, 0);
        b.UvMode = Pred.Dc;
        SetLumaPalette(choice.Palette);
        SetChromaPalette(-1);
        b.TxSize = _header.LosslessArray[b.SegmentId] ? Tx.T4x4 : Av1Tables.MaxTxSizeRectLookup[b.Size];
        long rate = ModeRate() + (choice.Palette >= 0 ? ColorMapRate(0) : 0);
        long error = PredictionErrorWithSource(0, b.Size);
        start.Restore(this, lumaOnly: true);
        return Av1Rate.RdCost(_rdmult, rate, error);
    }

    /// <summary>Estimates a chroma choice from its prediction alone, as <see cref="LumaPredictionCost"/> does for luma.</summary>
    private long ChromaPredictionCost(ChromaChoice choice, Snapshot afterLuma)
    {
        Av1BlockInfo b = _b;
        b.UvMode = choice.Mode;
        b.AngleDeltaUv = choice.AngleDelta;
        b.CflAlphaSigns = choice.CflSigns;
        b.CflAlphaIdx = choice.CflIdx;
        SetChromaPalette(choice.Palette);
        long rate = ModeRate() + (choice.Palette >= 0 ? ColorMapRate(1) : 0);
        long error = PredictionErrorWithSource(1, b.Size) + PredictionErrorWithSource(2, b.Size);
        afterLuma.Restore(this, chromaOnly: true);
        return Av1Rate.RdCost(_rdmult, rate, error);
    }

    /// <summary>Gets a plane's squared prediction error over the current block, copying the source in after each transform block.</summary>
    private long PredictionErrorWithSource(int plane, int bsize)
    {
        long error = 0;
        ushort[] frame = _frame.Plane<ushort>(plane);
        ushort[] source = _source.Planes[plane];
        int stride = _frame.Strides[plane];
        (int count, int txSize) = TransformBlocks(plane, bsize);
        int w = Av1Tables.TxSizeWide[txSize], h = Av1Tables.TxSizeHigh[txSize];
        for (int i = 0; i < count; i++)
        {
            (_, _, int x, int y) = _places[plane][i];
            _reconstructor.PredictIntraBlock(this, plane, x, y, txSize);
            error += Error(plane, x, y, txSize);
            for (int r = 0; r < h; r++)
            {
                int at = ((y + r) * stride) + x;
                source.AsSpan(at, w).CopyTo(frame.AsSpan(at));
            }
        }

        return error;
    }

    /// <summary>A luma choice: the mode, its angle delta, the filter intra mode (-1 for none), the transform depth and the candidate palette (-1 for none).</summary>
    private readonly record struct LumaChoice(int Mode, int AngleDelta, int FilterMode, int Depth, int Palette = -1);

    /// <summary>A chroma choice: the mode (<see cref="Pred.UvCfl"/> included), its angle delta, the chroma from luma alphas and the candidate palette (-1 for none).</summary>
    private readonly record struct ChromaChoice(int Mode, int AngleDelta, int CflSigns, int CflIdx, int Palette = -1);

    private List<LumaChoice> LumaCandidates(int bsize, bool lossless)
    {
        List<LumaChoice> list = _lumaCandidates;
        list.Clear();
        bool angles = bsize >= Bs.B8x8 && _profile.AngleDeltas;
        bool filter = !lossless && _profile.FilterIntra && _sequence.EnableFilterIntra
            && Av1Tables.BlockSizeWide[bsize] <= 32 && Av1Tables.BlockSizeHigh[bsize] <= 32;
        foreach (int mode in _profile.LumaModes)
        {
            int deltas = angles && Pred.IsDirectional(mode) ? 3 : 0;
            for (int delta = -deltas; delta <= deltas; delta++)
            {
                list.Add(new LumaChoice(mode, delta, -1, 0));
            }
        }

        if (filter)
        {
            for (int fm = 0; fm < 5; fm++)
            {
                list.Add(new LumaChoice(Pred.Dc, 0, fm, 0));
            }
        }

        return list;
    }

    private List<ChromaChoice> ChromaCandidates(int bsize, bool cfl)
    {
        List<ChromaChoice> list = _chromaCandidates;
        list.Clear();
        bool angles = bsize >= Bs.B8x8 && _profile.AngleDeltas;
        foreach (int mode in _profile.ChromaModes)
        {
            int deltas = angles && Pred.IsDirectional(mode) ? 3 : 0;
            for (int delta = -deltas; delta <= deltas; delta++)
            {
                list.Add(new ChromaChoice(mode, delta, 0, 0));
            }
        }

        if (cfl && _profile.Cfl)
        {
            ChromaChoice alphas = CflAlphas(bsize);
            if (alphas.Mode == Pred.UvCfl)
            {
                list.Add(alphas);
            }
        }

        return list;
    }

    /// <summary>Encodes the block's luma with a choice; returns luma's rate-distortion cost with the block's mode symbols.</summary>
    private long Luma(LumaChoice choice, bool cfl, long budget, bool searchTypes)
    {
        Av1BlockInfo b = _b;
        b.YMode = choice.Mode;
        b.AngleDeltaY = choice.AngleDelta;
        b.UseFilterIntra = choice.FilterMode >= 0;
        b.FilterIntraMode = Math.Max(choice.FilterMode, 0);
        SetLumaPalette(choice.Palette);
        SetChromaPalette(-1);

        // Chroma from luma needs the luma stored as it is reconstructed; any other chroma mode ignores it.
        b.UvMode = cfl ? Pred.UvCfl : Pred.Dc;
        b.AngleDeltaUv = 0;
        b.CflAlphaSigns = 0;
        b.CflAlphaIdx = 0;
        bool lossless = _header.LosslessArray[b.SegmentId];
        int tx = lossless ? Tx.T4x4 : Av1Tables.MaxTxSizeRectLookup[b.Size];
        for (int d = 0; d < choice.Depth; d++)
        {
            tx = Av1Tables.SubTxSizeMap[tx];
        }

        b.TxSize = tx;
        int xMis = Math.Min(_bw, _miCols - _miCol), yMis = Math.Min(_bh, _frame.MiRows - _miRow);
        for (int y = 0; y < yMis; y++)
        {
            _frame.TxSizeMap.AsSpan(((_miRow + y) * _miCols) + _miCol, xMis).Fill((byte)tx);
        }

        int uv = b.UvMode;
        b.UvMode = Pred.Dc;
        long cost = Av1Rate.RdCost(_rdmult, ModeRate() + (choice.Palette >= 0 ? ColorMapRate(0) : 0), 0);
        b.UvMode = uv;
        return cost >= budget ? long.MaxValue : Add(cost, EncodePlane(0, b.Size, Left(budget, cost), searchTypes));
    }

    /// <summary>Encodes the block's chroma with a choice; returns chroma's rate-distortion cost with the block's mode symbols.</summary>
    private long Chroma(ChromaChoice choice)
    {
        Av1BlockInfo b = _b;
        b.UvMode = choice.Mode;
        b.AngleDeltaUv = choice.AngleDelta;
        b.CflAlphaSigns = choice.CflSigns;
        b.CflAlphaIdx = choice.CflIdx;
        SetChromaPalette(choice.Palette);
        long cost = Av1Rate.RdCost(_rdmult, ModeRate() + (choice.Palette >= 0 ? ColorMapRate(1) : 0), 0);
        cost = Add(cost, EncodePlane(1, b.Size, long.MaxValue));
        return Add(cost, EncodePlane(2, b.Size, long.MaxValue));
    }

    /// <summary>
    /// Gets the chroma from luma alphas whose prediction alone is closest to the source, each chroma plane searched over every
    /// alpha, or around the minimum three of them place (<see cref="Av1SearchProfile.FullCflSearch"/>); DC when neither plane
    /// wants one.
    /// </summary>
    private ChromaChoice CflAlphas(int bsize)
    {
        Av1BlockInfo b = _b;
        b.UvMode = Pred.UvCfl;
        Span<int> sign = stackalloc int[3];
        Span<int> magnitude = stackalloc int[3];
        Span<long> errors = stackalloc long[33];
        for (int plane = 1; plane <= 2; plane++)
        {
            errors.Fill(-1);
            if (_profile.FullCflSearch)
            {
                for (int alpha = -16; alpha <= 16; alpha++)
                {
                    errors[alpha + 16] = CflError(plane, alpha, bsize);
                }
            }
            else
            {
                // The error is close to a parabola in alpha: three tries place its minimum, and its neighbours settle it.
                long left = errors[8] = CflError(plane, -8, bsize), middle = errors[16] = CflError(plane, 0, bsize), right = errors[24] = CflError(plane, 8, bsize);
                double curve = (left + right - (2.0 * middle)) / 128, slope = (right - left) / 16.0;
                int guess = curve > 0 ? (int)Math.Clamp(Math.Round(-slope / (2 * curve)), -16, 16) : left < right ? -16 : 16;
                for (int alpha = Math.Max(guess - 1, -16); alpha <= Math.Min(guess + 1, 16); alpha++)
                {
                    if (errors[alpha + 16] < 0)
                    {
                        errors[alpha + 16] = CflError(plane, alpha, bsize);
                    }
                }
            }

            int best = 16;
            for (int i = 0; i < 33; i++)
            {
                if (errors[i] >= 0 && (errors[best] < 0 || errors[i] < errors[best]))
                {
                    best = i;
                }
            }

            int bestAlpha = best - 16;
            sign[plane] = bestAlpha == 0 ? 0 : bestAlpha < 0 ? 1 : 2;
            magnitude[plane] = bestAlpha == 0 ? 0 : Math.Abs(bestAlpha) - 1;
        }

        b.UvMode = Pred.Dc;
        return sign[1] == 0 && sign[2] == 0
            ? new ChromaChoice(Pred.Dc, 0, 0, 0)
            : new ChromaChoice(Pred.UvCfl, 0, (3 * sign[1]) + sign[2] - 1, (magnitude[1] << 4) | magnitude[2]);
    }

    /// <summary>Gets the error of a chroma plane's prediction from luma with one alpha, the other plane's sign held nonzero so the joint sign is valid.</summary>
    private long CflError(int plane, int alpha, int bsize)
    {
        int sign = alpha == 0 ? 0 : alpha < 0 ? 1 : 2;
        int magnitude = alpha == 0 ? 0 : Math.Abs(alpha) - 1;
        int signU = plane == 1 ? sign : 2, signV = plane == 2 ? sign : 2;
        _b.CflAlphaSigns = (3 * signU) + signV - 1;
        _b.CflAlphaIdx = plane == 1 ? magnitude << 4 : magnitude;
        return PredictionError(plane, bsize);
    }

    /// <summary>Gets the squared error of the current block's prediction alone in a plane, each transform block predicted from the frame as it stands.</summary>
    private long PredictionError(int plane, int bsize)
    {
        long error = 0;
        (int count, int txSize) = TransformBlocks(plane, bsize);
        for (int i = 0; i < count; i++)
        {
            (_, _, int x, int y) = _places[plane][i];
            _reconstructor.PredictIntraBlock(this, plane, x, y, txSize);
            error += Error(plane, x, y, txSize);
        }

        return error;
    }

    /// <summary>
    /// Encodes every transform block of the current block in one plane, in the decoder's order, each with the transform type of
    /// lowest cost; returns the plane's coefficient rate-distortion cost, or <see cref="long.MaxValue"/> past the budget.
    /// </summary>
    private long EncodePlane(int plane, int bsize, long budget, bool searchTypes = true)
    {
        long cost = 0;
        _planeDistortion[plane] = 0;
        (int count, int txSize) = TransformBlocks(plane, bsize);
        for (int i = 0; i < count; i++)
        {
            (int blkRow, int blkCol, int x, int y) = _places[plane][i];
            cost = Add(cost, TransformBlock(plane, blkRow, blkCol, txSize, x, y, searchTypes));
            if (cost > budget)
            {
                return long.MaxValue;
            }
        }

        return cost;
    }

    /// <summary>A transform block's place: in 4x4 units from the block's plane origin, and in samples in the plane.</summary>
    private readonly record struct TxPlace(int BlkRow, int BlkCol, int X, int Y);

    /// <summary>
    /// Lists a plane's transform blocks of the current block in the decoder's order (in 64x64 units, then rows, then columns)
    /// into the plane's buffer; returns their count and size.
    /// </summary>
    private (int Count, int TxSize) TransformBlocks(int plane, int bsize)
    {
        TxPlace[] places = _places[plane];
        int count = 0;
        Av1BlockInfo b = _b;
        int maxW = MaxBlockWide(bsize, 0), maxH = MaxBlockHigh(bsize, 0);
        int muW = Math.Min(maxW, 16), muH = Math.Min(maxH, 16);
        bool lossless = _header.LosslessArray[b.SegmentId];
        int ssx = plane > 0 ? _ssx : 0, ssy = plane > 0 ? _ssy : 0;
        int txSize = lossless ? Tx.T4x4 : plane == 0 ? b.TxSize : Av1Tables.MaxUvTxSize(bsize, _ssx, _ssy);
        int stepR = Av1Tables.TxSizeHighUnit[txSize], stepC = Av1Tables.TxSizeWideUnit[txSize];
        (int col4, int row4) = PlaneOrigin(plane);
        for (int row = 0; row < maxH; row += muH)
        {
            for (int col = 0; col < maxW; col += muW)
            {
                int unitH = (Math.Min(muH + row, maxH) + ssy) >> ssy;
                int unitW = (Math.Min(muW + col, maxW) + ssx) >> ssx;
                for (int blkRow = row >> ssy; blkRow < unitH; blkRow += stepR)
                {
                    for (int blkCol = col >> ssx; blkCol < unitW; blkCol += stepC)
                    {
                        places[count++] = new TxPlace(blkRow, blkCol, (col4 + blkCol) * 4, (row4 + blkRow) * 4);
                    }
                }
            }
        }

        return (count, txSize);
    }

    /// <summary>
    /// Predicts one transform block, then for each transform type the effort allows transforms, quantizes and measures it, and
    /// keeps the type of lowest cost: its levels in the decisions, its reconstruction in the frame, its contexts set. Returns its
    /// cost: the coefficient rate (all-zero flag, type, levels) and the distortion inside the frame.
    /// </summary>
    private long TransformBlock(int plane, int blkRow, int blkCol, int txSize, int x, int y, bool searchTypes)
    {
        Av1BlockInfo b = _b;
        if (!b.IsInter)
        {
            _reconstructor.PredictIntraBlock(this, plane, x, y, txSize);
        }

        int w = Av1Tables.TxSizeWide[txSize], h = Av1Tables.TxSizeHigh[txSize];
        ushort[] frame = _frame.Plane<ushort>(plane);
        int stride = _frame.Strides[plane];
        ushort[] source = _source.Planes[plane];
        for (int r = 0; r < h; r++)
        {
            int at = ((y + r) * stride) + x;
            frame.AsSpan(at, w).CopyTo(_predicted.AsSpan(r * w));
            for (int c = 0; c < w; c++)
            {
                _residual[(r * w) + c] = source[at + c] - frame[at + c];
            }
        }

        bool lossless = _header.LosslessArray[b.SegmentId];
        int originIndex = ((_miRow + blkRow) * _miCols) + _miCol + blkCol;
        ReadOnlySpan<int> types = plane > 0 ? [GetTxType(1, blkRow, blkCol, txSize)]
            : lossless ? [TxType.DctDct]
            : searchTypes ? TxTypes(txSize)
            : [DefaultTxType(txSize)];
        if (types.Length == 1)
        {
            return TryType(plane, blkRow, blkCol, txSize, types[0], x, y, originIndex, lossless, keep: true);
        }

        byte[] aboveCtx = _aboveEntropy[plane], leftCtx = _leftEntropy[plane];
        int aOff = _aboveOffset[plane] + blkCol, lOff = _leftOffset[plane] + blkRow;
        int txw = Av1Tables.TxSizeWideUnit[txSize], txh = Av1Tables.TxSizeHighUnit[txSize];
        Span<byte> aboveSaved = stackalloc byte[16];
        Span<byte> leftSaved = stackalloc byte[16];
        aboveCtx.AsSpan(aOff, txw).CopyTo(aboveSaved);
        leftCtx.AsSpan(lOff, txh).CopyTo(leftSaved);
        long bestCost = long.MaxValue;
        int bestType = types[0];
        foreach (int txType in types)
        {
            long cost = TryType(plane, blkRow, blkCol, txSize, txType, x, y, originIndex, lossless, keep: false);
            if (cost < bestCost)
            {
                (bestCost, bestType) = (cost, txType);
            }

            aboveSaved[..txw].CopyTo(aboveCtx.AsSpan(aOff));
            leftSaved[..txh].CopyTo(leftCtx.AsSpan(lOff));
        }

        return TryType(plane, blkRow, blkCol, txSize, bestType, x, y, originIndex, lossless, keep: true);
    }

    /// <summary>Transforms, quantizes, costs and reconstructs the residual with one type; puts the prediction back unless it is kept.</summary>
    private long TryType(int plane, int blkRow, int blkCol, int txSize, int txType, int x, int y, int originIndex, bool lossless, bool keep)
    {
        int w = Av1Tables.TxSizeWide[txSize], h = Av1Tables.TxSizeHigh[txSize];
        Av1ForwardTransform.Forward(_residual, w, _transformed, txSize, txType, lossless);
        int adjusted = Av1Tables.AdjustedTxSize(txSize);
        int codedW = Av1Tables.TxSizeWide[adjusted], codedH = Av1Tables.TxSizeHigh[adjusted];
        int area = codedW * codedH;
        int eob = Quantize(plane, txSize, txType, area, lossless);
        long trellisRate = -1;
        if (Trellis && !lossless && eob > 0)
        {
            (int skipCtx, int dcSignCtx) = TxbContext(plane, blkRow, blkCol, txSize);
            eob = Av1Trellis.Optimize(_transformed, _quantized, _coeffs, eob, txSize, txType, plane, dcSignCtx, _table!.Steps,
                _profile.QmDistortion ? _table.Weights : null, _costs, _rdmult, _sequence.BitDepth, out long levelsRate);

            // The trellis's own rate, with the all-zero flag and the type, is the rate the syntax codes: each level's contexts
            // come from the levels after it in scan order, which it has settled by then.
            int txs = (Av1Tables.TxSizeSqrMap[txSize] + Av1Tables.TxSizeSqrUpMap[txSize] + 1) >> 1;
            trellisRate = eob == 0 ? _costs.TxbSkipCost(txs, skipCtx, 1) : _costs.TxbSkipCost(txs, skipCtx, 0) + levelsRate + TxTypeRate(plane, txSize, txType);
        }

        int[] levels = _decisions.Levels[plane];
        int levelStride = _decisions.Strides[plane];
        int bhl = Av1Tables.TxSizeHighLog2[adjusted];
        for (int c = 0; c < codedW; c++)
        {
            for (int r = 0; r < codedH; r++)
            {
                levels[((y + r) * levelStride) + x + c] = _quantized[(c << bhl) + r];
            }
        }

        if (plane == 0)
        {
            _frame.TxTypeMap[originIndex] = (byte)(eob == 0 ? TxType.DctDct : txType);
        }

        Av1CostSink rate = new(false);
        if (trellisRate >= 0)
        {
            rate.Cost = trellisRate;
            SetEntropyContexts(plane, blkRow, blkCol, txSize, CulLevel(txSize, txType, eob));
        }
        else
        {
            rate.Cost = CoefficientRate(plane, blkRow, blkCol, txSize, eob == 0 ? TxType.DctDct : txType, eob);
        }

        // Tune IQ's QM-PSNR: where a quantization matrix weighs the coefficients, so does the distortion, measured on them.
        bool weighted = eob > 0 && _table!.Weights is not null && _profile.QmDistortion && Av1Tables.TxSizeSqrUpMap[txSize] != Tx.T64x64;
        long distortion = weighted ? WeightedError(txSize, area) : 0;
        if (_b.IsInter)
        {
            _reconstructor.InterResidual(this, plane, x, y, txSize, eob == 0 ? TxType.DctDct : txType, eob, _coeffs);
        }
        else
        {
            _reconstructor.FinishIntraBlock(this, plane, x, y, txSize, eob == 0 ? TxType.DctDct : txType, eob, _coeffs);
        }
        short[] scan = Av1Tables.ScanOrders[(txSize * 16) + txType];
        for (int c = 0; c < eob; c++)
        {
            _coeffs[scan[c]] = 0;
        }

        if (!weighted)
        {
            distortion = Error(plane, x, y, txSize);
        }

        if (keep)
        {
            _planeDistortion[plane] += distortion;
        }
        if (!keep)
        {
            ushort[] frame = _frame.Plane<ushort>(plane);
            int stride = _frame.Strides[plane];
            for (int r = 0; r < h; r++)
            {
                _predicted.AsSpan(r * w, w).CopyTo(frame.AsSpan(((y + r) * stride) + x));
            }
        }

        return Av1Rate.RdCost(_rdmult, rate.Cost, distortion);
    }

    /// <summary>Gets whether the transform blocks being tried are optimized by the trellis.</summary>
    private bool Trellis => _profile.Trellis && (_winner || _profile.TrellisInSearch);

    /// <summary>Gets the rate of a luma transform block's type.</summary>
    private long TxTypeRate(int plane, int txSize, int txType)
    {
        if (plane > 0)
        {
            return 0;
        }

        Av1CostSink rate = new(false);
        CodeTxType(ref rate, txSize, txType);
        return rate.Cost;
    }

    /// <summary>Gets a transform block's entropy context from its levels: their sum up to 7, and the DC's sign (get_cul_level).</summary>
    private int CulLevel(int txSize, int txType, int eob)
    {
        short[] scan = Av1Tables.ScanOrders[(txSize * 16) + txType];
        int sum = 0;
        for (int c = 0; c < eob && sum < 7; c++)
        {
            sum += Math.Abs(_quantized[scan[c]]);
        }

        int cul = Math.Min(7, sum);
        int dc = _quantized[0];
        return dc < 0 ? cul | 8 : dc > 0 ? cul + 16 : cul;
    }

    /// <summary>Gets the luma transform type the mode suggests, as chroma's follows its mode, when the size's set has it; else DCT_DCT.</summary>
    private int DefaultTxType(int txSize)
    {
        if (_b.IsInter)
        {
            return TxType.DctDct;
        }

        int type = _b.UseFilterIntra ? Av1Tables.IntraModeToTxType[Av1Tables.FilterIntraToMode[_b.FilterIntraMode]] : Av1Tables.IntraModeToTxType[_b.YMode];
        int setType = Av1Tables.ExtTxSetType(txSize, false, _header.ReducedTxSet);
        return ((Av1Tables.ExtTxUsedMask[setType] >> type) & 1) != 0 ? type : TxType.DctDct;
    }

    /// <summary>Gets the transform types to try for a luma transform size: those of its set the effort allows.</summary>
    private ReadOnlySpan<int> TxTypes(int txSize)
    {
        int setType = Av1Tables.ExtTxSetType(txSize, _b.IsInter, _header.ReducedTxSet);
        int n = Av1Tables.NumExtTxSet[setType];
        int[] types = _profile.TxTypeBuffer;
        int count = 0;
        for (int i = 0; i < n; i++)
        {
            int type = Av1Tables.ExtTxInverse[(setType * 16) + i];
            if (Array.IndexOf(_profile.TxTypes, type) >= 0)
            {
                types[count++] = type;
            }
        }

        if (count == 0)
        {
            types[count++] = TxType.DctDct;
        }

        return types.AsSpan(0, count);
    }

    /// <summary>
    /// Gets a transform block's distortion from its coefficients, each error weighted by the quantization matrix, at the scale
    /// of <see cref="Error"/> (libaom's av1_block_error_qm and dist_block_tx_domain, tx_search.c).
    /// </summary>
    private long WeightedError(int txSize, int area)
    {
        int[] weights = _table!.Weights!;
        long error = 0;
        for (int i = 0; i < area; i++)
        {
            long d = (long)(_transformed[i] - _coeffs[i]) * weights[i];
            error += ((d * d) + 512) >> 10;
        }

        int depthShift = 2 * (_sequence.BitDepth - 8);
        error = (error + ((1 << depthShift) >> 1)) >> depthShift;
        int pels = Av1Tables.TxSizeWide[txSize] * Av1Tables.TxSizeHigh[txSize];
        int scale = (pels > 256 ? 1 : 0) + (pels > 1024 ? 1 : 0);
        return error >> ((1 - scale) * 2);
    }

    /// <summary>Gets the distortion of a transform block's part inside the image: 16 times its squared error, at 8-bit scale.</summary>
    private long Error(int plane, int x, int y, int txSize)
    {
        int w = Math.Min(Av1Tables.TxSizeWide[txSize], _source.Widths[plane] - x);
        int h = Math.Min(Av1Tables.TxSizeHigh[txSize], _source.Heights[plane] - y);
        ushort[] frame = _frame.Plane<ushort>(plane);
        ushort[] source = _source.Planes[plane];
        int stride = _frame.Strides[plane];
        long error = 0;
        for (int r = 0; r < h; r++)
        {
            int at = ((y + r) * stride) + x;
            for (int c = 0; c < w; c++)
            {
                int d = source[at + c] - frame[at + c];
                error += d * d;
            }
        }

        // libaom's distortion: 16 times the squared error, at 8-bit scale.
        return _sequence.BitDepth switch
        {
            10 => error,
            12 => (error + 8) >> 4,
            _ => error << 4,
        };
    }

    /// <summary>Gets the current block's squared error in a plane, after its reconstruction.</summary>
    private long Distortion(int plane, int bsize)
    {
        long error = 0;
        (int count, int txSize) = TransformBlocks(plane, bsize);
        for (int i = 0; i < count; i++)
        {
            (_, _, int x, int y) = _places[plane][i];
            error += Error(plane, x, y, txSize);
        }

        return error;
    }

    /// <summary>Gets whether any transform block of the current block has a nonzero coefficient.</summary>
    private bool AnyCoefficient(int bsize)
    {
        bool any = false;
        for (int plane = 0; plane < _planes && !any; plane++)
        {
            if (plane > 0 && !_isChromaRef)
            {
                break;
            }

            int[] levels = _decisions.Levels[plane];
            int stride = _decisions.Strides[plane];
            (int count, int txSize) = TransformBlocks(plane, bsize);
            int adjusted = Av1Tables.AdjustedTxSize(txSize);
            for (int i = 0; i < count && !any; i++)
            {
                (_, _, int x, int y) = _places[plane][i];
                for (int r = 0; r < Av1Tables.TxSizeHigh[adjusted] && !any; r++)
                {
                    any = levels.AsSpan(((y + r) * stride) + x, Av1Tables.TxSizeWide[adjusted]).IndexOfAnyExcept(0) >= 0;
                }
            }
        }

        return any;
    }

    /// <summary>Gets the rate of the block's mode and transform size symbols as they stand, leaving the tile's state as it was.</summary>
    private long ModeRate()
    {
        int savedQIndex = _currentQIndex;
        Span<int> deltaLf = stackalloc int[4];
        _deltaLf.CopyTo(deltaLf);
        Span<bool> cdef = stackalloc bool[4];
        _cdefTransmitted.CopyTo(cdef);
        Av1CostSink rate = new(false);
        CodeModeInfo(ref rate);
        bool lossless = _header.LosslessArray[_b.SegmentId];
        if (_b.IsInter && _header.TxMode == Av1TxMode.Select && _b.Size > Bs.B4x4 && !lossless && !_b.Skip)
        {
            // The transform tree, assuming the block has a residual, its contexts put back after.
            Span<byte> above = stackalloc byte[16], left = stackalloc byte[16];
            _aboveTxfm.AsSpan(_miCol - MiColStart, _bw).CopyTo(above);
            _leftTxfm.AsSpan(_miRow & MaxMibMask, _bh).CopyTo(left);
            CodeVarTxTree(ref rate, _b.Size);
            above[.._bw].CopyTo(_aboveTxfm.AsSpan(_miCol - MiColStart));
            left[.._bh].CopyTo(_leftTxfm.AsSpan(_miRow & MaxMibMask));
        }
        else if (!_b.IsInter || !_b.Skip)
        {
            CodeTxSize(ref rate, _b.Size, _b.IsInter, true, lossless);
        }

        _currentQIndex = savedQIndex;
        deltaLf.CopyTo(_deltaLf);
        cdef.CopyTo(_cdefTransmitted);
        return rate.Cost;
    }

    /// <summary>
    /// Quantizes the transformed block into levels (a dead zone: AC rounds up from 3/8 of a step before the trellis, a third
    /// without it, DC from half a step), and writes their dequantized values, as the decoder makes them, into the coefficients;
    /// returns the end of block in scan order.
    /// </summary>
    private int Quantize(int plane, int txSize, int txType, int area, bool lossless)
    {
        int bitDepth = _sequence.BitDepth;
        int qindex = _header.DeltaQPresent ? SegmentQIndex(_b.SegmentId, _b.QIndex) : _header.SegQIdx[_b.SegmentId];
        int pels = Av1Tables.TxSizeWide[txSize] * Av1Tables.TxSizeHigh[txSize];
        int shift = (pels > 256 ? 1 : 0) + (pels > 1024 ? 1 : 0);
        int maxValue = (1 << (7 + bitDepth)) - 1, minValue = -(1 << (7 + bitDepth));
        short[] scan = Av1Tables.ScanOrders[(txSize * 16) + txType];
        QuantTable table = _table = Table(plane, txSize, !lossless && txType < TxType.Idtx, qindex, area);
        int[] steps = table.Steps;
        uint[] inverses = table.Inverses;
        int eob = 0;
        if (!lossless)
        {
            // In raster order, which is sequential in memory; most levels are 0, which a sample below its step minus its
            // rounding gives without the division.
            int[] rounding = Trellis ? table.TrellisRounding : table.Rounding;
            short[] inverseScan = InverseScan(txSize, txType);
            for (int pos = 0; pos < area; pos++)
            {
                int value = _transformed[pos];
                long abs = (long)Math.Abs(value) << shift;
                int dqv = steps[pos];
                if (abs + rounding[pos] < dqv)
                {
                    _quantized[pos] = 0;
                    continue;
                }

                int level = (int)Math.Min(((ulong)(abs + rounding[pos]) * inverses[pos]) >> 32, (1 << 20) - 1);
                _quantized[pos] = value < 0 ? -level : level;
                eob = Math.Max(eob, inverseScan[pos] + 1);
                int dq = (int)(((long)level * dqv) & 0xffffff) >> shift;
                _coeffs[pos] = Math.Clamp(value < 0 ? -dq : dq, minValue, maxValue);
            }

            return eob;
        }

        for (int c = 0; c < area; c++)
        {
            int pos = scan[c];
            int value = _transformed[pos];
            int dqv = steps[pos];
            int level = value / dqv;
            _quantized[pos] = level;
            if (level != 0)
            {
                eob = c + 1;
                int dq = (int)(((long)Math.Abs(level) * dqv) & 0xffffff) >> shift;
                _coeffs[pos] = Math.Clamp(level < 0 ? -dq : dq, minValue, maxValue);
            }
        }

        return eob;
    }

    /// <summary>
    /// Gets the quantizer step of each coefficient of a transform size in a plane, the quantization matrix applied when
    /// <paramref name="matrix"/> is set and the frame uses one, with each step's reciprocal; cached by size, plane and qindex.
    /// </summary>
    private QuantTable Table(int plane, int txSize, bool matrix, int qindex, int area)
    {
        int key = (((((qindex * 3) + plane) * Tx.Count) + txSize) * 2) + (matrix ? 1 : 0);
        if (_tables.TryGetValue(key, out QuantTable? cached))
        {
            return cached;
        }

        int bitDepth = _sequence.BitDepth;
        (int dcDelta, int acDelta) = plane switch
        {
            0 => (_header.DeltaQYDc, 0),
            1 => (_header.DeltaQUDc, _header.DeltaQUAc),
            _ => (_header.DeltaQVDc, _header.DeltaQVAc),
        };
        int dcQ = DcQ(qindex + dcDelta, bitDepth), acQ = AcQ(qindex + acDelta, bitDepth);
        int qmLevel = matrix && _header.UsingQmatrix ? (plane == 0 ? _header.QmY : plane == 1 ? _header.QmU : _header.QmV) : 15;
        int qmOffset = qmLevel < 15 ? (((qmLevel * 2) + (plane > 0 ? 1 : 0)) * 3344) + Av1Tables.QmOffset(Av1Tables.AdjustedTxSize(txSize)) : -1;
        byte[] matrices = Av1Tables.InverseQuantizerMatrices;
        QuantTable table = new(new int[area], new uint[area], qmOffset >= 0 ? new int[area] : null, new int[area], new int[area]);
        for (int pos = 0; pos < area; pos++)
        {
            int dqv = pos == 0 ? dcQ : acQ;
            if (qmOffset >= 0)
            {
                // The decoder's weighted step: quantizing by it keeps each frequency's error in its step.
                dqv = ((matrices[qmOffset + pos] * dqv) + 16) >> 5;
                table.Weights![pos] = Av1Tables.QuantizerMatrices[qmOffset + pos];
            }

            table.Steps[pos] = dqv;
            table.Rounding[pos] = pos == 0 ? dqv >> 1 : (dqv * 21) >> 6;
            table.TrellisRounding[pos] = pos == 0 ? dqv >> 1 : (dqv * 24) >> 6;
            table.Inverses[pos] = (uint)(((1UL << 32) + (ulong)dqv - 1) / (ulong)dqv);
        }

        _tables[key] = table;
        return table;
    }

    /// <summary>The quantizer steps of a transform size, their reciprocals times 2^32, and the quantization matrix's weights when it has one.</summary>
    private sealed record QuantTable(int[] Steps, uint[] Inverses, int[]? Weights, int[] Rounding, int[] TrellisRounding);

    private static readonly short[]?[] InverseScans = new short[Tx.Count * 16][];

    /// <summary>Gets each coefficient's index in the scan of a transform size and type.</summary>
    private static short[] InverseScan(int txSize, int txType)
    {
        int key = (txSize * 16) + txType;
        if (InverseScans[key] is short[] cached)
        {
            return cached;
        }

        short[] scan = Av1Tables.ScanOrders[key];
        short[] inverse = new short[scan.Length];
        for (int c = 0; c < scan.Length; c++)
        {
            inverse[scan[c]] = (short)c;
        }

        return InverseScans[key] = inverse;
    }

    private Snapshot Take(int miRow, int miCol, int bsize, bool pixels)
    {
        Snapshot snapshot = _pool.Count > 0 ? _pool.Pop() : new Snapshot(this);
        snapshot.Save(this, miRow, miCol, bsize, pixels);
        return snapshot;
    }

    private void Give(Snapshot snapshot) => _pool.Push(snapshot);

    /// <summary>
    /// The state a search changes in a square area, to put back after a candidate: the above and left contexts, the grid and
    /// maps, the reconstruction and the levels.
    /// </summary>
    private sealed class Snapshot
    {
        private readonly byte[][] _above = [new byte[80], new byte[80], new byte[80]];
        private readonly byte[] _abovePartition = new byte[32];
        private readonly byte[] _aboveTxfm = new byte[32];
        private readonly byte[][] _left = [new byte[32], new byte[32], new byte[32]];
        private readonly byte[] _leftPartition = new byte[32];
        private readonly byte[] _leftTxfm = new byte[32];
        private readonly Av1BlockInfo?[] _grid = new Av1BlockInfo?[32 * 32];
        private readonly byte[] _segments = new byte[32 * 32];
        private readonly byte[] _txTypes = new byte[32 * 32];
        private readonly byte[] _txSizes = new byte[32 * 32];
        private readonly ushort[][] _pixels = new ushort[3][];
        private readonly int[][] _levels = new int[3][];
        private readonly (int X, int Y, int W, int H)[] _areas = new (int, int, int, int)[3];
        private readonly (int Lo, int Count)[] _aboveRanges = new (int, int)[3];
        private int _miRow, _miCol, _rows, _cols, _bw;
        private int _currentQIndex;
        private bool _pixelsSaved;

        internal Snapshot(Av1TileEncoder tile)
        {
            for (int plane = 0; plane < tile._planes; plane++)
            {
                _pixels[plane] = new ushort[136 * 136];
                _levels[plane] = new int[136 * 136];
            }
        }

        internal void Save(Av1TileEncoder t, int miRow, int miCol, int bsize, bool pixels)
        {
            int bw = Av1Tables.MiSizeWide[bsize], bh = Av1Tables.MiSizeHigh[bsize];
            (_miRow, _miCol, _bw) = (miRow, miCol, bw);
            _rows = Math.Min(bh, t._frame.MiRows - miRow);
            _cols = Math.Min(bw, t._miCols - miCol);
            for (int plane = 0; plane < t._planes; plane++)
            {
                int ssx = plane > 0 ? t._ssx : 0;
                int lo = Math.Max(0, ((miCol - 1) >> ssx) - (t.MiColStart >> ssx));
                int hi = Math.Min(t._aboveEntropy[plane].Length, ((miCol + bw) >> ssx) - (t.MiColStart >> ssx) + 1);
                _aboveRanges[plane] = (lo, hi - lo);
                t._aboveEntropy[plane].AsSpan(lo, hi - lo).CopyTo(_above[plane]);
                t._leftEntropy[plane].CopyTo(_left[plane], 0);
            }

            t._abovePartition.AsSpan(miCol - t.MiColStart, bw).CopyTo(_abovePartition);
            t._aboveTxfm.AsSpan(miCol - t.MiColStart, bw).CopyTo(_aboveTxfm);
            t._leftPartition.CopyTo(_leftPartition, 0);
            t._leftTxfm.CopyTo(_leftTxfm, 0);
            _currentQIndex = t._currentQIndex;
            for (int r = 0; r < _rows; r++)
            {
                int at = ((miRow + r) * t._miCols) + miCol;
                t._frame.Grid.AsSpan(at, _cols).CopyTo(_grid.AsSpan(r * bw));
                t._frame.SegmentMap.AsSpan(at, _cols).CopyTo(_segments.AsSpan(r * bw));
                t._frame.TxTypeMap.AsSpan(at, _cols).CopyTo(_txTypes.AsSpan(r * bw));
                t._frame.TxSizeMap.AsSpan(at, _cols).CopyTo(_txSizes.AsSpan(r * bw));
            }

            _pixelsSaved = pixels;
            if (!pixels)
            {
                return;
            }

            for (int plane = 0; plane < t._planes; plane++)
            {
                (int x, int y, int w, int h) = Area(t, plane, miRow, miCol, bw, bh);
                _areas[plane] = (x, y, w, h);
                ushort[] data = t._frame.Plane<ushort>(plane);
                int[] levels = t._decisions.Levels[plane];
                int stride = t._frame.Strides[plane];
                for (int r = 0; r < h; r++)
                {
                    data.AsSpan(((y + r) * stride) + x, w).CopyTo(_pixels[plane].AsSpan(r * w));
                    levels.AsSpan(((y + r) * stride) + x, w).CopyTo(_levels[plane].AsSpan(r * w));
                }
            }
        }

        /// <summary>Puts back the contexts alone.</summary>
        internal void RestoreContexts(Av1TileEncoder t)
        {
            for (int plane = 0; plane < t._planes; plane++)
            {
                RestorePlaneContexts(t, plane);
            }

            RestoreBlockContexts(t);
        }

        /// <summary>Puts back everything saved; with <paramref name="lumaOnly"/> or <paramref name="chromaOnly"/>, those planes' samples, levels and contexts.</summary>
        internal void Restore(Av1TileEncoder t, bool lumaOnly = false, bool chromaOnly = false)
        {
            for (int plane = 0; plane < t._planes; plane++)
            {
                if ((lumaOnly && plane > 0) || (chromaOnly && plane == 0))
                {
                    continue;
                }

                RestorePlaneContexts(t, plane);
                if (_pixelsSaved)
                {
                    (int x, int y, int w, int h) = _areas[plane];
                    ushort[] data = t._frame.Plane<ushort>(plane);
                    int[] levels = t._decisions.Levels[plane];
                    int stride = t._frame.Strides[plane];
                    for (int r = 0; r < h; r++)
                    {
                        _pixels[plane].AsSpan(r * w, w).CopyTo(data.AsSpan(((y + r) * stride) + x));
                        _levels[plane].AsSpan(r * w, w).CopyTo(levels.AsSpan(((y + r) * stride) + x));
                    }
                }
            }

            if (chromaOnly)
            {
                return;
            }

            for (int r = 0; r < _rows; r++)
            {
                int at = ((_miRow + r) * t._miCols) + _miCol;
                if (!lumaOnly)
                {
                    _grid.AsSpan(r * _bw, _cols).CopyTo(t._frame.Grid.AsSpan(at));
                    _segments.AsSpan(r * _bw, _cols).CopyTo(t._frame.SegmentMap.AsSpan(at));
                }

                _txTypes.AsSpan(r * _bw, _cols).CopyTo(t._frame.TxTypeMap.AsSpan(at));
                _txSizes.AsSpan(r * _bw, _cols).CopyTo(t._frame.TxSizeMap.AsSpan(at));
            }

            if (!lumaOnly)
            {
                RestoreBlockContexts(t);
            }
        }

        private void RestorePlaneContexts(Av1TileEncoder t, int plane)
        {
            (int lo, int count) = _aboveRanges[plane];
            _above[plane].AsSpan(0, count).CopyTo(t._aboveEntropy[plane].AsSpan(lo));
            _left[plane].CopyTo(t._leftEntropy[plane], 0);
        }

        private void RestoreBlockContexts(Av1TileEncoder t)
        {
            _abovePartition.AsSpan(0, _bw).CopyTo(t._abovePartition.AsSpan(_miCol - t.MiColStart));
            _aboveTxfm.AsSpan(0, _bw).CopyTo(t._aboveTxfm.AsSpan(_miCol - t.MiColStart));
            _leftPartition.CopyTo(t._leftPartition, 0);
            _leftTxfm.CopyTo(t._leftTxfm, 0);
            t._currentQIndex = _currentQIndex;
        }

        /// <summary>Gets a square area's samples in a plane, chroma widened to the pair of luma blocks a 4x4 chroma block covers.</summary>
        private static (int X, int Y, int W, int H) Area(Av1TileEncoder t, int plane, int miRow, int miCol, int bw, int bh)
        {
            int ssx = plane > 0 ? t._ssx : 0, ssy = plane > 0 ? t._ssy : 0;
            int x0 = miCol * 4, y0 = miRow * 4, x1 = (miCol + bw) * 4, y1 = (miRow + bh) * 4;
            if (ssx != 0)
            {
                x0 &= ~7;
                x1 = (x1 + 7) & ~7;
            }

            if (ssy != 0)
            {
                y0 &= ~7;
                y1 = (y1 + 7) & ~7;
            }

            return (x0 >> ssx, y0 >> ssy, (x1 - x0) >> ssx, (y1 - y0) >> ssy);
        }
    }
}

/// <summary>What the search tries at an effort level.</summary>
internal sealed class Av1SearchProfile
{
    private static readonly int[] AllModes = [Pred.Dc, Pred.V, Pred.H, Pred.D45, Pred.D135, Pred.D113, Pred.D157, Pred.D203, Pred.D67, Pred.Smooth, Pred.SmoothV, Pred.SmoothH, Pred.Paeth];

    /// <summary>Gets the largest block side the partition keeps whole.</summary>
    internal int MaxBlock { get; init; } = 64;

    /// <summary>Gets the smallest block side the partition splits.</summary>
    internal int MinBlock { get; init; } = 4;

    internal bool Rectangles { get; init; }

    internal bool ExtendedPartitions { get; init; }

    internal int[] LumaModes { get; init; } = AllModes;

    internal int[] ChromaModes { get; init; } = AllModes;

    internal bool AngleDeltas { get; init; }

    internal bool FilterIntra { get; init; }

    internal bool Cfl { get; init; }

    internal int TxDepth { get; init; }

    /// <summary>Gets whether quantization is rate-distortion optimized (<see cref="Av1Trellis"/>).</summary>
    internal bool Trellis { get; init; }

    /// <summary>Gets whether every candidate is optimized by the trellis; else only the chosen modes are, as libaom's winner mode processing does.</summary>
    internal bool TrellisInSearch { get; init; }

    /// <summary>Gets whether every luma mode tried also tries every transform type; else only the chosen mode does.</summary>
    internal bool TypesInModeSearch { get; init; }

    /// <summary>Gets whether chroma from luma tries every alpha; else three place the error's minimum and its neighbours settle it.</summary>
    internal bool FullCflSearch { get; init; }

    /// <summary>Gets how many intra block copy vectors, ranked by their prediction alone, are coded in full.</summary>
    internal int IntrabcCandidates { get; init; } = 1;

    /// <summary>
    /// Gets whether distortion is weighted by the quantization matrices, as tune IQ's QM-PSNR weighs it. Off at every effort:
    /// on the photo set it cost about 1% at equal SSIMULACRA2.
    /// </summary>
    internal bool QmDistortion { get; init; }

    /// <summary>Gets which palette sizes are tried, from the largest down: 0 the largest alone, 1 every other, 2 all.</summary>
    internal int PaletteSizes { get; init; }

    /// <summary>Gets how many luma modes, ranked by their prediction alone, are coded in full; 0 for all.</summary>
    internal int LumaShortlist { get; init; }

    /// <summary>Gets how many chroma modes, ranked by their prediction alone, are coded in full; 0 for all.</summary>
    internal int ChromaShortlist { get; init; }

    /// <summary>Gets whether partitions are pruned: no split of a whole block without residual, no rectangles where split lost.</summary>
    internal bool PrunePartitions { get; init; }

    internal int[] TxTypes { get; init; } = [TxType.DctDct];

    /// <summary>Gets how far the whole-sample motion search reaches from its start, in samples.</summary>
    internal int SearchRange { get; init; } = 32;

    /// <summary>Gets how many reference frames an inter block searches, nearest first.</summary>
    internal int InterReferences { get; init; } = 7;

    /// <summary>Gets how many inter choices, ranked by their luma prediction and mode rate, are coded in full.</summary>
    internal int InterCandidates { get; init; } = 2;

    /// <summary>Gets which compound predictions are tried: 0 none, 1 averaged, 2 distance-weighted too, 3 wedge and difference-weighted too.</summary>
    internal int Compound { get; init; }

    /// <summary>Gets whether inter-intra prediction is tried.</summary>
    internal bool InterIntra { get; init; }

    /// <summary>Gets whether overlapped and local warped motion are tried.</summary>
    internal bool MotionModes { get; init; }

    /// <summary>Gets which interpolation filters are tried: 0 regular, 1 the three filters, 2 every pair of them (dual filters).</summary>
    internal int Filters { get; init; }

    /// <summary>Gets whether an inter block also tries its transform split once.</summary>
    internal bool InterTxSplit { get; init; }

    /// <summary>Gets whether each reference's global motion is estimated.</summary>
    internal bool GlobalMotion { get; init; }

    /// <summary>Gets whether an inter block whose prediction needs no residual still searches the intra modes.</summary>
    internal bool IntraAfterSkip { get; init; }

    /// <summary>Gets a buffer for the transform types of one size.</summary>
    internal int[] TxTypeBuffer { get; } = new int[16];

    /// <summary>Gets the search of an effort, 0 to 10.</summary>
    internal static Av1SearchProfile For(int effort, bool lossless)
    {
        int[] fast = [Pred.Dc, Pred.V, Pred.H, Pred.Smooth, Pred.Paeth];
        int[] twoD = [TxType.DctDct, TxType.AdstDct, TxType.DctAdst, TxType.AdstAdst];
        return new Av1SearchProfile
        {
            // libaom's speed 6 keeps no block larger than 32x32 whole.
            MaxBlock = effort <= 4 ? 32 : 64,
            MinBlock = effort <= 1 ? 8 : 4,
            Rectangles = effort >= 5,
            ExtendedPartitions = effort >= 7,
            LumaModes = effort <= 2 ? fast : AllModes,
            ChromaModes = effort <= 2 ? [Pred.Dc, Pred.Paeth] : effort <= 5 ? fast : AllModes,
            AngleDeltas = effort >= 5 && !lossless,
            FilterIntra = effort >= 6,
            Cfl = effort >= 2,
            TxDepth = effort <= 4 ? 0 : effort <= 5 ? 1 : 2,
            LumaShortlist = effort <= 2 ? 2 : effort <= 4 ? 3 : effort <= 6 ? 5 : effort <= 8 ? 8 : 0,
            ChromaShortlist = effort <= 4 ? 2 : effort <= 6 ? 3 : effort <= 8 ? 5 : 0,
            PrunePartitions = effort <= 6,
            TypesInModeSearch = effort >= 8,
            Trellis = effort >= 1,
            FullCflSearch = effort >= 7,
            TrellisInSearch = effort >= 7,
            IntrabcCandidates = effort <= 3 ? 1 : effort <= 6 ? 2 : 4,
            PaletteSizes = effort <= 1 ? 0 : effort <= 5 ? 1 : 2,
            TxTypes = effort <= 2 ? [TxType.DctDct] : effort <= 5 ? twoD : [.. Enumerable.Range(0, 16)],
            SearchRange = effort <= 2 ? 16 : effort <= 4 ? 32 : effort <= 6 ? 64 : 96,
            InterReferences = effort <= 1 ? 1 : effort <= 3 ? 3 : 7,
            InterCandidates = effort <= 2 ? 1 : effort <= 4 ? 2 : effort <= 6 ? 3 : 5,
            Compound = effort <= 2 ? 0 : effort <= 4 ? 1 : effort <= 5 ? 2 : 3,
            InterIntra = effort >= 6,
            MotionModes = effort >= 6,
            Filters = effort <= 4 ? 0 : effort <= 6 ? 1 : 2,
            InterTxSplit = effort >= 5,
            GlobalMotion = effort >= 7,
            IntraAfterSkip = effort >= 8,
        };

    }
}
