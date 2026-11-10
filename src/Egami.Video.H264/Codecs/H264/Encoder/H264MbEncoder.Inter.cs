using Egami.Nal;

namespace Egami.Video.Codecs.H264;

/// <summary>
/// P macroblocks: motion search over the references (integer samples by SAD in a hexagon or diamond pattern, then half and
/// quarter samples by SATD, each vector's cost with λ times its mvd bits), partition choice by that cost, then squared error
/// plus λ times the bits among P_Skip, the best partitionings with the 4x4 or 8x8 transform, and intra.
/// </summary>
internal sealed partial class H264MbEncoder
{
    /// <summary>The rounding offset of inter levels without RDOQ, in 1/256 of a step (a sixth).</summary>
    internal const int InterRound = 43;

    private static readonly (int X, int Y)[] Diamond = [(0, -1), (-1, 0), (1, 0), (0, 1)];
    private static readonly (int X, int Y)[] Hexagon = [(-2, 0), (-1, -2), (1, -2), (2, 0), (1, 2), (-1, 2)];
    private static readonly (int X, int Y)[] Square = [(-1, -1), (0, -1), (1, -1), (-1, 0), (1, 0), (-1, 1), (0, 1), (1, 1)];

    // The cost of a lone ±1 level after a run of zeros, which decides when a sparse block is dropped.
    private static readonly byte[] RunCost = [3, 2, 2, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    private readonly H264MbCode _skip = new();
    private readonly H264MbCode[] _inter = [new(), new(), new(), new()];
    private readonly H264MbCode _inter8 = new();
    private readonly byte[] _ip = new byte[256];
    private readonly byte[] _ipCb = new byte[64];
    private readonly byte[] _ipCr = new byte[64];
    private readonly byte[] _mc = new byte[256];
    private readonly byte[][] _saveY = [new byte[256], new byte[256], new byte[256], new byte[256], new byte[256]];
    private readonly byte[][] _saveC = [new byte[128], new byte[128], new byte[128], new byte[128], new byte[128]];
    private H264RefPlanes[] _refs = [];
    private int _refCount;
    private int _maxMvY;
    private bool _sub8x8;
    private int[] _frameMv = [];

    /// <summary>
    /// Sets the references of the P picture to code (list 0 in order), the largest vertical vector component its level allows
    /// in quarter samples, and whether 8x8 blocks may split further.
    /// </summary>
    public void SetReferences(H264RefPlanes[] refs, int count, int maxMvY, bool sub8x8)
    {
        _refs = refs;
        _refCount = count;
        _maxMvY = maxMvY;
        _sub8x8 = sub8x8;
        if (_frameMv.Length != _mb.WidthMbs * _mb.HeightMbs)
        {
            _frameMv = new int[_mb.WidthMbs * _mb.HeightMbs];
        }
    }

    /// <summary>Forgets the vectors of earlier pictures, which seed the search (after a keyframe).</summary>
    public void ResetMotion() => Array.Clear(_frameMv);

    /// <summary>
    /// Chooses, codes and reconstructs the macroblock at (mbX, mbY) of a P slice at <paramref name="qp"/>; returns its cost.
    /// </summary>
    public double EncodeP(int mbX, int mbY, int qp)
    {
        Setup(mbX, mbY, qp);
        _round = InterRound;
        int addr = (mbY * _mb.WidthMbs) + mbX;

        // P_Skip: reference 0 and the predicted vector, no residual.
        H264MbCode skip = _skip;
        int skipMv = _syntax.SkipMv();
        ClearInter(skip, H264MbKind.PSkip);
        skip.Mv.AsSpan().Fill(skipMv);
        _syntax.SetBlocks(0, 0, 4, 4, 0, skipMv);
        _refs[0].Luma(mbX * 16, mbY * 16, 16, 16, skipMv, _ip, 0, 16, true);
        _refs[0].Chroma(mbX * 8, mbY * 8, 8, 8, skipMv, _ipCb, _ipCr, 0, 8, true);
        WritePrediction();
        long skipSse = Sse(_srcY, _y, _lumaAt, _stride, 16, 16) + Sse(_srcCb, _cb, _chromaAt, _cstride, 8, 8) + Sse(_srcCr, _cr, _chromaAt, _cstride, 8, 8);
        double bestCost = skipSse + (_lambda * Rate(skip) / CabacCost.Bit);
        H264MbCode best = skip;
        int bestSlot = 0;
        SaveMb(0);

        // Motion search: the partitionings worth coding, by SATD and vector bits.
        Span<int> starts = stackalloc int[10];
        int count = Starts(addr, skipMv, starts);
        H264MbCode p16 = _inter[0];
        long cost16 = Search16x16(p16, starts[..count]);
        _frameMv[addr] = p16.Mv[0];
        H264MbCode? second = null;
        long secondCost = long.MaxValue;
        H264MbCode first = p16;
        long firstCost = cost16;
        if (_effort >= 1)
        {
            starts[count++] = p16.Mv[0];
            for (int partition = 1; partition <= (_effort >= 2 ? 3 : 2); partition++)
            {
                H264MbCode code = _inter[partition];
                long cost = partition == 3 ? Search8x8(code, starts[..count]) : SearchHalves(code, partition, starts[..count]);
                if (cost < firstCost)
                {
                    (second, secondCost) = (first, firstCost);
                    (first, firstCost) = (code, cost);
                }
                else if (cost < secondCost)
                {
                    (second, secondCost) = (code, cost);
                }
            }
        }

        // Rate-distortion: the best partitioning, and the second from effort 3.
        double interCost = CodeInter(first);
        SaveMb(1);
        if (interCost < bestCost)
        {
            (bestCost, best, bestSlot) = (interCost, first, 1);
        }

        if (_effort >= 3 && second is not null)
        {
            interCost = CodeInter(second);
            SaveMb(2);
            if (interCost < bestCost)
            {
                (bestCost, best, bestSlot) = (interCost, second, 2);
            }
        }

        if (_effort >= 1 || firstCost > 16 * 16 * 4)
        {
            double cost = Encode(mbX, mbY, qp);
            _round = InterRound;
            SaveMb(3);
            if (cost < bestCost)
            {
                (bestCost, best, bestSlot) = (cost, Best, 3);
            }
        }

        RestoreMb(bestSlot);
        Best = best;
        return bestCost;
    }

    private static void ClearInter(H264MbCode code, H264MbKind kind)
    {
        code.Kind = kind;
        code.Partition = 0;
        code.Transform8x8 = false;
        code.Cbp = 0;
        Array.Clear(code.SubTypes);
        Array.Clear(code.RefIdx);
        Array.Clear(code.Mvd);
        Array.Clear(code.LumaCount);
        Array.Clear(code.Luma8Count);
        Array.Clear(code.ChromaDcCount);
        Array.Clear(code.ChromaAcCount);
    }

    /// <summary>The vectors the search starts from: P_Skip's, zero and the last vectors found around the macroblock.</summary>
    private int Starts(int addr, int skipMv, Span<int> starts)
    {
        int n = 0, w = _mb.WidthMbs, h = _mb.HeightMbs;
        starts[n++] = skipMv;
        starts[n++] = 0;
        starts[n++] = _frameMv[addr];
        if (_mbX > 0)
        {
            starts[n++] = _frameMv[addr - 1];
        }

        if (_mbY > 0)
        {
            starts[n++] = _frameMv[addr - w];
        }

        if (_mbY > 0 && _mbX < w - 1)
        {
            starts[n++] = _frameMv[addr - w + 1];
        }

        if (_mbX < w - 1)
        {
            starts[n++] = _frameMv[addr + 1];
        }

        if (_mbY < h - 1)
        {
            starts[n++] = _frameMv[addr + w];
        }

        return n;
    }

    // ---- Partitionings ----

    private long Search16x16(H264MbCode code, ReadOnlySpan<int> starts)
    {
        ClearInter(code, H264MbKind.Inter);
        long best = long.MaxValue;
        int bestRef = 0, bestMv = 0;
        for (int r = 0; r < _refCount; r++)
        {
            int mvp = _syntax.PredictMv(0, 0, 4, 4, r, 0);
            int mv = Search(r, 0, 0, 4, 4, mvp, starts, out long cost);
            if (cost < best)
            {
                (best, bestRef, bestMv) = (cost, r, mv);
            }
        }

        code.RefIdx[0] = bestRef;
        code.Mv.AsSpan().Fill(bestMv);
        _syntax.SetBlocks(0, 0, 4, 4, bestRef, bestMv);
        return best + (long)_sadLambda;
    }

    /// <summary>16x8 (partition 1) or 8x16 (partition 2): each half searched in turn, the second predicted from the first.</summary>
    private long SearchHalves(H264MbCode code, int partition, ReadOnlySpan<int> starts)
    {
        ClearInter(code, H264MbKind.Inter);
        code.Partition = partition;
        long total = (long)(3 * _sadLambda);
        for (int p = 0; p < 2; p++)
        {
            (int bx, int by, int w, int h) = partition == 1 ? (0, p * 2, 4, 2) : (p * 2, 0, 2, 4);
            int mask = p == 0 ? 0 : partition == 1 ? 0x00FF : 0x3333;
            long best = long.MaxValue;
            int bestRef = 0, bestMv = 0;
            for (int r = 0; r < _refCount; r++)
            {
                int mvp = _syntax.PredictMv(bx, by, w, h, r, mask);
                int mv = Search(r, bx, by, w, h, mvp, starts, out long cost);
                if (cost < best)
                {
                    (best, bestRef, bestMv) = (cost, r, mv);
                }
            }

            code.RefIdx[p] = bestRef;
            FillMv(code, bx, by, w, h, bestMv);
            _syntax.SetBlocks(bx, by, w, h, bestRef, bestMv);
            total += best;
        }

        return total;
    }

    /// <summary>P_8x8: each 8x8 block searched in turn, and split into 8x4, 4x8 or 4x4 where that costs less.</summary>
    private long Search8x8(H264MbCode code, ReadOnlySpan<int> starts)
    {
        ClearInter(code, H264MbKind.Inter);
        code.Partition = 3;
        long total = (long)(5 * _sadLambda);
        int mask = 0;
        Span<int> parts = stackalloc int[4];
        Span<int> subMvs = stackalloc int[4];
        Span<int> bestSubMvs = stackalloc int[4];
        for (int i8 = 0; i8 < 4; i8++)
        {
            int bx = (i8 & 1) * 2, by = (i8 >> 1) * 2;
            long best = long.MaxValue;
            int bestRef = 0, bestMv = 0;
            for (int r = 0; r < _refCount; r++)
            {
                int mvp = _syntax.PredictMv(bx, by, 2, 2, r, mask);
                int mv = Search(r, bx, by, 2, 2, mvp, starts, out long cost);
                if (cost < best)
                {
                    (best, bestRef, bestMv) = (cost, r, mv);
                }
            }

            best += (long)_sadLambda;
            int bestType = 0;
            if (_sub8x8)
            {
                // The sub-partitions, each predicted from those before it, with the 8x8 block's reference.
                for (int type = 1; type < 4; type++)
                {
                    int n = H264MbSyntax.SubParts(type, bx, by, parts);
                    int subMask = mask;
                    long cost = (long)(3 * _sadLambda);
                    for (int i = 0; i < n; i++)
                    {
                        int px = parts[i] & 15, py = (parts[i] >> 4) & 15, pw = (parts[i] >> 8) & 15, ph = parts[i] >> 12;
                        int mvp = _syntax.PredictMv(px, py, pw, ph, bestRef, subMask);
                        int mv = Search(bestRef, px, py, pw, ph, mvp, [bestMv, mvp], out long c);
                        subMvs[i] = mv;
                        cost += c;
                        _syntax.SetBlocks(px, py, pw, ph, bestRef, mv);
                        subMask |= BlockMask(px, py, pw, ph);
                    }

                    if (cost < best)
                    {
                        (best, bestType) = (cost, type);
                        subMvs.CopyTo(bestSubMvs);
                    }
                }
            }

            code.SubTypes[i8] = bestType;
            code.RefIdx[i8] = bestRef;
            if (bestType == 0)
            {
                FillMv(code, bx, by, 2, 2, bestMv);
                _syntax.SetBlocks(bx, by, 2, 2, bestRef, bestMv);
            }
            else
            {
                int n = H264MbSyntax.SubParts(bestType, bx, by, parts);
                for (int i = 0; i < n; i++)
                {
                    int px = parts[i] & 15, py = (parts[i] >> 4) & 15, pw = (parts[i] >> 8) & 15, ph = parts[i] >> 12;
                    FillMv(code, px, py, pw, ph, bestSubMvs[i]);
                    _syntax.SetBlocks(px, py, pw, ph, bestRef, bestSubMvs[i]);
                }
            }

            mask |= 0x33 << (bx + (by * 4));
            total += best;
        }

        return total;
    }

    private static int BlockMask(int bx, int by, int w, int h)
    {
        int row = ((1 << w) - 1) << bx, mask = 0;
        for (int y = by; y < by + h; y++)
        {
            mask |= row << (y * 4);
        }

        return mask;
    }

    private static void FillMv(H264MbCode code, int bx, int by, int w, int h, int mv)
    {
        for (int y = by; y < by + h; y++)
        {
            code.Mv.AsSpan((y * 4) + bx, w).Fill(mv);
        }
    }

    // ---- Search ----

    private static int MvdBits(int d) => BitSinkCodes.UeBits(d > 0 ? (uint)((2 * d) - 1) : (uint)(-2 * d));

    private long MvCost(int mvx, int mvy, int mvp) =>
        (long)(_sadLambda * (MvdBits(mvx - H264SliceDecoder.MvX(mvp)) + MvdBits(mvy - H264SliceDecoder.MvY(mvp))));

    private long RefCost(int r) => _refCount <= 1 ? 0 : (long)(_sadLambda * (_refCount == 2 ? 1 : BitSinkCodes.UeBits((uint)r)));

    /// <summary>
    /// Searches the w by h block at 4x4 block (bx, by) of the macroblock in reference r: the best start in whole samples, a
    /// pattern search around it, then half and quarter samples; returns the vector and its cost.
    /// </summary>
    private int Search(int r, int bx, int by, int w, int h, int mvp, ReadOnlySpan<int> starts, out long cost)
    {
        H264RefPlanes rp = _refs[r];
        int px = (_mbX * 16) + (bx * 4), py = (_mbY * 16) + (by * 4), pw = w * 4, ph = h * 4;
        int width = _mb.WidthMbs * 16, height = _mb.HeightMbs * 16;
        int minX = Math.Max(-H264RefPlanes.Reach - px, -2048), maxX = Math.Min(width + H264RefPlanes.Reach - pw - px, 2047);
        int minY = Math.Max(-H264RefPlanes.Reach - py, -(_maxMvY >> 2)), maxY = Math.Min(height + H264RefPlanes.Reach - ph - py, (_maxMvY >> 2) - 1);
        int srcAt = _origin + (py * _stride) + px;
        long refCost = RefCost(r);

        long IntCost(int x, int y) => (rp.Weighted ? Satd(rp, px, py, pw, ph, H264SliceDecoder.PackMv(x * 4, y * 4), srcAt) : Sad(srcAt, rp.Picture.Y<byte>(), _origin + ((py + y) * _stride) + px + x, pw, ph))
            + MvCost(x * 4, y * 4, mvp);

        int ix = Math.Clamp((H264SliceDecoder.MvX(mvp) + 2) >> 2, minX, maxX), iy = Math.Clamp((H264SliceDecoder.MvY(mvp) + 2) >> 2, minY, maxY);
        long best = IntCost(ix, iy);
        foreach (int start in starts)
        {
            int sx = Math.Clamp((H264SliceDecoder.MvX(start) + 2) >> 2, minX, maxX), sy = Math.Clamp((H264SliceDecoder.MvY(start) + 2) >> 2, minY, maxY);
            if (sx == ix && sy == iy)
            {
                continue;
            }

            long c = IntCost(sx, sy);
            if (c < best)
            {
                (best, ix, iy) = (c, sx, sy);
            }
        }

        int range = _effort >= 6 ? 32 : 16;
        if (_effort >= 2)
        {
            for (int step = 0; step < range / 2; step++)
            {
                int cx = ix, cy = iy;
                foreach ((int dx, int dy) in Hexagon)
                {
                    int x = cx + dx, y = cy + dy;
                    if (x < minX || x > maxX || y < minY || y > maxY)
                    {
                        continue;
                    }

                    long c = IntCost(x, y);
                    if (c < best)
                    {
                        (best, ix, iy) = (c, x, y);
                    }
                }

                if (ix == cx && iy == cy)
                {
                    break;
                }
            }

            int qx = ix, qy = iy;
            foreach ((int dx, int dy) in Square)
            {
                int x = qx + dx, y = qy + dy;
                if (x >= minX && x <= maxX && y >= minY && y <= maxY)
                {
                    long c = IntCost(x, y);
                    if (c < best)
                    {
                        (best, ix, iy) = (c, x, y);
                    }
                }
            }
        }
        else
        {
            for (int step = 0; step < range; step++)
            {
                int cx = ix, cy = iy;
                foreach ((int dx, int dy) in Diamond)
                {
                    int x = cx + dx, y = cy + dy;
                    if (x >= minX && x <= maxX && y >= minY && y <= maxY)
                    {
                        long c = IntCost(x, y);
                        if (c < best)
                        {
                            (best, ix, iy) = (c, x, y);
                        }
                    }
                }

                if (ix == cx && iy == cy)
                {
                    break;
                }
            }
        }

        // Half, then quarter samples, by SATD.
        int bxq = ix * 4, byq = iy * 4;
        long bestSatd = Satd(rp, px, py, pw, ph, H264SliceDecoder.PackMv(bxq, byq), srcAt) + MvCost(bxq, byq, mvp);
        int rounds = _effort >= 5 ? 3 : 2;
        for (int round = 0; round < rounds; round++)
        {
            int d = round == 0 ? 2 : 1;
            int cx = bxq, cy = byq;
            foreach ((int dx, int dy) in Square)
            {
                int x = cx + (dx * d), y = cy + (dy * d);
                if (x < minX * 4 || x > maxX * 4 || y < minY * 4 || y > maxY * 4)
                {
                    continue;
                }

                long c = Satd(rp, px, py, pw, ph, H264SliceDecoder.PackMv(x, y), srcAt) + MvCost(x, y, mvp);
                if (c < bestSatd)
                {
                    (bestSatd, bxq, byq) = (c, x, y);
                }
            }
        }

        cost = bestSatd + refCost;
        return H264SliceDecoder.PackMv(bxq, byq);
    }

    private long Sad(int srcAt, byte[] plane, int at, int w, int h)
    {
        long sum = 0;
        byte[] src = _srcY;
        for (int y = 0; y < h; y++)
        {
            int s = srcAt + (y * _stride), p = at + (y * _stride);
            for (int x = 0; x < w; x++)
            {
                sum += Math.Abs(src[s + x] - plane[p + x]);
            }
        }

        return sum;
    }

    private long Satd(H264RefPlanes rp, int px, int py, int pw, int ph, int mv, int srcAt)
    {
        rp.Luma(px, py, pw, ph, mv, _mc, 0, 16, true);
        return Satd(_srcY, srcAt, _stride, _mc, 0, 16, pw, ph);
    }

    // ---- Coding ----

    /// <summary>
    /// Codes an inter candidate whose partitioning, references and vectors are set: its mvd against the predictors in coding
    /// order, its prediction, and its residual with the 4x4 transform or, where that costs less, the 8x8; returns its cost.
    /// </summary>
    private double CodeInter(H264MbCode code)
    {
        code.Kind = H264MbKind.Inter;
        code.Qp = _qp;
        Span<int> parts = stackalloc int[16];
        int n = H264MbSyntax.Parts(code, parts);
        int mask = 0;
        for (int i = 0; i < n; i++)
        {
            int bx = parts[i] & 15, by = (parts[i] >> 4) & 15, w = (parts[i] >> 8) & 15, h = parts[i] >> 12;
            int r = H264MbSyntax.BlockRef(code, bx, by);
            int mv = code.Mv[(by * 4) + bx];
            int mvp = _syntax.PredictMv(bx, by, w, h, r, mask);
            int mvd = H264SliceDecoder.PackMv(H264SliceDecoder.MvX(mv) - H264SliceDecoder.MvX(mvp), H264SliceDecoder.MvY(mv) - H264SliceDecoder.MvY(mvp));
            for (int y = by; y < by + h; y++)
            {
                code.Mvd.AsSpan((y * 4) + bx, w).Fill(mvd);
            }

            _syntax.SetBlocks(bx, by, w, h, r, mv);
            mask |= BlockMask(bx, by, w, h);
            H264RefPlanes rp = _refs[r];
            rp.Luma((_mbX * 16) + (bx * 4), (_mbY * 16) + (by * 4), w * 4, h * 4, mv, _ip, (by * 64) + (bx * 4), 16, true);
            rp.Chroma((_mbX * 8) + (bx * 2), (_mbY * 8) + (by * 2), w * 2, h * 2, mv, _ipCb, _ipCr, (by * 16) + (bx * 2), 8, true);
        }

        WritePrediction();
        int chroma = 0;
        for (int c = 0; c < 2; c++)
        {
            (c == 0 ? _ipCb : _ipCr).CopyTo(_pred, 0);
            (bool dc, bool ac) = CodeChromaPlane(code, c, c == 0 ? _srcCb : _srcCr, c == 0 ? _cb : _cr);
            chroma = Math.Max(chroma, ac ? 2 : dc ? 1 : 0);
        }

        code.Cbp = chroma << 4;
        long chromaSse = Sse(_srcCb, _cb, _chromaAt, _cstride, 8, 8) + Sse(_srcCr, _cr, _chromaAt, _cstride, 8, 8);
        long sse = CodeInterLuma(code, eight: false);
        double cost = sse + chromaSse + (_lambda * Rate(code) / CabacCost.Bit);
        if (_allow8x8 && _effort >= 2 && H264MbSyntax.NoSub8x8(code))
        {
            Save(_recon[0]);
            H264MbCode eight = _inter8;
            eight.CopyFrom(code);
            long sse8 = CodeInterLuma(eight, eight: true);
            double cost8 = sse8 + chromaSse + (_lambda * Rate(eight) / CabacCost.Bit);
            if (cost8 < cost)
            {
                code.CopyFrom(eight);
                return cost8;
            }

            Restore(_recon[0]);
        }

        return cost;
    }

    /// <summary>Codes the luma residual against the prediction in <see cref="_ip"/> and reconstructs it; returns its squared error.</summary>
    private long CodeInterLuma(H264MbCode code, bool eight)
    {
        // The rate count of a candidate without levels sets its QP to the predicted one; levels are quantized at this one.
        code.Qp = _qp;
        code.Transform8x8 = eight;
        Span<int> block = stackalloc int[64];
        Span<int> score = stackalloc int[4];
        if (eight)
        {
            for (int i8 = 0; i8 < 4; i8++)
            {
                int bx = (i8 & 1) * 2, by = (i8 >> 1) * 2;
                Residual(_srcY, _lumaAt + (by * 4 * _stride) + (bx * 4), _stride, _ip, (by * 64) + (bx * 4), 16, 8, block);
                H264Forward.Transform8x8(block);
                code.Luma8Count[i8] = QuantizeBlock(block, code.Luma8, i8 * 64, 0, 64, true, _qp, 5);
                score[i8] = Score(code.Luma8, i8 * 64, 64, 4);
            }
        }
        else
        {
            score.Clear();
            for (int blk = 0; blk < 16; blk++)
            {
                int xy = H264Tables.BlockXY[blk];
                int bx = xy & 3, by = xy >> 2;
                Residual(_srcY, _lumaAt + (by * 4 * _stride) + (bx * 4), _stride, _ip, (by * 64) + (bx * 4), 16, 4, block);
                H264Forward.Transform4x4(block);
                code.LumaCount[blk] = QuantizeBlock(block, code.Luma, blk * 16, 0, 16, false, _qp, 2);
                score[blk >> 2] = Math.Min(score[blk >> 2] + Score(code.Luma, blk * 16, 16, 1), 1000);
            }
        }

        // Sparse blocks of lone ±1 levels cost more bits than they win back: dropped by 8x8 block, and all of them when the
        // macroblock has little.
        int total = score[0] + score[1] + score[2] + score[3];
        int cbp = 0;
        for (int i8 = 0; i8 < 4; i8++)
        {
            bool drop = score[i8] < 4 || total < 6;
            if (eight)
            {
                if (drop)
                {
                    Array.Clear(code.Luma8, i8 * 64, 64);
                    code.Luma8Count[i8] = 0;
                }

                cbp |= code.Luma8Count[i8] > 0 ? 1 << i8 : 0;
                continue;
            }

            for (int blk = i8 * 4; blk < (i8 * 4) + 4; blk++)
            {
                if (drop)
                {
                    Array.Clear(code.Luma, blk * 16, 16);
                    code.LumaCount[blk] = 0;
                }

                cbp |= code.LumaCount[blk] > 0 ? 1 << i8 : 0;
            }
        }

        code.Cbp = (code.Cbp & 0x30) | cbp;

        // transform_size_8x8_flag is coded only with luma levels; without them the decoder takes the 4x4 transform.
        code.Transform8x8 = eight && cbp != 0;
        for (int y = 0; y < 16; y++)
        {
            _ip.AsSpan(y * 16, 16).CopyTo(_y.AsSpan(_lumaAt + (y * _stride), 16));
        }

        Span<int> d = _d.AsSpan(0, eight ? 64 : 16);
        if (eight)
        {
            for (int i8 = 0; i8 < 4; i8++)
            {
                if (code.Luma8Count[i8] > 0)
                {
                    int bx = (i8 & 1) * 2, by = (i8 >> 1) * 2;
                    Dequantize(code.Luma8, i8 * 64, 0, 64, true, _qp, d);
                    H264Transform.Add8x8(d, _y, _lumaAt + (by * 4 * _stride) + (bx * 4), _stride, 255);
                }
            }
        }
        else
        {
            for (int blk = 0; blk < 16; blk++)
            {
                if (code.LumaCount[blk] > 0)
                {
                    int xy = H264Tables.BlockXY[blk];
                    Dequantize(code.Luma, blk * 16, 0, 16, false, _qp, d);
                    H264Transform.Add4x4(d, _y, _lumaAt + ((xy >> 2) * 4 * _stride) + ((xy & 3) * 4), _stride, 255);
                }
            }
        }

        return Sse(_srcY, _y, _lumaAt, _stride, 16, 16);
    }

    /// <summary>
    /// How much a block's levels are worth keeping: a level above 1 counts as much as a whole block, a lone ±1 by the run of
    /// zeros before it (runs divided by <paramref name="runScale"/> for 8x8 blocks).
    /// </summary>
    private static int Score(int[] levels, int at, int count, int runScale)
    {
        int score = 0, run = 0;
        for (int k = 0; k < count; k++)
        {
            int level = levels[at + k];
            if (level == 0)
            {
                run++;
                continue;
            }

            if (level is > 1 or < -1)
            {
                return 1000;
            }

            score += RunCost[Math.Min(run / runScale, 15)];
            run = 0;
        }

        return score;
    }

    /// <summary>Writes the prediction in <see cref="_ip"/>, <see cref="_ipCb"/> and <see cref="_ipCr"/> to the picture.</summary>
    private void WritePrediction()
    {
        for (int y = 0; y < 16; y++)
        {
            _ip.AsSpan(y * 16, 16).CopyTo(_y.AsSpan(_lumaAt + (y * _stride), 16));
        }

        for (int y = 0; y < 8; y++)
        {
            _ipCb.AsSpan(y * 8, 8).CopyTo(_cb.AsSpan(_chromaAt + (y * _cstride), 8));
            _ipCr.AsSpan(y * 8, 8).CopyTo(_cr.AsSpan(_chromaAt + (y * _cstride), 8));
        }
    }

    private void SaveMb(int slot)
    {
        Save(_saveY[slot]);
        for (int y = 0; y < 8; y++)
        {
            _cb.AsSpan(_chromaAt + (y * _cstride), 8).CopyTo(_saveC[slot].AsSpan(y * 8, 8));
            _cr.AsSpan(_chromaAt + (y * _cstride), 8).CopyTo(_saveC[slot].AsSpan(64 + (y * 8), 8));
        }
    }

    private void RestoreMb(int slot)
    {
        Restore(_saveY[slot]);
        for (int y = 0; y < 8; y++)
        {
            _saveC[slot].AsSpan(y * 8, 8).CopyTo(_cb.AsSpan(_chromaAt + (y * _cstride), 8));
            _saveC[slot].AsSpan(64 + (y * 8), 8).CopyTo(_cr.AsSpan(_chromaAt + (y * _cstride), 8));
        }
    }
}
