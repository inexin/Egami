using System.Runtime.CompilerServices;

namespace Egami.Video.Codecs.Vp9;

/// <summary>
/// Inter coding: motion vector candidates (vp9_mvref_common.c), motion search to 1/8 pixel, the choice of reference, mode,
/// interpolation filter and compound prediction by rate and distortion, and their mode info.
/// </summary>
internal sealed partial class Vp9Encoder<T>
{
    /// <summary>A reference frame with its edges repeated around it, so motion search and prediction read past the frame freely.</summary>
    internal sealed class PaddedReference
    {
        public const int Border = 160;

        private readonly int _width;
        private readonly int _height;
        private readonly int _subX;
        private readonly int _subY;
        private int _slot = -1;
        private int _version = -1;

        public PaddedReference(int width, int height, int subX, int subY)
        {
            _width = width;
            _height = height;
            _subX = subX;
            _subY = subY;
            for (int p = 0; p < 3; p++)
            {
                int w = p == 0 ? width : (width + subX) >> subX, h = p == 0 ? height : (height + subY) >> subY;
                int border = p == 0 ? Border : Border >> subX;
                int borderY = p == 0 ? Border : Border >> subY;
                Strides[p] = ((w + 63) & ~63) + (2 * border);
                Origins[p] = (borderY * Strides[p]) + border;
                Planes[p] = new T[Strides[p] * (((h + 63) & ~63) + (2 * borderY))];
            }
        }

        public T[][] Planes { get; } = new T[3][];

        public int[] Strides { get; } = new int[3];

        /// <summary>Gets where each plane's first sample is.</summary>
        public int[] Origins { get; } = new int[3];

        /// <summary>Copies a decoded frame in and repeats its edges, unless it holds that slot's version already.</summary>
        public void Load(Vp9Frame<T> frame, int slot, int version)
        {
            if (slot == _slot && version == _version)
            {
                return;
            }

            _slot = slot;
            _version = version;
            for (int p = 0; p < 3; p++)
            {
                int w = p == 0 ? _width : (_width + _subX) >> _subX, h = p == 0 ? _height : (_height + _subY) >> _subY;
                int border = p == 0 ? Border : Border >> _subX;
                int borderY = p == 0 ? Border : Border >> _subY;
                int stride = Strides[p];
                T[] plane = Planes[p];
                Span<T> src = frame.Plane(p);
                int srcStride = frame.Strides[p];
                int rows = ((h + 63) & ~63) + borderY;
                for (int y = 0; y < h; y++)
                {
                    Span<T> row = plane.AsSpan(((y + borderY) * stride), stride);
                    src.Slice(y * srcStride, w).CopyTo(row[border..]);
                    row[..border].Fill(row[border]);
                    row[(border + w)..].Fill(row[border + w - 1]);
                }

                for (int y = 0; y < borderY; y++)
                {
                    plane.AsSpan(borderY * stride, stride).CopyTo(plane.AsSpan(y * stride, stride));
                }

                ReadOnlySpan<T> last = plane.AsSpan((borderY + h - 1) * stride, stride);
                for (int y = borderY + h; y < rows + borderY; y++)
                {
                    last.CopyTo(plane.AsSpan(y * stride, stride));
                }
            }
        }

        /// <summary>Forgets what the reference holds.</summary>
        public void Invalidate() => _slot = -1;
    }

    internal sealed partial class Tile
    {
        private const int InterpExtend = 4;

        private readonly int[] _nearest = new int[4];
        private readonly int[] _near = new int[4];
        private readonly int[] _newMv = new int[4];
        private readonly long[] _newCost = new long[4];
        private readonly Candidate[] _candidates = new Candidate[64];
        private readonly T[] _searchPred = new T[64 * 64];
        private int _modeContext;
        private long _bestInterSse = long.MaxValue;
        private long _lastPredictionSse;

        private struct Candidate
        {
            public int Ref0;
            public int Ref1;
            public int Mode;
            public int Mv0;
            public int Mv1;
            public long Estimate;
        }

        /// <summary>
        /// Picks an inter frame block's coding: every reference's NEARESTMV, NEARMV, ZEROMV and NEWMV, compound pairs with the
        /// alternate reference, ranked by the luma prediction error and the mode's bits; the best few get a full RD search with
        /// transform sizes, filters and skip, against the best intra coding.
        /// </summary>
        private bool PickInter(ref Vp9ModeInfo mi, long bestCost)
        {
            Vp9Encoder<T> e = _e;
            int bsize = mi.SbType;
            int segment = mi.SegmentId;
            long best = bestCost;
            Vp9ModeInfo bestMi = default;
            bool found = false;
            int effort = e._plan.Effort;
            _bestInterSse = long.MaxValue;

            if (bsize < Vp9Tables.Block8x8)
            {
                if (!e.AllowSub8x8)
                {
                    return false;
                }

                Vp9ModeInfo intra = mi;
                long cost = PickIntra(ref intra, best);
                if (cost < best)
                {
                    mi = intra;
                    return true;
                }

                return false;
            }

            ReadOnlySpan<sbyte> search = Vp9Tables.MvRefBlocks.AsSpan(bsize * 16, 16);
            _modeContext = ModeContext(search);
            int count = 0;
            int refs = e.ReferenceCount;
            Span<int> list = stackalloc int[2];
            for (int r = Vp9Tables.LastFrame; r <= refs; r++)
            {
                FindMvRefs(r, search, list);
                _nearest[r] = LowerMvPrecision(list[0], e._allowHighPrecisionMv);
                _near[r] = LowerMvPrecision(list[1], e._allowHighPrecisionMv);
                count = AddCandidate(count, r, -1, Vp9Tables.ZeroMv, 0, 0);
                count = AddCandidate(count, r, -1, Vp9Tables.NearestMv, _nearest[r], 0);
                if (_near[r] != _nearest[r] || effort >= 6)
                {
                    count = AddCandidate(count, r, -1, Vp9Tables.NearMv, _near[r], 0);
                }

                _newMv[r] = MotionSearch(r, bsize, out _newCost[r]);
                if (_newMv[r] != _nearest[r] && _newMv[r] != _near[r] && _newMv[r] != 0)
                {
                    count = AddCandidate(count, r, -1, Vp9Tables.NewMv, _newMv[r], 0);
                }
            }

            if (e._referenceMode != Vp9Tables.SingleReference)
            {
                int fixedRef = e._compFixedRef;
                for (int v = 0; v < 2; v++)
                {
                    int varRef = e._compVarRef[v];
                    if (varRef > refs || fixedRef > refs)
                    {
                        continue;
                    }

                    // Ref0 is the reference with sign bias 0.
                    int r0 = e._refSignBias[fixedRef] == 0 ? fixedRef : varRef, r1 = r0 == fixedRef ? varRef : fixedRef;
                    count = AddCandidate(count, r0, r1, Vp9Tables.NearestMv, _nearest[r0], _nearest[r1]);
                    count = AddCandidate(count, r0, r1, Vp9Tables.ZeroMv, 0, 0);
                    if (effort >= 5)
                    {
                        count = AddCandidate(count, r0, r1, Vp9Tables.NearMv, _near[r0], _near[r1]);
                    }

                    count = AddCandidate(count, r0, r1, Vp9Tables.NewMv, _newMv[r0], _newMv[r1]);
                }
            }

            // Rank by the luma prediction's error and the mode's bits, then search the best few in full.
            for (int i = 0; i < count; i++)
            {
                ref Candidate c = ref _candidates[i];
                Vp9ModeInfo trial = mi;
                SetInter(ref trial, ref c, Vp9Tables.EightTap);
                long sse = PredictLumaSse(ref trial);
                c.Estimate = RdCost(InterModeRate(ref trial) + e._costs.IntraInter[(IntraInterContext() * 2) + 1], sse << 4);
            }

            SortCandidates(count);
            int full = Math.Min(count, e.FullInterCandidates);
            for (int i = 0; i < full; i++)
            {
                ref Candidate c = ref _candidates[i];
                if (i > 0 && c.Estimate > best + (best >> 2))
                {
                    break;
                }

                Vp9ModeInfo trial = mi;
                SetInter(ref trial, ref c, Vp9Tables.EightTap);
                if (e._interpFilter == Vp9Tables.Switchable && HasSubpel(ref trial) && effort >= 3)
                {
                    PickFilter(ref trial);
                }

                long cost = InterRd(ref trial, best);
                if (cost < best)
                {
                    best = cost;
                    bestMi = trial;
                    found = true;
                    _bestInterSse = _lastPredictionSse;
                }
            }

            // Intra, unless inter prediction already explains the block well.
            if (!found || e._plan.Effort >= 7 || _bestInterSse * e.IntraGate > BlockVariance(bsize))
            {
                Vp9ModeInfo intra = mi;
                long cost = PickIntra(ref intra, best);
                if (cost < best)
                {
                    best = cost;
                    bestMi = intra;
                    found = true;
                }
            }

            if (found)
            {
                mi = bestMi;
            }

            return found;
        }

        /// <summary>The luma source's squared deviation from its mean over the block: what DC prediction leaves.</summary>
        private long BlockVariance(int bsize)
        {
            Vp9Encoder<T> e = _e;
            int w = 4 * Vp9Tables.Num4x4BlocksWideLookup[bsize], h = 4 * Vp9Tables.Num4x4BlocksHighLookup[bsize];
            long variance = Vp9EncoderSimd.Variance(e._source[0], (_miRow * 8 * e._sourceStride[0]) + (_miCol * 8), e._sourceStride[0], w, h);
            return (variance * w * h) >> (2 * (e._bitDepth - 8));
        }

        private int AddCandidate(int count, int r0, int r1, int mode, int mv0, int mv1)
        {
            for (int i = 0; i < count; i++)
            {
                ref Candidate c = ref _candidates[i];
                if (c.Ref0 == r0 && c.Ref1 == r1 && c.Mv0 == mv0 && (r1 < 0 || c.Mv1 == mv1))
                {
                    return count;
                }
            }

            if (count >= _candidates.Length)
            {
                return count;
            }

            _candidates[count] = new Candidate { Ref0 = r0, Ref1 = r1, Mode = mode, Mv0 = mv0, Mv1 = mv1 };
            return count + 1;
        }

        private void SortCandidates(int count)
        {
            for (int i = 1; i < count; i++)
            {
                Candidate c = _candidates[i];
                int j = i - 1;
                while (j >= 0 && _candidates[j].Estimate > c.Estimate)
                {
                    _candidates[j + 1] = _candidates[j];
                    j--;
                }

                _candidates[j + 1] = c;
            }
        }

        private static void SetInter(ref Vp9ModeInfo mi, ref Candidate c, int filter)
        {
            mi.Ref0 = (sbyte)c.Ref0;
            mi.Ref1 = (sbyte)c.Ref1;
            mi.Mode = (byte)c.Mode;
            mi.Mv0 = c.Mv0;
            mi.Mv1 = c.Ref1 > 0 ? c.Mv1 : 0;
            mi.InterpFilter = (byte)filter;
            mi.UVMode = 0;
            mi.Skip = 0;
        }

        private static bool HasSubpel(ref Vp9ModeInfo mi) =>
            ((mi.Mv0 | (mi.HasSecondRef ? mi.Mv1 : 0)) & 0x00070007) != 0;

        /// <summary>The block's RD cost with the best transform sizes and skip choice, or long.MaxValue above <paramref name="limit"/>.</summary>
        private long InterRd(ref Vp9ModeInfo mi, long limit)
        {
            Vp9Encoder<T> e = _e;
            BuildInterPredictors(ref mi, _pred);
            int bsize = mi.SbType;
            int maxTx = Math.Min(Vp9Tables.MaxTxSizeLookup[bsize], (int)Vp9Tables.TxModeToBiggestTxSize[e._txMode]);
            int minTx = e._txMode == Vp9Tables.TxModeSelect ? Math.Max(0, maxTx - e.TxSearchDepth) : maxTx;

            // Skipped: no residual at all.
            long sse = 0;
            for (int plane = 0; plane < 3; plane++)
            {
                sse += PlaneSse(plane);
            }

            _lastPredictionSse = PlaneSse(0) >> 4;
            mi.Skip = 1;
            mi.TxSize = (byte)maxTx;
            long bestCost = RdCost(ModeInfoRate(ref mi), sse);
            int bestTx = maxTx;
            bool bestSkip = true;
            mi.Skip = 0;
            long overheadBase = RdCost(ModeInfoRateWithoutTx(ref mi), 0);
            for (int tx = maxTx; tx >= minTx; tx--)
            {
                mi.TxSize = (byte)tx;
                long cost = overheadBase + RdCost(TxSizeCost(ref mi, tx), 0);
                if (cost >= Math.Min(bestCost, limit))
                {
                    continue;
                }

                for (int plane = 0; plane < 3 && cost < Math.Min(bestCost, limit); plane++)
                {
                    LoadContexts(plane, _ta2, _tl2);
                    Rd rd = InterPlane(ref mi, plane, _ta2, _tl2, false, false, out _);
                    cost += RdCost(rd);
                }

                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestTx = tx;
                    bestSkip = false;
                }
            }

            mi.TxSize = (byte)bestTx;
            mi.Skip = (byte)(bestSkip ? 1 : 0);
            if (bestSkip)
            {
                mi.TxSize = (byte)maxTx;
            }

            return bestCost < limit ? bestCost : long.MaxValue;
        }

        private int ModeInfoRateWithoutTx(ref Vp9ModeInfo mi)
        {
            int full = ModeInfoRate(ref mi);
            return full - TxSizeCost(ref mi, mi.TxSize);
        }

        /// <summary>Picks the interpolation filter for a block whose vectors have a subpixel part, by luma error and filter bits.</summary>
        private void PickFilter(ref Vp9ModeInfo mi)
        {
            int ctx = SwitchableContext();
            long best = long.MaxValue;
            int bestFilter = Vp9Tables.EightTap;
            for (int f = 0; f < 3; f++)
            {
                mi.InterpFilter = (byte)f;
                long sse = PredictLumaSse(ref mi);
                long cost = RdCost(_e._costs.SwitchableInterp[(ctx * 3) + f], sse << 4);
                if (cost < best)
                {
                    best = cost;
                    bestFilter = f;
                }
            }

            mi.InterpFilter = (byte)bestFilter;
        }

        /// <summary>Codes one plane of an inter block from the prediction buffers; writes the reconstruction when asked.</summary>
        private Rd InterPlane(ref Vp9ModeInfo mi, int plane, Span<byte> above, Span<byte> left, bool skip, bool writeRecon, out bool nonZero)
        {
            Vp9Encoder<T> e = _e;
            int txSize = plane == 0 ? mi.TxSize : UVTxSize(ref mi, plane);
            int step = 1 << txSize;
            int n = 4 << txSize;
            MaxBlocks(plane, out int maxWide, out int maxHigh);
            Vp9Frame<T> frame = e._recon;
            int stride = frame.Strides[plane];
            Span<T> pixels = frame.Plane(plane);
            T[] source = e._source[plane];
            int sourceStride = e._sourceStride[plane];
            T[] pred = _pred[plane];
            int bx = (_miCol * 8) >> _ssX[plane], by = (_miRow * 8) >> _ssY[plane];
            if (writeRecon)
            {
                int w = 4 * _n4W[plane], h = 4 * _n4H[plane];
                for (int y = 0; y < h; y++)
                {
                    pred.AsSpan(y * 64, w).CopyTo(pixels.Slice(((by + y) * stride) + bx));
                }
            }

            int rate = 0;
            long dist = 0;
            nonZero = false;
            Vp9Scan scan = Vp9Scan.Default(txSize);
            for (int row = 0; row < maxHigh; row += step)
            {
                for (int col = 0; col < maxWide; col += step)
                {
                    int x0 = bx + (4 * col), y0 = by + (4 * row);
                    Residual(source, (y0 * sourceStride) + x0, sourceStride, pred, (4 * row * 64) + (4 * col), 64, n, n);
                    if (skip)
                    {
                        dist += Sse(n * n);
                        continue;
                    }

                    int ctx = EntropyContext(above, left, col, row, step);
                    (int r, long d, int eob) = CodeTransform(plane, txSize, Vp9Tables.DctDct, scan, ctx, true, mi.SegmentId);
                    rate += r;
                    dist += d;
                    SetEntropyContext(plane, above, left, col, row, step, eob > 0);
                    if (eob > 0)
                    {
                        nonZero = true;
                        if (writeRecon)
                        {
                            Reconstruct(pixels.Slice((y0 * stride) + x0), stride, txSize, Vp9Tables.DctDct, eob, scan);
                        }
                    }
                }
            }

            return new Rd(rate, dist);
        }

        /// <summary>Sixteen times the squared error of a plane's prediction over the block's visible transform blocks.</summary>
        private long PlaneSse(int plane)
        {
            Vp9Encoder<T> e = _e;
            MaxBlocks(plane, out int maxWide, out int maxHigh);
            int w = Math.Min(4 * maxWide, 4 * _n4W[plane]), h = Math.Min(4 * maxHigh, 4 * _n4H[plane]);
            int bx = (_miCol * 8) >> _ssX[plane], by = (_miRow * 8) >> _ssY[plane];
            long sse = Vp9EncoderSimd.Sse(e._source[plane], (by * e._sourceStride[plane]) + bx, e._sourceStride[plane], _pred[plane], 0, 64, w, h);
            return (sse << 4) >> (2 * (e._bitDepth - 8));
        }

        /// <summary>The luma prediction's squared error for a choice of references, vectors and filter.</summary>
        private long PredictLumaSse(ref Vp9ModeInfo mi)
        {
            Vp9Encoder<T> e = _e;
            int w = 4 * _n4W[0], h = 4 * _n4H[0];
            ReadOnlySpan<short> kernel = Kernel(mi.InterpFilter);
            for (int reference = 0; reference < (mi.HasSecondRef ? 2 : 1); reference++)
            {
                PredictPlane(e._references[mi.Ref(reference) - 1]!, 0, mi.Mv(reference), w, h, _searchPred, kernel, reference);
            }

            MaxBlocks(0, out int maxWide, out int maxHigh);
            int vw = Math.Min(w, 4 * maxWide), vh = Math.Min(h, 4 * maxHigh);
            long sse = Vp9EncoderSimd.Sse(e._source[0], (_miRow * 8 * e._sourceStride[0]) + (_miCol * 8), e._sourceStride[0], _searchPred, 0, 64, vw, vh);
            return sse >> (2 * (e._bitDepth - 8));
        }

        private static ReadOnlySpan<short> Kernel(int filter) => filter switch
        {
            Vp9Tables.EightTap => Vp9Tables.SubPelFilters8,
            Vp9Tables.EightTapSmooth => Vp9Tables.SubPelFilters8Lp,
            Vp9Tables.EightTapSharp => Vp9Tables.SubPelFilters8S,
            _ => Vp9Tables.BilinearFilters,
        };

        /// <summary>Builds the block's prediction in every plane into <paramref name="pred"/> (64 samples a row).</summary>
        private void BuildInterPredictors(ref Vp9ModeInfo mi, T[][] pred)
        {
            ReadOnlySpan<short> kernel = Kernel(mi.InterpFilter);
            for (int reference = 0; reference < (mi.HasSecondRef ? 2 : 1); reference++)
            {
                PaddedReference r = _e._references[mi.Ref(reference) - 1]!;
                for (int plane = 0; plane < 3; plane++)
                {
                    PredictPlane(r, plane, mi.Mv(reference), 4 * _n4W[plane], 4 * _n4H[plane], pred[plane], kernel, reference);
                }
            }
        }

        /// <summary>Predicts one plane of the block from a padded reference, as the decoder's dec_build_inter_predictors does unscaled.</summary>
        private void PredictPlane(PaddedReference r, int plane, int mv, int w, int h, T[] dst, ReadOnlySpan<short> kernel, int average)
        {
            int ssX = _ssX[plane], ssY = _ssY[plane];
            int x0 = (_miCol * 8) >> ssX, y0 = (_miRow * 8) >> ssY;
            int mvRow = Vp9ModeInfo.Row(mv) * (1 << (1 - ssY));
            int mvCol = Vp9ModeInfo.Col(mv) * (1 << (1 - ssX));
            int stride = r.Strides[plane];
            int offset = r.Origins[plane] + ((y0 + (mvRow >> 4)) * stride) + x0 + (mvCol >> 4);
            Vp9Convolve<T>.Predict(r.Planes[plane], offset, stride, dst, 0, 64, mvCol & 15, 16, mvRow & 15, 16, w, h, average, kernel,
                _e._bitDepth, _convolveTemp, _predTemp);
        }

        // Motion search.

        /// <summary>
        /// Finds the luma motion vector for a reference: a diamond search over whole pixels from the best of the candidate
        /// vectors, then half, quarter and (where allowed) eighth pixel refinement on the filtered prediction.
        /// </summary>
        private int MotionSearch(int refFrame, int bsize, out long bestCost)
        {
            Vp9Encoder<T> e = _e;
            PaddedReference r = e._references[refFrame - 1]!;
            int w = 4 * Vp9Tables.Num4x4BlocksWideLookup[bsize], h = 4 * Vp9Tables.Num4x4BlocksHighLookup[bsize];
            int refMv = _nearest[refFrame];
            bool usehp = e._allowHighPrecisionMv && UseMvHp(refMv);
            int srcOffset = (_miRow * 8 * e._sourceStride[0]) + (_miCol * 8);
            int stride = r.Strides[0];
            int origin = r.Origins[0] + (_miRow * 8 * stride) + (_miCol * 8);
            int sadPerBit = e._sadPerBit;

            // The whole-pixel range keeps the block and its filter taps inside the padding.
            int margin = PaddedReference.Border - 8 - InterpExtend;
            int minCol = -(_miCol * 8) - margin + 8, maxCol = (e._miCols * 8) - (_miCol * 8) - w + margin - 8;
            int minRow = -(_miRow * 8) - margin + 8, maxRow = (e._miRows * 8) - (_miRow * 8) - h + margin - 8;
            int range = e.SearchRange;

            // Start from the best whole-pixel version of the candidates.
            Span<int> starts = [_nearest[refFrame], _near[refFrame], 0];
            int bestRow = 0, bestCol = 0;
            long best = long.MaxValue;
            foreach (int s in starts)
            {
                int row = Math.Clamp((Vp9ModeInfo.Row(s) + 4) >> 3, minRow, maxRow), col = Math.Clamp((Vp9ModeInfo.Col(s) + 4) >> 3, minCol, maxCol);
                long cost = FullPelCost(r, origin, srcOffset, row, col, w, h, refMv, sadPerBit, usehp);
                if (cost < best)
                {
                    best = cost;
                    bestRow = row;
                    bestCol = col;
                }
            }

            int centerRow = bestRow, centerCol = bestCol;
            for (int step = range; step >= 1; step >>= 1)
            {
                bool moved = true;
                for (int iteration = 0; moved && iteration < 16; iteration++)
                {
                    moved = false;
                    int cr = bestRow, cc = bestCol;
                    for (int k = 0; k < 8; k++)
                    {
                        int row = cr + (DiamondRow[k] * step), col = cc + (DiamondCol[k] * step);
                        if (row < minRow || row > maxRow || col < minCol || col > maxCol ||
                            Math.Abs(row - centerRow) > 4 * range || Math.Abs(col - centerCol) > 4 * range)
                        {
                            continue;
                        }

                        long cost = FullPelCost(r, origin, srcOffset, row, col, w, h, refMv, sadPerBit, usehp);
                        if (cost < best)
                        {
                            best = cost;
                            bestRow = row;
                            bestCol = col;
                            moved = true;
                        }
                    }

                    if (step > 1)
                    {
                        break;
                    }
                }
            }

            // Subpixel refinement on the real filter's prediction, by squared error and vector bits.
            int mv = Vp9ModeInfo.Pack(bestRow * 8, bestCol * 8);
            long bestSse = SubpelCost(r, mv, w, h, refMv, usehp);
            int levels = usehp ? 3 : 2;
            if (e._plan.Effort <= 1)
            {
                levels = Math.Min(levels, 1);
            }

            for (int level = 0; level < levels; level++)
            {
                int stepSize = 4 >> level;
                int center = mv;
                int points = e._plan.Effort >= 5 ? 8 : 4;
                for (int k = 0; k < points; k++)
                {
                    int candidate = Vp9ModeInfo.Pack(Vp9ModeInfo.Row(center) + (DiamondRow[k] * stepSize), Vp9ModeInfo.Col(center) + (DiamondCol[k] * stepSize));
                    long cost = SubpelCost(r, candidate, w, h, refMv, usehp);
                    if (cost < bestSse)
                    {
                        bestSse = cost;
                        mv = candidate;
                    }
                }
            }

            bestCost = bestSse;
            return mv;
        }

        private static ReadOnlySpan<int> DiamondRow => [-1, 0, 0, 1, -1, -1, 1, 1];

        private static ReadOnlySpan<int> DiamondCol => [0, -1, 1, 0, -1, 1, -1, 1];

        private long FullPelCost(PaddedReference r, int origin, int srcOffset, int row, int col, int w, int h, int refMv, int sadPerBit, bool usehp)
        {
            Vp9Encoder<T> e = _e;
            long sad = Vp9EncoderSimd.Sad(e._source[0], srcOffset, e._sourceStride[0], r.Planes[0], origin + (row * r.Strides[0]) + col, r.Strides[0], w, h);
            int rate = e._costs.Mv((row * 8) - Vp9ModeInfo.Row(refMv), (col * 8) - Vp9ModeInfo.Col(refMv), usehp);
            return (sad << 8) + ((long)rate * sadPerBit >> 1);
        }

        private long SubpelCost(PaddedReference r, int mv, int w, int h, int refMv, bool usehp)
        {
            Vp9Encoder<T> e = _e;
            int row = Vp9ModeInfo.Row(mv), col = Vp9ModeInfo.Col(mv);
            if (Math.Abs(row - Vp9ModeInfo.Row(refMv)) > Vp9RdCosts.MvMax || Math.Abs(col - Vp9ModeInfo.Col(refMv)) > Vp9RdCosts.MvMax)
            {
                return long.MaxValue;
            }

            PredictPlane(r, 0, mv, w, h, _searchPred, Vp9Tables.SubPelFilters8, 0);
            long sse = Vp9EncoderSimd.Sse(e._source[0], (_miRow * 8 * e._sourceStride[0]) + (_miCol * 8), e._sourceStride[0], _searchPred, 0, 64, w, h);
            sse >>= 2 * (e._bitDepth - 8);
            int rate = e._costs.Mv(row - Vp9ModeInfo.Row(refMv), col - Vp9ModeInfo.Col(refMv), usehp);
            return RdCost(rate, sse << 4);
        }

        // Candidate vectors (vp9_find_mv_refs without the decoder's early exit, so NEARESTMV and NEARMV come from one search).

        private int ModeContext(ReadOnlySpan<sbyte> search)
        {
            int counter = 0;
            for (int i = 0; i < 2; i++)
            {
                int row = search[i * 2], col = search[(i * 2) + 1];
                if (IsInside(row, col))
                {
                    counter += Vp9Tables.ModeToCounter[CandidateAt(row, col).Mode];
                }
            }

            return Vp9Tables.CounterToContext[counter];
        }

        private bool IsInside(int row, int col) =>
            !(_miRow + row < 0 || _miCol + col < MiColStart || _miRow + row >= _e._miRows || _miCol + col >= MiColEnd);

        private ref Vp9ModeInfo CandidateAt(int row, int col) =>
            ref _e._mi[_e._miGrid[((_miRow + row) * _e._miCols) + _miCol + col]];

        private void FindMvRefs(int refFrame, ReadOnlySpan<sbyte> search, Span<int> list)
        {
            Vp9Encoder<T> e = _e;
            int[] signBias = e._refSignBias;
            int count = 0;
            bool differentRefFound = false;
            list[0] = 0;
            list[1] = 0;
            for (int i = 0; i < 8; i++)
            {
                int row = search[i * 2], col = search[(i * 2) + 1];
                if (IsInside(row, col))
                {
                    ref Vp9ModeInfo candidate = ref CandidateAt(row, col);
                    differentRefFound = true;
                    if (candidate.Ref0 == refFrame)
                    {
                        if (Add(candidate.Mv0, list, ref count))
                        {
                            goto Done;
                        }
                    }
                    else if (candidate.Ref1 == refFrame)
                    {
                        if (Add(candidate.Mv1, list, ref count))
                        {
                            goto Done;
                        }
                    }
                }
            }

            int prevIndex = (_miRow * e._miCols) + _miCol;
            if (e._usePrevFrameMvs)
            {
                ref Vp9MvRef prev = ref e._previousMvs[prevIndex];
                if (prev.Ref0 == refFrame)
                {
                    if (Add(prev.Mv0, list, ref count))
                    {
                        goto Done;
                    }
                }
                else if (prev.Ref1 == refFrame)
                {
                    if (Add(prev.Mv1, list, ref count))
                    {
                        goto Done;
                    }
                }
            }

            if (differentRefFound)
            {
                for (int i = 0; i < 8; i++)
                {
                    int row = search[i * 2], col = search[(i * 2) + 1];
                    if (IsInside(row, col))
                    {
                        ref Vp9ModeInfo candidate = ref CandidateAt(row, col);
                        if (candidate.IsInter)
                        {
                            if (candidate.Ref0 != refFrame && Add(ScaleMv(candidate.Mv0, candidate.Ref0, refFrame, signBias), list, ref count))
                            {
                                goto Done;
                            }

                            if (candidate.HasSecondRef && candidate.Ref1 != refFrame && candidate.Mv1 != candidate.Mv0 &&
                                Add(ScaleMv(candidate.Mv1, candidate.Ref1, refFrame, signBias), list, ref count))
                            {
                                goto Done;
                            }
                        }
                    }
                }
            }

            if (e._usePrevFrameMvs)
            {
                ref Vp9MvRef prev = ref e._previousMvs[prevIndex];
                if (prev.Ref0 != refFrame && prev.Ref0 > Vp9Tables.IntraFrame &&
                    Add(ScaleMv(prev.Mv0, prev.Ref0, refFrame, signBias), list, ref count))
                {
                    goto Done;
                }

                if (prev.Ref1 > Vp9Tables.IntraFrame && prev.Ref1 != refFrame && prev.Mv1 != prev.Mv0 &&
                    Add(ScaleMv(prev.Mv1, prev.Ref1, refFrame, signBias), list, ref count))
                {
                    goto Done;
                }
            }

        Done:
            list[0] = ClampMvRef(list[0]);
            list[1] = ClampMvRef(list[1]);
        }

        /// <summary>Adds a candidate if new; returns whether two are found.</summary>
        private static bool Add(int mv, Span<int> list, ref int count)
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
            return false;
        }

        private static int ScaleMv(int mv, int candidateRef, int refFrame, int[] signBias) =>
            signBias[candidateRef] != signBias[refFrame] ? Vp9ModeInfo.Pack(-Vp9ModeInfo.Row(mv), -Vp9ModeInfo.Col(mv)) : mv;

        private int ClampMvRef(int mv)
        {
            int row = Vp9Tables.Clamp(Vp9ModeInfo.Row(mv), _toTopEdge - Vp9Tables.MvBorder, _toBottomEdge + Vp9Tables.MvBorder);
            int col = Vp9Tables.Clamp(Vp9ModeInfo.Col(mv), _toLeftEdge - Vp9Tables.MvBorder, _toRightEdge + Vp9Tables.MvBorder);
            return Vp9ModeInfo.Pack(row, col);
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

        // Inter mode info costs and contexts.

        private int SwitchableContext()
        {
            Vp9Encoder<T> e = _e;
            int leftType = _leftMi >= 0 ? e._mi[_leftMi].InterpFilter : Vp9Tables.SwitchableFilters;
            int aboveType = _aboveMi >= 0 ? e._mi[_aboveMi].InterpFilter : Vp9Tables.SwitchableFilters;
            if (leftType == aboveType)
            {
                return leftType;
            }

            if (leftType == Vp9Tables.SwitchableFilters)
            {
                return aboveType;
            }

            return aboveType == Vp9Tables.SwitchableFilters ? leftType : Vp9Tables.SwitchableFilters;
        }

        /// <summary>The cost of an inter block's references, mode, filter and vectors.</summary>
        private int InterModeRate(ref Vp9ModeInfo mi)
        {
            Vp9Encoder<T> e = _e;
            Vp9RdCosts c = e._costs;
            int rate = RefFramesRate(ref mi);
            if (!SegmentSkips(mi.SegmentId))
            {
                rate += c.InterMode[(_modeContext * 4) + (mi.Mode - Vp9Tables.NearestMv)];
            }

            if (e._interpFilter == Vp9Tables.Switchable)
            {
                rate += c.SwitchableInterp[(SwitchableContext() * 3) + mi.InterpFilter];
            }

            if (mi.Mode == Vp9Tables.NewMv)
            {
                for (int reference = 0; reference < (mi.HasSecondRef ? 2 : 1); reference++)
                {
                    int refMv = _nearest[mi.Ref(reference)];
                    int mv = mi.Mv(reference);
                    rate += c.Mv(Vp9ModeInfo.Row(mv) - Vp9ModeInfo.Row(refMv), Vp9ModeInfo.Col(mv) - Vp9ModeInfo.Col(refMv), e._allowHighPrecisionMv && UseMvHp(refMv));
                }
            }

            return rate;
        }

        private int RefFramesRate(ref Vp9ModeInfo mi)
        {
            Vp9Encoder<T> e = _e;
            Vp9RdCosts c = e._costs;
            if (e.SegmentFeatureActive(mi.SegmentId, Vp9Tables.SegLvlRefFrame))
            {
                return 0;
            }

            int rate = 0;
            bool compound = mi.HasSecondRef;
            if (e._referenceMode == Vp9Tables.ReferenceModeSelect)
            {
                rate += c.CompInter[(ReferenceModeContext() * 2) + (compound ? 1 : 0)];
            }

            if (compound)
            {
                int idx = e._refSignBias[e._compFixedRef];
                int varRef = mi.Ref(idx == 0 ? 1 : 0);
                rate += c.CompRef[(CompRefContext() * 2) + (varRef == e._compVarRef[1] ? 1 : 0)];
            }
            else
            {
                int bit0 = mi.Ref0 != Vp9Tables.LastFrame ? 1 : 0;
                rate += c.SingleRef[(((SingleRefP1Context() * 2) + 0) * 2) + bit0];
                if (bit0 != 0)
                {
                    rate += c.SingleRef[(((SingleRefP2Context() * 2) + 1) * 2) + (mi.Ref0 == Vp9Tables.AltrefFrame ? 1 : 0)];
                }
            }

            return rate;
        }

        // Packing.

        private void PackInterBlock(ref Vp9ModeInfo mi)
        {
            Vp9Encoder<T> e = _e;
            byte[] p = e._fc.P;
            PackRefFrames(ref mi);
            int bsize = mi.SbType;
            ReadOnlySpan<sbyte> search = Vp9Tables.MvRefBlocks.AsSpan(bsize * 16, 16);
            int ctx = ModeContext(search);
            if (!SegmentSkips(mi.SegmentId))
            {
                int symbol = mi.Mode - Vp9Tables.NearestMv;
                PutTree(Vp9EncoderTables.InterMode, p.AsSpan(Vp9FrameContext.InterMode + (ctx * 3), 3), symbol);
                if (_counting)
                {
                    Counts.InterMode[(ctx * 4) + symbol]++;
                }
            }

            if (e._interpFilter == Vp9Tables.Switchable)
            {
                int fctx = SwitchableContext();
                PutTree(Vp9EncoderTables.SwitchableInterp, p.AsSpan(Vp9FrameContext.SwitchableInterp + (fctx * 2), 2), mi.InterpFilter);
                if (_counting)
                {
                    Counts.SwitchableInterp[(fctx * 3) + mi.InterpFilter]++;
                }
            }

            if (mi.Mode == Vp9Tables.NewMv)
            {
                Span<int> list = stackalloc int[2];
                for (int reference = 0; reference < (mi.HasSecondRef ? 2 : 1); reference++)
                {
                    FindMvRefs(mi.Ref(reference), search, list);
                    int refMv = LowerMvPrecision(list[0], e._allowHighPrecisionMv);
                    int mv = mi.Mv(reference);
                    PackMv(Vp9ModeInfo.Row(mv) - Vp9ModeInfo.Row(refMv), Vp9ModeInfo.Col(mv) - Vp9ModeInfo.Col(refMv), e._allowHighPrecisionMv && UseMvHp(refMv));
                }
            }
        }

        private void PackRefFrames(ref Vp9ModeInfo mi)
        {
            Vp9Encoder<T> e = _e;
            byte[] p = e._fc.P;
            if (e.SegmentFeatureActive(mi.SegmentId, Vp9Tables.SegLvlRefFrame))
            {
                return;
            }

            bool compound = mi.HasSecondRef;
            if (e._referenceMode == Vp9Tables.ReferenceModeSelect)
            {
                int ctx = ReferenceModeContext();
                Put(compound ? 1 : 0, p[Vp9FrameContext.CompInter + ctx]);
                if (_counting)
                {
                    Counts.CompInter[(ctx * 2) + (compound ? 1 : 0)]++;
                }
            }

            if (compound)
            {
                int idx = e._refSignBias[e._compFixedRef];
                int varRef = mi.Ref(idx == 0 ? 1 : 0);
                int bit = varRef == e._compVarRef[1] ? 1 : 0;
                int ctx = CompRefContext();
                Put(bit, p[Vp9FrameContext.CompRef + ctx]);
                if (_counting)
                {
                    Counts.CompRef[(ctx * 2) + bit]++;
                }
            }
            else
            {
                int bit0 = mi.Ref0 != Vp9Tables.LastFrame ? 1 : 0;
                int ctx0 = SingleRefP1Context();
                Put(bit0, p[Vp9FrameContext.SingleRef + (ctx0 * 2)]);
                if (_counting)
                {
                    Counts.SingleRef[(((ctx0 * 2) + 0) * 2) + bit0]++;
                }

                if (bit0 != 0)
                {
                    int bit1 = mi.Ref0 == Vp9Tables.AltrefFrame ? 1 : 0;
                    int ctx1 = SingleRefP2Context();
                    Put(bit1, p[Vp9FrameContext.SingleRef + (ctx1 * 2) + 1]);
                    if (_counting)
                    {
                        Counts.SingleRef[(((ctx1 * 2) + 1) * 2) + bit1]++;
                    }
                }
            }
        }

        private void PackMv(int row, int col, bool usehp)
        {
            byte[] p = _e._fc.P;
            int joint = (row != 0 ? 2 : 0) | (col != 0 ? 1 : 0);
            PutTree(Vp9EncoderTables.MvJoint, p.AsSpan(Vp9FrameContext.MvJoints, 3), joint);
            if (row != 0)
            {
                PackMvComponent(p, Vp9FrameContext.MvComp, row, usehp);
            }

            if (col != 0)
            {
                PackMvComponent(p, Vp9FrameContext.MvComp + Vp9FrameContext.MvCompSize, col, usehp);
            }

            if (_counting)
            {
                IncMv(row, col);
            }
        }

        private void PackMvComponent(byte[] p, int c, int v, bool usehp)
        {
            int sign = v < 0 ? 1 : 0;
            int z = Math.Abs(v) - 1;
            int mvClass = z >= 2 * 4096 ? 10 : Log2(z >> 3);
            int offset = z - (mvClass != 0 ? 2 << (mvClass + 2) : 0);
            int d = offset >> 3, fr = (offset >> 1) & 3, hp = offset & 1;
            Put(sign, p[c + Vp9FrameContext.MvSign]);
            PutTree(Vp9EncoderTables.MvClass, p.AsSpan(c + Vp9FrameContext.MvClasses, 10), mvClass);
            if (mvClass == 0)
            {
                PutTree(Vp9EncoderTables.MvClass0, p.AsSpan(c + Vp9FrameContext.MvClass0, 1), d);
            }
            else
            {
                for (int i = 0; i < mvClass; i++)
                {
                    Put((d >> i) & 1, p[c + Vp9FrameContext.MvBits + i]);
                }
            }

            PutTree(Vp9EncoderTables.MvFp, mvClass == 0 ? p.AsSpan(c + Vp9FrameContext.MvClass0Fp + (d * 3), 3) : p.AsSpan(c + Vp9FrameContext.MvFp, 3), fr);
            if (usehp)
            {
                Put(hp, p[c + (mvClass == 0 ? Vp9FrameContext.MvClass0Hp : Vp9FrameContext.MvHp)]);
            }
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
                for (int i = 0; i < mvClass; i++)
                {
                    c[k + Vp9Counts.MvBits + (i * 2) + ((d >> i) & 1)]++;
                }

                c[k + Vp9Counts.MvFp + f]++;
                c[k + Vp9Counts.MvHp + e]++;
            }
        }

        private static int Log2(int n) => n < 2 ? 0 : System.Numerics.BitOperations.Log2((uint)n);

        // Reference contexts (vp9_pred_common.c), as the decoder computes them.

        private int ReferenceModeContext()
        {
            Vp9Encoder<T> e = _e;
            bool hasAbove = _aboveMi >= 0, hasLeft = _leftMi >= 0;
            int fixedRef = e._compFixedRef;
            if (hasAbove && hasLeft)
            {
                ref Vp9ModeInfo above = ref e._mi[_aboveMi];
                ref Vp9ModeInfo left = ref e._mi[_leftMi];
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
                ref Vp9ModeInfo edge = ref e._mi[hasAbove ? _aboveMi : _leftMi];
                return !edge.HasSecondRef ? (edge.Ref0 == fixedRef ? 1 : 0) : 3;
            }

            return 1;
        }

        private int CompRefContext()
        {
            Vp9Encoder<T> e = _e;
            bool aboveIn = _aboveMi >= 0, leftIn = _leftMi >= 0;
            int fixRefIdx = e._refSignBias[e._compFixedRef];
            int varRefIdx = fixRefIdx == 0 ? 1 : 0;
            int var0 = e._compVarRef[0], var1 = e._compVarRef[1], fixedRef = e._compFixedRef;
            if (aboveIn && leftIn)
            {
                ref Vp9ModeInfo above = ref e._mi[_aboveMi];
                ref Vp9ModeInfo left = ref e._mi[_leftMi];
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
                ref Vp9ModeInfo edge = ref e._mi[aboveIn ? _aboveMi : _leftMi];
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
            Vp9Encoder<T> e = _e;
            bool hasAbove = _aboveMi >= 0, hasLeft = _leftMi >= 0;
            const int Last = Vp9Tables.LastFrame;
            if (hasAbove && hasLeft)
            {
                ref Vp9ModeInfo above = ref e._mi[_aboveMi];
                ref Vp9ModeInfo left = ref e._mi[_leftMi];
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
                ref Vp9ModeInfo edge = ref e._mi[hasAbove ? _aboveMi : _leftMi];
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
            Vp9Encoder<T> e = _e;
            bool hasAbove = _aboveMi >= 0, hasLeft = _leftMi >= 0;
            const int Last = Vp9Tables.LastFrame, Golden = Vp9Tables.GoldenFrame, Altref = Vp9Tables.AltrefFrame;
            if (hasAbove && hasLeft)
            {
                ref Vp9ModeInfo above = ref e._mi[_aboveMi];
                ref Vp9ModeInfo left = ref e._mi[_leftMi];
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
                ref Vp9ModeInfo edge = ref e._mi[hasAbove ? _aboveMi : _leftMi];
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
