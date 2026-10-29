namespace Egami.Heic.Hevc;

/// <summary>
/// The inter coding units of P and B slices (H.265 7.3.8.5, 7.3.8.6, 7.3.8.9): their prediction units' merge and motion
/// vector syntax, the motion vectors (8.5.3.2: merge candidates, AMVP, temporal motion vector prediction) and the prediction.
/// </summary>
internal sealed partial class HevcSliceDecoder
{
    private const int Part2Nx2N = 0;
    private const int Part2NxN = 1;
    private const int PartNx2N = 2;
    private const int PartNxN = 3;
    private const int Part2NxnU = 4;
    private const int Part2NxnD = 5;
    private const int PartnLx2N = 6;
    private const int PartnRx2N = 7;

    private readonly HevcInter _inter = new();
    private readonly Candidate[] _merge = new Candidate[5];
    private HevcSliceRefs? _refs;
    private bool _cuIntra = true;
    private int _partMode;

    /// <summary>The motion of a prediction block or of a candidate: predFlagL0 and L1 in bits 0 and 1, reference indices, vectors.</summary>
    private struct Candidate
    {
        internal int Flags;
        internal int RefIdx0;
        internal int RefIdx1;
        internal int Mv0;
        internal int Mv1;

        internal readonly bool SameMotion(in Candidate other) =>
            Flags == other.Flags && ((Flags & 1) == 0 || (RefIdx0 == other.RefIdx0 && Mv0 == other.Mv0))
            && ((Flags & 2) == 0 || (RefIdx1 == other.RefIdx1 && Mv1 == other.Mv1));
    }

    /// <summary>cu_skip_flag (ctxInc from the skip flags left and above, 9.3.4.2.2).</summary>
    private bool SkipFlag(int x0, int y0)
    {
        int ctxInc = 0;
        if (_picture.Available(x0, y0, x0 - 1, y0) && _picture.SkipFlags[((y0 >> 2) * _picture.Width4) + ((x0 - 1) >> 2)] != 0)
        {
            ctxInc++;
        }

        if (_picture.Available(x0, y0, x0, y0 - 1) && _picture.SkipFlags[(((y0 - 1) >> 2) * _picture.Width4) + (x0 >> 2)] != 0)
        {
            ctxInc++;
        }

        return _cabac.Decode(ref _contexts[HevcContexts.CuSkipFlag + ctxInc]) == 1;
    }

    /// <summary>part_mode of an inter coding unit (Table 9-43, 9.3.4.2.2; the AMP bin's context is HM's, index 3).</summary>
    private int InterPartMode(int log2CbSize)
    {
        if (_cabac.Decode(ref _contexts[HevcContexts.PartMode]) == 1)
        {
            return Part2Nx2N;
        }

        bool horizontal = _cabac.Decode(ref _contexts[HevcContexts.PartMode + 1]) == 1;
        if (log2CbSize == _sps.MinCbLog2Size)
        {
            if (horizontal)
            {
                return Part2NxN;
            }

            if (log2CbSize == 3)
            {
                return PartNx2N;
            }

            return _cabac.Decode(ref _contexts[HevcContexts.PartMode + 2]) == 1 ? PartNx2N : PartNxN;
        }

        if (!_sps.AmpEnabled)
        {
            return horizontal ? Part2NxN : PartNx2N;
        }

        if (_cabac.Decode(ref _contexts[HevcContexts.PartMode + 3]) == 1)
        {
            return horizontal ? Part2NxN : PartNx2N;
        }

        int second = (int)_cabac.Bypass();
        return horizontal ? (second == 0 ? Part2NxnU : Part2NxnD) : (second == 0 ? PartnLx2N : PartnRx2N);
    }

    /// <summary>
    /// An inter coding unit after its pred_mode_flag (or a skipped one): its prediction units, their prediction, then
    /// rqt_root_cbf and the transform tree.
    /// </summary>
    private void InterCodingUnit(int x0, int y0, int log2CbSize, bool skip)
    {
        int nCbS = 1 << log2CbSize;
        _cuIntra = false;
        _intraSplit = false;
        _partMode = skip ? Part2Nx2N : InterPartMode(log2CbSize);
        FillBlocks(_picture.IntraMode, x0, y0, nCbS, nCbS, (byte)1);
        MarkTransformEdges(x0, y0, nCbS);
        bool mergeWhole = false;
        int half = nCbS >> 1, quarter = nCbS >> 2;
        switch (_partMode)
        {
            case Part2Nx2N:
                mergeWhole = PredictionUnit(x0, y0, nCbS, x0, y0, nCbS, nCbS, 0, skip);
                break;
            case Part2NxN:
                PredictionUnit(x0, y0, nCbS, x0, y0, nCbS, half, 0, false);
                PredictionUnit(x0, y0, nCbS, x0, y0 + half, nCbS, half, 1, false);
                break;
            case PartNx2N:
                PredictionUnit(x0, y0, nCbS, x0, y0, half, nCbS, 0, false);
                PredictionUnit(x0, y0, nCbS, x0 + half, y0, half, nCbS, 1, false);
                break;
            case Part2NxnU:
                PredictionUnit(x0, y0, nCbS, x0, y0, nCbS, quarter, 0, false);
                PredictionUnit(x0, y0, nCbS, x0, y0 + quarter, nCbS, nCbS * 3 / 4, 1, false);
                break;
            case Part2NxnD:
                PredictionUnit(x0, y0, nCbS, x0, y0, nCbS, nCbS * 3 / 4, 0, false);
                PredictionUnit(x0, y0, nCbS, x0, y0 + (nCbS * 3 / 4), nCbS, quarter, 1, false);
                break;
            case PartnLx2N:
                PredictionUnit(x0, y0, nCbS, x0, y0, quarter, nCbS, 0, false);
                PredictionUnit(x0, y0, nCbS, x0 + quarter, y0, nCbS * 3 / 4, nCbS, 1, false);
                break;
            case PartnRx2N:
                PredictionUnit(x0, y0, nCbS, x0, y0, nCbS * 3 / 4, nCbS, 0, false);
                PredictionUnit(x0, y0, nCbS, x0 + (nCbS * 3 / 4), y0, quarter, nCbS, 1, false);
                break;
            default:
                PredictionUnit(x0, y0, nCbS, x0, y0, half, half, 0, false);
                PredictionUnit(x0, y0, nCbS, x0 + half, y0, half, half, 1, false);
                PredictionUnit(x0, y0, nCbS, x0, y0 + half, half, half, 2, false);
                PredictionUnit(x0, y0, nCbS, x0 + half, y0 + half, half, half, 3, false);
                break;
        }

        if (skip)
        {
            FillBlocks(_picture.SkipFlags, x0, y0, nCbS, nCbS, (byte)1);
            return;
        }

        bool rqtRootCbf = (_partMode == Part2Nx2N && mergeWhole) || _cabac.Decode(ref _contexts[HevcContexts.RqtRootCbf]) == 1;
        if (rqtRootCbf)
        {
            TransformTree(x0, y0, x0, y0, log2CbSize, 0, 0, _sps.MaxTransformHierarchyDepthInter, true, true, true, true);
        }
    }

    /// <summary>
    /// prediction_unit (7.3.8.6) of partition partIdx, its motion (8.5.3.2) and its prediction (8.5.3.3); returns merge_flag.
    /// </summary>
    private bool PredictionUnit(int xCb, int yCb, int nCbS, int xPb, int yPb, int nPbW, int nPbH, int partIdx, bool skip)
    {
        MarkPredictionEdges(xPb, yPb, nPbW, nPbH);
        HevcSliceRefs refs = _refs!;
        Candidate motion;
        bool merge = skip || _cabac.Decode(ref _contexts[HevcContexts.MergeFlag]) == 1;
        if (merge)
        {
            int mergeIdx = 0;
            if (_slice.MaxNumMergeCand > 1)
            {
                // TR with cMax MaxNumMergeCand - 1: the first bin coded, the rest bypass.
                if (_cabac.Decode(ref _contexts[HevcContexts.MergeIdx]) == 1)
                {
                    mergeIdx = 1;
                    while (mergeIdx < _slice.MaxNumMergeCand - 1 && _cabac.Bypass() == 1)
                    {
                        mergeIdx++;
                    }
                }
            }

            motion = MergeMotion(xCb, yCb, nCbS, xPb, yPb, nPbW, nPbH, partIdx, mergeIdx);
        }
        else
        {
            int interPredIdc = 0;
            if (_slice.SliceType == HevcSliceHeader.B)
            {
                interPredIdc = InterPredIdc(xPb, yPb, nPbW, nPbH);
            }

            motion = default;
            Span<int> mvd = stackalloc int[2];
            Span<int> mvpFlag = stackalloc int[2];
            Span<int> refIdx = stackalloc int[2];
            for (int list = 0; list < 2; list++)
            {
                if (interPredIdc == (list == 0 ? 1 : 0))
                {
                    // PRED_L1 (1) has no list 0, PRED_L0 (0) no list 1; PRED_BI (2) both.
                    continue;
                }

                int count = list == 0 ? _slice.NumRefIdxL0 : _slice.NumRefIdxL1;
                refIdx[list] = count > 1 ? RefIdxSyntax(count - 1) : 0;
                mvd[list] = list == 1 && _slice.MvdL1Zero && interPredIdc == 2 ? 0 : MvdCoding();
                mvpFlag[list] = _cabac.Decode(ref _contexts[HevcContexts.MvpFlag]);
                motion.Flags |= 1 << list;
            }

            motion.RefIdx0 = refIdx[0];
            motion.RefIdx1 = refIdx[1];
            for (int list = 0; list < 2; list++)
            {
                if ((motion.Flags & (1 << list)) == 0)
                {
                    continue;
                }

                int mvp = Amvp(xCb, yCb, nCbS, xPb, yPb, nPbW, nPbH, partIdx, list, refIdx[list], mvpFlag[list]);

                // 8-272 to 8-275: each component wraps to 16 bits.
                int mv = Pack((short)((short)mvp + (short)mvd[list]), (short)((mvp >> 16) + (mvd[list] >> 16)));
                if (list == 0)
                {
                    motion.Mv0 = mv;
                }
                else
                {
                    motion.Mv1 = mv;
                }
            }
        }

        if ((motion.Flags & 1) == 0)
        {
            motion.RefIdx0 = -1;
            motion.Mv0 = 0;
        }

        if ((motion.Flags & 2) == 0)
        {
            motion.RefIdx1 = -1;
            motion.Mv1 = 0;
        }

        StoreMotion(xPb, yPb, nPbW, nPbH, motion, refs);
        if (Environment.GetEnvironmentVariable("ZZ_PU") is { })
        {
            System.Console.Error.WriteLine($"PU {refs.Poc} {xPb} {yPb} part {partIdx} dir {motion.Flags} r0 {motion.RefIdx0} mv0 {(short)motion.Mv0} {motion.Mv0 >> 16} r1 {motion.RefIdx1} mv1 {(short)motion.Mv1} {motion.Mv1 >> 16} merge {(skip ? 2 : merge ? 1 : 0)}");
        }

        _inter.Predict(_picture, refs, _slice, xPb, yPb, nPbW, nPbH, motion.Flags, motion.RefIdx0, motion.RefIdx1, motion.Mv0, motion.Mv1);
        return merge;
    }

    private static int Pack(int x, int y) => (x & 0xFFFF) | (y << 16);

    /// <summary>inter_pred_idc (9.3.4.2.2): 0 PRED_L0, 1 PRED_L1, 2 PRED_BI; 8x4 and 4x8 blocks cannot be bi-predicted.</summary>
    private int InterPredIdc(int xPb, int yPb, int nPbW, int nPbH)
    {
        if (nPbW + nPbH != 12)
        {
            int depth = _picture.CtDepth[((yPb >> 2) * _picture.Width4) + (xPb >> 2)];
            if (_cabac.Decode(ref _contexts[HevcContexts.InterPredIdc + depth]) == 1)
            {
                return 2;
            }
        }

        return _cabac.Decode(ref _contexts[HevcContexts.InterPredIdc + 4]);
    }

    /// <summary>ref_idx_lX: TR with cMax num_ref_idx_active_minus1, two coded bins then bypass ones.</summary>
    private int RefIdxSyntax(int cMax)
    {
        int value = 0;
        while (value < cMax)
        {
            int bin = value < 2 ? _cabac.Decode(ref _contexts[HevcContexts.RefIdx + value]) : (int)_cabac.Bypass();
            if (bin == 0)
            {
                break;
            }

            value++;
        }

        return value;
    }

    /// <summary>mvd_coding (7.3.8.9): the two components, packed.</summary>
    private int MvdCoding()
    {
        bool gr0X = _cabac.Decode(ref _contexts[HevcContexts.AbsMvdGreater0]) == 1;
        bool gr0Y = _cabac.Decode(ref _contexts[HevcContexts.AbsMvdGreater0]) == 1;
        bool gr1X = gr0X && _cabac.Decode(ref _contexts[HevcContexts.AbsMvdGreater1]) == 1;
        bool gr1Y = gr0Y && _cabac.Decode(ref _contexts[HevcContexts.AbsMvdGreater1]) == 1;
        int x = MvdComponent(gr0X, gr1X), y = MvdComponent(gr0Y, gr1Y);
        return Pack(x, y);
    }

    private int MvdComponent(bool greater0, bool greater1)
    {
        if (!greater0)
        {
            return 0;
        }

        int abs = 1;
        if (greater1)
        {
            // abs_mvd_minus2: first-order Exp-Golomb, bypass coded.
            int k = 1, value = 0;
            while (_cabac.Bypass() == 1)
            {
                value += 1 << k;
                if (++k > 31)
                {
                    throw HevcErrors.Corrupt("abs_mvd_minus2 is too long.");
                }
            }

            value += (int)_cabac.Bypass(k);
            abs = value + 2;
            if (abs > 32768)
            {
                throw HevcErrors.Corrupt($"A motion vector difference of {abs} is out of range.");
            }
        }

        return _cabac.Bypass() == 1 ? -abs : abs;
    }

    /// <summary>Stores a prediction block's motion for the blocks that follow, the deblocking filter and later pictures.</summary>
    private void StoreMotion(int xPb, int yPb, int nPbW, int nPbH, in Candidate motion, HevcSliceRefs refs)
    {
        int id0 = (motion.Flags & 1) != 0 ? refs.Lists[0][motion.RefIdx0].Id : -1;
        int id1 = (motion.Flags & 2) != 0 ? refs.Lists[1][motion.RefIdx1].Id : -1;
        int x4 = xPb >> 2, y4 = yPb >> 2, w4 = nPbW >> 2, h4 = nPbH >> 2, stride = _picture.Width4;
        for (int y = 0; y < h4; y++)
        {
            int at = ((y4 + y) * stride) + x4;
            _picture.PredFlags.AsSpan(at, w4).Fill((byte)motion.Flags);
            _picture.RefIdx[0].AsSpan(at, w4).Fill((sbyte)motion.RefIdx0);
            _picture.RefIdx[1].AsSpan(at, w4).Fill((sbyte)motion.RefIdx1);
            _picture.Mv[0].AsSpan(at, w4).Fill(motion.Mv0);
            _picture.Mv[1].AsSpan(at, w4).Fill(motion.Mv1);
            _picture.RefId[0].AsSpan(at, w4).Fill(id0);
            _picture.RefId[1].AsSpan(at, w4).Fill(id1);
        }
    }

    /// <summary>The motion of the block covering (x, y), which must lie in the picture.</summary>
    private Candidate MotionAt(int x, int y)
    {
        int b = ((y >> 2) * _picture.Width4) + (x >> 2);
        return new Candidate
        {
            Flags = _picture.PredFlags[b],
            RefIdx0 = _picture.RefIdx[0][b],
            RefIdx1 = _picture.RefIdx[1][b],
            Mv0 = _picture.Mv[0][b],
            Mv1 = _picture.Mv[1][b],
        };
    }

    /// <summary>
    /// The availability of a neighbouring prediction block (6.4.2): z-scan availability, not the later partition of an NxN
    /// coding unit's second one, and not intra.
    /// </summary>
    private bool PbAvailable(int xCb, int yCb, int nCbS, int xPb, int yPb, int nPbW, int nPbH, int partIdx, int xN, int yN)
    {
        bool sameCb = xCb <= xN && yCb <= yN && xCb + nCbS > xN && yCb + nCbS > yN;
        bool available;
        if (!sameCb)
        {
            available = _picture.Available(xPb, yPb, xN, yN);
        }
        else
        {
            // Inside the coding unit: available unless it is the NxN partition 1's neighbour in partition 2.
            available = !((nPbW << 1) == nCbS && (nPbH << 1) == nCbS && partIdx == 1 && yCb + nPbH <= yN && xCb + nPbW > xN);
        }

        return available && _picture.PredFlags[((yN >> 2) * _picture.Width4) + (xN >> 2)] != 0;
    }

    // ---------------------------------------------------------------- merge mode (8.5.3.2.2 to 8.5.3.2.5)

    private Candidate MergeMotion(int xCb, int yCb, int nCbS, int xPb, int yPb, int nPbW, int nPbH, int partIdx, int mergeIdx)
    {
        int origW = nPbW, origH = nPbH;
        if (_pps.Log2ParMrgLevel > 2 && nCbS == 8)
        {
            // singleMCLFlag: every partition of an 8x8 coding unit shares the 2Nx2N candidate list.
            (xPb, yPb, nPbW, nPbH, partIdx) = (xCb, yCb, nCbS, nCbS, 0);
        }

        Span<Candidate> list = stackalloc Candidate[5];
        int count = 0;
        int level = _pps.Log2ParMrgLevel;
        bool singleMcl = _pps.Log2ParMrgLevel > 2 && nCbS == 8;
        int partMode = singleMcl ? Part2Nx2N : _partMode;

        // Spatial candidates A1, B1, B0, A0 and B2 (8.5.3.2.3).
        int xA1 = xPb - 1, yA1 = yPb + nPbH - 1;
        bool availableA1 = !SameMergeRegion(xPb, yPb, xA1, yA1, level)
            && !(partMode is PartNx2N or PartnLx2N or PartnRx2N && partIdx == 1)
            && PbAvailable(xCb, yCb, nCbS, xPb, yPb, nPbW, nPbH, partIdx, xA1, yA1);
        Candidate a1 = availableA1 ? MotionAt(xA1, yA1) : default;
        if (availableA1)
        {
            list[count++] = a1;
        }

        int xB1 = xPb + nPbW - 1, yB1 = yPb - 1;
        bool availableB1 = !SameMergeRegion(xPb, yPb, xB1, yB1, level)
            && !(partMode is Part2NxN or Part2NxnU or Part2NxnD && partIdx == 1)
            && PbAvailable(xCb, yCb, nCbS, xPb, yPb, nPbW, nPbH, partIdx, xB1, yB1);
        // The comparisons are with the neighbours that are available (6.4.2), whether or not they joined the list.
        Candidate b1 = availableB1 ? MotionAt(xB1, yB1) : default;
        if (availableB1 && !(availableA1 && a1.SameMotion(b1)))
        {
            list[count++] = b1;
        }

        int xB0 = xPb + nPbW, yB0 = yPb - 1;
        bool availableB0 = !SameMergeRegion(xPb, yPb, xB0, yB0, level) && PbAvailable(xCb, yCb, nCbS, xPb, yPb, nPbW, nPbH, partIdx, xB0, yB0);
        if (availableB0)
        {
            Candidate b0 = MotionAt(xB0, yB0);
            if (!(availableB1 && b1.SameMotion(b0)))
            {
                list[count++] = b0;
            }
        }

        int xA0 = xPb - 1, yA0 = yPb + nPbH;
        bool availableA0 = !SameMergeRegion(xPb, yPb, xA0, yA0, level) && PbAvailable(xCb, yCb, nCbS, xPb, yPb, nPbW, nPbH, partIdx, xA0, yA0);
        if (availableA0)
        {
            Candidate a0 = MotionAt(xA0, yA0);
            if (!(availableA1 && a1.SameMotion(a0)))
            {
                list[count++] = a0;
            }
        }

        if (count < 4)
        {
            int xB2 = xPb - 1, yB2 = yPb - 1;
            if (!SameMergeRegion(xPb, yPb, xB2, yB2, level) && PbAvailable(xCb, yCb, nCbS, xPb, yPb, nPbW, nPbH, partIdx, xB2, yB2))
            {
                Candidate b2 = MotionAt(xB2, yB2);
                if (!(availableA1 && a1.SameMotion(b2)) && !(availableB1 && b1.SameMotion(b2)))
                {
                    list[count++] = b2;
                }
            }
        }

        // The temporal candidate (8.5.3.2.2 with refIdxLXCol 0).
        if (count < _slice.MaxNumMergeCand && _slice.TemporalMvp)
        {
            Candidate col = default;
            if (TemporalMv(xPb, yPb, nPbW, nPbH, 0, 0, out int mv0))
            {
                col.Flags |= 1;
                col.Mv0 = mv0;
            }

            if (_slice.SliceType == HevcSliceHeader.B && TemporalMv(xPb, yPb, nPbW, nPbH, 1, 0, out int mv1))
            {
                col.Flags |= 2;
                col.Mv1 = mv1;
            }

            if (col.Flags != 0)
            {
                list[count++] = col;
            }
        }

        // Combined bi-predictive candidates (8.5.3.2.4).
        int origCount = count;
        if (_slice.SliceType == HevcSliceHeader.B && origCount > 1 && origCount < _slice.MaxNumMergeCand)
        {
            ReadOnlySpan<byte> l0 = [0, 1, 0, 2, 1, 2, 0, 3, 1, 3, 2, 3];
            ReadOnlySpan<byte> l1 = [1, 0, 2, 0, 2, 1, 3, 0, 3, 1, 3, 2];
            for (int combIdx = 0; combIdx < origCount * (origCount - 1) && count < _slice.MaxNumMergeCand; combIdx++)
            {
                Candidate c0 = list[l0[combIdx]], c1 = list[l1[combIdx]];
                if ((c0.Flags & 1) != 0 && (c1.Flags & 2) != 0
                    && (_refs!.Lists[0][c0.RefIdx0].Poc != _refs.Lists[1][c1.RefIdx1].Poc || _refs.Lists[0][c0.RefIdx0] != _refs.Lists[1][c1.RefIdx1] || c0.Mv0 != c1.Mv1))
                {
                    list[count++] = new Candidate { Flags = 3, RefIdx0 = c0.RefIdx0, Mv0 = c0.Mv0, RefIdx1 = c1.RefIdx1, Mv1 = c1.Mv1 };
                }
            }
        }

        // Zero candidates (8.5.3.2.5).
        int numRefIdx = _slice.SliceType == HevcSliceHeader.P ? _slice.NumRefIdxL0 : Math.Min(_slice.NumRefIdxL0, _slice.NumRefIdxL1);
        for (int zeroIdx = 0; count < _slice.MaxNumMergeCand; zeroIdx++)
        {
            int r = zeroIdx < numRefIdx ? zeroIdx : 0;
            list[count++] = new Candidate { Flags = _slice.SliceType == HevcSliceHeader.P ? 1 : 3, RefIdx0 = r, RefIdx1 = _slice.SliceType == HevcSliceHeader.P ? -1 : r };
        }

        Candidate chosen = list[mergeIdx];
        if (Environment.GetEnvironmentVariable("ZZ_HEVC") is { } zz && xPb >= 128 && xPb < 160 && yPb >= 320 && yPb < 352)
        {
            System.Console.Error.WriteLine($"ZZ merge pb {xPb},{yPb} {nPbW}x{nPbH} part {partIdx} pm {_partMode} idx {mergeIdx} orig {origCount} avail A1 {availableA1} B1 {availableB1} B0 {availableB0} A0 {availableA0}");
            for (int i = 0; i < count; i++)
            {
                Candidate c = list[i];
                System.Console.Error.WriteLine($"ZZ   cand {i}: f{c.Flags} r{c.RefIdx0},{c.RefIdx1} mv0 {(short)c.Mv0},{c.Mv0 >> 16} mv1 {(short)c.Mv1},{c.Mv1 >> 16}");
            }
        }

        if (chosen.Flags == 3 && origW + origH == 12)
        {
            // 8x4 and 4x8 blocks predict from list 0 only (8-??).
            chosen.Flags = 1;
            chosen.RefIdx1 = -1;
            chosen.Mv1 = 0;
        }

        return chosen;
    }

    private static bool SameMergeRegion(int xPb, int yPb, int xN, int yN, int level) =>
        xPb >> level == xN >> level && yPb >> level == yN >> level;

    // ---------------------------------------------------------------- AMVP (8.5.3.2.6, 8.5.3.2.7)

    /// <summary>mvpLX: the predictor mvp_lX_flag picks from the spatial, temporal and zero candidates.</summary>
    private int Amvp(int xCb, int yCb, int nCbS, int xPb, int yPb, int nPbW, int nPbH, int partIdx, int list, int refIdx, int mvpFlag)
    {
        HevcSliceRefs refs = _refs!;
        HevcDpbPicture target = refs.Lists[list][refIdx];
        bool targetLong = refs.LongTerm[list][refIdx];

        // A0 and A1.
        int xA0 = xPb - 1, yA0 = yPb + nPbH, xA1 = xPb - 1, yA1 = yPb + nPbH - 1;
        bool availableA0 = PbAvailable(xCb, yCb, nCbS, xPb, yPb, nPbW, nPbH, partIdx, xA0, yA0);
        bool availableA1 = PbAvailable(xCb, yCb, nCbS, xPb, yPb, nPbW, nPbH, partIdx, xA1, yA1);
        bool isScaled = availableA0 || availableA1;
        bool availableA = false;
        int mvA = 0;
        for (int k = 0; k < 2 && !availableA; k++)
        {
            bool available = k == 0 ? availableA0 : availableA1;
            if (available)
            {
                availableA = SameReference(MotionAt(k == 0 ? xA0 : xA1, k == 0 ? yA0 : yA1), list, target, out mvA);
            }
        }

        for (int k = 0; k < 2 && !availableA; k++)
        {
            bool available = k == 0 ? availableA0 : availableA1;
            if (available)
            {
                availableA = ScaledReference(MotionAt(k == 0 ? xA0 : xA1, k == 0 ? yA0 : yA1), list, target, targetLong, out mvA);
            }
        }

        // B0, B1 and B2.
        Span<int> xB = [xPb + nPbW, xPb + nPbW - 1, xPb - 1];
        int yB = yPb - 1;
        Span<bool> availableBk = stackalloc bool[3];
        for (int k = 0; k < 3; k++)
        {
            availableBk[k] = PbAvailable(xCb, yCb, nCbS, xPb, yPb, nPbW, nPbH, partIdx, xB[k], yB);
        }

        bool availableB = false;
        int mvB = 0;
        for (int k = 0; k < 3 && !availableB; k++)
        {
            if (availableBk[k])
            {
                availableB = SameReference(MotionAt(xB[k], yB), list, target, out mvB);
            }
        }

        if (!isScaled && availableB)
        {
            availableA = true;
            mvA = mvB;
        }

        if (!isScaled)
        {
            availableB = false;
            for (int k = 0; k < 3 && !availableB; k++)
            {
                if (availableBk[k])
                {
                    availableB = ScaledReference(MotionAt(xB[k], yB), list, target, targetLong, out mvB);
                }
            }
        }

        // The list (8.5.3.2.6): A, B unless equal to A, then the temporal candidate, then zeros.
        Span<int> mvp = stackalloc int[2];
        int count = 0;
        if (availableA)
        {
            mvp[count++] = mvA;
        }

        if (availableB && !(availableA && mvA == mvB))
        {
            mvp[count++] = mvB;
        }

        if (count < 2 && _slice.TemporalMvp && TemporalMv(xPb, yPb, nPbW, nPbH, list, refIdx, out int mvCol))
        {
            mvp[count++] = mvCol;
        }

        if (Environment.GetEnvironmentVariable("ZZ_HEVC") is { } && xPb >= 128 && xPb < 160 && yPb >= 320 && yPb < 352)
        {
            System.Console.Error.WriteLine($"ZZ amvp pb {xPb},{yPb} {nPbW}x{nPbH} part {partIdx} list {list} ref {refIdx} flag {mvpFlag} A {availableA} {(short)mvA},{mvA >> 16} B {availableB} {(short)mvB},{mvB >> 16} isScaled {isScaled} count {count} -> {(short)mvp[0]},{mvp[0] >> 16} / {(short)mvp[1]},{mvp[1] >> 16}");
        }

        return mvpFlag < count ? mvp[mvpFlag] : 0;
    }

    /// <summary>A neighbour predicting from the same picture in list X, else in list Y (8.5.3.2.7's first passes): its vector.</summary>
    private bool SameReference(in Candidate n, int list, HevcDpbPicture target, out int mv)
    {
        HevcSliceRefs refs = _refs!;
        for (int i = 0; i < 2; i++)
        {
            int l = i == 0 ? list : 1 - list;
            if ((n.Flags & (1 << l)) != 0 && refs.Lists[l][l == 0 ? n.RefIdx0 : n.RefIdx1] == target)
            {
                mv = l == 0 ? n.Mv0 : n.Mv1;
                return true;
            }
        }

        mv = 0;
        return false;
    }

    /// <summary>
    /// A neighbour predicting from a picture of the same long-term marking in list X, else list Y, its vector scaled by the order
    /// count distances when both pictures are short-term (8.5.3.2.7's later passes, 8-?).
    /// </summary>
    private bool ScaledReference(in Candidate n, int list, HevcDpbPicture target, bool targetLong, out int mv)
    {
        HevcSliceRefs refs = _refs!;
        for (int i = 0; i < 2; i++)
        {
            int l = i == 0 ? list : 1 - list;
            if ((n.Flags & (1 << l)) == 0)
            {
                continue;
            }

            int r = l == 0 ? n.RefIdx0 : n.RefIdx1;
            bool neighbourLong = refs.LongTerm[l][r];
            if (neighbourLong != targetLong)
            {
                continue;
            }

            mv = l == 0 ? n.Mv0 : n.Mv1;
            if (!targetLong)
            {
                mv = Scale(mv, refs.Poc - refs.Lists[l][r].Poc, refs.Poc - target.Poc);
            }

            return true;
        }

        mv = 0;
        return false;
    }

    /// <summary>Scales a vector by tb / td (8-? to 8-?), both clipped to -128..127, each component clipped to 16 bits.</summary>
    private static int Scale(int mv, int td, int tb)
    {
        td = Math.Clamp(td, -128, 127);
        tb = Math.Clamp(tb, -128, 127);
        if (td == 0 || td == tb)
        {
            return mv;
        }

        int tx = (16384 + (Math.Abs(td) >> 1)) / td;
        int distScaleFactor = Math.Clamp(((tb * tx) + 32) >> 6, -4096, 4095);
        return Pack(ScaleComponent((short)mv, distScaleFactor), ScaleComponent(mv >> 16, distScaleFactor));
    }

    private static int ScaleComponent(int v, int factor)
    {
        int product = factor * v;
        return Math.Clamp(Math.Sign(product) * ((Math.Abs(product) + 127) >> 8), -32768, 32767);
    }

    // ---------------------------------------------------------------- temporal motion vector prediction (8.5.3.2.8, 8.5.3.2.9)

    /// <summary>mvLXCol for list X and refIdxLX: from the collocated picture's block below right of the prediction block, else its centre.</summary>
    private bool TemporalMv(int xPb, int yPb, int nPbW, int nPbH, int list, int refIdx, out int mv)
    {
        mv = 0;
        HevcSliceRefs refs = _refs!;
        HevcDpbPicture[] colList = refs.Lists[_slice.SliceType == HevcSliceHeader.B && !_slice.CollocatedFromL0 ? 1 : 0];
        if (_slice.CollocatedRefIdx >= colList.Length)
        {
            return false;
        }

        HevcDpbPicture colPic = colList[_slice.CollocatedRefIdx];
        HevcColMotion? col = colPic.Picture.ColMotion;
        if (col is null)
        {
            return false;
        }

        int xBr = xPb + nPbW, yBr = yPb + nPbH;
        if (yPb >> _sps.CtbLog2Size == yBr >> _sps.CtbLog2Size && yBr < _sps.Height && xBr < _sps.Width
            && Collocated(col, colPic, xBr, yBr, list, refIdx, out mv))
        {
            return true;
        }

        return Collocated(col, colPic, xPb + (nPbW >> 1), yPb + (nPbH >> 1), list, refIdx, out mv);
    }

    /// <summary>The collocated motion vectors (8.5.3.2.9) at the 16x16 block covering (x, y).</summary>
    private bool Collocated(HevcColMotion col, HevcDpbPicture colPic, int x, int y, int list, int refIdx, out int mv)
    {
        mv = 0;
        int i = ((y >> 4) * col.Width16) + (x >> 4);
        int flags = col.PredFlags[i];
        if (flags == 0)
        {
            return false;
        }

        int listCol;
        if ((flags & 1) == 0)
        {
            listCol = 1;
        }
        else if (flags == 1)
        {
            listCol = 0;
        }
        else
        {
            // Both: with no reference after the current picture, the list being derived; else the one collocated_from_l0_flag names.
            listCol = NoBackwardPrediction() ? list : (_slice.CollocatedFromL0 ? 1 : 0);
        }

        HevcSliceRefs refs = _refs!;
        bool targetLong = refs.LongTerm[list][refIdx];
        if (col.LongTerm[listCol][i] != targetLong)
        {
            return false;
        }

        mv = col.Mv[listCol][i];
        int colPocDiff = colPic.Poc - col.RefPoc[listCol][i];
        int currPocDiff = refs.Poc - refs.Lists[list][refIdx].Poc;
        if (!targetLong && colPocDiff != currPocDiff)
        {
            mv = Scale(mv, colPocDiff, currPocDiff);
        }

        return true;
    }

    /// <summary>NoBackwardPredFlag: no picture of either list follows the current one in order count.</summary>
    private bool NoBackwardPrediction()
    {
        HevcSliceRefs refs = _refs!;
        foreach (HevcDpbPicture[] l in refs.Lists)
        {
            foreach (HevcDpbPicture p in l)
            {
                if (p.Poc > refs.Poc)
                {
                    return false;
                }
            }
        }

        return true;
    }
}
