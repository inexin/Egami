namespace Egami.Formats.Av1;

/// <summary>
/// The mode info of inter frames: segments from the previous frame, skip mode, intra and inter blocks, reference frames, the
/// reference vector stack with temporal candidates, vectors, motion modes, compound types and interpolation filters, as libaom's
/// decodemv.c, mvref_common.c and pred_common.c read and derive them (BSD 2-clause, see THIRD-PARTY-NOTICES).
/// </summary>
internal sealed partial class Av1TileParser
{
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

    private bool ReadSkipMode(int segment)
    {
        return SkipModeAllowed(segment) && Symbol(Av1FrameContext.SkipMode[SkipModeContext()], 2) != 0;
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

        return Symbol(Av1FrameContext.IntraInter[IntraInterContext()], 2) != 0;
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

        b.RefMvIdx = refMvIdx;
        BlockVectors v = Vectors(refMvIdx);
        AssignMv(b, v);

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
            SetLocalWarp(numSamples);
        }
    }

    private void AssignMv(Av1BlockInfo b, BlockVectors v)
    {
        (int refRow0, int refCol0, int refRow1, int refCol1) = (v.RefRow0, v.RefCol0, v.RefRow1, v.RefCol1);
        (int nearestRow0, int nearestCol0, int nearestRow1, int nearestCol1) = (v.NearestRow0, v.NearestCol0, v.NearestRow1, v.NearestCol1);
        (int nearRow0, int nearCol0, int nearRow1, int nearCol1) = (v.NearRow0, v.NearCol0, v.NearRow1, v.NearCol1);
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

    private int ReadMotionMode(bool overlappable, int numSamples)
    {
        int allowed = MotionModesAllowed(overlappable, numSamples);
        return allowed == 2 ? Symbol(Av1FrameContext.MotionMode[_b.Size], 3) : allowed == 1 ? Symbol(Av1FrameContext.Obmc[_b.Size], 2) : 0;
    }

    private void ReadInterpFilter()
    {
        Av1BlockInfo b = _b;
        if (!InterpFilterCoded(out int implied))
        {
            b.FilterY = b.FilterX = implied;
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
}
