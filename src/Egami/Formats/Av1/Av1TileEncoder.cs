namespace Egami.Formats.Av1;

/// <summary>
/// Decides and reconstructs one tile of a key frame by rate-distortion search: for each superblock the partition tree, each
/// block's luma and chroma modes, transform sizes and types, and each transform block's coefficients, every choice weighed as
/// distortion (the sum of squared errors) plus lambda times its rate (<see cref="Av1Rate"/>), the rate counted by running the
/// writer's own syntax (<see cref="Av1TileCoder"/>) against the tile's CDFs. Every transform block is predicted and reconstructed
/// with the decoder's own <see cref="Av1Reconstructor"/> as it is tried, so later blocks predict from exactly the samples a
/// decoder will have. A chosen superblock's symbols then adapt the CDFs, as writing them will, so the rates of the next stay true.
/// </summary>
internal sealed class Av1TileEncoder : Av1TileCoder
{
    private readonly Av1Reconstructor _reconstructor;
    private readonly Av1EncoderSource _source;
    private readonly Av1SearchProfile _profile;
    private readonly long _rdmult;
    private readonly int[] _residual = new int[64 * 64];
    private readonly int[] _transformed = new int[64 * 64];
    private readonly int[] _quantized = new int[32 * 32];
    private readonly int[] _coeffs = new int[32 * 32];
    private readonly ushort[] _predicted = new ushort[64 * 64];
    private readonly Stack<Snapshot> _pool = new();

    /// <summary>Starts a tile.</summary>
    internal Av1TileEncoder(Av1Frame frame, int tileRow, int tileCol, Av1EncoderSource source, Av1FrameDecisions decisions, Av1EncoderSettings settings)
        : base(frame, tileRow, tileCol, decisions)
    {
        _reconstructor = new Av1Reconstructor(frame);
        _source = source;
        _profile = Av1SearchProfile.For(settings.Effort, frame.Header.CodedLossless);
        _rdmult = Av1Rate.RdMultiplier(Math.Max(frame.Header.BaseQIdx, 1), frame.Sequence.BitDepth);
    }

    /// <summary>Gets the rate the chosen superblocks cost as their symbols adapted the CDFs, in 1/512 bits.</summary>
    internal long Rate { get; private set; }

    /// <summary>Gets the rate the search estimated for the chosen superblocks, from the CDFs as each superblock began, in 1/512 bits.</summary>
    internal long EstimatedRate { get; private set; }

    /// <summary>Decides and reconstructs the tile.</summary>
    internal void Encode()
    {
        for (int miRow = MiRowStart; miRow < MiRowEnd; miRow += _sbMi)
        {
            _frame.CancellationToken.ThrowIfCancellationRequested();
            ResetLeftContext();
            for (int miCol = MiColStart; miCol < MiColEnd; miCol += _sbMi)
            {
                Snapshot start = Take(miRow, miCol, _frame.SbSize, pixels: false);
                SearchPartition(miRow, miCol, _frame.SbSize, long.MaxValue);

                // Code the chosen superblock from its starting contexts: first as the search saw it, then adapting the CDFs as
                // the writer will.
                start.RestoreContexts(this);
                Av1CostSink estimate = new(false);
                CodePartition(ref estimate, miRow, miCol, _frame.SbSize);
                EstimatedRate += estimate.Cost;
                start.RestoreContexts(this);
                Av1CostSink adapt = new(true);
                CodePartition(ref adapt, miRow, miCol, _frame.SbSize);
                Rate += adapt.Cost;
                Give(start);
            }
        }
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
        long bestCost = long.MaxValue;
        bool first = true;
        bool lastIsBest = false;
        long noneCost = long.MaxValue, splitCost = long.MaxValue;
        foreach (int partition in Candidates(bsize, hasRows, hasCols))
        {
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
            long cost = PartitionCost(miRow, miCol, bsize, hasRows, hasCols, partition);
            cost = cost >= limit ? long.MaxValue : Add(cost, EncodePartitionChoice(miRow, miCol, bsize, partition, hasRows, hasCols, limit - cost));
            if (partition == Partition.None)
            {
                noneCost = cost;
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
        Av1BlockInfo b = new() { Size = bsize, Partition = partition, MiRow = miRow, MiCol = miCol, QIndex = _currentQIndex };
        for (int y = 0; y < yMis; y++)
        {
            _frame.Grid.AsSpan(((miRow + y) * _miCols) + miCol, xMis).Fill(b);
        }

        _b = b;
        SetBlockPosition(miRow, miCol, bw, bh);
        bool lossless = _header.LosslessArray[b.SegmentId];
        Snapshot start = Take(miRow, miCol, bsize, pixels: true);
        bool chroma = _planes > 1 && _isChromaRef;
        bool cfl = chroma && CflAllowed();

        // Luma: the modes whose prediction alone costs least, coded in full at the largest transform; then the best of them at
        // each smaller transform depth. Chroma's own symbols count at DC.
        LumaChoice bestLuma = default;
        long bestLumaCost = long.MaxValue;
        foreach (LumaChoice candidate in Shortlist(LumaCandidates(bsize, lossless), _profile.LumaShortlist, c => LumaPredictionCost(c, cfl, start)))
        {
            start.Restore(this, lumaOnly: true);
            long cost = Luma(candidate, cfl, Math.Min(budget, bestLumaCost));
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
            long cost = Luma(candidate, cfl, Math.Min(budget, bestLumaCost));
            if (cost < bestLumaCost)
            {
                (bestLumaCost, bestLuma) = (cost, candidate);
            }
        }

        if (bestLumaCost == long.MaxValue)
        {
            start.Restore(this);
            Give(start);
            return long.MaxValue;
        }

        start.Restore(this, lumaOnly: true);
        Luma(bestLuma, cfl, long.MaxValue);
        if (chroma)
        {
            Snapshot afterLuma = Take(miRow, miCol, bsize, pixels: true);
            ChromaChoice bestChroma = new(Pred.Dc, 0, 0, 0);
            long bestChromaCost = long.MaxValue;
            foreach (ChromaChoice candidate in Shortlist(ChromaCandidates(bsize, cfl), _profile.ChromaShortlist, c => ChromaPredictionCost(c, afterLuma)))
            {
                afterLuma.Restore(this, chromaOnly: true);
                long cost = Chroma(candidate);
                if (cost < bestChromaCost)
                {
                    (bestChromaCost, bestChroma) = (cost, candidate);
                }
            }

            afterLuma.Restore(this, chromaOnly: true);
            Chroma(bestChroma);
            Give(afterLuma);
        }

        // The block's exact rate: its symbols as the writer codes them, from its starting contexts.
        b.Skip = !AnyCoefficient(bsize);
        start.RestoreContexts(this);
        Span<bool> cdef = stackalloc bool[4];
        _cdefTransmitted.CopyTo(cdef);
        Av1CostSink rate = new(false);
        CodeBlock(ref rate, miRow, miCol, bsize);
        cdef.CopyTo(_cdefTransmitted);
        Give(start);
        long distortion = Distortion(0, bsize) + (chroma ? Distortion(1, bsize) + Distortion(2, bsize) : 0);
        return Av1Rate.RdCost(_rdmult, rate.Cost, distortion);
    }

    /// <summary>Keeps the <paramref name="count"/> candidates of lowest estimated cost, in order; all of them when count is 0.</summary>
    private static List<T> Shortlist<T>(List<T> candidates, int count, Func<T, long> estimate)
    {
        if (count == 0 || candidates.Count <= count)
        {
            return candidates;
        }

        List<(long Cost, int Index)> scored = [];
        for (int i = 0; i < candidates.Count; i++)
        {
            scored.Add((estimate(candidates[i]), i));
        }

        scored.Sort();
        return [.. scored.Take(count).Select(s => candidates[s.Index])];
    }

    /// <summary>
    /// Estimates a luma choice from its prediction alone: the squared prediction error plus its mode symbols, each transform
    /// block predicted with the source standing in for the reconstruction before it.
    /// </summary>
    private long LumaPredictionCost(LumaChoice choice, bool cfl, Snapshot start)
    {
        Av1BlockInfo b = _b;
        b.YMode = choice.Mode;
        b.AngleDeltaY = choice.AngleDelta;
        b.UseFilterIntra = choice.FilterMode >= 0;
        b.FilterIntraMode = Math.Max(choice.FilterMode, 0);
        b.UvMode = Pred.Dc;
        b.TxSize = _header.LosslessArray[b.SegmentId] ? Tx.T4x4 : Av1Tables.MaxTxSizeRectLookup[b.Size];
        long rate = ModeRate();
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
        long rate = ModeRate();
        long error = PredictionErrorWithSource(1, b.Size) + PredictionErrorWithSource(2, b.Size);
        afterLuma.Restore(this, chromaOnly: true);
        return Av1Rate.RdCost(_rdmult, rate, error);
    }

    /// <summary>Gets a plane's squared prediction error over the current block, copying the source in after each transform block.</summary>
    private long PredictionErrorWithSource(int plane, int bsize)
    {
        long error = 0;
        ushort[] frame = _frame.Planes![plane];
        ushort[] source = _source.Planes[plane];
        int stride = _frame.Strides[plane];
        ForEachTransformBlock(plane, bsize, (_, _, txSize, x, y) =>
        {
            _reconstructor.PredictIntraBlock(this, plane, x, y, txSize);
            error += Error(plane, x, y, txSize);
            int w = Av1Tables.TxSizeWide[txSize];
            for (int r = 0; r < Av1Tables.TxSizeHigh[txSize]; r++)
            {
                int at = ((y + r) * stride) + x;
                source.AsSpan(at, w).CopyTo(frame.AsSpan(at));
            }
        });
        return error;
    }

    /// <summary>A luma choice: the mode, its angle delta, the filter intra mode (-1 for none) and the transform depth.</summary>
    private readonly record struct LumaChoice(int Mode, int AngleDelta, int FilterMode, int Depth);

    /// <summary>A chroma choice: the mode (<see cref="Pred.UvCfl"/> included), its angle delta and the chroma from luma alphas.</summary>
    private readonly record struct ChromaChoice(int Mode, int AngleDelta, int CflSigns, int CflIdx);

    private List<LumaChoice> LumaCandidates(int bsize, bool lossless)
    {
        List<LumaChoice> list = [];
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
        List<ChromaChoice> list = [];
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
    private long Luma(LumaChoice choice, bool cfl, long budget)
    {
        Av1BlockInfo b = _b;
        b.YMode = choice.Mode;
        b.AngleDeltaY = choice.AngleDelta;
        b.UseFilterIntra = choice.FilterMode >= 0;
        b.FilterIntraMode = Math.Max(choice.FilterMode, 0);

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
        long cost = Av1Rate.RdCost(_rdmult, ModeRate(), 0);
        b.UvMode = uv;
        return cost >= budget ? long.MaxValue : Add(cost, EncodePlane(0, b.Size, Left(budget, cost)));
    }

    /// <summary>Encodes the block's chroma with a choice; returns chroma's rate-distortion cost with the block's mode symbols.</summary>
    private long Chroma(ChromaChoice choice)
    {
        Av1BlockInfo b = _b;
        b.UvMode = choice.Mode;
        b.AngleDeltaUv = choice.AngleDelta;
        b.CflAlphaSigns = choice.CflSigns;
        b.CflAlphaIdx = choice.CflIdx;
        long cost = Av1Rate.RdCost(_rdmult, ModeRate(), 0);
        cost = Add(cost, EncodePlane(1, b.Size, long.MaxValue));
        return Add(cost, EncodePlane(2, b.Size, long.MaxValue));
    }

    /// <summary>
    /// Gets the chroma from luma alphas whose prediction alone is closest to the source, each chroma plane searched over every
    /// sign and magnitude; DC when neither plane wants one.
    /// </summary>
    private ChromaChoice CflAlphas(int bsize)
    {
        Av1BlockInfo b = _b;
        b.UvMode = Pred.UvCfl;
        Span<int> sign = stackalloc int[3];
        Span<int> magnitude = stackalloc int[3];
        for (int plane = 1; plane <= 2; plane++)
        {
            long bestError = long.MaxValue;
            for (int alpha = -16; alpha <= 16; alpha++)
            {
                int s = alpha == 0 ? 0 : alpha < 0 ? 1 : 2;
                int m = alpha == 0 ? 0 : Math.Abs(alpha) - 1;

                // The other plane's sign is set nonzero, so the joint sign is valid whatever this plane's is.
                int signU = plane == 1 ? s : 2, signV = plane == 2 ? s : 2;
                b.CflAlphaSigns = (3 * signU) + signV - 1;
                b.CflAlphaIdx = plane == 1 ? m << 4 : m;
                long error = PredictionError(plane, bsize);
                if (error < bestError)
                {
                    (bestError, sign[plane], magnitude[plane]) = (error, s, m);
                }
            }
        }

        b.UvMode = Pred.Dc;
        return sign[1] == 0 && sign[2] == 0
            ? new ChromaChoice(Pred.Dc, 0, 0, 0)
            : new ChromaChoice(Pred.UvCfl, 0, (3 * sign[1]) + sign[2] - 1, (magnitude[1] << 4) | magnitude[2]);
    }

    /// <summary>Gets the squared error of the current block's prediction alone in a plane, each transform block predicted from the frame as it stands.</summary>
    private long PredictionError(int plane, int bsize)
    {
        long error = 0;
        ForEachTransformBlock(plane, bsize, (blkRow, blkCol, txSize, x, y) =>
        {
            _reconstructor.PredictIntraBlock(this, plane, x, y, txSize);
            error += Error(plane, x, y, txSize);
        });
        return error;
    }

    /// <summary>
    /// Encodes every transform block of the current block in one plane, in the decoder's order, each with the transform type of
    /// lowest cost; returns the plane's coefficient rate-distortion cost, or <see cref="long.MaxValue"/> past the budget.
    /// </summary>
    private long EncodePlane(int plane, int bsize, long budget)
    {
        long cost = 0;
        ForEachTransformBlock(plane, bsize, (blkRow, blkCol, txSize, x, y) =>
        {
            if (cost != long.MaxValue)
            {
                cost = Add(cost, TransformBlock(plane, blkRow, blkCol, txSize, x, y));
                if (cost > budget)
                {
                    cost = long.MaxValue;
                }
            }
        });
        return cost;
    }

    private delegate void TransformBlockAction(int blkRow, int blkCol, int txSize, int x, int y);

    /// <summary>Visits a plane's transform blocks of the current block in the decoder's order (in 64x64 units, then rows, then columns).</summary>
    private void ForEachTransformBlock(int plane, int bsize, TransformBlockAction action)
    {
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
                        action(blkRow, blkCol, txSize, (col4 + blkCol) * 4, (row4 + blkRow) * 4);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Predicts one transform block, then for each transform type the effort allows transforms, quantizes and measures it, and
    /// keeps the type of lowest cost: its levels in the decisions, its reconstruction in the frame, its contexts set. Returns its
    /// cost: the coefficient rate (all-zero flag, type, levels) and the distortion inside the frame.
    /// </summary>
    private long TransformBlock(int plane, int blkRow, int blkCol, int txSize, int x, int y)
    {
        Av1BlockInfo b = _b;
        _reconstructor.PredictIntraBlock(this, plane, x, y, txSize);
        int w = Av1Tables.TxSizeWide[txSize], h = Av1Tables.TxSizeHigh[txSize];
        ushort[] frame = _frame.Planes![plane];
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
        ReadOnlySpan<int> types = plane == 0 && !lossless ? TxTypes(txSize) : [plane == 0 ? TxType.DctDct : GetTxType(1, blkRow, blkCol, txSize)];
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
        CodeCoefficients(ref rate, plane, blkRow, blkCol, txSize, x, y);
        _reconstructor.FinishIntraBlock(this, plane, x, y, txSize, eob == 0 ? TxType.DctDct : txType, eob, _coeffs);
        Array.Clear(_coeffs, 0, area);
        long distortion = Error(plane, x, y, txSize);
        if (!keep)
        {
            ushort[] frame = _frame.Planes![plane];
            int stride = _frame.Strides[plane];
            for (int r = 0; r < h; r++)
            {
                _predicted.AsSpan(r * w, w).CopyTo(frame.AsSpan(((y + r) * stride) + x));
            }
        }

        return Av1Rate.RdCost(_rdmult, rate.Cost, distortion);
    }

    /// <summary>Gets the transform types to try for a luma transform size: those of its set the effort allows.</summary>
    private ReadOnlySpan<int> TxTypes(int txSize)
    {
        int setType = Av1Tables.ExtTxSetType(txSize, false, _header.ReducedTxSet);
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

    /// <summary>Gets the distortion of a transform block's part inside the image: 16 times its squared error, at 8-bit scale.</summary>
    private long Error(int plane, int x, int y, int txSize)
    {
        int w = Math.Min(Av1Tables.TxSizeWide[txSize], _source.Widths[plane] - x);
        int h = Math.Min(Av1Tables.TxSizeHigh[txSize], _source.Heights[plane] - y);
        ushort[] frame = _frame.Planes![plane];
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
        ForEachTransformBlock(plane, bsize, (_, _, txSize, x, y) => error += Error(plane, x, y, txSize));
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
            ForEachTransformBlock(plane, bsize, (_, _, txSize, x, y) =>
            {
                int adjusted = Av1Tables.AdjustedTxSize(txSize);
                for (int r = 0; r < Av1Tables.TxSizeHigh[adjusted] && !any; r++)
                {
                    any = levels.AsSpan(((y + r) * stride) + x, Av1Tables.TxSizeWide[adjusted]).IndexOfAnyExcept(0) >= 0;
                }
            });
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
        CodeIntraFrameModeInfo(ref rate);
        CodeTxSize(ref rate, _b.Size, false, true, _header.LosslessArray[_b.SegmentId]);
        _currentQIndex = savedQIndex;
        deltaLf.CopyTo(_deltaLf);
        cdef.CopyTo(_cdefTransmitted);
        return rate.Cost;
    }

    /// <summary>
    /// Quantizes the transformed block into levels (a dead zone: a third of a step rounds up for AC, half a step for DC), and
    /// writes their dequantized values, as the decoder makes them, into the coefficients; returns the end of block in scan order.
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
        int qmLevel = _header.UsingQmatrix && !lossless ? (plane == 0 ? _header.QmY : plane == 1 ? _header.QmU : _header.QmV) : 15;
        int qmOffset = qmLevel < 15 && txType < TxType.Idtx
            ? (((qmLevel * 2) + (plane > 0 ? 1 : 0)) * 3344) + Av1Tables.QmOffset(Av1Tables.AdjustedTxSize(txSize)) : -1;
        byte[] matrices = Av1Tables.InverseQuantizerMatrices;
        Array.Clear(_quantized, 0, area);
        int eob = 0;
        for (int c = 0; c < area; c++)
        {
            int pos = scan[c];
            int value = _transformed[pos];
            int dqv = pos == 0 ? dcQ : acQ;
            if (qmOffset >= 0)
            {
                // The decoder's weighted step: quantizing by it keeps each frequency's error in its step.
                dqv = ((matrices[qmOffset + pos] * dqv) + 16) >> 5;
            }

            int level;
            if (lossless)
            {
                level = value / dqv;
            }
            else
            {
                long abs = (long)Math.Abs(value) << shift;
                long rounding = pos == 0 ? dqv >> 1 : (dqv * 21) >> 6;
                level = (int)Math.Min((abs + rounding) / dqv, (1 << 20) - 1);
                level = value < 0 ? -level : level;
            }

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
                ushort[] data = t._frame.Planes![plane];
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
                    ushort[] data = t._frame.Planes![plane];
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

    /// <summary>Gets how many luma modes, ranked by their prediction alone, are coded in full; 0 for all.</summary>
    internal int LumaShortlist { get; init; }

    /// <summary>Gets how many chroma modes, ranked by their prediction alone, are coded in full; 0 for all.</summary>
    internal int ChromaShortlist { get; init; }

    /// <summary>Gets whether partitions are pruned: no split of a whole block without residual, no rectangles where split lost.</summary>
    internal bool PrunePartitions { get; init; }

    internal int[] TxTypes { get; init; } = [TxType.DctDct];

    /// <summary>Gets a buffer for the transform types of one size.</summary>
    internal int[] TxTypeBuffer { get; } = new int[16];

    /// <summary>Gets the search of an effort, 0 to 10.</summary>
    internal static Av1SearchProfile For(int effort, bool lossless)
    {
        int[] fast = [Pred.Dc, Pred.V, Pred.H, Pred.Smooth, Pred.Paeth];
        int[] twoD = [TxType.DctDct, TxType.AdstDct, TxType.DctAdst, TxType.AdstAdst];
        return new Av1SearchProfile
        {
            MaxBlock = 64,
            MinBlock = effort <= 1 ? 8 : 4,
            Rectangles = effort >= 3,
            ExtendedPartitions = effort >= 7,
            LumaModes = effort <= 2 ? fast : AllModes,
            ChromaModes = effort <= 2 ? [Pred.Dc, Pred.Paeth] : effort <= 5 ? fast : AllModes,
            AngleDeltas = effort >= 5 && !lossless,
            FilterIntra = effort >= 6,
            Cfl = effort >= 2,
            TxDepth = effort <= 2 ? 0 : effort <= 5 ? 1 : 2,
            LumaShortlist = effort <= 2 ? 2 : effort <= 4 ? 3 : effort <= 6 ? 5 : effort <= 8 ? 8 : 0,
            ChromaShortlist = effort <= 4 ? 2 : effort <= 6 ? 3 : effort <= 8 ? 5 : 0,
            PrunePartitions = effort <= 6,
            TxTypes = effort <= 2 ? [TxType.DctDct] : effort <= 5 ? twoD : [.. Enumerable.Range(0, 16)],
        };
    }
}
