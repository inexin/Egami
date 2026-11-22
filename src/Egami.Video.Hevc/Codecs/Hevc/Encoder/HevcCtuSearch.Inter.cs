using Egami.Video.Codecs.Hevc.Encoder;

namespace Egami.Heic.Hevc;

/// <summary>
/// The coding quadtree of a P picture's CTB (HEVC encoder design, task 6): at each coding unit the merge candidates ranked by SATD
/// and the best coded in full, with and without a residual (a skipped unit stops there); then the four smaller units; then the
/// motion search of 2Nx2N in the references the smaller units used, and of the 2NxN and Nx2N halves seeded with its vector, the
/// best one or two of them by SATD coded in full; intra only where the smaller units found it useful. Every full coding is the
/// decoder's prediction and reconstruction, its rate counted by <see cref="HevcSyntaxWriter"/>.
/// </summary>
internal sealed partial class HevcCtuSearch
{
    private readonly HevcSnapshot[] _splitBest = new HevcSnapshot[5];
    private readonly HevcPredictionUnit[] _mergeList = new HevcPredictionUnit[5];
    private readonly long[] _mergeCosts = new long[5];
    private readonly ushort[][] _cuPrediction = [new ushort[64 * 64], new ushort[64 * 64], new ushort[64 * 64]];
    private readonly ushort[] _rankBlock = new ushort[64 * 64];
    private readonly byte[] _contextsResidual = new byte[HevcContexts.Count];
    private readonly HevcMotionSearch _motionSearch = new();
    private readonly HevcInter _interPrediction = new();
    private HevcInterContext? _interContext;
    private HevcMotionPredictor? _predictor;
    private int _ctb;
    private int _lambdaMe;
    private int _minDepth;

    /// <summary>Gets or sets what the CTU searches of a P picture share, or null for an I picture.</summary>
    internal HevcInterContext? InterContext
    {
        get => _interContext;
        set
        {
            _interContext = value;
            _predictor = value is null ? null : new HevcMotionPredictor(_picture, _slice, value.Refs);
            if (value is not null && _splitBest[0] is null)
            {
                for (int i = 0; i < _splitBest.Length; i++)
                {
                    _splitBest[i] = new HevcSnapshot(_picture) { Motion = true };
                    _before[i].Motion = _best[i].Motion = true;
                }

                _partBefore.Motion = _partBest.Motion = true;
            }

            if (value is not null)
            {
                HevcInterProfile profile = value.Profile;
                _motionSearch.Range = profile.SearchRange;
                _motionSearch.SquareSubpel = profile.SquareSubpel;
                _motionSearch.QuarterSamples = profile.QuarterSamples;
                if (_sps.BitDepthLuma == 8)
                {
                    lock (value)
                    {
                        value.Source8 ??= HevcReference.Bytes(_source.Planes[0]);
                    }
                }

                _motionSearch.Source8 = value.Source8;
            }
        }
    }

    partial void InterCtu(int xCtb, int yCtb, List<HevcCodingUnit> cus, ref bool coded)
    {
        if (_interContext is null)
        {
            return;
        }

        coded = true;
        _ctb = ((yCtb >> _sps.CtbLog2Size) * _sps.WidthInCtbs) + (xCtb >> _sps.CtbLog2Size);
        _minDepth = _interContext.Profile.CoLocatedDepth ? CoLocatedDepth(xCtb, yCtb) : 0;
        InterQuadtree(xCtb, yCtb, _sps.CtbLog2Size, 0, cus, out _);
    }

    /// <summary>
    /// The least coding tree depth worth trying whole in a CTB, from the co-located CTBs of the first reference of each list: the
    /// least depth any of them used, one less where that reference's QP was no higher than this CTB's and its mean depth under
    /// 1.5 times its least.
    /// </summary>
    private int CoLocatedDepth(int xCtb, int yCtb)
    {
        int size = 1 << _sps.CtbLog2Size;
        int x4 = xCtb >> 2, y4 = yCtb >> 2, w4 = Math.Min(size, _sps.Width - xCtb + 3) >> 2, h4 = Math.Min(size, _sps.Height - yCtb + 3) >> 2;
        int qp = QgQp(xCtb, yCtb), result = int.MaxValue;
        HevcSliceRefs refs = _interContext!.Refs;
        for (int list = 0; list < 2; list++)
        {
            if (refs.Lists[list].Length == 0)
            {
                continue;
            }

            HevcPicture reference = refs.Lists[list][0].Picture;
            int stride = reference.Width4, least = int.MaxValue, sum = 0, qpSum = 0;
            for (int y = 0; y < h4; y++)
            {
                int at = ((y4 + y) * stride) + x4;
                for (int x = 0; x < w4; x++)
                {
                    int d = reference.CtDepth[at + x];
                    least = Math.Min(least, d);
                    sum += d;
                    qpSum += reference.QpY[at + x];
                }
            }

            int blocks = w4 * h4;
            if (least > 0 && qp >= (double)qpSum / blocks && sum < 1.5 * least * blocks)
            {
                least--;
            }

            result = Math.Min(result, least);
        }

        return result == int.MaxValue ? 0 : result;
    }

    /// <summary>What a coding quadtree node's decision tells its parent: the list 0 references it used and whether it chose intra anywhere.</summary>
    private readonly record struct NodeInfo(int RefMask, bool Intra);

    /// <summary>A coding unit's inter prediction: its PartMode and the motion and syntax of its prediction units, with its rough cost.</summary>
    private struct InterChoice
    {
        internal int PartMode;
        internal HevcPredictionUnit U0;
        internal HevcPredictionUnit U1;
        internal long Cost;
    }

    private double InterQuadtree(int x0, int y0, int log2, int depth, List<HevcCodingUnit> cus, out NodeInfo info)
    {
        HevcInterProfile profile = _interContext!.Profile;
        int size = 1 << log2;
        bool inside = x0 + size <= _sps.Width && y0 + size <= _sps.Height;
        bool canSplit = log2 > _sps.MinCbLog2Size;
        bool mustSplit = canSplit && (!inside || log2 > profile.MaxCuLog2);
        bool canStop = inside && !mustSplit;
        canSplit &= mustSplit || log2 > profile.MinCuLog2;
        canStop &= !canSplit || depth >= _minDepth;
        bool flagCoded = inside && log2 > _sps.MinCbLog2Size;

        if (_pps.CuQpDeltaEnabled && log2 >= _log2MinCuQpDeltaSize)
        {
            _writer.IsCuQpDeltaCoded = false;
        }

        if (!canStop)
        {
            return InterSplit(x0, y0, log2, depth, flagCoded, cus, out info);
        }

        HevcEncodeStats.Add(HevcEncodeStat.CodingUnits);
        HevcSnapshot before = _before[depth], best = _best[depth];
        Save(before, x0, y0, size);
        double lambda = Lambda(QgQp(x0, y0));
        _lambdaMe = (int)(Math.Sqrt(SearchLambda(QgQp(x0, y0))) * 256);

        // The merge candidates: the best by SATD (and a close second) coded in full, with and without residual.
        HevcCodingUnit? bestCu = null;
        InterChoice bestChoice = default;
        double bestCost = double.MaxValue;
        bool dirty = false, current = false;
        int merges = MergeChoices(x0, y0, log2, profile);
        for (int m = 0; m < merges; m++)
        {
            InterChoice choice = MergeChoice(m);
            HevcCodingUnit cu = TryInter(x0, y0, log2, depth, flagCoded, choice, !profile.RdoqKeptOnly, ref dirty, before, out double cost);
            current = cost < bestCost;
            if (current)
            {
                (bestCu, bestCost, bestChoice) = (cu, cost, choice);
                Save(best, x0, y0, size);
            }
        }

        if (bestCu!.Skip && profile.EarlySkip)
        {
            HevcEncodeStats.Add(HevcEncodeStat.EarlySkips);
            RestoreBest(best, x0, y0, size, ref current);
            return Stop(bestCu, bestCost, depth, cus, out info);
        }

        // The four smaller units first: their references narrow the motion search here.
        double splitCost = double.MaxValue;
        List<HevcCodingUnit> splitCus = [];
        NodeInfo children = new(0, false);
        bool childrenTried = false;
        int orientation = 0;
        if (canSplit && !(profile.SplitStatistics && bestCost < AverageSkipCost(depth)))
        {
            Restore(before, x0, y0, size);
            splitCost = InterSplit(x0, y0, log2, depth, flagCoded, splitCus, out children);
            Save(_splitBest[depth], x0, y0, size);
            orientation = profile.HalvesOrientation ? ChildOrientation(x0, y0, log2) : 0;
            dirty = true;
            current = false;
            childrenTried = true;
        }
        else if (canSplit)
        {
            HevcEncodeStats.Add(HevcEncodeStat.SplitsSkipped);
        }

        // The motion search of 2Nx2N in the references the smaller units used, coded in full.
        InterChoice whole = SearchWhole(x0, y0, log2, children.RefMask);
        HevcEncodeStats.Add(HevcEncodeStat.InterCandidates);
        HevcCodingUnit wholeCu = TryInter(x0, y0, log2, depth, flagCoded, whole, !profile.RdoqKeptOnly, ref dirty, before, out double wholeCost);
        current = wholeCost < bestCost;
        if (current)
        {
            (bestCu, bestCost, bestChoice) = (wholeCu, wholeCost, whole);
            Save(best, x0, y0, size);
        }

        // A B picture's runner-up between one list and both, coded in full when near the best.
        if (_wholeAlternative.Cost != 0)
        {
            HevcEncodeStats.Add(HevcEncodeStat.InterCandidates);
            InterChoice alternative = _wholeAlternative;
            HevcCodingUnit alternativeCu = TryInter(x0, y0, log2, depth, flagCoded, alternative, !profile.RdoqKeptOnly, ref dirty, before, out double alternativeCost);
            current = alternativeCost < bestCost;
            if (current)
            {
                (bestCu, bestCost, bestChoice) = (alternativeCu, alternativeCost, alternative);
                Save(best, x0, y0, size);
            }
        }

        // The halves, seeded with 2Nx2N's vector: the better one by SATD coded in full when it is near 2Nx2N's.
        if (profile.Halves && log2 >= profile.HalvesMinLog2 && (!profile.HalvesGate || (bestCu.RootCbf && (!childrenTried || splitCost < bestCost))))
        {
            Span<InterChoice> halves = stackalloc InterChoice[2];
            int count = orientation < 0 ? 0 : SearchHalves(x0, y0, log2, profile, whole, children.RefMask, orientation, ref dirty, before, halves);
            current = false;
            double ratio = profile.SecondCandidateRatio;
            for (int i = 0; i < count; i++)
            {
                if (halves[i].Cost > whole.Cost * Math.Max(ratio, 1) || (i > 0 && (ratio == 0 || halves[i].Cost > halves[0].Cost * ratio)))
                {
                    break;
                }

                HevcEncodeStats.Add(HevcEncodeStat.InterCandidates);
                HevcCodingUnit cu = TryInter(x0, y0, log2, depth, flagCoded, halves[i], !profile.RdoqKeptOnly, ref dirty, before, out double cost);
                current = cost < bestCost;
                if (current)
                {
                    (bestCu, bestCost, bestChoice) = (cu, cost, halves[i]);
                    Save(best, x0, y0, size);
                }
            }
        }

        // Intra: where the best inter prediction leaves a residual and a smaller unit inside chose intra (none at 64x64).
        bool tryIntra = log2 < 6 && (!profile.IntraGate || (bestCu.RootCbf && (children.Intra || !childrenTried)))
            && (!profile.IntraGate || !profile.IntraSatdGate || IntraRoughCost(x0, y0, log2) < bestChoice.Cost >> 8);
        bool bestIntra = false;
        if (tryIntra)
        {
            HevcEncodeStats.Add(HevcEncodeStat.IntraPasses);
            if (dirty)
            {
                Restore(before, x0, y0, size);
            }

            dirty = true;
            long flag = StopFlagCost(x0, y0, depth, flagCoded);
            HevcCodingUnit cu = CodingUnit(x0, y0, log2, depth, out double cost);
            cost += Rate(flag, cu.Qp);
            FillBlocks(_picture.PredFlags, x0, y0, size, size, 0);
            FillBlocks(_picture.SkipFlags, x0, y0, size, size, 0);
            current = false;
            if (cost < bestCost)
            {
                (bestCu, bestCost, bestIntra) = (cu, cost, true);
                Save(best, x0, y0, size);
                current = true;
            }
        }

        // The kept inter coding once more in full (RDOQ, sign hiding, the transform tree's splits), when it was a trial.
        bool recode = !bestIntra && profile.RdoqKeptOnly && bestCu.RootCbf;
        if (recode && !profile.RecodeAfterSplit)
        {
            Restore(before, x0, y0, size);
            dirty = true;
            bestCu = TryInter(x0, y0, log2, depth, flagCoded, bestChoice, true, ref dirty, before, out bestCost);
            current = true;
            recode = false;
        }

        if (splitCost < bestCost)
        {
            Restore(_splitBest[depth], x0, y0, size);
            cus.AddRange(splitCus);
            info = children;
            return splitCost;
        }

        if (recode)
        {
            Restore(before, x0, y0, size);
            dirty = true;
            bestCu = TryInter(x0, y0, log2, depth, flagCoded, bestChoice, true, ref dirty, before, out bestCost);
            current = true;
        }

        RestoreBest(best, x0, y0, size, ref current);
        return Stop(bestCu, bestCost, depth, cus, out info);
    }

    /// <summary>A rough intra cost of a coding unit (planar, DC, horizontal and vertical by SATD plus their mode bits), in SATD units.</summary>
    private long IntraRoughCost(int x0, int y0, int log2)
    {
        Span<bool> tried = stackalloc bool[35];
        ReadOnlySpan<int> modes = [0, 1, 10, 26];
        RoughCosts(x0, y0, log2, modes, modes[..3], Math.Sqrt(SearchLambda(QgQp(x0, y0))), tried);
        long best = long.MaxValue;
        foreach (int m in modes)
        {
            best = Math.Min(best, _roughCosts[m]);
        }

        return best;
    }

    private void RestoreBest(HevcSnapshot best, int x0, int y0, int size, ref bool current)
    {
        if (!current)
        {
            Restore(best, x0, y0, size);
            current = true;
        }
    }

    /// <summary>Keeps a unit decided whole: its statistics and what it tells its parent.</summary>
    private double Stop(HevcCodingUnit cu, double cost, int depth, List<HevcCodingUnit> cus, out NodeInfo info)
    {
        cus.Add(cu);
        if (cu.Skip && depth < HevcInterContext.Depths)
        {
            _interContext!.SkipCost[(_ctb * HevcInterContext.Depths) + depth] += cost;
            _interContext.SkipCount[(_ctb * HevcInterContext.Depths) + depth]++;
        }

        int mask = 0;
        if (cu.Inter)
        {
            for (int i = 0; i < cu.UnitCount; i++)
            {
                mask |= 1 << Math.Max(cu.Units[i].RefIdx0, 0);
            }
        }

        info = new NodeInfo(mask, !cu.Inter);
        return cost;
    }

    /// <summary>The four smaller units, from the current state, with split_cu_flag's rate when it is coded.</summary>
    private double InterSplit(int x0, int y0, int log2, int depth, bool flagCoded, List<HevcCodingUnit> cus, out NodeInfo info)
    {
        double cost = 0;
        if (flagCoded)
        {
            _bins.Cost = 0;
            _writer.SplitFlag(x0, y0, depth, true);
            cost = Rate(_bins.Cost, QgQp(x0, y0));
        }

        int half = 1 << (log2 - 1), mask = 0;
        bool intra = false;
        for (int i = 0; i < 4; i++)
        {
            int x = x0 + ((i & 1) * half), y = y0 + ((i >> 1) * half);
            if (x < _sps.Width && y < _sps.Height)
            {
                cost += InterQuadtree(x, y, log2 - 1, depth + 1, cus, out NodeInfo child);
                mask |= child.RefMask;
                intra |= child.Intra;
            }
        }

        info = new NodeInfo(mask, intra);
        return cost;
    }

    /// <summary>
    /// Which halves the four smaller units just decided point to: <see cref="HevcCodingUnit.Part2NxN"/> when the upper pair and the
    /// lower pair move more alike than the left and right pairs, <see cref="HevcCodingUnit.PartNx2N"/> the other way, 0 on a tie.
    /// </summary>
    private int ChildOrientation(int x0, int y0, int log2)
    {
        int half = 1 << (log2 - 1);
        int a = MotionAt(x0, y0), b = MotionAt(x0 + half, y0), c = MotionAt(x0, y0 + half), d = MotionAt(x0 + half, y0 + half);
        long across = Apart(a, b) + Apart(c, d), down = Apart(a, c) + Apart(b, d);
        return across == 0 && down == 0 ? -1 : across < down ? HevcCodingUnit.Part2NxN : down < across ? HevcCodingUnit.PartNx2N : 0;
    }

    /// <summary>The 4x4 block index at a luma sample, for <see cref="Apart"/>.</summary>
    private int MotionAt(int x, int y) => ((y >> 2) * _picture.Width4) + (x >> 2);

    /// <summary>How far apart the motion of two 4x4 blocks is: the vector distance in quarter samples, large across references or intra.</summary>
    private long Apart(int a, int b)
    {
        long far = 1 << 16, sum = 0;
        if (_picture.PredFlags[a] != _picture.PredFlags[b] || _picture.PredFlags[a] == 0)
        {
            return far;
        }

        for (int l = 0; l < 2; l++)
        {
            if ((_picture.PredFlags[a] & (1 << l)) == 0)
            {
                continue;
            }

            if (_picture.RefIdx[l][a] != _picture.RefIdx[l][b])
            {
                return far;
            }

            int u = _picture.Mv[l][a], v = _picture.Mv[l][b];
            sum += Math.Abs((short)u - (short)v) + Math.Abs((u >> 16) - (v >> 16));
        }

        return sum;
    }

    /// <summary>The rate of split_cu_flag 0 when it is coded, in 1/32768 bits, the contexts moved on with it.</summary>
    private long StopFlagCost(int x0, int y0, int depth, bool flagCoded)
    {
        if (!flagCoded)
        {
            return 0;
        }

        _bins.Cost = 0;
        _writer.SplitFlag(x0, y0, depth, false);
        return _bins.Cost;
    }

    /// <summary>The average cost of the skipped units of a depth in this CTB (weight 3) and the CTBs left of and above it (weight 2).</summary>
    private double AverageSkipCost(int depth)
    {
        if (depth >= HevcInterContext.Depths)
        {
            return 0;
        }

        HevcInterContext context = _interContext!;
        int width = _sps.WidthInCtbs, x = _ctb % width, y = _ctb / width;
        double sum = 3 * context.SkipCost[(_ctb * HevcInterContext.Depths) + depth];
        long count = 3 * context.SkipCount[(_ctb * HevcInterContext.Depths) + depth];
        foreach ((int dx, int dy) in (ReadOnlySpan<(int, int)>)[(-1, 0), (-1, -1), (0, -1), (1, -1)])
        {
            int nx = x + dx, ny = y + dy;
            if (nx >= 0 && nx < width && ny >= 0)
            {
                int at = (((ny * width) + nx) * HevcInterContext.Depths) + depth;
                sum += 2 * context.SkipCost[at];
                count += 2 * context.SkipCount[at];
            }
        }

        return count == 0 ? 0 : sum / count;
    }

    // ---------------------------------------------------------------- candidates

    /// <summary>
    /// Ranks the 2Nx2N merge candidates by SATD plus their index's rate into <see cref="_mergeList"/>'s order; gives how many to
    /// code in full (the best, and a second close to it when the profile allows).
    /// </summary>
    private int MergeChoices(int x0, int y0, int log2, HevcInterProfile profile)
    {
        int size = 1 << log2, max = _slice.MaxNumMergeCand;
        HevcMotionPredictor.Unit unit = new(x0, y0, size, x0, y0, size, size, 0, HevcCodingUnit.Part2Nx2N);
        Span<HevcPredictionUnit> list = _mergeList.AsSpan(0, max);
        _predictor!.MergeCandidates(unit, list);
        Span<long> rank = stackalloc long[5];
        for (int i = 0; i < max; i++)
        {
            rank[i] = RankCostOnce(x0, y0, size, size, list, rank, i);
            _mergeCosts[i] = rank[i] + (_lambdaMe * (long)Math.Min(i + 1, max - 1));
        }

        // Insertion sort of the few candidates by cost, the list's order following.
        for (int i = 1; i < max; i++)
        {
            for (int j = i; j > 0 && _mergeCosts[j] < _mergeCosts[j - 1]; j--)
            {
                (_mergeCosts[j], _mergeCosts[j - 1]) = (_mergeCosts[j - 1], _mergeCosts[j]);
                (list[j], list[j - 1]) = (list[j - 1], list[j]);
            }
        }

        double ratio = profile.SecondCandidateRatio;
        return max > 1 && ratio > 0 && _mergeCosts[1] <= _mergeCosts[0] * ratio ? 2 : 1;
    }

    private InterChoice MergeChoice(int rank) =>
        new() { PartMode = HevcCodingUnit.Part2Nx2N, U0 = _mergeList[rank], Cost = _mergeCosts[rank] };

    /// <summary>The rough cost of merge candidate i, taken from an earlier candidate with the same motion when there is one.</summary>
    private long RankCostOnce(int x, int y, int w, int h, ReadOnlySpan<HevcPredictionUnit> list, ReadOnlySpan<long> rank, int i)
    {
        for (int j = 0; j < i; j++)
        {
            if (HevcMotionPredictor.SameMotion(list[j], list[i]))
            {
                return rank[j];
            }
        }

        HevcEncodeStats.Add(HevcEncodeStat.MergeCosts);
        return RankCost(x, y, w, h, list[i]);
    }

    /// <summary>
    /// The rough cost of a unit's motion: SATD of its luma prediction (quarter samples averaged, both lists' predictions averaged
    /// again) in 1/256.
    /// </summary>
    private long RankCost(int x, int y, int w, int h, in HevcPredictionUnit pu)
    {
        if ((pu.Flags & 1) != 0)
        {
            RoughBlock(0, pu.RefIdx0, pu.Mv0, x, y, w, h, _rankBlock);
            if ((pu.Flags & 2) != 0)
            {
                RoughBlock(1, pu.RefIdx1, pu.Mv1, x, y, w, h, _rankBlock1);
                Average(_rankBlock, _rankBlock1, _rankBlock, w, h);
            }
        }
        else
        {
            RoughBlock(1, pu.RefIdx1, pu.Mv1, x, y, w, h, _rankBlock);
        }

        int stride = _picture.Widths[0];
        return HevcMotionSearch.Satd(_source.Planes[0], (y * stride) + x, stride, _rankBlock, 0, 64, w, h, _sps.BitDepthLuma == 8) << 8;
    }

    // A B picture's 2Nx2N runner-up between one list and both, when near the best (its Cost 0 when there is none).
    private InterChoice _wholeAlternative;

    /// <summary>
    /// The motion search of 2Nx2N in the references of <paramref name="refMask"/> (every active one when it names none); in a B
    /// picture in both lists, with <see cref="_wholeAlternative"/>.
    /// </summary>
    private InterChoice SearchWhole(int x0, int y0, int log2, int refMask)
    {
        int size = 1 << log2;
        int refs = Math.Min(_slice.NumRefIdxL0, _interContext!.References.Length);
        if ((refMask & ((1 << refs) - 1)) == 0)
        {
            refMask = (1 << refs) - 1;
        }

        HevcMotionPredictor.Unit unit = new(x0, y0, size, x0, y0, size, size, 0, HevcCodingUnit.Part2Nx2N);
        _wholeAlternative = default;
        if (_interContext.B)
        {
            HevcInterProfile profile = _interContext.Profile;
            HevcPredictionUnit b = SearchUnitB(unit, NoSeed, NoSeed, false, false, profile.BiRefine, out long bCost, out HevcPredictionUnit alternative,
                out long alternativeCost);
            if (alternativeCost != long.MaxValue && profile.BAlternative)
            {
                _wholeAlternative = new InterChoice { PartMode = HevcCodingUnit.Part2Nx2N, U0 = alternative, Cost = alternativeCost };
            }

            return new InterChoice { PartMode = HevcCodingUnit.Part2Nx2N, U0 = b, Cost = bCost };
        }

        HevcPredictionUnit pu = SearchUnit(unit, refMask, 0, false, out long cost);
        return new InterChoice { PartMode = HevcCodingUnit.Part2Nx2N, U0 = pu, Cost = cost };
    }

    /// <summary>
    /// The motion search of the 2NxN and Nx2N halves, each seeded with 2Nx2N's vector (in its reference only, with the halves
    /// gate); fills <paramref name="choices"/> sorted by rough cost and gives how many there are.
    /// </summary>
    private int SearchHalves(int x0, int y0, int log2, HevcInterProfile profile, in InterChoice whole, int refMask, int orientation, ref bool dirty,
        HevcSnapshot before, Span<InterChoice> choices)
    {
        int size = 1 << log2, count = 0;
        int seed = whole.U0.Mv0, seedRef = 1 << Math.Max(whole.U0.RefIdx0, 0);
        bool b = _interContext!.B;
        int seed0 = _lastMv0, seed1 = _lastMv1;
        int mask = profile.HalvesGate ? seedRef : refMask | seedRef;

        // The second half predicts from the first's motion, which the block maps must hold meanwhile.
        foreach (int partMode in (ReadOnlySpan<int>)[HevcCodingUnit.Part2NxN, HevcCodingUnit.PartNx2N])
        {
            if (orientation != 0 && orientation != partMode)
            {
                continue;
            }

            if (dirty)
            {
                Restore(before, x0, y0, size);
            }

            dirty = true;
            InterChoice choice = new() { PartMode = partMode };
            for (int part = 0; part < 2; part++)
            {
                (int px, int py, int pw, int ph) = HevcCodingUnit.UnitRect(x0, y0, log2, partMode, part);
                HevcMotionPredictor.Unit unit = new(x0, y0, size, px, py, pw, ph, part, partMode);
                HevcPredictionUnit found = b
                    ? SearchUnitB(unit, seed0, seed1, profile.HalvesMerges, profile.HalvesStartsOnly, false, out long partCost, out _, out _)
                    : SearchUnit(unit, mask, seed, profile.HalvesMerges, out partCost, profile.HalvesStartsOnly);
                choice.Cost += partCost;
                if (part == 0)
                {
                    choice.U0 = found;
                    StoreMotion(px, py, pw, ph, found);
                }
                else
                {
                    choice.U1 = found;
                }
            }

            // Two halves with one motion are 2Nx2N coded dearer.
            if (!HevcMotionPredictor.SameMotion(choice.U0, choice.U1))
            {
                choices[count++] = choice;
            }
        }

        if (count == 2 && choices[1].Cost < choices[0].Cost)
        {
            (choices[0], choices[1]) = (choices[1], choices[0]);
        }

        return count;
    }

    /// <summary>
    /// One prediction unit's motion: the search in each reference of <paramref name="refMask"/> from its AMVP predictors, the merge
    /// candidates and <paramref name="seed"/>, and with <paramref name="merges"/> the merge candidates themselves; gives the
    /// cheapest by SATD plus rate.
    /// </summary>
    private HevcPredictionUnit SearchUnit(in HevcMotionPredictor.Unit unit, int refMask, int seed, bool merges, out long cost, bool startsOnly = false)
    {
        int max = _slice.MaxNumMergeCand;
        Span<HevcPredictionUnit> list = stackalloc HevcPredictionUnit[5];
        _predictor!.MergeCandidates(unit, list[..max]);
        HevcPredictionUnit best = default;
        cost = long.MaxValue;
        if (merges)
        {
            Span<long> rank = stackalloc long[5];
            for (int i = 0; i < max; i++)
            {
                rank[i] = RankCostOnce(unit.XPb, unit.YPb, unit.Width, unit.Height, list, rank, i);
                long c = rank[i] + (_lambdaMe * (long)(1 + Math.Min(i + 1, max - 1)));
                if (c < cost)
                {
                    (cost, best) = (c, list[i]);
                }
            }
        }

        int refs = Math.Min(_slice.NumRefIdxL0, _interContext!.References.Length);
        Span<int> starts = stackalloc int[6];
        int stride = _picture.Widths[0];
        for (int r = 0; r < refs; r++)
        {
            if ((refMask & (1 << r)) == 0)
            {
                continue;
            }

            int n = 0;
            starts[n++] = seed;
            for (int i = 0; i < max; i++)
            {
                if ((list[i].Flags & 1) != 0 && list[i].RefIdx0 == r && !starts[..n].Contains(list[i].Mv0))
                {
                    starts[n++] = list[i].Mv0;
                }
            }

            (int mvp0, int mvp1) = _predictor.Amvp(unit, 0, r);
            (int mv, long c, int flag) = _motionSearch.Search(_source.Planes[0], stride, unit.XPb, unit.YPb, unit.Width, unit.Height, _interContext.References[r],
                (mvp0, mvp1), starts[..n], _lambdaMe, startsOnly);
            c += _lambdaMe * (long)(1 + (refs > 1 ? Math.Min(r + 1, refs - 1) : 0));
            if (c < cost)
            {
                int mvp = flag == 0 ? mvp0 : mvp1;
                cost = c;
                best = new HevcPredictionUnit
                {
                    Flags = 1,
                    RefIdx0 = r,
                    RefIdx1 = -1,
                    Mv0 = mv,
                    Mvd0 = HevcMotionPredictor.Pack((short)mv - (short)mvp, (mv >> 16) - (mvp >> 16)),
                    MvpFlag0 = flag,
                };
            }
        }

        return best;
    }

    // ---------------------------------------------------------------- full coding

    /// <summary>Codes an inter choice in full from the state before the unit (restored first when another trial left it changed).</summary>
    private HevcCodingUnit TryInter(int x0, int y0, int log2, int depth, bool flagCoded, in InterChoice choice, bool rdoq, ref bool dirty, HevcSnapshot before,
        out double cost)
    {
        if (dirty)
        {
            Restore(before, x0, y0, 1 << log2);
        }

        dirty = true;
        HevcEncodeStats.Add(HevcEncodeStat.RdCodings);
        long flag = StopFlagCost(x0, y0, depth, flagCoded);
        HevcCodingUnit cu = CodeInter(x0, y0, log2, depth, choice, rdoq, out cost);
        cost += Rate(flag, cu.Qp);
        return cu;
    }

    /// <summary>
    /// Predicts an inter coding unit, codes its transform tree, keeps the residual only where it pays (per block, and for the
    /// whole unit, which without one is skipped when it is a merged 2Nx2N), commits it and gives its cost J.
    /// </summary>
    private HevcCodingUnit CodeInter(int x0, int y0, int log2, int depth, in InterChoice choice, bool rdoq, out double cost)
    {
        int qp = UnitQp(x0, y0);
        HevcCodingUnit cu = new()
        {
            X = x0,
            Y = y0,
            Log2 = log2,
            Depth = depth,
            Bypass = _settings.Lossless,
            Qp = qp,
            Inter = true,
            PartMode = choice.PartMode,
        };
        cu.LumaModes[0] = 1;
        cu.ChromaSyntax[0] = 4;
        cu.Units[0] = choice.U0;
        cu.Units[1] = choice.U1;
        Array.Copy(_bins.Contexts, _contextsAtUnit, HevcContexts.Count);
        bool qpDeltaCoded = _writer.IsCuQpDeltaCoded;
        double lambda = Lambda(qp);

        HevcSliceRefs refs = _interContext!.Refs;
        for (int i = 0; i < cu.UnitCount; i++)
        {
            (int px, int py, int pw, int ph) = HevcCodingUnit.UnitRect(x0, y0, log2, cu.PartMode, i);
            HevcPredictionUnit pu = cu.Units[i];
            StoreMotion(px, py, pw, ph, pu);
            _interPrediction.Predict(_picture, refs, _slice, px, py, pw, ph, pu.Flags, pu.RefIdx0, pu.RefIdx1, pu.Mv0, pu.Mv1);
        }

        // A static trial leaves the prediction in the picture; a full coding reconstructs over it, so it is kept to go back to.
        bool trial = !rdoq && !cu.Bypass && _interContext.Profile.RdoqKeptOnly && _interContext.Profile.StaticTrials;
        if (!trial)
        {
            CopyPrediction(x0, y0, log2, true);
        }

        double predicted = UnitDistortion(x0, y0, log2, qp);

        // The transform tree, its blocks' residuals counted as they are coded (moving the residual contexts on); the unit's own
        // syntax then from those contexts, whose other parts the tree leaves as they were at the unit's start.
        HevcTransformNode root = InterTree(cu, x0, y0, log2, 0, qp, lambda, rdoq, out double treeCost);
        bool residual = HasCoefficients(root);
        bool merged = cu.PartMode == HevcCodingUnit.Part2Nx2N && cu.Units[0].Merge;
        double coded = double.MaxValue;
        if (residual)
        {
            SetResidual(cu, qpDeltaCoded, true, merged, root, qp);
            _writer.IsCuQpDeltaCoded = qpDeltaCoded;
            _writer.FlagsOnly = true;
            _bins.Cost = 0;
            _writer.CodingUnit(cu);
            _writer.FlagsOnly = false;
            coded = treeCost + (lambda * _bins.Cost / Nal.CabacCost.Bit);
            Array.Copy(_bins.Contexts, _contextsResidual, HevcContexts.Count);
        }

        // The unit without any residual (skipped when merged whole).
        SetResidual(cu, qpDeltaCoded, false, merged, root, qp);
        Array.Copy(_contextsAtUnit, _bins.Contexts, HevcContexts.Count);
        _writer.IsCuQpDeltaCoded = qpDeltaCoded;
        _bins.Cost = 0;
        _writer.CodingUnit(cu);
        cost = predicted + (lambda * _bins.Cost / Nal.CabacCost.Bit);
        if (coded < cost)
        {
            SetResidual(cu, qpDeltaCoded, true, merged, root, qp);
            Array.Copy(_contextsResidual, _bins.Contexts, HevcContexts.Count);
            _writer.IsCuQpDeltaCoded = qpDeltaCoded || (_pps.CuQpDeltaEnabled && residual);
            cost = coded;
        }
        else if (residual)
        {
            if (!trial)
            {
                CopyPrediction(x0, y0, log2, false);
            }

            residual = false;
        }

        // QpY as the decoder derives it: the unit's own QP only when it codes cu_qp_delta.
        if (_pps.CuQpDeltaEnabled && residual && !qpDeltaCoded)
        {
            _qp.QpY = qp;
        }
        else if (!_pps.CuQpDeltaEnabled)
        {
            _qp.QpY = _slice.SliceQp;
        }

        cu.Qp = _qp.QpY;
        _qp.LastQpY = _qp.QpY;
        CommitInter(cu);
        return cu;
    }

    /// <summary>Sets the unit's residual syntax: with or without the tree, skipped when merged whole without one.</summary>
    private void SetResidual(HevcCodingUnit cu, bool qpDeltaCoded, bool residual, bool merged, HevcTransformNode root, int qp)
    {
        cu.RootCbf = residual;
        cu.Skip = merged && !residual;
        cu.Tree = residual ? root : Empty;
        cu.QpDelta = _pps.CuQpDeltaEnabled && residual && !qpDeltaCoded ? qp - _qp.QpYPred : 0;
    }

    private static readonly HevcTransformNode Empty = new();

    /// <summary>The unit's QP and quantization group, as <see cref="CodingUnit"/> starts them.</summary>
    private int UnitQp(int x0, int y0)
    {
        int mask = (1 << _log2MinCuQpDeltaSize) - 1;
        int xQg = x0 & ~mask, yQg = y0 & ~mask;
        if (xQg != _qp.QgX || yQg != _qp.QgY || _qp.FirstQgInGroup)
        {
            StartQuantizationGroup(xQg, yQg);
        }

        if (!_pps.CuQpDeltaEnabled)
        {
            return _slice.SliceQp;
        }

        int offset = _sps.QpBdOffsetLuma;
        return _writer.IsCuQpDeltaCoded ? _qp.QpY : Math.Clamp(QgQp(x0, y0), _qp.QpYPred - 26 - (offset / 2), _qp.QpYPred + 25 + (offset / 2));
    }

    private double UnitDistortion(int x0, int y0, int log2, int qp)
    {
        double distortion = Distortion(0, x0, y0, log2);
        for (int c = 1; c < _picture.Planes.Length; c++)
        {
            distortion += ChromaWeight(qp, c) * Distortion(c, x0 / _sps.SubWidthC, y0 / _sps.SubHeightC, log2, true);
        }

        return distortion;
    }

    /// <summary>Copies the unit's prediction out of the picture (<paramref name="save"/>) or back into it.</summary>
    private void CopyPrediction(int x0, int y0, int log2, bool save)
    {
        for (int c = 0; c < _picture.Planes.Length; c++)
        {
            int sx = c == 0 ? 1 : _sps.SubWidthC, sy = c == 0 ? 1 : _sps.SubHeightC;
            int w = (1 << log2) / sx, h = (1 << log2) / sy, stride = _picture.Widths[c];
            int at = ((y0 / sy) * stride) + (x0 / sx);
            ushort[] plane = _picture.Planes[c], saved = _cuPrediction[c];
            for (int r = 0; r < h; r++)
            {
                if (save)
                {
                    HevcCopy.Run(plane, at + (r * stride), saved, r * 64, w);
                }
                else
                {
                    HevcCopy.Run(saved, r * 64, plane, at + (r * stride), w);
                }
            }
        }
    }

    /// <summary>
    /// Codes an inter transform tree node over the prediction in the picture: forced splits (above the largest transform, or the
    /// partitioned unit's split when max_transform_hierarchy_depth_inter is 0), and down to 8x8 the better of one transform unit
    /// and four. Gives the node and its approximate J.
    /// </summary>
    private HevcTransformNode InterTree(HevcCodingUnit cu, int x, int y, int log2, int depth, int qp, double lambda, bool rdoq, out double cost)
    {
        int maxDepth = _sps.MaxTransformHierarchyDepthInter;
        bool interSplit = maxDepth == 0 && cu.PartMode != HevcCodingUnit.Part2Nx2N && depth == 0;
        bool forced = log2 > _sps.MaxTbLog2Size || interSplit;
        bool flagCoded = !forced && log2 > _sps.MinTbLog2Size && depth < maxDepth;
        bool trySplit = flagCoded && log2 > 3 && _interContext!.Profile.TransformSplit;
        if (forced)
        {
            return InterSplitTree(cu, x, y, log2, depth, qp, lambda, rdoq, out cost);
        }

        // A static trial leaves the picture and the contexts as they are: nothing to keep or put back.
        bool trial = !rdoq && !cu.Bypass && _interContext!.Profile.RdoqKeptOnly && _interContext.Profile.StaticTrials;
        trySplit &= !trial || _interContext!.Profile.TrialTransformSplit;
        HevcSnapshot before = _tuBefore[log2], best = _tuBest[log2];
        if (trySplit && !trial)
        {
            before.Capture(x, y, 1 << log2, false, true);
            Array.Copy(_bins.Contexts, before.Contexts, HevcContexts.Count);
        }

        HevcTransformNode leaf = InterLeaf(cu, x, y, log2, qp, lambda, rdoq, true, out cost);
        if (!trySplit)
        {
            return leaf;
        }

        if (trial)
        {
            HevcTransformNode trialSplit = InterSplitTree(cu, x, y, log2, depth, qp, lambda, rdoq, out double trialCost);
            if (trialCost < cost)
            {
                cost = trialCost;
                return trialSplit;
            }

            return leaf;
        }

        best.Capture(x, y, 1 << log2, false, true);
        Array.Copy(_bins.Contexts, best.Contexts, HevcContexts.Count);
        before.Restore();
        Array.Copy(before.Contexts, _bins.Contexts, HevcContexts.Count);
        HevcTransformNode split = InterSplitTree(cu, x, y, log2, depth, qp, lambda, rdoq, out double splitCost);
        if (splitCost < cost)
        {
            cost = splitCost;
            return split;
        }

        best.Restore();
        Array.Copy(best.Contexts, _bins.Contexts, HevcContexts.Count);
        return leaf;
    }

    private HevcTransformNode InterSplitTree(HevcCodingUnit cu, int x, int y, int log2, int depth, int qp, double lambda, bool rdoq, out double cost)
    {
        HevcTransformNode node = new() { Split = true, Children = new HevcTransformNode[4] };
        int half = 1 << (log2 - 1);
        bool chromaHere = log2 == 3 && _chroma != 3 && _chroma != 0;
        cost = 0;
        for (int i = 0; i < 4; i++)
        {
            int cx = x + ((i & 1) * half), cy = y + ((i >> 1) * half);
            if (log2 - 1 == 2)
            {
                node.Children[i] = InterLeaf(cu, cx, cy, 2, qp, lambda, rdoq, !chromaHere, out double c);
                cost += c;
            }
            else
            {
                node.Children[i] = InterTree(cu, cx, cy, log2 - 1, depth + 1, qp, lambda, rdoq, out double c);
                cost += c;
            }
        }

        if (chromaHere)
        {
            // 4x4 luma blocks: the chroma of the 8x8 node is coded once, with the fourth, and each child reads its flags.
            cost += InterChroma(cu, node, node.Children[3], x, y, 2, qp, lambda, rdoq);
            foreach (HevcTransformNode child in node.Children)
            {
                (child.Cb0, child.Cb1, child.Cr0, child.Cr1) = (node.Cb0, node.Cb1, node.Cr0, node.Cr1);
            }
        }
        else
        {
            bool cb = false, cr = false;
            foreach (HevcTransformNode child in node.Children)
            {
                cb |= child.Cb0 || child.Cb1;
                cr |= child.Cr0 || child.Cr1;
            }

            (node.Cb0, node.Cb1, node.Cr0, node.Cr1) = (cb, false, cr, false);
        }

        return node;
    }

    /// <summary>A transform unit: its luma block and, unless its parent codes them, its chroma blocks.</summary>
    private HevcTransformNode InterLeaf(HevcCodingUnit cu, int x, int y, int log2, int qp, double lambda, bool rdoq, bool chroma, out double cost)
    {
        HevcTransformNode node = new();
        BlockResult luma = InterBlock(0, x, y, log2, qp, cu.Bypass, lambda, 1, rdoq);
        node.CbfLuma = luma.Levels is not null;
        node.Levels[0] = luma.Levels;
        cost = luma.Distortion + (lambda * luma.Bits / Nal.CabacCost.Bit);
        if (chroma && _chroma != 0)
        {
            cost += InterChroma(cu, node, node, x, y, _chroma == 3 ? log2 : log2 - 1, qp, lambda, rdoq);
        }

        return node;
    }

    /// <summary>Codes a node's chroma blocks (two per component for 4:2:2) at chroma size log2C, setting its flags and the target's levels.</summary>
    private double InterChroma(HevcCodingUnit cu, HevcTransformNode node, HevcTransformNode target, int x, int y, int log2C, int qp, double lambda, bool rdoq)
    {
        double cost = 0;
        int xC = x / _sps.SubWidthC, yC = y / _sps.SubHeightC;
        for (int c = 1; c <= 2; c++)
        {
            double weight = ChromaWeight(qp, c);
            for (int t = 0; t < (_chroma == 2 ? 2 : 1); t++)
            {
                BlockResult result = InterBlock(c, xC, yC + (t << log2C), log2C, qp, cu.Bypass, lambda, weight, rdoq);
                target.Levels[HevcTransformNode.Block(c, t)] = result.Levels;
                SetChromaFlag(node, c, t, result.Levels is not null);
                cost += result.Distortion + (lambda * result.Bits / Nal.CabacCost.Bit);
            }
        }

        return cost;
    }

    /// <summary>
    /// Transforms, quantizes and reconstructs one block's residual over the prediction in the picture; drops it when coding it costs
    /// more than leaving the prediction.
    /// </summary>
    private BlockResult InterBlock(int c, int x, int y, int log2, int qp, bool bypass, double lambda, double weight, bool rdoq)
    {
        if (!rdoq && !bypass && _interContext!.Profile.RdoqKeptOnly && _interContext.Profile.StaticTrials)
        {
            return TrialBlock(c, x, y, log2, qp, lambda, weight);
        }

        int n = 1 << log2, count = n * n;
        ushort[] plane = _picture.Planes[c], source = _source.Planes[c];
        int stride = _picture.Widths[c];
        int at = (y * stride) + x;
        for (int r = 0; r < n; r++)
        {
            for (int k = 0; k < n; k++)
            {
                int i = (r * n) + k;
                int p = plane[at + (r * stride) + k];
                _prediction[i] = (ushort)p;
                _residual[i] = source[at + (r * stride) + k] - p;
            }
        }

        if (bypass)
        {
            short[]? levels = null;
            for (int i = 0; i < count; i++)
            {
                if (_residual[i] != 0)
                {
                    levels ??= new short[count];
                    levels[i] = (short)_residual[i];
                }
            }

            CopyBlock(source, plane, at, stride, n, n);
            long bits = 0;
            if (levels is not null)
            {
                long saved = _bins.Cost;
                _bins.Cost = 0;
                _writer.Residual(levels, log2, c, 1, false, true);
                bits = _bins.Cost;
                _bins.Cost = saved;
            }

            return new BlockResult(levels, false, 0, bits);
        }

        long predicted = Sse(source, plane, at, stride, n, n);
        int bitDepth = c == 0 ? _sps.BitDepthLuma : _sps.BitDepthChroma;
        bool plain = _interContext!.B && !_interContext.Referenced && _interContext.Profile.PlainB;
        BlockResult result = Quantized(c, plane, source, at, stride, log2, 1, Qp(c, qp), bitDepth, false, lambda, weight, _levels, true, rdoq && !plain);
        if (result.Levels is not null && result.Distortion + (lambda * result.Bits / Nal.CabacCost.Bit) >= weight * predicted)
        {
            for (int r = 0; r < n; r++)
            {
                HevcCopy.Run(_prediction, r * n, plane, at + (r * stride), n);
            }

            return new BlockResult(null, false, weight * predicted, 0);
        }

        return result;
    }

    // ---------------------------------------------------------------- block maps

    /// <summary>Stores a prediction block's motion in the block maps, as the decoder does.</summary>
    private void StoreMotion(int xPb, int yPb, int nPbW, int nPbH, in HevcPredictionUnit pu)
    {
        HevcSliceRefs refs = _interContext!.Refs;
        int id0 = (pu.Flags & 1) != 0 ? refs.Lists[0][pu.RefIdx0].Id : -1;
        int id1 = (pu.Flags & 2) != 0 ? refs.Lists[1][pu.RefIdx1].Id : -1;
        int refIdx0 = (pu.Flags & 1) != 0 ? pu.RefIdx0 : -1, refIdx1 = (pu.Flags & 2) != 0 ? pu.RefIdx1 : -1;
        int mv0 = (pu.Flags & 1) != 0 ? pu.Mv0 : 0, mv1 = (pu.Flags & 2) != 0 ? pu.Mv1 : 0;
        int x4 = xPb >> 2, y4 = yPb >> 2, w4 = nPbW >> 2, h4 = nPbH >> 2, stride = _picture.Width4;
        for (int y = 0; y < h4; y++)
        {
            int at = ((y4 + y) * stride) + x4;
            _picture.PredFlags.AsSpan(at, w4).Fill((byte)pu.Flags);
            _picture.RefIdx[0].AsSpan(at, w4).Fill((sbyte)refIdx0);
            _picture.RefIdx[1].AsSpan(at, w4).Fill((sbyte)refIdx1);
            _picture.Mv[0].AsSpan(at, w4).Fill(mv0);
            _picture.Mv[1].AsSpan(at, w4).Fill(mv1);
            _picture.RefId[0].AsSpan(at, w4).Fill(id0);
            _picture.RefId[1].AsSpan(at, w4).Fill(id1);
        }
    }

    /// <summary>Fills the block maps of a decided inter unit as the decoder does (its motion is stored already).</summary>
    private void CommitInter(HevcCodingUnit cu)
    {
        int size = 1 << cu.Log2;
        FillBlocks(_picture.CtDepth, cu.X, cu.Y, size, size, (byte)cu.Depth);
        FillBlocks(_picture.IntraMode, cu.X, cu.Y, size, size, 1);
        FillBlocks(_picture.SkipFlags, cu.X, cu.Y, size, size, (byte)(cu.Skip ? 1 : 0));
        MarkTransformEdges(cu.X, cu.Y, size);
        for (int i = 0; i < cu.UnitCount; i++)
        {
            (int px, int py, int pw, int ph) = HevcCodingUnit.UnitRect(cu.X, cu.Y, cu.Log2, cu.PartMode, i);
            MarkPredictionEdges(px, py, pw, ph);
        }

        if (cu.RootCbf)
        {
            MarkTree(cu.Tree, cu.X, cu.Y, cu.Log2);
        }

        if (cu.Bypass)
        {
            FillFlags(cu.X, cu.Y, size, HevcPicture.Bypass);
        }

        FillBlocks(System.Runtime.CompilerServices.Unsafe.As<byte[]>(_picture.QpY), cu.X, cu.Y, size, size, (byte)(sbyte)cu.Qp);
    }

    /// <summary>Marks a prediction block's left and top edges for the deblocking filter.</summary>
    private void MarkPredictionEdges(int x0, int y0, int width, int height)
    {
        int x4 = x0 >> 2, y4 = y0 >> 2;
        int w4 = Math.Min((x0 + width + 3) >> 2, _picture.Width4) - x4, h4 = Math.Min((y0 + height + 3) >> 2, _picture.Height4) - y4;
        for (int y = 0; y < h4; y++)
        {
            _picture.Flags[((y4 + y) * _picture.Width4) + x4] |= HevcPicture.EdgeLeft;
        }

        for (int x = 0; x < w4; x++)
        {
            _picture.Flags[(y4 * _picture.Width4) + x4 + x] |= HevcPicture.EdgeTop;
        }
    }
}
