namespace Egami.Video.Codecs.Vp9;

/// <summary>Mode info: segment ids, skip, transform size, intra modes, references and motion vectors (vp9_decodemv.c).</summary>
internal sealed partial class Vp9Decoder<T>
{
    internal sealed partial class Tile
    {
        private const uint InvalidMv = 0x80008000;

        private void ReadModeInfo(int miRow, int miCol, int xMis, int yMis)
        {
            Vp9Decoder<T> d = _d;
            ref Vp9ModeInfo mi = ref d._mi[_miIndex];
            if (d._header.FrameIsIntraOnly)
            {
                ReadIntraFrameModeInfo(ref mi, miRow, miCol, xMis, yMis);
                return;
            }

            ReadInterFrameModeInfo(ref mi, miRow, miCol, xMis, yMis);
            Vp9MvRef mv = new() { Ref0 = mi.Ref0, Ref1 = mi.Ref1, Mv0 = mi.Mv0, Mv1 = mi.Mv1 };
            int cols = d._miCols;
            for (int h = 0; h < yMis; h++)
            {
                d._mvsCurrent.AsSpan(((miRow + h) * cols) + miCol, xMis).Fill(mv);
            }
        }

        private void ReadIntraFrameModeInfo(ref Vp9ModeInfo mi, int miRow, int miCol, int xMis, int yMis)
        {
            int offset = (miRow * _d._miCols) + miCol;
            mi.SegmentId = (byte)ReadIntraSegmentId(offset, xMis, yMis);
            mi.Skip = (byte)ReadSkip(mi.SegmentId);
            mi.TxSize = (byte)ReadTxSize(ref mi, true);
            mi.Ref0 = Vp9Tables.IntraFrame;
            mi.Ref1 = Vp9Tables.NoRefFrame;
            switch (mi.SbType)
            {
                case Vp9Tables.Block4x4:
                    for (int i = 0; i < 4; i++)
                    {
                        mi.Bmi[i].Mode = (byte)ReadIntraMode(KfYModeProbs(ref mi, i));
                    }

                    mi.Mode = mi.Bmi.B3.Mode;
                    break;
                case Vp9Tables.Block4x8:
                    mi.Bmi.B0.Mode = mi.Bmi.B2.Mode = (byte)ReadIntraMode(KfYModeProbs(ref mi, 0));
                    mi.Bmi.B1.Mode = mi.Bmi.B3.Mode = mi.Mode = (byte)ReadIntraMode(KfYModeProbs(ref mi, 1));
                    break;
                case Vp9Tables.Block8x4:
                    mi.Bmi.B0.Mode = mi.Bmi.B1.Mode = (byte)ReadIntraMode(KfYModeProbs(ref mi, 0));
                    mi.Bmi.B2.Mode = mi.Bmi.B3.Mode = mi.Mode = (byte)ReadIntraMode(KfYModeProbs(ref mi, 2));
                    break;
                default:
                    mi.Mode = (byte)ReadIntraMode(KfYModeProbs(ref mi, 0));
                    break;
            }

            mi.UVMode = (byte)ReadIntraMode(Vp9Tables.KfUVModeProb.AsSpan(mi.Mode * 9, 9));
        }

        private ReadOnlySpan<byte> KfYModeProbs(ref Vp9ModeInfo mi, int block)
        {
            int above = AboveBlockMode(ref mi, block), left = LeftBlockMode(ref mi, block);
            return Vp9Tables.KfYModeProb.AsSpan(((above * 10) + left) * 9, 9);
        }

        private int AboveBlockMode(ref Vp9ModeInfo cur, int b)
        {
            if (b == 0 || b == 1)
            {
                if (_aboveMi < 0)
                {
                    return Vp9Tables.DcPred;
                }

                ref Vp9ModeInfo above = ref _d._mi[_aboveMi];
                return above.IsInter ? Vp9Tables.DcPred : YMode(ref above, b + 2);
            }

            return cur.Bmi[b - 2].Mode;
        }

        private int LeftBlockMode(ref Vp9ModeInfo cur, int b)
        {
            if (b == 0 || b == 2)
            {
                if (_leftMi < 0)
                {
                    return Vp9Tables.DcPred;
                }

                ref Vp9ModeInfo left = ref _d._mi[_leftMi];
                return left.IsInter ? Vp9Tables.DcPred : YMode(ref left, b + 1);
            }

            return cur.Bmi[b - 1].Mode;
        }

        private static int YMode(ref Vp9ModeInfo mi, int block) => mi.SbType < Vp9Tables.Block8x8 ? mi.Bmi[block].Mode : mi.Mode;

        private int ReadIntraMode(ReadOnlySpan<byte> probs) => _r.ReadTree(Vp9Tables.IntraModeTree, probs);

        private int ReadIntraModeY(int sizeGroup)
        {
            int mode = ReadIntraMode(_d._fc.P.AsSpan(Vp9FrameContext.YMode + (sizeGroup * 9), 9));
            if (CountsEnabled)
            {
                Counts.YMode[(sizeGroup * 10) + mode]++;
            }

            return mode;
        }

        private int ReadIntraModeUV(int yMode)
        {
            int mode = ReadIntraMode(_d._fc.P.AsSpan(Vp9FrameContext.UVMode + (yMode * 9), 9));
            if (CountsEnabled)
            {
                Counts.UVMode[(yMode * 10) + mode]++;
            }

            return mode;
        }

        private int ReadSegmentId() => _r.ReadTree(Vp9Tables.SegmentTree, _d._segTreeProbs);

        private int ReadIntraSegmentId(int offset, int xMis, int yMis)
        {
            Vp9Decoder<T> d = _d;
            if (!d._segEnabled)
            {
                return 0;
            }

            if (!d._segUpdateMap)
            {
                CopySegmentId(offset, xMis, yMis);
                return 0;
            }

            int id = ReadSegmentId();
            SetSegmentId(offset, xMis, yMis, id);
            return id;
        }

        private int ReadInterSegmentId(ref Vp9ModeInfo mi, int miRow, int miCol, int xMis, int yMis)
        {
            Vp9Decoder<T> d = _d;
            if (!d._segEnabled)
            {
                return 0;
            }

            int offset = (miRow * d._miCols) + miCol;
            int predicted = int.MaxValue;
            for (int y = 0; y < yMis; y++)
            {
                for (int x = 0; x < xMis; x++)
                {
                    predicted = Math.Min(predicted, d._segMapLast[offset + (y * d._miCols) + x]);
                }
            }

            if (!d._segUpdateMap)
            {
                CopySegmentId(offset, xMis, yMis);
                return predicted;
            }

            int id;
            if (d._segTemporalUpdate)
            {
                int ctx = (_aboveMi >= 0 ? d._mi[_aboveMi].SegIdPredicted : 0) + (_leftMi >= 0 ? d._mi[_leftMi].SegIdPredicted : 0);
                mi.SegIdPredicted = (byte)_r.Read(d._segPredProbs[ctx]);
                id = mi.SegIdPredicted != 0 ? predicted : ReadSegmentId();
            }
            else
            {
                id = ReadSegmentId();
            }

            SetSegmentId(offset, xMis, yMis, id);
            return id;
        }

        private void SetSegmentId(int offset, int xMis, int yMis, int id)
        {
            for (int y = 0; y < yMis; y++)
            {
                _d._segMapCurrent.AsSpan(offset + (y * _d._miCols), xMis).Fill((byte)id);
            }
        }

        private void CopySegmentId(int offset, int xMis, int yMis)
        {
            for (int y = 0; y < yMis; y++)
            {
                int at = offset + (y * _d._miCols);
                _d._segMapLast.AsSpan(at, xMis).CopyTo(_d._segMapCurrent.AsSpan(at, xMis));
            }
        }

        private int ReadSkip(int segmentId)
        {
            if (_d.SegFeatureActive(segmentId, Vp9Tables.SegLvlSkip))
            {
                return 1;
            }

            int ctx = (_aboveMi >= 0 ? _d._mi[_aboveMi].Skip : 0) + (_leftMi >= 0 ? _d._mi[_leftMi].Skip : 0);
            int skip = _r.Read(_d._fc.P[Vp9FrameContext.Skip + ctx]);
            if (CountsEnabled)
            {
                Counts.Skip[(ctx * 2) + skip]++;
            }

            return skip;
        }

        private int ReadTxSize(ref Vp9ModeInfo mi, bool allowSelect)
        {
            int maxTx = Vp9Tables.MaxTxSizeLookup[mi.SbType];
            if (allowSelect && _d._txMode == Vp9Tables.TxModeSelect && mi.SbType >= Vp9Tables.Block8x8)
            {
                return ReadSelectedTxSize(maxTx);
            }

            return Math.Min(maxTx, (int)Vp9Tables.TxModeToBiggestTxSize[_d._txMode]);
        }

        private int ReadSelectedTxSize(int maxTx)
        {
            Vp9Decoder<T> d = _d;
            int aboveCtx = maxTx, leftCtx = maxTx;
            bool hasAbove = _aboveMi >= 0, hasLeft = _leftMi >= 0;
            if (hasAbove && d._mi[_aboveMi].Skip == 0)
            {
                aboveCtx = d._mi[_aboveMi].TxSize;
            }

            if (hasLeft && d._mi[_leftMi].Skip == 0)
            {
                leftCtx = d._mi[_leftMi].TxSize;
            }

            if (!hasLeft)
            {
                leftCtx = aboveCtx;
            }

            if (!hasAbove)
            {
                aboveCtx = leftCtx;
            }

            int ctx = aboveCtx + leftCtx > maxTx ? 1 : 0;
            byte[] p = d._fc.P;
            int probs = maxTx switch
            {
                Vp9Tables.Tx8x8 => Vp9FrameContext.Tx8x8 + ctx,
                Vp9Tables.Tx16x16 => Vp9FrameContext.Tx16x16 + (ctx * 2),
                _ => Vp9FrameContext.Tx32x32 + (ctx * 3),
            };
            int txSize = _r.Read(p[probs]);
            if (txSize != Vp9Tables.Tx4x4 && maxTx >= Vp9Tables.Tx16x16)
            {
                txSize += _r.Read(p[probs + 1]);
                if (txSize != Vp9Tables.Tx8x8 && maxTx >= Vp9Tables.Tx32x32)
                {
                    txSize += _r.Read(p[probs + 2]);
                }
            }

            if (CountsEnabled)
            {
                switch (maxTx)
                {
                    case Vp9Tables.Tx8x8:
                        Counts.Tx8x8[(ctx * 2) + txSize]++;
                        break;
                    case Vp9Tables.Tx16x16:
                        Counts.Tx16x16[(ctx * 3) + txSize]++;
                        break;
                    default:
                        Counts.Tx32x32[(ctx * 4) + txSize]++;
                        break;
                }
            }

            return txSize;
        }

        private void ReadInterFrameModeInfo(ref Vp9ModeInfo mi, int miRow, int miCol, int xMis, int yMis)
        {
            mi.SegmentId = (byte)ReadInterSegmentId(ref mi, miRow, miCol, xMis, yMis);
            mi.Skip = (byte)ReadSkip(mi.SegmentId);
            bool inter = ReadIsInterBlock(mi.SegmentId);
            mi.TxSize = (byte)ReadTxSize(ref mi, mi.Skip == 0 || !inter);
            if (inter)
            {
                ReadInterBlockModeInfo(ref mi, miRow, miCol);
            }
            else
            {
                ReadIntraBlockModeInfo(ref mi);
            }
        }

        private bool ReadIsInterBlock(int segmentId)
        {
            Vp9Decoder<T> d = _d;
            if (d.SegFeatureActive(segmentId, Vp9Tables.SegLvlRefFrame))
            {
                return d.SegData(segmentId, Vp9Tables.SegLvlRefFrame) != Vp9Tables.IntraFrame;
            }

            int ctx;
            bool hasAbove = _aboveMi >= 0, hasLeft = _leftMi >= 0;
            if (hasAbove && hasLeft)
            {
                bool aboveIntra = !d._mi[_aboveMi].IsInter, leftIntra = !d._mi[_leftMi].IsInter;
                ctx = leftIntra && aboveIntra ? 3 : (leftIntra || aboveIntra ? 1 : 0);
            }
            else if (hasAbove || hasLeft)
            {
                ctx = 2 * (d._mi[hasAbove ? _aboveMi : _leftMi].IsInter ? 0 : 1);
            }
            else
            {
                ctx = 0;
            }

            int isInter = _r.Read(d._fc.P[Vp9FrameContext.IntraInter + ctx]);
            if (CountsEnabled)
            {
                Counts.IntraInter[(ctx * 2) + isInter]++;
            }

            return isInter != 0;
        }

        private void ReadIntraBlockModeInfo(ref Vp9ModeInfo mi)
        {
            switch (mi.SbType)
            {
                case Vp9Tables.Block4x4:
                    for (int i = 0; i < 4; i++)
                    {
                        mi.Bmi[i].Mode = (byte)ReadIntraModeY(0);
                    }

                    mi.Mode = mi.Bmi.B3.Mode;
                    break;
                case Vp9Tables.Block4x8:
                    mi.Bmi.B0.Mode = mi.Bmi.B2.Mode = (byte)ReadIntraModeY(0);
                    mi.Bmi.B1.Mode = mi.Bmi.B3.Mode = mi.Mode = (byte)ReadIntraModeY(0);
                    break;
                case Vp9Tables.Block8x4:
                    mi.Bmi.B0.Mode = mi.Bmi.B1.Mode = (byte)ReadIntraModeY(0);
                    mi.Bmi.B2.Mode = mi.Bmi.B3.Mode = mi.Mode = (byte)ReadIntraModeY(0);
                    break;
                default:
                    mi.Mode = (byte)ReadIntraModeY(Vp9Tables.SizeGroupLookup[mi.SbType]);
                    break;
            }

            mi.UVMode = (byte)ReadIntraModeUV(mi.Mode);
            mi.InterpFilter = Vp9Tables.SwitchableFilters;
            mi.Ref0 = Vp9Tables.IntraFrame;
            mi.Ref1 = Vp9Tables.NoRefFrame;
        }

        private void ReadRefFrames(ref Vp9ModeInfo mi)
        {
            Vp9Decoder<T> d = _d;
            int segmentId = mi.SegmentId;
            if (d.SegFeatureActive(segmentId, Vp9Tables.SegLvlRefFrame))
            {
                mi.Ref0 = (sbyte)d.SegData(segmentId, Vp9Tables.SegLvlRefFrame);
                mi.Ref1 = Vp9Tables.NoRefFrame;
                return;
            }

            int mode = d._referenceMode;
            if (mode == Vp9Tables.ReferenceModeSelect)
            {
                int ctx = ReferenceModeContext();
                mode = _r.Read(d._fc.P[Vp9FrameContext.CompInter + ctx]);
                if (CountsEnabled)
                {
                    Counts.CompInter[(ctx * 2) + mode]++;
                }
            }

            if (mode == Vp9Tables.CompoundReference)
            {
                int idx = d._refSignBias[d._compFixedRef];
                int ctx = CompRefContext();
                int bit = _r.Read(d._fc.P[Vp9FrameContext.CompRef + ctx]);
                if (CountsEnabled)
                {
                    Counts.CompRef[(ctx * 2) + bit]++;
                }

                sbyte fixedRef = (sbyte)d._compFixedRef, varRef = (sbyte)d._compVarRef[bit];
                if (idx == 0)
                {
                    mi.Ref0 = fixedRef;
                    mi.Ref1 = varRef;
                }
                else
                {
                    mi.Ref1 = fixedRef;
                    mi.Ref0 = varRef;
                }
            }
            else
            {
                int ctx0 = SingleRefP1Context();
                int bit0 = _r.Read(d._fc.P[Vp9FrameContext.SingleRef + (ctx0 * 2)]);
                if (CountsEnabled)
                {
                    Counts.SingleRef[(((ctx0 * 2) + 0) * 2) + bit0]++;
                }

                if (bit0 != 0)
                {
                    int ctx1 = SingleRefP2Context();
                    int bit1 = _r.Read(d._fc.P[Vp9FrameContext.SingleRef + (ctx1 * 2) + 1]);
                    if (CountsEnabled)
                    {
                        Counts.SingleRef[(((ctx1 * 2) + 1) * 2) + bit1]++;
                    }

                    mi.Ref0 = (sbyte)(bit1 != 0 ? Vp9Tables.AltrefFrame : Vp9Tables.GoldenFrame);
                }
                else
                {
                    mi.Ref0 = Vp9Tables.LastFrame;
                }

                mi.Ref1 = Vp9Tables.NoRefFrame;
            }
        }

        private void ReadInterBlockModeInfo(ref Vp9ModeInfo mi, int miRow, int miCol)
        {
            Vp9Decoder<T> d = _d;
            int bsize = mi.SbType;
            bool allowHp = d._header.AllowHighPrecisionMv;
            Span<int> bestRefMvs = [0, 0];
            ReadRefFrames(ref mi);
            bool compound = mi.HasSecondRef;
            ReadOnlySpan<sbyte> search = Vp9Tables.MvRefBlocks.AsSpan(bsize * 16, 16);
            int interModeCtx = ModeContext(search, miRow, miCol);

            if (d.SegFeatureActive(mi.SegmentId, Vp9Tables.SegLvlSkip))
            {
                mi.Mode = Vp9Tables.ZeroMv;
                if (bsize < Vp9Tables.Block8x8)
                {
                    throw new CorruptDataException("VP9", -1, "The skip segment feature is used on a block below 8x8.");
                }
            }
            else if (bsize >= Vp9Tables.Block8x8)
            {
                mi.Mode = (byte)ReadInterMode(interModeCtx);
            }

            mi.InterpFilter = (byte)(d._header.InterpFilter == Vp9Tables.Switchable ? ReadSwitchableInterpFilter() : d._header.InterpFilter);

            Span<int> mvList = stackalloc int[2];
            if (bsize < Vp9Tables.Block8x8)
            {
                int num4x4W = 1 << _bmodeBlocksWl, num4x4H = 1 << _bmodeBlocksHl;
                bool gotMvRefsForNew = false;
                Span<int> bestSub8x8 = [0, unchecked((int)InvalidMv)];
                int bMode = 0;
                for (int idy = 0; idy < 2; idy += num4x4H)
                {
                    for (int idx = 0; idx < 2; idx += num4x4W)
                    {
                        int j = (idy * 2) + idx;
                        bMode = ReadInterMode(interModeCtx);
                        if (bMode is Vp9Tables.NearestMv or Vp9Tables.NearMv)
                        {
                            for (int reference = 0; reference < (compound ? 2 : 1); reference++)
                            {
                                bestSub8x8[reference] = AppendSub8x8MvsForIdx(ref mi, search, bMode, j, reference, miRow, miCol);
                            }
                        }
                        else if (bMode == Vp9Tables.NewMv && !gotMvRefsForNew)
                        {
                            for (int reference = 0; reference < (compound ? 2 : 1); reference++)
                            {
                                FindMvRefs(Vp9Tables.NewMv, mi.Ref(reference), search, mvList, miRow, miCol, -1);
                                bestRefMvs[reference] = LowerMvPrecision(mvList[0], allowHp);
                                gotMvRefsForNew = true;
                            }
                        }

                        ref Vp9SubBlock b = ref mi.Bmi[j];
                        if (!AssignMv(bMode, ref b.Mv0, ref b.Mv1, bestRefMvs, bestSub8x8, compound, allowHp))
                        {
                            Corrupted = true;
                            return;
                        }

                        b.Mode = (byte)bMode;
                        if (num4x4H == 2)
                        {
                            mi.Bmi[j + 2] = b;
                        }

                        if (num4x4W == 2)
                        {
                            mi.Bmi[j + 1] = b;
                        }
                    }
                }

                mi.Mode = (byte)bMode;
                mi.Mv0 = mi.Bmi.B3.Mv0;
                mi.Mv1 = mi.Bmi.B3.Mv1;
            }
            else
            {
                if (mi.Mode != Vp9Tables.ZeroMv)
                {
                    for (int reference = 0; reference < (compound ? 2 : 1); reference++)
                    {
                        int count = FindMvRefs(mi.Mode, mi.Ref(reference), search, mvList, miRow, miCol, -1);
                        bestRefMvs[reference] = LowerMvPrecision(mvList[count - 1], allowHp);
                    }
                }

                if (!AssignMv(mi.Mode, ref mi.Mv0, ref mi.Mv1, bestRefMvs, bestRefMvs, compound, allowHp))
                {
                    Corrupted = true;
                }
            }
        }

        private int ReadInterMode(int ctx)
        {
            int mode = _r.ReadTree(Vp9Tables.InterModeTree, _d._fc.P.AsSpan(Vp9FrameContext.InterMode + (ctx * 3), 3));
            if (CountsEnabled)
            {
                Counts.InterMode[(ctx * 4) + mode]++;
            }

            return Vp9Tables.NearestMv + mode;
        }

        private int ReadSwitchableInterpFilter()
        {
            Vp9Decoder<T> d = _d;
            int leftType = _leftMi >= 0 ? d._mi[_leftMi].InterpFilter : Vp9Tables.SwitchableFilters;
            int aboveType = _aboveMi >= 0 ? d._mi[_aboveMi].InterpFilter : Vp9Tables.SwitchableFilters;
            int ctx;
            if (leftType == aboveType)
            {
                ctx = leftType;
            }
            else if (leftType == Vp9Tables.SwitchableFilters)
            {
                ctx = aboveType;
            }
            else if (aboveType == Vp9Tables.SwitchableFilters)
            {
                ctx = leftType;
            }
            else
            {
                ctx = Vp9Tables.SwitchableFilters;
            }

            int type = _r.ReadTree(Vp9Tables.SwitchableInterpTree, d._fc.P.AsSpan(Vp9FrameContext.SwitchableInterp + (ctx * 2), 2));
            if (CountsEnabled)
            {
                Counts.SwitchableInterp[(ctx * 3) + type]++;
            }

            return type;
        }

        private bool AssignMv(int mode, ref int mv0, ref int mv1, ReadOnlySpan<int> refMv, ReadOnlySpan<int> nearNearest, bool compound, bool allowHp)
        {
            switch (mode)
            {
                case Vp9Tables.NewMv:
                    {
                        bool ok = true;
                        mv0 = ReadMv(refMv[0], allowHp);
                        ok &= IsMvValid(mv0);
                        if (compound)
                        {
                            mv1 = ReadMv(refMv[1], allowHp);
                            ok &= IsMvValid(mv1);
                        }

                        return ok;
                    }

                case Vp9Tables.NearMv:
                case Vp9Tables.NearestMv:
                    mv0 = nearNearest[0];
                    mv1 = nearNearest[1];
                    return true;
                case Vp9Tables.ZeroMv:
                    mv0 = 0;
                    mv1 = 0;
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsMvValid(int mv)
        {
            int row = Vp9ModeInfo.Row(mv), col = Vp9ModeInfo.Col(mv);
            return row > Vp9Tables.MvLow && row < Vp9Tables.MvUpp && col > Vp9Tables.MvLow && col < Vp9Tables.MvUpp;
        }

        private static bool UseMvHp(int mv) =>
            (Math.Abs(Vp9ModeInfo.Row(mv)) >> 3) < Vp9Tables.CompandedMvrefThresh && (Math.Abs(Vp9ModeInfo.Col(mv)) >> 3) < Vp9Tables.CompandedMvrefThresh;

        private static int LowerMvPrecision(int mv, bool allowHp)
        {
            if (allowHp && UseMvHp(mv))
            {
                return mv;
            }

            int row = Vp9ModeInfo.Row(mv), col = Vp9ModeInfo.Col(mv);
            if ((row & 1) != 0)
            {
                row += row > 0 ? -1 : 1;
            }

            if ((col & 1) != 0)
            {
                col += col > 0 ? -1 : 1;
            }

            return Vp9ModeInfo.Pack(row, col);
        }

        private int ReadMv(int refMv, bool allowHp)
        {
            Vp9Decoder<T> d = _d;
            byte[] p = d._fc.P;
            int joint = _r.ReadTree(Vp9Tables.MvJointTree, p.AsSpan(Vp9FrameContext.MvJoints, 3));
            bool useHp = allowHp && UseMvHp(refMv);
            int diffRow = 0, diffCol = 0;
            if (joint is 2 or 3)
            {
                diffRow = ReadMvComponent(p, Vp9FrameContext.MvComp, useHp);
            }

            if (joint is 1 or 3)
            {
                diffCol = ReadMvComponent(p, Vp9FrameContext.MvComp + Vp9FrameContext.MvCompSize, useHp);
            }

            if (CountsEnabled)
            {
                IncMv(diffRow, diffCol);
            }

            return Vp9ModeInfo.Pack(Vp9ModeInfo.Row(refMv) + diffRow, Vp9ModeInfo.Col(refMv) + diffCol);
        }

        private int ReadMvComponent(byte[] p, int c, bool useHp)
        {
            int sign = _r.Read(p[c + Vp9FrameContext.MvSign]);
            int mvClass = _r.ReadTree(Vp9Tables.MvClassTree, p.AsSpan(c + Vp9FrameContext.MvClasses, 10));
            bool class0 = mvClass == 0;
            int d, mag;
            if (class0)
            {
                d = _r.Read(p[c + Vp9FrameContext.MvClass0]);
                mag = 0;
            }
            else
            {
                int n = mvClass + 1 - 1;
                d = 0;
                for (int i = 0; i < n; i++)
                {
                    d |= _r.Read(p[c + Vp9FrameContext.MvBits + i]) << i;
                }

                mag = 2 << (mvClass + 2);
            }

            int fr = _r.ReadTree(Vp9Tables.MvFpTree, class0 ? p.AsSpan(c + Vp9FrameContext.MvClass0Fp + (d * 3), 3) : p.AsSpan(c + Vp9FrameContext.MvFp, 3));
            int hp = useHp ? _r.Read(p[c + (class0 ? Vp9FrameContext.MvClass0Hp : Vp9FrameContext.MvHp)]) : 1;
            mag += ((d << 3) | (fr << 1) | hp) + 1;
            return sign != 0 ? -mag : mag;
        }

        private void IncMv(int row, int col)
        {
            int joint = (row != 0 ? 2 : 0) | (col != 0 ? 1 : 0);
            Counts.MvJoints[joint]++;
            if (row != 0)
            {
                IncMvComponent(row, 0);
            }

            if (col != 0)
            {
                IncMvComponent(col, Vp9Counts.MvCompSize);
            }
        }

        private void IncMvComponent(int v, int k)
        {
            int[] c = Counts.MvComp;
            int s = v < 0 ? 1 : 0;
            c[k + Vp9Counts.MvSign + s]++;
            int z = (s != 0 ? -v : v) - 1;
            int mvClass = z >= 2 * 4096 ? 10 : Log2(z >> 3);
            int o = z - (mvClass != 0 ? 2 << (mvClass + 2) : 0);
            c[k + Vp9Counts.MvClasses + mvClass]++;
            int d = o >> 3, f = (o >> 1) & 3, e = o & 1;
            if (mvClass == 0)
            {
                c[k + Vp9Counts.MvClass0 + d]++;
                c[k + Vp9Counts.MvClass0Fp + (d * 4) + f]++;
                c[k + Vp9Counts.MvClass0Hp + e]++;
            }
            else
            {
                int b = mvClass + 1 - 1;
                for (int i = 0; i < b; i++)
                {
                    c[k + Vp9Counts.MvBits + (i * 2) + ((d >> i) & 1)]++;
                }

                c[k + Vp9Counts.MvFp + f]++;
                c[k + Vp9Counts.MvHp + e]++;
            }
        }

        private static int Log2(int n) => n < 2 ? 0 : System.Numerics.BitOperations.Log2((uint)n);

        // Motion vector candidates (vp9_mvref_common.h and the decoder's dec_find_mv_refs).

        private int ModeContext(ReadOnlySpan<sbyte> search, int miRow, int miCol)
        {
            int counter = 0;
            for (int i = 0; i < 2; i++)
            {
                int row = search[i * 2], col = search[(i * 2) + 1];
                if (IsInside(miRow, miCol, row, col))
                {
                    ref Vp9ModeInfo candidate = ref Candidate(miRow, miCol, row, col);
                    counter += Vp9Tables.ModeToCounter[candidate.Mode];
                }
            }

            return Vp9Tables.CounterToContext[counter];
        }

        private bool IsInside(int miRow, int miCol, int row, int col) =>
            !(miRow + row < 0 || miCol + col < MiColStart || miRow + row >= _d._miRows || miCol + col >= MiColEnd);

        private ref Vp9ModeInfo Candidate(int miRow, int miCol, int row, int col) =>
            ref _d._mi[_d._miGrid[((miRow + row) * _d._miCols) + miCol + col]];

        private int FindMvRefs(int mode, int refFrame, ReadOnlySpan<sbyte> search, Span<int> list, int miRow, int miCol, int block)
        {
            Vp9Decoder<T> d = _d;
            int[] signBias = d._refSignBias;
            int count = 0;
            bool differentRefFound = false;
            bool earlyBreak = mode != Vp9Tables.NearMv;
            list[0] = 0;
            list[1] = 0;
            int i = 0;

            if (block >= 0)
            {
                for (i = 0; i < 2; i++)
                {
                    int row = search[i * 2], col = search[(i * 2) + 1];
                    if (IsInside(miRow, miCol, row, col))
                    {
                        ref Vp9ModeInfo candidate = ref Candidate(miRow, miCol, row, col);
                        differentRefFound = true;
                        if (candidate.Ref0 == refFrame)
                        {
                            if (Add(SubBlockMv(ref candidate, 0, col, block), list, ref count, earlyBreak))
                            {
                                goto Done;
                            }
                        }
                        else if (candidate.Ref1 == refFrame)
                        {
                            if (Add(SubBlockMv(ref candidate, 1, col, block), list, ref count, earlyBreak))
                            {
                                goto Done;
                            }
                        }
                    }
                }
            }

            for (; i < 8; i++)
            {
                int row = search[i * 2], col = search[(i * 2) + 1];
                if (IsInside(miRow, miCol, row, col))
                {
                    ref Vp9ModeInfo candidate = ref Candidate(miRow, miCol, row, col);
                    differentRefFound = true;
                    if (candidate.Ref0 == refFrame)
                    {
                        if (Add(candidate.Mv0, list, ref count, earlyBreak))
                        {
                            goto Done;
                        }
                    }
                    else if (candidate.Ref1 == refFrame)
                    {
                        if (Add(candidate.Mv1, list, ref count, earlyBreak))
                        {
                            goto Done;
                        }
                    }
                }
            }

            int prevIndex = (miRow * d._miCols) + miCol;
            if (d._usePrevFrameMvs)
            {
                ref Vp9MvRef prev = ref d._mvsPrevious[prevIndex];
                if (prev.Ref0 == refFrame)
                {
                    if (Add(prev.Mv0, list, ref count, earlyBreak))
                    {
                        goto Done;
                    }
                }
                else if (prev.Ref1 == refFrame)
                {
                    if (Add(prev.Mv1, list, ref count, earlyBreak))
                    {
                        goto Done;
                    }
                }
            }

            if (differentRefFound)
            {
                for (i = 0; i < 8; i++)
                {
                    int row = search[i * 2], col = search[(i * 2) + 1];
                    if (IsInside(miRow, miCol, row, col))
                    {
                        ref Vp9ModeInfo candidate = ref Candidate(miRow, miCol, row, col);
                        if (candidate.IsInter)
                        {
                            if (candidate.Ref0 != refFrame &&
                                Add(ScaleMv(candidate.Mv0, candidate.Ref0, refFrame, signBias), list, ref count, earlyBreak))
                            {
                                goto Done;
                            }

                            if (candidate.HasSecondRef && candidate.Ref1 != refFrame && candidate.Mv1 != candidate.Mv0 &&
                                Add(ScaleMv(candidate.Mv1, candidate.Ref1, refFrame, signBias), list, ref count, earlyBreak))
                            {
                                goto Done;
                            }
                        }
                    }
                }
            }

            if (d._usePrevFrameMvs)
            {
                ref Vp9MvRef prev = ref d._mvsPrevious[prevIndex];
                if (prev.Ref0 != refFrame && prev.Ref0 > Vp9Tables.IntraFrame &&
                    Add(ScaleMv(prev.Mv0, prev.Ref0, refFrame, signBias), list, ref count, earlyBreak))
                {
                    goto Done;
                }

                if (prev.Ref1 > Vp9Tables.IntraFrame && prev.Ref1 != refFrame && prev.Mv1 != prev.Mv0 &&
                    Add(ScaleMv(prev.Mv1, prev.Ref1, refFrame, signBias), list, ref count, earlyBreak))
                {
                    goto Done;
                }
            }

            count = mode == Vp9Tables.NearMv ? 2 : 1;

        Done:
            for (i = 0; i < count; i++)
            {
                list[i] = ClampMvRef(list[i]);
            }

            return count;
        }

        /// <summary>Adds a candidate if new (ADD_MV_REF_LIST_EB); returns whether the search is done.</summary>
        private static bool Add(int mv, Span<int> list, ref int count, bool earlyBreak)
        {
            if (count != 0)
            {
                if (mv != list[0])
                {
                    list[count] = mv;
                    count++;
                    return true;
                }

                return false;
            }

            list[count++] = mv;
            return earlyBreak;
        }

        private static int ScaleMv(int mv, int candidateRef, int refFrame, int[] signBias) =>
            signBias[candidateRef] != signBias[refFrame] ? Vp9ModeInfo.Pack(-Vp9ModeInfo.Row(mv), -Vp9ModeInfo.Col(mv)) : mv;

        private static ReadOnlySpan<byte> IdxNColumnToSubblock => [1, 2, 1, 3, 3, 2, 3, 3];

        private static int SubBlockMv(ref Vp9ModeInfo candidate, int which, int searchCol, int block) =>
            block >= 0 && candidate.SbType < Vp9Tables.Block8x8
                ? candidate.Bmi[IdxNColumnToSubblock[(block * 2) + (searchCol == 0 ? 1 : 0)]].Mv(which)
                : candidate.Mv(which);

        private int ClampMvRef(int mv)
        {
            int row = Vp9Tables.Clamp(Vp9ModeInfo.Row(mv), _toTopEdge - Vp9Tables.MvBorder, _toBottomEdge + Vp9Tables.MvBorder);
            int col = Vp9Tables.Clamp(Vp9ModeInfo.Col(mv), _toLeftEdge - Vp9Tables.MvBorder, _toRightEdge + Vp9Tables.MvBorder);
            return Vp9ModeInfo.Pack(row, col);
        }

        private int AppendSub8x8MvsForIdx(ref Vp9ModeInfo mi, ReadOnlySpan<sbyte> search, int bMode, int block, int reference, int miRow, int miCol)
        {
            Span<int> mvList = stackalloc int[2];
            int refFrame = mi.Ref(reference);
            switch (block)
            {
                case 0:
                    {
                        int count = FindMvRefs(bMode, refFrame, search, mvList, miRow, miCol, block);
                        return mvList[count - 1];
                    }

                case 1:
                case 2:
                    if (bMode == Vp9Tables.NearestMv)
                    {
                        return mi.Bmi.B0.Mv(reference);
                    }

                    FindMvRefs(bMode, refFrame, search, mvList, miRow, miCol, block);
                    for (int n = 0; n < 2; n++)
                    {
                        if (mi.Bmi.B0.Mv(reference) != mvList[n])
                        {
                            return mvList[n];
                        }
                    }

                    return 0;
                default:
                    {
                        if (bMode == Vp9Tables.NearestMv)
                        {
                            return mi.Bmi.B2.Mv(reference);
                        }

                        int b2 = mi.Bmi.B2.Mv(reference);
                        if (b2 != mi.Bmi.B1.Mv(reference))
                        {
                            return mi.Bmi.B1.Mv(reference);
                        }

                        if (b2 != mi.Bmi.B0.Mv(reference))
                        {
                            return mi.Bmi.B0.Mv(reference);
                        }

                        FindMvRefs(bMode, refFrame, search, mvList, miRow, miCol, block);
                        for (int n = 0; n < 2; n++)
                        {
                            if (b2 != mvList[n])
                            {
                                return mvList[n];
                            }
                        }

                        return 0;
                    }
            }
        }

        // Reference contexts (vp9_pred_common.c).

        private int ReferenceModeContext()
        {
            Vp9Decoder<T> d = _d;
            bool hasAbove = _aboveMi >= 0, hasLeft = _leftMi >= 0;
            int fixedRef = d._compFixedRef;
            if (hasAbove && hasLeft)
            {
                ref Vp9ModeInfo above = ref d._mi[_aboveMi];
                ref Vp9ModeInfo left = ref d._mi[_leftMi];
                if (!above.HasSecondRef && !left.HasSecondRef)
                {
                    return (above.Ref0 == fixedRef ? 1 : 0) ^ (left.Ref0 == fixedRef ? 1 : 0);
                }

                if (!above.HasSecondRef)
                {
                    return 2 + (above.Ref0 == fixedRef || !above.IsInter ? 1 : 0);
                }

                if (!left.HasSecondRef)
                {
                    return 2 + (left.Ref0 == fixedRef || !left.IsInter ? 1 : 0);
                }

                return 4;
            }

            if (hasAbove || hasLeft)
            {
                ref Vp9ModeInfo edge = ref d._mi[hasAbove ? _aboveMi : _leftMi];
                return !edge.HasSecondRef ? (edge.Ref0 == fixedRef ? 1 : 0) : 3;
            }

            return 1;
        }

        private int CompRefContext()
        {
            Vp9Decoder<T> d = _d;
            bool aboveIn = _aboveMi >= 0, leftIn = _leftMi >= 0;
            int fixRefIdx = d._refSignBias[d._compFixedRef];
            int varRefIdx = fixRefIdx == 0 ? 1 : 0;
            int var0 = d._compVarRef[0], var1 = d._compVarRef[1], fixedRef = d._compFixedRef;
            if (aboveIn && leftIn)
            {
                ref Vp9ModeInfo above = ref d._mi[_aboveMi];
                ref Vp9ModeInfo left = ref d._mi[_leftMi];
                bool aboveIntra = !above.IsInter, leftIntra = !left.IsInter;
                if (aboveIntra && leftIntra)
                {
                    return 2;
                }

                if (aboveIntra || leftIntra)
                {
                    ref Vp9ModeInfo edge = ref aboveIntra ? ref left : ref above;
                    return !edge.HasSecondRef
                        ? 1 + (2 * (edge.Ref0 != var1 ? 1 : 0))
                        : 1 + (2 * (edge.Ref(varRefIdx) != var1 ? 1 : 0));
                }

                bool lSg = !left.HasSecondRef, aSg = !above.HasSecondRef;
                int vrfa = aSg ? above.Ref0 : above.Ref(varRefIdx);
                int vrfl = lSg ? left.Ref0 : left.Ref(varRefIdx);
                if (vrfa == vrfl && var1 == vrfa)
                {
                    return 0;
                }

                if (lSg && aSg)
                {
                    if ((vrfa == fixedRef && vrfl == var0) || (vrfl == fixedRef && vrfa == var0))
                    {
                        return 4;
                    }

                    return vrfa == vrfl ? 3 : 1;
                }

                if (lSg || aSg)
                {
                    int vrfc = lSg ? vrfa : vrfl;
                    int rfs = aSg ? vrfa : vrfl;
                    if (vrfc == var1 && rfs != var1)
                    {
                        return 1;
                    }

                    if (rfs == var1 && vrfc != var1)
                    {
                        return 2;
                    }

                    return 4;
                }

                return vrfa == vrfl ? 4 : 2;
            }

            if (aboveIn || leftIn)
            {
                ref Vp9ModeInfo edge = ref d._mi[aboveIn ? _aboveMi : _leftMi];
                if (!edge.IsInter)
                {
                    return 2;
                }

                return edge.HasSecondRef
                    ? 4 * (edge.Ref(varRefIdx) != var1 ? 1 : 0)
                    : 3 * (edge.Ref0 != var1 ? 1 : 0);
            }

            return 2;
        }

        private int SingleRefP1Context()
        {
            Vp9Decoder<T> d = _d;
            bool hasAbove = _aboveMi >= 0, hasLeft = _leftMi >= 0;
            const int Last = Vp9Tables.LastFrame;
            if (hasAbove && hasLeft)
            {
                ref Vp9ModeInfo above = ref d._mi[_aboveMi];
                ref Vp9ModeInfo left = ref d._mi[_leftMi];
                bool aboveIntra = !above.IsInter, leftIntra = !left.IsInter;
                if (aboveIntra && leftIntra)
                {
                    return 2;
                }

                if (aboveIntra || leftIntra)
                {
                    ref Vp9ModeInfo edge = ref aboveIntra ? ref left : ref above;
                    return !edge.HasSecondRef ? 4 * (edge.Ref0 == Last ? 1 : 0) : 1 + (edge.Ref0 == Last || edge.Ref1 == Last ? 1 : 0);
                }

                bool aboveSecond = above.HasSecondRef, leftSecond = left.HasSecondRef;
                int above0 = above.Ref0, above1 = above.Ref1, left0 = left.Ref0, left1 = left.Ref1;
                if (aboveSecond && leftSecond)
                {
                    return 1 + (above0 == Last || above1 == Last || left0 == Last || left1 == Last ? 1 : 0);
                }

                if (aboveSecond || leftSecond)
                {
                    int rfs = !aboveSecond ? above0 : left0;
                    int crf1 = aboveSecond ? above0 : left0;
                    int crf2 = aboveSecond ? above1 : left1;
                    return rfs == Last ? 3 + (crf1 == Last || crf2 == Last ? 1 : 0) : (crf1 == Last || crf2 == Last ? 1 : 0);
                }

                return (2 * (above0 == Last ? 1 : 0)) + (2 * (left0 == Last ? 1 : 0));
            }

            if (hasAbove || hasLeft)
            {
                ref Vp9ModeInfo edge = ref d._mi[hasAbove ? _aboveMi : _leftMi];
                if (!edge.IsInter)
                {
                    return 2;
                }

                return !edge.HasSecondRef ? 4 * (edge.Ref0 == Last ? 1 : 0) : 1 + (edge.Ref0 == Last || edge.Ref1 == Last ? 1 : 0);
            }

            return 2;
        }

        private int SingleRefP2Context()
        {
            Vp9Decoder<T> d = _d;
            bool hasAbove = _aboveMi >= 0, hasLeft = _leftMi >= 0;
            const int Last = Vp9Tables.LastFrame, Golden = Vp9Tables.GoldenFrame, Altref = Vp9Tables.AltrefFrame;
            if (hasAbove && hasLeft)
            {
                ref Vp9ModeInfo above = ref d._mi[_aboveMi];
                ref Vp9ModeInfo left = ref d._mi[_leftMi];
                bool aboveIntra = !above.IsInter, leftIntra = !left.IsInter;
                if (aboveIntra && leftIntra)
                {
                    return 2;
                }

                if (aboveIntra || leftIntra)
                {
                    ref Vp9ModeInfo edge = ref aboveIntra ? ref left : ref above;
                    if (!edge.HasSecondRef)
                    {
                        return edge.Ref0 == Last ? 3 : 4 * (edge.Ref0 == Golden ? 1 : 0);
                    }

                    return 1 + (2 * (edge.Ref0 == Golden || edge.Ref1 == Golden ? 1 : 0));
                }

                bool aboveSecond = above.HasSecondRef, leftSecond = left.HasSecondRef;
                int above0 = above.Ref0, above1 = above.Ref1, left0 = left.Ref0, left1 = left.Ref1;
                if (aboveSecond && leftSecond)
                {
                    if (above0 == left0 && above1 == left1)
                    {
                        return 3 * (above0 == Golden || above1 == Golden || left0 == Golden || left1 == Golden ? 1 : 0);
                    }

                    return 2;
                }

                if (aboveSecond || leftSecond)
                {
                    int rfs = !aboveSecond ? above0 : left0;
                    int crf1 = aboveSecond ? above0 : left0;
                    int crf2 = aboveSecond ? above1 : left1;
                    if (rfs == Golden)
                    {
                        return 3 + (crf1 == Golden || crf2 == Golden ? 1 : 0);
                    }

                    if (rfs == Altref)
                    {
                        return crf1 == Golden || crf2 == Golden ? 1 : 0;
                    }

                    return 1 + (2 * (crf1 == Golden || crf2 == Golden ? 1 : 0));
                }

                if (above0 == Last && left0 == Last)
                {
                    return 3;
                }

                if (above0 == Last || left0 == Last)
                {
                    int edge0 = above0 == Last ? left0 : above0;
                    return 4 * (edge0 == Golden ? 1 : 0);
                }

                return (2 * (above0 == Golden ? 1 : 0)) + (2 * (left0 == Golden ? 1 : 0));
            }

            if (hasAbove || hasLeft)
            {
                ref Vp9ModeInfo edge = ref d._mi[hasAbove ? _aboveMi : _leftMi];
                if (!edge.IsInter || (edge.Ref0 == Last && !edge.HasSecondRef))
                {
                    return 2;
                }

                if (!edge.HasSecondRef)
                {
                    return 4 * (edge.Ref0 == Golden ? 1 : 0);
                }

                return 3 * (edge.Ref0 == Golden || edge.Ref1 == Golden ? 1 : 0);
            }

            return 2;
        }
    }
}
