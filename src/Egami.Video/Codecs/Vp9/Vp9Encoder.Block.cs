using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Egami.Video.Codecs.Vp9;

/// <summary>Coding one block: its prediction, transforms, quantization, token costs and mode info costs.</summary>
internal sealed partial class Vp9Encoder<T>
{
    internal sealed partial class Tile
    {
        /// <summary>
        /// Codes a block with the modes in <paramref name="mi"/> (encode_superblock): predicts it, codes its residual, writes its
        /// reconstruction, mode info and contexts, and when the tile keeps its output, its tokens. Sets the skip flag when nothing
        /// is coded. Returns the block's rate (mode info and tokens) and distortion.
        /// </summary>
        private Rd EncodeBlock(int miRow, int miCol, int bsize, ref Vp9ModeInfo mi)
        {
            SetBlock(miRow, miCol, bsize);
            mi.SbType = (byte)bsize;
            PlaceBlock(ref mi);
            ref Vp9ModeInfo placed = ref _e._mi[_miIndex];
            int tokenStart = _tokenCount;
            int tokenRate = 0;
            long dist = 0;
            bool any = false;
            if (!placed.IsInter)
            {
                for (int plane = 0; plane < 3; plane++)
                {
                    LoadContexts(plane, _ta, _tl);
                    Rd rd = IntraPlane(ref placed, plane, _ta, _tl, long.MaxValue, placed.Skip != 0, out bool nonZero);
                    StoreContexts(plane, _ta, _tl);
                    tokenRate += rd.Rate;
                    dist += rd.Dist;
                    any |= nonZero;
                }
            }
            else
            {
                BuildInterPredictors(ref placed, _pred);
                for (int plane = 0; plane < 3; plane++)
                {
                    LoadContexts(plane, _ta, _tl);
                    Rd rd = InterPlane(ref placed, plane, _ta, _tl, placed.Skip != 0, true, out bool nonZero);
                    StoreContexts(plane, _ta, _tl);
                    tokenRate += rd.Rate;
                    dist += rd.Dist;
                    any |= nonZero;
                }
            }

            if (!any && placed.Skip == 0)
            {
                // Nothing coded: a skipped block, whose tokens (all end of block) go.
                placed.Skip = 1;
                _tokenCount = tokenStart;
                tokenRate = 0;
            }

            if (placed.Skip != 0)
            {
                tokenRate = 0;
                if (placed.IsInter)
                {
                    placed.TxSize = (byte)Math.Min(Vp9Tables.MaxTxSizeLookup[bsize], (int)Vp9Tables.TxModeToBiggestTxSize[_e._txMode]);
                }

                for (int p = 0; p < 3; p++)
                {
                    _e._aboveContext[p].AsSpan(AboveContextIndex(p), _n4W[p]).Clear();
                    _leftContext[p].AsSpan(LeftContextIndex(p), _n4H[p]).Clear();
                }
            }

            mi = placed;
            return new Rd(tokenRate + ModeInfoRate(ref placed), dist);
        }

        /// <summary>Searches a block's modes, then leaves the best coded. Returns its rate and distortion, or <see cref="Rd.Max"/> above <paramref name="bestCost"/>.</summary>
        private Rd SearchBlock(int miRow, int miCol, int bsize, ref Vp9ModeInfo best, long bestCost)
        {
            if (bestCost <= 0)
            {
                return Rd.Max;
            }

            // 4:2:2 has no chroma block for some sizes (8x16 and the other tall halves): those cannot be coded.
            if (bsize >= Vp9Tables.Block8x8 && Vp9Tables.SsSizeLookup[(bsize * 4) + (_e._subX * 2) + _e._subY] == Vp9Tables.BlockInvalid)
            {
                return Rd.Max;
            }

            SetBlock(miRow, miCol, bsize);
            best = default;
            best.SbType = (byte)bsize;
            best.SegmentId = (byte)_e.SegmentFor(this, miRow, miCol, bsize);
            bool found = _e._frameIsIntra ? PickIntra(ref best, bestCost) != long.MaxValue : PickInter(ref best, bestCost);
            if (!found)
            {
                return Rd.Max;
            }

            Rd rd = EncodeBlock(miRow, miCol, bsize, ref best);
            return RdCost(rd) < bestCost ? rd : Rd.Max;
        }

        // Intra coding.

        /// <summary>
        /// Predicts and codes one plane of an intra block, transform block by transform block, and writes its reconstruction.
        /// Stops early (returning <see cref="Rd.Max"/>) once the cost passes <paramref name="limit"/>.
        /// </summary>
        private Rd IntraPlane(ref Vp9ModeInfo mi, int plane, Span<byte> above, Span<byte> left, long limit, bool skip, out bool nonZero)
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
            int rate = 0;
            long dist = 0;
            nonZero = false;
            for (int row = 0; row < maxHigh; row += step)
            {
                for (int col = 0; col < maxWide; col += step)
                {
                    int mode = plane == 0 ? mi.Mode : mi.UVMode;
                    if (mi.SbType < Vp9Tables.Block8x8 && plane == 0)
                    {
                        mode = mi.Bmi[(row << 1) + col].Mode;
                    }

                    int x0 = ((_miCol * 8) >> _ssX[plane]) + (4 * col);
                    int y0 = ((_miRow * 8) >> _ssY[plane]) + (4 * row);
                    int dst = (y0 * stride) + x0;
                    bool haveTop = row != 0 || _aboveMi >= 0;
                    bool haveLeft = col != 0 || _leftMi >= 0;
                    bool haveRight = col + step < (1 << _n4Wl[plane]);
                    Vp9Intra<T>.Predict(pixels, dst, stride, mode, txSize, haveTop, haveLeft, haveRight, _toRightEdge < 0, _toBottomEdge < 0,
                        x0, y0, frame.AlignedWidth(plane), frame.AlignedHeight(plane), e._bitDepth);
                    Residual(source, (y0 * sourceStride) + x0, sourceStride, pixels, dst, stride, n, n);
                    if (skip)
                    {
                        dist += Sse(n * n);
                        continue;
                    }

                    int txType = plane != 0 || e._lossless ? Vp9Tables.DctDct : Vp9Tables.IntraModeToTxTypeLookup[mode];
                    Vp9Scan scan = plane != 0 || e._lossless ? Vp9Scan.Default(txSize) : Vp9Scan.For(txSize, txType);
                    int ctx = EntropyContext(above, left, col, row, step);
                    (int r, long d, int eob) = CodeTransform(plane, txSize, txType, scan, ctx, false, mi.SegmentId);
                    rate += r;
                    dist += d;
                    SetEntropyContext(plane, above, left, col, row, step, eob > 0);
                    if (eob > 0)
                    {
                        nonZero = true;
                        Reconstruct(pixels.Slice(dst), stride, txSize, txType, eob, scan);
                    }

                    if (RdCost(rate, dist) > limit)
                    {
                        return Rd.Max;
                    }
                }
            }

            return new Rd(rate, dist);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int UVTxSize(ref Vp9ModeInfo mi, int plane) =>
            mi.SbType < Vp9Tables.Block8x8 ? 0 : Vp9Tables.UVTxSizeLookup[(mi.SbType * 16) + (mi.TxSize * 4) + (_ssX[plane] * 2) + _ssY[plane]];

        /// <summary>Writes source minus prediction into the residual buffer, <paramref name="w"/> samples a row.</summary>
        private void Residual(T[] source, int sourceOffset, int sourceStride, ReadOnlySpan<T> pred, int predOffset, int predStride, int w, int h)
        {
            Span<short> residual = _residual;
            if (typeof(T) == typeof(byte))
            {
                ReadOnlySpan<byte> s = MemoryMarshal.Cast<T, byte>(source.AsSpan());
                ReadOnlySpan<byte> p = MemoryMarshal.Cast<T, byte>(pred);
                Vp9EncoderSimd.Subtract(s, sourceOffset, sourceStride, p, predOffset, predStride, residual, w, h);
                return;
            }

            for (int y = 0; y < h; y++)
            {
                ReadOnlySpan<T> s = source.AsSpan(sourceOffset + (y * sourceStride), w);
                ReadOnlySpan<T> p = pred.Slice(predOffset + (y * predStride), w);
                Span<short> r = residual.Slice(y * w, w);
                for (int x = 0; x < w; x++)
                {
                    r[x] = (short)(int.CreateTruncating(s[x]) - int.CreateTruncating(p[x]));
                }
            }
        }

        /// <summary>Sixteen times the residual's squared sum, in 8-bit units.</summary>
        private long Sse(int count)
        {
            long sum = Vp9EncoderSimd.SumSquares(_residual.AsSpan(0, count));
            return (sum << 4) >> (2 * (_e._bitDepth - 8));
        }

        private void Reconstruct(Span<T> dst, int stride, int txSize, int txType, int eob, Vp9Scan scan)
        {
            if (_e._lossless)
            {
                Vp9Transform.Iwht4x4Add(_dqcoeff, dst, stride, eob, _e._bitDepth);
            }
            else
            {
                Vp9Transform.InverseAdd(_dqcoeff, dst, stride, txSize, txType, eob, _e._bitDepth);
            }
        }

        // Transform, quantization and tokens.

        /// <summary>
        /// Transforms and quantizes the residual buffer as one transform block, costs its tokens in context
        /// <paramref name="ctx"/>, and keeps them when the tile keeps its output. Leaves the dequantized coefficients in raster
        /// order for the reconstruction. Returns the token rate, the distortion and the end of block.
        /// </summary>
        private (int Rate, long Dist, int Eob) CodeTransform(int plane, int txSize, int txType, Vp9Scan scan, int ctx, bool inter, int segment)
        {
            Vp9Encoder<T> e = _e;
            int n = 4 << txSize;
            if (e._lossless)
            {
                Vp9Transform.ForwardWht(_residual, n, _coeff);
            }
            else if (typeof(T) == typeof(byte) && Vp9TransformSimd.IsSupported)
            {
                Vp9TransformSimd.Forward(_residual, n, _coeff, txSize, txType);
            }
            else
            {
                Vp9Transform.Forward(_residual, n, _coeff, txSize, txType, txSize == Vp9Tables.Tx32x32);
            }

            short[] dequant = plane == 0 ? e._yDequant : e._uvDequant;
            int dqDc = dequant[segment * 2], dqAc = dequant[(segment * 2) + 1];
            int type = plane > 0 ? 1 : 0;
            int reference = inter ? 1 : 0;
            int eob = Quantize(txSize, scan, dqDc, dqAc, out long err);
            if (eob > 0 && !e._lossless && (_output ? e._plan.Effort >= 1 : e._plan.Effort >= TrellisSearchEffort))
            {
                eob = Optimize(txSize, type, reference, ctx, scan, dqDc, dqAc, eob, ref err);
            }

            int rate = CostCoeffs(txSize, type, reference, ctx, scan, eob);
            long dist = (txSize == Vp9Tables.Tx32x32 ? err : err >> 2) >> (2 * (e._bitDepth - 8));
            if (_output)
            {
                EnsureTokens(eob + 1);
                _tokens[_tokenCount++] = eob;
                _qcoeff.AsSpan(0, eob).CopyTo(_tokens.AsSpan(_tokenCount));
                _tokenCount += eob;
            }

            return (rate, dist, eob);
        }

        /// <summary>
        /// Quantizes the coefficients (vpx_quantize_b's dead zone and rounding): levels in scan order into the level buffer,
        /// dequantized values in raster order. Returns the end of block and the squared error in the transform domain.
        /// </summary>
        private int Quantize(int txSize, Vp9Scan scan, int dqDc, int dqAc, out long err)
        {
            int count = 16 << (txSize << 1);
            ReadOnlySpan<short> order = scan.Order;
            Span<int> coeff = _coeff.AsSpan(0, count);
            Span<int> dq = _dqcoeff.AsSpan(0, count);
            Span<int> q = _qcoeff.AsSpan(0, count);
            int shift = txSize == Vp9Tables.Tx32x32 ? 1 : 0;
            bool lossless = _e._lossless;
            int zbinFactor = lossless ? 64 : DcZbinFactor(dqDc);
            int roundFactor = lossless ? 64 : 48;
            int zbinDc = ((zbinFactor * dqDc) + 64) >> 7, zbinAc = ((zbinFactor * dqAc) + 64) >> 7;
            int roundDc = (roundFactor * dqDc) >> 7, roundAc = (roundFactor * dqAc) >> 7;
            // First the squared sum and the last coefficient in scan order above the dead zone, over the raster: most blocks
            // have few such coefficients, so the levels are found for those positions of the scan only.
            ReadOnlySpan<short> iscan = Vp9EncoderTables.InverseScan(scan);
            long total = 0;
            int last = -1;
            int start = 0;
            if (count >= 16 && Vector256.IsHardwareAccelerated)
            {
                Vector256<int> zbinV = Vector256.Create((zbinAc + (1 << shift) - 1) >> shift);
                Vector256<long> squares = Vector256<long>.Zero;
                ref int c0 = ref MemoryMarshal.GetReference(coeff);
                for (int i = 0; i < count; i += 8)
                {
                    Vector256<int> v = Vector256.LoadUnsafe(ref c0, (nuint)i);
                    (Vector256<long> lo, Vector256<long> hi) = Vector256.Widen(v);
                    squares += (lo * lo) + (hi * hi);
                    uint mask = Vector256.GreaterThanOrEqual(Vector256.Abs(v), zbinV).ExtractMostSignificantBits();
                    if (i == 0)
                    {
                        mask &= ~1u;
                    }

                    while (mask != 0)
                    {
                        int lane = System.Numerics.BitOperations.TrailingZeroCount(mask);
                        last = Math.Max(last, iscan[i + lane]);
                        mask &= mask - 1;
                    }
                }

                total = Vector256.Sum(squares);
                start = count;
            }

            for (int i = start; i < count; i++)
            {
                int c = coeff[i];
                total += (long)c * c;
                if (i != 0 && (Math.Abs(c) << shift) >= zbinAc)
                {
                    last = Math.Max(last, iscan[i]);
                }
            }

            if ((Math.Abs(coeff[0]) << shift) >= zbinDc)
            {
                last = Math.Max(last, iscan[0]);
            }

            dq.Clear();
            int eob = 0;
            for (int i = 0; i <= last; i++)
            {
                int rc = order[i];
                int c = coeff[rc];
                int a = Math.Abs(c) << shift;
                int level = 0;
                int dqv = rc == 0 ? dqDc : dqAc;
                if (a >= (rc == 0 ? zbinDc : zbinAc))
                {
                    level = (a + (rc == 0 ? roundDc : roundAc)) / dqv;
                }

                if (level != 0)
                {
                    int value = (level * dqv) >> shift;
                    int signed = c < 0 ? -value : value;
                    dq[rc] = signed;
                    q[i] = c < 0 ? -level : level;
                    long diff = c - signed;
                    total += (diff * diff) - ((long)c * c);
                    eob = i + 1;
                }
                else
                {
                    q[i] = 0;
                }
            }

            err = total;
            return eob;
        }

        /// <summary>Each plane type's and reference type's weight of the trellis's rate (plane_rd_mult).</summary>
        private static ReadOnlySpan<int> PlaneRdMult => [10, 6, 8, 5];

        /// <summary>
        /// Lowers levels where that saves more bits than it costs in error, and moves the end of block back where that pays
        /// (libvpx 1.14.1's vp9_optimize_b: for each coefficient in turn the level or one less, by the RD cost of the level and
        /// its effect on the next token's context). Updates the levels, the dequantized values and the error; returns the new end
        /// of block.
        /// </summary>
        private int Optimize(int txSize, int type, int reference, int ctx, Vp9Scan scan, int dqDc, int dqAc, int eob, ref long err)
        {
            Vp9Encoder<T> e = _e;
            int shift = txSize == Vp9Tables.Tx32x32 ? 1 : 0;
            int hbd = e._bitDepth - 8;
            int maxEob = 16 << (txSize << 1);
            int[] tokens = e._costs.Tokens;
            Vp9RdCosts costs = e._costs;
            int baseIndex = Vp9RdCosts.TokenIndex(txSize, type, reference, 0, 0);
            ReadOnlySpan<byte> bands = txSize == Vp9Tables.Tx4x4 ? Vp9Tables.CoefbandTrans4x4 : Vp9Tables.CoefbandTrans8x8Plus;
            ReadOnlySpan<short> order = scan.Order;
            ReadOnlySpan<short> neighbors = scan.Neighbors;
            ReadOnlySpan<byte> energy = Vp9EncoderTables.EnergyClass;
            Span<byte> cache = _tokenCache;
            Span<int> q = _qcoeff;
            Span<int> dq = _dqcoeff;
            Span<int> coeff = _coeff;
            long rdmult = ((long)e._rdMult * PlaneRdMult[(reference * 2) + type]) >> 1;
            for (int i = 0; i < eob; i++)
            {
                cache[order[i]] = energy[Vp9EncoderTables.Token(Math.Abs(q[i]))];
            }

            long accuRate = 0;
            long accuError = 1L << 50;
            int xPrev = 1;
            int finalEob = 0;
            int beforeQc = 0, beforeDqc = 0;
            long best = Cost(tokens[baseIndex + (((bands[0] * 6) + ctx) * 24) + Vp9RdCosts.EobToken], accuError);
            for (int i = 0; i < eob; i++)
            {
                int rc = order[i];
                int x = q[i];
                int ctxCur = i == 0 ? ctx : (1 + cache[neighbors[i * 2]] + cache[neighbors[(i * 2) + 1]]) >> 1;
                int tc = baseIndex + (((bands[i] * 6) + ctxCur) * 24) + (xPrev == 0 ? 12 : 0);
                if (x == 0)
                {
                    accuRate += tokens[tc];
                    xPrev = 0;
                    continue;
                }

                int dqv = rc == 0 ? dqDc : dqAc;
                int c = coeff[rc];
                long diffZero = (long)(-c << shift) >> hbd;
                long distZero = diffZero * diffZero;
                long diff0 = (long)((dq[rc] - c) << shift) >> hbd;
                long dist0 = diff0 * diff0;
                int sign = x < 0 ? -1 : 0;
                int x1 = x - (2 * sign) - 1;
                long dist1;
                if (x1 != 0)
                {
                    int step = dqv >> hbd;
                    long diff1 = diff0 - ((step + sign) ^ sign);
                    dist1 = diff1 * diff1;
                }
                else
                {
                    dist1 = distZero;
                }

                int m0 = Math.Abs(x), m1 = Math.Abs(x1);
                int t0 = Vp9EncoderTables.Token(m0), t1 = Vp9EncoderTables.Token(m1);
                long rate0 = costs.Extra(m0) + tokens[tc + t0];
                long rate1 = costs.Extra(m1) + tokens[tc + t1];
                long next0 = 0, next1 = 0, nextEob0 = 0, nextEob1 = 0;
                if (i < maxEob - 1)
                {
                    int tokenNext = i + 1 != eob ? Vp9EncoderTables.Token(Math.Abs(q[i + 1])) : Vp9RdCosts.EobToken;
                    int bandNext = bands[i + 1];
                    cache[rc] = energy[t0];
                    int ctxNext = (1 + cache[neighbors[(i + 1) * 2]] + cache[neighbors[((i + 1) * 2) + 1]]) >> 1;
                    int at = baseIndex + (((bandNext * 6) + ctxNext) * 24);
                    next0 = tokens[at + tokenNext];
                    nextEob0 = tokens[at + Vp9RdCosts.EobToken];
                    cache[rc] = energy[t1];
                    ctxNext = (1 + cache[neighbors[(i + 1) * 2]] + cache[neighbors[((i + 1) * 2) + 1]]) >> 1;
                    at = baseIndex + (((bandNext * 6) + ctxNext) * 24) + (x1 == 0 ? 12 : 0);
                    next1 = tokens[at + tokenNext];
                    if (x1 != 0)
                    {
                        nextEob1 = tokens[at + Vp9RdCosts.EobToken];
                    }
                }

                bool better1 = Cost(rate1 + next1, dist1) < Cost(rate0 + next0, dist0);
                long eobCost0 = Cost(accuRate + rate0 + nextEob0, accuError + dist0 - distZero);
                long eobCost1 = eobCost0;
                bool eobBetter1 = false;
                if (x1 != 0)
                {
                    eobCost1 = Cost(accuRate + rate1 + nextEob1, accuError + dist1 - distZero);
                    eobBetter1 = eobCost1 < eobCost0;
                }

                int dqc0 = dq[rc];
                int dqc1 = 0;
                if ((better1 || eobBetter1) && x1 != 0)
                {
                    int v = (m1 * dqv) >> shift;
                    dqc1 = x1 < 0 ? -v : v;
                }

                if (better1)
                {
                    q[i] = x1;
                    dq[rc] = dqc1;
                    accuRate += rate1;
                    accuError += dist1 - distZero;
                    cache[rc] = energy[t1];
                }
                else
                {
                    accuRate += rate0;
                    accuError += dist0 - distZero;
                    cache[rc] = energy[t0];
                }

                xPrev = q[i];
                bool useX1 = x1 != 0 && eobBetter1;
                long bestHere = useX1 ? eobCost1 : eobCost0;
                if (bestHere < best)
                {
                    best = bestHere;
                    finalEob = i + 1;
                    beforeQc = useX1 ? x1 : x;
                    beforeDqc = useX1 ? dqc1 : dqc0;
                }
            }

            if (finalEob > 0)
            {
                q[finalEob - 1] = beforeQc;
                dq[order[finalEob - 1]] = beforeDqc;
            }

            for (int i = finalEob; i < eob; i++)
            {
                q[i] = 0;
                dq[order[i]] = 0;
            }

            // The error again, over every coefficient.
            int count = 16 << (txSize << 1);
            long total = 0;
            for (int i = 0; i < count; i++)
            {
                long d = coeff[i] - dq[i];
                total += d * d;
            }

            err = total;
            return finalEob;

            long Cost(long rate, long dist) => ((rate * rdmult) + 256 >> 9) + (dist << 7);
        }

        private int DcZbinFactor(int dqDc)
        {
            int limit = _e._bitDepth switch { 8 => 148, 10 => 592, _ => 2368 };
            return DcQuantOf(dqDc) < limit ? 84 : 80;
        }

        private static int DcQuantOf(int dqDc) => dqDc;

        /// <summary>The cost of a transform block's tokens (cost_coeffs).</summary>
        private int CostCoeffs(int txSize, int type, int reference, int ctx, Vp9Scan scan, int eob)
        {
            int[] tokens = _e._costs.Tokens;
            if (eob == 0)
            {
                return tokens[Vp9RdCosts.TokenIndex(txSize, type, reference, 0, ctx) + Vp9RdCosts.EobToken];
            }

            ReadOnlySpan<byte> bands = txSize == Vp9Tables.Tx4x4 ? Vp9Tables.CoefbandTrans4x4 : Vp9Tables.CoefbandTrans8x8Plus;
            ReadOnlySpan<short> order = scan.Order;
            ReadOnlySpan<short> neighbors = scan.Neighbors;
            ReadOnlySpan<byte> energy = Vp9EncoderTables.EnergyClass;
            Span<byte> cache = _tokenCache;
            Vp9RdCosts costs = _e._costs;
            int baseIndex = Vp9RdCosts.TokenIndex(txSize, type, reference, 0, 0);
            int cost = 0;
            int skipEob = 0;
            int maxEob = 16 << (txSize << 1);
            for (int c = 0; c < eob; c++)
            {
                if (c > 0)
                {
                    ctx = (1 + cache[neighbors[c * 2]] + cache[neighbors[(c * 2) + 1]]) >> 1;
                }

                int v = _qcoeff[c];
                int m = Math.Abs(v);
                int t = Vp9EncoderTables.Token(m);
                cost += tokens[baseIndex + (((bands[c] * 6) + ctx) * 24) + (skipEob * 12) + t] + costs.Extra(m);
                cache[order[c]] = energy[t];
                skipEob = t == 0 ? 1 : 0;
            }

            if (eob < maxEob)
            {
                ctx = (1 + cache[neighbors[eob * 2]] + cache[neighbors[(eob * 2) + 1]]) >> 1;
                cost += tokens[baseIndex + (((bands[eob] * 6) + ctx) * 24) + Vp9RdCosts.EobToken];
            }

            return cost;
        }

        // Mode info contexts (vp9_pred_common.h) and costs.

        private int SkipContext() =>
            (_aboveMi >= 0 ? _e._mi[_aboveMi].Skip : 0) + (_leftMi >= 0 ? _e._mi[_leftMi].Skip : 0);

        private int TxSizeContext(int maxTx)
        {
            Vp9Encoder<T> e = _e;
            int aboveCtx = maxTx, leftCtx = maxTx;
            bool hasAbove = _aboveMi >= 0, hasLeft = _leftMi >= 0;
            if (hasAbove && e._mi[_aboveMi].Skip == 0)
            {
                aboveCtx = e._mi[_aboveMi].TxSize;
            }

            if (hasLeft && e._mi[_leftMi].Skip == 0)
            {
                leftCtx = e._mi[_leftMi].TxSize;
            }

            if (!hasLeft)
            {
                leftCtx = aboveCtx;
            }

            if (!hasAbove)
            {
                aboveCtx = leftCtx;
            }

            return aboveCtx + leftCtx > maxTx ? 1 : 0;
        }

        private int IntraInterContext()
        {
            Vp9Encoder<T> e = _e;
            bool hasAbove = _aboveMi >= 0, hasLeft = _leftMi >= 0;
            if (hasAbove && hasLeft)
            {
                bool aboveIntra = !e._mi[_aboveMi].IsInter, leftIntra = !e._mi[_leftMi].IsInter;
                return leftIntra && aboveIntra ? 3 : (leftIntra || aboveIntra ? 1 : 0);
            }

            if (hasAbove || hasLeft)
            {
                return 2 * (e._mi[hasAbove ? _aboveMi : _leftMi].IsInter ? 0 : 1);
            }

            return 0;
        }

        private int AboveBlockMode(ref Vp9ModeInfo cur, int b)
        {
            if (b == 0 || b == 1)
            {
                if (_aboveMi < 0)
                {
                    return Vp9Tables.DcPred;
                }

                ref Vp9ModeInfo above = ref _e._mi[_aboveMi];
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

                ref Vp9ModeInfo left = ref _e._mi[_leftMi];
                return left.IsInter ? Vp9Tables.DcPred : YMode(ref left, b + 1);
            }

            return cur.Bmi[b - 1].Mode;
        }

        private static int YMode(ref Vp9ModeInfo mi, int block) => mi.SbType < Vp9Tables.Block8x8 ? mi.Bmi[block].Mode : mi.Mode;

        /// <summary>The cost of a luma intra mode: by the neighbors' modes on intra frames, by size group otherwise.</summary>
        private int YModeCost(ref Vp9ModeInfo mi, int block, int mode)
        {
            if (_e._frameIsIntra)
            {
                int a = AboveBlockMode(ref mi, block), l = LeftBlockMode(ref mi, block);
                return _e._costs.KfYMode[(((a * 10) + l) * 10) + mode];
            }

            int group = mi.SbType < Vp9Tables.Block8x8 ? 0 : Vp9Tables.SizeGroupLookup[mi.SbType];
            return _e._costs.YMode[(group * 10) + mode];
        }

        private int TxSizeCost(ref Vp9ModeInfo mi, int txSize)
        {
            if (_e._txMode != Vp9Tables.TxModeSelect || mi.SbType < Vp9Tables.Block8x8 || (mi.IsInter && mi.Skip != 0))
            {
                return 0;
            }

            int maxTx = Vp9Tables.MaxTxSizeLookup[mi.SbType];
            if (maxTx == 0)
            {
                return 0;
            }

            return _e._costs.TxSize[(((maxTx * 2) + TxSizeContext(maxTx)) * 4) + txSize];
        }

        private int SegmentCost(int segment) => _e._segmentationEnabled ? _e._costs.Segment[segment] : 0;

        private bool SegmentSkips(int segment) => _e.SegmentFeatureActive(segment, Vp9Tables.SegLvlSkip);

        /// <summary>The cost of a block's mode info as the packer writes it.</summary>
        private int ModeInfoRate(ref Vp9ModeInfo mi)
        {
            Vp9RdCosts c = _e._costs;
            int rate = SegmentCost(mi.SegmentId);
            if (!SegmentSkips(mi.SegmentId))
            {
                rate += c.Skip[(SkipContext() * 2) + mi.Skip];
            }

            if (!_e._frameIsIntra)
            {
                rate += c.IntraInter[(IntraInterContext() * 2) + (mi.IsInter ? 1 : 0)];
            }

            rate += TxSizeCost(ref mi, mi.TxSize);
            if (mi.IsInter)
            {
                return rate + InterModeRate(ref mi);
            }

            return rate + IntraModesRate(ref mi);
        }

        private int IntraModesRate(ref Vp9ModeInfo mi)
        {
            int rate = 0;
            switch (mi.SbType)
            {
                case Vp9Tables.Block4x4:
                    for (int i = 0; i < 4; i++)
                    {
                        rate += YModeCost(ref mi, i, mi.Bmi[i].Mode);
                    }

                    break;
                default:
                    rate += YModeCost(ref mi, 0, mi.Mode);
                    break;
            }

            return rate + _e._costs.UVMode[(mi.Mode * 10) + mi.UVMode];
        }

        /// <summary>
        /// Picks an intra block's modes and transform size by rate and distortion (rd_pick_intra_sby_mode and
        /// rd_pick_intra_sbuv_mode), within <paramref name="bestCost"/>. Returns the block's RD cost with its mode info, or
        /// long.MaxValue when it cannot beat <paramref name="bestCost"/>.
        /// </summary>
        private long PickIntra(ref Vp9ModeInfo mi, long bestCost)
        {
            mi.Ref0 = Vp9Tables.IntraFrame;
            mi.Ref1 = Vp9Tables.NoRefFrame;
            mi.InterpFilter = Vp9Tables.SwitchableFilters;
            mi.Skip = 0;
            long lumaCost = PickIntraLuma(ref mi, bestCost, out _);
            if (lumaCost == long.MaxValue)
            {
                return long.MaxValue;
            }

            long chromaCost = PickIntraChroma(ref mi, bestCost - lumaCost);
            if (chromaCost == long.MaxValue)
            {
                return long.MaxValue;
            }

            // The rest of the mode info: segment, skip, the intra flag on inter frames.
            int rate = SegmentCost(mi.SegmentId) + (SegmentSkips(mi.SegmentId) ? 0 : _e._costs.Skip[SkipContext() * 2]);
            if (!_e._frameIsIntra)
            {
                rate += _e._costs.IntraInter[IntraInterContext() * 2];
            }

            return lumaCost + chromaCost + RdCost(rate, 0);
        }

        /// <summary>Picks the luma mode and transform size; returns the luma's RD cost (with mode and size costs), or long.MaxValue.</summary>
        private long PickIntraLuma(ref Vp9ModeInfo mi, long bestCost, out Rd bestRd)
        {
            Vp9Encoder<T> e = _e;
            bestRd = Rd.Max;
            if (mi.SbType == Vp9Tables.Block4x4)
            {
                return PickIntraSub8x8(ref mi, bestCost, out bestRd);
            }

            int maxTx = Math.Min(Vp9Tables.MaxTxSizeLookup[mi.SbType], (int)Vp9Tables.TxModeToBiggestTxSize[e._txMode]);
            int depth = e._frameIsIntra ? e.TxSearchDepth : e.InterFrameIntraTxDepth;
            int minTx = e._txMode == Vp9Tables.TxModeSelect ? Math.Max(0, maxTx - depth) : maxTx;
            long best = long.MaxValue;
            int bestMode = Vp9Tables.DcPred, bestTx = maxTx;
            Span<int> modes = stackalloc int[10];
            int count = e.IntraModesFor(mi.SbType, modes);
            Span<long> modeCosts = stackalloc long[10];
            int keep = e.IntraModesKept;
            for (int tx = maxTx; tx >= minTx; tx--)
            {
                mi.TxSize = (byte)tx;
                if (tx < maxTx && count > keep)
                {
                    // Smaller transforms only for the modes the largest liked best.
                    SortModes(modes[..count], modeCosts[..count]);
                    count = keep;
                }

                bool screening = tx == maxTx && count > keep && minTx < maxTx;
                for (int k = 0; k < count; k++)
                {
                    int mode = modes[k];
                    mi.Mode = (byte)mode;
                    LoadContexts(0, _ta2, _tl2);
                    long overhead = RdCost(YModeCost(ref mi, 0, mode) + TxSizeCost(ref mi, tx), 0);
                    long cap = screening ? (best == long.MaxValue ? bestCost : Math.Min(bestCost, best * 2)) : Math.Min(best, bestCost);
                    long limit = cap - overhead;
                    if (limit <= 0)
                    {
                        modeCosts[k] = long.MaxValue;
                        continue;
                    }

                    Rd rd = IntraPlane(ref mi, 0, _ta2, _tl2, limit, false, out _);
                    long cost = rd.IsMax ? long.MaxValue : RdCost(rd) + overhead;
                    modeCosts[k] = cost;
                    if (cost < best)
                    {
                        best = cost;
                        bestMode = mode;
                        bestTx = tx;
                        bestRd = rd;
                    }
                }
            }

            mi.Mode = (byte)bestMode;
            mi.TxSize = (byte)bestTx;
            return best;
        }

        private static void SortModes(Span<int> modes, Span<long> costs)
        {
            for (int i = 1; i < modes.Length; i++)
            {
                int m = modes[i];
                long c = costs[i];
                int j = i - 1;
                while (j >= 0 && costs[j] > c)
                {
                    modes[j + 1] = modes[j];
                    costs[j + 1] = costs[j];
                    j--;
                }

                modes[j + 1] = m;
                costs[j + 1] = c;
            }
        }

        /// <summary>Picks each 4x4 sub-block's mode in turn (rd_pick_intra_sub_8x8_y_mode).</summary>
        private long PickIntraSub8x8(ref Vp9ModeInfo mi, long bestCost, out Rd total)
        {
            Vp9Encoder<T> e = _e;
            mi.TxSize = Vp9Tables.Tx4x4;
            Vp9Frame<T> frame = e._recon;
            int stride = frame.Strides[0];
            Span<T> pixels = frame.Plane(0);
            T[] source = e._source[0];
            int sourceStride = e._sourceStride[0];
            LoadContexts(0, _ta, _tl);
            Span<T> keep = stackalloc T[16];
            int rateSum = 0;
            long distSum = 0;
            Span<int> modes = stackalloc int[10];
            int count = e.IntraModesFor(Vp9Tables.Block4x4, modes);
            for (int b = 0; b < 4; b++)
            {
                int row = b >> 1, col = b & 1;
                int x0 = (_miCol * 8) + (4 * col), y0 = (_miRow * 8) + (4 * row);
                int dst = (y0 * stride) + x0;
                bool haveTop = row != 0 || _aboveMi >= 0;
                bool haveLeft = col != 0 || _leftMi >= 0;
                bool haveRight = col + 1 < 2;
                long best = long.MaxValue;
                int bestMode = 0;
                int bestRate = 0;
                long bestDist = 0;
                bool bestHas = false;
                for (int k = 0; k < count; k++)
                {
                    int mode = modes[k];
                    mi.Bmi[b].Mode = (byte)mode;
                    Vp9Intra<T>.Predict(pixels, dst, stride, mode, 0, haveTop, haveLeft, haveRight, _toRightEdge < 0, _toBottomEdge < 0,
                        x0, y0, frame.AlignedWidth(0), frame.AlignedHeight(0), e._bitDepth);
                    Residual(source, (y0 * sourceStride) + x0, sourceStride, pixels, dst, stride, 4, 4);
                    int txType = e._lossless ? Vp9Tables.DctDct : Vp9Tables.IntraModeToTxTypeLookup[mode];
                    Vp9Scan scan = e._lossless ? Vp9Scan.Default(0) : Vp9Scan.For(0, txType);
                    int ctx = EntropyContext(_ta, _tl, col, row, 1);
                    (int r, long d, int eob) = CodeTransform(0, 0, txType, scan, ctx, false, mi.SegmentId);
                    r += YModeCost(ref mi, b, mode);
                    long cost = RdCost(r, d);
                    if (cost < best)
                    {
                        best = cost;
                        bestMode = mode;
                        bestRate = r;
                        bestDist = d;
                        bestHas = eob > 0;
                    }
                }

                // Code the sub-block again with its best mode: the next ones predict from it.
                mi.Bmi[b].Mode = (byte)bestMode;
                Vp9Intra<T>.Predict(pixels, dst, stride, bestMode, 0, haveTop, haveLeft, haveRight, _toRightEdge < 0, _toBottomEdge < 0,
                    x0, y0, frame.AlignedWidth(0), frame.AlignedHeight(0), e._bitDepth);
                Residual(source, (y0 * sourceStride) + x0, sourceStride, pixels, dst, stride, 4, 4);
                int bestType = e._lossless ? Vp9Tables.DctDct : Vp9Tables.IntraModeToTxTypeLookup[bestMode];
                Vp9Scan bestScan = e._lossless ? Vp9Scan.Default(0) : Vp9Scan.For(0, bestType);
                int bctx = EntropyContext(_ta, _tl, col, row, 1);
                (_, _, int beob) = CodeTransform(0, 0, bestType, bestScan, bctx, false, mi.SegmentId);
                if (beob > 0)
                {
                    Reconstruct(pixels.Slice(dst), stride, 0, bestType, beob, bestScan);
                }

                SetEntropyContext(0, _ta, _tl, col, row, 1, bestHas);
                rateSum += bestRate;
                distSum += bestDist;
                if (RdCost(rateSum, distSum) > bestCost)
                {
                    total = Rd.Max;
                    return long.MaxValue;
                }
            }

            mi.Mode = mi.Bmi.B3.Mode;
            _ = keep;
            total = new Rd(rateSum, distSum);
            return RdCost(total);
        }

        /// <summary>Picks the chroma mode for the luma mode and size already chosen; returns its RD cost with the mode's, or long.MaxValue above <paramref name="limit"/>.</summary>
        private long PickIntraChroma(ref Vp9ModeInfo mi, long limit)
        {
            Vp9Encoder<T> e = _e;
            long best = limit;
            int bestMode = -1;
            Span<int> modes = stackalloc int[10];
            int count = e.IntraChromaModes(modes);
            for (int k = 0; k < count; k++)
            {
                int mode = modes[k];
                mi.UVMode = (byte)mode;
                long cost = RdCost(e._costs.UVMode[(mi.Mode * 10) + mode], 0);
                for (int plane = 1; plane < 3 && cost < best; plane++)
                {
                    LoadContexts(plane, _ta2, _tl2);
                    Rd rd = IntraPlane(ref mi, plane, _ta2, _tl2, best - cost, false, out _);
                    cost = rd.IsMax ? long.MaxValue : cost + RdCost(rd);
                }

                if (cost < best)
                {
                    best = cost;
                    bestMode = mode;
                }
            }

            mi.UVMode = (byte)Math.Max(0, bestMode);
            return bestMode < 0 ? long.MaxValue : best;
        }
    }
}
