namespace Egami.Formats.Av1;

/// <summary>
/// The mode info of inter frames: segments from the previous frame, skip mode, intra and inter blocks, reference frames, the
/// reference vector stack with temporal candidates, vectors, motion modes, compound types and interpolation filters, as libaom's
/// decodemv.c, mvref_common.c and pred_common.c read and derive them (BSD 2-clause, see THIRD-PARTY-NOTICES).
/// </summary>
internal sealed partial class Av1TileParser
{
    private const int RefCatLevel = 640;
    private const int MaxRefMvStack = 8;
    private const int MvLow = -(1 << 14), MvUpp = 1 << 14;

    private static readonly byte[][] CompoundModeContexts = [[0, 1, 1, 1, 1], [1, 2, 3, 4, 4], [4, 4, 5, 6, 7]];

    // The reference vector stack of the block's reference frame (or pair).
    private readonly int[] _mvRow0 = new int[MaxRefMvStack], _mvCol0 = new int[MaxRefMvStack];
    private readonly int[] _mvRow1 = new int[MaxRefMvStack], _mvCol1 = new int[MaxRefMvStack];
    private readonly int[] _mvWeight = new int[MaxRefMvStack];
    private readonly int[] _neighborRefCounts = new int[8];
    private readonly int[] _samples = new int[2 * 8], _samplesInRef = new int[2 * 8];
    private int _mvCount, _modeContext;
    private int _gmRow0, _gmCol0, _gmRow1, _gmCol1;

    private void ReadInterFrameModeInfo(int xMis, int yMis)
    {
        Av1BlockInfo b = _b;
        b.SegmentId = ReadInterSegmentId(true, xMis, yMis);
        b.SkipMode = ReadSkipMode(b.SegmentId);
        b.Skip = b.SkipMode || ReadSkip(b.SegmentId);
        if (!_header.SegIdPreSkip)
        {
            b.SegmentId = ReadInterSegmentId(false, xMis, yMis);
        }

        ReadCdef();
        ReadDeltaQParams();
        bool inter = b.SkipMode || ReadIsInter(b.SegmentId);
        b.QIndex = _currentQIndex;
        if (inter)
        {
            ReadInterBlockModeInfo();
        }
        else
        {
            ReadIntraBlockModeInfo();
        }
    }

    private int ReadInterSegmentId(bool preskip, int xMis, int yMis)
    {
        if (!_header.SegmentationEnabled)
        {
            return 0;
        }

        byte[] map = _frame.SegmentMap;
        byte[]? previous = _frame.PreviousSegmentMap;
        if (!_header.SegmentationUpdateMap)
        {
            // copy_segment_id: the previous frame's map, or zeros.
            for (int y = 0; y < yMis; y++)
            {
                int at = ((_miRow + y) * _miCols) + _miCol;
                if (previous is null)
                {
                    map.AsSpan(at, xMis).Clear();
                }
                else
                {
                    previous.AsSpan(at, xMis).CopyTo(map.AsSpan(at));
                }
            }

            return PredictedSegmentId(previous, xMis, yMis);
        }

        int segment;
        if (preskip)
        {
            if (!_header.SegIdPreSkip)
            {
                return 0;
            }
        }
        else if (_b.Skip)
        {
            _b.SegIdPredicted = false;
            segment = SegmentPrediction().Pred;
            FillSegment(map, segment, xMis, yMis);
            return segment;
        }

        if (_header.SegmentationTemporalUpdate)
        {
            int ctx = (_above?.SegIdPredicted == true ? 1 : 0) + (_left?.SegIdPredicted == true ? 1 : 0);
            _b.SegIdPredicted = Symbol(Av1FrameContext.SegmentPred[ctx], 2) != 0;
            segment = _b.SegIdPredicted ? PredictedSegmentId(previous, xMis, yMis) : ReadSpatialSegmentId();
        }
        else
        {
            segment = ReadSpatialSegmentId();
        }

        FillSegment(map, segment, xMis, yMis);
        return segment;
    }

    private int ReadSpatialSegmentId()
    {
        (int cdfIndex, int pred) = SegmentPrediction();
        int coded = Symbol(Av1FrameContext.SpatialPredSegTree[cdfIndex], 8);
        int segment = NegDeinterleave(coded, pred, _header.LastActiveSegId + 1);

        // libaom rejects a segment past the last active one; dav1d, whose output the gates hold to, takes segment 0.
        return segment < 0 || segment > _header.LastActiveSegId ? 0 : segment;
    }

    private int PredictedSegmentId(byte[]? previous, int xMis, int yMis)
    {
        if (previous is null)
        {
            return 0;
        }

        int segment = 7;
        for (int y = 0; y < yMis; y++)
        {
            for (int x = 0; x < xMis; x++)
            {
                segment = Math.Min(segment, previous[((_miRow + y) * _miCols) + _miCol + x]);
            }
        }

        return segment;
    }

    private void FillSegment(byte[] map, int segment, int xMis, int yMis)
    {
        for (int y = 0; y < yMis; y++)
        {
            map.AsSpan(((_miRow + y) * _miCols) + _miCol, xMis).Fill((byte)segment);
        }
    }

    private bool ReadSkipMode(int segment)
    {
        if (!_header.SkipModePresent || SegFeatureActive(segment, 6) || !CompRefAllowed(_b.Size)
            || SegFeatureActive(segment, 5) || SegFeatureActive(segment, 7))
        {
            return false;
        }

        int ctx = (_above?.SkipMode == true ? 1 : 0) + (_left?.SkipMode == true ? 1 : 0);
        return Symbol(Av1FrameContext.SkipMode[ctx], 2) != 0;
    }

    private bool ReadIsInter(int segment)
    {
        if (SegFeatureActive(segment, 5))
        {
            return _header.FeatureData[segment, 5] != Ref.Intra;
        }

        if (SegFeatureActive(segment, 7))
        {
            return true;
        }

        int ctx;
        if (_upAvailable && _leftAvailable)
        {
            bool aboveIntra = !_above!.IsInter, leftIntra = !_left!.IsInter;
            ctx = aboveIntra && leftIntra ? 3 : aboveIntra || leftIntra ? 1 : 0;
        }
        else if (_upAvailable || _leftAvailable)
        {
            ctx = 2 * ((_upAvailable ? _above! : _left!).IsInter ? 0 : 1);
        }
        else
        {
            ctx = 0;
        }

        return Symbol(Av1FrameContext.IntraInter[ctx], 2) != 0;
    }

    /// <summary>read_intra_block_mode_info: an intra block of an inter frame.</summary>
    private void ReadIntraBlockModeInfo()
    {
        Av1BlockInfo b = _b;
        b.RefFrame0 = Ref.Intra;
        b.RefFrame1 = Ref.None;
        b.YMode = Symbol(Av1FrameContext.IfYMode[Av1Tables.SizeGroupLookup[b.Size]], 13);
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

    private static bool CompRefAllowed(int bsize) => Math.Min(Av1Tables.BlockSizeWide[bsize], Av1Tables.BlockSizeHigh[bsize]) >= 8;

    private static bool WedgeUsed(int bsize) => bsize is Bs.B8x8 or Bs.B8x16 or Bs.B16x8 or Bs.B16x16 or Bs.B16x32 or Bs.B32x16 or Bs.B32x32 or Bs.B8x32 or Bs.B32x8;

    /// <summary>read_inter_block_mode_info.</summary>
    private void ReadInterBlockModeInfo()
    {
        Av1BlockInfo b = _b;
        int bsize = b.Size;
        b.UvMode = Pred.Dc;
        CollectNeighborRefCounts();
        ReadRefFrames();
        bool compound = b.HasSecondRef;
        FindMvRefs();

        int refMvIdx = 0;
        if (b.SkipMode)
        {
            b.YMode = InterMode.NearestNearest;
        }
        else if (SegFeatureActive(b.SegmentId, 6) || SegFeatureActive(b.SegmentId, 7))
        {
            b.YMode = InterMode.Global;
        }
        else
        {
            int modeCtx = _modeContext;
            if (compound)
            {
                int newCtx = modeCtx & 7, refCtx = (modeCtx >> 4) & 15;
                modeCtx = CompoundModeContexts[refCtx >> 1][Math.Min(newCtx, 4)];
                b.YMode = InterMode.NearestNearest + Symbol(Av1FrameContext.InterCompoundMode[modeCtx], 8);
            }
            else if (Symbol(Av1FrameContext.Newmv[modeCtx & 7], 2) == 0)
            {
                b.YMode = InterMode.New;
            }
            else if (Symbol(Av1FrameContext.Zeromv[(modeCtx >> 3) & 1], 2) == 0)
            {
                b.YMode = InterMode.Global;
            }
            else
            {
                b.YMode = Symbol(Av1FrameContext.Refmv[(modeCtx >> 4) & 15], 2) == 0 ? InterMode.Nearest : InterMode.Near;
            }

            if (b.YMode is InterMode.New or InterMode.NewNew)
            {
                for (int idx = 0; idx < 2; idx++)
                {
                    if (_mvCount > idx + 1)
                    {
                        int drl = Symbol(Av1FrameContext.Drl[DrlContext(idx)], 2);
                        refMvIdx = idx + drl;
                        if (drl == 0)
                        {
                            break;
                        }
                    }
                }
            }
            else if (InterMode.HasNear(b.YMode))
            {
                for (int idx = 1; idx < 3; idx++)
                {
                    if (_mvCount > idx + 1)
                    {
                        int drl = Symbol(Av1FrameContext.Drl[DrlContext(idx)], 2);
                        refMvIdx = idx + drl - 1;
                        if (drl == 0)
                        {
                            break;
                        }
                    }
                }
            }
        }

        if (compound != InterMode.IsCompound(b.YMode))
        {
            throw Corrupt("An AV1 block's prediction mode does not match its number of references.");
        }

        bool hp = _header.AllowHighPrecisionMv, integer = _header.ForceIntegerMv;
        int nearestRow0 = 0, nearestCol0 = 0, nearestRow1 = 0, nearestCol1 = 0, nearRow0 = 0, nearCol0 = 0, nearRow1 = 0, nearCol1 = 0;
        if (!compound && b.YMode != InterMode.Global)
        {
            // av1_find_best_ref_mvs over the stack's first two (the global vector past its end).
            (nearestRow0, nearestCol0) = Lower(StackOrGlobal(0), hp, integer);
            (nearRow0, nearCol0) = Lower(StackOrGlobal(1), hp, integer);
        }

        if (compound && b.YMode != InterMode.GlobalGlobal)
        {
            int idx = refMvIdx + 1;
            (nearestRow0, nearestCol0) = Lower((_mvRow0[0], _mvCol0[0]), hp, integer);
            (nearestRow1, nearestCol1) = Lower((_mvRow1[0], _mvCol1[0]), hp, integer);
            (nearRow0, nearCol0) = Lower((_mvRow0[idx], _mvCol0[idx]), hp, integer);
            (nearRow1, nearCol1) = Lower((_mvRow1[idx], _mvCol1[idx]), hp, integer);
        }
        else if (refMvIdx > 0 && b.YMode == InterMode.Near)
        {
            // Not lowered, as libaom leaves it.
            (nearRow0, nearCol0) = (_mvRow0[1 + refMvIdx], _mvCol0[1 + refMvIdx]);
        }

        (int refRow0, int refCol0, int refRow1, int refCol1) = (nearestRow0, nearestCol0, nearestRow1, nearestCol1);
        if (compound)
        {
            int idx = b.YMode is InterMode.NearNew or InterMode.NewNear ? 1 + refMvIdx : refMvIdx;
            if (InterMode.First(b.YMode) == InterMode.New)
            {
                (refRow0, refCol0) = (_mvRow0[idx], _mvCol0[idx]);
            }

            if (InterMode.Second(b.YMode) == InterMode.New)
            {
                (refRow1, refCol1) = (_mvRow1[idx], _mvCol1[idx]);
            }
        }
        else if (b.YMode == InterMode.New && _mvCount > 1)
        {
            (refRow0, refCol0) = (_mvRow0[refMvIdx], _mvCol0[refMvIdx]);
        }

        AssignMv(b, refRow0, refCol0, refRow1, refCol1, nearestRow0, nearestCol0, nearestRow1, nearestCol1, nearRow0, nearCol0, nearRow1, nearCol1);

        b.UseWedgeInterintra = false;
        if (_sequence.EnableInterintraCompound && !b.SkipMode && bsize >= Bs.B8x8 && bsize <= Bs.B32x32
            && b.YMode >= InterMode.Nearest && b.YMode <= InterMode.New && b.RefFrame0 > Ref.Intra && b.RefFrame1 <= Ref.Intra)
        {
            int group = Av1Tables.SizeGroupLookup[bsize];
            if (Symbol(Av1FrameContext.Interintra[group], 2) != 0)
            {
                b.InterintraMode = Symbol(Av1FrameContext.InterintraMode[group], 4);
                b.RefFrame1 = Ref.Intra;
                b.AngleDeltaY = 0;
                b.AngleDeltaUv = 0;
                b.UseFilterIntra = false;
                if (WedgeUsed(bsize))
                {
                    b.UseWedgeInterintra = Symbol(Av1FrameContext.WedgeInterintra[bsize], 2) != 0;
                    if (b.UseWedgeInterintra)
                    {
                        b.InterintraWedgeIndex = Symbol(Av1FrameContext.WedgeIdx[bsize], 16);
                    }
                }
            }
        }

        b.MotionMode = 0;
        int numSamples = 0;
        if (Math.Min(Av1Tables.BlockSizeWide[bsize], Av1Tables.BlockSizeHigh[bsize]) >= 8 && !b.SkipMode && !b.HasSecondRef)
        {
            numSamples = FindSamples();
        }

        bool overlappable = HasOverlappableNeighbors();
        if (b.RefFrame1 != Ref.Intra)
        {
            b.MotionMode = ReadMotionMode(overlappable, numSamples);
        }

        b.CompGroupIdx = 0;
        b.CompoundIdx = 1;
        b.CompoundType = Compound.Average;
        if (b.HasSecondRef && !b.SkipMode)
        {
            if (CompRefAllowed(bsize) && _sequence.EnableMaskedCompound)
            {
                b.CompGroupIdx = Symbol(Av1FrameContext.CompGroupIdx[CompGroupIdxContext()], 2);
            }

            if (b.CompGroupIdx == 0)
            {
                if (_sequence.EnableJntComp)
                {
                    b.CompoundIdx = Symbol(Av1FrameContext.CompoundIdx[CompoundIdxContext()], 2);
                    b.CompoundType = b.CompoundIdx != 0 ? Compound.Average : Compound.Distance;
                }
            }
            else
            {
                b.CompoundType = WedgeUsed(bsize) ? Compound.Wedge + Symbol(Av1FrameContext.CompoundType[bsize], 2) : Compound.DiffWeighted;
                if (b.CompoundType == Compound.Wedge)
                {
                    b.WedgeIndex = Symbol(Av1FrameContext.WedgeIdx[bsize], 16);
                    b.WedgeSign = _r.Bit();
                }
                else
                {
                    b.MaskType = _r.Literal(1);
                }
            }
        }

        ReadInterpFilter();
        if (b.MotionMode == 2)
        {
            if (numSamples > 1)
            {
                numSamples = SelectSamples(numSamples);
            }

            int[] warp = new int[6];
            b.WarpValid = Av1Warp.FindProjection(numSamples, _samples, _samplesInRef, bsize, b.MvRow, b.MvCol, warp, _miRow, _miCol)
                && Av1Warp.ShearValid(warp);
            b.WarpParams = warp;
        }
    }

    private (int Row, int Col) StackOrGlobal(int index) => index < _mvCount ? (_mvRow0[index], _mvCol0[index]) : (_gmRow0, _gmCol0);

    private static (int Row, int Col) Lower((int Row, int Col) mv, bool allowHp, bool integer)
    {
        (int row, int col) = mv;
        if (integer)
        {
            return (IntegerPrecision(row), IntegerPrecision(col));
        }

        if (!allowHp)
        {
            row += (row & 1) != 0 ? (row > 0 ? -1 : 1) : 0;
            col += (col & 1) != 0 ? (col > 0 ? -1 : 1) : 0;
        }

        return (row, col);
    }

    private static int IntegerPrecision(int v)
    {
        int mod = v % 8;
        if (mod != 0)
        {
            v -= mod;
            if (Math.Abs(mod) > 4)
            {
                v += mod > 0 ? 8 : -8;
            }
        }

        return v;
    }

    private void AssignMv(Av1BlockInfo b, int refRow0, int refCol0, int refRow1, int refCol1, int nearestRow0, int nearestCol0,
        int nearestRow1, int nearestCol1, int nearRow0, int nearCol0, int nearRow1, int nearCol1)
    {
        int mode = b.YMode;
        int r0 = 0, c0 = 0, r1 = 0, c1 = 0;
        switch (mode)
        {
            case InterMode.New:
                (r0, c0) = ReadMv(refRow0, refCol0);
                break;
            case InterMode.Nearest:
                (r0, c0) = (nearestRow0, nearestCol0);
                break;
            case InterMode.Near:
                (r0, c0) = (nearRow0, nearCol0);
                break;
            case InterMode.Global:
                (r0, c0) = (_gmRow0, _gmCol0);
                break;
            case InterMode.NewNew:
                (r0, c0) = ReadMv(refRow0, refCol0);
                (r1, c1) = ReadMv(refRow1, refCol1);
                break;
            case InterMode.NearestNearest:
                (r0, c0, r1, c1) = (nearestRow0, nearestCol0, nearestRow1, nearestCol1);
                break;
            case InterMode.NearNear:
                (r0, c0, r1, c1) = (nearRow0, nearCol0, nearRow1, nearCol1);
                break;
            case InterMode.NewNearest:
                (r0, c0) = ReadMv(refRow0, refCol0);
                (r1, c1) = (nearestRow1, nearestCol1);
                break;
            case InterMode.NearestNew:
                (r0, c0) = (nearestRow0, nearestCol0);
                (r1, c1) = ReadMv(refRow1, refCol1);
                break;
            case InterMode.NearNew:
                (r0, c0) = (nearRow0, nearCol0);
                (r1, c1) = ReadMv(refRow1, refCol1);
                break;
            case InterMode.NewNear:
                (r0, c0) = ReadMv(refRow0, refCol0);
                (r1, c1) = (nearRow1, nearCol1);
                break;
            default:
                (r0, c0, r1, c1) = (_gmRow0, _gmCol0, _gmRow1, _gmCol1);
                break;
        }

        b.MvRow = r0;
        b.MvCol = c0;
        b.Mv1Row = r1;
        b.Mv1Col = c1;
        if (!Valid(r0, c0) || (b.HasSecondRef && !Valid(r1, c1)))
        {
            throw Corrupt("An AV1 motion vector is out of range.");
        }

        static bool Valid(int row, int col) => row > MvLow && row < MvUpp && col > MvLow && col < MvUpp;
    }

    private (int Row, int Col) ReadMv(int refRow, int refCol)
    {
        Av1FrameContext.MvTables t = Av1FrameContext.Mv;
        int precision = _header.ForceIntegerMv ? -1 : _header.AllowHighPrecisionMv ? 1 : 0;
        int joint = Symbol(t.Joints[0], 4);
        int row = joint is 2 or 3 ? ReadMvComponent(t, 0, precision) : 0;
        int col = joint is 1 or 3 ? ReadMvComponent(t, 1, precision) : 0;
        return (refRow + row, refCol + col);
    }

    private int ReadMvComponent(Av1FrameContext.MvTables t, int comp, int precision)
    {
        int sign = Symbol(t.Sign[comp], 2);
        int mvClass = Symbol(t.Classes[comp], 11);
        int d, mag;
        if (mvClass == 0)
        {
            d = Symbol(t.Class0[comp], 2);
            mag = 0;
        }
        else
        {
            d = 0;
            for (int i = 0; i < mvClass; i++)
            {
                d |= Symbol(t.Bits[(comp * 10) + i], 2) << i;
            }

            mag = 2 << (mvClass + 2);
        }

        int fr = 3, hp = 1;
        if (precision >= 0)
        {
            fr = Symbol(mvClass == 0 ? t.Class0Fp[(comp * 2) + d] : t.Fp[comp], 4);
            hp = precision > 0 ? Symbol(mvClass == 0 ? t.Class0Hp[comp] : t.Hp[comp], 2) : 1;
        }

        mag += ((d << 3) | (fr << 1) | hp) + 1;
        return sign != 0 ? -mag : mag;
    }

    private int DrlContext(int idx)
    {
        bool w0 = _mvWeight[idx] >= RefCatLevel, w1 = _mvWeight[idx + 1] >= RefCatLevel;
        return w0 && w1 ? 0 : w0 ? 1 : !w1 ? 2 : 0;
    }

    private void CollectNeighborRefCounts()
    {
        Array.Clear(_neighborRefCounts);
        if (_upAvailable && _above!.IsInter)
        {
            Count(_above);
        }

        if (_leftAvailable && _left!.IsInter)
        {
            Count(_left);
        }

        void Count(Av1BlockInfo n)
        {
            // An intra block copy counts as INTRA_FRAME, which no context reads.
            _neighborRefCounts[Math.Max(n.RefFrame0, 0)]++;
            if (n.HasSecondRef)
            {
                _neighborRefCounts[n.RefFrame1]++;
            }
        }
    }

    private static int CountContext(int a, int b) => a == b ? 1 : a < b ? 0 : 2;

    private int RefCount(int frame) => _neighborRefCounts[frame];

    private int RefBit(Av1FrameContext.Table table, int perCtx, int n, int ctx) => Symbol(table[(ctx * perCtx) + n], 2);

    /// <summary>read_ref_frames.</summary>
    private void ReadRefFrames()
    {
        Av1BlockInfo b = _b;
        if (b.SkipMode)
        {
            b.RefFrame0 = _header.SkipModeFrame[0];
            b.RefFrame1 = _header.SkipModeFrame[1];
            return;
        }

        if (SegFeatureActive(b.SegmentId, 5))
        {
            b.RefFrame0 = _header.FeatureData[b.SegmentId, 5];
            b.RefFrame1 = Ref.None;
            return;
        }

        if (SegFeatureActive(b.SegmentId, 6) || SegFeatureActive(b.SegmentId, 7))
        {
            b.RefFrame0 = Ref.Last;
            b.RefFrame1 = Ref.None;
            return;
        }

        int fwd = RefCount(Ref.Last) + RefCount(Ref.Last2) + RefCount(Ref.Last3) + RefCount(Ref.Golden);
        int bwd = RefCount(Ref.Bwd) + RefCount(Ref.Alt2) + RefCount(Ref.Alt);
        int ll2VsL3g = CountContext(RefCount(Ref.Last) + RefCount(Ref.Last2), RefCount(Ref.Last3) + RefCount(Ref.Golden));
        int lVsL2 = CountContext(RefCount(Ref.Last), RefCount(Ref.Last2));
        int l3VsG = CountContext(RefCount(Ref.Last3), RefCount(Ref.Golden));
        int bA2VsA = CountContext(RefCount(Ref.Bwd) + RefCount(Ref.Alt2), RefCount(Ref.Alt));
        int bVsA2 = CountContext(RefCount(Ref.Bwd), RefCount(Ref.Alt2));
        bool compound = _header.ReferenceSelect && CompRefAllowed(b.Size) && Symbol(Av1FrameContext.CompInter[ReferenceModeContext()], 2) != 0;
        if (compound)
        {
            bool bidir = Symbol(Av1FrameContext.CompRefType[CompReferenceTypeContext()], 2) != 0;
            if (!bidir)
            {
                if (RefBit(Av1FrameContext.UniCompRef, 3, 0, CountContext(fwd, bwd)) != 0)
                {
                    (b.RefFrame0, b.RefFrame1) = (Ref.Bwd, Ref.Alt);
                }
                else if (RefBit(Av1FrameContext.UniCompRef, 3, 1, CountContext(RefCount(Ref.Last2), RefCount(Ref.Last3) + RefCount(Ref.Golden))) != 0)
                {
                    b.RefFrame0 = Ref.Last;
                    b.RefFrame1 = RefBit(Av1FrameContext.UniCompRef, 3, 2, l3VsG) != 0 ? Ref.Golden : Ref.Last3;
                }
                else
                {
                    (b.RefFrame0, b.RefFrame1) = (Ref.Last, Ref.Last2);
                }

                return;
            }

            if (RefBit(Av1FrameContext.CompRef, 3, 0, ll2VsL3g) == 0)
            {
                b.RefFrame0 = RefBit(Av1FrameContext.CompRef, 3, 1, lVsL2) != 0 ? Ref.Last2 : Ref.Last;
            }
            else
            {
                b.RefFrame0 = RefBit(Av1FrameContext.CompRef, 3, 2, l3VsG) != 0 ? Ref.Golden : Ref.Last3;
            }

            if (RefBit(Av1FrameContext.CompBwdref, 2, 0, bA2VsA) == 0)
            {
                b.RefFrame1 = RefBit(Av1FrameContext.CompBwdref, 2, 1, bVsA2) != 0 ? Ref.Alt2 : Ref.Bwd;
            }
            else
            {
                b.RefFrame1 = Ref.Alt;
            }

            return;
        }

        if (RefBit(Av1FrameContext.SingleRef, 6, 0, CountContext(fwd, bwd)) != 0)
        {
            if (RefBit(Av1FrameContext.SingleRef, 6, 1, bA2VsA) == 0)
            {
                b.RefFrame0 = RefBit(Av1FrameContext.SingleRef, 6, 5, bVsA2) != 0 ? Ref.Alt2 : Ref.Bwd;
            }
            else
            {
                b.RefFrame0 = Ref.Alt;
            }
        }
        else if (RefBit(Av1FrameContext.SingleRef, 6, 2, ll2VsL3g) != 0)
        {
            b.RefFrame0 = RefBit(Av1FrameContext.SingleRef, 6, 4, l3VsG) != 0 ? Ref.Golden : Ref.Last3;
        }
        else
        {
            b.RefFrame0 = RefBit(Av1FrameContext.SingleRef, 6, 3, lVsL2) != 0 ? Ref.Last2 : Ref.Last;
        }

        b.RefFrame1 = Ref.None;
    }

    private static bool IsBackward(int frame) => frame >= Ref.Bwd && frame <= Ref.Alt;

    private static bool HasUniCompRefs(Av1BlockInfo n) => n.HasSecondRef && !(IsBackward(n.RefFrame0) ^ IsBackward(n.RefFrame1));

    /// <summary>av1_get_reference_mode_context.</summary>
    private int ReferenceModeContext()
    {
        if (_upAvailable && _leftAvailable)
        {
            Av1BlockInfo a = _above!, l = _left!;
            if (!a.HasSecondRef && !l.HasSecondRef)
            {
                return (IsBackward(a.RefFrame0) ? 1 : 0) ^ (IsBackward(l.RefFrame0) ? 1 : 0);
            }

            if (!a.HasSecondRef)
            {
                return 2 + (IsBackward(a.RefFrame0) || !a.IsInter ? 1 : 0);
            }

            if (!l.HasSecondRef)
            {
                return 2 + (IsBackward(l.RefFrame0) || !l.IsInter ? 1 : 0);
            }

            return 4;
        }

        if (_upAvailable || _leftAvailable)
        {
            Av1BlockInfo e = _upAvailable ? _above! : _left!;
            return !e.HasSecondRef ? (IsBackward(e.RefFrame0) ? 1 : 0) : 3;
        }

        return 1;
    }

    /// <summary>av1_get_comp_reference_type_context.</summary>
    private int CompReferenceTypeContext()
    {
        if (_upAvailable && _leftAvailable)
        {
            Av1BlockInfo a = _above!, l = _left!;
            bool aboveIntra = !a.IsInter, leftIntra = !l.IsInter;
            if (aboveIntra && leftIntra)
            {
                return 2;
            }

            if (aboveIntra || leftIntra)
            {
                Av1BlockInfo inter = aboveIntra ? l : a;
                return !inter.HasSecondRef ? 2 : 1 + (2 * (HasUniCompRefs(inter) ? 1 : 0));
            }

            bool aSingle = !a.HasSecondRef, lSingle = !l.HasSecondRef;
            int frfa = a.RefFrame0, frfl = l.RefFrame0;
            if (aSingle && lSingle)
            {
                return 1 + (2 * (!(IsBackward(frfa) ^ IsBackward(frfl)) ? 1 : 0));
            }

            if (aSingle || lSingle)
            {
                bool uni = aSingle ? HasUniCompRefs(l) : HasUniCompRefs(a);
                return !uni ? 1 : 3 + (!(IsBackward(frfa) ^ IsBackward(frfl)) ? 1 : 0);
            }

            bool aUni = HasUniCompRefs(a), lUni = HasUniCompRefs(l);
            if (!aUni && !lUni)
            {
                return 0;
            }

            if (!aUni || !lUni)
            {
                return 2;
            }

            return 3 + (!((frfa == Ref.Bwd) ^ (frfl == Ref.Bwd)) ? 1 : 0);
        }

        if (_upAvailable || _leftAvailable)
        {
            Av1BlockInfo e = _upAvailable ? _above! : _left!;
            return !e.IsInter || !e.HasSecondRef ? 2 : 4 * (HasUniCompRefs(e) ? 1 : 0);
        }

        return 2;
    }

    private int CompGroupIdxContext()
    {
        int above = Ctx(_above), left = Ctx(_left);
        return Math.Min(5, above + left);

        static int Ctx(Av1BlockInfo? n) => n is null ? 0 : n.HasSecondRef ? n.CompGroupIdx : n.RefFrame0 == Ref.Alt ? 3 : 0;
    }

    private int CompoundIdxContext()
    {
        Av1BlockInfo b = _b;
        int cur = _header.OrderHint;
        int fwd = Math.Abs(_header.RelativeDist(_header.OrderHints[b.RefFrame1], cur));
        int bck = Math.Abs(_header.RelativeDist(cur, _header.OrderHints[b.RefFrame0]));
        int above = Ctx(_above), left = Ctx(_left);
        return above + left + (3 * (fwd == bck ? 1 : 0));

        static int Ctx(Av1BlockInfo? n) => n is null ? 0 : n.HasSecondRef ? n.CompoundIdx : n.RefFrame0 == Ref.Alt ? 1 : 0;
    }

    /// <summary>Gets whether a block uses global motion other than a translation (is_global_mv_block).</summary>
    private bool IsGlobalMvBlock(Av1BlockInfo n, int frame) =>
        n.YMode is InterMode.Global or InterMode.GlobalGlobal && _header.GmType[frame] > GlobalMotion.Translation && CompRefAllowed(n.Size);

    private int ReadMotionMode(bool overlappable, int numSamples)
    {
        Av1BlockInfo b = _b;
        if (b.SkipMode || !_header.IsMotionModeSwitchable || !overlappable)
        {
            return 0;
        }

        if (!_header.ForceIntegerMv && IsGlobalMvBlock(b, b.RefFrame0))
        {
            return 0;
        }

        if (Math.Min(Av1Tables.BlockSizeWide[b.Size], Av1Tables.BlockSizeHigh[b.Size]) < 8 || b.YMode < InterMode.Nearest
            || b.RefFrame1 == Ref.Intra || b.HasSecondRef)
        {
            return 0;
        }

        bool warp = numSamples >= 1 && _header.AllowWarpedMotion && !_header.ForceIntegerMv && !RefIsScaled(b.RefFrame0);
        return warp ? Symbol(Av1FrameContext.MotionMode[b.Size], 3) : Symbol(Av1FrameContext.Obmc[b.Size], 2);
    }

    /// <summary>Gets whether a reference frame differs in size from the frame (av1_is_scaled).</summary>
    private bool RefIsScaled(int frame)
    {
        Av1FrameHeader reference = _header.RefFrames[frame]!.Header;
        int xScale = ((reference.UpscaledWidth << 14) + (_header.FrameWidth / 2)) / _header.FrameWidth;
        int yScale = ((reference.FrameHeight << 14) + (_header.FrameHeight / 2)) / _header.FrameHeight;
        return xScale != 1 << 14 || yScale != 1 << 14;
    }

    /// <summary>av1_count_overlappable_neighbors: whether an inter block lies above or left (only the count's being nonzero matters).</summary>
    private bool HasOverlappableNeighbors()
    {
        if (Math.Min(Av1Tables.BlockSizeWide[_b.Size], Av1Tables.BlockSizeHigh[_b.Size]) < 8)
        {
            return false;
        }

        if (_upAvailable)
        {
            int end = Math.Min(_miCol + _bw, _miCols);
            for (int col = _miCol; col < end;)
            {
                Av1BlockInfo n = _frame.Grid[((_miRow - 1) * _miCols) + col]!;
                int step = Math.Min((int)Av1Tables.MiSizeWide[n.Size], 16);
                if (step == 1)
                {
                    col &= ~1;
                    n = _frame.Grid[((_miRow - 1) * _miCols) + col + 1]!;
                    step = 2;
                }

                if (n.IsInter)
                {
                    return true;
                }

                col += step;
            }
        }

        if (_leftAvailable)
        {
            int end = Math.Min(_miRow + _bh, _frame.MiRows);
            for (int row = _miRow; row < end;)
            {
                Av1BlockInfo n = _frame.Grid[(row * _miCols) + _miCol - 1]!;
                int step = Math.Min((int)Av1Tables.MiSizeHigh[n.Size], 16);
                if (step == 1)
                {
                    row &= ~1;
                    n = _frame.Grid[((row + 1) * _miCols) + _miCol - 1]!;
                    step = 2;
                }

                if (n.IsInter)
                {
                    return true;
                }

                row += step;
            }
        }

        return false;
    }

    private void ReadInterpFilter()
    {
        Av1BlockInfo b = _b;
        int frameFilter = _header.InterpolationFilter;
        if (b.SkipMode || b.MotionMode == 2 || IsNonTranslationalGlobal(b))
        {
            b.FilterY = b.FilterX = frameFilter == Interp.Switchable ? Interp.Regular : frameFilter;
            return;
        }

        if (frameFilter != Interp.Switchable)
        {
            b.FilterY = b.FilterX = frameFilter;
            return;
        }

        int y = Symbol(Av1FrameContext.SwitchableInterp[InterpContext(0)], 3);
        int x = y;
        if (_sequence.EnableDualFilter)
        {
            x = Symbol(Av1FrameContext.SwitchableInterp[InterpContext(1)], 3);
        }

        b.FilterY = y;
        b.FilterX = x;
    }

    /// <summary>is_nontrans_global_motion: a global mode whose every reference's global motion is not a translation.</summary>
    private bool IsNonTranslationalGlobal(Av1BlockInfo b)
    {
        if (b.YMode is not (InterMode.Global or InterMode.GlobalGlobal) || Math.Min(Av1Tables.MiSizeWide[b.Size], Av1Tables.MiSizeHigh[b.Size]) < 2)
        {
            return false;
        }

        return _header.GmType[b.RefFrame0] != GlobalMotion.Translation && (!b.HasSecondRef || _header.GmType[b.RefFrame1] != GlobalMotion.Translation);
    }

    /// <summary>av1_get_pred_context_switchable_interp.</summary>
    private int InterpContext(int dir)
    {
        Av1BlockInfo b = _b;
        int ctx = (b.RefFrame1 > Ref.Intra ? 4 : 0) + (dir * 8);
        int frame = b.RefFrame0;
        int left = _leftAvailable ? FilterType(_left!) : 3, above = _upAvailable ? FilterType(_above!) : 3;
        if (left == above)
        {
            return ctx + left;
        }

        if (left == 3)
        {
            return ctx + above;
        }

        return above == 3 ? ctx + left : ctx + 3;

        int FilterType(Av1BlockInfo n) => n.RefFrame0 == frame || n.RefFrame1 == frame ? (dir == 0 ? n.FilterY : n.FilterX) : 3;
    }

    /// <summary>av1_findSamples: the centres of the neighbours that use the same single reference, for local warp.</summary>
    private int FindSamples()
    {
        Av1BlockInfo b = _b;
        int frame = b.RefFrame0;
        int np = 0;
        bool doTopLeft = true, doTopRight = true;
        if (_upAvailable)
        {
            Av1BlockInfo n = _frame.Grid[((_miRow - 1) * _miCols) + _miCol]!;
            int width = Av1Tables.MiSizeWide[n.Size];
            if (_bw <= width)
            {
                int colOffset = -_miCol % width;
                doTopLeft &= colOffset >= 0;
                doTopRight &= colOffset + width <= _bw;
                if (n.RefFrame0 == frame && n.RefFrame1 == Ref.None && Record(n, 0, -1, colOffset, 1))
                {
                    return 8;
                }
            }
            else
            {
                for (int i = 0; i < Math.Min(_bw, _miCols - _miCol); i += width)
                {
                    n = _frame.Grid[((_miRow - 1) * _miCols) + _miCol + i]!;
                    width = Av1Tables.MiSizeWide[n.Size];
                    if (n.RefFrame0 == frame && n.RefFrame1 == Ref.None && Record(n, 0, -1, i, 1))
                    {
                        return 8;
                    }
                }
            }
        }

        if (_leftAvailable)
        {
            Av1BlockInfo n = _frame.Grid[(_miRow * _miCols) + _miCol - 1]!;
            int height = Av1Tables.MiSizeHigh[n.Size];
            if (_bh <= height)
            {
                int rowOffset = -_miRow % height;
                doTopLeft &= rowOffset >= 0;
                if (n.RefFrame0 == frame && n.RefFrame1 == Ref.None && Record(n, rowOffset, 1, 0, -1))
                {
                    return 8;
                }
            }
            else
            {
                for (int i = 0; i < Math.Min(_bh, _frame.MiRows - _miRow); i += height)
                {
                    n = _frame.Grid[((_miRow + i) * _miCols) + _miCol - 1]!;
                    height = Av1Tables.MiSizeHigh[n.Size];
                    if (n.RefFrame0 == frame && n.RefFrame1 == Ref.None && Record(n, i, 1, 0, -1))
                    {
                        return 8;
                    }
                }
            }
        }

        if (doTopLeft && _leftAvailable && _upAvailable)
        {
            Av1BlockInfo n = _frame.Grid[((_miRow - 1) * _miCols) + _miCol - 1]!;
            if (n.RefFrame0 == frame && n.RefFrame1 == Ref.None && Record(n, 0, -1, 0, -1))
            {
                return 8;
            }
        }

        if (doTopRight && HasTopRight(Math.Max(_bw, _bh)))
        {
            int row = _miRow - 1, col = _miCol + _bw;
            if (row >= MiRowStart && col >= MiColStart && row < MiRowEnd && col < MiColEnd)
            {
                Av1BlockInfo n = _frame.Grid[(row * _miCols) + col]!;
                if (n.RefFrame0 == frame && n.RefFrame1 == Ref.None && Record(n, 0, -1, _bw, 1))
                {
                    return 8;
                }
            }
        }

        return np;

        // record_samples; returns whether the sample list is full.
        bool Record(Av1BlockInfo n, int rowOffset, int signR, int colOffset, int signC)
        {
            int bw = Av1Tables.BlockSizeWide[n.Size], bh = Av1Tables.BlockSizeHigh[n.Size];
            int x = (colOffset * 4) + (signC * bw / 2) - 1;
            int y = (rowOffset * 4) + (signR * bh / 2) - 1;
            _samples[2 * np] = x * 8;
            _samples[(2 * np) + 1] = y * 8;
            _samplesInRef[2 * np] = (x * 8) + n.MvCol;
            _samplesInRef[(2 * np) + 1] = (y * 8) + n.MvRow;
            return ++np >= 8;
        }
    }

    /// <summary>av1_selectSamples: keeps the samples whose vectors are close to the block's.</summary>
    private int SelectSamples(int len)
    {
        int bw = Av1Tables.BlockSizeWide[_b.Size], bh = Av1Tables.BlockSizeHigh[_b.Size];
        int thresh = Math.Clamp(Math.Max(bw, bh), 16, 112);
        int kept = 0;
        for (int i = 0; i < len; i++)
        {
            int diff = Math.Abs(_samplesInRef[2 * i] - _samples[2 * i] - _b.MvCol) + Math.Abs(_samplesInRef[(2 * i) + 1] - _samples[(2 * i) + 1] - _b.MvRow);
            if (diff > thresh)
            {
                continue;
            }

            if (kept != i)
            {
                _samples[2 * kept] = _samples[2 * i];
                _samples[(2 * kept) + 1] = _samples[(2 * i) + 1];
                _samplesInRef[2 * kept] = _samplesInRef[2 * i];
                _samplesInRef[(2 * kept) + 1] = _samplesInRef[(2 * i) + 1];
            }

            kept++;
        }

        return Math.Max(kept, 1);
    }

    /// <summary>gm_get_motion_vector: the vector global motion gives the block's centre.</summary>
    private (int Row, int Col) GlobalVector(int frame)
    {
        int type = _header.GmType[frame];
        bool hp = _header.AllowHighPrecisionMv, integer = _header.ForceIntegerMv;
        if (type == GlobalMotion.Identity)
        {
            return (0, 0);
        }

        int[] mat = _header.GmParams[frame];
        int row, col;
        if (type == GlobalMotion.Translation)
        {
            // As libaom and the specification have it: the first parameter (horizontal) becomes the row.
            row = mat[0] >> (Av1FrameHeader.WarpPrecBits - 3);
            col = mat[1] >> (Av1FrameHeader.WarpPrecBits - 3);
        }
        else
        {
            int x = (_miCol * 4) + (Av1Tables.BlockSizeWide[_b.Size] / 2) - 1;
            int y = (_miRow * 4) + (Av1Tables.BlockSizeHigh[_b.Size] / 2) - 1;
            int xc = ((mat[2] - (1 << Av1FrameHeader.WarpPrecBits)) * x) + (mat[3] * y) + mat[0];
            int yc = (mat[4] * x) + ((mat[5] - (1 << Av1FrameHeader.WarpPrecBits)) * y) + mat[1];
            col = TransPrecision(xc);
            row = TransPrecision(yc);
        }

        return integer ? (IntegerPrecision(row), IntegerPrecision(col)) : (row, col);

        int TransPrecision(int v) => hp ? Round2Signed(v, Av1FrameHeader.WarpPrecBits - 3) : Round2Signed(v, Av1FrameHeader.WarpPrecBits - 2) * 2;
    }

    private static int Round2Signed(int v, int n) => v < 0 ? -((-v + (1 << (n - 1))) >> n) : (v + (1 << (n - 1))) >> n;

    /// <summary>av1_find_mv_refs for the block's reference frame or pair: the stack, its weights and the mode context.</summary>
    private void FindMvRefs()
    {
        Av1BlockInfo b = _b;
        int rf0 = b.RefFrame0, rf1 = b.HasSecondRef ? b.RefFrame1 : Ref.None;
        (_gmRow0, _gmCol0) = GlobalVector(rf0);
        (_gmRow1, _gmCol1) = rf1 > Ref.Intra ? GlobalVector(rf1) : (0, 0);

        int bs = Math.Max(_bw, _bh);
        bool hasTr = HasTopRight(bs);
        int maxRowOffset = 0, maxColOffset = 0;
        int rowAdj = _bh < 2 && (_miRow & 1) != 0 ? 1 : 0;
        int colAdj = _bw < 2 && (_miCol & 1) != 0 ? 1 : 0;
        int processedRows = 0, processedCols = 0;
        _modeContext = 0;
        _mvCount = 0;
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

        int rowMatch = 0, colMatch = 0, newMvCount = 0, dummy = 0;
        if (Math.Abs(maxRowOffset) >= 1)
        {
            ScanInterRow(-1, maxRowOffset, ref processedRows, rf0, rf1, ref rowMatch, ref newMvCount);
        }

        if (Math.Abs(maxColOffset) >= 1)
        {
            ScanInterCol(-1, maxColOffset, ref processedCols, rf0, rf1, ref colMatch, ref newMvCount);
        }

        if (hasTr)
        {
            ScanInterBlock(-1, _bw, rf0, rf1, ref rowMatch, ref newMvCount);
        }

        int nearestMatch = (rowMatch > 0 ? 1 : 0) + (colMatch > 0 ? 1 : 0);
        int nearestCount = _mvCount;
        for (int i = 0; i < nearestCount; i++)
        {
            _mvWeight[i] += RefCatLevel;
        }

        if (_header.UseRefFrameMvs)
        {
            bool available = false;
            int voffset = Math.Max(2, _bh), hoffset = Math.Max(2, _bw);
            int rowEnd = Math.Min(_bh, 16), colEnd = Math.Min(_bw, 16);
            bool extension = _bh >= 2 && _bh < 16 && _bw >= 2 && _bw < 16;
            int stepH = _bh >= 16 ? 4 : 2, stepW = _bw >= 16 ? 4 : 2;
            for (int row = 0; row < rowEnd; row += stepH)
            {
                for (int col = 0; col < colEnd; col += stepW)
                {
                    bool ret = AddTemporalCandidate(row, col, rf0, rf1);
                    if (row == 0 && col == 0)
                    {
                        available = ret;
                    }
                }
            }

            if (!available)
            {
                _modeContext |= 1 << 3;
            }

            ReadOnlySpan<int> positions = [voffset, -2, voffset, hoffset, voffset - 2, hoffset];
            for (int i = 0; i < 3 && extension; i++)
            {
                int row = positions[2 * i], col = positions[(2 * i) + 1];
                int r = (_miRow & 15) + row, c = (_miCol & 15) + col;
                if (r < 0 || r >= 16 || c < 0 || c >= 16)
                {
                    continue;
                }

                AddTemporalCandidate(row, col, rf0, rf1);
            }
        }

        ScanInterBlock(-1, -1, rf0, rf1, ref rowMatch, ref dummy);
        for (int idx = 2; idx <= 3; idx++)
        {
            int rowOffset = -(idx << 1) + 1 + rowAdj;
            int colOffset = -(idx << 1) + 1 + colAdj;
            if (Math.Abs(rowOffset) <= Math.Abs(maxRowOffset) && Math.Abs(rowOffset) > processedRows)
            {
                ScanInterRow(rowOffset, maxRowOffset, ref processedRows, rf0, rf1, ref rowMatch, ref dummy);
            }

            if (Math.Abs(colOffset) <= Math.Abs(maxColOffset) && Math.Abs(colOffset) > processedCols)
            {
                ScanInterCol(colOffset, maxColOffset, ref processedCols, rf0, rf1, ref colMatch, ref dummy);
            }
        }

        int refMatch = (rowMatch > 0 ? 1 : 0) + (colMatch > 0 ? 1 : 0);
        switch (nearestMatch)
        {
            case 0:
                _modeContext |= refMatch >= 1 ? 1 : 0;
                _modeContext |= refMatch == 1 ? 1 << 4 : refMatch >= 2 ? 2 << 4 : 0;
                break;
            case 1:
                _modeContext |= newMvCount > 0 ? 2 : 3;
                _modeContext |= refMatch == 1 ? 3 << 4 : refMatch >= 2 ? 4 << 4 : 0;
                break;
            default:
                _modeContext |= newMvCount >= 1 ? 4 : 5;
                _modeContext |= 5 << 4;
                break;
        }

        SortInterStack(0, nearestCount);
        SortInterStack(nearestCount, _mvCount);

        int miSize = Math.Min(Math.Min(Math.Min(16, _bw), _miCols - _miCol), Math.Min(Math.Min(16, _bh), _frame.MiRows - _miRow));
        if (rf1 > Ref.None)
        {
            if (_mvCount < 2)
            {
                ExtendCompoundStack(maxRowOffset, maxColOffset, miSize, rf0, rf1);
            }

            for (int i = 0; i < _mvCount; i++)
            {
                (_mvRow0[i], _mvCol0[i]) = ClampMvRef(_mvRow0[i], _mvCol0[i]);
                (_mvRow1[i], _mvCol1[i]) = ClampMvRef(_mvRow1[i], _mvCol1[i]);
            }
        }
        else
        {
            for (int idx = 0; Math.Abs(maxRowOffset) >= 1 && idx < miSize && _mvCount < 2;)
            {
                Av1BlockInfo n = GridAt(-1, idx)!;
                AddSingleExtra(n, rf0);
                idx += Av1Tables.MiSizeWide[n.Size];
            }

            for (int idx = 0; Math.Abs(maxColOffset) >= 1 && idx < miSize && _mvCount < 2;)
            {
                Av1BlockInfo n = GridAt(idx, -1)!;
                AddSingleExtra(n, rf0);
                idx += Av1Tables.MiSizeHigh[n.Size];
            }

            for (int i = 0; i < _mvCount; i++)
            {
                (_mvRow0[i], _mvCol0[i]) = ClampMvRef(_mvRow0[i], _mvCol0[i]);
            }
        }
    }

    private (int Row, int Col) ClampMvRef(int row, int col)
    {
        int bw = _bw * 4, bh = _bh * 4;
        return (Math.Clamp(row, _toTopEdge - (bh * 8) - 128, _toBottomEdge + (bh * 8) + 128),
            Math.Clamp(col, _toLeftEdge - (bw * 8) - 128, _toRightEdge + (bw * 8) + 128));
    }

    private void SortInterStack(int start, int end)
    {
        int len = end;
        while (len > start)
        {
            int newLen = start;
            for (int idx = start + 1; idx < len; idx++)
            {
                if (_mvWeight[idx - 1] < _mvWeight[idx])
                {
                    (_mvRow0[idx - 1], _mvRow0[idx]) = (_mvRow0[idx], _mvRow0[idx - 1]);
                    (_mvCol0[idx - 1], _mvCol0[idx]) = (_mvCol0[idx], _mvCol0[idx - 1]);
                    (_mvRow1[idx - 1], _mvRow1[idx]) = (_mvRow1[idx], _mvRow1[idx - 1]);
                    (_mvCol1[idx - 1], _mvCol1[idx]) = (_mvCol1[idx], _mvCol1[idx - 1]);
                    (_mvWeight[idx - 1], _mvWeight[idx]) = (_mvWeight[idx], _mvWeight[idx - 1]);
                    newLen = idx;
                }
            }

            len = newLen;
        }
    }

    private void ScanInterRow(int rowOffset, int maxRowOffset, ref int processedRows, int rf0, int rf1, ref int matchCount, ref int newMvCount)
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
            Av1BlockInfo candidate = GridAt(rowOffset, colOffset + i)!;
            int n4w = Av1Tables.MiSizeWide[candidate.Size];
            int len = Math.Min(_bw, n4w);
            len = step16 ? Math.Max(4, len) : Math.Abs(rowOffset) > 1 ? Math.Max(len, 2) : len;
            int weight = 2;
            if (_bw >= 2 && _bw <= n4w)
            {
                int inc = Math.Min(-maxRowOffset + rowOffset + 1, Av1Tables.MiSizeHigh[candidate.Size]);
                weight = Math.Max(weight, inc);
                processedRows = inc - rowOffset - 1;
            }

            AddInterCandidate(candidate, rf0, rf1, len * weight, ref matchCount, ref newMvCount);
            i += len;
        }
    }

    private void ScanInterCol(int colOffset, int maxColOffset, ref int processedCols, int rf0, int rf1, ref int matchCount, ref int newMvCount)
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
            Av1BlockInfo candidate = GridAt(rowOffset + i, colOffset)!;
            int n4h = Av1Tables.MiSizeHigh[candidate.Size];
            int len = Math.Min(_bh, n4h);
            len = step16 ? Math.Max(4, len) : Math.Abs(colOffset) > 1 ? Math.Max(len, 2) : len;
            int weight = 2;
            if (_bh >= 2 && _bh <= n4h)
            {
                int inc = Math.Min(-maxColOffset + colOffset + 1, Av1Tables.MiSizeWide[candidate.Size]);
                weight = Math.Max(weight, inc);
                processedCols = inc - colOffset - 1;
            }

            AddInterCandidate(candidate, rf0, rf1, len * weight, ref matchCount, ref newMvCount);
            i += len;
        }
    }

    private void ScanInterBlock(int rowOffset, int colOffset, int rf0, int rf1, ref int matchCount, ref int newMvCount)
    {
        int row = _miRow + rowOffset, col = _miCol + colOffset;
        if (row >= MiRowStart && col >= MiColStart && row < MiRowEnd && col < MiColEnd)
        {
            AddInterCandidate(GridAt(rowOffset, colOffset)!, rf0, rf1, 2 * 2, ref matchCount, ref newMvCount);
        }
    }

    /// <summary>add_ref_mv_candidate.</summary>
    private void AddInterCandidate(Av1BlockInfo candidate, int rf0, int rf1, int weight, ref int matchCount, ref int newMvCount)
    {
        if (!candidate.IsInter)
        {
            return;
        }

        if (rf1 == Ref.None)
        {
            for (int list = 0; list < 2; list++)
            {
                if ((list == 0 ? candidate.RefFrame0 : candidate.RefFrame1) != rf0)
                {
                    continue;
                }

                (int row, int col) = IsGlobalMvBlock(candidate, rf0) ? (_gmRow0, _gmCol0)
                    : list == 0 ? (candidate.MvRow, candidate.MvCol) : (candidate.Mv1Row, candidate.Mv1Col);
                int index = 0;
                for (; index < _mvCount; index++)
                {
                    if (_mvRow0[index] == row && _mvCol0[index] == col)
                    {
                        _mvWeight[index] += weight;
                        break;
                    }
                }

                if (index == _mvCount && _mvCount < MaxRefMvStack)
                {
                    _mvRow0[index] = row;
                    _mvCol0[index] = col;
                    _mvWeight[index] = weight;
                    _mvCount++;
                }

                newMvCount += InterMode.HasNew(candidate.YMode) ? 1 : 0;
                matchCount++;
            }
        }
        else if (candidate.RefFrame0 == rf0 && candidate.RefFrame1 == rf1)
        {
            (int row0, int col0) = IsGlobalMvBlock(candidate, rf0) ? (_gmRow0, _gmCol0) : (candidate.MvRow, candidate.MvCol);
            (int row1, int col1) = IsGlobalMvBlock(candidate, rf1) ? (_gmRow1, _gmCol1) : (candidate.Mv1Row, candidate.Mv1Col);
            int index = 0;
            for (; index < _mvCount; index++)
            {
                if (_mvRow0[index] == row0 && _mvCol0[index] == col0 && _mvRow1[index] == row1 && _mvCol1[index] == col1)
                {
                    _mvWeight[index] += weight;
                    break;
                }
            }

            if (index == _mvCount && _mvCount < MaxRefMvStack)
            {
                _mvRow0[index] = row0;
                _mvCol0[index] = col0;
                _mvRow1[index] = row1;
                _mvCol1[index] = col1;
                _mvWeight[index] = weight;
                _mvCount++;
            }

            newMvCount += InterMode.HasNew(candidate.YMode) ? 1 : 0;
            matchCount++;
        }
    }

    /// <summary>add_tpl_ref_mv: a candidate from the motion field projected from the reference frames.</summary>
    private bool AddTemporalCandidate(int blkRow, int blkCol, int rf0, int rf1)
    {
        int row = (_miRow & 1) != 0 ? blkRow : blkRow + 1;
        int col = (_miCol & 1) != 0 ? blkCol : blkCol + 1;
        int r = _miRow + row, c = _miCol + col;
        if (r < MiRowStart || c < MiColStart || r >= MiRowEnd || c >= MiColEnd)
        {
            return false;
        }

        int at = ((r >> 1) * _frame.MotionFieldStride) + (c >> 1);
        if (_frame.MotionFieldOffsets![at] == 0)
        {
            return false;
        }

        int mfRow = _frame.MotionFieldRows![at], mfCol = _frame.MotionFieldCols![at], offset = _frame.MotionFieldOffsets[at];
        int cur = _header.OrderHint;
        bool hp = _header.AllowHighPrecisionMv, integer = _header.ForceIntegerMv;
        (int row0, int col0) = Lower(Av1MotionField.Project(mfRow, mfCol, _header.RelativeDist(cur, _header.OrderHints[rf0]), offset), hp, integer);
        if (rf1 == Ref.None)
        {
            if (blkRow == 0 && blkCol == 0 && (Math.Abs(row0 - _gmRow0) >= 16 || Math.Abs(col0 - _gmCol0) >= 16))
            {
                _modeContext |= 1 << 3;
            }

            int idx = 0;
            for (; idx < _mvCount; idx++)
            {
                if (_mvRow0[idx] == row0 && _mvCol0[idx] == col0)
                {
                    break;
                }
            }

            if (idx < _mvCount)
            {
                _mvWeight[idx] += 2;
            }
            else if (_mvCount < MaxRefMvStack)
            {
                _mvRow0[idx] = row0;
                _mvCol0[idx] = col0;
                _mvWeight[idx] = 2;
                _mvCount++;
            }
        }
        else
        {
            (int row1, int col1) = Lower(Av1MotionField.Project(mfRow, mfCol, _header.RelativeDist(cur, _header.OrderHints[rf1]), offset), hp, integer);
            if (blkRow == 0 && blkCol == 0 && (Math.Abs(row0 - _gmRow0) >= 16 || Math.Abs(col0 - _gmCol0) >= 16
                || Math.Abs(row1 - _gmRow1) >= 16 || Math.Abs(col1 - _gmCol1) >= 16))
            {
                _modeContext |= 1 << 3;
            }

            int idx = 0;
            for (; idx < _mvCount; idx++)
            {
                if (_mvRow0[idx] == row0 && _mvCol0[idx] == col0 && _mvRow1[idx] == row1 && _mvCol1[idx] == col1)
                {
                    break;
                }
            }

            if (idx < _mvCount)
            {
                _mvWeight[idx] += 2;
            }
            else if (_mvCount < MaxRefMvStack)
            {
                _mvRow0[idx] = row0;
                _mvCol0[idx] = col0;
                _mvRow1[idx] = row1;
                _mvCol1[idx] = col1;
                _mvWeight[idx] = 2;
                _mvCount++;
            }
        }

        return true;
    }

    /// <summary>process_single_ref_mv_candidate: any inter neighbour's vector, its sign matched to the reference's.</summary>
    private void AddSingleExtra(Av1BlockInfo candidate, int frame)
    {
        for (int list = 0; list < 2; list++)
        {
            int candFrame = list == 0 ? candidate.RefFrame0 : candidate.RefFrame1;
            if (candFrame <= Ref.Intra)
            {
                continue;
            }

            (int row, int col) = list == 0 ? (candidate.MvRow, candidate.MvCol) : (candidate.Mv1Row, candidate.Mv1Col);
            if (_header.RefFrameSignBias[candFrame] != _header.RefFrameSignBias[frame])
            {
                (row, col) = (-row, -col);
            }

            int idx = 0;
            for (; idx < _mvCount; idx++)
            {
                if (_mvRow0[idx] == row && _mvCol0[idx] == col)
                {
                    break;
                }
            }

            if (idx == _mvCount)
            {
                _mvRow0[idx] = row;
                _mvCol0[idx] = col;
                _mvWeight[idx] = 2;
                _mvCount++;
            }
        }
    }

    /// <summary>The compound stack's extension from the neighbours' vectors (setup_ref_mv_list's compound branch).</summary>
    private void ExtendCompoundStack(int maxRowOffset, int maxColOffset, int miSize, int rf0, int rf1)
    {
        Span<int> idRow = stackalloc int[4], idCol = stackalloc int[4], diffRow = stackalloc int[4], diffCol = stackalloc int[4];
        Span<int> idCount = stackalloc int[2], diffCount = stackalloc int[2];
        for (int idx = 0; Math.Abs(maxRowOffset) >= 1 && idx < miSize;)
        {
            Av1BlockInfo n = GridAt(-1, idx)!;
            Process(n, idRow, idCol, idCount, diffRow, diffCol, diffCount);
            idx += Av1Tables.MiSizeWide[n.Size];
        }

        for (int idx = 0; Math.Abs(maxColOffset) >= 1 && idx < miSize;)
        {
            Av1BlockInfo n = GridAt(idx, -1)!;
            Process(n, idRow, idCol, idCount, diffRow, diffCol, diffCount);
            idx += Av1Tables.MiSizeHigh[n.Size];
        }

        // comp_list[candidate][reference]
        Span<int> listRow = stackalloc int[4], listCol = stackalloc int[4];
        for (int r = 0; r < 2; r++)
        {
            int k = 0;
            for (int i = 0; i < idCount[r] && k < 2; i++, k++)
            {
                listRow[(k * 2) + r] = idRow[(r * 2) + i];
                listCol[(k * 2) + r] = idCol[(r * 2) + i];
            }

            for (int i = 0; i < diffCount[r] && k < 2; i++, k++)
            {
                listRow[(k * 2) + r] = diffRow[(r * 2) + i];
                listCol[(k * 2) + r] = diffCol[(r * 2) + i];
            }

            for (; k < 2; k++)
            {
                listRow[(k * 2) + r] = r == 0 ? _gmRow0 : _gmRow1;
                listCol[(k * 2) + r] = r == 0 ? _gmCol0 : _gmCol1;
            }
        }

        if (_mvCount == 1)
        {
            int pick = listRow[0] == _mvRow0[0] && listCol[0] == _mvCol0[0] && listRow[1] == _mvRow1[0] && listCol[1] == _mvCol1[0] ? 1 : 0;
            Put(1, pick, listRow, listCol);
        }
        else
        {
            Put(0, 0, listRow, listCol);
            Put(1, 1, listRow, listCol);
        }

        void Put(int index, int pick, Span<int> listRow, Span<int> listCol)
        {
            _mvRow0[index] = listRow[pick * 2];
            _mvCol0[index] = listCol[pick * 2];
            _mvRow1[index] = listRow[(pick * 2) + 1];
            _mvCol1[index] = listCol[(pick * 2) + 1];
            _mvWeight[index] = 2;
            _mvCount = index + 1;
        }

        void Process(Av1BlockInfo n, Span<int> idRow, Span<int> idCol, Span<int> idCount, Span<int> diffRow, Span<int> diffCol, Span<int> diffCount)
        {
            for (int list = 0; list < 2; list++)
            {
                int candFrame = list == 0 ? n.RefFrame0 : n.RefFrame1;
                (int row, int col) = list == 0 ? (n.MvRow, n.MvCol) : (n.Mv1Row, n.Mv1Col);
                for (int cmp = 0; cmp < 2; cmp++)
                {
                    int target = cmp == 0 ? rf0 : rf1;
                    if (candFrame == target && idCount[cmp] < 2)
                    {
                        idRow[(cmp * 2) + idCount[cmp]] = row;
                        idCol[(cmp * 2) + idCount[cmp]] = col;
                        idCount[cmp]++;
                    }
                    else if (candFrame > Ref.Intra && diffCount[cmp] < 2)
                    {
                        bool flip = _header.RefFrameSignBias[candFrame] != _header.RefFrameSignBias[target];
                        diffRow[(cmp * 2) + diffCount[cmp]] = flip ? -row : row;
                        diffCol[(cmp * 2) + diffCount[cmp]] = flip ? -col : col;
                        diffCount[cmp]++;
                    }
                }
            }
        }
    }
}
