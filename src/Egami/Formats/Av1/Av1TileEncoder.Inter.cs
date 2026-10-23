namespace Egami.Formats.Av1;

/// <summary>
/// The inter search of <see cref="Av1TileEncoder"/>, after the structure of libaom's handle_inter_mode and motion search (rdopt.c,
/// mcomp.c, reconinter_enc.c; BSD 2-clause, see THIRD-PARTY-NOTICES): each reference's vector found by a whole-sample pattern search
/// from the reference vector stack, refined to a quarter or an eighth of a sample with the regular filter; the single-reference
/// modes (NEARESTMV, NEARMV, GLOBALMV, NEWMV with each stack index) and the compound pairs ranked by their luma prediction and exact
/// mode rate; the best few coded in full, with and without residual; then the winner's interpolation filters, compound type,
/// overlapped or warped motion, inter-intra and transform split tried in turn, each kept when its full cost is lower.
/// </summary>
internal sealed partial class Av1TileEncoder
{
    private readonly int[] _subpelIntermediate = new int[(64 + 7) * 64];
    private readonly ushort[] _subpelPrediction = new ushort[64 * 64];
    private readonly List<(long Cost, InterChoice Choice)> _interCandidates = [];
    private readonly (int Row, int Col)[] _newMv = new (int, int)[8];
    private readonly (int Row, int Col)[] _seedMv = new (int, int)[8];
    private readonly long[] _singleCost = new long[8];

    /// <summary>The segment the current block is coded in when it has a residual.</summary>
    private int _blockSegment;

    /// <summary>The quantizer the current block is coded at when it has a residual.</summary>
    private int _blockQIndex;

    /// <summary>
    /// An inter choice: the references (<see cref="Ref.None"/> second for one, <see cref="Ref.Intra"/> for inter-intra), the mode
    /// and its stack index, the vectors, the filters, the motion mode, the compound type with its wedge or mask, the inter-intra
    /// mode and wedge, the transform depth, whether the residual is dropped, and skip mode.
    /// </summary>
    private readonly record struct InterChoice(int Ref0, int Ref1, int Mode, int RefMvIdx, int MvRow, int MvCol, int Mv1Row, int Mv1Col)
    {
        internal int FilterY { get; init; }

        internal int FilterX { get; init; }

        internal int MotionMode { get; init; }

        internal int CompoundType { get; init; }

        internal int WedgeIndex { get; init; }

        internal int WedgeSign { get; init; }

        internal int MaskType { get; init; }

        internal int Interintra { get; init; } = -1;

        internal bool WedgeInterintra { get; init; }

        internal int InterintraWedge { get; init; }

        internal int TxDepth { get; init; }

        internal bool Skip { get; init; }

        internal bool SkipMode { get; init; }
    }

    /// <summary>
    /// Searches the current block's inter choices from <paramref name="start"/>; returns the best one's full cost and the choice, or
    /// <see cref="long.MaxValue"/> when no reference is searched. Leaves the block as it started, with its intra defaults.
    /// </summary>
    private (long Cost, InterChoice Choice) SearchInter(int bsize, Snapshot start, long budget, bool chroma)
    {
        int[] refs = _source.SearchReferences;
        (long Cost, InterChoice Choice) best = (long.MaxValue, default);
        if (refs.Length == 0)
        {
            return best;
        }

        _interCandidates.Clear();
        int implied = _header.InterpolationFilter == Interp.Switchable ? Interp.Regular : _header.InterpolationFilter;
        foreach (int rf in refs)
        {
            _singleCost[rf] = long.MaxValue;
            SetReferences(rf, Ref.None);
            _b.YMode = InterMode.New;
            _b.RefMvIdx = 0;
            FindMvRefs();
            int count = _mvCount;
            _newMv[rf] = _seedMv[rf] = MotionSearch(rf, bsize, Vectors(0));
            Rank(new InterChoice(rf, Ref.None, InterMode.Nearest, 0, 0, 0, 0, 0) { FilterY = implied, FilterX = implied });
            for (int idx = 0; idx < 3 && (idx == 0 || count > idx + 1); idx++)
            {
                Rank(new InterChoice(rf, Ref.None, InterMode.Near, idx, 0, 0, 0, 0) { FilterY = implied, FilterX = implied });
            }

            Rank(new InterChoice(rf, Ref.None, InterMode.Global, 0, 0, 0, 0, 0) { FilterY = implied, FilterX = implied });
            for (int idx = 0; idx < 3 && (idx == 0 || count > idx); idx++)
            {
                Rank(new InterChoice(rf, Ref.None, InterMode.New, idx, _newMv[rf].Row, _newMv[rf].Col, 0, 0) { FilterY = implied, FilterX = implied });
            }
        }

        if (_profile.Compound > 0 && _header.ReferenceSelect && CompRefAllowed(bsize))
        {
            foreach ((int r0, int r1) in CompoundPairs(refs))
            {
                InterChoice pair = new(r0, r1, InterMode.NearestNearest, 0, 0, 0, 0, 0) { FilterY = implied, FilterX = implied };
                Rank(pair);
                Rank(pair with { Mode = InterMode.NearNear });
                Rank(pair with { Mode = InterMode.GlobalGlobal });
                Rank(pair with { Mode = InterMode.NewNew, MvRow = _newMv[r0].Row, MvCol = _newMv[r0].Col, Mv1Row = _newMv[r1].Row, Mv1Col = _newMv[r1].Col });
                Rank(pair with { Mode = InterMode.NewNearest, MvRow = _newMv[r0].Row, MvCol = _newMv[r0].Col });
                Rank(pair with { Mode = InterMode.NearestNew, Mv1Row = _newMv[r1].Row, Mv1Col = _newMv[r1].Col });
            }
        }

        if (_header.SkipModePresent && SkipModeAllowed(_b.SegmentId))
        {
            InterChoice skipMode = new(_header.SkipModeFrame[0], _header.SkipModeFrame[1], InterMode.NearestNearest, 0, 0, 0, 0, 0)
            {
                FilterY = implied,
                FilterX = implied,
                Skip = true,
                SkipMode = true,
            };
            Rank(skipMode);
        }

        _interCandidates.Sort((a, c) => a.Cost.CompareTo(c.Cost));
        int full = Math.Min(_interCandidates.Count, _profile.InterCandidates);
        for (int i = 0; i < full; i++)
        {
            InterChoice choice = _interCandidates[i].Choice;
            Try(choice);
            if (!choice.SkipMode)
            {
                Try(choice with { Skip = true });
            }
        }

        if (best.Cost != long.MaxValue && !best.Choice.SkipMode)
        {
            Refine();
        }

        start.Restore(this);
        ResetToIntra();
        return best;

        void Rank(InterChoice choice)
        {
            long cost = ModelCost(choice, start);
            if (cost == long.MaxValue)
            {
                return;
            }

            if (choice.Ref1 == Ref.None && cost < _singleCost[choice.Ref0])
            {
                _singleCost[choice.Ref0] = cost;
            }

            // A choice whose vectors and references another already gives predicts the same: keep the cheaper.
            for (int i = 0; i < _interCandidates.Count; i++)
            {
                InterChoice other = _interCandidates[i].Choice;
                if (other.Ref0 == choice.Ref0 && other.Ref1 == choice.Ref1 && other.MvRow == _b.MvRow && other.MvCol == _b.MvCol
                    && other.Mv1Row == _b.Mv1Row && other.Mv1Col == _b.Mv1Col && other.SkipMode == choice.SkipMode)
                {
                    if (cost < _interCandidates[i].Cost)
                    {
                        _interCandidates[i] = (cost, choice with { MvRow = _b.MvRow, MvCol = _b.MvCol, Mv1Row = _b.Mv1Row, Mv1Col = _b.Mv1Col });
                    }

                    return;
                }
            }

            _interCandidates.Add((cost, choice with { MvRow = _b.MvRow, MvCol = _b.MvCol, Mv1Row = _b.Mv1Row, Mv1Col = _b.Mv1Col }));
        }

        void Try(InterChoice choice)
        {
            long cost = EncodeInter(choice, start, Math.Min(budget, best.Cost), chroma);
            if (cost < best.Cost)
            {
                best = (cost, choice);
            }
        }

        void Refine()
        {
            InterChoice winner = best.Choice;
            bool compound = winner.Ref1 > Ref.Intra;

            // Interpolation filters: the three filters, then every pair of them.
            if (_profile.Filters > 0 && _header.InterpolationFilter == Interp.Switchable && FiltersCoded(winner))
            {
                ReadOnlySpan<int> filters = [Interp.Regular, Interp.Smooth, Interp.Sharp];
                InterChoice baseChoice = best.Choice;
                foreach (int fy in filters)
                {
                    foreach (int fx in filters)
                    {
                        if ((fx != fy && (_profile.Filters < 2 || !_sequence.EnableDualFilter)) || (fx == baseChoice.FilterX && fy == baseChoice.FilterY))
                        {
                            continue;
                        }

                        Try(baseChoice with { FilterY = fy, FilterX = fx, Skip = false });
                        Try(baseChoice with { FilterY = fy, FilterX = fx, Skip = true });
                    }
                }
            }

            if (compound && _profile.Compound >= 2 && _sequence.EnableJntComp)
            {
                InterChoice baseChoice = best.Choice;
                Try(baseChoice with { CompoundType = Compound.Distance, Skip = false });
            }

            if (compound && _profile.Compound >= 3 && _sequence.EnableMaskedCompound)
            {
                InterChoice baseChoice = best.Choice with { Skip = false };
                InterChoice? bestMasked = null;
                long bestModel = long.MaxValue;
                if (WedgeUsed(bsize))
                {
                    for (int index = 0; index < 16; index++)
                    {
                        for (int sign = 0; sign < 2; sign++)
                        {
                            InterChoice wedge = baseChoice with { CompoundType = Compound.Wedge, WedgeIndex = index, WedgeSign = sign };
                            long model = ModelCost(wedge, start);
                            if (model < bestModel)
                            {
                                (bestModel, bestMasked) = (model, wedge);
                            }
                        }
                    }
                }

                for (int mask = 0; mask < 2; mask++)
                {
                    InterChoice diff = baseChoice with { CompoundType = Compound.DiffWeighted, MaskType = mask };
                    long model = ModelCost(diff, start);
                    if (model < bestModel)
                    {
                        (bestModel, bestMasked) = (model, diff);
                    }
                }

                if (bestMasked is InterChoice masked)
                {
                    Try(masked);
                }
            }

            if (!compound && _profile.MotionModes)
            {
                InterChoice baseChoice = best.Choice with { Skip = false };
                int allowed = MotionModesFor(baseChoice);
                if (allowed >= 1)
                {
                    Try(baseChoice with { MotionMode = 1 });
                }

                if (allowed >= 2)
                {
                    Try(baseChoice with { MotionMode = 2 });
                }
            }

            if (!compound && _profile.InterIntra && _sequence.EnableInterintraCompound && bsize >= Bs.B8x8 && bsize <= Bs.B32x32
                && best.Choice is { MotionMode: 0, Mode: >= InterMode.Nearest and <= InterMode.New })
            {
                InterChoice baseChoice = best.Choice with { Skip = false };
                InterChoice? bestII = null;
                long bestModel = long.MaxValue;
                for (int mode = 0; mode < 4; mode++)
                {
                    InterChoice ii = baseChoice with { Ref1 = Ref.Intra, Interintra = mode };
                    long model = ModelCost(ii, start);
                    if (model < bestModel)
                    {
                        (bestModel, bestII) = (model, ii);
                    }

                    if (WedgeUsed(bsize))
                    {
                        for (int index = 0; index < 16; index++)
                        {
                            InterChoice wedge = ii with { WedgeInterintra = true, InterintraWedge = index };
                            model = ModelCost(wedge, start);
                            if (model < bestModel)
                            {
                                (bestModel, bestII) = (model, wedge);
                            }
                        }
                    }
                }

                if (bestII is InterChoice chosen)
                {
                    Try(chosen);
                }
            }

            if (_profile.InterTxSplit && !best.Choice.Skip && !_header.LosslessArray[_b.SegmentId] && _header.TxMode == Av1TxMode.Select
                && Av1Tables.SubTxSizeMap[Av1Tables.MaxTxSizeRectLookup[bsize]] != Av1Tables.MaxTxSizeRectLookup[bsize] && bsize > Bs.B4x4)
            {
                Try(best.Choice with { TxDepth = 1 });
            }
        }
    }

    /// <summary>Lists the compound pairs to try: the references with the cheapest single predictions, each pair the syntax allows.</summary>
    private List<(int R0, int R1)> CompoundPairs(int[] refs)
    {
        List<int> ranked = [.. refs.Where(r => _singleCost[r] != long.MaxValue).OrderBy(r => _singleCost[r]).Take(3)];
        List<(int, int)> pairs = [];
        for (int i = 0; i < ranked.Count; i++)
        {
            for (int j = 0; j < ranked.Count; j++)
            {
                int r0 = ranked[i], r1 = ranked[j];
                bool bidir = !IsBackward(r0) && IsBackward(r1);
                bool unidir = (r0, r1) is (Ref.Bwd, Ref.Alt) or (Ref.Last, Ref.Last2) or (Ref.Last, Ref.Last3) or (Ref.Last, Ref.Golden);
                if ((bidir || unidir) && !pairs.Contains((r0, r1)))
                {
                    pairs.Add((r0, r1));
                }
            }
        }

        return pairs;
    }

    /// <summary>Points the current block at one or two references, its compound and motion fields at their defaults.</summary>
    private void SetReferences(int r0, int r1)
    {
        Av1BlockInfo b = _b;
        b.RefFrame0 = r0;
        b.RefFrame1 = r1;
        b.SkipMode = false;
        b.MotionMode = 0;
        b.CompGroupIdx = 0;
        b.CompoundIdx = 1;
        b.CompoundType = Compound.Average;
        b.InterintraMode = 0;
        b.UseWedgeInterintra = false;
    }

    /// <summary>Makes the current block an intra block again: no references and none of the inter fields.</summary>
    private void ResetToIntra()
    {
        SetReferences(Ref.Intra, Ref.None);
        Av1BlockInfo b = _b;
        (b.MvRow, b.MvCol, b.Mv1Row, b.Mv1Col) = (0, 0, 0, 0);
        b.RefMvIdx = 0;
        b.FilterX = b.FilterY = 0;
        b.WarpParams = null;
        b.WarpValid = false;
        b.Skip = false;
        b.YMode = Pred.Dc;
        b.UvMode = Pred.Dc;
        b.QIndex = _blockQIndex;
        b.SegmentId = _blockSegment;
        if (_header.SegmentationEnabled)
        {
            FillSegment(_frame.SegmentMap, _blockSegment, Math.Min(_bw, _miCols - _miCol), Math.Min(_bh, _frame.MiRows - _miRow));
        }
    }

    /// <summary>
    /// Sets the current block to a choice: its references and mode, the vectors the mode derives (or the new ones), its prediction
    /// tools and its transform size; returns false when the choice is not one the block can code.
    /// </summary>
    private bool Apply(InterChoice c)
    {
        Av1BlockInfo b = _b;
        SetReferences(c.Ref0, c.Ref1 == Ref.Intra ? Ref.None : c.Ref1);
        b.SkipMode = c.SkipMode;
        b.UseIntrabc = false;
        b.YMode = c.Mode;
        b.RefMvIdx = c.RefMvIdx;
        b.UvMode = Pred.Dc;
        b.AngleDeltaY = b.AngleDeltaUv = 0;
        b.UseFilterIntra = false;
        b.CflAlphaSigns = b.CflAlphaIdx = 0;
        b.PaletteSize[0] = b.PaletteSize[1] = 0;
        FindMvRefs();
        if (c.RefMvIdx > 0 && (c.Mode is InterMode.New or InterMode.NewNew ? _mvCount <= c.RefMvIdx : _mvCount <= c.RefMvIdx + 1))
        {
            return false;
        }

        BlockVectors v = Vectors(c.RefMvIdx);
        (b.MvRow, b.MvCol, b.Mv1Row, b.Mv1Col) = c.Mode switch
        {
            InterMode.Nearest => (v.NearestRow0, v.NearestCol0, 0, 0),
            InterMode.Near => (v.NearRow0, v.NearCol0, 0, 0),
            InterMode.Global => (_gmRow0, _gmCol0, 0, 0),
            InterMode.NearestNearest => (v.NearestRow0, v.NearestCol0, v.NearestRow1, v.NearestCol1),
            InterMode.NearNear => (v.NearRow0, v.NearCol0, v.NearRow1, v.NearCol1),
            InterMode.NewNearest => (c.MvRow, c.MvCol, v.NearestRow1, v.NearestCol1),
            InterMode.NearestNew => (v.NearestRow0, v.NearestCol0, c.Mv1Row, c.Mv1Col),
            InterMode.GlobalGlobal => (_gmRow0, _gmCol0, _gmRow1, _gmCol1),
            _ => (c.MvRow, c.MvCol, c.Mv1Row, c.Mv1Col),
        };
        if (!ValidMv(b.MvRow, b.MvCol) || (b.HasSecondRef && !ValidMv(b.Mv1Row, b.Mv1Col)))
        {
            return false;
        }

        if (InterMode.First(c.Mode) == InterMode.New && !Codable(b.MvRow - v.RefRow0, b.MvCol - v.RefCol0))
        {
            return false;
        }

        if (b.HasSecondRef && InterMode.Second(c.Mode) == InterMode.New && !Codable(b.Mv1Row - v.RefRow1, b.Mv1Col - v.RefCol1))
        {
            return false;
        }

        b.FilterY = c.FilterY;
        b.FilterX = c.FilterX;
        if (b.HasSecondRef && !c.SkipMode)
        {
            b.CompoundType = c.CompoundType;
            b.CompoundIdx = c.CompoundType == Compound.Distance ? 0 : 1;
            b.CompGroupIdx = c.CompoundType >= Compound.Wedge ? 1 : 0;
            b.WedgeIndex = c.WedgeIndex;
            b.WedgeSign = c.WedgeSign;
            b.MaskType = c.MaskType;
        }

        if (c.Interintra >= 0)
        {
            b.RefFrame1 = Ref.Intra;
            b.InterintraMode = c.Interintra;
            b.UseWedgeInterintra = c.WedgeInterintra;
            b.InterintraWedgeIndex = c.InterintraWedge;
        }

        b.MotionMode = c.MotionMode;
        if (c.MotionMode == 2)
        {
            SetLocalWarp(FindSamples());
            if (!b.WarpValid)
            {
                return false;
            }
        }

        if (!InterpFilterCoded(out int implied))
        {
            b.FilterY = b.FilterX = implied;
        }

        bool lossless = _header.LosslessArray[b.SegmentId];
        int tx = lossless ? Tx.T4x4 : Av1Tables.MaxTxSizeRectLookup[b.Size];
        for (int d = 0; d < c.TxDepth && !lossless; d++)
        {
            tx = Av1Tables.SubTxSizeMap[tx];
        }

        b.TxSize = tx;
        int xMis = Math.Min(_bw, _miCols - _miCol), yMis = Math.Min(_bh, _frame.MiRows - _miRow);
        for (int y = 0; y < yMis; y++)
        {
            _frame.TxSizeMap.AsSpan(((_miRow + y) * _miCols) + _miCol, xMis).Fill((byte)tx);
        }

        b.Skip = c.Skip;

        // A whole superblock without residual codes no quantizer delta, and keeps the one before.
        b.QIndex = c.Skip && b.Size == _frame.SbSize ? _currentQIndex : _blockQIndex;
        b.SegmentId = _blockSegment;
        if (_header.SegmentationEnabled)
        {
            FillSegment(_frame.SegmentMap, _blockSegment, xMis, yMis);
            KeepSkipSegment();
        }

        return true;

        bool Codable(int row, int col) => (_header.ForceIntegerMv ? ((row | col) & 7) == 0 : _header.AllowHighPrecisionMv || ((row | col) & 1) == 0)
            && Math.Abs(row) < (1 << 14) && Math.Abs(col) < (1 << 14);
    }

    private static bool ValidMv(int row, int col) => row > MvLow && row < MvUpp && col > MvLow && col < MvUpp;

    /// <summary>Gets whether a choice codes its filters, the block set to it.</summary>
    private bool FiltersCoded(InterChoice c) => !c.SkipMode && c.MotionMode != 2 && Apply(c) && InterpFilterCoded(out _);

    /// <summary>Gets the motion modes a single-reference choice may use (0, 1 with OBMC, 2 with local warp too).</summary>
    private int MotionModesFor(InterChoice c)
    {
        if (!Apply(c with { MotionMode = 0 }) || Math.Min(Av1Tables.BlockSizeWide[_b.Size], Av1Tables.BlockSizeHigh[_b.Size]) < 8)
        {
            return 0;
        }

        return MotionModesAllowed(HasOverlappableNeighbors(), FindSamples());
    }

    /// <summary>Estimates a choice from its luma prediction alone and its exact mode rate; <see cref="long.MaxValue"/> when it cannot be coded.</summary>
    private long ModelCost(InterChoice choice, Snapshot start)
    {
        if (!Apply(choice))
        {
            return long.MaxValue;
        }

        long rate = ModeRate();
        _reconstructor.LumaOnly = true;
        _reconstructor.InterPrediction(this);
        _reconstructor.LumaOnly = false;
        long error = Distortion(0, _b.Size);
        start.Restore(this);
        return Av1Rate.RdCost(_rdmult, rate, error);
    }

    /// <summary>
    /// Encodes the current block with an inter choice: its prediction, then its residual unless the choice drops it; returns its
    /// full cost (<see cref="FinishBlock"/>), or <see cref="long.MaxValue"/> past the budget or when the choice cannot be coded.
    /// </summary>
    private long EncodeInter(InterChoice choice, Snapshot start, long budget, bool chroma)
    {
        start.Restore(this);
        if (!Apply(choice))
        {
            start.Restore(this);
            return long.MaxValue;
        }

        _reconstructor.InterPrediction(this);
        if (choice.Skip)
        {
            for (int plane = 0; plane < _planes; plane++)
            {
                _planeDistortion[plane] = plane == 0 || chroma ? Distortion(plane, _b.Size) : 0;
            }

            // A lossless block drops its residual only when the prediction is exact.
            if (_header.LosslessArray[_b.SegmentId] && _planeDistortion[0] + (chroma ? _planeDistortion[1] + _planeDistortion[2] : 0) > 0)
            {
                start.Restore(this);
                return long.MaxValue;
            }

            _reconstructor.InterDone(this);
            return FinishInter(start, _b.Size, chroma);
        }

        _b.Skip = false;
        long cost = Av1Rate.RdCost(_rdmult, ModeRate(), 0);
        cost = Add(cost, EncodePlane(0, _b.Size, Left(budget, cost)));
        if (chroma && cost != long.MaxValue)
        {
            cost = Add(cost, EncodePlane(1, _b.Size, Left(budget, cost)));
            cost = Add(cost, EncodePlane(2, _b.Size, Left(budget, cost)));
        }

        if (cost == long.MaxValue)
        {
            start.Restore(this);
            return long.MaxValue;
        }

        _reconstructor.InterDone(this);
        return FinishBlock(start, _b.Size, chroma);
    }

    /// <summary>Settles a block coded without residual and returns its exact cost, as <see cref="FinishBlock"/> does.</summary>
    private long FinishInter(Snapshot start, int bsize, bool chroma)
    {
        _b.Skip = true;
        SetSkipTxSize(bsize);
        start.RestoreContexts(this);
        if (bsize == _frame.SbSize)
        {
            _b.QIndex = _currentQIndex;
        }

        KeepSkipSegment();
        Span<bool> cdef = stackalloc bool[4];
        _cdefTransmitted.CopyTo(cdef);
        Av1CostSink rate = new(false);
        CodeBlock(ref rate, _miRow, _miCol, bsize);
        cdef.CopyTo(_cdefTransmitted);
        long distortion = _planeDistortion[0] + (chroma ? _planeDistortion[1] + _planeDistortion[2] : 0);
        return Av1Rate.RdCost(_rdmult, rate.Cost, distortion);
    }

    /// <summary>Gives a block without residual the segment its neighbours predict, which is the one the decoder takes.</summary>
    private void KeepSkipSegment()
    {
        if (!_header.SegmentationEnabled || !_b.Skip || _header.SegIdPreSkip)
        {
            return;
        }

        int segment = SegmentPrediction().Pred;
        if (segment != _b.SegmentId)
        {
            _b.SegmentId = segment;
            int xMis = Math.Min(_bw, _miCols - _miCol), yMis = Math.Min(_bh, _frame.MiRows - _miRow);
            FillSegment(_frame.SegmentMap, segment, xMis, yMis);
        }
    }

    /// <summary>Encodes the chosen inter choice for keeps, the trellis on.</summary>
    private long EncodeInterWinner(InterChoice choice, Snapshot start, bool chroma)
    {
        _winner = true;
        long cost = EncodeInter(choice, start, long.MaxValue, chroma);
        _winner = false;
        return cost;
    }

    /// <summary>
    /// Finds the current block's new vector against a reference: a whole-sample pattern search from the best of the stack's
    /// vectors, the global vector, zero and the last vector found, then a refinement by halves to the frame's precision, each
    /// place costed with its vector's rate against the stack's first vector.
    /// </summary>
    private (int Row, int Col) MotionSearch(int rf, int bsize, BlockVectors reference)
    {
        Av1ReferencePlane plane = _source.ReferencePlanes[rf]!;
        int w = Av1Tables.BlockSizeWide[bsize], h = Av1Tables.BlockSizeHigh[bsize];
        int x0 = _miCol * 4, y0 = _miRow * 4;
        ushort[] source = _source.Planes[0];
        int stride = _frame.Strides[0];
        int sourceAt = (y0 * stride) + x0;
        int depth = _sequence.BitDepth - 8;
        double sadPerBit = Math.Sqrt(_rdmult / 2048.0) * (1 << depth) / 512;
        (int refRow, int refCol) = (reference.RefRow0, reference.RefCol0);
        bool integer = _header.ForceIntegerMv;
        bool hp = _header.AllowHighPrecisionMv && !integer;

        // Whole samples.
        long bestCost = long.MaxValue;
        (int Row, int Col) best = (0, 0);
        Check(0, 0);
        for (int i = 0; i < _mvCount; i++)
        {
            Check(Round(_mvRow0[i]), Round(_mvCol0[i]));
        }

        Check(Round(_gmRow0), Round(_gmCol0));
        Check(Round(_seedMv[rf].Row), Round(_seedMv[rf].Col));
        (int Row, int Col) center = best;
        int range = _profile.SearchRange;
        for (int step = Math.Max(1, range / 4); step >= 1; step >>= 1)
        {
            bool moved = true;
            for (int round = 0; moved && round < 16; round++)
            {
                moved = false;
                (int Row, int Col) from = best;
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if ((dx | dy) == 0)
                        {
                            continue;
                        }

                        int row = from.Row + (dy * step), col = from.Col + (dx * step);
                        if (Math.Abs(row - center.Row) <= range && Math.Abs(col - center.Col) <= range && Check(row, col))
                        {
                            moved = true;
                        }
                    }
                }
            }
        }

        (int Row, int Col) mv = (best.Row * 8, best.Col * 8);
        if (integer)
        {
            return mv;
        }

        // Fractions: halves, quarters and, with high precision, eighths, each from the best so far.
        long bestSub = SubpelCost(mv.Row, mv.Col);
        for (int step = 4; step >= (hp ? 1 : 2); step >>= 1)
        {
            (int Row, int Col) from = mv;
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if ((dx | dy) == 0)
                    {
                        continue;
                    }

                    int row = from.Row + (dy * step), col = from.Col + (dx * step);
                    long cost = SubpelCost(row, col);
                    if (cost < bestSub)
                    {
                        (bestSub, mv) = (cost, (row, col));
                    }
                }
            }
        }

        return mv;

        static int Round(int v) => (v + 4) >> 3;

        bool Check(int row, int col)
        {
            if (!plane.Holds(x0 + col, y0 + row, w, h))
            {
                return false;
            }

            long cost = Av1MotionSearch.Sad(source, sourceAt, stride, plane.Samples, plane.At(x0 + col, y0 + row), plane.Stride, w, h)
                + (long)(MvRate((row * 8) - refRow, (col * 8) - refCol) * sadPerBit);
            if (cost < bestCost)
            {
                (bestCost, best) = (cost, (row, col));
                return true;
            }

            return false;
        }

        long SubpelCost(int row, int col)
        {
            if (!plane.Holds(x0 + (col >> 3), y0 + (row >> 3), w + 1, h + 1) || !ValidMv(row, col))
            {
                return long.MaxValue;
            }

            Av1MotionSearch.Predict(plane, (x0 * 8) + col, (y0 * 8) + row, w, h, _sequence.BitDepth, _subpelIntermediate, _subpelPrediction);
            long sse = Av1MotionSearch.Sse(source, sourceAt, stride, _subpelPrediction, w, h);
            long scaled = depth switch { 2 => sse, 4 => (sse + 8) >> 4, _ => sse << 4 };
            return Av1Rate.RdCost(_rdmult, MvRate(row - refRow, col - refCol), scaled);
        }
    }

    /// <summary>Gets the rate of a vector difference at the frame's precision, in 1/512 bits.</summary>
    private long MvRate(int row, int col)
    {
        if (!_header.AllowHighPrecisionMv || _header.ForceIntegerMv)
        {
            // Rounded to the precision the frame codes, as the rate is only an estimate during the search.
            int mask = _header.ForceIntegerMv ? ~7 : ~1;
            row &= mask;
            col &= mask;
        }

        if (Math.Abs(row) >= 1 << 14 || Math.Abs(col) >= 1 << 14)
        {
            return 1 << 20;
        }

        Av1CostSink rate = new(false);
        CodeMv(ref rate, row, col);
        return rate.Cost;
    }
}
