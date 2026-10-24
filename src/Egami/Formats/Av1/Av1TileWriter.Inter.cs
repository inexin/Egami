namespace Egami.Formats.Av1;

/// <summary>
/// The mode info of inter frames as the writer codes it: every symbol <see cref="Av1TileParser"/>'s inter frame mode info reads, in
/// its order, from the block's decisions (libaom's bitstream.c writes the same syntax; BSD 2-clause, see THIRD-PARTY-NOTICES). The
/// vectors and modes the decoder derives rather than reads are checked against the block, so a decision the syntax cannot carry
/// fails here instead of decoding differently.
/// </summary>
internal partial class Av1TileCoder
{
    /// <summary>Codes the current block's mode info, as an intra frame or an inter frame codes it.</summary>
    private protected void CodeModeInfo<TSink>(ref TSink s)
        where TSink : struct, IAv1SymbolSink
    {
        if (_header.FrameIsIntra)
        {
            CodeIntraFrameModeInfo(ref s);
        }
        else
        {
            CodeInterFrameModeInfo(ref s);
        }
    }

    private void CodeInterFrameModeInfo<TSink>(ref TSink s)
        where TSink : struct, IAv1SymbolSink
    {
        Av1BlockInfo b = _b;
        CodeInterSegmentId(ref s, true);
        if (SkipModeAllowed(b.SegmentId))
        {
            s.Symbol(_cdf, Av1FrameContext.SkipMode[SkipModeContext()], 2, b.SkipMode ? 1 : 0);
        }
        else if (b.SkipMode)
        {
            throw new InvalidOperationException("The frame or the block's segment does not allow skip mode.");
        }

        if (b.SkipMode && !b.Skip)
        {
            throw new InvalidOperationException("A skip mode block has no residual.");
        }

        if (!b.SkipMode)
        {
            if (!SegFeatureActive(b.SegmentId, 6))
            {
                s.Symbol(_cdf, Av1FrameContext.SkipTxfm[SkipContext()], 2, b.Skip ? 1 : 0);
            }
            else if (!b.Skip)
            {
                throw new InvalidOperationException("A block of a skip segment must skip its residual.");
            }
        }

        if (!_header.SegIdPreSkip)
        {
            CodeInterSegmentId(ref s, false);
        }

        CodeCdef(ref s);
        CodeDeltaQParams(ref s);
        if (!b.SkipMode)
        {
            CodeIsInter(ref s);
        }

        if (b.UseIntrabc)
        {
            throw new InvalidOperationException("An inter frame has no intra block copy.");
        }

        if (b.IsInter)
        {
            CodeInterBlockModeInfo(ref s);
        }
        else
        {
            if (b.RefFrame1 != Ref.None)
            {
                throw new InvalidOperationException("An intra block has no second reference.");
            }

            CodeIntraModes(ref s, Av1FrameContext.IfYMode[Av1Tables.SizeGroupLookup[b.Size]]);
        }
    }

    /// <summary>Codes the segment of a block of an inter frame (read_inter_segment_id), which the encoder always codes spatially.</summary>
    private void CodeInterSegmentId<TSink>(ref TSink s, bool preskip)
        where TSink : struct, IAv1SymbolSink
    {
        Av1BlockInfo b = _b;
        if (!_header.SegmentationEnabled)
        {
            return;
        }

        if (!_header.SegmentationUpdateMap)
        {
            int xMis = Math.Min(_bw, _miCols - _miCol), yMis = Math.Min(_bh, _frame.MiRows - _miRow);
            if (b.SegmentId != PredictedSegmentId(_frame.PreviousSegmentMap, xMis, yMis))
            {
                throw new InvalidOperationException("A frame that keeps the segment map cannot change a block's segment.");
            }

            return;
        }

        if (preskip)
        {
            if (!_header.SegIdPreSkip)
            {
                return;
            }
        }
        else if (b.Skip)
        {
            if (b.SegmentId != SegmentPrediction().Pred)
            {
                throw new InvalidOperationException("A block without residual takes the segment its neighbours predict.");
            }

            return;
        }

        if (_header.SegmentationTemporalUpdate)
        {
            throw new InvalidOperationException("The encoder codes segments spatially.");
        }

        (int cdfIndex, int pred) = SegmentPrediction();
        s.Symbol(_cdf, Av1FrameContext.SpatialPredSegTree[cdfIndex], 8, NegInterleave(b.SegmentId, pred, _header.LastActiveSegId + 1));
    }

    private void CodeIsInter<TSink>(ref TSink s)
        where TSink : struct, IAv1SymbolSink
    {
        Av1BlockInfo b = _b;
        if (SegFeatureActive(b.SegmentId, 5))
        {
            if (b.IsInter != (_header.FeatureData[b.SegmentId, 5] != Ref.Intra))
            {
                throw new InvalidOperationException("A block's segment fixes its reference frame.");
            }

            return;
        }

        if (SegFeatureActive(b.SegmentId, 7))
        {
            if (!b.IsInter)
            {
                throw new InvalidOperationException("A block of a global motion segment is inter.");
            }

            return;
        }

        s.Symbol(_cdf, Av1FrameContext.IntraInter[IntraInterContext()], 2, b.IsInter ? 1 : 0);
    }

    /// <summary>Codes an inter block's references, mode, vectors and prediction tools (read_inter_block_mode_info).</summary>
    private void CodeInterBlockModeInfo<TSink>(ref TSink s)
        where TSink : struct, IAv1SymbolSink
    {
        Av1BlockInfo b = _b;
        int bsize = b.Size;
        if (b.UvMode != Pred.Dc)
        {
            throw new InvalidOperationException("An inter block's chroma mode is DC.");
        }

        CollectNeighborRefCounts();
        CodeRefFrames(ref s);
        bool compound = b.HasSecondRef;
        if (compound != InterMode.IsCompound(b.YMode))
        {
            throw new InvalidOperationException("An inter block's mode does not match its number of references.");
        }

        BlockMvRefs();
        if (b.SkipMode)
        {
            Require(b.YMode == InterMode.NearestNearest, "A skip mode block uses its nearest vectors.");
        }
        else if (SegFeatureActive(b.SegmentId, 6) || SegFeatureActive(b.SegmentId, 7))
        {
            Require(b.YMode == InterMode.Global, "A block of a skip or global motion segment uses global motion.");
        }
        else
        {
            int modeCtx = _modeContext;
            if (compound)
            {
                int newCtx = modeCtx & 7, refCtx = (modeCtx >> 4) & 15;
                s.Symbol(_cdf, Av1FrameContext.InterCompoundMode[CompoundModeContexts[refCtx >> 1][Math.Min(newCtx, 4)]], 8, b.YMode - InterMode.NearestNearest);
            }
            else
            {
                s.Symbol(_cdf, Av1FrameContext.Newmv[modeCtx & 7], 2, b.YMode == InterMode.New ? 0 : 1);
                if (b.YMode != InterMode.New)
                {
                    s.Symbol(_cdf, Av1FrameContext.Zeromv[(modeCtx >> 3) & 1], 2, b.YMode == InterMode.Global ? 0 : 1);
                    if (b.YMode != InterMode.Global)
                    {
                        s.Symbol(_cdf, Av1FrameContext.Refmv[(modeCtx >> 4) & 15], 2, b.YMode == InterMode.Nearest ? 0 : 1);
                    }
                }
            }

            CodeDrl(ref s);
        }

        if (b.RefMvIdx != 0 && b.YMode is not (InterMode.New or InterMode.NewNew) && !InterMode.HasNear(b.YMode))
        {
            throw new InvalidOperationException("Only new and near modes have a reference vector index.");
        }

        BlockVectors v = Vectors(b.RefMvIdx);
        CodeVectors(ref s, v);

        bool interintraAllowed = _sequence.EnableInterintraCompound && !b.SkipMode && bsize >= Bs.B8x8 && bsize <= Bs.B32x32
            && b.YMode >= InterMode.Nearest && b.YMode <= InterMode.New && b.RefFrame0 > Ref.Intra && b.RefFrame1 <= Ref.Intra;
        bool interintra = b.RefFrame1 == Ref.Intra;
        Require(interintraAllowed || !interintra, "The block cannot use inter-intra.");
        if (interintraAllowed)
        {
            int group = Av1Tables.SizeGroupLookup[bsize];
            s.Symbol(_cdf, Av1FrameContext.Interintra[group], 2, interintra ? 1 : 0);
            if (interintra)
            {
                s.Symbol(_cdf, Av1FrameContext.InterintraMode[group], 4, b.InterintraMode);
                if (WedgeUsed(bsize))
                {
                    s.Symbol(_cdf, Av1FrameContext.WedgeInterintra[bsize], 2, b.UseWedgeInterintra ? 1 : 0);
                    if (b.UseWedgeInterintra)
                    {
                        s.Symbol(_cdf, Av1FrameContext.WedgeIdx[bsize], 16, b.InterintraWedgeIndex);
                    }
                }
                else
                {
                    Require(!b.UseWedgeInterintra, "The block's size has no inter-intra wedges.");
                }
            }
        }

        int numSamples = 0;
        if (Math.Min(Av1Tables.BlockSizeWide[bsize], Av1Tables.BlockSizeHigh[bsize]) >= 8 && !b.SkipMode && !b.HasSecondRef)
        {
            numSamples = FindSamples();
        }

        int allowed = b.RefFrame1 != Ref.Intra ? MotionModesAllowed(HasOverlappableNeighbors(), numSamples) : 0;
        Require(b.MotionMode <= allowed, "The block cannot use its motion mode.");
        if (allowed == 2)
        {
            s.Symbol(_cdf, Av1FrameContext.MotionMode[bsize], 3, b.MotionMode);
        }
        else if (allowed == 1)
        {
            s.Symbol(_cdf, Av1FrameContext.Obmc[bsize], 2, b.MotionMode);
        }

        if (b.HasSecondRef && !b.SkipMode)
        {
            if (CompRefAllowed(bsize) && _sequence.EnableMaskedCompound)
            {
                s.Symbol(_cdf, Av1FrameContext.CompGroupIdx[CompGroupIdxContext()], 2, b.CompGroupIdx);
            }
            else
            {
                Require(b.CompGroupIdx == 0, "The sequence has no masked compounds.");
            }

            if (b.CompGroupIdx == 0)
            {
                if (_sequence.EnableJntComp)
                {
                    s.Symbol(_cdf, Av1FrameContext.CompoundIdx[CompoundIdxContext()], 2, b.CompoundIdx);
                }
                else
                {
                    Require(b.CompoundIdx == 1, "The sequence has no distance-weighted compounds.");
                }

                Require(b.CompoundType == (b.CompoundIdx != 0 ? Compound.Average : Compound.Distance), "A block's compound type does not match its index.");
            }
            else if (WedgeUsed(bsize))
            {
                Require(b.CompoundType is Compound.Wedge or Compound.DiffWeighted, "A masked compound is a wedge or difference-weighted.");
                s.Symbol(_cdf, Av1FrameContext.CompoundType[bsize], 2, b.CompoundType - Compound.Wedge);
                if (b.CompoundType == Compound.Wedge)
                {
                    s.Symbol(_cdf, Av1FrameContext.WedgeIdx[bsize], 16, b.WedgeIndex);
                    s.Bit(b.WedgeSign);
                }
                else
                {
                    s.Literal(b.MaskType, 1);
                }
            }
            else
            {
                Require(b.CompoundType == Compound.DiffWeighted, "The block's size has no compound wedges.");
                s.Literal(b.MaskType, 1);
            }
        }
        else if (!b.SkipMode)
        {
            Require(b.CompGroupIdx == 0 && b.CompoundIdx == 1 && b.CompoundType == Compound.Average, "A single reference block has no compound type.");
        }

        if (InterpFilterCoded(out int implied))
        {
            s.Symbol(_cdf, Av1FrameContext.SwitchableInterp[InterpContext(0)], 3, b.FilterY);
            if (_sequence.EnableDualFilter)
            {
                s.Symbol(_cdf, Av1FrameContext.SwitchableInterp[InterpContext(1)], 3, b.FilterX);
            }
            else
            {
                Require(b.FilterX == b.FilterY, "Without dual filters both directions share one filter.");
            }
        }
        else
        {
            Require(b.FilterX == implied && b.FilterY == implied, "The block's filters are fixed by its frame or mode.");
        }
    }

    /// <summary>Codes the reference vector index of a new or near mode (the drl_mode symbols).</summary>
    private void CodeDrl<TSink>(ref TSink s)
        where TSink : struct, IAv1SymbolSink
    {
        Av1BlockInfo b = _b;
        int idx = b.RefMvIdx;
        if (b.YMode is InterMode.New or InterMode.NewNew)
        {
            for (int i = 0; i < 2; i++)
            {
                if (_mvCount > i + 1)
                {
                    int drl = idx > i ? 1 : 0;
                    s.Symbol(_cdf, Av1FrameContext.Drl[DrlContext(i)], 2, drl);
                    if (drl == 0)
                    {
                        return;
                    }
                }
            }

            Require(idx <= Math.Max(0, Math.Min(2, _mvCount - 1)), "A new mode's reference vector index is past the stack.");
        }
        else if (InterMode.HasNear(b.YMode))
        {
            for (int i = 1; i < 3; i++)
            {
                if (_mvCount > i + 1)
                {
                    int drl = idx > i - 1 ? 1 : 0;
                    s.Symbol(_cdf, Av1FrameContext.Drl[DrlContext(i)], 2, drl);
                    if (drl == 0)
                    {
                        return;
                    }
                }
            }

            Require(idx <= Math.Max(0, Math.Min(2, _mvCount - 2)), "A near mode's reference vector index is past the stack.");
        }
    }

    /// <summary>Codes the new vectors of the block's mode against their references, and checks the vectors it derives.</summary>
    private void CodeVectors<TSink>(ref TSink s, BlockVectors v)
        where TSink : struct, IAv1SymbolSink
    {
        Av1BlockInfo b = _b;
        (int r0, int c0, int r1, int c1) = b.YMode switch
        {
            InterMode.Nearest => (v.NearestRow0, v.NearestCol0, 0, 0),
            InterMode.Near => (v.NearRow0, v.NearCol0, 0, 0),
            InterMode.Global => (_gmRow0, _gmCol0, 0, 0),
            InterMode.NearestNearest => (v.NearestRow0, v.NearestCol0, v.NearestRow1, v.NearestCol1),
            InterMode.NearNear => (v.NearRow0, v.NearCol0, v.NearRow1, v.NearCol1),
            InterMode.NewNearest => (b.MvRow, b.MvCol, v.NearestRow1, v.NearestCol1),
            InterMode.NearestNew => (v.NearestRow0, v.NearestCol0, b.Mv1Row, b.Mv1Col),
            InterMode.NearNew => (v.NearRow0, v.NearCol0, b.Mv1Row, b.Mv1Col),
            InterMode.NewNear => (b.MvRow, b.MvCol, v.NearRow1, v.NearCol1),
            InterMode.GlobalGlobal => (_gmRow0, _gmCol0, _gmRow1, _gmCol1),
            _ => (b.MvRow, b.MvCol, b.Mv1Row, b.Mv1Col),
        };
        Require(r0 == b.MvRow && c0 == b.MvCol && (!b.HasSecondRef || (r1 == b.Mv1Row && c1 == b.Mv1Col)),
            $"The block's vectors ({b.MvRow}, {b.MvCol}) do not match the ({r0}, {c0}) its mode {b.YMode} derives.");
        if (InterMode.First(b.YMode) == InterMode.New)
        {
            CodeMv(ref s, b.MvRow - v.RefRow0, b.MvCol - v.RefCol0);
        }

        if (b.HasSecondRef && InterMode.Second(b.YMode) == InterMode.New)
        {
            CodeMv(ref s, b.Mv1Row - v.RefRow1, b.Mv1Col - v.RefCol1);
        }
    }

    /// <summary>Codes a vector difference at the frame's precision (read_mv).</summary>
    private protected void CodeMv<TSink>(ref TSink s, int row, int col)
        where TSink : struct, IAv1SymbolSink
    {
        Av1FrameContext.MvTables t = Av1FrameContext.Mv;
        int precision = _header.ForceIntegerMv ? -1 : _header.AllowHighPrecisionMv ? 1 : 0;
        s.Symbol(_cdf, t.Joints[0], 4, (row != 0 ? 2 : 0) | (col != 0 ? 1 : 0));
        if (row != 0)
        {
            CodeMvComponent(ref s, t, 0, row, precision);
        }

        if (col != 0)
        {
            CodeMvComponent(ref s, t, 1, col, precision);
        }
    }

    /// <summary>Codes one nonzero component of a vector difference: sign, class, integer bits, fraction and high precision bit (read_mv_component).</summary>
    private protected void CodeMvComponent<TSink>(ref TSink s, Av1FrameContext.MvTables t, int comp, int value, int precision)
        where TSink : struct, IAv1SymbolSink
    {
        int z = Math.Abs(value) - 1;
        int mvClass = z >= 2 * 4096 ? 10 : z < 16 ? 0 : 31 - int.LeadingZeroCount(z >> 3);
        int offset = z - (mvClass == 0 ? 0 : 2 << (mvClass + 2));
        int d = offset >> 3, fr = (offset >> 1) & 3, hp = offset & 1;
        Require(precision >= 0 || (fr == 3 && hp == 1), "A vector of an integer vector frame is a whole number of samples.");
        Require(precision > 0 || hp == 1, "A vector of a frame without high precision is in quarter samples.");
        Require(mvClass < 10 || d < 1 << 10, "A vector difference is out of range.");
        s.Symbol(_cdf, t.Sign[comp], 2, value < 0 ? 1 : 0);
        s.Symbol(_cdf, t.Classes[comp], 11, mvClass);
        if (mvClass == 0)
        {
            s.Symbol(_cdf, t.Class0[comp], 2, d);
        }
        else
        {
            for (int i = 0; i < mvClass; i++)
            {
                s.Symbol(_cdf, t.Bits[(comp * 10) + i], 2, (d >> i) & 1);
            }
        }

        if (precision >= 0)
        {
            s.Symbol(_cdf, mvClass == 0 ? t.Class0Fp[(comp * 2) + d] : t.Fp[comp], 4, fr);
            if (precision > 0)
            {
                s.Symbol(_cdf, mvClass == 0 ? t.Class0Hp[comp] : t.Hp[comp], 2, hp);
            }
        }
    }

    private void RefBit<TSink>(ref TSink s, Av1FrameContext.Table table, int perCtx, int n, int ctx, bool bit)
        where TSink : struct, IAv1SymbolSink => s.Symbol(_cdf, table[(ctx * perCtx) + n], 2, bit ? 1 : 0);

    /// <summary>Codes the block's reference frames (read_ref_frames).</summary>
    private void CodeRefFrames<TSink>(ref TSink s)
        where TSink : struct, IAv1SymbolSink
    {
        Av1BlockInfo b = _b;
        int r0 = b.RefFrame0, r1 = b.HasSecondRef ? b.RefFrame1 : Ref.None;
        if (b.SkipMode)
        {
            Require(r0 == _header.SkipModeFrame[0] && r1 == _header.SkipModeFrame[1], "A skip mode block uses the frame's skip mode references.");
            return;
        }

        if (SegFeatureActive(b.SegmentId, 5))
        {
            Require(r0 == _header.FeatureData[b.SegmentId, 5] && r1 == Ref.None, "A block's segment fixes its reference frame.");
            return;
        }

        if (SegFeatureActive(b.SegmentId, 6) || SegFeatureActive(b.SegmentId, 7))
        {
            Require(r0 == Ref.Last && r1 == Ref.None, "A block of a skip or global motion segment predicts from the last frame.");
            return;
        }

        int fwd = RefCount(Ref.Last) + RefCount(Ref.Last2) + RefCount(Ref.Last3) + RefCount(Ref.Golden);
        int bwd = RefCount(Ref.Bwd) + RefCount(Ref.Alt2) + RefCount(Ref.Alt);
        int ll2VsL3g = CountContext(RefCount(Ref.Last) + RefCount(Ref.Last2), RefCount(Ref.Last3) + RefCount(Ref.Golden));
        int lVsL2 = CountContext(RefCount(Ref.Last), RefCount(Ref.Last2));
        int l3VsG = CountContext(RefCount(Ref.Last3), RefCount(Ref.Golden));
        int bA2VsA = CountContext(RefCount(Ref.Bwd) + RefCount(Ref.Alt2), RefCount(Ref.Alt));
        int bVsA2 = CountContext(RefCount(Ref.Bwd), RefCount(Ref.Alt2));
        bool compound = r1 > Ref.Intra;
        if (_header.ReferenceSelect && CompRefAllowed(b.Size))
        {
            s.Symbol(_cdf, Av1FrameContext.CompInter[ReferenceModeContext()], 2, compound ? 1 : 0);
        }
        else
        {
            Require(!compound, "The frame or the block's size has no compound prediction.");
        }

        if (compound)
        {
            bool bidir = IsBackward(r0) != IsBackward(r1);
            s.Symbol(_cdf, Av1FrameContext.CompRefType[CompReferenceTypeContext()], 2, bidir ? 1 : 0);
            if (!bidir)
            {
                Require((r0, r1) is (Ref.Bwd, Ref.Alt) or (Ref.Last, Ref.Last2) or (Ref.Last, Ref.Last3) or (Ref.Last, Ref.Golden),
                    "A one-direction compound pairs the last frame with last 2, last 3 or golden, or backward with alternate.");
                RefBit(ref s, Av1FrameContext.UniCompRef, 3, 0, CountContext(fwd, bwd), r0 == Ref.Bwd);
                if (r0 == Ref.Bwd)
                {
                    return;
                }

                RefBit(ref s, Av1FrameContext.UniCompRef, 3, 1, CountContext(RefCount(Ref.Last2), RefCount(Ref.Last3) + RefCount(Ref.Golden)), r1 != Ref.Last2);
                if (r1 != Ref.Last2)
                {
                    RefBit(ref s, Av1FrameContext.UniCompRef, 3, 2, l3VsG, r1 == Ref.Golden);
                }

                return;
            }

            Require(!IsBackward(r0) && IsBackward(r1), "A two-direction compound has a forward reference first.");
            bool l3g = r0 is Ref.Last3 or Ref.Golden;
            RefBit(ref s, Av1FrameContext.CompRef, 3, 0, ll2VsL3g, l3g);
            if (!l3g)
            {
                RefBit(ref s, Av1FrameContext.CompRef, 3, 1, lVsL2, r0 == Ref.Last2);
            }
            else
            {
                RefBit(ref s, Av1FrameContext.CompRef, 3, 2, l3VsG, r0 == Ref.Golden);
            }

            RefBit(ref s, Av1FrameContext.CompBwdref, 2, 0, bA2VsA, r1 == Ref.Alt);
            if (r1 != Ref.Alt)
            {
                RefBit(ref s, Av1FrameContext.CompBwdref, 2, 1, bVsA2, r1 == Ref.Alt2);
            }

            return;
        }

        bool backward = IsBackward(r0);
        RefBit(ref s, Av1FrameContext.SingleRef, 6, 0, CountContext(fwd, bwd), backward);
        if (backward)
        {
            RefBit(ref s, Av1FrameContext.SingleRef, 6, 1, bA2VsA, r0 == Ref.Alt);
            if (r0 != Ref.Alt)
            {
                RefBit(ref s, Av1FrameContext.SingleRef, 6, 5, bVsA2, r0 == Ref.Alt2);
            }
        }
        else
        {
            bool l3g = r0 is Ref.Last3 or Ref.Golden;
            RefBit(ref s, Av1FrameContext.SingleRef, 6, 2, ll2VsL3g, l3g);
            if (l3g)
            {
                RefBit(ref s, Av1FrameContext.SingleRef, 6, 4, l3VsG, r0 == Ref.Golden);
            }
            else
            {
                RefBit(ref s, Av1FrameContext.SingleRef, 6, 3, lVsL2, r0 == Ref.Last2);
            }
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
